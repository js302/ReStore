using FluentAssertions;
using Moq;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// <c>SystemState.SaveStateAsync</c> writes chunk-id sidecars, then a temp file, then swaps it over the live file keeping the old one
/// as <c>.bak</c>. A process or machine can die between any two of these steps. Each test rebuilds the on-disk picture of a crash at
/// one step from two REAL consecutive saves (A, then B) and asserts the next start loads a usable state without losing what A committed.
/// </summary>
public class SystemStateCrashConsistencyTests : IDisposable
{
    private readonly string _root = Path.Combine(TestEnvironment.RootDirectory, "statecrash_" + Guid.NewGuid().ToString("N"));
    private readonly TestLogger _logger = new();

    public SystemStateCrashConsistencyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed record Saved(string Dir, int SnapshotsA, int SnapshotsB, Dictionary<string, int> RefCountsA);

    private static string StateFileName => "system_state.json";

    /// <summary>Two real saves: A (one snapshot) and B (two snapshots). Returns the directory holding the final files (state, .bak, sidecars).</summary>
    private async Task<(string DirAfterA, string DirAfterB, int CountA, int CountB, Dictionary<string, int> RefsA)> TwoSavesAsync()
    {
        var work = Path.Combine(_root, "real_" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(work, "src");
        Directory.CreateDirectory(source);
        var statePath = Path.Combine(work, "state", StateFileName);
        var storage = new InMemoryStorage(_logger);
        var config = new Mock<IConfigManager>();
        config.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = false });
        config.SetupGet(c => c.GlobalStorageType).Returns("local");
        config.SetupGet(c => c.SizeThresholdMB).Returns(500);
        config.SetupGet(c => c.ExcludedPatterns).Returns([]);
        config.SetupGet(c => c.ExcludedPaths).Returns([]);
        config.SetupGet(c => c.BackupType).Returns(BackupType.ChunkSnapshot);
        config.SetupGet(c => c.WatchDirectories).Returns([]);
        config.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        config.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig { ManifestVersion = 2, MinChunkSizeKB = 2, TargetChunkSizeKB = 4, MaxChunkSizeKB = 8 });
        config.SetupGet(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(storage);
        var state = new SystemState(_logger);
        state.SetStateFilePath(statePath);
        var coordinator = new RepositoryCoordinator(Path.Combine(work, "repository.lock"));

        var rng = new Random(5);
        var bytes = new byte[30_000];
        rng.NextBytes(bytes);
        await File.WriteAllBytesAsync(Path.Combine(source, "a.bin"), bytes);
        await new Backup(_logger, state, new SizeAnalyzer(), config.Object, null, coordinator).BackupDirectoryAsync(source);
        // (Backup saves the state itself when it commits.)
        var dirA = Path.Combine(_root, "afterA_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.GetDirectoryName(statePath)!, dirA);
        var refsA = new Dictionary<string, int>(state.ChunkReferenceCounts);
        var countA = state.BackupHistory.Values.Sum(v => v.Count);

        rng.NextBytes(bytes);
        await File.WriteAllBytesAsync(Path.Combine(source, "b.bin"), bytes);
        await new Backup(_logger, state, new SizeAnalyzer(), config.Object, null, coordinator).BackupDirectoryAsync(source);
        var dirB = Path.Combine(_root, "afterB_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.GetDirectoryName(statePath)!, dirB);
        return (dirA, dirB, countA, state.BackupHistory.Values.Sum(v => v.Count), refsA);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        }
    }

    private static IEnumerable<string> Relative(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).Order(StringComparer.Ordinal);

    private async Task<SystemState> LoadFromAsync(string dir)
    {
        var state = new SystemState(_logger);
        state.SetStateFilePath(Path.Combine(dir, StateFileName));
        await state.LoadStateAsync();
        return state;
    }

    /// <summary>The loaded state is usable: it can be saved again and read back identically.</summary>
    private async Task AssertSavesAgainAsync(string dir, SystemState state)
    {
        await state.SaveStateAsync();
        var reloaded = await LoadFromAsync(dir);
        reloaded.BackupHistory.Values.Sum(v => v.Count).Should().Be(state.BackupHistory.Values.Sum(v => v.Count));
        reloaded.ChunkReferenceCounts.Should().BeEquivalentTo(state.ChunkReferenceCounts);
    }

    [Fact]
    public async Task TheRealSaveSequence_LeavesTheFilesTheCrashTestsAssumeAbout()
    {
        var (dirA, dirB, countA, countB, _) = await TwoSavesAsync();

        countA.Should().Be(1);
        countB.Should().Be(2);
        Relative(dirA).Should().Contain(StateFileName).And.NotContain(f => f.EndsWith(".tmp"), "a completed save leaves no temp file");
        Relative(dirB).Should().Contain(StateFileName).And.Contain(StateFileName + ".bak");
        File.ReadAllBytes(Path.Combine(dirB, StateFileName + ".bak")).Should().NotEqual(File.ReadAllBytes(Path.Combine(dirB, StateFileName)), "the backup copy holds the previous save");
        (await LoadFromAsync(dirB)).BackupHistory.Values.Sum(v => v.Count).Should().Be(2);
    }

    [Fact]
    public async Task CrashAfterWritingSidecars_BeforeTheTempFile_LoadsTheLastCommittedState()
    {
        var (dirA, dirB, countA, _, refsA) = await TwoSavesAsync();
        var crashed = Path.Combine(_root, "c1_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(dirA, crashed);
        // B's new sidecar files exist; the state file is still A.
        foreach (var file in Relative(dirB).Where(f => !f.StartsWith(StateFileName + ".bak", StringComparison.Ordinal) && f != StateFileName && !Relative(dirA).Contains(f)))
        {
            var target = Path.Combine(crashed, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(dirB, file), target, overwrite: true);
        }

        var state = await LoadFromAsync(crashed);

        state.BackupHistory.Values.Sum(v => v.Count).Should().Be(countA);
        state.ChunkReferenceCounts.Should().BeEquivalentTo(refsA);
        await AssertSavesAgainAsync(crashed, state);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(50)]
    [InlineData(99)]
    public async Task CrashWhileWritingTheTempFile_LoadsTheLastCommittedState(int percentWritten)
    {
        var (dirA, dirB, countA, _, refsA) = await TwoSavesAsync();
        var crashed = Path.Combine(_root, "c2_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(dirA, crashed);
        var full = await File.ReadAllBytesAsync(Path.Combine(dirB, StateFileName));
        // percentWritten == 100 is "temp fully written but not yet swapped in" (the next step's crash).
        await File.WriteAllBytesAsync(Path.Combine(crashed, $"{StateFileName}.{Guid.NewGuid():N}.tmp"), full.AsSpan(0, full.Length * percentWritten / 100).ToArray());

        var state = await LoadFromAsync(crashed);

        state.BackupHistory.Values.Sum(v => v.Count).Should().Be(countA, "the live file was never touched");
        state.ChunkReferenceCounts.Should().BeEquivalentTo(refsA);
        await AssertSavesAgainAsync(crashed, state);
    }

    [Fact]
    public async Task CrashInsideTheSwap_LiveFileGone_BackupCopyAndTempPresent_RecoversWithoutLosingCommittedState()
    {
        var (dirA, dirB, countA, countB, _) = await TwoSavesAsync();
        var crashed = Path.Combine(_root, "c3_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(dirA, crashed);
        // File.Replace on Unix renames the live file to .bak and then the temp file into place: dying in between leaves no live file.
        File.Move(Path.Combine(crashed, StateFileName), Path.Combine(crashed, StateFileName + ".bak"), overwrite: true);
        File.Copy(Path.Combine(dirB, StateFileName), Path.Combine(crashed, $"{StateFileName}.{Guid.NewGuid():N}.tmp"));
        foreach (var file in Relative(dirB).Where(f => f.Contains(".chunks", StringComparison.Ordinal) || (f != StateFileName && !f.StartsWith(StateFileName + ".bak", StringComparison.Ordinal))))
        {
            var target = Path.Combine(crashed, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(dirB, file), target, overwrite: true);
        }

        var state = await LoadFromAsync(crashed);

        state.BackupHistory.Values.Sum(v => v.Count).Should().BeGreaterThanOrEqualTo(countA, "the committed snapshot of the previous save must not disappear from the history");
        state.BackupHistory.Values.Sum(v => v.Count).Should().BeLessThanOrEqualTo(countB);
        await AssertSavesAgainAsync(crashed, state);
    }

    [Fact]
    public async Task CrashInsideTheSwap_WithATruncatedTemp_FallsBackToTheBackupCopy()
    {
        var (dirA, dirB, _, _, _) = await TwoSavesAsync();
        var crashed = Path.Combine(_root, "c3b_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(dirB, crashed);
        var full = await File.ReadAllBytesAsync(Path.Combine(crashed, StateFileName));
        File.Delete(Path.Combine(crashed, StateFileName));
        await File.WriteAllBytesAsync(Path.Combine(crashed, $"{StateFileName}.{Guid.NewGuid():N}.tmp"), full.AsSpan(0, full.Length / 3).ToArray());
        var expectedFromBak = Path.Combine(_root, "bakonly_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(dirB, expectedFromBak);
        File.Move(Path.Combine(expectedFromBak, StateFileName + ".bak"), Path.Combine(expectedFromBak, StateFileName), overwrite: true);
        var expectedCount = (await LoadFromAsync(expectedFromBak)).BackupHistory.Values.Sum(v => v.Count);

        var state = await LoadFromAsync(crashed);

        state.BackupHistory.Values.Sum(v => v.Count).Should().Be(expectedCount);
        dirA.Should().NotBeNull();
        await AssertSavesAgainAsync(crashed, state);
    }

    [Fact]
    public async Task LiveFileDeletedOnPurpose_WithoutALeftoverTemp_StartsEmpty_AndDoesNotResurrectTheBackupCopy()
    {
        var (_, dirB, _, _, _) = await TwoSavesAsync();
        File.Delete(Path.Combine(dirB, StateFileName));
        File.Exists(Path.Combine(dirB, StateFileName + ".bak")).Should().BeTrue();

        var state = await LoadFromAsync(dirB);

        state.BackupHistory.Should().BeEmpty("a missing state file without an interrupted save means 'reset'");
    }

    [Fact]
    public async Task CrashAfterTheSwap_LoadsTheNewState()
    {
        var (_, dirB, _, countB, _) = await TwoSavesAsync();
        var state = await LoadFromAsync(dirB);

        state.BackupHistory.Values.Sum(v => v.Count).Should().Be(countB);
        await AssertSavesAgainAsync(dirB, state);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("truncated")]
    [InlineData("garbage")]
    public async Task TornLiveFile_FallsBackToTheBackupCopy_AndKeepsTheBrokenOneForInspection(string damage)
    {
        var (_, dirB, _, _, _) = await TwoSavesAsync();
        var bak = await File.ReadAllBytesAsync(Path.Combine(dirB, StateFileName + ".bak"));
        var expectedFromBak = Path.Combine(_root, "bakonly_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(dirB, expectedFromBak);
        File.Move(Path.Combine(expectedFromBak, StateFileName + ".bak"), Path.Combine(expectedFromBak, StateFileName), overwrite: true);
        var expectedCount = (await LoadFromAsync(expectedFromBak)).BackupHistory.Values.Sum(v => v.Count);
        var live = Path.Combine(dirB, StateFileName);
        var original = await File.ReadAllBytesAsync(live);
        await File.WriteAllBytesAsync(live, damage switch
        {
            "empty" => [],
            "truncated" => original.AsSpan(0, original.Length / 2).ToArray(),
            _ => [0x00, 0xFF, 0x13, 0x37, 0x00, 0x00]
        });

        var state = await LoadFromAsync(dirB);

        state.BackupHistory.Values.Sum(v => v.Count).Should().Be(expectedCount, "the .bak copy holds the previous save");
        Relative(dirB).Should().Contain(f => f.StartsWith(StateFileName + ".corrupt-", StringComparison.Ordinal), "the damaged file is kept, not deleted");
        bak.Should().NotBeEmpty();
        await AssertSavesAgainAsync(dirB, state);
    }

    [Fact]
    public async Task LiveFileAndBackupCopyBothDamaged_StartsEmpty_WithoutThrowing_AndKeepsBothForInspection()
    {
        var (_, dirB, _, _, _) = await TwoSavesAsync();
        await File.WriteAllBytesAsync(Path.Combine(dirB, StateFileName), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(dirB, StateFileName + ".bak"), [4, 5, 6]);

        var state = await LoadFromAsync(dirB);

        state.BackupHistory.Should().BeEmpty();
        Relative(dirB).Should().Contain(f => f.StartsWith(StateFileName + ".corrupt-", StringComparison.Ordinal));
        File.Exists(Path.Combine(dirB, StateFileName + ".bak")).Should().BeTrue();
    }
}
