using FluentAssertions;
using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// The scheduler's due decisions with clock schedules per folder and the backup window, on a fake clock and a fixed (UTC) time zone.
/// The clock starts two days ahead of real time at midnight UTC, because the state stamps its history with the real clock.
/// </summary>
public class BackupSchedulerScheduleTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _watchDir;
    private readonly string _watchDir2;
    private readonly string _storageDir;
    private readonly TestLogger _logger = new();
    private readonly Mock<IConfigManager> _configMock = new();
    private readonly SystemState _state;
    private readonly SizeAnalyzer _sizeAnalyzer = new();
    private readonly LocalStorage _storage;
    private readonly List<WatchDirectoryConfig> _watches;
    private readonly SystemBackupConfig _systemBackup = new() { Enabled = false };
    private readonly RetentionConfig _retention = new();
    private readonly VerificationConfig _verification = new();
    private readonly RepositoryCoordinator _coordinator = new(
        Path.Combine(TestEnvironment.RootDirectory, "lock_" + Guid.NewGuid().ToString("N") + ".lock"));

    private BackupWindowConfig? _window;
    private TimeSpan _backupInterval = TimeSpan.FromHours(1);

    /// <summary>Midnight UTC two days from now: after every timestamp the real clock stamps into the state during the test.</summary>
    private readonly DateTime _base = DateTime.UtcNow.Date.AddDays(2);

    private DateTime _now;

    public BackupSchedulerScheduleTests()
    {
        _now = _base;
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreSchedScheduleTests_" + Guid.NewGuid().ToString("N"));
        _watchDir = Path.Combine(_testRoot, "watch");
        _watchDir2 = Path.Combine(_testRoot, "watch2");
        _storageDir = Path.Combine(_testRoot, "storage");
        Directory.CreateDirectory(_watchDir);
        Directory.CreateDirectory(_watchDir2);
        Directory.CreateDirectory(_storageDir);
        File.WriteAllText(Path.Combine(_watchDir, "seed.txt"), "seed content");
        File.WriteAllText(Path.Combine(_watchDir2, "seed.txt"), "other seed content");

        _state = new SystemState(_logger);
        _state.SetStateFilePath(Path.Combine(_testRoot, "state.json"));

        _storage = new LocalStorage(_logger);
        _storage.InitializeAsync(new Dictionary<string, string> { ["path"] = _storageDir }).GetAwaiter().GetResult();

        _watches = [new WatchDirectoryConfig { Path = _watchDir }];
        _configMock.SetupGet(c => c.WatchDirectories).Returns(() => _watches);
        _configMock.SetupGet(c => c.BackupInterval).Returns(() => _backupInterval);
        _configMock.SetupGet(c => c.ExcludedPatterns).Returns([]);
        _configMock.SetupGet(c => c.ExcludedPaths).Returns([]);
        _configMock.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        _configMock.SetupGet(c => c.SizeThresholdMB).Returns(500);
        _configMock.SetupGet(c => c.GlobalStorageType).Returns("local");
        _configMock.SetupGet(c => c.BackupType).Returns(BackupType.ChunkSnapshot);
        _configMock.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig());
        _configMock.SetupGet(c => c.Retention).Returns(() => _retention);
        _configMock.SetupGet(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        _configMock.SetupGet(c => c.SystemBackup).Returns(() => _systemBackup);
        _configMock.SetupGet(c => c.Verification).Returns(() => _verification);
        _configMock.SetupGet(c => c.Notifications).Returns(new NotificationConfig());
        _configMock.SetupGet(c => c.BackupWindow).Returns(() => _window);
        _configMock.SetupGet(c => c.Protection).Returns(new ProtectionConfig());
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

    private BackupScheduler Create(ProtectionPauseStore? pauseStore = null) => new(
        _configMock.Object, _logger, _state, _sizeAnalyzer, passwordProvider: null, utcNow: () => _now,
        coordinator: _coordinator, pauseStore: pauseStore, timeZone: TimeZoneInfo.Utc);

    private void At(double hours, int days = 0) => _now = _base.AddDays(days).AddHours(hours);

    private static async Task<int> BackedUp(BackupScheduler scheduler) => (await scheduler.RunDueBackupsAsync()).DirectoriesBackedUp;

    private string Group(string directory) => Path.GetFullPath(directory);

    private void UseWindow(string start, string end, string days = "all", bool enabled = true)
        => _window = new BackupWindowConfig { Enabled = enabled, Start = start, End = end, Days = days };

    // ---- old configuration: nothing changes --------------------------------------------------------------------------

    [Fact]
    public async Task WithoutSchedulesOrWindow_TheIntervalBehavesAsBefore()
    {
        using var scheduler = Create();

        At(1);
        (await BackedUp(scheduler)).Should().Be(1, "never backed up is due at once");
        At(1.5);
        (await BackedUp(scheduler)).Should().Be(0, "inside the hour");
        At(2.01);
        (await BackedUp(scheduler)).Should().Be(1, "the hour has passed");

        var result = await scheduler.RunDueBackupsAsync();
        result.WindowClosed.Should().BeFalse();
        result.WindowDeferred.Should().Be(0);
    }

    [Fact]
    public void WithoutSchedulesOrWindow_TheNextRunIsLastBackupPlusInterval()
    {
        using var scheduler = Create();
        At(1);
        scheduler.ComputeNextRunUtc().Should().Be(_now, "never backed up is due now");
        scheduler.ResolveFolderSchedule(_watches[0])!.Interval.Should().Be(TimeSpan.FromHours(1));
        scheduler.CurrentWindow().Should().BeNull();
    }

    [Fact]
    public async Task ABlankSchedule_IsTheSameAsNone()
    {
        _watches[0].Schedule = "  ";
        using var scheduler = Create();

        At(1);
        (await BackedUp(scheduler)).Should().Be(1);
        At(1.5);
        (await BackedUp(scheduler)).Should().Be(0);
    }

    // ---- clock schedules ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADailySchedule_BacksUpOnceAtItsTime_AndNotAgainUntilTheNextDay()
    {
        _watches[0].Schedule = "daily 02:00";
        using var scheduler = Create();

        At(1);
        (await BackedUp(scheduler)).Should().Be(1, "a folder that was never backed up is due at once");
        At(1.5);
        (await BackedUp(scheduler)).Should().Be(0, "02:00 has not come yet");
        At(2.01);
        (await BackedUp(scheduler)).Should().Be(1, "the 02:00 run");
        At(2.2);
        (await BackedUp(scheduler)).Should().Be(0);
        At(14);
        (await BackedUp(scheduler)).Should().Be(0, "not after the hour interval: the schedule replaces it for this folder");
        At(1.9, days: 1);
        (await BackedUp(scheduler)).Should().Be(0);
        At(2.01, days: 1);
        (await BackedUp(scheduler)).Should().Be(1);
    }

    [Fact]
    public async Task AMissedRun_CatchesUpOnce_WhenTheMachineComesBack()
    {
        _watches[0].Schedule = "daily 02:00";
        using var scheduler = Create();
        At(1);
        await BackedUp(scheduler);

        // Off for three nights, back at 10:00 on the fourth day.
        At(10, days: 3);
        (await BackedUp(scheduler)).Should().Be(1, "one catch-up run");
        At(10.1, days: 3);
        (await BackedUp(scheduler)).Should().Be(0, "no repeated catch-up");
        At(23, days: 3);
        (await BackedUp(scheduler)).Should().Be(0);
        At(2.01, days: 4);
        (await BackedUp(scheduler)).Should().Be(1, "the next regular run");
    }

    [Fact]
    public async Task AManualBackupAfterTheScheduledTime_SatisfiesIt()
    {
        _watches[0].Schedule = "daily 02:00";
        using var scheduler = Create();
        At(1);
        await BackedUp(scheduler);

        // The scheduled run at 02:00 is missed; a backup made by hand (recorded as a successful run) at 09:00 counts.
        _state.RecordGroupRunSucceeded(Group(_watchDir), _base.AddHours(9));
        At(10);
        (await BackedUp(scheduler)).Should().Be(0);
    }

    [Fact]
    public async Task ARunThatFoundNothingToBackUp_CountsAsDone_ForAFreshProcess()
    {
        _watches[0].Schedule = "daily 02:00";
        _state.RecordGroupRunSucceeded(Group(_watchDir), _base.AddHours(2).AddMinutes(1));

        // A new scheduler (as 'run-due' is) has no in-memory attempt: the persisted successful run is what keeps it from re-scanning.
        using var scheduler = Create();
        At(12);
        (await BackedUp(scheduler)).Should().Be(0);
    }

    [Fact]
    public async Task Folders_HaveTheirOwnSchedules_MixedWithTheDefaultInterval()
    {
        _watches.Add(new WatchDirectoryConfig { Path = _watchDir2 });
        _watches[0].Schedule = "daily 02:00";
        using var scheduler = Create();

        At(1);
        (await BackedUp(scheduler)).Should().Be(2, "both were never backed up");

        At(1.5);
        (await BackedUp(scheduler)).Should().Be(0);

        At(2.01);
        var both = await scheduler.RunDueBackupsAsync();
        both.DirectoriesBackedUp.Should().Be(2, "the daily folder reached 02:00 and the interval folder is over an hour old");

        At(3.5);
        (await BackedUp(scheduler)).Should().Be(1, "only the interval folder is due again");
        _logger.Messages.Last(m => m.Contains("Scheduled backup due")).Should().Contain("watch2");
    }

    [Fact]
    public async Task AnEveryScheduleOnAFolder_UsesItsOwnInterval_NotTheGlobalOne()
    {
        _watches[0].Schedule = "every 4h";
        using var scheduler = Create();

        At(1);
        await BackedUp(scheduler);
        At(3);
        (await BackedUp(scheduler)).Should().Be(0, "two hours is more than the global hour but less than 4h");
        At(5.01);
        (await BackedUp(scheduler)).Should().Be(1);
    }

    [Fact]
    public async Task AnOffFolder_IsNeverScheduled_AndOtherFoldersStillAre()
    {
        _watches.Add(new WatchDirectoryConfig { Path = _watchDir2 });
        _watches[0].Schedule = "off";
        using var scheduler = Create();

        At(1);
        var result = await scheduler.RunDueBackupsAsync();

        result.DirectoriesBackedUp.Should().Be(1);
        _state.GetLastBackupTimeForGroup(Group(_watchDir)).Should().Be(DateTime.MinValue, "off means the scheduler never backs it up");
        _state.GetLastBackupTimeForGroup(Group(_watchDir2)).Should().NotBe(DateTime.MinValue);

        var perGroup = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        scheduler.ComputeNextRunUtc(perGroup);
        perGroup.Should().NotContainKey(Group(_watchDir));
        perGroup.Should().ContainKey(Group(_watchDir2));
    }

    [Fact]
    public async Task ASchedule_WorksWhenTheGlobalIntervalIsDisabled()
    {
        _backupInterval = TimeSpan.Zero;
        _watches.Add(new WatchDirectoryConfig { Path = _watchDir2 });
        _watches[1].Schedule = "daily 02:00";
        using var scheduler = Create();

        At(1);
        var result = await scheduler.RunDueBackupsAsync();

        result.DirectoriesBackedUp.Should().Be(1, "only the folder with its own schedule");
        _state.GetLastBackupTimeForGroup(Group(_watchDir)).Should().Be(DateTime.MinValue);
    }

    [Fact]
    public async Task AnInvalidSchedule_FallsBackToTheInterval_AndWarnsOnce()
    {
        _watches[0].Schedule = "daily 2pm";
        using var scheduler = Create();

        At(1);
        (await BackedUp(scheduler)).Should().Be(1);
        At(1.2);
        await scheduler.RunDueBackupsAsync();
        At(1.4);
        await scheduler.RunDueBackupsAsync();

        _logger.Messages.Count(m => m.Contains("Ignoring the invalid schedule 'daily 2pm'")).Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_StartsForAFolderSchedule_EvenWhenTheGlobalIntervalIsDisabled_AndNotWhenEverythingIsOff()
    {
        _backupInterval = TimeSpan.Zero;
        using (var none = Create())
        {
            await none.StartAsync();
            none.IsRunning.Should().BeFalse();
        }

        _watches[0].Schedule = "daily 02:00";
        using var scheduler = Create();
        await scheduler.StartAsync();
        try
        {
            scheduler.IsRunning.Should().BeTrue();
        }
        finally
        {
            await scheduler.StopAsync();
        }

        _watches[0].Schedule = "off";
        using var off = Create();
        await off.StartAsync();
        off.IsRunning.Should().BeFalse("the only folder is off");
    }

    // ---- failures and busy repository ---------------------------------------------------------------------------------

    [Fact]
    public async Task NextRun_IsNotHeldByTheWindow_WhenTheScheduleItselfSaysSo()
    {
        _watches[0].Schedule = "daily 23:00";
        UseWindow("22:00", "06:00");
        using var scheduler = Create();
        _state.RecordGroupRunSucceeded(Group(_watchDir), _base.AddHours(-1));

        At(12);
        await scheduler.RunDueBackupsAsync();

        scheduler.NextRunUtc.Should().Be(_base.AddHours(23));
        scheduler.NextRunHeldByWindow.Should().BeFalse("23:00 is inside the window: nothing is held back");
    }

    [Fact]
    public async Task NextRun_WithoutAWindow_IsNeverHeld()
    {
        _watches[0].Schedule = "daily 23:00";
        using var scheduler = Create();
        _state.RecordGroupRunSucceeded(Group(_watchDir), _base.AddHours(-1));

        At(12);
        await scheduler.RunDueBackupsAsync();

        scheduler.NextRunHeldByWindow.Should().BeFalse();
    }

    [Fact]
    public async Task AFailedClockBackup_IsRetriedWithBackoff_NotOnEveryTick_AndNotOnlyTomorrow()
    {
        _watches[0].Schedule = "daily 02:00";
        _watches[0].StorageType = "broken";
        var broken = true;
        _configMock.Setup(c => c.CreateStorageAsync("broken"))
            .Returns(() => broken ? throw new InvalidOperationException("provider unavailable") : Task.FromResult<IStorage>(_storage));
        int Attempts() => _logger.Messages.Count(m => m.Contains("Scheduled backup failed"));

        using var scheduler = Create();
        At(2);
        (await scheduler.RunDueBackupsAsync()).DirectoriesFailed.Should().Be(1);
        Attempts().Should().Be(1);
        scheduler.NextRunUtc.Should().Be(_now.AddMinutes(5));

        _now = _base.AddMinutes(124);
        await scheduler.RunDueBackupsAsync();
        Attempts().Should().Be(1, "inside the five minute backoff");

        _now = _base.AddMinutes(125);
        await scheduler.RunDueBackupsAsync();
        Attempts().Should().Be(2);

        _now = _base.AddMinutes(134);
        await scheduler.RunDueBackupsAsync();
        Attempts().Should().Be(2, "the second retry waits ten minutes");

        _now = _base.AddMinutes(135);
        await scheduler.RunDueBackupsAsync();
        Attempts().Should().Be(3);

        // The provider comes back: the next retry succeeds and the run is then served until tomorrow.
        broken = false;
        _now = _base.AddMinutes(156);
        (await BackedUp(scheduler)).Should().Be(1);
        At(3);
        (await BackedUp(scheduler)).Should().Be(0);
        At(2.01, days: 1);
        (await BackedUp(scheduler)).Should().Be(1);
    }

    [Fact]
    public void TheClockRetryBackoff_IsCappedAtAnHour()
    {
        BackupScheduler.RetryBackoff(1, BackupScheduler.ClockRetryCap).Should().Be(TimeSpan.FromMinutes(5));
        BackupScheduler.RetryBackoff(5, BackupScheduler.ClockRetryCap).Should().Be(TimeSpan.FromMinutes(60));
        BackupScheduler.RetryBackoff(20, BackupScheduler.ClockRetryCap).Should().Be(TimeSpan.FromMinutes(60));
    }

    [Fact]
    public async Task AFailedIntervalFolderSchedule_KeepsTheExistingBackoff()
    {
        _watches[0].Schedule = "every 4h";
        _watches[0].StorageType = "broken";
        _configMock.Setup(c => c.CreateStorageAsync("broken")).ThrowsAsync(new InvalidOperationException("provider unavailable"));
        int Attempts() => _logger.Messages.Count(m => m.Contains("Scheduled backup failed"));

        using var scheduler = Create();
        At(1);
        await scheduler.RunDueBackupsAsync();
        _now = _base.AddMinutes(64);
        await scheduler.RunDueBackupsAsync();
        Attempts().Should().Be(1);
        _now = _base.AddMinutes(65);
        await scheduler.RunDueBackupsAsync();
        Attempts().Should().Be(2);
    }

    [Fact]
    public async Task ABusyRepository_DefersAClockBackup_WhichStaysDue()
    {
        _watches[0].Schedule = "daily 02:00";
        using var scheduler = Create();
        At(1);
        await BackedUp(scheduler);
        At(2.01);

        BackupCycleResult busy;
        await using (await _coordinator.AcquireMutationAsync("another operation"))
        {
            busy = await scheduler.RunDueBackupsAsync();
        }

        busy.Deferred.Should().Be(1);
        busy.HasFailures.Should().BeFalse();
        busy.DirectoriesBackedUp.Should().Be(0);

        At(2.02);
        (await BackedUp(scheduler)).Should().Be(1, "still due once the repository is free");
    }

    // ---- backup window -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task OutsideTheWindow_DueWorkWaits_AndRunsWhenItOpens()
    {
        UseWindow("22:00", "06:00");
        using var scheduler = Create();

        At(12);
        var closed = await scheduler.RunDueBackupsAsync();

        closed.DirectoriesBackedUp.Should().Be(0);
        closed.WindowClosed.Should().BeTrue();
        closed.WindowDeferred.Should().Be(1);
        closed.HasFailures.Should().BeFalse();
        _state.GetLastBackupTimeForGroup(Group(_watchDir)).Should().Be(DateTime.MinValue);
        scheduler.NextRunUtc.Should().Be(_base.AddHours(22), "the next run is when the window opens");
        scheduler.NextRunHeldByWindow.Should().BeTrue("the status bar must say the start is late only because of the quiet hours");

        _now = _base.AddHours(21).AddMinutes(59).AddSeconds(59);
        (await BackedUp(scheduler)).Should().Be(0, "one second before the window");

        At(22);
        var open = await scheduler.RunDueBackupsAsync();
        open.DirectoriesBackedUp.Should().Be(1, "the start is inclusive");
        open.WindowClosed.Should().BeFalse();
    }

    [Fact]
    public async Task TheWindowEnd_IsExclusive_SoWorkDueAtTheEndWaitsForTheNextWindow()
    {
        UseWindow("22:00", "06:00");
        using var scheduler = Create();

        At(23);
        (await BackedUp(scheduler)).Should().Be(1);

        At(6, days: 1);
        var closed = await scheduler.RunDueBackupsAsync();
        closed.DirectoriesBackedUp.Should().Be(0, "06:00 is outside");
        closed.WindowDeferred.Should().Be(1);

        At(22, days: 1);
        (await BackedUp(scheduler)).Should().Be(1, "it was only waiting");
    }

    [Fact]
    public async Task OneSecondBeforeTheWindowEnd_WorkStillStarts()
    {
        UseWindow("22:00", "06:00");
        using var scheduler = Create();

        At(23);
        (await BackedUp(scheduler)).Should().Be(1);

        _now = _base.AddDays(1).AddHours(5).AddMinutes(59).AddSeconds(59);
        (await BackedUp(scheduler)).Should().Be(1, "due and still inside the window");
    }

    [Fact]
    public async Task AClockScheduleOutsideTheWindow_RunsOnceWhenTheWindowOpens()
    {
        _watches[0].Schedule = "daily 12:00";
        UseWindow("22:00", "06:00");
        using var scheduler = Create();
        // Seed an earlier backup so only the 12:00 run is due.
        _state.RecordGroupRunSucceeded(Group(_watchDir), _base.AddHours(-1));

        At(12.5);
        var closed = await scheduler.RunDueBackupsAsync();
        closed.DirectoriesBackedUp.Should().Be(0);
        closed.WindowDeferred.Should().Be(1);
        scheduler.NextRunUtc.Should().Be(_base.AddHours(22));

        At(22.5);
        (await BackedUp(scheduler)).Should().Be(1);
        At(23);
        (await BackedUp(scheduler)).Should().Be(0, "once");
    }

    [Fact]
    public async Task TheDaysFilter_OnlyOpensWindowsThatStartOnThoseDays()
    {
        var startDay = _base.DayOfWeek.ToString()[..3].ToLowerInvariant();
        UseWindow("22:00", "06:00", startDay);
        using var scheduler = Create();

        At(23, days: 1);
        (await BackedUp(scheduler)).Should().Be(0, "the next day does not start a window");

        At(23);
        (await BackedUp(scheduler)).Should().Be(1, "the chosen day does");

        At(3, days: 1);
        (await BackedUp(scheduler)).Should().Be(1, "the morning after still belongs to that window (an hour has passed)");

        At(23, days: 1);
        (await BackedUp(scheduler)).Should().Be(0);
    }

    [Fact]
    public async Task AWholeDayWindow_OrADisabledOne_NeverBlocks()
    {
        UseWindow("00:00", "00:00");
        using var whole = Create();
        At(12);
        (await BackedUp(whole)).Should().Be(1);

        UseWindow("22:00", "06:00", enabled: false);
        At(14);
        (await BackedUp(whole)).Should().Be(1, "a disabled window is ignored");
    }

    [Fact]
    public async Task AMalformedWindow_IsIgnored_WithAWarning_SoBackupsStillRun()
    {
        _window = new BackupWindowConfig { Enabled = true, Start = "25:00", End = "06:00", Days = "all" };
        using var scheduler = Create();

        At(12);
        (await BackedUp(scheduler)).Should().Be(1);
        _logger.Messages.Should().Contain(m => m.Contains("Ignoring the invalid backupWindow"));
    }

    [Fact]
    public async Task Retention_IsHeldBackOutsideTheWindow_AndRunsWhenItOpens()
    {
        _retention.Enabled = true;
        _retention.KeepLastPerDirectory = 1;
        _retention.MaxAgeDays = 0;
        using var scheduler = Create();

        At(1);
        (await scheduler.RunDueBackupsAsync()).RetentionRan.Should().BeTrue();
        var firstRun = _state.LastRetentionRunUtc;

        UseWindow("22:00", "06:00");
        At(12, days: 2);
        var closed = await scheduler.RunDueBackupsAsync();
        closed.RetentionRan.Should().BeFalse();
        closed.WindowDeferred.Should().Be(2, "the backup and the retention pass are both waiting");
        _state.LastRetentionRunUtc.Should().Be(firstRun);

        At(22, days: 2);
        var open = await scheduler.RunDueBackupsAsync();
        open.RetentionRan.Should().BeTrue();
        open.DirectoriesBackedUp.Should().Be(1);
    }

    [Fact]
    public async Task ScheduledVerification_IsHeldBackOutsideTheWindow()
    {
        _verification.Enabled = true;
        _verification.VerifyInterval = TimeSpan.FromDays(7);
        using var scheduler = Create();

        At(1);
        (await scheduler.RunDueBackupsAsync()).SnapshotsVerified.Should().Be(1);

        UseWindow("22:00", "06:00");
        At(12, days: 8);
        var closed = await scheduler.RunDueBackupsAsync();
        closed.SnapshotsVerified.Should().Be(0);
        closed.WindowDeferred.Should().BeGreaterThanOrEqualTo(1);

        At(22, days: 8);
        (await scheduler.RunDueBackupsAsync()).SnapshotsVerified.Should().Be(1);
    }

    [Fact]
    public async Task APauseStillWins_OverTheWindow()
    {
        UseWindow("22:00", "06:00");
        var store = new ProtectionPauseStore(() => Path.Combine(_testRoot, "pause.json"));
        store.Save(ProtectionPause.Indefinite);
        using var scheduler = Create(store);

        At(23);
        var result = await scheduler.RunDueBackupsAsync();

        result.Paused.Should().BeTrue();
        result.DirectoriesBackedUp.Should().Be(0);
    }

    [Fact]
    public async Task ADueFolderIsNotLostWhenTheWindowIsClosedForSeveralDays()
    {
        UseWindow("22:00", "06:00", "sat");
        using var scheduler = Create();

        // Whatever day it is, a week of sweeps outside the window never backs up, then the Saturday evening does.
        var saturday = Enumerable.Range(0, 7).Select(d => _base.AddDays(d)).First(d => d.DayOfWeek == DayOfWeek.Saturday);
        var count = 0;
        for (var hour = 0; hour < 24 * 7; hour += 3)
        {
            _now = _base.AddHours(hour);
            if (_now >= saturday.AddHours(22) && _now < saturday.AddDays(1).AddHours(6))
            {
                continue;
            }

            count += await BackedUp(scheduler);
        }

        count.Should().Be(0);
        _now = saturday.AddHours(22);
        (await BackedUp(scheduler)).Should().Be(1);
    }

    // ---- next run ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NextRun_FollowsTheClockSchedule()
    {
        _watches[0].Schedule = "daily 02:00";
        using var scheduler = Create();
        At(1);
        await BackedUp(scheduler);

        At(1.5);
        await scheduler.RunDueBackupsAsync();
        scheduler.NextRunUtc.Should().Be(_base.AddHours(2));

        At(2.5);
        await scheduler.RunDueBackupsAsync();
        scheduler.NextRunUtc.Should().Be(_base.AddDays(1).AddHours(2));
    }

    [Fact]
    public async Task NextRun_IsTheEarliestOfAllFolders()
    {
        _watches.Add(new WatchDirectoryConfig { Path = _watchDir2, Schedule = "weekly sun 03:00" });
        _watches[0].Schedule = "daily 05:00";
        using var scheduler = Create();
        At(1);
        await BackedUp(scheduler);

        scheduler.NextRunUtc.Should().Be(_base.AddHours(5));
    }

    [Fact]
    public async Task PerGroupDueTimes_AreNotMovedByTheWindow_ButTheNextRunIs()
    {
        UseWindow("22:00", "06:00");
        using var scheduler = Create();
        At(12);
        await scheduler.RunDueBackupsAsync();

        var perGroup = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var next = scheduler.ComputeNextRunUtc(perGroup);

        perGroup[Group(_watchDir)].Should().Be(DateTime.MinValue, "never backed up");
        next.Should().Be(_base.AddHours(22));
        scheduler.EffectiveStartUtc(_base.AddHours(3), _now).Should().Be(_base.AddHours(22));
        scheduler.EffectiveStartUtc(_base.AddHours(23), _now).Should().Be(_base.AddHours(23));
        scheduler.EffectiveStartUtc(DateTime.MinValue, _now).Should().Be(_base.AddHours(22));
    }

    [Fact]
    public void EffectiveStart_WithoutAWindow_IsTheDueTimeItself()
    {
        using var scheduler = Create();
        At(1);

        scheduler.EffectiveStartUtc(_base.AddHours(-5), _now).Should().Be(_base.AddHours(-5));
        scheduler.EffectiveStartUtc(_base.AddHours(5), _now).Should().Be(_base.AddHours(5));
    }

    // ---- system backup (the due logic, on any platform) --------------------------------------------------------------

    [Fact]
    public void TheSystemBackup_UsesItsScheduleOrItsInterval()
    {
        _systemBackup.Enabled = true;
        using var scheduler = Create();
        scheduler.SystemBackupSupported = true;
        _watches[0].Schedule = "off";

        scheduler.ResolveSystemSchedule()!.Interval.Should().Be(TimeSpan.FromHours(24));

        _systemBackup.Schedule = "daily 03:00";
        scheduler.ResolveSystemSchedule()!.Expression.Should().Be("daily 03:00");

        _systemBackup.Schedule = "off";
        scheduler.ResolveSystemSchedule().Should().BeNull();

        _systemBackup.Schedule = "garbage";
        scheduler.ResolveSystemSchedule()!.Interval.Should().Be(TimeSpan.FromHours(24), "an invalid schedule falls back to the interval");
    }

    [Fact]
    public void TheSystemBackup_NextRun_FollowsItsScheduleAndTheWindow()
    {
        _systemBackup.Enabled = true;
        _systemBackup.Schedule = "daily 03:00";
        _watches[0].Schedule = "off";
        using var scheduler = Create();
        scheduler.SystemBackupSupported = true;

        At(12);
        scheduler.ComputeNextRunUtc().Should().Be(_now, "never backed up: due now");

        UseWindow("22:00", "06:00");
        scheduler.ComputeNextRunUtc().Should().Be(_base.AddHours(22));

        _systemBackup.Schedule = "off";
        scheduler.ComputeNextRunUtc().Should().BeNull();

        _systemBackup.Enabled = false;
        _systemBackup.Schedule = "daily 03:00";
        scheduler.ComputeNextRunUtc().Should().BeNull();
    }

    [Fact]
    public void OffWindows_SystemBackupIsIgnored()
    {
        _systemBackup.Enabled = true;
        _watches[0].Schedule = "off";
        using var scheduler = Create();
        scheduler.SystemBackupSupported = false;

        scheduler.ComputeNextRunUtc().Should().BeNull();
    }

    // ---- reporting: DescribeTargets and the status report -------------------------------------------------------------

    private BackupScheduler.ScheduleTarget Folder(BackupScheduler scheduler, bool paused = false, string? busy = null)
        => scheduler.DescribeTargets(_now, paused, busy).Single(t => t.Kind == "folder" && t.Path == _watchDir);

    [Fact]
    public async Task DescribeTargets_ExplainsEachState()
    {
        _watches[0].Schedule = "daily 02:00";
        using var scheduler = Create();

        At(1);
        var never = Folder(scheduler);
        never.State.Should().Be("due");
        never.Reason.Should().Contain("Never done");
        never.Source.Should().Be("folder");
        never.Expression.Should().Be("daily 02:00");

        await BackedUp(scheduler);
        At(1.5);
        var scheduled = Folder(scheduler);
        scheduled.State.Should().Be("scheduled");
        scheduled.NextStartUtc.Should().Be(_base.AddHours(2));
        scheduled.DueUtc.Should().Be(_base.AddHours(2));
        scheduled.LastUtc.Should().NotBeNull();
        scheduled.Reason.Should().BeNull();

        At(2.5);
        Folder(scheduler).State.Should().Be("due");
        Folder(scheduler, paused: true).State.Should().Be("paused");
        var busy = Folder(scheduler, busy: "another ReStore process");
        busy.State.Should().Be("busy");
        busy.Reason.Should().Contain("another ReStore process");
    }

    [Fact]
    public void DescribeTargets_WithAClosedWindow_SaysWaitingForWindow_AndWhenItOpens()
    {
        UseWindow("22:00", "06:00");
        using var scheduler = Create();
        At(12);

        var folder = Folder(scheduler);

        folder.State.Should().Be("waiting-for-window");
        folder.NextStartUtc.Should().Be(_base.AddHours(22));
        folder.Reason.Should().Contain("backup window is closed").And.Contain(":00");
    }

    [Fact]
    public void DescribeTargets_ADueTimeThatFallsOutsideTheWindow_StartsWhenItOpens()
    {
        _watches[0].Schedule = "daily 12:00";
        UseWindow("22:00", "06:00");
        using var scheduler = Create();
        _state.RecordGroupRunSucceeded(Group(_watchDir), _base.AddHours(-1));

        At(11);
        var folder = Folder(scheduler);

        folder.State.Should().Be("scheduled");
        folder.DueUtc.Should().Be(_base.AddHours(12));
        folder.NextStartUtc.Should().Be(_base.AddHours(22));
        folder.Reason.Should().Contain("window is closed then");
    }

    [Fact]
    public void DescribeTargets_OffMissingAndDisabledTargets()
    {
        _watches.Add(new WatchDirectoryConfig { Path = Path.Combine(_testRoot, "gone") });
        _watches[0].Schedule = "off";
        using var scheduler = Create();
        At(1);

        var targets = scheduler.DescribeTargets(_now);

        var off = targets.Single(t => t.Path == _watchDir);
        off.State.Should().Be("off");
        off.Expression.Should().Be("off");
        off.NextStartUtc.Should().BeNull();
        targets.Single(t => t.Path?.EndsWith("gone", StringComparison.Ordinal) == true).State.Should().Be("folder-missing");
        targets.Single(t => t.Kind == "system").State.Should().Be("disabled");
        targets.Single(t => t.Kind == "verification").State.Should().Be("disabled");
        targets.Single(t => t.Kind == "retention").State.Should().Be("disabled");
    }

    [Fact]
    public async Task DescribeTargets_Verification_IsDueOnceItsIntervalHasPassed()
    {
        _verification.Enabled = true;
        _verification.VerifyInterval = TimeSpan.FromDays(7);
        using var scheduler = Create();
        At(1);
        await scheduler.RunDueBackupsAsync();

        At(2);
        var verification = scheduler.DescribeTargets(_now).Single(t => t.Kind == "verification");
        verification.State.Should().Be("scheduled");
        verification.NextStartUtc.Should().BeAfter(_base.AddDays(6));

        At(0, days: 8);
        scheduler.DescribeTargets(_now).Single(t => t.Kind == "verification").State.Should().Be("due");
    }

    [Fact]
    public void DescribeTargets_TheSystemBackup_FollowsItsScheduleAndTheWindow()
    {
        _systemBackup.Enabled = true;
        _systemBackup.Schedule = "daily 03:00";
        _watches[0].Schedule = "off";
        using var scheduler = Create();
        scheduler.SystemBackupSupported = true;
        UseWindow("22:00", "06:00");
        At(12);

        var system = scheduler.DescribeTargets(_now).Single(t => t.Kind == "system");

        system.Expression.Should().Be("daily 03:00");
        system.Source.Should().Be("systemBackup.schedule");
        system.State.Should().Be("waiting-for-window", "never backed up, so due, but the window is closed");
        system.NextStartUtc.Should().Be(_base.AddHours(22));
        scheduler.ComputeSystemNextDueUtc(_now).Should().Be(_base.AddHours(22));
    }

    [Fact]
    public void TheStatusReport_ShowsSchedulesTheWindowAndStaleness()
    {
        _watches.Add(new WatchDirectoryConfig { Path = _watchDir2, Schedule = "off" });
        _watches[0].Schedule = "weekdays 18:30";
        _systemBackup.Schedule = "weekly sun 03:00";
        UseWindow("22:00", "06:00");
        using var scheduler = Create();
        At(12);

        var status = ProtectionStatusBuilder.Build(_configMock.Object, _state, scheduler, _now);

        status.Window.Active.Should().BeTrue();
        status.Window.OpenNow.Should().BeFalse();
        status.Window.NextOpenUtc.Should().Be(_base.AddHours(22));
        status.Window.Start.Should().Be("22:00");
        status.SystemBackupSchedule.Should().Be("weekly sun 03:00");
        status.SystemBackupScheduleDescription.Should().Be("Every Sunday at 03:00");

        var weekdays = status.Groups.Single(g => g.Path == _watchDir);
        weekdays.Schedule.Should().Be("weekdays 18:30");
        weekdays.ScheduleSource.Should().Be("folder");
        weekdays.ScheduleDescription.Should().Be("Every weekday at 18:30");
        weekdays.Overdue.Should().BeTrue("never backed up");
        weekdays.WaitingForWindow.Should().BeTrue();

        var off = status.Groups.Single(g => g.Path == _watchDir2);
        off.Schedule.Should().Be("off");
        off.Overdue.Should().BeFalse();
        off.NextDueUtc.Should().BeNull();
        off.Stale.Should().BeFalse();
    }

    [Fact]
    public async Task TheStatusReport_WithoutSchedulesOrWindow_ReportsTheDefaultInterval()
    {
        using var scheduler = Create();
        At(1);
        await scheduler.RunDueBackupsAsync();

        var status = ProtectionStatusBuilder.Build(_configMock.Object, _state, scheduler, _now);

        status.Window.Configured.Should().BeFalse();
        status.Window.OpenNow.Should().BeTrue();
        var group = status.Groups.Single();
        group.Schedule.Should().BeNull();
        group.ScheduleSource.Should().Be("default");
        group.ScheduleDescription.Should().Be("Every hour");
        group.NextDueUtc.Should().NotBeNull();
        group.WaitingForWindow.Should().BeFalse();
    }
}
