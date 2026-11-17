using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

[Collection("GlobalState")]
public class WatcherAndConfigDefaultsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreWatcherMinTests_" + Guid.NewGuid().ToString("N"));
    private readonly string _watchDir;
    private readonly TestLogger _logger = new();
    private readonly Mock<IConfigManager> _config = new();
    private readonly SystemState _state;
    private readonly RepositoryCoordinator _coordinator;

    public WatcherAndConfigDefaultsTests()
    {
        _watchDir = Path.Combine(_root, "watch");
        Directory.CreateDirectory(_watchDir);
        _coordinator = new RepositoryCoordinator(Path.Combine(_root, "repo.lock"));
        _state = new SystemState(_logger);
        _state.SetStateFilePath(Path.Combine(_root, "state", "state.json"));

        _config.SetupGet(c => c.WatchDirectories).Returns([new WatchDirectoryConfig { Path = _watchDir }]);
        _config.SetupGet(c => c.ExcludedPatterns).Returns([]);
        _config.SetupGet(c => c.ExcludedPaths).Returns([]);
        _config.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        _config.SetupGet(c => c.Retention).Returns(new RetentionConfig());
        _config.SetupGet(c => c.GlobalStorageType).Returns("local");
        _config.SetupGet(c => c.Encryption).Returns(new EncryptionConfig());
        _config.SetupGet(c => c.SizeThresholdMB).Returns(500);
        _config.SetupGet(c => c.BackupType).Returns(BackupType.Incremental);
        _config.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig());
        _config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(new Mock<IStorage>().Object);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static readonly BindingFlags NonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

    private static ConcurrentDictionary<string, DateTime> Pending(FileWatcher watcher)
        => (ConcurrentDictionary<string, DateTime>)typeof(FileWatcher).GetField("_changedFiles", NonPublic)!.GetValue(watcher)!;

    // Files are created BEFORE the watcher starts: a write made afterwards raises a real, asynchronous
    // FileSystemWatcher event that can land at any time and re-queue the file behind the test's back.
    // The tests drive changes only through the explicit OnChanged / ProcessBackupTimerAsync calls.
    private void CreateFiles(params string[] names)
    {
        foreach (var name in names)
        {
            File.WriteAllText(Path.Combine(_watchDir, name), name);
        }
    }

    private async Task ChangeAndFlushAsync(FileWatcher watcher, string name)
    {
        typeof(FileWatcher).GetMethod("OnChanged", NonPublic)!
            .Invoke(watcher, [this, new FileSystemEventArgs(WatcherChangeTypes.Changed, _watchDir, name)]);
        await (Task)typeof(FileWatcher).GetMethod("ProcessBackupTimerAsync", NonPublic)!.Invoke(watcher, [])!;
    }

    [Fact]
    public async Task MinSnapshotInterval_AccumulatesChangesAndFlushesOnceTheIntervalElapsed()
    {
        _config.SetupGet(c => c.Watcher).Returns(new WatcherConfig { MinSnapshotInterval = TimeSpan.FromMinutes(15) });
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        CreateFiles("one.txt", "two.txt");
        using var watcher = new FileWatcher(_config.Object, _logger, _state, new SizeAnalyzer(), coordinator: _coordinator)
        {
            UtcNow = () => now
        };
        await watcher.StartAsync();

        var first = Path.Combine(_watchDir, "one.txt");
        var second = Path.Combine(_watchDir, "two.txt");

        await ChangeAndFlushAsync(watcher, "one.txt");
        _state.HasMetadata(_watchDir, first).Should().BeTrue("the first flush has no earlier snapshot to wait for");
        _state.BackupHistory[Path.GetFullPath(_watchDir)].Should().HaveCount(1);

        now = now.AddMinutes(5);
        await ChangeAndFlushAsync(watcher, "two.txt");
        _state.BackupHistory[Path.GetFullPath(_watchDir)].Should().HaveCount(1, "5 minutes is inside the 15 minute minimum");
        Pending(watcher).Should().ContainKey(second, "deferred changes are kept, not dropped");

        now = now.AddMinutes(11);
        await (Task)typeof(FileWatcher).GetMethod("ProcessBackupTimerAsync", NonPublic)!.Invoke(watcher, [])!;
        _state.BackupHistory[Path.GetFullPath(_watchDir)].Should().HaveCount(2);
        _state.HasMetadata(_watchDir, second).Should().BeTrue();
        Pending(watcher).Should().BeEmpty();
    }

    [Fact]
    public async Task MinSnapshotInterval_DeferredChanges_AreReturnedByTakePendingChanges_AfterStop()
    {
        _config.SetupGet(c => c.Watcher).Returns(new WatcherConfig { MinSnapshotInterval = TimeSpan.FromMinutes(15) });
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        CreateFiles("one.txt", "two.txt");
        var watcher = new FileWatcher(_config.Object, _logger, _state, new SizeAnalyzer(), coordinator: _coordinator) { UtcNow = () => now };
        await watcher.StartAsync();

        await ChangeAndFlushAsync(watcher, "one.txt");
        now = now.AddMinutes(1);
        var second = Path.Combine(_watchDir, "two.txt");

        // Stop while the sweep defers: the timer must not be re-armed on a disposed timer, and nothing is lost.
        typeof(FileWatcher).GetMethod("OnChanged", NonPublic)!
            .Invoke(watcher, [this, new FileSystemEventArgs(WatcherChangeTypes.Changed, _watchDir, "two.txt")]);
        await watcher.StopAsync();
        await (Task)typeof(FileWatcher).GetMethod("ProcessBackupTimerAsync", NonPublic)!.Invoke(watcher, [])!;

        var pending = watcher.TakePendingChanges();
        pending.Keys.Should().Contain(second, "the deferred change is handed over for the next watcher");
        _logger.Messages.Should().NotContain(m => m.Contains("Unhandled error"));
        watcher.Dispose();
    }

    [Fact]
    public async Task MinSnapshotInterval_DeferralDuringASweep_DoesNotLeaveChangesInTheUnprocessedList()
    {
        _config.SetupGet(c => c.Watcher).Returns(new WatcherConfig { MinSnapshotInterval = TimeSpan.FromMinutes(15) });
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        CreateFiles("one.txt", "two.txt");
        using var watcher = new FileWatcher(_config.Object, _logger, _state, new SizeAnalyzer(), coordinator: _coordinator) { UtcNow = () => now };
        await watcher.StartAsync();

        await ChangeAndFlushAsync(watcher, "one.txt");
        now = now.AddMinutes(1);
        await ChangeAndFlushAsync(watcher, "two.txt");

        // A shutdown cancellation after the deferral would re-add "unprocessed" items; the deferred change must still be queued exactly once.
        Pending(watcher).Keys.Should().ContainSingle().Which.Should().EndWith("two.txt");
    }

    [Fact]
    public async Task MinSnapshotInterval_Zero_FlushesEveryTime()
    {
        _config.SetupGet(c => c.Watcher).Returns(new WatcherConfig { MinSnapshotInterval = TimeSpan.Zero });
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        CreateFiles("a.txt", "b.txt");
        using var watcher = new FileWatcher(_config.Object, _logger, _state, new SizeAnalyzer(), coordinator: _coordinator) { UtcNow = () => now };
        await watcher.StartAsync();

        await ChangeAndFlushAsync(watcher, "a.txt");
        await ChangeAndFlushAsync(watcher, "b.txt");

        _state.HasMetadata(_watchDir, Path.Combine(_watchDir, "b.txt")).Should().BeTrue();
    }

    private static string TemplatePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "ReStore.Core", "config", "config.example.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("config.example.json not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Template_ShipsRetentionOn_SystemBackupOff_AndNoAppData()
    {
        var root = JsonNode.Parse(File.ReadAllText(TemplatePath()))!.AsObject();

        root["retention"]!["enabled"]!.GetValue<bool>().Should().BeTrue();
        root["systemBackup"]!["enabled"]!.GetValue<bool>().Should().BeFalse();
        root["watcher"]!["minSnapshotInterval"]!.GetValue<string>().Should().Be("00:15:00");
        root["watcher"]!["maxDebounceWait"]!.GetValue<string>().Should().Be("00:05:00");
        root["watchDirectories"]!.AsArray().Select(w => w!["path"]!.GetValue<string>())
            .Should().NotContain(p => p.Contains("%APPDATA%", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Migrate_ConfigWithoutSystemBackupBlock_CreatesItDisabled()
    {
        var root = JsonNode.Parse("""{ "configSchemaVersion": 4 }""")!.AsObject();

        ConfigSchemaManager.Migrate(root);

        root["systemBackup"]!["enabled"]!.GetValue<bool>().Should().BeFalse();
        root["watcher"]!["minSnapshotInterval"]!.GetValue<string>().Should().Be("00:15:00");
    }

    [Fact]
    public void Migrate_DoesNotTouchExistingSystemBackupOrRetentionChoices()
    {
        var root = JsonNode.Parse("""
        { "configSchemaVersion": 4,
          "systemBackup": { "enabled": true },
          "retention": { "enabled": false, "keepLastPerDirectory": 3, "maxAgeDays": 5 },
          "watcher": { "minSnapshotInterval": "00:01:00" } }
        """)!.AsObject();

        ConfigSchemaManager.Migrate(root);

        root["systemBackup"]!["enabled"]!.GetValue<bool>().Should().BeTrue();
        root["retention"]!["enabled"]!.GetValue<bool>().Should().BeFalse();
        root["watcher"]!["minSnapshotInterval"]!.GetValue<string>().Should().Be("00:01:00");
    }

    [Fact]
    public void Validator_RejectsNegativeMinSnapshotInterval()
    {
        var result = new ConfigValidationResult();
        typeof(ConfigValidator).GetMethod("ValidateWatcherSettings", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [new WatcherConfig { MinSnapshotInterval = TimeSpan.FromMinutes(-1) }, result]);

        result.Errors.Should().Contain(e => e.Contains("watcher.minSnapshotInterval"));
    }

    [Fact]
    public void Validator_RejectsNegativeMaxDebounceWait_AndWarnsWhenVeryLong()
    {
        var validate = typeof(ConfigValidator).GetMethod("ValidateWatcherSettings", BindingFlags.Static | BindingFlags.NonPublic)!;

        var negative = new ConfigValidationResult();
        validate.Invoke(null, [new WatcherConfig { MaxDebounceWait = TimeSpan.FromSeconds(-1) }, negative]);
        negative.Errors.Should().Contain(e => e.Contains("watcher.maxDebounceWait"));

        var tooLong = new ConfigValidationResult();
        validate.Invoke(null, [new WatcherConfig { MaxDebounceWait = TimeSpan.FromHours(2) }, tooLong]);
        tooLong.Errors.Should().BeEmpty();
        tooLong.Warnings.Should().Contain(w => w.Contains("watcher.maxDebounceWait"));
    }

    [Fact]
    public void Migrate_AddsMaxDebounceWaitDefault_AndKeepsAnExistingValue()
    {
        var fresh = JsonNode.Parse("""{ "configSchemaVersion": 4 }""")!.AsObject();
        ConfigSchemaManager.Migrate(fresh);
        fresh["watcher"]!["maxDebounceWait"]!.GetValue<string>().Should().Be("00:05:00");

        var custom = JsonNode.Parse("""{ "configSchemaVersion": 4, "watcher": { "maxDebounceWait": "00:02:00" } }""")!.AsObject();
        ConfigSchemaManager.Migrate(custom);
        custom["watcher"]!["maxDebounceWait"]!.GetValue<string>().Should().Be("00:02:00");
    }

    [Fact]
    public async Task ConfigManager_LoadsWatcherMinSnapshotInterval_AndTemplateRetention()
    {
        var json = JsonNode.Parse(await File.ReadAllTextAsync(TemplatePath()))!.AsObject();
        json["watcher"]!["minSnapshotInterval"] = "00:07:30";
        json["watcher"]!["maxDebounceWait"] = "00:03:00";

        var manager = new ConfigManager(_logger);
        var configPath = manager.GetConfigFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath, json.ToJsonString());

        await manager.LoadAsync();

        manager.Watcher.MinSnapshotInterval.Should().Be(TimeSpan.FromMinutes(7.5));
        manager.Watcher.MaxDebounceWait.Should().Be(TimeSpan.FromMinutes(3));
        manager.Retention.Enabled.Should().BeTrue();
        manager.SystemBackup.Enabled.Should().BeFalse();
    }
}
