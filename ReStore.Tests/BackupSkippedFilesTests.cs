using System.Text.Json;
using FluentAssertions;
using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// C4: one locked / vanished / unreadable file must not abort the snapshot. The file is carried
/// forward from the previous snapshot when possible, reported as skipped, and left out of the
/// file metadata so the next run retries it.
/// </summary>
public class BackupSkippedFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(TestEnvironment.RootDirectory, "skipped_" + Guid.NewGuid().ToString("N"));
    private readonly TestLogger _logger = new();
    private readonly string _storageDir;
    private readonly string _source;
    private readonly SystemState _state;
    private readonly Mock<IConfigManager> _config = new();
    private readonly RepositoryCoordinator _coordinator;

    public BackupSkippedFilesTests()
    {
        _storageDir = Path.Combine(_root, "storage");
        _source = Path.GetFullPath(Path.Combine(_root, "src"));
        Directory.CreateDirectory(_storageDir);
        Directory.CreateDirectory(_source);

        _coordinator = new RepositoryCoordinator(Path.Combine(_root, "repository.lock"));
        _state = new SystemState(_logger);
        _state.SetStateFilePath(Path.Combine(_root, "state.json"));

        _config.Setup(c => c.Retention).Returns(new RetentionConfig { Enabled = false, KeepLastPerDirectory = 10, MaxAgeDays = 30 });
        _config.Setup(c => c.GlobalStorageType).Returns("local");
        _config.Setup(c => c.SizeThresholdMB).Returns(100);
        _config.Setup(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        _config.Setup(c => c.ExcludedPatterns).Returns([]);
        _config.Setup(c => c.ExcludedPaths).Returns([]);
        _config.Setup(c => c.BackupType).Returns(BackupType.Incremental);
        _config.Setup(c => c.WatchDirectories).Returns([]);
        _config.Setup(c => c.MaxFileSizeMB).Returns(100);
        _config.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig());
        _config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).Returns(async () =>
        {
            var storage = new LocalStorage(_logger);
            await storage.InitializeAsync(new Dictionary<string, string> { { "path", _storageDir } });
            return storage;
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class PinRecordingState(ILogger logger) : SystemState(logger)
    {
        public List<string> Pinned { get; } = [];

        public override void PinChunks(object token, IEnumerable<string> chunkIds)
        {
            var ids = chunkIds.ToList();
            lock (Pinned)
            {
                Pinned.AddRange(ids);
            }

            base.PinChunks(token, ids);
        }
    }

    private Backup NewBackup(Func<string, Stream>? opener = null, SystemState? state = null) =>
        new(_logger, state ?? _state, new SizeAnalyzer(), _config.Object, null, _coordinator) { SourceFileOpener = opener };

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_source, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static Stream OpenShared(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private Dictionary<string, SnapshotFileManifestEntry> LatestManifestFiles()
    {
        var manifests = Directory.GetFiles(_storageDir, "*.manifest.json", SearchOption.AllDirectories)
            .Select(p => ManifestIo.Read(p)!)
            .OrderBy(m => m.CreatedUtc)
            .ToList();
        return manifests.Last().Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LockedFile_DoesNotAbortSnapshot_AndIsReportedSkipped()
    {
        var good = Write("good.txt", "good");
        var locked = Write("locked.pst", "locked content");

        var result = await NewBackup(path =>
            path.EndsWith("locked.pst", StringComparison.Ordinal)
                ? throw new IOException("The process cannot access the file because it is being used by another process.")
                : OpenShared(path)).BackupDirectoryAsync(_source);

        result.Skipped.Should().ContainSingle();
        result.Skipped[0].Path.Should().Be(locked);
        result.Skipped[0].CarriedForward.Should().BeFalse();
        result.SkippedSummary.Should().Contain("1 skipped");
        LatestManifestFiles().Keys.Should().BeEquivalentTo(["good.txt"]);
        _state.HasMetadata(_source, good).Should().BeTrue();
        _state.HasMetadata(_source, locked).Should().BeFalse("a skipped file must be retried next run");
    }

    [Fact]
    public async Task UnreadableFile_WithPreviousEntry_IsCarriedForward_AndMetadataNotUpdated()
    {
        var good = Write("good.txt", "good");
        var target = Write("mail.pst", "version one");
        await NewBackup().BackupDirectoryAsync(_source);
        var oldEntry = LatestManifestFiles()["mail.pst"];
        var oldMetadata = _state.MetadataFor(_source, target);

        File.WriteAllText(target, "version two, now locked by Outlook");
        var result = await NewBackup(path =>
            path == target ? throw new UnauthorizedAccessException("denied") : OpenShared(path)).BackupDirectoryAsync(_source);

        result.Skipped.Should().ContainSingle().Which.CarriedForward.Should().BeTrue();
        var files = LatestManifestFiles();
        files["mail.pst"].ContentHash.Should().Be(oldEntry.ContentHash);
        files.Should().ContainKey("good.txt");
        _state.MetadataFor(_source, target).Hash.Should().Be(oldMetadata.Hash);
        _state.HasMetadata(_source, good).Should().BeTrue();

        // Next run (file readable again) picks the new version up.
        var retry = await NewBackup().BackupDirectoryAsync(_source);
        retry.Skipped.Should().BeEmpty();
        LatestManifestFiles()["mail.pst"].ContentHash.Should().NotBe(oldEntry.ContentHash);
    }

    [Fact]
    public async Task FileDeletedAfterEnumeration_IsSkipped_AndNotCarriedWhenAbsentFromPreviousSnapshot()
    {
        Write("good.txt", "good");
        var vanishing = Write("vanishing.txt", "here a moment ago");

        var result = await NewBackup(path =>
            path == vanishing ? throw new FileNotFoundException("gone", path) : OpenShared(path)).BackupDirectoryAsync(_source);

        result.Skipped.Should().ContainSingle().Which.CarriedForward.Should().BeFalse();
        LatestManifestFiles().Keys.Should().BeEquivalentTo(["good.txt"]);
    }

    [Fact]
    public async Task FileReallyDeletedBeforeRead_IsSkippedWithoutFailingTheBackup()
    {
        // Real deletion between enumeration and read: simulated by deleting inside the opener seam's predecessor
        // step (the first file read), so the second no longer exists when its turn comes.
        var first = Write("a.txt", "first");
        var second = Write("b.txt", "second");

        var result = await NewBackup(path =>
        {
            if (path == first)
            {
                File.Delete(second);
            }

            return OpenShared(path);
        }).BackupDirectoryAsync(_source);

        result.Skipped.Should().ContainSingle().Which.Path.Should().Be(second);
        LatestManifestFiles().Keys.Should().BeEquivalentTo(["a.txt"]);
    }

    [PosixPermissionsFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task UnreadableDirectory_WithPreviousSnapshot_CarriesItsEntriesForward()
    {
        Write("top.txt", "top");
        Write("private/one.txt", "one");
        Write("private/deeper/two.txt", "two");
        await NewBackup().BackupDirectoryAsync(_source);

        var privateDir = Path.Combine(_source, "private");
        var topFile = Write("top.txt", "top changed");
        File.SetUnixFileMode(privateDir, UnixFileMode.None);
        try
        {
            var result = await NewBackup().BackupDirectoryAsync(_source);

            result.Skipped.Should().HaveCount(2).And.OnlyContain(s => s.CarriedForward);
            LatestManifestFiles().Keys.Should().BeEquivalentTo(["top.txt", "private/one.txt", "private/deeper/two.txt"]);
            _state.HasMetadata(_source, Path.Combine(privateDir, "one.txt")).Should().BeTrue("metadata for unreadable files is kept");
            _state.HasMetadata(_source, topFile).Should().BeTrue();
        }
        finally
        {
            File.SetUnixFileMode(privateDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [PosixPermissionsFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task UnreadableDirectory_WithoutPreviousSnapshot_IsReportedNotCarried()
    {
        Write("top.txt", "top");
        Write("private/one.txt", "one");
        var privateDir = Path.Combine(_source, "private");
        File.SetUnixFileMode(privateDir, UnixFileMode.None);
        try
        {
            var result = await NewBackup().BackupDirectoryAsync(_source);

            result.Skipped.Should().ContainSingle().Which.Should().Match<SkippedFile>(s => s.Path == privateDir && !s.CarriedForward);
            LatestManifestFiles().Keys.Should().BeEquivalentTo(["top.txt"]);
        }
        finally
        {
            File.SetUnixFileMode(privateDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task FileChangingDuringRead_IsRetried_ThenSucceeds()
    {
        var target = Write("live.log", "line one\n");
        var opens = 0;

        var result = await NewBackup(path =>
        {
            var stream = OpenShared(path);
            if (path == target && ++opens == 1)
            {
                File.AppendAllText(path, "appended mid-read\n");
            }

            return stream;
        }).BackupDirectoryAsync(_source);

        opens.Should().Be(2);
        result.Skipped.Should().BeEmpty();
        var entry = LatestManifestFiles()["live.log"];
        entry.SizeBytes.Should().Be(entry.Chunks.Sum(c => (long)c.PlainSizeBytes))
            .And.Be(new FileInfo(target).Length);
    }

    [Fact]
    public async Task FileAlwaysChangingDuringRead_IsSkippedAfterThreeAttempts()
    {
        var target = Write("busy.db", "start\n");
        var opens = 0;

        var result = await NewBackup(path =>
        {
            var stream = OpenShared(path);
            if (path == target)
            {
                opens++;
                File.AppendAllText(path, "more\n");
            }

            return stream;
        }).BackupDirectoryAsync(_source);

        opens.Should().Be(3, "the first attempt plus two retries");
        result.Skipped.Should().ContainSingle().Which.Reason.Should().Contain("Changed during backup");
        _state.HasMetadata(_source, target).Should().BeFalse();
    }

    [Fact]
    public async Task ChunkingService_ReadsFilesOpenedByAnotherWriter()
    {
        var path = Write("shared.txt", "shared");
        await using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        var result = await NewBackup().BackupDirectoryAsync(_source);

        result.Skipped.Should().BeEmpty();
        LatestManifestFiles().Should().ContainKey("shared.txt");
    }

    [Fact]
    public async Task FileDeletedAfterEnumeration_WithPreviousEntry_IsNotCarriedForward()
    {
        Write("good.txt", "good");
        var doomed = Write("doomed.txt", "present in the previous snapshot");
        await NewBackup().BackupDirectoryAsync(_source);
        LatestManifestFiles().Should().ContainKey("doomed.txt");

        File.WriteAllText(doomed, "changed, then deleted before it is read");
        var result = await NewBackup(path =>
        {
            if (path == doomed)
            {
                File.Delete(doomed);
            }

            return OpenShared(path);
        }).BackupDirectoryAsync(_source);

        result.Skipped.Should().ContainSingle().Which.Should().Match<SkippedFile>(
            s => s.Path == doomed && !s.CarriedForward && s.Reason.Contains("Deleted"));
        LatestManifestFiles().Keys.Should().BeEquivalentTo(["good.txt"]);
    }

    [Fact]
    public async Task CarriedForwardEntries_ArePinnedDuringTheRun_AndReferencedAfterCommit()
    {
        var state = new PinRecordingState(_logger);
        state.SetStateFilePath(Path.Combine(_root, "state2.json"));
        var target = Write("mail.pst", "version one");
        await NewBackup(null, state).BackupDirectoryAsync(_source);
        var oldChunkIds = LatestManifestFiles()["mail.pst"].Chunks.Select(c => c.ChunkId).ToList();
        oldChunkIds.Should().NotBeEmpty();
        state.Pinned.Clear();

        File.WriteAllText(target, "version two");
        await NewBackup(path => path == target ? throw new IOException("locked") : OpenShared(path), state)
            .BackupDirectoryAsync(_source);

        state.Pinned.Should().Contain(oldChunkIds);
        foreach (var id in oldChunkIds)
        {
            state.ChunkReferenceCounts.Should().Contain(pair => pair.Key.EndsWith(id, StringComparison.OrdinalIgnoreCase) && pair.Value >= 2);
        }
    }

    [PosixPermissionsFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void FileSelectionService_ReportsUnreadableDirectories()
    {
        Write("top.txt", "top");
        Write("private/one.txt", "one");
        var privateDir = Path.Combine(_source, "private");
        File.SetUnixFileMode(privateDir, UnixFileMode.None);
        try
        {
            var unreadable = new List<string>();
            var files = new FileSelectionService(_logger, _config.Object).GetFilesToBackup([_source], unreadable);

            files.Should().BeEquivalentTo([Path.Combine(_source, "top.txt")]);
            unreadable.Should().BeEquivalentTo([privateDir]);
        }
        finally
        {
            File.SetUnixFileMode(privateDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
