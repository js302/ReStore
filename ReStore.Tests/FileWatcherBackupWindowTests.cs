using System.Collections.Concurrent;
using System.Reflection;
using FluentAssertions;
using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// The watcher honours the backup window: outside it changes stay queued (never dropped) and are backed up once it opens.
/// Driven like <see cref="FileWatcherReliabilityTests"/>: explicit OnChanged / ProcessBackupTimerAsync calls and a fake clock.
/// </summary>
public class FileWatcherBackupWindowTests : IDisposable
{
    private static readonly BindingFlags NonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreWatcherWindow_" + Guid.NewGuid().ToString("N"));
    private readonly string _watchDir;
    private readonly TestLogger _logger = new();
    private readonly Mock<IConfigManager> _config = new();
    private readonly SystemState _state;
    private readonly RepositoryCoordinator _coordinator;
    private readonly WatcherConfig _watcherConfig = new() { MinSnapshotInterval = TimeSpan.Zero };
    private BackupWindowConfig? _window = new() { Enabled = true, Start = "22:00", End = "06:00", Days = "all" };
    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public FileWatcherBackupWindowTests()
    {
        _watchDir = Path.Combine(_testRoot, "watch");
        Directory.CreateDirectory(_watchDir);
        _coordinator = new RepositoryCoordinator(Path.Combine(_testRoot, "repo.lock"));
        _state = new SystemState(_logger);
        _state.SetStateFilePath(Path.Combine(_testRoot, "state", "state.json"));

        _config.SetupGet(c => c.WatchDirectories).Returns([new WatchDirectoryConfig { Path = _watchDir }]);
        _config.SetupGet(c => c.Watcher).Returns(() => _watcherConfig);
        _config.SetupGet(c => c.BackupWindow).Returns(() => _window);
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
        try { Directory.Delete(_testRoot, true); } catch { }
    }

    private FileWatcher CreateWatcher(ProtectionPauseStore? pauseStore = null) => new(
        _config.Object, _logger, _state, new SizeAnalyzer(), coordinator: _coordinator, pauseStore: pauseStore)
    { UtcNow = () => _now, TimeZone = TimeZoneInfo.Utc };

    private string CreateFile(string name)
    {
        var path = Path.Combine(_watchDir, name);
        File.WriteAllText(path, name);
        return path;
    }

    private void Changed(FileWatcher watcher, string name)
        => typeof(FileWatcher).GetMethod("OnChanged", NonPublic)!.Invoke(watcher, [this, new FileSystemEventArgs(WatcherChangeTypes.Changed, _watchDir, name)]);

    private static Task Process(FileWatcher watcher)
        => (Task)typeof(FileWatcher).GetMethod("ProcessBackupTimerAsync", NonPublic)!.Invoke(watcher, [])!;

    private static ConcurrentDictionary<string, DateTime> Pending(FileWatcher watcher)
        => (ConcurrentDictionary<string, DateTime>)typeof(FileWatcher).GetField("_changedFiles", NonPublic)!.GetValue(watcher)!;

    [Fact]
    public async Task OutsideTheWindow_ChangesStayQueued_AndAreBackedUpWhenItOpens()
    {
        var file = CreateFile("a.txt");
        using var watcher = CreateWatcher();
        await watcher.StartAsync();

        Changed(watcher, "a.txt");
        await Process(watcher);

        Pending(watcher).Should().ContainKey(file, "nothing is backed up outside the window, and nothing is lost");
        _state.HasMetadata(_watchDir, file).Should().BeFalse();

        _now = new DateTime(2026, 1, 1, 21, 59, 59, DateTimeKind.Utc);
        await Process(watcher);
        Pending(watcher).Should().ContainKey(file, "one second before the window");

        _now = new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc);
        await Process(watcher);

