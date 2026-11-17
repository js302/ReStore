using FluentAssertions;
using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

public sealed class ProtectionWorkersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreWorkers_" + Guid.NewGuid().ToString("N"));
    private readonly string _watchDir;
    private readonly TestLogger _logger = new();
    private readonly Mock<IConfigManager> _config = new();
    private readonly SystemState _state;
    private readonly RepositoryCoordinator _coordinator;

    public ProtectionWorkersTests()
    {
        _watchDir = Path.Combine(_root, "watch");
        Directory.CreateDirectory(_watchDir);
        _state = new SystemState(_logger);
        _state.SetStateFilePath(Path.Combine(_root, "state.json"));
        _coordinator = new RepositoryCoordinator(Path.Combine(_root, "repo.lock"));

        _config.SetupGet(c => c.WatchDirectories).Returns([new WatchDirectoryConfig { Path = _watchDir, StorageType = null }]);
        _config.SetupGet(c => c.BackupInterval).Returns(TimeSpan.Zero);
        _config.SetupGet(c => c.ExcludedPatterns).Returns([]);
        _config.SetupGet(c => c.ExcludedPaths).Returns([]);
        _config.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        _config.SetupGet(c => c.SizeThresholdMB).Returns(500);
        _config.SetupGet(c => c.GlobalStorageType).Returns("local");
        _config.SetupGet(c => c.BackupType).Returns(BackupType.Incremental);
        _config.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig());
        _config.SetupGet(c => c.Retention).Returns(new RetentionConfig());
        _config.SetupGet(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        _config.SetupGet(c => c.SystemBackup).Returns(new SystemBackupConfig { Enabled = false });
        _config.SetupGet(c => c.Verification).Returns(new VerificationConfig { Enabled = false });
        _config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(new Mock<IStorage>().Object);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private FileWatcher NewWatcher() => new(_config.Object, _logger, _state, new SizeAnalyzer(), coordinator: _coordinator);

    private BackupScheduler NewScheduler() => new(_config.Object, _logger, _state, new SizeAnalyzer(), coordinator: _coordinator);

    [Fact]
    public async Task FileWatcherWorker_StartWatchesTheConfiguredDirectory()
    {
        IProtectionWorker worker = new FileWatcherWorker(NewWatcher());

        await worker.StartAsync();
        await worker.StopAsync();

        _logger.Messages.Should().Contain(m => m.Contains(_watchDir));
    }

    [Fact]
    public async Task FileWatcherWorker_HasNoPendingChangesBeforeItIsStopped_AndNoScheduleOfItsOwn()
    {
        IProtectionWorker worker = new FileWatcherWorker(NewWatcher());

        worker.TakePendingChanges().Should().BeNull();
        worker.NextRunUtc.Should().BeNull();
        await worker.StopAsync();
    }

    [Fact]
    public async Task FileWatcherWorker_HandsOverAdoptedChangesAfterStop()
    {
        // The watcher is not started, so nothing is backed up: whatever it was given is still pending when it stops.
        var file = Path.Combine(_watchDir, "changed.txt");
        await File.WriteAllTextAsync(file, "x");
        IProtectionWorker worker = new FileWatcherWorker(NewWatcher());
        worker.AdoptPendingChanges(new Dictionary<string, string?> { [file] = null });

        await worker.StopAsync();

        worker.TakePendingChanges().Should().BeAssignableTo<IReadOnlyDictionary<string, string?>>()
            .Which.Should().ContainKey(file);
    }

    [Fact]
    public async Task FileWatcherWorker_CarriesRenamesAcrossTheHandOver()
    {
        var file = Path.Combine(_watchDir, "new-name.txt");
        var old = Path.Combine(_watchDir, "old-name.txt");
        IProtectionWorker first = new FileWatcherWorker(NewWatcher());
        first.AdoptPendingChanges(new Dictionary<string, string?> { [file] = old });
        await first.StopAsync();

        var carried = first.TakePendingChanges();
        IProtectionWorker second = new FileWatcherWorker(NewWatcher());
        second.AdoptPendingChanges(carried);
        await second.StopAsync();

        second.TakePendingChanges().Should().BeAssignableTo<IReadOnlyDictionary<string, string?>>()
            .Which.Should().Contain(new KeyValuePair<string, string?>(file, old));
    }

    [Fact]
    public async Task FileWatcherWorker_IgnoresPendingObjectsOfTheWrongShape()
    {
        IProtectionWorker worker = new FileWatcherWorker(NewWatcher());

        worker.AdoptPendingChanges("not a dictionary");
        worker.AdoptPendingChanges(null);
        await worker.StopAsync();

        ((IReadOnlyDictionary<string, string?>)worker.TakePendingChanges()!).Should().BeEmpty();
    }

    [Fact]
    public async Task FileWatcherWorker_StopReleasesTheWatcher()
    {
        var watcher = NewWatcher();
        IProtectionWorker worker = new FileWatcherWorker(watcher);

        await worker.StartAsync();
        await worker.StopAsync();

        // A disposed watcher refuses to take over carried-over work.
        watcher.AdoptPendingChanges(new Dictionary<string, string?> { [Path.Combine(_watchDir, "late.txt")] = null });
        watcher.TakePendingChanges().Should().BeEmpty();
    }

    [Fact]
    public async Task SchedulerWorker_WithEverythingDisabled_DoesNotStart_AndReportsNoNextRun()
    {
        IProtectionWorker worker = new SchedulerWorker(NewScheduler());

        await worker.StartAsync();

        worker.NextRunUtc.Should().BeNull();
        _logger.Messages.Should().Contain(m => m.Contains("Backup scheduler not started"));
        await worker.StopAsync();
    }

    [Fact]
    public async Task SchedulerWorker_StartsTheLoop_ThenStopsAndDisposesTheScheduler()
    {
        _config.SetupGet(c => c.BackupInterval).Returns(TimeSpan.FromHours(1));
        var scheduler = NewScheduler();
        IProtectionWorker worker = new SchedulerWorker(scheduler);

        await worker.StartAsync();
        scheduler.IsRunning.Should().BeTrue();
        await worker.StopAsync();

        scheduler.IsRunning.Should().BeFalse();
        var restart = () => scheduler.StartAsync();
        await restart.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task SchedulerWorker_StopWithoutStart_StillDisposes()
    {
        var scheduler = NewScheduler();
        IProtectionWorker worker = new SchedulerWorker(scheduler);

        await worker.StopAsync();

        var restart = () => scheduler.StartAsync();
        await restart.Should().ThrowAsync<ObjectDisposedException>();
    }
}

public sealed class SizeAnalyzerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreSizeAnalyzer_" + Guid.NewGuid().ToString("N"));

    public SizeAnalyzerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private void Write(string relative, int bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    [Fact]
    public void DefaultThreshold_Is500Megabytes()
    {
        new SizeAnalyzer().SizeThreshold.Should().Be(500L * 1024 * 1024);
    }

    [Fact]
    public async Task AnalyzeDirectory_SumsFilesInAllNestedFolders()
    {
        Write("a.bin", 10);
        Write("sub/b.bin", 20);
        Write("sub/deeper/c.bin", 30);
        Directory.CreateDirectory(Path.Combine(_root, "empty"));

        var (size, exceeds) = await new SizeAnalyzer().AnalyzeDirectoryAsync(_root);

        size.Should().Be(60);
        exceeds.Should().BeFalse();
    }

    [Fact]
    public async Task AnalyzeDirectory_EmptyFolder_IsZero()
    {
        var (size, exceeds) = await new SizeAnalyzer { SizeThreshold = 0 }.AnalyzeDirectoryAsync(_root);

        size.Should().Be(0);
        exceeds.Should().BeFalse("zero bytes do not exceed a zero threshold");
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(101, false)]
    [InlineData(102, false)]
    public async Task ExceedsThreshold_OnlyWhenStrictlyGreater(long threshold, bool expected)
    {
        Write("a.bin", 60);
        Write("sub/b.bin", 41);

        var (size, exceeds) = await new SizeAnalyzer { SizeThreshold = threshold }.AnalyzeDirectoryAsync(_root);

        size.Should().Be(101);
        exceeds.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnalyzeDirectory_RejectsBlankPaths(string? path)
    {
        var act = () => new SizeAnalyzer().AnalyzeDirectoryAsync(path!);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AnalyzeDirectory_ThrowsForAMissingDirectory()
    {
        var act = () => new SizeAnalyzer().AnalyzeDirectoryAsync(Path.Combine(_root, "nope"));

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [PosixPermissionsFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task AnalyzeDirectory_SkipsUnreadableSubfolders_AndKeepsCountingTheRest()
    {
        Write("ok.bin", 5);
        Write("locked/hidden.bin", 1000);
        var locked = Path.Combine(_root, "locked");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var (size, _) = await new SizeAnalyzer().AnalyzeDirectoryAsync(_root);

            size.Should().Be(5);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task AnalyzeDirectory_IsOverridable_ForCallersThatFakeTheScan()
    {
        var fake = new FixedSizeAnalyzer();

        var (size, exceeds) = await fake.AnalyzeDirectoryAsync("anything");

        size.Should().Be(7);
        exceeds.Should().BeTrue();
    }

    private sealed class FixedSizeAnalyzer : SizeAnalyzer
    {
        public override Task<(long Size, bool ExceedsThreshold)> AnalyzeDirectoryAsync(string path) => Task.FromResult((7L, true));
    }
}
