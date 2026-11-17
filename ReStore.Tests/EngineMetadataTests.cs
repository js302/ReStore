using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// H3 (metadata from the bytes backed up, removals only after a commit), H4 (per-destination metadata and
/// forced full passes), M-E3 (labelled snapshots survive retention), M-R2 (stale .restorepartial files) and
/// E-23 (case-only path duplicates).
/// </summary>
public class EngineMetadataTests : IDisposable
{
    private readonly string _root = Path.Combine(TestEnvironment.RootDirectory, "engine_" + Guid.NewGuid().ToString("N"));
    private readonly TestLogger _logger = new();
    private readonly string _source;
    private readonly string _statePath;
    private readonly SystemState _state;
    private readonly Mock<IConfigManager> _config = new();
    private readonly RepositoryCoordinator _coordinator;
    private readonly EncryptionConfig _encryption = new() { Enabled = false, KeyDerivationIterations = 1000 };
    private string _password = "pw-one";

    /// <summary>Called with the remote path of every manifest upload, i.e. after all chunks of a snapshot are stored.</summary>
    private Action<string>? _beforeManifestUpload;

    public EngineMetadataTests()
    {
        _source = Path.GetFullPath(Path.Combine(_root, "src"));
        Directory.CreateDirectory(_source);

        _coordinator = new RepositoryCoordinator(Path.Combine(_root, "repository.lock"));
        _statePath = Path.Combine(_root, "state.json");
        _state = new SystemState(_logger);
        _state.SetStateFilePath(_statePath);

        _config.Setup(c => c.Retention).Returns(new RetentionConfig { Enabled = false });
        _config.Setup(c => c.GlobalStorageType).Returns("local");
        _config.Setup(c => c.SizeThresholdMB).Returns(100);
        _config.Setup(c => c.Encryption).Returns(_encryption);
        _config.Setup(c => c.ExcludedPatterns).Returns([]);
        _config.Setup(c => c.ExcludedPaths).Returns([]);
        _config.Setup(c => c.BackupType).Returns(BackupType.Incremental);
        _config.Setup(c => c.WatchDirectories).Returns([]);
        _config.Setup(c => c.MaxFileSizeMB).Returns(100);
        _config.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig { ManifestVersion = 2 }); // these tests exercise the v2 password-derived namespace
        _config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).Returns(async (string type) =>
        {
            var inner = new LocalStorage(_logger);
            await inner.InitializeAsync(new Dictionary<string, string> { { "path", StorageDir(type) } });
            return new HookStorage(inner, path => _beforeManifestUpload?.Invoke(path));
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class HookStorage(IStorage inner, Action<string> onUpload) : DelegatingStorage(inner)
    {
        public override Task UploadAsync(string localPath, string remotePath, CancellationToken ct = default)
        {
            if (remotePath.EndsWith(".manifest.json", StringComparison.Ordinal))
            {
                onUpload(remotePath);
            }

            return base.UploadAsync(localPath, remotePath, ct);
        }
    }

    private string StorageDir(string type) => Path.Combine(_root, "storage-" + type);

    private Backup NewBackup(SystemState? state = null) =>
        new(_logger, state ?? _state, new SizeAnalyzer(), _config.Object, new StaticPasswordProvider(_password), _coordinator);

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_source, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private List<SnapshotManifest> Manifests(string type = "local") =>
        Directory.Exists(StorageDir(type))
            ? Directory.GetFiles(StorageDir(type), "*.manifest.json", SearchOption.AllDirectories)
                .Select(p => ManifestIo.Read(p)!)
                .OrderBy(m => m.CreatedUtc)
                .ToList()
            : [];

    private int ChunkCount(string type) =>
        Directory.Exists(StorageDir(type))
            ? Directory.GetFiles(StorageDir(type), "*.chunk", SearchOption.AllDirectories).Length
            : 0;

    private static string Sha(string content) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

    // ---- H3

    [Fact]
    public async Task FileChangedAfterItWasChunked_IsRecordedWithTheSnapshotsHash_AndBackedUpNextRun()
    {
        var file = Write("db.bin", "version one");
        Write("other.txt", "other");

        // The file changes after its chunks are stored but before the snapshot is committed.
        _beforeManifestUpload = _ => File.WriteAllText(file, "version two!");
        await NewBackup().BackupDirectoryAsync(_source);
        _beforeManifestUpload = null;

        var recorded = _state.MetadataFor(_source, file);
        recorded.Hash.Should().Be(Sha("version one"), "the snapshot holds the bytes that were read, not what is on disk now");
        recorded.Size.Should().Be("version one".Length);
        Manifests().Single().Files.Single(f => f.RelativePath == "db.bin").ContentHash.Should().Be(recorded.Hash);

        await NewBackup().BackupDirectoryAsync(_source);

        var latest = Manifests().Last();
        latest.Files.Single(f => f.RelativePath == "db.bin").ContentHash.Should().Be(Sha("version two!"));
        _state.MetadataFor(_source, file).Hash.Should().Be(Sha("version two!"));
    }

    [Fact]
    public async Task RemovedFile_StaysTracked_WhenTheSnapshotCannotBeCommitted_AndIsDroppedOnceItIs()
    {
        var keep = Write("keep.txt", "keep");
        var gone = Write("gone.txt", "gone");
        await NewBackup().BackupDirectoryAsync(_source);
        _state.HasMetadata(_source, gone).Should().BeTrue();

        File.Delete(gone);
        _beforeManifestUpload = _ => throw new IOException("storage went away");
        var failing = () => NewBackup().BackupDirectoryAsync(_source);
        await failing.Should().ThrowAsync<IOException>();
        _beforeManifestUpload = null;

        _state.HasMetadata(_source, gone).Should().BeTrue("the removal is only applied after a snapshot commits");
        Manifests().Should().ContainSingle();

        await NewBackup().BackupDirectoryAsync(_source);

        Manifests().Should().HaveCount(2, "the retry must still see the file as removed");
        Manifests().Last().Files.Select(f => f.RelativePath).Should().BeEquivalentTo(["keep.txt"]);
        _state.HasMetadata(_source, gone).Should().BeFalse();
        _state.HasMetadata(_source, keep).Should().BeTrue();
    }

    // ---- H4

    [Fact]
    public async Task SwitchingStorageType_BacksUpEveryFileToTheNewDestination()
    {
        Write("a.txt", "alpha");
        Write("b.txt", "beta");
        await NewBackup().BackupDirectoryAsync(_source, "local");
        ChunkCount("other").Should().Be(0);

        await NewBackup().BackupDirectoryAsync(_source, "other");

        var manifest = Manifests("other").Should().ContainSingle().Subject;
        manifest.Files.Select(f => f.RelativePath).Should().BeEquivalentTo(["a.txt", "b.txt"]);
        ChunkCount("other").Should().Be(ChunkCount("local"), "the new destination received all chunks, not only changes");

        // Each destination now has its own record, and neither re-uploads on an unchanged run.
        _state.HasMetadata(_source, Path.Combine(_source, "a.txt"), "other").Should().BeTrue();
        await NewBackup().BackupDirectoryAsync(_source, "other");
        await NewBackup().BackupDirectoryAsync(_source, "local");
        Manifests("other").Should().ContainSingle();
        Manifests("local").Should().ContainSingle();
    }

    [Fact]
    public async Task TurningEncryptionOn_WithNothingChanged_BacksUpEverythingEncrypted()
    {
        _encryption.Salt = Convert.ToBase64String(EncryptionService.GenerateSalt());
        Write("a.txt", "alpha");
        await NewBackup().BackupDirectoryAsync(_source);

        _encryption.Enabled = true;
        await NewBackup().BackupDirectoryAsync(_source);

        var manifests = Manifests();
        manifests.Should().HaveCount(2);
        manifests[1].EncryptionEnabled.Should().BeTrue();
        manifests[1].Files.Select(f => f.RelativePath).Should().BeEquivalentTo(["a.txt"]);
        ChunkCount("local").Should().BeGreaterThan(1, "the encrypted chunks live next to the plain ones");

        await NewBackup().BackupDirectoryAsync(_source);
        Manifests().Should().HaveCount(2, "nothing changed since the encrypted snapshot");

        _encryption.Enabled = false;
        await NewBackup().BackupDirectoryAsync(_source);
        Manifests().Should().HaveCount(3);
        Manifests()[2].EncryptionEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task ChangingTheEncryptionPassword_WithNothingChanged_BacksUpEverythingAgain()
    {
        _encryption.Enabled = true;
        _encryption.Salt = Convert.ToBase64String(EncryptionService.GenerateSalt());
        Write("a.txt", "alpha");
        await NewBackup().BackupDirectoryAsync(_source);
        await NewBackup().BackupDirectoryAsync(_source);
        Manifests().Should().ContainSingle();

        _password = "pw-two";
        await NewBackup().BackupDirectoryAsync(_source);

        var manifests = Manifests();
        manifests.Should().HaveCount(2);
        manifests[1].ChunkStorageNamespace.Should().NotBe(manifests[0].ChunkStorageNamespace);
        manifests[1].Files.Should().ContainSingle();
    }

    [Fact]
    public async Task MissingManifestAtTheDestination_ForcesAFullPass()
    {
        Write("a.txt", "alpha");
        Write("b.txt", "beta");
        await NewBackup().BackupDirectoryAsync(_source);
        var manifestFile = Directory.GetFiles(StorageDir("local"), "*.manifest.json", SearchOption.AllDirectories).Single();
        File.Delete(manifestFile);

        await NewBackup().BackupDirectoryAsync(_source);

        var manifests = Manifests();
        manifests.Should().ContainSingle("the lost snapshot is rebuilt rather than reported as 'nothing to do'");
        manifests[0].Files.Select(f => f.RelativePath).Should().BeEquivalentTo(["a.txt", "b.txt"]);
    }

    [Fact]
    public async Task MetadataWithoutAnySnapshotAtTheDestination_ForcesAFullPass()
    {
        Write("a.txt", "alpha");
        await NewBackup().BackupDirectoryAsync(_source);

        // History lost (e.g. the destination was re-pointed) while the file metadata survives.
        _state.BackupHistory.Clear();
        Directory.Delete(StorageDir("local"), recursive: true);

        await NewBackup().BackupDirectoryAsync(_source);

        Manifests().Should().ContainSingle().Which.Files.Should().ContainSingle();
    }

    [Fact]
    public async Task UnchangedRunAtAHealthyDestination_DoesNotCreateASnapshot()
    {
        Write("a.txt", "alpha");
        await NewBackup().BackupDirectoryAsync(_source);
        await NewBackup().BackupDirectoryAsync(_source);

        Manifests().Should().ContainSingle();
    }

    [Fact]
    public async Task FlatFileMetadataFromV17_IsMigratedToTheGroupsLatestStorageType()
    {
        var a = Write("a.txt", "alpha");
        Write("sub/b.txt", "beta");
        await NewBackup().BackupDirectoryAsync(_source, "local");
        await _state.SaveStateAsync();

        // Rewrite the state file the way v1.7 wrote it: one flat dictionary keyed by absolute path.
        var root = JsonNode.Parse(await File.ReadAllTextAsync(_statePath))!.AsObject();
        var flat = new JsonObject();
        foreach (var (rel, metadata) in root["FileMetadataByGroup"]![_source]!["local"]!.AsObject())
        {
            var absolute = Path.Combine(_source, rel.Replace('/', Path.DirectorySeparatorChar));
            var copy = JsonNode.Parse(metadata!.ToJsonString())!.AsObject();
            copy["FilePath"] = absolute;
            flat[absolute] = copy;
        }

        root.Remove("FileMetadataByGroup");
        root["FileMetadata"] = flat;
        await File.WriteAllTextAsync(_statePath, root.ToJsonString());

        var upgraded = new SystemState(_logger);
        upgraded.SetStateFilePath(_statePath);
        await upgraded.LoadStateAsync();

        upgraded.HasMetadata(_source, a).Should().BeTrue();
        upgraded.HasMetadata(_source, Path.Combine(_source, "sub", "b.txt")).Should().BeTrue();
        upgraded.GetTrackedFiles(_source, "local").Should().HaveCount(2);
        upgraded.GetTrackedFiles(_source, "other").Should().BeEmpty("old entries describe the destination the group was last backed up to");

        await NewBackup(upgraded).BackupDirectoryAsync(_source, "local");
        Manifests().Should().ContainSingle("migrated metadata still says nothing changed");

        // What is written back is the new structure only: v1.7 reads a state without file metadata, not a changed one.
        await upgraded.SaveStateAsync();
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(_statePath))!.AsObject();
        saved.ContainsKey("FileMetadata").Should().BeFalse();
        saved.ContainsKey("FileMetadataByGroup").Should().BeTrue();
    }

    [Fact]
    public async Task FlatFileMetadata_OfAFolderWithoutASnapshot_IsDropped()
    {
        var orphan = Path.Combine(_root, "never-backed-up", "x.txt");
        var json = new JsonObject
        {
            ["FileMetadata"] = new JsonObject
            {
                [orphan] = new JsonObject { ["FilePath"] = orphan, ["Size"] = 1, ["Hash"] = "h", ["LastModified"] = "2026-01-01T00:00:00Z" }
            }
        };
        await File.WriteAllTextAsync(_statePath, json.ToJsonString());

        var loaded = new SystemState(_logger);
        loaded.SetStateFilePath(_statePath);
        await loaded.LoadStateAsync();

        loaded.GetTrackedFiles(Path.GetDirectoryName(orphan)!, "local").Should().BeEmpty();
    }

    // ---- M-E3

    [Fact]
    public void Retention_NeverSelectsALabelledSnapshot()
    {
        var now = DateTime.UtcNow;
        var backups = new List<BackupInfo>
        {
            new() { Path = "new", Timestamp = now },
            new() { Path = "mid", Timestamp = now.AddDays(-100) },
            new() { Path = "old-labelled", Timestamp = now.AddDays(-400), Label = "before reinstall" },
            new() { Path = "old", Timestamp = now.AddDays(-500) },
            new() { Path = "blank-label", Timestamp = now.AddDays(-450), Label = "  " }
        };

        var toDelete = RetentionManager.SelectBackupsToDelete(backups, new RetentionConfig { Enabled = true, KeepLastPerDirectory = 1, MaxAgeDays = 30 });

        toDelete.Select(b => b.Path).Should().BeEquivalentTo(["mid", "old", "blank-label"]);
    }

    // ---- M-R2

    [Fact]
    public async Task RestorePartialFiles_AreNotBackedUp()
    {
        Write("a.txt", "alpha");
        Write("a.txt.restorepartial", "half a file");
        Write("deep/b.bin.RESTOREPARTIAL", "half");

        await NewBackup().BackupDirectoryAsync(_source);

        Manifests().Single().Files.Select(f => f.RelativePath).Should().BeEquivalentTo(["a.txt"]);

        var viaWatcher = Write("c.txt.restorepartial", "x");
        await NewBackup().BackupFilesAsync([viaWatcher], _source);
        Manifests().Should().ContainSingle("a lone partial file is not a change");
    }

    [Fact]
    public async Task Restore_SweepsStalePartialFiles_ButNotOtherFiles()
    {
        Write("a.txt", "alpha");
        await NewBackup().BackupDirectoryAsync(_source);
        var manifestPath = _state.GetPreviousBackupPath(_source)!;

        var target = Path.Combine(_root, "restore-target");
        Directory.CreateDirectory(Path.Combine(target, "nested"));
        var stale = Path.Combine(target, "nested", "crashed.dat.restorepartial");
        var lookalike = Path.Combine(target, "nested", "notes.restorepartial.txt");
        var other = Path.Combine(target, "keep.partial");
        File.WriteAllText(stale, "leftover");
        File.WriteAllText(lookalike, "mine");
        File.WriteAllText(other, "mine");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));

        using var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { { "path", StorageDir("local") } });
        await new Restore(_logger, storage, null, _state, _coordinator).RestoreFromBackupAsync(manifestPath, target);

        File.Exists(stale).Should().BeFalse();
        File.Exists(lookalike).Should().BeTrue();
        File.Exists(other).Should().BeTrue();
        File.ReadAllText(Path.Combine(target, "a.txt")).Should().Be("alpha");
        Directory.GetFiles(target, "*.restorepartial", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task Restore_LeavesAPartialFileWrittenAfterTheRunStarted()
    {
        Write("a.txt", "alpha");
        await NewBackup().BackupDirectoryAsync(_source);
        var manifestPath = _state.GetPreviousBackupPath(_source)!;

        var target = Path.Combine(_root, "restore-target-fresh");
        Directory.CreateDirectory(target);
        var fresh = Path.Combine(target, "other-restore.bin.restorepartial");
        File.WriteAllText(fresh, "being written by another restore");
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow.AddMinutes(5));

        using var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { { "path", StorageDir("local") } });
        await new Restore(_logger, storage, null, _state, _coordinator).RestoreFromBackupAsync(manifestPath, target);

        File.Exists(fresh).Should().BeTrue();
    }

    // ---- E-23

    [Fact]
    public async Task PathsDifferingOnlyByCase_DoNotBreakTheBackup_AndEachKeepsItsOwnContent()
    {
        var upper = Path.Combine(_source, "Report.txt");
        var lower = Path.Combine(_source, "report.txt");
        File.WriteAllText(upper, "UPPER");
        File.WriteAllText(lower, "lower");
        if (Directory.GetFiles(_source).Length < 2)
        {
            return; // case-insensitive file system: such a pair cannot exist
        }

        await NewBackup().BackupDirectoryAsync(_source);
        File.WriteAllText(upper, "UPPER changed");
        await NewBackup().BackupDirectoryAsync(_source);

        var files = Manifests().Last().Files;
        files.Single(f => f.RelativePath == "Report.txt").ContentHash.Should().Be(Sha("UPPER changed"));
        files.Single(f => f.RelativePath == "report.txt").ContentHash.Should().Be(Sha("lower"));
        _logger.Messages.Should().Contain(m => m.Contains("differ only by case"));
    }
}