        Pending(watcher).Should().BeEmpty();
        _state.HasMetadata(_watchDir, file).Should().BeTrue("the first sweep inside the window backs the change up");
    }

    [Fact]
    public async Task WhileClosed_TheTimerWaitsForTheWindow_ButNoLongerThanTheRecheckDelay()
    {
        CreateFile("a.txt");
        using var watcher = CreateWatcher();
        await watcher.StartAsync();
        Changed(watcher, "a.txt");

        await Process(watcher);
        watcher.LastArmedDelay.Should().Be(TimeSpan.FromMinutes(5), "the window opens in ten hours; look again after the recheck delay");

        watcher.WindowRecheckDelay = TimeSpan.FromSeconds(45);
        await Process(watcher);
        watcher.LastArmedDelay.Should().Be(TimeSpan.FromSeconds(45));

        _now = new DateTime(2026, 1, 1, 21, 58, 0, DateTimeKind.Utc);
        watcher.WindowRecheckDelay = TimeSpan.FromMinutes(5);
        await Process(watcher);
        watcher.LastArmedDelay.Should().Be(TimeSpan.FromMinutes(2), "the window opens in two minutes");
    }

    [Fact]
    public async Task NewChangesOutsideTheWindow_DoNotShortenTheWait()
    {
        CreateFile("a.txt");
        CreateFile("b.txt");
        using var watcher = CreateWatcher();
        await watcher.StartAsync();
        Changed(watcher, "a.txt");
        await Process(watcher);
        var wait = watcher.LastArmedDelay;

        Changed(watcher, "b.txt");

        watcher.LastArmedDelay.Should().Be(wait, "a burst of events must not turn the wait into a ten second retry loop");
        Pending(watcher).Should().HaveCount(2);
    }

    [Fact]
    public async Task WithoutAWindow_OrWithItDisabled_ChangesAreBackedUpAsBefore()
    {
        var file = CreateFile("a.txt");
        _window = null;
        using var watcher = CreateWatcher();
        await watcher.StartAsync();
        Changed(watcher, "a.txt");
        await Process(watcher);
        _state.HasMetadata(_watchDir, file).Should().BeTrue();

        var other = CreateFile("b.txt");
        _window = new BackupWindowConfig { Enabled = false, Start = "22:00", End = "06:00" };
        Changed(watcher, "b.txt");
        await Process(watcher);
        _state.HasMetadata(_watchDir, other).Should().BeTrue("a disabled window is ignored");
    }

    [Fact]
    public async Task AMalformedWindow_DoesNotBlockTheWatcher()
    {
        var file = CreateFile("a.txt");
        _window = new BackupWindowConfig { Enabled = true, Start = "nonsense", End = "06:00" };
        using var watcher = CreateWatcher();
        await watcher.StartAsync();
        Changed(watcher, "a.txt");

        await Process(watcher);

        _state.HasMetadata(_watchDir, file).Should().BeTrue();
        _logger.Messages.Should().Contain(m => m.Contains("Ignoring the invalid backupWindow"));
    }

    [Fact]
    public async Task ChangesQueuedOutsideTheWindow_SurviveAStop_ForTheNextWatcher()
    {
        var file = CreateFile("a.txt");
        using var watcher = CreateWatcher();
        await watcher.StartAsync();
        Changed(watcher, "a.txt");
        await Process(watcher);

        await watcher.StopAsync();
        var carried = watcher.TakePendingChanges();

        carried.Should().ContainKey(file);
    }

    [Fact]
    public async Task APauseIsStillChecked_BeforeTheWindow()
    {
        CreateFile("a.txt");
        var store = new ProtectionPauseStore(() => Path.Combine(_testRoot, "pause.json"));
        store.Save(ProtectionPause.Indefinite);
        using var watcher = CreateWatcher(store);
        watcher.PausedRecheckDelay = TimeSpan.FromSeconds(30);
        await watcher.StartAsync();
        Changed(watcher, "a.txt");

        await Process(watcher);

        watcher.LastArmedDelay.Should().Be(TimeSpan.FromSeconds(30), "the pause recheck delay, not the window's");
    }
}
