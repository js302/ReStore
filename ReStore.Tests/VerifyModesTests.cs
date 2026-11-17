using FluentAssertions;
using Moq;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.core;
using ReStore.Core.src.storage;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>Verification modes (quick / sample / full) and the test restore, against a real local repository.</summary>
public class VerifyModesTests : IDisposable
{
    private const int FileCount = 40;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreVerifyModes_" + Guid.NewGuid().ToString("N"));
    private readonly ILogger _logger = new Mock<ILogger>().Object;
    private readonly RepositoryCoordinator _coordinator;

    public VerifyModesTests()
    {
        Directory.CreateDirectory(_root);
        _coordinator = new RepositoryCoordinator(Path.Combine(_root, "repo.lock"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed record Repo(LocalStorage Storage, string StorageRoot, string ManifestPath, SnapshotManifest Manifest)
    {
        public string ChunkFile(string chunkId) => Path.Combine(
            StorageRoot,
            SnapshotStoragePaths.GetChunkPath(chunkId, Manifest.ChunkStorageNamespace).Replace('/', Path.DirectorySeparatorChar));
    }

    private async Task<Repo> CreateRepoAsync(bool encrypted = false)
    {
        var source = Path.Combine(_root, "src_" + Guid.NewGuid().ToString("N"));
        var storageRoot = Path.Combine(_root, "storage_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(storageRoot);

        // Distinct, incompressible contents: every file is its own chunk.
        for (var i = 0; i < FileCount; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(source, $"file{i:D2}.bin"), System.Security.Cryptography.RandomNumberGenerator.GetBytes(2000 + i));
        }

        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = storageRoot });

        var config = new Mock<IConfigManager>();
        config.SetupGet(c => c.Retention).Returns(new RetentionConfig());
        config.SetupGet(c => c.GlobalStorageType).Returns("local");
        config.SetupGet(c => c.SizeThresholdMB).Returns(100);
        config.SetupGet(c => c.ExcludedPatterns).Returns([]);
        config.SetupGet(c => c.ExcludedPaths).Returns([]);
        config.SetupGet(c => c.BackupType).Returns(BackupType.ChunkSnapshot);
        config.SetupGet(c => c.WatchDirectories).Returns([]);
        config.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        config.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig { ManifestVersion = 3 });
        config.SetupGet(c => c.Encryption).Returns(new EncryptionConfig
        {
            Enabled = encrypted,
            Salt = encrypted ? Convert.ToBase64String(EncryptionService.GenerateSalt()) : null,
            KeyDerivationIterations = 1000
        });
        config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(storage);

        var state = new SystemState(_logger);
        state.SetStateFilePath(Path.Combine(_root, "state_" + Guid.NewGuid().ToString("N") + ".json"));
        IPasswordProvider? provider = encrypted ? new StaticPasswordProvider("StrongPassword123!") : null;

        await new Backup(_logger, state, new SizeAnalyzer(), config.Object, provider, _coordinator).BackupDirectoryAsync(source);
        var manifestPath = state.GetPreviousBackupPath(source)!;
        var manifestFile = Path.Combine(storageRoot, manifestPath.Replace('/', Path.DirectorySeparatorChar));
        using var reader = new RepositoryManifestReader(_logger, storage, provider, zeroKeysOnDispose: false);
        var manifest = await reader.ReadFileAsync(manifestFile, manifestPath, default);
        return new Repo(storage, storageRoot, manifestPath, manifest);
    }

    private SnapshotIntegrityVerifier Verifier(IStorage storage, SystemState? state = null)
        => new(_logger, storage, systemState: state, coordinator: _coordinator);

    [Fact]
    public async Task Quick_PassesWithoutDownloadingAnyChunk()
    {
        var repo = await CreateRepoAsync();
        var storage = new CountingStorage(repo.Storage);

        var result = await Verifier(storage).VerifyAsync(repo.ManifestPath, null, default, new VerificationOptions(VerificationMode.Quick));

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        result.Mode.Should().Be(VerificationMode.Quick);
        result.UniqueChunks.Should().Be(FileCount);
        result.ChunksDownloaded.Should().Be(0);
        storage.Downloads.Should().OnlyContain(path => !path.StartsWith("chunks/"), "quick mode must never download a chunk");
    }

