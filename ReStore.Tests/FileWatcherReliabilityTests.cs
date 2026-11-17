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
/// H16: the watcher must neither strand nor drop changes. Files are created before StartAsync and changes are
/// driven through explicit OnChanged / ProcessBackupTimerAsync calls, never through real filesystem events.
/// </summary>
public class FileWatcherReliabilityTests : IDisposable
{
    private static readonly BindingFlags NonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreWatcherReliability_" + Guid.NewGuid().ToString("N"));
    private readonly string _watchDir;
    private readonly TestLogger _logger = new();
    private readonly Mock<IConfigManager> _config = new();
    private readonly SystemState _state;
    private readonly RepositoryCoordinator _coordinator;
    private readonly WatcherConfig _watcherConfig = new() { MinSnapshotInterval = TimeSpan.Zero };
    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public FileWatcherReliabilityTests()
    {
        _watchDir = Path.Combine(_testRoot, "watch");
        Directory.CreateDirectory(_watchDir);
        _coordinator = new RepositoryCoordinator(Path.Combine(_testRoot, "repo.lock"));
        _state = new SystemState(_logger);
        _state.SetStateFilePath(Path.Combine(_testRoot, "state", "state.json"));

        _config.SetupGet(c => c.WatchDirectories).Returns([new WatchDirectoryConfig { Path = _watchDir }]);
        _config.SetupGet(c => c.Watcher).Returns(() => _watcherConfig);
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

    private FileWatcher CreateWatcher() => new(_config.Object, _logger, _state, new SizeAnalyzer(), coordinator: _coordinator) { UtcNow = () => _now };

    [Fact]
    public async Task PausedByAnotherProcess_KeepsChangesQueued_UntilTheResume()
    {
        var file = CreateFile("a.txt");
        var store = new ProtectionPauseStore(() => Path.Combine(_testRoot, "pause.json"));
        using var watcher = new FileWatcher(_config.Object, _logger, _state, new SizeAnalyzer(), coordinator: _coordinator, pauseStore: store) { UtcNow = () => _now };
        watcher.PausedRecheckDelay = TimeSpan.FromSeconds(45);
        await watcher.StartAsync();

        // The pause is written after the watcher started, as 'restore pause' does.
        new ProtectionPauseStore(() => Path.Combine(_testRoot, "pause.json")).Save(ProtectionPause.Indefinite);
        Changed(watcher, "a.txt");
        await Process(watcher);

        Pending(watcher).Should().ContainKey(file, "nothing is backed up while paused, and nothing is lost");
        watcher.LastArmedDelay.Should().Be(TimeSpan.FromSeconds(45));
        _state.HasMetadata(_watchDir, file).Should().BeFalse();

        store.Clear();
        await Process(watcher);

        Pending(watcher).Should().BeEmpty();
        _state.HasMetadata(_watchDir, file).Should().BeTrue("the first sweep after the resume backs the change up");
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_watchDir, name);
        File.WriteAllText(path, name);
        return path;
    }

    private void Changed(FileWatcher watcher, string name, WatcherChangeTypes type = WatcherChangeTypes.Changed)
        => typeof(FileWatcher).GetMethod("OnChanged", NonPublic)!.Invoke(watcher, [this, new FileSystemEventArgs(type, _watchDir, name)]);

    private static Task Process(FileWatcher watcher)
        => (Task)typeof(FileWatcher).GetMethod("ProcessBackupTimerAsync", NonPublic)!.Invoke(watcher, [])!;

    private static ConcurrentDictionary<string, DateTime> Pending(FileWatcher watcher)
        => (ConcurrentDictionary<string, DateTime>)typeof(FileWatcher).GetField("_changedFiles", NonPublic)!.GetValue(watcher)!;

    private static ConcurrentDictionary<string, byte> FullRoots(FileWatcher watcher)
        => (ConcurrentDictionary<string, byte>)typeof(FileWatcher).GetField("_fullBackupRoots", NonPublic)!.GetValue(watcher)!;

