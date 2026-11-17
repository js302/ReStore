using System.IO.Compression;
using FluentAssertions;
using Moq;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.storage;
using ReStore.Core.src.updates;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// Hostile zip archives: the update installer's <c>ExtractSafely</c> and the system-backup restore path (<c>CompressionUtil.DecompressAsync</c>)
/// must refuse or safely contain every entry-name shape an attacker can craft. Whatever happens, nothing may appear outside the
/// destination. The corpus mixes Windows-only shapes (drive letters, UNC, ADS, reserved names), which are ordinary file names on Linux
/// (contained) and must be refused on Windows, with shapes that escape on every OS.
/// </summary>
// Reads the process-wide AppPaths root and scans the temp folder for leaked files: run with the other global-state tests, not in parallel.
[Collection("GlobalState")]
public class HostileArchiveTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "ReStoreHostileZip_" + Guid.NewGuid().ToString("N"));

    private string Destination => Path.Combine(_base, "dest");

    private string ZipPath => Path.Combine(_base, "zips", "hostile.zip");

    public HostileArchiveTests()
    {
        Directory.CreateDirectory(Path.Combine(_base, "zips"));
        Directory.CreateDirectory(Path.Combine(_base, "dest-evil"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, true); } catch { }
    }

    /// <summary>Entry names that resolve outside the destination on every OS.</summary>
    public static IEnumerable<object[]> EscapingNames() =>
    [
        ["/abs/evil.txt"],
        ["../evil.txt"],
        ["../../evil.txt"],
        ["a/../../evil.txt"],
        ["a/b/../../../evil.txt"],
        ["a/./../../evil.txt"],
        ["../dest-evil/evil.txt"],
        ["../dest-evil"],
        ["../dest-evil/"],
        [".."],
        ["../"],
        ["./../evil.txt"]
    ];

    /// <summary>Names that are only special on Windows: refused there, plain contained file names on Linux.</summary>
    public static IEnumerable<object[]> WindowsShapedNames() =>
    [
        ["C:\\evil.txt"],
        ["C:/evil.txt"],
        ["C:evil.txt"],
        ["\\\\server\\share\\evil.txt"],
        ["//server/share/evil.txt"],
        ["\\\\?\\C:\\evil.txt"],
        ["\\\\.\\C:\\evil.txt"],
        ["a.txt:evil"],
        ["a.txt::$DATA"],
        ["CON"],
        ["nul.txt"],
        ["a/COM1.log"],
        ["trailing."],
        ["trailing "],
        ["a\\..\\..\\evil.txt"],
        ["\\evil.txt"],
        ["a\\b\\evil.txt"],
        ["..\\..\\evil.txt"],
        ["..\\dest-evil\\evil.txt"]
    ];

    private void Build(params (string Name, string Content)[] entries)
    {
        using var stream = File.Create(ZipPath);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }

    /// <summary>Everything under the base folder except the destination tree and the zip itself.</summary>
    private List<string> OutsideDestination() =>
        [.. Directory.EnumerateFileSystemEntries(_base, "*", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(Destination + Path.DirectorySeparatorChar, StringComparison.Ordinal) && path != Destination)
            .Where(path => !path.StartsWith(Path.GetDirectoryName(ZipPath)!, StringComparison.Ordinal))
            .Where(path => !string.Equals(path, Path.Combine(_base, "dest-evil"), StringComparison.Ordinal))];

    private void NothingMayLeakOut()
    {
        OutsideDestination().Should().BeEmpty("no entry may be written outside the destination");
        Directory.EnumerateFileSystemEntries(Path.Combine(_base, "dest-evil")).Should().BeEmpty("a sibling folder sharing the destination's prefix stays untouched");
        File.Exists(Path.Combine(Path.GetTempPath(), "evil.txt")).Should().BeFalse();
        File.Exists(Path.Combine(Path.GetPathRoot(_base)!, "evil.txt")).Should().BeFalse();
    }

    // ---- UpdateInstaller.ExtractSafely ---------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(EscapingNames))]
    public void ExtractSafely_RefusesEscapingEntries(string name)
    {
        Build(("restore.exe", "x"), (name, "boom"));

        var act = () => UpdateInstaller.ExtractSafely(ZipPath, Destination);

        act.Should().Throw<UpdateInstallException>();
        NothingMayLeakOut();
    }

    [Theory]
    [MemberData(nameof(WindowsShapedNames))]
    public void ExtractSafely_ContainsOrRefusesWindowsShapedNames(string name)
    {
        Build(("restore.exe", "x"), (name, "boom"));

        try
        {
            UpdateInstaller.ExtractSafely(ZipPath, Destination);
        }
        catch (UpdateInstallException)
        {
            // Refused: fine on every OS.
        }

        NothingMayLeakOut();
        if (OperatingSystem.IsWindows() && !name.Contains("a.txt:") && !name.Contains("trailing") && name is not ("CON" or "nul.txt") && !name.StartsWith("a", StringComparison.Ordinal))
        {
            // Rooted and drive-relative names must never be extracted on Windows.
            File.Exists(Path.Combine(Destination, "evil.txt")).Should().BeFalse();
        }
    }

    [Fact]
    public void ExtractSafely_RefusesAtomically_NothingFromABadArchiveIsInstalledOutsideStaging()
    {
        Build(("restore.exe", "x"), ("sub/ok.txt", "ok"), ("../../evil.txt", "boom"));

        var act = () => UpdateInstaller.ExtractSafely(ZipPath, Destination);

        act.Should().Throw<UpdateInstallException>();
        NothingMayLeakOut();
    }

    [Fact]
    public void ExtractSafely_DuplicateEntries_LastOneWins_AndStaysInside()
    {
        Build(("restore.exe", "first"), ("RESTORE.EXE", "second"), ("restore.exe", "third"));

        UpdateInstaller.ExtractSafely(ZipPath, Destination);

        Directory.GetFiles(Destination).Should().NotBeEmpty();
        File.ReadAllText(Path.Combine(Destination, "restore.exe")).Should().Be("third");
        NothingMayLeakOut();
    }

    [Fact]
    public void ExtractSafely_DirectoryThenFileOfTheSameName_FailsCleanly()
    {
        Build(("restore.exe", "x"), ("thing/", ""), ("thing", "now a file"));

        var act = () => UpdateInstaller.ExtractSafely(ZipPath, Destination);

        act.Should().Throw<UpdateInstallException>("a directory/file clash is a refusal, not a raw IO exception");
        NothingMayLeakOut();
    }

    [Fact]
    public void ExtractSafely_VeryLongNames_FailCleanly()
    {
        Build(("restore.exe", "x"), (new string('a', 300) + ".txt", "boom"));

        var act = () => UpdateInstaller.ExtractSafely(ZipPath, Destination);

        act.Should().Throw<UpdateInstallException>("an unusable name is a refusal, not a raw IO exception");
        NothingMayLeakOut();
    }

    [Fact]
    public void ExtractSafely_ManyNestedDirectories_StayInside()
    {
        Build(("restore.exe", "x"), (string.Join('/', Enumerable.Repeat("d", 40)) + "/leaf.txt", "deep"));

        UpdateInstaller.ExtractSafely(ZipPath, Destination);

        Directory.EnumerateFiles(Destination, "leaf.txt", SearchOption.AllDirectories).Should().ContainSingle();
        NothingMayLeakOut();
    }

    [Fact]
    public void ExtractSafely_GarbageIsNotAZip()
    {
        File.WriteAllBytes(ZipPath, [.. Enumerable.Range(0, 2048).Select(i => (byte)(i * 31))]);

        var act = () => UpdateInstaller.ExtractSafely(ZipPath, Destination);

        act.Should().Throw<UpdateInstallException>().WithMessage("*not a valid zip*");
        NothingMayLeakOut();
    }

    // ---- system-backup restore: the extraction primitive and the real restore path --------------------------------

    [Theory]
    [MemberData(nameof(EscapingNames))]
    public async Task SystemBackupExtraction_RefusesEscapingEntries(string name)
    {
        Build(("programs.json", "{}"), (name, "boom"));

        var act = () => CompressionUtil.DecompressAsync(ZipPath, Destination);

        await act.Should().ThrowAsync<Exception>();
        NothingMayLeakOut();
    }

    [Theory]
    [MemberData(nameof(WindowsShapedNames))]
    public async Task SystemBackupExtraction_ContainsOrRefusesWindowsShapedNames(string name)
    {
        Build(("programs.json", "{}"), (name, "boom"));

        try
        {
            await CompressionUtil.DecompressAsync(ZipPath, Destination);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // Refused or unusable on this OS: fine.
        }

        NothingMayLeakOut();
    }

    [Theory]
    [InlineData("../../evil.txt")]
    [InlineData("/abs/evil.txt")]
    [InlineData("../dest-evil/evil.txt")]
    public async Task SystemRestore_OfAHostileBackup_WritesNothingOutsideItsWorkFolder(string name)
    {
        Build(("programs.json", "{}"), (name, "boom"));
        var storage = new Mock<IStorage>();
        storage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>((_, local, _) =>
            {
                File.Copy(ZipPath, local, overwrite: true);
                return Task.CompletedTask;
            });
        var config = new Mock<IConfigManager>();
        config.SetupGet(c => c.GlobalStorageType).Returns("local");
        config.SetupGet(c => c.SystemBackup).Returns(new SystemBackupConfig());
        var manager = new SystemBackupManager(new TestLogger(), config.Object, new SystemState(new TestLogger()), null, new RepositoryCoordinator(Path.Combine(_base, "zips", "repo.lock")));

        var act = () => manager.RestoreSystemAsync(storage.Object, "programs", "system_backups/programs/hostile.zip", new SystemRestoreOptions { PlanOnly = true });

        await act.Should().ThrowAsync<IOException>();
        NothingMayLeakOut();
        Directory.EnumerateFiles(AppPaths.Root, "evil.txt", SearchOption.AllDirectories).Should().BeEmpty();
        Directory.EnumerateFiles(Path.GetTempPath(), "evil.txt", SearchOption.TopDirectoryOnly).Should().BeEmpty();
    }
}