    [Fact]
    public async Task Quick_DetectsAMissingChunk()
    {
        var repo = await CreateRepoAsync();
        var chunk = repo.Manifest.Files[3].Chunks[0].ChunkId;
        File.Delete(repo.ChunkFile(chunk));
        var storage = new CountingStorage(repo.Storage);

        var result = await Verifier(storage).VerifyAsync(repo.ManifestPath, null, default, new VerificationOptions(VerificationMode.Quick));

        result.IsValid.Should().BeFalse();
        result.MissingChunks.Should().Be(1);
        result.InvalidFiles.Should().Be(1);
        result.Errors.Should().Contain(error => error.Contains("Missing chunk object"));
        storage.Downloads.Should().OnlyContain(path => !path.StartsWith("chunks/"));
    }

    [Fact]
    public async Task Quick_DetectsAStoredSizeMismatch()
    {
        var repo = await CreateRepoAsync();
        var chunk = repo.Manifest.Files[5].Chunks[0].ChunkId;
        await File.AppendAllTextAsync(repo.ChunkFile(chunk), "trailing bytes");
        var storage = new CountingStorage(repo.Storage);

        var result = await Verifier(storage).VerifyAsync(repo.ManifestPath, null, default, new VerificationOptions(VerificationMode.Quick));

        result.IsValid.Should().BeFalse();
        result.InvalidChunks.Should().Be(1);
        result.Errors.Should().Contain(error => error.Contains("size mismatch") && error.Contains(chunk));
        storage.Downloads.Should().OnlyContain(path => !path.StartsWith("chunks/"));
    }

    [Fact]
    public async Task Quick_DoesNotNeedAPassword_ForAnEncryptedV3Repository()
    {
        var repo = await CreateRepoAsync(encrypted: true);
        repo.Manifest.EncryptionEnabled.Should().BeTrue();

        // The provider is only needed to open the manifest's key file; quick mode never decrypts a chunk.
        var verifier = new SnapshotIntegrityVerifier(_logger, repo.Storage, new StaticPasswordProvider("StrongPassword123!"), coordinator: _coordinator);
        var result = await verifier.VerifyAsync(repo.ManifestPath, null, default, new VerificationOptions(VerificationMode.Quick));

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        result.ChunksDownloaded.Should().Be(0);
    }

    [Fact]
    public async Task Sample_VerifiesTheRequestedPercentageOfChunks_AndReportsTheSeed()
    {
        var repo = await CreateRepoAsync();
        var storage = new CountingStorage(repo.Storage);

        var result = await Verifier(storage).VerifyAsync(repo.ManifestPath, null, default, new VerificationOptions(VerificationMode.Sample, 25, 7));

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        result.SampleSeed.Should().Be(7);
        result.ChunksSampled.Should().Be(10, "25% of 40 chunks");
        result.ChunksDownloaded.Should().Be(10);
        storage.Downloads.Count(path => path.StartsWith("chunks/")).Should().Be(10);
    }

    [Fact]
    public async Task Sample_RoundsUp_AndChecksAtLeastOneChunk()
    {
        var repo = await CreateRepoAsync();

        var result = await Verifier(repo.Storage).VerifyAsync(repo.ManifestPath, null, default, new VerificationOptions(VerificationMode.Sample, 1, 1));

        result.ChunksSampled.Should().Be(1, "ceil(1% of 40)");
    }

    [Fact]
    public async Task Sample_SelectsTheSameChunksForTheSameSeed_AndOthersForADifferentSeed()
    {
        var repo = await CreateRepoAsync();

        async Task<HashSet<string>> SampledAsync(int seed)
        {
            var storage = new CountingStorage(repo.Storage);
            var result = await Verifier(storage).VerifyAsync(repo.ManifestPath, null, default, new VerificationOptions(VerificationMode.Sample, 25, seed));
            result.SampleSeed.Should().Be(seed);
            return [.. storage.Downloads.Where(path => path.StartsWith("chunks/"))];
        }

        var first = await SampledAsync(42);
        var again = await SampledAsync(42);
        var other = await SampledAsync(43);

        again.Should().BeEquivalentTo(first);
        other.Should().NotBeEquivalentTo(first);
    }

