using FluentAssertions;
using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;
using System.Collections.Concurrent;
using System.Reflection;

namespace ReStore.Tests;

public class FileWatcherTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _watchDir;
    private readonly TestLogger _logger;
    private readonly Mock<IConfigManager> _configMock;
    private readonly SystemState _state;
    private readonly SizeAnalyzer _sizeAnalyzer;

    // Own coordinator: the watcher backs off when the repository is busy, so sharing the process-wide
    // singleton would make these tests depend on whatever else runs in parallel.
    private readonly RepositoryCoordinator _coordinator = new(
        Path.Combine(TestEnvironment.RootDirectory, "lock_" + Guid.NewGuid().ToString("N") + ".lock"));

    public FileWatcherTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreFileWatcherTests_" + Guid.NewGuid());
        _watchDir = Path.Combine(_testRoot, "watch");
        Directory.CreateDirectory(_watchDir);

        _logger = new TestLogger();
        _configMock = new Mock<IConfigManager>();
        _state = new SystemState(_logger);
        _sizeAnalyzer = new SizeAnalyzer();

        _configMock.SetupGet(c => c.WatchDirectories).Returns([
            new WatchDirectoryConfig { Path = _watchDir, StorageType = null }
        ]);
        _configMock.SetupGet(c => c.ExcludedPatterns).Returns([]);
        _configMock.SetupGet(c => c.ExcludedPaths).Returns([]);
        _configMock.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        _configMock.SetupGet(c => c.Retention).Returns(new RetentionConfig());
        _configMock.SetupGet(c => c.GlobalStorageType).Returns("local");
        _configMock.SetupGet(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        _configMock.SetupGet(c => c.SizeThresholdMB).Returns(500);
        _configMock.SetupGet(c => c.BackupType).Returns(BackupType.Incremental);
        _configMock.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig());
        _configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(new Mock<IStorage>().Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    [Fact]
    public async Task StartAsync_ShouldLogWarning_WhenWatchDirectoryMissing()
    {
        var missing = Path.Combine(_testRoot, "missing-dir");
        _configMock.SetupGet(c => c.WatchDirectories).Returns([
            new WatchDirectoryConfig { Path = missing, StorageType = null }
        ]);

        using var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer, coordinator: _coordinator);
        await watcher.StartAsync();

        _logger.Messages.Should().Contain(m => m.Contains("Directory not found, cannot watch"));
    }

    [Fact]
    public async Task StartAsync_ShouldLogWatchingDirectory_WhenDirectoryExists()
    {
        using var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer, coordinator: _coordinator);
        await watcher.StartAsync();

        _logger.Messages.Should().Contain(m => m.Contains($"Watching directory: {_watchDir}"));
    }

    [Fact]
    public async Task OnChanged_ShouldTrackFile_WhenNotExcluded()
    {
        using var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer, coordinator: _coordinator);
        await watcher.StartAsync();

        var method = typeof(FileWatcher).GetMethod("OnChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var changedFile = Path.Combine(_watchDir, "file.txt");
        var args = new FileSystemEventArgs(WatcherChangeTypes.Changed, _watchDir, "file.txt");
        method!.Invoke(watcher, [this, args]);

        var changed = GetChangedFiles(watcher);
        changed.ContainsKey(changedFile).Should().BeTrue();
    }

    [Fact]
    public async Task OnChanged_ShouldIgnoreFile_WhenPatternExcluded()
    {
        _configMock.SetupGet(c => c.ExcludedPatterns).Returns(["*.tmp"]);

        using var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer, coordinator: _coordinator);
        await watcher.StartAsync();

        var method = typeof(FileWatcher).GetMethod("OnChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        var excludedFile = Path.Combine(_watchDir, "skip.tmp");
        var args = new FileSystemEventArgs(WatcherChangeTypes.Changed, _watchDir, "skip.tmp");
        method!.Invoke(watcher, [this, args]);

        var changed = GetChangedFiles(watcher);
        changed.ContainsKey(excludedFile).Should().BeFalse();
    }

    [Fact]
    public async Task OnRenamed_ShouldTrackNewFilePath_WhenNotExcluded()
    {
        using var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer, coordinator: _coordinator);
        await watcher.StartAsync();

        var method = typeof(FileWatcher).GetMethod("OnRenamed", BindingFlags.Instance | BindingFlags.NonPublic);
        var renamedFile = Path.Combine(_watchDir, "renamed.txt");
        var args = new RenamedEventArgs(WatcherChangeTypes.Renamed, _watchDir, "renamed.txt", "old.txt");
        method!.Invoke(watcher, [this, args]);

        var changed = GetChangedFiles(watcher);
        changed.ContainsKey(renamedFile).Should().BeTrue();
    }

    [Fact]
    public async Task ProcessBackupTimerAsync_ShouldRemoveOldMetadata_ForRenamedFiles()
    {
        var storageMock = new Mock<IStorage>();
        _configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(storageMock.Object);

        var oldPath = Path.Combine(_watchDir, "old-name.txt");
        var newPath = Path.Combine(_watchDir, "new-name.txt");
        await File.WriteAllTextAsync(oldPath, "content");
        await _state.SeedFileMetadataAsync(_watchDir, "local", oldPath);
        File.Move(oldPath, newPath);

        using var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer, coordinator: _coordinator);
        await watcher.StartAsync();

        var renamedMethod = typeof(FileWatcher).GetMethod("OnRenamed", BindingFlags.Instance | BindingFlags.NonPublic);
        renamedMethod!.Invoke(watcher, [this, new RenamedEventArgs(WatcherChangeTypes.Renamed, _watchDir, "new-name.txt", "old-name.txt")]);

        var processMethod = typeof(FileWatcher).GetMethod("ProcessBackupTimerAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        var task = (Task)processMethod!.Invoke(watcher, [])!;
        await task;

        _state.HasMetadata(_watchDir, oldPath).Should().BeFalse();
        _state.HasMetadata(_watchDir, newPath).Should().BeTrue();
    }

    [Fact]
    public async Task ProcessBackupTimerAsync_ShouldRequeueChanges_WhenRepositoryIsBusy()
    {
        var file = Path.Combine(_watchDir, "busy.txt");
        await File.WriteAllTextAsync(file, "content");

        using var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer, coordinator: _coordinator);
        watcher.BusyRetryDelay = TimeSpan.FromHours(1);
        await watcher.StartAsync();

        var onChanged = typeof(FileWatcher).GetMethod("OnChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        onChanged!.Invoke(watcher, [this, new FileSystemEventArgs(WatcherChangeTypes.Changed, _watchDir, "busy.txt")]);

        var process = typeof(FileWatcher).GetMethod("ProcessBackupTimerAsync", BindingFlags.Instance | BindingFlags.NonPublic);

        await using (await _coordinator.AcquireMutationAsync("another operation"))
        {
            await (Task)process!.Invoke(watcher, [])!;
            GetChangedFiles(watcher).Should().ContainKey(file, "the change is put back, not dropped, when the repository is busy");
        }

        await (Task)process!.Invoke(watcher, [])!;
        GetChangedFiles(watcher).Should().BeEmpty("once the repository is free the change is backed up");
    }

    [Fact]
    public async Task FindWatchedRoot_ShouldReturnMostSpecificRoot()
    {
        var parent = Path.Combine(_testRoot, "parent");
        var child = Path.Combine(parent, "child");
        Directory.CreateDirectory(child);

        _configMock.SetupGet(c => c.WatchDirectories).Returns([
            new WatchDirectoryConfig { Path = parent, StorageType = null },
            new WatchDirectoryConfig { Path = child, StorageType = null }
        ]);

        using var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer, coordinator: _coordinator);
        await watcher.StartAsync();

        var method = typeof(FileWatcher).GetMethod("FindWatchedRoot", BindingFlags.Instance | BindingFlags.NonPublic);
        var fileInChild = Path.Combine(child, "nested", "file.txt");
        var root = (string?)method!.Invoke(watcher, [fileInChild]);

        root.Should().Be(Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    [Fact]
    public async Task FindWatchedRoot_ShouldReturnNull_WhenPathNotUnderWatchedRoots()
    {
        using var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer, coordinator: _coordinator);
        await watcher.StartAsync();

        var method = typeof(FileWatcher).GetMethod("FindWatchedRoot", BindingFlags.Instance | BindingFlags.NonPublic);
        var externalPath = Path.Combine(_testRoot, "external", "file.txt");
        var root = (string?)method!.Invoke(watcher, [externalPath]);

        root.Should().BeNull();
    }

    [Fact]
    public async Task OnError_ShouldLogWatcherException()
    {
        using var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer, coordinator: _coordinator);
        await watcher.StartAsync();

        var method = typeof(FileWatcher).GetMethod("OnError", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var exception = new IOException("watcher boom");
        var args = new ErrorEventArgs(exception);
        method!.Invoke(watcher, [this, args]);

        _logger.Messages.Should().Contain(m => m.Contains("File watcher error") && m.Contains("watcher boom"));
    }

    [Fact]
    public async Task Dispose_ShouldBeIdempotent()
    {
        var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer, coordinator: _coordinator);
        await watcher.StartAsync();

        watcher.Dispose();
        var action = () => watcher.Dispose();

        action.Should().NotThrow();
    }

    [Fact]
    public async Task StopAsync_WaitsForTheInFlightBackup_ThenKeepsUnbackedChangesForCarryOver()
    {
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        _configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).Returns(async () =>
        {
            entered.TrySetResult();
            await release.Task;
            return new Mock<IStorage>().Object;
        });

        await File.WriteAllTextAsync(Path.Combine(_watchDir, "a.txt"), "content");
        var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer, coordinator: _coordinator);
        await watcher.StartAsync();

        var onChanged = typeof(FileWatcher).GetMethod("OnChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        onChanged!.Invoke(watcher, [this, new FileSystemEventArgs(WatcherChangeTypes.Changed, _watchDir, "a.txt")]);
        var process = typeof(FileWatcher).GetMethod("ProcessBackupTimerAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        var sweep = (Task)process!.Invoke(watcher, [])!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var stop = watcher.StopAsync();
        await Task.Delay(200);
        stop.IsCompleted.Should().BeFalse("stop must wait for the in-flight backup to unwind");

        release.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        await sweep;

        // A change that arrives after stop began is ignored, and what was queued can be handed on.
        var pending = watcher.TakePendingChanges();
        pending.Should().NotBeNull();
        watcher.Dispose();
    }

    [Fact]
    public async Task PendingChanges_CanBeCarriedToAnotherWatcher()
    {
        var file = Path.Combine(_watchDir, "carry.txt");
        var old = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer, coordinator: _coordinator);
        await old.StartAsync();
        var onChanged = typeof(FileWatcher).GetMethod("OnChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        onChanged!.Invoke(old, [this, new FileSystemEventArgs(WatcherChangeTypes.Changed, _watchDir, "carry.txt")]);

        await old.StopAsync();
        var pending = old.TakePendingChanges();
        old.Dispose();
        pending.Should().ContainKey(file);

        using var next = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer, coordinator: _coordinator);
        await next.StartAsync();
        next.AdoptPendingChanges(pending);

        GetChangedFiles(next).Should().ContainKey(file);
    }

    [Fact]
    public async Task OnChanged_ShouldDoNothing_AfterDispose()
    {
        var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer, coordinator: _coordinator);
        await watcher.StartAsync();
        watcher.Dispose();

        var method = typeof(FileWatcher).GetMethod("OnChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        var args = new FileSystemEventArgs(WatcherChangeTypes.Changed, _watchDir, "after-dispose.txt");
        method!.Invoke(watcher, [this, args]);

        var changed = GetChangedFiles(watcher);
        changed.Count.Should().Be(0);
    }

    [Fact]
    public async Task ProcessBackupTimerAsync_ShouldStopPromptly_WhenDisposedMidFlight()
    {
        var storageMock = new Mock<IStorage>();
        _configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(storageMock.Object);

        await File.WriteAllTextAsync(Path.Combine(_watchDir, "a.txt"), "content");

        var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer, coordinator: _coordinator);
        await watcher.StartAsync();

        var changedMethod = typeof(FileWatcher).GetMethod("OnChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        changedMethod!.Invoke(watcher, [this, new FileSystemEventArgs(WatcherChangeTypes.Changed, _watchDir, "a.txt")]);

        // Dispose before the sweep runs: the token is already cancelled, so the sweep must
        // bail out rather than back up against a torn-down watcher.
        watcher.Dispose();

        var processMethod = typeof(FileWatcher).GetMethod("ProcessBackupTimerAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        var task = (Task)processMethod!.Invoke(watcher, [])!;

        var act = async () => await task;
        await act.Should().NotThrowAsync("shutdown cancellation is expected, not an error");

        storageMock.Verify(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Dispose_ShouldNotThrow_WhenCalledWhileSweepHoldsTheLock()
    {
        var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer, coordinator: _coordinator);
        await watcher.StartAsync();

        // The execution lock is deliberately not disposed, because an in-flight sweep still
        // releases it in its finally block.
        var lockField = typeof(FileWatcher).GetField("_backupExecutionLock", BindingFlags.Instance | BindingFlags.NonPublic);
        var executionLock = (SemaphoreSlim)lockField!.GetValue(watcher)!;
        await executionLock.WaitAsync();

        watcher.Dispose();

        var release = () => executionLock.Release();
        release.Should().NotThrow("disposing the watcher must not dispose a semaphore a sweep still holds");
    }

    private static ConcurrentDictionary<string, DateTime> GetChangedFiles(FileWatcher watcher)
    {
        var field = typeof(FileWatcher).GetField("_changedFiles", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();
        return (ConcurrentDictionary<string, DateTime>)field!.GetValue(watcher)!;
    }
}
