using FluentAssertions;
using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// H2: stat-cache change detection with the racy-timestamp rule, the periodic deep scan that keeps the
/// same-size-same-timestamp rewrite detectable within a bounded window, and the single enumeration.
/// </summary>
public class StatCacheTests : IDisposable
{
    private static readonly DateTime OldTime = new(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(TestEnvironment.RootDirectory, "statcache_" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly TestLogger _logger = new();
    private readonly SystemState _state;
    private readonly Mock<IConfigManager> _config = new();
    private BackupSettingsConfig _backupSettings = new();

    public StatCacheTests()
    {
        _source = Path.GetFullPath(Path.Combine(_root, "src"));
        Directory.CreateDirectory(_source);

        _state = new SystemState(_logger);
        _state.SetStateFilePath(Path.Combine(_root, "state.json"));

        _config.Setup(c => c.Retention).Returns(new RetentionConfig { Enabled = false });
        _config.Setup(c => c.GlobalStorageType).Returns("local");
        _config.Setup(c => c.SizeThresholdMB).Returns(100);
        _config.Setup(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        _config.Setup(c => c.ExcludedPatterns).Returns([]);
        _config.Setup(c => c.ExcludedPaths).Returns([]);
        _config.Setup(c => c.BackupType).Returns(BackupType.Incremental);
        _config.Setup(c => c.WatchDirectories).Returns([]);
        _config.Setup(c => c.MaxFileSizeMB).Returns(100);
        _config.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig());
        _config.SetupGet(c => c.BackupSettings).Returns(() => _backupSettings);
        _config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).Returns(async (string _) =>
        {
            var storage = new LocalStorage(_logger);
            await storage.InitializeAsync(new Dictionary<string, string> { { "path", Path.Combine(_root, "storage") } });
            return (ReStore.Core.src.storage.IStorage)storage;
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static ScannedFile Stat(string path)
    {
        var info = new FileInfo(path);
        return new ScannedFile(path, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc, info.Attributes);
    }

    private string WriteOld(string name, string content, DateTime? mtime = null)
    {
        var path = Path.Combine(_source, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, mtime ?? OldTime);
        return path;
    }

    /// <summary>Records the file the way a committed snapshot would, scanning at <paramref name="scanStart"/>.</summary>
    private async Task RecordAsync(string path, DateTime scanStart)
    {
        var info = Stat(path);
        var entry = new SnapshotFileManifestEntry
        {
            RelativePath = Path.GetRelativePath(_source, path).Replace(Path.DirectorySeparatorChar, '/'),
            SizeBytes = info.Length,
            LastModifiedUtc = info.LastWriteUtc,
            ContentHash = await FileHasher.ComputeHashAsync(path)
        };
        _state.RecordFileMetadata(_source, "local", [entry], scanStart, [info]);
    }

    private List<string> Changed(string path, ChangeScanContext scan, BackupType type = BackupType.Incremental)
        => _state.GetChangedFiles([Stat(path)], type, _source, "local", scan);

    [Theory]
    [InlineData(BackupType.Incremental)]
    [InlineData(BackupType.ChunkSnapshot)]
    public async Task SameSizeRewriteWithPreservedTimestamp_IsMissedByNormalPass_ButFoundByDeepScan(BackupType type)
    {
        var path = WriteOld("same-size.txt", "content1");
        await RecordAsync(path, DateTime.UtcNow);

        File.WriteAllText(path, "content2");
        File.SetLastWriteTimeUtc(path, OldTime);

        var normal = new ChangeScanContext();
        Changed(path, normal, type).Should().BeEmpty("a normal pass trusts size and timestamps");
        normal.FilesReused.Should().Be(1);
        normal.FilesHashed.Should().Be(0);

        var deep = new ChangeScanContext { DeepScan = true };
        Changed(path, deep, type).Should().Contain(path, "a deep scan hashes every file");
        deep.FilesHashed.Should().Be(1);
        deep.FilesReused.Should().Be(0);
    }

    [Fact]
    public async Task ChangedSizeOrTimestamp_IsDetectedByNormalPass()
    {
        var path = WriteOld("a.txt", "content1");
        await RecordAsync(path, DateTime.UtcNow);

        File.SetLastWriteTimeUtc(path, OldTime.AddMinutes(1));
        Changed(path, new ChangeScanContext()).Should().BeEmpty("content equal: the hash confirms the touch is not a change");

        File.WriteAllText(path, "longer content");
        var scan = new ChangeScanContext();
        Changed(path, scan).Should().Contain(path);
        scan.FilesHashed.Should().Be(0, "a size change needs no read");
    }

    [Fact]
    public async Task ChangedContentWithNewTimestamp_IsDetectedByNormalPass()
    {
        var path = WriteOld("a.txt", "content1");
        await RecordAsync(path, DateTime.UtcNow);

        File.WriteAllText(path, "content2");
        File.SetLastWriteTimeUtc(path, OldTime.AddMinutes(1));

        Changed(path, new ChangeScanContext()).Should().Contain(path);
    }

    [Fact]
    public async Task RacyTimestamp_IsAlwaysHashed_AndASameSizeRewriteIsFound()
    {
        // Recorded 1 s after the file's mtime: within the 2 s window, so a later write could have left it unchanged.
        var path = WriteOld("racy.txt", "content1");
        await RecordAsync(path, OldTime.AddSeconds(1));

        File.WriteAllText(path, "content2");
        File.SetLastWriteTimeUtc(path, OldTime);

        var scan = new ChangeScanContext();
        Changed(path, scan).Should().Contain(path);
        scan.FilesHashed.Should().Be(1);
        scan.FilesReused.Should().Be(0);
    }

    [Fact]
    public async Task TimestampOutsideTheRacyWindow_IsTrusted()
    {
        var path = WriteOld("calm.txt", "content1");
        await RecordAsync(path, OldTime.AddSeconds(3));

        var scan = new ChangeScanContext();
        Changed(path, scan).Should().BeEmpty();
        scan.FilesReused.Should().Be(1);
        scan.FilesHashed.Should().Be(0);
    }

    [Fact]
    public async Task RacyFileFoundUnchanged_IsTrustedOnTheNextPass()
    {
        var path = WriteOld("racy.txt", "content1");
        await RecordAsync(path, OldTime.AddSeconds(1));

        var first = new ChangeScanContext { ScanStartUtc = OldTime.AddMinutes(5) };
        Changed(path, first).Should().BeEmpty();
        first.FilesHashed.Should().Be(1);

        var second = new ChangeScanContext { ScanStartUtc = OldTime.AddMinutes(10) };
        Changed(path, second).Should().BeEmpty();
        second.FilesReused.Should().Be(1);
        second.FilesHashed.Should().Be(0);
    }

    [Fact]
    public async Task MetadataWithoutAStatSignature_IsHashedOnceThenTrusted()
    {
        var path = WriteOld("legacy.txt", "content1");
        var info = Stat(path);
        _state.SetFileMetadata(_source, "local", path, new FileMetadata
        {
            FilePath = path,
            Size = info.Length,
            LastModified = info.LastWriteUtc,
            Hash = await FileHasher.ComputeHashAsync(path)
        });

        var first = new ChangeScanContext();
        Changed(path, first).Should().BeEmpty();
        first.FilesHashed.Should().Be(1);

        var second = new ChangeScanContext();
        Changed(path, second).Should().BeEmpty();
        second.FilesReused.Should().Be(1);
        second.FilesHashed.Should().Be(0);
    }

    [Fact]
    public async Task ChangedCreationTime_ForcesAHash()
    {
        var path = WriteOld("a.txt", "content1");
        await RecordAsync(path, DateTime.UtcNow);
        var meta = _state.MetadataFor(_source, path);
        meta.CreatedUtc = meta.CreatedUtc!.Value.AddHours(-1);

        var scan = new ChangeScanContext();
        Changed(path, scan).Should().BeEmpty();
        scan.FilesHashed.Should().Be(1);
    }

    [Fact]
    public async Task StatSignature_SurvivesSaveAndLoad_AndOldStateStillLoads()
    {
        var path = WriteOld("a.txt", "content1");
        await RecordAsync(path, DateTime.UtcNow);
        await _state.SaveStateAsync();

        var reloaded = new SystemState(_logger);
        reloaded.SetStateFilePath(Path.Combine(_root, "state.json"));
        await reloaded.LoadStateAsync();

        var meta = reloaded.MetadataFor(_source, path);
        meta.CreatedUtc.Should().NotBeNull();
        meta.RecordedUtc.Should().NotBeNull();

        // State written before the stat cache has neither field.
        var legacy = System.Text.Json.JsonSerializer.Deserialize<FileMetadata>("""{"FilePath":"a","Size":1,"LastModified":"2024-01-01T00:00:00Z","Hash":"x"}""")!;
        legacy.CreatedUtc.Should().BeNull();
        legacy.RecordedUtc.Should().BeNull();
    }

    [Fact]
    public async Task DeepScanSchedule_IsDueWhenNeverRunOrIntervalElapsed_AndPersists()
    {
        var now = new DateTime(2025, 6, 10, 0, 0, 0, DateTimeKind.Utc);

        _state.IsDeepScanDue(_source, "local", 7, now).Should().BeTrue("never deep-scanned");

        _state.RecordDeepScan(_source, "local", now);
        _state.IsDeepScanDue(_source, "local", 7, now.AddDays(6)).Should().BeFalse();
        _state.IsDeepScanDue(_source, "local", 7, now.AddDays(7)).Should().BeTrue();
        _state.IsDeepScanDue(_source, "local", 0, now.AddMinutes(1)).Should().BeTrue("0 = every run");
        _state.IsDeepScanDue(_source, "local", 7, now.AddDays(-1)).Should().BeTrue("a recorded time in the future is not trusted");
        _state.IsDeepScanDue(_source, "s3", 7, now.AddDays(1)).Should().BeTrue("tracked per storage type");

        await _state.SaveStateAsync();
        var reloaded = new SystemState(_logger);
        reloaded.SetStateFilePath(Path.Combine(_root, "state.json"));
        await reloaded.LoadStateAsync();
        reloaded.IsDeepScanDue(_source, "local", 7, now.AddDays(6)).Should().BeFalse();
    }

    [Fact]
    public void FileSelection_ScanFiles_ReturnsTheStatOfEachSelectedFileInOnePass()
    {
        var kept = WriteOld("kept.txt", "12345", OldTime);
        WriteOld("skip.tmp", "x");
        var sub = Path.Combine(_source, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "nested.txt"), "abc");

        var configMock = new Mock<IConfigManager>();
        configMock.Setup(c => c.ExcludedPaths).Returns([]);
        configMock.Setup(c => c.ExcludedPatterns).Returns(["*.tmp"]);
        configMock.Setup(c => c.MaxFileSizeMB).Returns(100);
        var service = new FileSelectionService(_logger, configMock.Object);

        var files = service.ScanFiles([_source]);

        files.Select(f => f.Path).Should().BeEquivalentTo([kept, Path.Combine(sub, "nested.txt")]);
        var record = files.Single(f => f.Path == kept);
        record.Length.Should().Be(5);
        record.LastWriteUtc.Should().Be(OldTime);
    }

    // ---- engine level

    private ReStore.Core.src.core.Backup NewBackup()
        => new(_logger, _state, new SizeAnalyzer(), _config.Object, new StaticPasswordProvider("pw"), new RepositoryCoordinator(Path.Combine(_root, "repository.lock")));

    private bool LoggedScan(string kind, int hashed, int reused)
        => _logger.Messages.Any(m => m.Contains($"({kind})") && m.Contains($"{hashed} hashed, {reused} reused unread"));

    [Fact]
    public async Task Backup_SecondRunReusesUnchangedFilesWithoutReadingThem_AndDeepScanFindsASilentRewrite()
    {
        _backupSettings = new BackupSettingsConfig { DeepScanIntervalDays = 7 };
        var path = WriteOld("data.txt", "content1");
        WriteOld("other.txt", "other");

        await NewBackup().BackupDirectoryAsync(_source);
        LoggedScan("deep", 0, 0).Should().BeTrue("the first run has no previous snapshot, so it is a deep scan");

        await NewBackup().BackupDirectoryAsync(_source);
        LoggedScan("stat-cache", 0, 2).Should().BeTrue("unchanged files are recognised without being read");

        // Same size, same timestamp, different bytes: invisible to the stat cache until the next deep scan.
        File.WriteAllText(path, "content2");
        File.SetLastWriteTimeUtc(path, OldTime);
        var manifestsBefore = Directory.GetFiles(Path.Combine(_root, "storage"), "*.manifest.json", SearchOption.AllDirectories).Length;
        await NewBackup().BackupDirectoryAsync(_source);
        Directory.GetFiles(Path.Combine(_root, "storage"), "*.manifest.json", SearchOption.AllDirectories)
            .Length.Should().Be(manifestsBefore, "nothing looked changed");

        _backupSettings = new BackupSettingsConfig { DeepScanIntervalDays = 0 };
        await NewBackup().BackupDirectoryAsync(_source);
        Directory.GetFiles(Path.Combine(_root, "storage"), "*.manifest.json", SearchOption.AllDirectories)
            .Length.Should().Be(manifestsBefore + 1, "the deep scan hashed the file and found the rewrite");
        LoggedScan("deep", 2, 0).Should().BeTrue();
    }

    [Fact]
    public void ConfigMigration_AddsDeepScanIntervalDefault_AndKeepsAnExplicitValue()
    {
        var missing = System.Text.Json.Nodes.JsonNode.Parse("""{ "configSchemaVersion": 4 }""") as System.Text.Json.Nodes.JsonObject;
        ConfigSchemaManager.Migrate(missing!).AppliedMigrations.Should().Contain(m => m.Contains("deepScanIntervalDays"));
        missing!["backup"]!["deepScanIntervalDays"]!.GetValue<int>().Should().Be(7);

        var explicitValue = System.Text.Json.Nodes.JsonNode.Parse("""{ "configSchemaVersion": 4, "backup": { "deepScanIntervalDays": 0 } }""") as System.Text.Json.Nodes.JsonObject;
        ConfigSchemaManager.Migrate(explicitValue!);
        explicitValue!["backup"]!["deepScanIntervalDays"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task DeepScan_FileThatCannotBeHashed_IsChangedAndUnverified()
    {
        var path = WriteOld("gone.txt", "content1");
        await RecordAsync(path, DateTime.UtcNow);
        var stale = Stat(path);
        File.Delete(path);

        var scan = new ChangeScanContext { DeepScan = true };
        _state.GetChangedFiles([stale], BackupType.Incremental, _source, "local", scan).Should().Contain(path);
        scan.FilesUnverified.Should().Be(1);
    }

    [Fact]
    public async Task Backup_StatUnchangedFile_IsRechunkedWhenItsRecordDisagreesWithThePreviousEntry()
    {
        var data = WriteOld("data.txt", "content1");
        WriteOld("other.txt", "other");
        await NewBackup().BackupDirectoryAsync(_source);

        var opens = new List<string>();
        Stream Open(string p)
        {
            opens.Add(Path.GetFileName(p));
            return File.OpenRead(p);
        }

        // Another file changes so a snapshot is made; data.txt is stat-unchanged and its record agrees: not read.
        var trigger = WriteOld("trigger.txt", "t1");
        var backup = NewBackup();
        backup.SourceFileOpener = Open;
        await backup.BackupDirectoryAsync(_source);
        opens.Should().NotContain("data.txt");

        // The record now claims different content than the previous snapshot entry holds.
        var meta = _state.MetadataFor(_source, data);
        meta.Hash = "00" + meta.Hash[2..];
        File.WriteAllText(trigger, "t2");
        File.SetLastWriteTimeUtc(trigger, OldTime.AddMinutes(1));
        opens.Clear();
        backup = NewBackup();
        backup.SourceFileOpener = Open;
        await backup.BackupDirectoryAsync(_source);
        opens.Should().Contain("data.txt", "a record that disagrees with the entry is not trusted");
    }
}