    [Fact]
    public async Task Sample_PicksAndReportsASeed_WhenNoneIsGiven()
    {
        var repo = await CreateRepoAsync();

        var result = await Verifier(repo.Storage).VerifyAsync(repo.ManifestPath, null, default, new VerificationOptions(VerificationMode.Sample, 10));

        result.SampleSeed.Should().NotBeNull();
    }

    [Fact]
    public async Task Sample_AtOneHundredPercent_DetectsACorruptedChunkThatQuickCannotSee()
    {
        var repo = await CreateRepoAsync();
        var chunk = repo.Manifest.Files[9].Chunks[0].ChunkId;
        var path = repo.ChunkFile(chunk);
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[bytes.Length / 2] ^= 0xFF; // same size, different content
        await File.WriteAllBytesAsync(path, bytes);

        var quick = await Verifier(repo.Storage).VerifyAsync(repo.ManifestPath, null, default, new VerificationOptions(VerificationMode.Quick));
        var sample = await Verifier(repo.Storage).VerifyAsync(repo.ManifestPath, null, default, new VerificationOptions(VerificationMode.Sample, 100, 1));

        quick.IsValid.Should().BeTrue("a bit flip that keeps the size is invisible to quick mode");
        sample.IsValid.Should().BeFalse();
        sample.InvalidChunks.Should().Be(1);
        sample.Errors.Should().Contain(error => error.Contains(chunk));
    }

    [Fact]
    public async Task Sample_ReportsAMissingChunk_EvenWhenItIsNotInTheSample()
    {
        var repo = await CreateRepoAsync();
        File.Delete(repo.ChunkFile(repo.Manifest.Files[0].Chunks[0].ChunkId));

        var result = await Verifier(repo.Storage).VerifyAsync(repo.ManifestPath, null, default, new VerificationOptions(VerificationMode.Sample, 1, 1));

        result.IsValid.Should().BeFalse();
        result.MissingChunks.Should().Be(1);
    }

    [Fact]
    public async Task Full_IsTheDefault_AndDownloadsEveryChunk()
    {
        var repo = await CreateRepoAsync();
        var storage = new CountingStorage(repo.Storage);

        var result = await Verifier(storage).VerifyAsync(repo.ManifestPath);

        result.Mode.Should().Be(VerificationMode.Full);
        result.IsValid.Should().BeTrue();
        result.ChunksDownloaded.Should().Be(FileCount);
        result.SampleSeed.Should().BeNull();
    }

    [Fact]
    public async Task Verify_RejectsAnOutOfRangeSamplePercent()
    {
        var repo = await CreateRepoAsync();

        var act = () => Verifier(repo.Storage).VerifyAsync(repo.ManifestPath, null, default, new VerificationOptions(VerificationMode.Sample, 0));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("full", true, VerificationMode.Full)]
    [InlineData("QUICK", true, VerificationMode.Quick)]
    [InlineData("sample", true, VerificationMode.Sample)]
    [InlineData(null, true, VerificationMode.Full)]
    [InlineData("", true, VerificationMode.Full)]
    [InlineData("partial", false, VerificationMode.Full)]
    public void VerificationModes_Parse(string? text, bool valid, VerificationMode expected)
    {
        VerificationModes.TryParse(text, out var mode).Should().Be(valid);
        mode.Should().Be(expected);
    }