    [Fact]
    public async Task FailedBatch_IsRequeued_AndRetriedWithBackoff()
    {
        var file = CreateFile("a.txt");
        using var watcher = CreateWatcher();
        watcher.FailureRetryDelay = TimeSpan.FromMinutes(2);
        await watcher.StartAsync();

        _config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("provider unavailable"));
        Changed(watcher, "a.txt");
        await Process(watcher);

        Pending(watcher).Should().ContainKey(file, "a failed batch must be put back, not dropped");
        watcher.LastArmedDelay.Should().Be(TimeSpan.FromMinutes(2));

        await Process(watcher);
        watcher.LastArmedDelay.Should().Be(TimeSpan.FromMinutes(4), "consecutive failures back off");

        _config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(new Mock<IStorage>().Object);
        await Process(watcher);

        Pending(watcher).Should().BeEmpty();
        _state.HasMetadata(_watchDir, file).Should().BeTrue();
    }

    [Fact]
    public async Task SkippedOverlappingTick_ReArmsTheTimer()
    {
        CreateFile("a.txt");
        using var watcher = CreateWatcher();
        await watcher.StartAsync();

        var gate = (SemaphoreSlim)typeof(FileWatcher).GetField("_backupExecutionLock", NonPublic)!.GetValue(watcher)!;
        await gate.WaitAsync();
        try
        {
            watcher.LastArmedDelay.Should().BeNull();
            await Process(watcher);
            watcher.LastArmedDelay.Should().Be(TimeSpan.FromSeconds(10), "the skipped tick must not strand the pending changes");
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task ChangeArrivingDuringAFailingSweep_IsNotDelayedByTheRetryBackoff()
    {
        CreateFile("a.txt");
        CreateFile("b.txt");
        using var watcher = CreateWatcher();
        watcher.FailureRetryDelay = TimeSpan.FromMinutes(10);
        await watcher.StartAsync();
        Changed(watcher, "a.txt");

        // While the sweep is running (inside its storage call) another change arrives, then the sweep fails.
        _config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).Returns(() =>
        {
            Changed(watcher, "b.txt");
            throw new InvalidOperationException("provider unavailable");
        });
        await Process(watcher);

        Pending(watcher).Keys.Should().HaveCount(2);
        watcher.LastArmedDelay.Should().Be(TimeSpan.FromMinutes(10), "a change arriving during a failing sweep must not turn the retry backoff into a 10 s loop");
    }

    [Fact]
    public async Task NewEvents_AfterAFailure_WaitOutTheBackoffFloor()
    {
        CreateFile("a.txt");
        using var watcher = CreateWatcher();
        watcher.FailureRetryDelay = TimeSpan.FromMinutes(10);
        _watcherConfig.MaxDebounceWait = TimeSpan.Zero;
        await watcher.StartAsync();
        Changed(watcher, "a.txt");
        _config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("provider unavailable"));
        await Process(watcher);

        _now = _now.AddMinutes(4);
        Changed(watcher, "b.txt");
        watcher.LastArmedDelay.Should().Be(TimeSpan.FromMinutes(6), "not before the end of the backoff");

        _now = _now.AddMinutes(7);
        Changed(watcher, "c.txt");
        watcher.LastArmedDelay.Should().Be(TimeSpan.FromSeconds(10), "the floor has passed");
    }

    [Fact]
    public async Task DirectoryCreated_SchedulesAFullBackupOfItsRoot()
    {
        var file = CreateFile("a.txt");
        var moved = Path.Combine(_watchDir, "moved-in");
        Directory.CreateDirectory(moved);
        File.WriteAllText(Path.Combine(moved, "inner.txt"), "inner");
        using var watcher = CreateWatcher();
        await watcher.StartAsync();

        Changed(watcher, "moved-in", WatcherChangeTypes.Created);

        Pending(watcher).Should().BeEmpty();
        FullRoots(watcher).Should().ContainSingle();
        await Process(watcher);
        _state.HasMetadata(_watchDir, file).Should().BeTrue();
        _state.HasMetadata(_watchDir, Path.Combine(moved, "inner.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task CancellationNotCausedByShutdown_RequeuesAndReArms()
    {
        var file = CreateFile("a.txt");
        using var watcher = CreateWatcher();
        watcher.FailureRetryDelay = TimeSpan.FromMinutes(3);
        await watcher.StartAsync();
        Changed(watcher, "a.txt");
        _config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ThrowsAsync(new OperationCanceledException("storage timeout"));

        await Process(watcher);

        Pending(watcher).Should().ContainKey(file);
        watcher.LastArmedDelay.Should().Be(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public async Task ChangeOutsideEveryRoot_IsLoggedNotSilentlySkipped()
    {
        using var watcher = CreateWatcher();
        await watcher.StartAsync();
        Pending(watcher)[Path.Combine(_testRoot, "elsewhere", "x.txt")] = DateTime.UtcNow;

        await Process(watcher);

        _logger.Messages.Should().Contain(m => m.Contains("outside every watched folder"));
    }

    [Fact]
    public async Task UnexpectedErrorWhileBuildingBatches_DoesNotDropTheDrainedChanges()
    {
        var file = CreateFile("a.txt");
        using var watcher = CreateWatcher();
        await watcher.StartAsync();
        Changed(watcher, "a.txt");
        var bad = "bad\0path";
        Pending(watcher)[bad] = DateTime.UtcNow;

        await Process(watcher);

        _logger.Messages.Should().Contain(m => m.Contains("Unhandled error"), "a path with a NUL character makes root resolution throw");
        Pending(watcher).Should().ContainKeys(file, bad);
        watcher.LastArmedDelay.Should().NotBeNull();
    }

    [Fact]
    public async Task AdoptingARootThatIsNoLongerWatched_IsSkippedAndLogged()
    {
        var other = Path.Combine(_testRoot, "other");
        Directory.CreateDirectory(other);
        using var watcher = CreateWatcher();
        await watcher.StartAsync();

        watcher.AdoptPendingChanges(new Dictionary<string, string?> { [other] = null });

        Pending(watcher).Should().BeEmpty();
        FullRoots(watcher).Should().BeEmpty();
        _logger.Messages.Should().Contain(m => m.Contains("no longer watched"));
    }

    [Fact]
    public async Task StartAsync_UsesA64KbInternalBuffer()
    {
        using var watcher = CreateWatcher();
        await watcher.StartAsync();

        var watchers = (List<FileSystemWatcher>)typeof(FileWatcher).GetField("_watchers", NonPublic)!.GetValue(watcher)!;
        watchers.Should().ContainSingle().Which.InternalBufferSize.Should().Be(64 * 1024);
    }

    [Fact]
    public async Task WatcherError_SchedulesAFullBackupOfThatRoot()
    {
        var a = CreateFile("a.txt");
        var b = CreateFile("b.txt");
        using var watcher = CreateWatcher();
        await watcher.StartAsync();

        var source = ((List<FileSystemWatcher>)typeof(FileWatcher).GetField("_watchers", NonPublic)!.GetValue(watcher)!).Single();
        typeof(FileWatcher).GetMethod("OnError", NonPublic)!
            .Invoke(watcher, [source, new ErrorEventArgs(new InternalBufferOverflowException("overflow"))]);

        Pending(watcher).Should().BeEmpty("no individual change is known after an overflow");
        FullRoots(watcher).Should().ContainSingle();
        watcher.LastArmedDelay.Should().Be(TimeSpan.FromSeconds(10));

        await Process(watcher);

        FullRoots(watcher).Should().BeEmpty();
        _state.HasMetadata(_watchDir, a).Should().BeTrue();
        _state.HasMetadata(_watchDir, b).Should().BeTrue();
    }

    [Fact]
    public async Task WatcherError_FromAnUnknownSender_ScheduleAFullBackupOfEveryRoot()
    {
        using var watcher = CreateWatcher();
        await watcher.StartAsync();

        typeof(FileWatcher).GetMethod("OnError", NonPublic)!.Invoke(watcher, [this, new ErrorEventArgs(new IOException("boom"))]);

        FullRoots(watcher).Should().ContainSingle();
    }

    [Fact]
    public async Task PendingFullBackup_IsCarriedToTheNextWatcher_AndStillRuns()
    {
        var a = CreateFile("a.txt");
        using var first = CreateWatcher();
        await first.StartAsync();
        typeof(FileWatcher).GetMethod("OnError", NonPublic)!.Invoke(first, [this, new ErrorEventArgs(new IOException("boom"))]);
        await first.StopAsync();

        var carried = first.TakePendingChanges();
        carried.Should().ContainSingle();

        using var second = CreateWatcher();
        await second.StartAsync();
        second.AdoptPendingChanges(carried);
        FullRoots(second).Should().ContainSingle();
        Pending(second).Should().BeEmpty("the root is a full-backup request, not a changed file");

        await Process(second);
        _state.HasMetadata(_watchDir, a).Should().BeTrue();
    }

    [Fact]
    public async Task FailedFullBackup_KeepsTheRootQueued()
    {
        CreateFile("a.txt");
        using var watcher = CreateWatcher();
        await watcher.StartAsync();
        typeof(FileWatcher).GetMethod("OnError", NonPublic)!.Invoke(watcher, [this, new ErrorEventArgs(new IOException("boom"))]);

        _config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("provider unavailable"));
        await Process(watcher);

        FullRoots(watcher).Should().ContainSingle("the full backup is retried, not forgotten");
    }

    [Fact]
    public async Task Debounce_IsCappedByMaxDebounceWait()
    {
        _watcherConfig.MaxDebounceWait = TimeSpan.FromMinutes(1);
        using var watcher = CreateWatcher();
        await watcher.StartAsync();

        Changed(watcher, "x1.txt");
        watcher.LastArmedDelay.Should().Be(TimeSpan.FromSeconds(10));

        _now = _now.AddSeconds(55);
        Changed(watcher, "x2.txt");
        watcher.LastArmedDelay.Should().Be(TimeSpan.FromSeconds(5), "only 5 s of the 1 minute cap remain");

        _now = _now.AddSeconds(30);
        Changed(watcher, "x3.txt");
        watcher.LastArmedDelay.Should().Be(TimeSpan.Zero, "the cap has passed, so the next sweep is immediate");
    }

    [Fact]
    public async Task Debounce_StartsAFreshWindow_AfterASweep()
    {
        _watcherConfig.MaxDebounceWait = TimeSpan.FromMinutes(1);
        using var watcher = CreateWatcher();
        await watcher.StartAsync();

        Changed(watcher, "x1.txt");
        _now = _now.AddMinutes(5);
        await Process(watcher);

        Changed(watcher, "x2.txt");
        watcher.LastArmedDelay.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Debounce_HasNoCap_WhenMaxDebounceWaitIsZero()
    {
        _watcherConfig.MaxDebounceWait = TimeSpan.Zero;
        using var watcher = CreateWatcher();
        await watcher.StartAsync();

        Changed(watcher, "x1.txt");
        _now = _now.AddHours(1);
        Changed(watcher, "x2.txt");

        watcher.LastArmedDelay.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task DirectoryOnlyEvents_AreIgnored()
    {
        var sub = Path.Combine(_watchDir, "sub");
        Directory.CreateDirectory(sub);
        CreateFile("a.txt");
        using var watcher = CreateWatcher();
        await watcher.StartAsync();

        Changed(watcher, "sub", WatcherChangeTypes.Changed);

        Pending(watcher).Should().BeEmpty();
        FullRoots(watcher).Should().BeEmpty();
        watcher.LastArmedDelay.Should().BeNull();

        Changed(watcher, "a.txt");
        Pending(watcher).Should().ContainSingle();
    }

    [Fact]
    public async Task DirectoryRename_SchedulesAFullBackupInsteadOfTrackingTheDirectory()
    {
        var renamed = Path.Combine(_watchDir, "renamed");
        Directory.CreateDirectory(renamed);
        using var watcher = CreateWatcher();
        await watcher.StartAsync();

        typeof(FileWatcher).GetMethod("OnRenamed", NonPublic)!.Invoke(watcher,
            [this, new RenamedEventArgs(WatcherChangeTypes.Renamed, _watchDir, "renamed", "old")]);

        Pending(watcher).Should().BeEmpty();
        FullRoots(watcher).Should().ContainSingle();
    }
}
