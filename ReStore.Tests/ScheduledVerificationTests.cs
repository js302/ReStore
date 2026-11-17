using FluentAssertions;
using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>Drives verification cycles with an injected clock, as BackupSchedulerTests does.</summary>
public class ScheduledVerificationTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _watchDir;
    private readonly string _storageDir;
    private readonly TestLogger _logger = new();
    private readonly Mock<IConfigManager> _configMock = new();
    private readonly SystemState _state;
    private readonly SizeAnalyzer _sizeAnalyzer = new();
    private readonly LocalStorage _storage;
    private readonly VerificationConfig _verification = new();

    private DateTime _now = DateTime.UtcNow;

    public ScheduledVerificationTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreVerifySchedTests_" + Guid.NewGuid().ToString("N"));
        _watchDir = Path.Combine(_testRoot, "watch");
        _storageDir = Path.Combine(_testRoot, "storage");
        Directory.CreateDirectory(_watchDir);
        Directory.CreateDirectory(_storageDir);
        File.WriteAllText(Path.Combine(_watchDir, "seed.txt"), "seed content");

        _state = new SystemState(_logger);
        _state.SetStateFilePath(Path.Combine(_testRoot, "state.json"));

        _storage = new LocalStorage(_logger);
        _storage.InitializeAsync(new Dictionary<string, string> { ["path"] = _storageDir }).GetAwaiter().GetResult();

        _configMock.SetupGet(c => c.WatchDirectories).Returns([
            new WatchDirectoryConfig { Path = _watchDir, StorageType = null }
        ]);
        _configMock.SetupGet(c => c.BackupInterval).Returns(TimeSpan.FromHours(1));
        _configMock.SetupGet(c => c.ExcludedPatterns).Returns([]);
        _configMock.SetupGet(c => c.ExcludedPaths).Returns([]);
        _configMock.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        _configMock.SetupGet(c => c.SizeThresholdMB).Returns(500);
        _configMock.SetupGet(c => c.GlobalStorageType).Returns("local");
        _configMock.SetupGet(c => c.BackupType).Returns(BackupType.ChunkSnapshot);
        _configMock.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig());
        _configMock.SetupGet(c => c.Retention).Returns(new RetentionConfig());
        _configMock.SetupGet(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        _configMock.SetupGet(c => c.SystemBackup).Returns(new SystemBackupConfig { Enabled = false });
        _configMock.SetupGet(c => c.Verification).Returns(() => _verification);
        _configMock.SetupGet(c => c.Notifications).Returns(new NotificationConfig());
        _configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(_storage);
    }

    public void Dispose()
    {
        _storage.Dispose();

        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    // Own coordinator: the scheduler backs off (rather than waits) when the repository is busy, so a
    // shared singleton would make these tests depend on whatever else runs in parallel.
    private readonly RepositoryCoordinator _coordinator = new(
        Path.Combine(TestEnvironment.RootDirectory, "lock_" + Guid.NewGuid().ToString("N") + ".lock"));

    private BackupScheduler CreateScheduler() => new(
        _configMock.Object,
        _logger,
        _state,
        _sizeAnalyzer,
        passwordProvider: null,
        utcNow: () => _now,
        coordinator: _coordinator);

    [Fact]
    public async Task RunDueBackupsAsync_ShouldNotVerify_WhenVerificationIsDisabled()
    {
        _verification.Enabled = false;

        using var scheduler = CreateScheduler();
        var result = await scheduler.RunDueBackupsAsync();

        result.SnapshotsVerified.Should().Be(0);
        _logger.Messages.Should().NotContain(m => m.Contains("Scheduled verification due"));
    }

    [Fact]
    public async Task RunDueBackupsAsync_ShouldVerifyNewestSnapshot_WhenEnabledAndDue()
    {
        _verification.Enabled = true;
        _verification.VerifyInterval = TimeSpan.FromDays(7);

        using var scheduler = CreateScheduler();

        // First sweep creates the snapshot, and verification of it runs in the same sweep.
        var result = await scheduler.RunDueBackupsAsync();

        result.SnapshotsVerified.Should().Be(1);
        result.VerificationsFailed.Should().Be(0);
        _logger.Messages.Should().Contain(m => m.Contains("Scheduled verification passed"));
    }

    [Fact]
    public async Task RunDueBackupsAsync_ShouldNotVerifyAgain_BeforeIntervalElapses()
    {
        _verification.Enabled = true;
        _verification.VerifyInterval = TimeSpan.FromDays(7);

        using var scheduler = CreateScheduler();
        await scheduler.RunDueBackupsAsync();

        var verifyCountAfterFirst = _logger.Messages.Count(m => m.Contains("Scheduled verification due"));

        _now = _now.AddDays(1);
        var second = await scheduler.RunDueBackupsAsync();

        second.SnapshotsVerified.Should().Be(0);
        _logger.Messages.Count(m => m.Contains("Scheduled verification due")).Should().Be(verifyCountAfterFirst);
    }

    [Fact]
    public async Task RunDueBackupsAsync_ShouldNotVerifyAfterRestart_WhenTheGroupWasVerifiedRecently()
    {
        _verification.Enabled = true;
        _verification.VerifyInterval = TimeSpan.FromDays(7);

        using (var scheduler = CreateScheduler())
        {
            (await scheduler.RunDueBackupsAsync()).SnapshotsVerified.Should().Be(1);
        }

        await _state.SaveStateAsync();

        // A fresh state and scheduler stand in for an app restart: no in-memory attempt marker survives.
        var reloaded = new SystemState(_logger);
        reloaded.SetStateFilePath(Path.Combine(_testRoot, "state.json"));
        await reloaded.LoadStateAsync();

        _now = _now.AddDays(1);
        using var restarted = new BackupScheduler(_configMock.Object, _logger, reloaded, _sizeAnalyzer, null, () => _now, _coordinator);
        var result = await restarted.RunDueBackupsAsync();

        result.SnapshotsVerified.Should().Be(0, "the persisted verification time says it is not due yet");
        result.VerificationsFailed.Should().Be(0);
    }

    [Fact]
    public async Task RunDueBackupsAsync_ShouldRetryAFailedVerification_AfterAnHour_NotAfterAFullInterval()
    {
        // Seed a snapshot with verification off and the file backups disabled afterwards.
        _verification.Enabled = false;
        using (var seed = CreateScheduler())
        {
            await seed.RunDueBackupsAsync();
        }

        _configMock.SetupGet(c => c.BackupInterval).Returns(TimeSpan.Zero);
        _verification.Enabled = true;
        _verification.VerifyInterval = TimeSpan.FromDays(7);

        var failingStorage = new Mock<IStorage>();
        failingStorage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("provider unreachable"));
        failingStorage.Setup(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(failingStorage.Object);

        int Attempts() => _logger.Messages.Count(m => m.Contains("Scheduled verification due"));

        using var scheduler = CreateScheduler();
        _now = _now.AddDays(8);
        (await scheduler.RunDueBackupsAsync()).VerificationsFailed.Should().Be(1);
        Attempts().Should().Be(1);

        _now = _now.AddMinutes(30);
        await scheduler.RunDueBackupsAsync();
        Attempts().Should().Be(1, "a failed attempt backs off, it does not loop every tick");

        _now = _now.AddMinutes(31);
        await scheduler.RunDueBackupsAsync();
        Attempts().Should().Be(2, "the retry comes after an hour, well before the 7 day interval");
    }

    [Fact]
    public async Task RunDueBackupsAsync_ShouldStillVerify_WhenBackupIntervalIsZero()
    {
        using (var seed = CreateScheduler())
        {
            await seed.RunDueBackupsAsync();
        }

        _configMock.SetupGet(c => c.BackupInterval).Returns(TimeSpan.Zero);
        _verification.Enabled = true;
        _verification.VerifyInterval = TimeSpan.FromDays(7);

        using var scheduler = CreateScheduler();
        var result = await scheduler.RunDueBackupsAsync();

        result.DirectoriesBackedUp.Should().Be(0, "backupInterval <= 0 disables the interval file backups");
        result.SnapshotsVerified.Should().Be(1, "verification has its own setting");
    }

    [Fact]
    public async Task StartAsync_ShouldStart_WhenBackupIntervalIsZeroButVerificationIsEnabled()
    {
        _configMock.SetupGet(c => c.BackupInterval).Returns(TimeSpan.Zero);
        _verification.Enabled = true;
        _verification.VerifyInterval = TimeSpan.FromDays(7);

        using var scheduler = CreateScheduler();
        await scheduler.StartAsync();

        scheduler.IsRunning.Should().BeTrue();
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task RunDueBackupsAsync_ShouldVerifyAgain_OnceIntervalElapses()
    {
        _verification.Enabled = true;
        _verification.VerifyInterval = TimeSpan.FromDays(7);

        using var scheduler = CreateScheduler();
        await scheduler.RunDueBackupsAsync();

        _now = _now.AddDays(8);
        var second = await scheduler.RunDueBackupsAsync();

        second.SnapshotsVerified.Should().Be(1);
    }

    [Fact]
    public async Task RunDueBackupsAsync_ShouldReportFailure_WhenSnapshotChunkIsMissing()
    {
        _verification.Enabled = true;
        _verification.VerifyInterval = TimeSpan.FromDays(7);

        using var scheduler = CreateScheduler();
        await scheduler.RunDueBackupsAsync();

        // Simulate bit-rot / a provider dropping an object.
        var chunkFiles = Directory.GetFiles(Path.Combine(_storageDir, "chunks"), "*.chunk", SearchOption.AllDirectories);
        chunkFiles.Should().NotBeEmpty();
        File.Delete(chunkFiles[0]);

        _now = _now.AddDays(8);
        var result = await scheduler.RunDueBackupsAsync();

        result.VerificationsFailed.Should().Be(1);
        result.HasFailures.Should().BeTrue();
        result.VerificationFailureMessages.Should().NotBeEmpty();
        _logger.Messages.Should().Contain(m => m.Contains("Scheduled verification FAILED"));
    }

    [Fact]
    public async Task RunDueBackupsAsync_ShouldSkipRemoteProviders_WhenLocalStorageOnlyIsSet()
    {
        _verification.Enabled = true;
        _verification.VerifyInterval = TimeSpan.FromDays(7);
        _verification.LocalStorageOnly = true;

        _configMock.SetupGet(c => c.WatchDirectories).Returns([
            new WatchDirectoryConfig { Path = _watchDir, StorageType = "s3" }
        ]);

        using var scheduler = CreateScheduler();
        var result = await scheduler.RunDueBackupsAsync();

        result.DirectoriesBackedUp.Should().Be(1, "the backup itself still runs against the configured provider");
        result.SnapshotsVerified.Should().Be(0,
            "local-only is the default so enabling verification cannot silently bill cloud egress");
    }

    [Fact]
    public async Task RunDueBackupsAsync_ShouldVerifyRemoteProvider_WhenLocalStorageOnlyIsCleared()
    {
        _verification.Enabled = true;
        _verification.VerifyInterval = TimeSpan.FromDays(7);
        _verification.LocalStorageOnly = false;

        _configMock.SetupGet(c => c.WatchDirectories).Returns([
            new WatchDirectoryConfig { Path = _watchDir, StorageType = "s3" }
        ]);

        using var scheduler = CreateScheduler();
        var result = await scheduler.RunDueBackupsAsync();

        result.SnapshotsVerified.Should().Be(1);
    }

    [Fact]
    public async Task RunDueBackupsAsync_ShouldRaiseBackupCycleFinished_WithFailureDetail()
    {
        _configMock.SetupGet(c => c.WatchDirectories).Returns([
            new WatchDirectoryConfig { Path = _watchDir, StorageType = "broken" }
        ]);
        _configMock.Setup(c => c.CreateStorageAsync("broken"))
            .ThrowsAsync(new InvalidOperationException("provider unavailable"));

        using var scheduler = CreateScheduler();
        BackupCycleResult? captured = null;
        scheduler.BackupCycleFinished += result => captured = result;

        await scheduler.RunDueBackupsAsync();

        captured.Should().NotBeNull();
        captured!.DirectoriesFailed.Should().Be(1);
        captured.HasFailures.Should().BeTrue();
        captured.FailureMessages.Should().Contain(m => m.Contains("provider unavailable"));
    }

    [Fact]
    public async Task RunDueBackupsAsync_ShouldRaiseBackupRecovered_OnFirstSuccessAfterFailure()
    {
        _configMock.SetupGet(c => c.WatchDirectories).Returns([
            new WatchDirectoryConfig { Path = _watchDir, StorageType = "flaky" }
        ]);
        _configMock.Setup(c => c.CreateStorageAsync("flaky"))
            .ThrowsAsync(new InvalidOperationException("provider unavailable"));

        using var scheduler = CreateScheduler();
        var recoveredCount = 0;
        scheduler.BackupRecovered += _ => recoveredCount++;

        await scheduler.RunDueBackupsAsync();
        recoveredCount.Should().Be(0, "a failing cycle is not a recovery");

        // Provider comes back.
        _configMock.Setup(c => c.CreateStorageAsync("flaky")).ReturnsAsync(_storage);
        _now = _now.AddHours(2);
        File.WriteAllText(Path.Combine(_watchDir, "changed.txt"), "new content");

        await scheduler.RunDueBackupsAsync();

        recoveredCount.Should().Be(1, "the first good cycle after failures should notify");

        // A second consecutive success must not re-notify.
        _now = _now.AddHours(2);
        File.WriteAllText(Path.Combine(_watchDir, "changed2.txt"), "more content");
        await scheduler.RunDueBackupsAsync();

        recoveredCount.Should().Be(1);
    }

    [Fact]
    public async Task RunDueBackupsAsync_ShouldVerifyOlderSnapshots_ByRotation()
    {
        _verification.Enabled = true;
        _verification.VerifyInterval = TimeSpan.FromDays(7);
        _verification.SnapshotsPerRun = 2;

        using var scheduler = CreateScheduler();

        await scheduler.RunDueBackupsAsync();

        _now = _now.AddDays(8);
        File.WriteAllText(Path.Combine(_watchDir, "second.txt"), "second");
        await scheduler.RunDueBackupsAsync();

        _now = _now.AddDays(8);
        File.WriteAllText(Path.Combine(_watchDir, "third.txt"), "third");
        var third = await scheduler.RunDueBackupsAsync();

        _state.GetBackupsForGroup(WatchGroupKey()).Should().HaveCount(3);

        // With a budget of 2 the newest plus one older snapshot are checked, so an older
        // restore point gets covered rather than never being looked at.
        third.SnapshotsVerified.Should().Be(2);
    }

    [Fact]
    public async Task RunDueBackupsAsync_ShouldVerifyOnlyNewest_WhenBudgetIsOne()
    {
        _verification.Enabled = true;
        _verification.VerifyInterval = TimeSpan.FromDays(7);
        _verification.SnapshotsPerRun = 1;

        using var scheduler = CreateScheduler();
        await scheduler.RunDueBackupsAsync();

        _now = _now.AddDays(8);
        File.WriteAllText(Path.Combine(_watchDir, "second.txt"), "second");
        var second = await scheduler.RunDueBackupsAsync();

        second.SnapshotsVerified.Should().Be(1);
    }

    [Fact]
    public async Task RunDueBackupsAsync_ShouldExtendCoverage_AcrossSuccessiveCycles()
    {
        _verification.Enabled = true;
        _verification.VerifyInterval = TimeSpan.FromDays(7);
        _verification.SnapshotsPerRun = 2;

        using var scheduler = CreateScheduler();

        await scheduler.RunDueBackupsAsync();

        _now = _now.AddDays(8);
        File.WriteAllText(Path.Combine(_watchDir, "b.txt"), "b");
        await scheduler.RunDueBackupsAsync();

        _now = _now.AddDays(8);
        File.WriteAllText(Path.Combine(_watchDir, "c.txt"), "c");
        await scheduler.RunDueBackupsAsync();

        var coveredAfterThird = _state.SnapshotVerificationTimes.Count;

        _now = _now.AddDays(8);
        File.WriteAllText(Path.Combine(_watchDir, "d.txt"), "d");
        await scheduler.RunDueBackupsAsync();

        _state.SnapshotVerificationTimes.Count.Should().BeGreaterThan(coveredAfterThird,
            "each cycle should reach a snapshot it had not verified before");
    }

    [Fact]
    public async Task VerificationRotation_ShouldSurviveARestart()
    {
        _verification.Enabled = true;
        _verification.VerifyInterval = TimeSpan.FromDays(7);
        _verification.SnapshotsPerRun = 2;

        using (var scheduler = CreateScheduler())
        {
            await scheduler.RunDueBackupsAsync();

            _now = _now.AddDays(8);
            File.WriteAllText(Path.Combine(_watchDir, "b.txt"), "b");
            await scheduler.RunDueBackupsAsync();
        }

        await _state.SaveStateAsync();
        var verifiedBeforeRestart = _state.SnapshotVerificationTimes.Count;
        verifiedBeforeRestart.Should().BeGreaterThan(0);

        // A fresh state instance stands in for an app restart.
        var reloaded = new SystemState(_logger);
        reloaded.SetStateFilePath(Path.Combine(_testRoot, "state.json"));
        await reloaded.LoadStateAsync();

        reloaded.SnapshotVerificationTimes.Count.Should().Be(verifiedBeforeRestart,
            "the rotation must resume where it left off rather than restarting at the newest snapshot");
    }

    [Fact]
    public async Task VerificationRotation_ShouldPersistMarker_WhenVerificationFails()
    {
        // Seed a snapshot with verification OFF, so the only marker that can reach disk is the
        // one written by the failing verify cycle below.
        _verification.Enabled = false;

        using (var seedScheduler = CreateScheduler())
        {
            await seedScheduler.RunDueBackupsAsync();
        }

        await _state.SaveStateAsync();
        _state.SnapshotVerificationTimes.Should().BeEmpty("verification was disabled for the seed cycle");

        // Now enable verification, but make the provider unreachable at verify time.
        _verification.Enabled = true;
        _verification.VerifyInterval = TimeSpan.FromDays(7);

        var failingStorage = new Mock<IStorage>();
        failingStorage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("provider unreachable"));
        failingStorage.Setup(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(failingStorage.Object);

        _now = _now.AddDays(8);

        using (var scheduler = CreateScheduler())
        {
            var result = await scheduler.RunDueBackupsAsync();
            result.VerificationsFailed.Should().BeGreaterThan(0);
        }

        // The marker must be on disk even though the verify threw: otherwise a persistently
        // failing provider makes the rotation restart at the same snapshot after every
        // restart, and older restore points are never reached.
        var reloaded = new SystemState(_logger);
        reloaded.SetStateFilePath(Path.Combine(_testRoot, "state.json"));
        await reloaded.LoadStateAsync();

        reloaded.SnapshotVerificationTimes.Should().NotBeEmpty(
            "a failed verification still consumed its turn in the rotation");
    }

    [Fact]
    public async Task RunDueBackupsAsync_ShouldKeepGroupDue_WhenEverySnapshotWasSkippedAsRemote()
    {
        _verification.Enabled = true;
        _verification.VerifyInterval = TimeSpan.FromDays(7);
        _verification.LocalStorageOnly = true;

        _configMock.SetupGet(c => c.WatchDirectories).Returns([
            new WatchDirectoryConfig { Path = _watchDir, StorageType = "s3" }
        ]);

        using var scheduler = CreateScheduler();
        await scheduler.RunDueBackupsAsync();

        // Nothing was attempted, so the group must not have started its back-off. Allowing
        // remote should let it verify straight away rather than waiting out an interval it
        // never actually consumed.
        _verification.LocalStorageOnly = false;
        var second = await scheduler.RunDueBackupsAsync();

        second.SnapshotsVerified.Should().Be(1,
            "a group whose snapshots were all skipped must stay due, not silently back off");
    }

    [Fact]
    public void RemoveBackupsFromGroup_ShouldPruneVerificationTimes()
    {
        _state.AddSnapshotBackup("C:/Data", "snap-1", "snapshots/data/snap-1.manifest.json", "local", ["aa11"]);
        _state.RecordSnapshotVerified("snapshots/data/snap-1.manifest.json", DateTime.UtcNow);

        _state.SnapshotVerificationTimes.Should().ContainKey("snapshots/data/snap-1.manifest.json");

        _state.RemoveBackupsFromGroup("C:/Data", ["snapshots/data/snap-1.manifest.json"]);

        _state.SnapshotVerificationTimes.Should().BeEmpty(
            "retention prunes snapshots, so the rotation bookkeeping must not grow without bound");
    }

    [Fact]
    public async Task RunDueOnceAsync_QuickMode_DownloadsNoChunk_AndStillReportsAMissingOne()
    {
        _verification.Enabled = true;
        _verification.VerifyInterval = TimeSpan.FromDays(7);
        _verification.Mode = "quick";

        using var scheduler = CreateScheduler();
        var first = await scheduler.RunDueOnceAsync();

        first.SnapshotsVerified.Should().Be(1);
        _logger.Messages.Should().Contain(m => m.Contains("quick mode)"));
        _state.Telemetry.Verification.DownloadedChunks.Should().Be(0, "quick mode reads no chunk");

        var chunkFiles = Directory.GetFiles(Path.Combine(_storageDir, "chunks"), "*.chunk", SearchOption.AllDirectories);
        File.Delete(chunkFiles[0]);
        _now = _now.AddDays(8);
        var second = await scheduler.RunDueOnceAsync();

        second.VerificationsFailed.Should().Be(1);
        second.HasFailures.Should().BeTrue();
    }

    [Fact]
    public async Task RunDueOnceAsync_UsesTheConfiguredSamplePercent_AndFallsBackToFullForAnUnknownMode()
    {
        _verification.Enabled = true;
        _verification.VerifyInterval = TimeSpan.FromDays(7);
        _verification.Mode = "sample";
        _verification.SamplePercent = 50;

        using var scheduler = CreateScheduler();
        await scheduler.RunDueOnceAsync();
        _logger.Messages.Should().Contain(m => m.Contains("sample mode)"));

        _verification.Mode = "no-such-mode";
        _now = _now.AddDays(8);
        await scheduler.RunDueOnceAsync();
        _logger.Messages.Should().Contain(m => m.Contains("full mode)"), "an unreadable mode must not weaken verification");
    }

    [Fact]
    public async Task RunDueOnceAsync_IsTheSchedulersOwnSweep_AndReportsRetentionAndNothingDue()
    {
        _configMock.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = true, KeepLastPerDirectory = 10, MaxAgeDays = 30 });

        using var scheduler = CreateScheduler();
        var first = await scheduler.RunDueOnceAsync();
        var second = await scheduler.RunDueOnceAsync();

        first.DirectoriesBackedUp.Should().Be(1);
        first.RetentionRan.Should().BeTrue();
        first.RetentionFailed.Should().BeFalse();
        second.DidWork.Should().BeFalse("nothing is due right after a backup");
        second.RetentionRan.Should().BeFalse("retention runs once a day");
        second.HasFailures.Should().BeFalse();
    }

    [Fact]
    public async Task RunDueOnceAsync_CountsABusyRepositoryAsDeferred_NotAsAFailure()
    {
        using var scheduler = CreateScheduler();
        await using var held = await _coordinator.AcquireMutationAsync("another operation");

        var result = await scheduler.RunDueOnceAsync();

        result.Deferred.Should().BeGreaterThan(0);
        result.HasFailures.Should().BeFalse();
        result.DirectoriesBackedUp.Should().Be(0);
    }

    [Fact]
    public async Task ProtectionStatus_ReportsGroupsDueTimesAndTheLastVerification()
    {
        _verification.Enabled = true;
        _verification.VerifyInterval = TimeSpan.FromDays(7);
        _verification.Mode = "sample";
        _configMock.SetupGet(c => c.Protection).Returns(new ProtectionConfig());

        using var scheduler = CreateScheduler();
        var before = ProtectionStatusBuilder.Build(_configMock.Object, _state, scheduler, _now);
        before.Groups.Should().ContainSingle().Which.LastSnapshotUtc.Should().BeNull();
        before.Groups[0].Overdue.Should().BeTrue("never backed up means due now");
        before.Verify.LastVerifiedUtc.Should().BeNull();

        await scheduler.RunDueOnceAsync();
        var after = ProtectionStatusBuilder.Build(_configMock.Object, _state, scheduler, _now.AddMinutes(5), repositoryBusy: true, busyReason: "Backing up");

        var group = after.Groups.Should().ContainSingle().Subject;
        group.LastSnapshotUtc.Should().NotBeNull();
        group.SnapshotCount.Should().Be(1);
        group.LastRun.Should().NotBeNull();
        group.Overdue.Should().BeFalse();
        group.Stale.Should().BeFalse();
        group.NextDueUtc.Should().BeCloseTo(_now.AddHours(1), TimeSpan.FromMinutes(5));
        after.NextDueUtc.Should().NotBeNull();
        after.VerificationMode.Should().Be("sample");
        after.Verify.LastVerifiedUtc.Should().NotBeNull();
        after.Verify.LastResult.Should().Be(ActivityResult.Succeeded);
        after.RepositoryBusy.Should().BeTrue();
        after.RepositoryBusyReason.Should().Be("Backing up");
    }

    [Fact]
    public async Task ProtectionStatus_ShowsTheLastFailedBackupPerGroupFromTheActivityLog()
    {
        _configMock.SetupGet(c => c.Protection).Returns(new ProtectionConfig());
        using var scheduler = CreateScheduler();
        ActivityRecorder.RecordFailure(_state, ActivityType.Backup, _watchDir, DateTime.UtcNow.AddMinutes(-5), new IOException("provider unreachable"));
        ActivityRecorder.RecordFailure(_state, ActivityType.Backup, Path.Combine(_testRoot, "other"), DateTime.UtcNow.AddMinutes(-5), new IOException("elsewhere"));

        var status = ProtectionStatusBuilder.Build(_configMock.Object, _state, scheduler, _now);

        var failure = status.Groups.Should().ContainSingle().Which.LastFailure;
        failure.Should().NotBeNull();
        failure!.Message.Should().Be("provider unreachable");
    }

    [Fact]
    public async Task ProbeBusyAsync_ReflectsWhetherAnotherOperationHoldsTheRepository()
    {
        (await ProtectionStatusBuilder.ProbeBusyAsync(_coordinator)).Busy.Should().BeFalse();

        await using var held = await _coordinator.AcquireMutationAsync("Backing up docs");
        var (busy, reason) = await ProtectionStatusBuilder.ProbeBusyAsync(_coordinator);

        busy.Should().BeTrue();
        reason.Should().Be("Backing up docs");
    }

    private string WatchGroupKey() => Path.GetFullPath(_watchDir);
}
