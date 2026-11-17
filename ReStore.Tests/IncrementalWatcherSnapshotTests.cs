using FluentAssertions;
using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>P-9: watcher flushes extend the previous manifest instead of walking the tree, with strict fallbacks.</summary>
public class IncrementalWatcherSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(TestEnvironment.RootDirectory, "incwatch_" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _storageDir;
    private readonly TestLogger _logger = new();
    private readonly SystemState _state;
    private readonly Mock<IConfigManager> _config = new();
    private ChunkDiffingConfig _chunkDiffing = new();

    public IncrementalWatcherSnapshotTests()
    {
        _source = Path.GetFullPath(Path.Combine(_root, "src"));
        _storageDir = Path.Combine(_root, "storage");
        Directory.CreateDirectory(_source);

        _state = new SystemState(_logger);
        _state.SetStateFilePath(Path.Combine(_root, "state.json"));

        _config.Setup(c => c.Retention).Returns(new RetentionConfig { Enabled = false });
        _config.Setup(c => c.GlobalStorageType).Returns("local");
        _config.Setup(c => c.SizeThresholdMB).Returns(100);
        _config.Setup(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        _config.Setup(c => c.ExcludedPatterns).Returns(["*.tmp"]);
        _config.Setup(c => c.ExcludedPaths).Returns([]);
        _config.Setup(c => c.BackupType).Returns(BackupType.Incremental);
        _config.Setup(c => c.WatchDirectories).Returns([]);
        _config.Setup(c => c.MaxFileSizeMB).Returns(100);
        _config.SetupGet(c => c.ChunkDiffing).Returns(() => _chunkDiffing);
        _config.SetupGet(c => c.BackupSettings).Returns(new BackupSettingsConfig());
        _config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).Returns(async (string _) =>
        {
            var storage = new LocalStorage(_logger);
            await storage.InitializeAsync(new Dictionary<string, string> { { "path", _storageDir } });
            return (ReStore.Core.src.storage.IStorage)storage;
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private Backup NewBackup() => new(_logger, _state, new SizeAnalyzer(), _config.Object, null, new RepositoryCoordinator(Path.Combine(_root, "repository.lock")));

    private string Write(string rel, string content, string? root = null)
    {
        var path = Path.Combine(root ?? _source, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private void MakeTree(int files)
    {
        for (var i = 0; i < files; i++)
        {
            Write($"d{i % 10}/f{i}.txt", "content " + i);
        }
    }

    private SnapshotManifest Latest(string group) =>
        Directory.GetFiles(_storageDir, "*.manifest.json", SearchOption.AllDirectories)
            .Select(ManifestIo.Read)
            .Where(m => string.Equals(m.Group, group, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.CreatedUtc).Last();

    private static List<string> Describe(SnapshotManifest m) =>
        [.. m.Files.Select(f => $"{f.RelativePath}|{f.SizeBytes}|{f.ContentHash}|{string.Join(",", f.Chunks.Select(c => c.ChunkId))}")
            .Concat((m.Directories ?? []).Select(d => "dir:" + d.RelativePath))];

    /// <summary>The manifest a full pass makes for a copy of the current tree.</summary>
    private async Task<List<string>> FullPassDescriptionAsync()
    {
        var copy = Path.Combine(_root, "copy");
        if (Directory.Exists(copy)) Directory.Delete(copy, true);
        CopyDirectory(_source, copy);
        await NewBackup().BackupDirectoryAsync(copy);
        return Describe(Latest(Path.GetFullPath(copy)));
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    [Fact]
    public async Task Flush_OneChangedFileInLargeTree_ReadsOnlyThatFileAndDoesNotEnumerate()
    {
        MakeTree(1000);
        Directory.CreateDirectory(Path.Combine(_source, "empty"));
        await NewBackup().BackupDirectoryAsync(_source);

        var changed = Write("d3/f3.txt", "changed content");
        var opens = new List<string>();
        var backup = NewBackup();
        backup.SourceFileOpener = p => { opens.Add(p); return File.OpenRead(p); };

        await backup.BackupFilesAsync([changed], _source);

        opens.Should().Equal(changed);
        backup.TreeScanCount.Should().Be(0);
        var manifest = Latest(_source);
        manifest.Files.Should().HaveCount(1000);
        manifest.Directories!.Select(d => d.RelativePath).Should().Equal("empty");
        Describe(manifest).Should().Equal(await FullPassDescriptionAsync());
    }

    [Fact]
    public async Task Flush_CreatedDeletedAndRenamedPaths_MatchesFullPass()
    {
        MakeTree(50);
        await NewBackup().BackupDirectoryAsync(_source);

        var created = Write("new/created.txt", "new file");
        var deleted = Path.Combine(_source, "d1", "f1.txt");
        File.Delete(deleted);
        var oldName = Path.Combine(_source, "d2", "f2.txt");
        var newName = Path.Combine(_source, "d2", "renamed.txt");
        File.Move(oldName, newName);
        Write("skipped.tmp", "excluded by pattern");
        var backup = NewBackup();

        await backup.BackupFilesAsync([created, deleted, newName, Path.Combine(_source, "skipped.tmp")], _source, previousPaths: [oldName]);

        backup.TreeScanCount.Should().Be(0);
        var paths = Latest(_source).Files.Select(f => f.RelativePath).ToList();
        paths.Should().Contain(["new/created.txt", "d2/renamed.txt"]);
        paths.Should().NotContain(["d1/f1.txt", "d2/f2.txt", "skipped.tmp"]);
        Describe(Latest(_source)).Should().Equal(await FullPassDescriptionAsync());
    }

    [Fact]
    public async Task Flush_DeletedLastFileOfDirectoryAndCreatedEmptyDirectory_UpdatesEmptyDirectoryList()
    {
        var only = Write("solo/only.txt", "x");
        Write("keep/k.txt", "k");
        await NewBackup().BackupDirectoryAsync(_source);
        File.Delete(only);
        var made = Path.Combine(_source, "fresh");
        Directory.CreateDirectory(made);

        await NewBackup().BackupFilesAsync([only, made], _source);

        Latest(_source).Directories!.Select(d => d.RelativePath).Should().BeEquivalentTo("solo", "fresh");
        Describe(Latest(_source)).Should().Equal(await FullPassDescriptionAsync());
    }

    [Fact]
    public async Task Flush_FileInsideNowNonEmptyDirectory_DropsEmptyDirectoryEntry()
    {
        Write("keep/k.txt", "k");
        Directory.CreateDirectory(Path.Combine(_source, "hollow"));
        await NewBackup().BackupDirectoryAsync(_source);
        Latest(_source).Directories!.Should().ContainSingle(d => d.RelativePath == "hollow");

        var filled = Write("hollow/now.txt", "filled");
        await NewBackup().BackupFilesAsync([filled], _source);

        Latest(_source).Directories.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Flush_WithoutPreviousSnapshot_FallsBackToFullPass()
    {
        MakeTree(5);
        var backup = NewBackup();

        await backup.BackupFilesAsync([Path.Combine(_source, "d0", "f0.txt")], _source);

        backup.TreeScanCount.Should().Be(1);
        Latest(_source).Files.Should().HaveCount(5);
    }

    [Fact]
    public async Task Flush_WhenPreviousManifestIsMissingFromDestination_FallsBackToFullPass()
    {
        MakeTree(5);
        await NewBackup().BackupDirectoryAsync(_source);
        foreach (var manifest in Directory.GetFiles(_storageDir, "*.manifest.json", SearchOption.AllDirectories))
        {
            File.Delete(manifest);
        }

        var changed = Write("d0/f0.txt", "changed");
        var backup = NewBackup();
        await backup.BackupFilesAsync([changed], _source);

        backup.TreeScanCount.Should().Be(1);
        Latest(_source).Files.Should().HaveCount(5);
    }

    [Fact]
    public async Task Flush_WhenManifestFormatChanges_FallsBackToFullPass()
    {
        MakeTree(5);
        _chunkDiffing = new ChunkDiffingConfig { ManifestVersion = SnapshotManifest.VersionV2 };
        await NewBackup().BackupDirectoryAsync(_source);
        _chunkDiffing = new ChunkDiffingConfig { ManifestVersion = SnapshotManifest.VersionV3 };

        var changed = Write("d0/f0.txt", "changed");
        var backup = NewBackup();
        await backup.BackupFilesAsync([changed], _source);

        backup.TreeScanCount.Should().Be(1);
        Latest(_source).Version.Should().Be(SnapshotManifest.VersionV3);
        Describe(Latest(_source)).Should().Equal(await FullPassDescriptionAsync());
    }

    [Fact]
    public async Task Flush_OnV2Group_StaysV2AndIncremental()
    {
        MakeTree(5);
        _chunkDiffing = new ChunkDiffingConfig { ManifestVersion = SnapshotManifest.VersionV2 };
        await NewBackup().BackupDirectoryAsync(_source);

        var changed = Write("d0/f0.txt", "changed");
        var backup = NewBackup();
        await backup.BackupFilesAsync([changed], _source);

        backup.TreeScanCount.Should().Be(0);
        Latest(_source).Version.Should().Be(SnapshotManifest.VersionV2);
    }

    [Fact]
    public async Task Flush_FileBecomesDirectory_FallsBackAndDropsFileEntry()
    {
        var foo = Write("foo", "was a file");
        Write("other.txt", "o");
        await NewBackup().BackupDirectoryAsync(_source);
        File.Delete(foo);
        var bar = Write("foo/bar.txt", "now beneath a directory");

        var backup = NewBackup();
        await backup.BackupFilesAsync([foo, bar], _source);

        backup.TreeScanCount.Should().Be(1);
        Latest(_source).Files.Select(f => f.RelativePath).Should().BeEquivalentTo("foo/bar.txt", "other.txt");
    }

    [Fact]
    public async Task Flush_DirectoryBecomesFile_FallsBackAndDropsDescendants()
    {
        Write("foo/bar.txt", "beneath");
        Write("other.txt", "o");
        await NewBackup().BackupDirectoryAsync(_source);
        Directory.Delete(Path.Combine(_source, "foo"), true);
        var foo = Write("foo", "now a file");

        var backup = NewBackup();
        await backup.BackupFilesAsync([foo], _source);

        backup.TreeScanCount.Should().Be(1);
        Latest(_source).Files.Select(f => f.RelativePath).Should().BeEquivalentTo("foo", "other.txt");
    }

    [Fact]
    public async Task Flush_RecordsFullChunkSetOfTheSnapshot()
    {
        MakeTree(20);
        await NewBackup().BackupDirectoryAsync(_source);
        var changed = Write("d1/f1.txt", "changed");

        await NewBackup().BackupFilesAsync([changed], _source);

        var expected = Latest(_source).Files.SelectMany(f => f.Chunks).Select(c => c.ChunkId).Distinct().ToList();
        var recorded = await _state.TryGetSnapshotChunkIdsAsync(_state.GetLatestSnapshotForGroup(_source, "local", "local")!);
        recorded.Should().NotBeNull();
        recorded!.Should().BeEquivalentTo(expected);
        expected.Should().OnlyContain(id => _state.IsChunkInUse("local", null, id));
        expected.Count.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task Flush_LockedChangedFile_KeepsPreviousEntryAndReportsSkipped()
    {
        MakeTree(5);
        await NewBackup().BackupDirectoryAsync(_source);
        var before = Latest(_source).Files.Single(f => f.RelativePath == "d0/f0.txt");
        var locked = Write("d0/f0.txt", "changed but locked");

        var backup = NewBackup();
        backup.SourceFileOpener = p => p == locked ? throw new IOException("locked") : File.OpenRead(p);
        var result = await backup.BackupFilesAsync([locked], _source);

        result.HasSkipped.Should().BeTrue();
        Latest(_source).Files.Single(f => f.RelativePath == "d0/f0.txt").ContentHash.Should().Be(before.ContentHash);
        backup.TreeScanCount.Should().Be(0);
    }

    [Fact]
    public async Task Flush_ProgressTotalsCountOnlyChangedFiles()
    {
        MakeTree(30);
        await NewBackup().BackupDirectoryAsync(_source);
        var changed = Write("d1/f1.txt", "changed");
        var reports = new List<BackupProgress>();

        await NewBackup().BackupFilesAsync([changed], _source, progress: new Progress<BackupProgress>(r => { lock (reports) reports.Add(r); }));
        await Task.Delay(100);

        reports.Should().NotBeEmpty();
        reports.Should().OnlyContain(r => r.FilesTotal == 1);
    }
}