    [Fact]
    public void ConfigValidator_RejectsAnUnknownVerificationModeAndPercent()
    {
        var result = new ConfigValidationResult();
        var method = typeof(ConfigValidator).GetMethod("ValidateVerificationSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        method.Invoke(null, [new VerificationConfig { Enabled = true, Mode = "partial", SamplePercent = 0 }, result]);

        result.Errors.Should().Contain(error => error.Contains("verification.mode"));
        result.Errors.Should().Contain(error => error.Contains("verification.samplePercent"));
    }

    // ---- test restore ----

    [Fact]
    public async Task TestRestore_RestoresASampleAndLeavesNoTemporaryFiles()
    {
        var repo = await CreateRepoAsync();
        var tempRoot = Path.Combine(_root, "temp");
        Directory.CreateDirectory(tempRoot);

        var result = await new SnapshotTestRestore(_logger, repo.Storage, null, null, _coordinator, tempRoot)
            .RunAsync(repo.ManifestPath, 6, 11);

        result.Passed.Should().BeTrue(string.Join("; ", result.Files.Select(f => f.Error)));
        result.FilesTested.Should().Be(6);
        result.FilesPassed.Should().Be(6);
        result.Seed.Should().Be(11);
        result.BytesRestored.Should().BeGreaterThan(0);
        Directory.GetFileSystemEntries(tempRoot).Should().BeEmpty("the temporary restore folder must be deleted");
    }

    [Fact]
    public async Task TestRestore_PicksTheSameFilesForTheSameSeed_AndClampsToTheSnapshotSize()
    {
        var repo = await CreateRepoAsync();
        var tester = new SnapshotTestRestore(_logger, repo.Storage, null, null, _coordinator, _root);

        var first = await tester.RunAsync(repo.ManifestPath, 5, 3);
        var again = await tester.RunAsync(repo.ManifestPath, 5, 3);
        var other = await tester.RunAsync(repo.ManifestPath, 5, 4);
        var all = await tester.RunAsync(repo.ManifestPath, 1000, 3);

        again.Files.Select(f => f.RelativePath).Should().Equal(first.Files.Select(f => f.RelativePath));
        other.Files.Select(f => f.RelativePath).Should().NotEqual(first.Files.Select(f => f.RelativePath));
        all.FilesTested.Should().Be(FileCount);
        all.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task TestRestore_DetectsACorruptedChunk()
    {
        var repo = await CreateRepoAsync();
        var victim = repo.Manifest.Files[7];
        var path = repo.ChunkFile(victim.Chunks[0].ChunkId);
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[10] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);

        var result = await new SnapshotTestRestore(_logger, repo.Storage, null, null, _coordinator, _root)
            .RunAsync(repo.ManifestPath, 1000, 1);

        result.Passed.Should().BeFalse();
        result.FilesFailed.Should().Be(1);
        result.Files.Single(f => !f.Passed).RelativePath.Should().Be(victim.RelativePath);
        result.FilesPassed.Should().Be(FileCount - 1, "the other files are still tested and pass");
    }

    [Fact]
    public async Task TestRestore_WorksFromAHeadReference_ForAnEncryptedRepository()
    {
        var repo = await CreateRepoAsync(encrypted: true);
        var head = SnapshotStoragePaths.GetHeadPath(repo.Manifest.Group);

        var result = await new SnapshotTestRestore(_logger, repo.Storage, new StaticPasswordProvider("StrongPassword123!"), null, _coordinator, _root)
            .RunAsync(head, 3, 1);

        result.Passed.Should().BeTrue(string.Join("; ", result.Files.Select(f => f.Error)) + result.Error);
        result.SnapshotId.Should().Be(repo.Manifest.SnapshotId);
    }

    [Fact]
    public async Task TestRestore_RecordsOneVerifyActivity_AndNoRestoreActivityOrTelemetry()
    {
        var repo = await CreateRepoAsync();
        var state = new SystemState(_logger);

        await new SnapshotTestRestore(_logger, repo.Storage, null, state, _coordinator, _root).RunAsync(repo.ManifestPath, 2, 1);

        var activity = state.GetRecentActivity();
        activity.Should().ContainSingle();
        activity[0].Type.Should().Be(ActivityType.Verify);
        activity[0].Target.Should().StartWith("Test restore");
        activity[0].Result.Should().Be(ActivityResult.Succeeded);
        state.Telemetry.Restore.AttemptCount.Should().Be(0);
    }

    [Fact]
    public async Task TestRestore_FailsCleanly_ForAnUnknownSnapshot()
    {
        var repo = await CreateRepoAsync();

        var result = await new SnapshotTestRestore(_logger, repo.Storage, null, null, _coordinator, _root)
            .RunAsync("snapshots/nothing/missing.manifest.json", 3, 1);

        result.Passed.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    private sealed class CountingStorage(IStorage inner) : DelegatingStorage(inner)
    {
        public List<string> Downloads { get; } = [];

        public override Task DownloadAsync(string remotePath, string localPath, CancellationToken ct = default)
        {
            lock (Downloads)
            {
                Downloads.Add(remotePath);
            }

            return base.DownloadAsync(remotePath, localPath, ct);
        }
    }
}
