using FluentAssertions;
using Moq;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>P6-A: snapshot tree, search, per-file version history and manifest-only snapshot diff.</summary>
public class SnapshotBrowserTests : IDisposable
{
    private const string Group = "/data/docs";

    private readonly string _root = Path.Combine(TestEnvironment.RootDirectory, "browser_" + Guid.NewGuid().ToString("N"));
    private readonly string _storageDir;
    private readonly TestLogger _logger = new();
    private readonly RepositoryCoordinator _coordinator;
    private readonly DateTime _baseTime = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private int _sequence;

    public SnapshotBrowserTests()
    {
        _storageDir = Path.Combine(_root, "storage");
        Directory.CreateDirectory(_storageDir);
        _coordinator = new RepositoryCoordinator(Path.Combine(_root, "repository.lock"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private async Task<RecordingStorage> OpenStorageAsync()
    {
        var local = new LocalStorage(_logger);
        await local.InitializeAsync(new Dictionary<string, string> { ["path"] = _storageDir });
        return new RecordingStorage(local);
    }

    private static SnapshotFileManifestEntry File(string path, string hash, long size = 10, int minute = 0) => new()
    {
        RelativePath = path,
        SizeBytes = size,
        ContentHash = hash,
        LastModifiedUtc = new DateTime(2025, 6, 1, 0, minute, 0, DateTimeKind.Utc)
    };

    /// <summary>Writes a manifest straight into storage; gzip v3 for version 3, plain JSON v2 otherwise.</summary>
    private string WriteSnapshot(
        int version,
        IEnumerable<SnapshotFileManifestEntry> files,
        IEnumerable<string>? emptyDirectories = null,
        string group = Group,
        bool encrypted = false)
    {
        _sequence++;
        var manifest = new SnapshotManifest
        {
            Version = version,
            SnapshotId = $"snapshot_{_sequence:D4}",
            Group = group,
            CreatedUtc = _baseTime.AddHours(_sequence),
            EncryptionEnabled = encrypted,
            Files = [.. files],
            Directories = version >= 3
                ? [.. (emptyDirectories ?? []).Select(d => new SnapshotDirectoryManifestEntry { RelativePath = d, LastWriteUtc = _baseTime })]
                : null
        };
        manifest.RootHash = SnapshotManifestHasher.ComputeRootHash(manifest);

        var remote = SnapshotStoragePaths.GetManifestPath(group, manifest.SnapshotId);
        var local = Path.Combine(_storageDir, remote.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        System.IO.File.WriteAllBytes(local, SnapshotManifestSerializer.Serialize(manifest));
        return remote;
    }

    private async Task<SnapshotBrowser> NewBrowserAsync() => new(_logger, await OpenStorageAsync(), _coordinator);

    // ---- tree --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task LoadTree_V3_BuildsTreeWithEmptyDirectoriesUnicodeNamesAndAggregates()
    {
        var path = WriteSnapshot(
            3,
            [
                File("readme.txt", "h1", 5),
                File("src/app/main.cs", "h2", 100),
                File("src/lib.cs", "h3", 20),
                File("Ünïcödé/日本語 ファイル.txt", "h4", 7),
            ],
            emptyDirectories: ["empty/nested", "src/app/also-empty"]);

        var tree = await (await NewBrowserAsync()).LoadTreeAsync(path);

        tree.ManifestVersion.Should().Be(3);
        tree.Group.Should().Be(Group);
        tree.GroupKey.Should().Be(SnapshotStoragePaths.BuildGroupStorageKey(Group));
        tree.FileCount.Should().Be(4);
        tree.TotalBytes.Should().Be(132);

        // Directories first, then files; ordered by name regardless of case.
        tree.Root.Children.Select(c => c.Name).Should().Equal("empty", "src", "Ünïcödé", "readme.txt");

        var empty = tree.Find("empty/nested")!;
        empty.IsDirectory.Should().BeTrue();
        empty.Children.Should().BeEmpty();
        empty.LastModifiedUtc.Should().Be(_baseTime);
        tree.Find("empty")!.FileCount.Should().Be(0);

        var src = tree.Find("src")!;
        src.SizeBytes.Should().Be(120);
        src.FileCount.Should().Be(2);
        src.Children.Select(c => c.Name).Should().Equal("app", "lib.cs");
        tree.Find("src/app")!.Children.Select(c => c.Name).Should().Equal("also-empty", "main.cs");

        var unicode = tree.Find("Ünïcödé/日本語 ファイル.txt")!;
        unicode.IsDirectory.Should().BeFalse();
        unicode.SizeBytes.Should().Be(7);
        unicode.ContentHash.Should().Be("h4");
        tree.Find("ünïcödé\\日本語 ファイル.txt").Should().BeSameAs(unicode, "lookup ignores case and slash direction");
        tree.Find("").Should().BeSameAs(tree.Root);
        tree.Find("nope").Should().BeNull();
    }

    [Fact]
    public async Task LoadTree_V2_ImpliesDirectoriesFromFilePaths_WithoutDirectoryTimes()
    {
        var path = WriteSnapshot(2, [File("a/b/c.txt", "h1", 3), File("top.txt", "h2", 4)]);

        var tree = await (await NewBrowserAsync()).LoadTreeAsync(path);

        tree.ManifestVersion.Should().Be(2);
        tree.Find("a/b")!.IsDirectory.Should().BeTrue();
        tree.Find("a/b")!.LastModifiedUtc.Should().BeNull();
        tree.Find("a")!.SizeBytes.Should().Be(3);
        tree.Find("a/b/c.txt")!.LastModifiedUtc.Should().NotBeNull();
        tree.TotalBytes.Should().Be(7);
    }

    [Fact]
    public async Task LoadTree_FromHead_ResolvesTheLatestSnapshot()
    {
        WriteSnapshot(3, [File("old.txt", "h1")]);
        var latest = WriteSnapshot(3, [File("new.txt", "h2")]);
        var headLocal = Path.Combine(_storageDir, SnapshotStoragePaths.GetHeadPath(Group).Replace('/', Path.DirectorySeparatorChar));
        await System.IO.File.WriteAllTextAsync(headLocal, latest + "\nroot");

        var tree = await (await NewBrowserAsync()).LoadTreeAsync(SnapshotStoragePaths.GetHeadPath(Group));

        tree.Root.Children.Select(c => c.Name).Should().Equal("new.txt");
        tree.ManifestPath.Should().Be(latest);
    }

    [Fact]
    public async Task LoadTree_RejectsAManifestThatFailsItsIntegrityCheck()
    {
        var path = WriteSnapshot(3, [File("a.txt", "h1")]);
        var local = Path.Combine(_storageDir, path.Replace('/', Path.DirectorySeparatorChar));
        var manifest = ManifestIo.Read(local);
        manifest.Files[0].SizeBytes = 999;
        ManifestIo.Write(local, manifest);

        var act = async () => await (await NewBrowserAsync()).LoadTreeAsync(path);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- search ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Search_MatchesNamesCaseInsensitively_AndPathsWhenTheQueryHasASlash()
    {
        var path = WriteSnapshot(
            3,
            [
                File("docs/Report.PDF", "h1"),
                File("docs/notes.txt", "h2"),
                File("misc/report-final.docx", "h3"),
                File("Ünï/ÄÖÜ-Straße.txt", "h4"),
            ],
            emptyDirectories: ["reports-archive"]);
        var tree = await (await NewBrowserAsync()).LoadTreeAsync(path);

        tree.Search("report").Select(n => n.RelativePath).Should().BeEquivalentTo("docs/Report.PDF", "misc/report-final.docx", "reports-archive");

        // "docs" matches the folder, not every file under it.
        tree.Search("docs").Select(n => n.RelativePath).Should().Equal("docs");

        tree.Search("docs/notes").Select(n => n.RelativePath).Should().Equal("docs/notes.txt");
        tree.Search("docs\\notes").Select(n => n.RelativePath).Should().Equal("docs/notes.txt");
        tree.Search("äöü-STRAßE").Select(n => n.RelativePath).Should().Equal("Ünï/ÄÖÜ-Straße.txt");
        tree.Search("   ").Should().BeEmpty();
        tree.Search("zzz").Should().BeEmpty();
        tree.Search("t", maxResults: 2).Should().HaveCount(2);
    }

    [Fact]
    public async Task EnumerateFilePaths_ReturnsEveryFileBelowADirectory_OrTheFileItself()
    {
        var path = WriteSnapshot(3, [File("a/1.txt", "h1"), File("a/b/2.txt", "h2"), File("c.txt", "h3")], emptyDirectories: ["a/empty"]);
        var tree = await (await NewBrowserAsync()).LoadTreeAsync(path);

        SnapshotTree.EnumerateFilePaths(tree.Find("a")!).Should().BeEquivalentTo("a/1.txt", "a/b/2.txt");
        SnapshotTree.EnumerateFilePaths(tree.Find("c.txt")!).Should().Equal("c.txt");
        SnapshotTree.EnumerateFilePaths(tree.Root).Should().HaveCount(3);
    }

    [Fact]
    public async Task CollectFilePaths_DeduplicatesFilesCoveredByASelectedDirectory()
    {
        var path = WriteSnapshot(3, [File("a/1.txt", "h1", 1), File("a/b/2.txt", "h2", 2), File("c.txt", "h3", 4), File("ab/3.txt", "h4", 8)]);
        var tree = await (await NewBrowserAsync()).LoadTreeAsync(path);

        var selection = new[] { tree.Find("a/b/2.txt")!, tree.Find("a")!, tree.Find("a/1.txt")!, tree.Find("ab")! };

        SnapshotTree.TopLevel(selection).Select(n => n.RelativePath).Should().BeEquivalentTo("a", "ab");
        SnapshotTree.CollectFilePaths(selection).Should().BeEquivalentTo("a/1.txt", "a/b/2.txt", "ab/3.txt");
        SnapshotTree.CollectFilePaths([tree.Find("c.txt")!, tree.Find("c.txt")!]).Should().Equal("c.txt");
        SnapshotTree.CollectFilePaths([]).Should().BeEmpty();
    }

    // ---- versions ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetFileVersions_CollapsesConsecutiveIdenticalHashes_NewestFirst()
    {
        // A, A, (absent), A, B, B, A: runs are A x3 (the gap does not split it), B x2, A x1.
        var s1 = WriteSnapshot(3, [File("doc.txt", "A", 10, 1), File("other.txt", "X")]);
        WriteSnapshot(3, [File("doc.txt", "A", 10, 1)]);
        WriteSnapshot(3, [File("other.txt", "X")]);
        WriteSnapshot(3, [File("doc.txt", "A", 10, 1)]);
        var s5 = WriteSnapshot(2, [File("doc.txt", "B", 25, 2)]);
        WriteSnapshot(3, [File("doc.txt", "B", 25, 2)]);
        var s7 = WriteSnapshot(3, [File("doc.txt", "A", 10, 3)]);

        var history = await (await NewBrowserAsync()).GetFileVersionsAsync(Group, "doc.txt");

        history.SnapshotsScanned.Should().Be(7);
        history.Warnings.Should().BeEmpty();
        history.Versions.Select(v => (v.ContentHash, v.SnapshotCount)).Should().Equal(("A", 1), ("B", 2), ("A", 3));
        history.Versions[0].ManifestPath.Should().Be(s7);
        history.Versions[1].ManifestPath.Should().Be(s5, "a run is reported at its oldest snapshot");
        history.Versions[1].SizeBytes.Should().Be(25);
        history.Versions[2].ManifestPath.Should().Be(s1);
        history.Versions[2].SnapshotId.Should().Be("snapshot_0001");
    }

    [Fact]
    public async Task GetFileVersions_AcceptsGroupKey_BackslashesAndCase_AndReturnsEmptyForUnknownFiles()
    {
        WriteSnapshot(3, [File("Sub/Doc.txt", "A")]);
        var browser = await NewBrowserAsync();
        var key = SnapshotStoragePaths.BuildGroupStorageKey(Group);

        (await browser.GetFileVersionsAsync(key, "sub\\doc.TXT")).Versions.Should().ContainSingle();
        (await browser.GetFileVersionsAsync(Group, "Sub/Doc.txt")).Versions.Should().ContainSingle();
        (await browser.GetFileVersionsAsync(Group, "missing.txt")).Versions.Should().BeEmpty();
        (await browser.GetFileVersionsAsync("/other/group", "Sub/Doc.txt")).Versions.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFileVersions_SkipsAnUnreadableManifest_WithAWarning()
    {
        WriteSnapshot(3, [File("doc.txt", "A")]);
        var bad = WriteSnapshot(3, [File("doc.txt", "B")]);
        System.IO.File.WriteAllText(Path.Combine(_storageDir, bad.Replace('/', Path.DirectorySeparatorChar)), "not a manifest");
        WriteSnapshot(3, [File("doc.txt", "C")]);

        var history = await (await NewBrowserAsync()).GetFileVersionsAsync(Group, "doc.txt");

        history.Versions.Select(v => v.ContentHash).Should().Equal("C", "A");
        history.Warnings.Should().ContainSingle().Which.Should().Contain(bad);
    }

    [Fact]
    public async Task GetFileVersions_DoesNotMixGroups()
    {
        WriteSnapshot(3, [File("doc.txt", "A")]);
        WriteSnapshot(3, [File("doc.txt", "Z")], group: "/data/other");

        var history = await (await NewBrowserAsync()).GetFileVersionsAsync(Group, "doc.txt");

        history.Versions.Should().ContainSingle().Which.ContentHash.Should().Be("A");
    }

    [Fact]
    public async Task GetFileVersions_RequiresAListingCapableStorage()
    {
        WriteSnapshot(3, [File("doc.txt", "A")]);
        var storage = await OpenStorageAsync();
        storage.ListingSupported = false;

        var act = async () => await new SnapshotBrowser(_logger, storage, _coordinator).GetFileVersionsAsync(Group, "doc.txt");

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    // ---- diff --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Diff_ClassifiesAddedRemovedChangedAndUnchanged_FromManifestsOnly()
    {
        var a = WriteSnapshot(2, [
            File("same.txt", "S", 5),
            File("edited.txt", "E1", 5),
            File("resized.txt", "R", 5),
            File("gone.txt", "G", 7),
            File("dir/gone2.txt", "G2", 8)]);
        var b = WriteSnapshot(3, [
            File("same.txt", "S", 5),
            File("edited.txt", "E2", 5),
            File("resized.txt", "R", 6),
            File("new.txt", "N", 9),
            File("dir/new2.txt", "N2", 11)]);
        var storage = await OpenStorageAsync();

        var diff = await new SnapshotBrowser(_logger, storage, _coordinator).DiffAsync(a, b);

        diff.Added.Select(e => e.RelativePath).Should().Equal("dir/new2.txt", "new.txt");
        diff.Added[1].SizeBytesA.Should().BeNull();
        diff.Added[1].SizeBytesB.Should().Be(9);
        diff.Removed.Select(e => e.RelativePath).Should().Equal("dir/gone2.txt", "gone.txt");
        diff.Removed[1].SizeBytesA.Should().Be(7);
        diff.Removed[1].SizeBytesB.Should().BeNull();
        diff.Changed.Select(e => e.RelativePath).Should().Equal("edited.txt", "resized.txt");
        diff.Changed[1].SizeBytesA.Should().Be(5);
        diff.Changed[1].SizeBytesB.Should().Be(6);
        diff.UnchangedCount.Should().Be(1);
        diff.HasDifferences.Should().BeTrue();
        diff.SnapshotIdA.Should().Be("snapshot_0001");
        diff.SnapshotIdB.Should().Be("snapshot_0002");

        storage.Downloads.Should().OnlyContain(p => p.EndsWith(".manifest.json", StringComparison.Ordinal), "a diff never fetches chunks");
        storage.Downloads.Should().HaveCount(2);
    }

    [Fact]
    public async Task Diff_OfASnapshotWithItself_HasNoDifferences()
    {
        var a = WriteSnapshot(3, [File("x.txt", "X"), File("y.txt", "Y")]);

        var diff = await (await NewBrowserAsync()).DiffAsync(a, a);

        diff.HasDifferences.Should().BeFalse();
        diff.UnchangedCount.Should().Be(2);
    }

    [Fact]
    public async Task Diff_TreatsACaseOnlyPathDifferenceAsChanged_NotRemovedPlusAdded()
    {
        var a = WriteSnapshot(3, [File("Docs/Report.TXT", "R", 5), File("keep.txt", "K"), File("Exact.txt", "E1", 1)]);
        var b = WriteSnapshot(3, [File("docs/report.txt", "R", 5), File("keep.txt", "K"), File("Exact.txt", "E2", 1)]);

        var diff = await (await NewBrowserAsync()).DiffAsync(a, b);

        diff.Added.Should().BeEmpty();
        diff.Removed.Should().BeEmpty();
        diff.UnchangedCount.Should().Be(1);
        diff.Changed.Select(e => e.RelativePath).Should().Equal("Docs/Report.TXT", "Exact.txt");
        var renamed = diff.Changed[0];
        renamed.CaseRename.Should().BeTrue();
        renamed.RelativePathB.Should().Be("docs/report.txt");
        diff.Changed[1].CaseRename.Should().BeFalse();
    }

    [Fact]
    public async Task LoadTree_KeepsFilesThatDifferOnlyInCase()
    {
        var path = WriteSnapshot(3, [File("Docs/a.txt", "h1", 1), File("docs/A.TXT", "h2", 2), File("docs/a.txt", "h3", 4)]);

        var tree = await (await NewBrowserAsync()).LoadTreeAsync(path);

        tree.FileCount.Should().Be(3);
        tree.TotalBytes.Should().Be(7);
        SnapshotTree.EnumerateFilePaths(tree.Root).Should().BeEquivalentTo("Docs/a.txt", "docs/A.TXT", "docs/a.txt");
        tree.Root.Children.Should().ContainSingle("case-variant folders share one node");
        tree.Root.Children[0].Children.Should().HaveCount(3);
    }

    [Fact]
    public void ResolveOriginalLocation_PrefersTheHistoryPathOverTheManifestPath()
    {
        var mapped = Path.GetFullPath(Path.Combine(_root, "new-pc-docs"));
        var foreign = Path.GetFullPath(Path.Combine(_root, "old-pc-docs"));

        SnapshotBrowser.ResolveOriginalLocation(mapped, foreign).Should().Be(mapped);
        SnapshotBrowser.ResolveOriginalLocation(null, foreign).Should().Be(foreign);
        SnapshotBrowser.ResolveOriginalLocation("relative/only", foreign).Should().Be(foreign);
        SnapshotBrowser.ResolveOriginalLocation("", "relative").Should().BeNull();
    }

    // ---- encrypted snapshots -----------------------------------------------------------------------------------

    [Fact]
    public async Task EncryptedSnapshot_BrowsesWithoutAPassword_AndRestoresSelectedFilesWithADecryptRequest()
    {
        var source = Path.Combine(_root, "src");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        await System.IO.File.WriteAllTextAsync(Path.Combine(source, "secret.txt"), "top secret " + new string('s', 2000));
        await System.IO.File.WriteAllTextAsync(Path.Combine(source, "sub", "other.txt"), "other " + new string('o', 2000));

        var config = new Mock<IConfigManager>();
        config.Setup(c => c.Retention).Returns(new RetentionConfig { Enabled = false, KeepLastPerDirectory = 10, MaxAgeDays = 30 });
        config.Setup(c => c.GlobalStorageType).Returns("local");
        config.Setup(c => c.SizeThresholdMB).Returns(100);
        config.Setup(c => c.Encryption).Returns(new EncryptionConfig
        {
            Enabled = true,
            Salt = Convert.ToBase64String(EncryptionService.GenerateSalt()),
            KeyDerivationIterations = 1000
        });
        config.Setup(c => c.ExcludedPatterns).Returns([]);
        config.Setup(c => c.ExcludedPaths).Returns([]);
        config.Setup(c => c.BackupType).Returns(BackupType.Incremental);
        config.Setup(c => c.WatchDirectories).Returns([]);
        config.Setup(c => c.MaxFileSizeMB).Returns(100);
        config.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig { ManifestVersion = 2 }); // legacy (v2) encrypted behaviour
        config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).Returns(async () => await OpenStorageAsync());

        var state = new SystemState(_logger);
        state.SetStateFilePath(Path.Combine(_root, "state.json"));
        await new Backup(_logger, state, new SizeAnalyzer(), config.Object, new StaticPasswordProvider("pw-123456!"), _coordinator)
            .BackupDirectoryAsync(source);
        var manifestPath = state.GetPreviousBackupPath(Path.GetFullPath(source))!;

        var browsePasswords = new RecordingPasswordProvider("pw-123456!");
        var browseStorage = await OpenStorageAsync();
        var tree = await new SnapshotBrowser(_logger, browseStorage, _coordinator).LoadTreeAsync(manifestPath);

        tree.Encrypted.Should().BeTrue();
        tree.Find("sub/other.txt").Should().NotBeNull();
        tree.Group.Should().Be(Path.GetFullPath(source));
        browsePasswords.Purposes.Should().BeEmpty("manifests are not encrypted, so browsing never asks for a password");
        browseStorage.Downloads.Should().OnlyContain(p => p.EndsWith(".manifest.json", StringComparison.Ordinal));

        var target = Path.Combine(_root, "restored");
        var restore = new Restore(_logger, await OpenStorageAsync(), browsePasswords, state, _coordinator);
        await restore.RestoreFromBackupAsync(
            manifestPath,
            target,
            new RestoreOptions { RelativePaths = new HashSet<string>(["secret.txt"], StringComparer.OrdinalIgnoreCase) });

        System.IO.File.ReadAllText(Path.Combine(target, "secret.txt")).Should().StartWith("top secret");
        System.IO.File.Exists(Path.Combine(target, "sub", "other.txt")).Should().BeFalse();
        browsePasswords.Purposes.Should().Contain(PasswordPurpose.Decrypt).And.NotContain(PasswordPurpose.Encrypt);
    }

    private sealed class RecordingPasswordProvider(string password) : IPurposePasswordProvider
    {
        public List<PasswordPurpose> Purposes { get; } = [];

        public Task<string?> GetPasswordAsync() => Task.FromResult<string?>(password);

        public Task<string?> GetPasswordAsync(PasswordPurpose purpose)
        {
            Purposes.Add(purpose);
            return Task.FromResult<string?>(password);
        }

        public bool IsPasswordSet() => true;

        public void ClearPassword() { }
    }

    private sealed class RecordingStorage(IStorage inner) : DelegatingStorage(inner)
    {
        public List<string> Downloads { get; } = [];
        public bool ListingSupported { get; set; } = true;

        public override bool SupportsListing => ListingSupported && Inner.SupportsListing;

        public override Task DownloadAsync(string remotePath, string localPath, CancellationToken ct = default)
        {
            Downloads.Add(remotePath);
            return Inner.DownloadAsync(remotePath, localPath, ct);
        }
    }
}
