using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.monitoring;

/// <summary>
/// Runs periodic backups per <c>backupInterval</c> and <c>systemBackup.backupInterval</c>, or per the clock schedules
/// (<c>watchDirectories[].schedule</c>, <c>systemBackup.schedule</c>), and only inside the <c>backupWindow</c> when one is set.
/// Complements <see cref="FileWatcher"/>, which only sees changes made while it runs;
/// this catches anything changed while the machine was asleep or the app was closed.
/// Due times come from the last recorded backup, so restarting doesn't reset the clock.
/// </summary>
/// <summary>Outcome of one scheduler sweep, for hosts that surface failures to the user.</summary>
public sealed class BackupCycleResult
{
    public int DirectoriesBackedUp { get; set; }
    public int DirectoriesFailed { get; set; }
    /// <summary>Files (or directories) skipped across this cycle's backups, for "completed with N skipped" reporting.</summary>
    public int FilesSkipped { get; set; }
    public bool SystemBackupRan { get; set; }
    public bool SystemBackupFailed { get; set; }
    public int SnapshotsVerified { get; set; }
    public int VerificationsFailed { get; set; }

    /// <summary>Operations that were due but found the repository busy; they stay due for the next sweep. Not failures.</summary>
    public int Deferred { get; set; }

    /// <summary>True when this sweep applied the daily retention pass.</summary>
    public bool RetentionRan { get; set; }

    /// <summary>
    /// True when the retention pass failed. Kept apart from <see cref="HasFailures"/>: retention is housekeeping and
    /// never notifies the user, but a one-shot run (<c>run-due</c>) still reports it.
    /// </summary>
    public bool RetentionFailed { get; set; }

    /// <summary>True when the sweep did not run because another sweep of the same scheduler was still running.</summary>
    public bool SkippedBecauseRunning { get; set; }

    /// <summary>True when the backup window (quiet hours) is closed at the start of this sweep: nothing automatic may start until it opens.</summary>
    public bool WindowClosed { get; set; }

    /// <summary>Operations that were due but not started because the backup window is closed; they run once it opens. Not failures.</summary>
    public int WindowDeferred { get; set; }

    /// <summary>True when protection is paused (<c>restore pause</c>, the tray menu): nothing was started and nothing is due-and-lost, the work resumes with the pause.</summary>
    public bool Paused { get; set; }

    public List<string> FailureMessages { get; } = [];
    public List<string> VerificationFailureMessages { get; } = [];

    public bool DidWork => DirectoriesBackedUp > 0 || SystemBackupRan || SnapshotsVerified > 0;
    public bool HasFailures => DirectoriesFailed > 0 || SystemBackupFailed || VerificationsFailed > 0;
}

public class BackupScheduler : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    // A sub-minute sweep of every watched directory would keep the disk permanently busy.
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    private readonly IConfigManager _configManager;
    private readonly ILogger _logger;
    private readonly SystemState _systemState;
    private readonly SizeAnalyzer _sizeAnalyzer;
    private readonly IPasswordProvider? _passwordProvider;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeZoneInfo _timeZone;
    private readonly RepositoryCoordinator _coordinator;

    // When set, the pause file is read at the start of every sweep and before every directory, so a pause written by another
    // process (CLI, the app's tray) stops this scheduler (run-due, --service) at the next decision, no restart needed.
    private readonly ProtectionPauseStore? _pauseStore;

    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    // Backup records no history when nothing changed, which would leave such a directory
    // permanently "due" and rescanned every tick. History still drives decisions across
    // restarts; this only suppresses repeat work within one run.
    private readonly Dictionary<string, DateTime> _lastAttemptUtc = new(StringComparer.OrdinalIgnoreCase);

    // Consecutive failed scheduled backups per group, driving the retry backoff (see RetryBackoff).
    private readonly Dictionary<string, int> _failureCount = new(StringComparer.OrdinalIgnoreCase);
    private int _systemFailureCount;

    // Clock-scheduled targets cannot back-date an attempt like an interval can: a failed one waits for this time (see RetryBackoff)
    // while its scheduled run stays unserved, so it is retried after the backoff and not on every tick.
    private readonly Dictionary<string, DateTime> _retryAfterUtc = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _systemRetryAfterUtc = DateTime.MinValue;

    // Warnings about hand-edited schedules are logged once per distinct problem, not every minute.
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    /// <summary>Longest wait between retries of a failed clock-scheduled backup (a daily backup that fails is retried hourly, not once a day).</summary>
    internal static readonly TimeSpan ClockRetryCap = TimeSpan.FromHours(1);

    /// <summary>Whether the system backup exists on this platform; tests switch it off/on to exercise the due logic on any OS (it never runs off Windows).</summary>
    internal bool SystemBackupSupported { get; set; } = OperatingSystem.IsWindows();

    // Groups whose last verification attempt threw (unreachable provider, ...): retried after
    // VerifyRetryDelay instead of waiting a full verify interval. In-memory by design; the attempt
    // time itself is persisted (SystemState.SnapshotVerificationTimes) so a restart never re-triggers.
    private readonly HashSet<string> _verifyFailedGroups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>First retry delay after a failed scheduled backup; doubles per consecutive failure up to the interval.</summary>
    internal static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(5);

    /// <summary>Retry delay after a verification attempt that threw (capped at the verify interval).</summary>
    internal static readonly TimeSpan VerifyRetryDelay = TimeSpan.FromHours(1);

    private Task? _loop;
    private bool _isDisposed;

    private DateTime _lastSystemBackupUtc = DateTime.MinValue;

    // In-memory: spaces out retries of a failing retention pass (the successful run time is persisted in the state).
    private DateTime _lastRetentionAttemptUtc = DateTime.MinValue;

    // Drives the "first success after a run of failures" notification.
    private bool _lastCycleHadFailures;

    /// <param name="utcNow">
    /// Test clock override. SystemState stamps history with the real UTC clock, so a
    /// substitute must stay anchored near real time for due-time comparisons to hold.
    /// </param>
    public BackupScheduler(
        IConfigManager configManager,
        ILogger logger,
        SystemState systemState,
        SizeAnalyzer sizeAnalyzer,
        IPasswordProvider? passwordProvider = null,
        Func<DateTime>? utcNow = null,
        RepositoryCoordinator? coordinator = null,
        ProtectionPauseStore? pauseStore = null,
        TimeZoneInfo? timeZone = null)
    {
        _timeZone = timeZone ?? TimeZoneInfo.Local;
        _pauseStore = pauseStore;
        _coordinator = coordinator ?? RepositoryCoordinator.Instance;
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _systemState = systemState ?? throw new ArgumentNullException(nameof(systemState));
        _sizeAnalyzer = sizeAnalyzer ?? throw new ArgumentNullException(nameof(sizeAnalyzer));
        _passwordProvider = passwordProvider;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    private long _nextRunTicks;
    private int _nextRunHeldByWindow;

    /// <summary>
    /// Earliest time any watched directory or the system backup next becomes due, as of the last
    /// sweep (never earlier than that sweep); null before the first sweep or when nothing is scheduled.
    /// </summary>
    public DateTime? NextRunUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _nextRunTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    /// <summary>
    /// True when <see cref="NextRunUtc"/> is later than the work's own due time only because the backup window is closed until then
    /// ("quiet hours"); false when it is simply when the schedule says. Same moment of computation as <see cref="NextRunUtc"/>.
    /// </summary>
    public bool NextRunHeldByWindow => Volatile.Read(ref _nextRunHeldByWindow) != 0;

    /// <summary>Raised after a sweep that did work, so a host can refresh its UI.</summary>
    public event Action? BackupCycleCompleted;

    /// <summary>
    /// Raised after every sweep that did work, carrying what succeeded and what failed.
    /// Fires alongside <see cref="BackupCycleCompleted"/>.
    /// </summary>
    public event Action<BackupCycleResult>? BackupCycleFinished;

    /// <summary>Raised on the first successful sweep after one or more failing sweeps.</summary>
    public event Action<BackupCycleResult>? BackupRecovered;

    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (IsRunning)
        {
            _logger.Log("Backup scheduler is already running.", LogLevel.Warning);
            return Task.CompletedTask;
        }

        // backupInterval <= 0 disables only the interval file backups; system backup and verification
        // have their own settings and keep running.
        var interval = ResolveUserBackupInterval();
        var folderSchedules = _configManager.WatchDirectories.Count(w => !string.IsNullOrWhiteSpace(w.Path) && ResolveFolderSchedule(w) != null);
        if (folderSchedules == 0 && !HasSystemBackupSchedule() && !HasVerificationSchedule())
        {
            _logger.Log(
                "Backup scheduler not started: backupInterval, the folder and system schedules, systemBackup.backupInterval and verification.verifyInterval are all disabled or unusable.",
                LogLevel.Warning);
            return Task.CompletedTask;
        }

        var customSchedules = _configManager.WatchDirectories.Count(w => !string.IsNullOrWhiteSpace(w.Schedule));
        var window = CurrentWindow();
        _logger.Log(
            $"Backup scheduler started. User-file interval: {(interval?.ToString() ?? "disabled")}" +
            (customSchedules > 0 ? $" ({customSchedules} folder(s) with their own schedule)" : string.Empty) + ". " +
            $"System backup: {DescribeSystemBackupInterval()}. " +
            $"Verification interval: {DescribeVerificationInterval()}." +
            (window != null ? $" Backup window: {window.Describe()}." : string.Empty),
            LogLevel.Info);

        _loop = RunLoopAsync(_shutdown.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_loop == null)
        {
            return;
        }

        if (!_shutdown.IsCancellationRequested)
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        finally
        {
            _loop = null;
            _logger.Log("Backup scheduler stopped.", LogLevel.Info);
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        // So StartAsync returns without sweeping inline.
        await Task.Yield();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RunDueBackupsAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Log and retry next tick rather than killing the scheduler.
                    _logger.Log($"Scheduled backup cycle failed: {ex.Message}", LogLevel.Error);
                }

                await Task.Delay(TickInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    /// <summary>
    /// One scheduler sweep, exactly what each tick of the running scheduler does: due interval backups, the due system
    /// backup, due verification and the daily retention pass. Used by <c>restore.exe run-due</c> (Task Scheduler) so the
    /// command and the background scheduler never disagree about what is due.
    /// </summary>
    public Task<BackupCycleResult> RunDueOnceAsync(CancellationToken cancellationToken = default)
        => RunDueBackupsAsync(cancellationToken);

    internal async Task<BackupCycleResult> RunDueBackupsAsync(CancellationToken cancellationToken = default)
    {
        var result = new BackupCycleResult();

        // A long backup must not be re-entered by the next tick.
        if (!await _runLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.Log("Scheduled backup skipped: a previous cycle is still running.", LogLevel.Debug);
            result.SkippedBecauseRunning = true;
            return result;
        }

        try
        {
            if (IsProtectionPaused())
            {
                _logger.Log("Scheduled sweep skipped: protection is paused.", LogLevel.Info);
                result.Paused = true;
                return result;
            }

            result.WindowClosed = !IsWindowOpen(_utcNow());
            if (result.WindowClosed)
            {
                _logger.Log($"Backup window is closed ({CurrentWindow()?.Describe()}); due work waits for it to open.", LogLevel.Debug);
            }

            await RunDueUserBackupsAsync(result, cancellationToken).ConfigureAwait(false);
            await RunDueSystemBackupAsync(result, cancellationToken).ConfigureAwait(false);
            await RunDueVerificationAsync(result, cancellationToken).ConfigureAwait(false);
            await RunDueRetentionAsync(result, cancellationToken).ConfigureAwait(false);

            UpdateNextRunUtc();

            if (result.DidWork || result.HasFailures)
            {
                if (result.DidWork)
                {
                    BackupCycleCompleted?.Invoke();
                }

                BackupCycleFinished?.Invoke(result);

                if (_lastCycleHadFailures && !result.HasFailures)
                {
                    BackupRecovered?.Invoke(result);
                }

                _lastCycleHadFailures = result.HasFailures;
            }

            return result;
        }
        finally
        {
            _runLock.Release();
        }
    }

    private bool IsProtectionPaused() => _pauseStore?.LoadActive(_utcNow()) != null;

    private void UpdateNextRunUtc()
    {
        var next = ComputeNextRun(null);
        Interlocked.Exchange(ref _nextRunTicks, next.Next?.Ticks ?? 0);
        Interlocked.Exchange(ref _nextRunHeldByWindow, next.HeldByWindow ? 1 : 0);
    }

    // ---- schedules and the backup window ---------------------------------------------------------------------------

    /// <summary>
    /// The schedule that applies to a watched folder: its own <c>schedule</c>, else the global <c>backupInterval</c>. Null when
    /// the folder is not scheduled (<c>off</c>, or <c>backupInterval</c> disabled). An unparsable <c>schedule</c> (hand-edited
    /// file; the validator reports it) falls back to the interval so the folder stays protected.
    /// </summary>
    internal BackupSchedule? ResolveFolderSchedule(WatchDirectoryConfig watch)
    {
        if (!string.IsNullOrWhiteSpace(watch.Schedule))
        {
            if (BackupSchedule.TryParse(watch.Schedule, out var own, out var error))
            {
                return own.IsOff ? null : own;
            }

            WarnOnce($"Ignoring the invalid schedule '{watch.Schedule}' of '{watch.Path}' ({error}); backupInterval applies.");
        }

        var interval = ResolveUserBackupInterval();
        return interval == null ? null : BackupSchedule.FromInterval(interval.Value);
    }

    /// <summary>The schedule of the system backup: <c>systemBackup.schedule</c>, else <c>systemBackup.backupInterval</c>; null when off.</summary>
    internal BackupSchedule? ResolveSystemSchedule()
    {
        var systemBackup = _configManager.SystemBackup;
        if (systemBackup == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(systemBackup.Schedule))
        {
            if (BackupSchedule.TryParse(systemBackup.Schedule, out var own, out var error))
            {
                return own.IsOff ? null : own;
            }

            WarnOnce($"Ignoring the invalid systemBackup.schedule '{systemBackup.Schedule}' ({error}); systemBackup.backupInterval applies.");
        }

        var interval = Normalize(systemBackup.BackupInterval);
        return interval == null ? null : BackupSchedule.FromInterval(interval.Value);
    }

    private void WarnOnce(string message)
    {
        if (_warned.Add(message))
        {
            _logger.Log(message, LogLevel.Warning);
        }
    }

    /// <summary>The configured window; null when there is none, it is switched off or it is malformed (never blocks work).</summary>
    internal BackupWindow? CurrentWindow()
    {
        var window = BackupWindow.FromConfig(_configManager.BackupWindow, out var error);
        if (error != null)
        {
            WarnOnce($"Ignoring the invalid backupWindow ({error}); automatic backups are not restricted.");
        }

        return window;
    }

    internal bool IsWindowOpen(DateTime nowUtc) => CurrentWindow()?.IsOpen(nowUtc, _timeZone) ?? true;

    /// <summary>
    /// When work due at <paramref name="dueUtc"/> actually starts: unchanged without a window or when the window is open then,
    /// otherwise the moment the window next opens (never earlier than <paramref name="nowUtc"/>). Not clamped to now otherwise.
    /// </summary>
    internal DateTime EffectiveStartUtc(DateTime dueUtc, DateTime nowUtc)
    {
        var window = CurrentWindow();
        if (window == null)
        {
            return dueUtc;
        }

        return dueUtc < nowUtc
            ? (window.IsOpen(nowUtc, _timeZone) ? dueUtc : window.NextOpenAtOrAfter(nowUtc, _timeZone))
            : window.NextOpenAtOrAfter(dueUtc, _timeZone);
    }

    /// <summary>The later of the last recorded backup and the last attempt (<see cref="DateTime.MinValue"/> for none).</summary>
    private DateTime FolderLastActivity(BackupSchedule schedule, string group)
    {
        // A clock schedule counts any successful run, including one that found nothing to back up (which records no history):
        // otherwise run-due, a fresh process each time, would rescan a quiet folder on every invocation after its scheduled time.
        var lastBackup = schedule.IsClock ? _systemState.GetLastSuccessfulRunUtc(group) : _systemState.GetLastBackupTimeForGroup(group);
        return _lastAttemptUtc.TryGetValue(group, out var lastAttempt) && lastAttempt > lastBackup ? lastAttempt : lastBackup;
    }

    /// <summary>When a folder is due, unclamped (<see cref="DateTime.MinValue"/> = never backed up); null when it never will be.</summary>
    private DateTime? FolderDueUtc(BackupSchedule schedule, string group, DateTime now)
    {
        var due = schedule.DueUtc(FolderLastActivity(schedule, group), now, _timeZone);
        if (schedule.IsClock && due != null && _retryAfterUtc.TryGetValue(group, out var retryAfter) && retryAfter > due)
        {
            due = retryAfter;
        }

        return due;
    }

    private DateTime? SystemDueUtc(BackupSchedule schedule, DateTime now)
    {
        var due = schedule.DueUtc(ResolveLastSystemBackupUtc(), now, _timeZone);
        if (schedule.IsClock && due != null && _systemRetryAfterUtc > due)
        {
            due = _systemRetryAfterUtc;
        }

        return due;
    }

    /// <summary>
    /// Earliest time a watched directory or the system backup next starts (never earlier than now; later when the backup window is
    /// closed when it falls due), or null when nothing is scheduled. <paramref name="perGroup"/> receives each scheduled watched
    /// directory's own due time, not clamped to now and not moved by the window (<see cref="DateTime.MinValue"/> = never backed up).
    /// </summary>
    internal DateTime? ComputeNextRunUtc(Dictionary<string, DateTime>? perGroup = null) => ComputeNextRun(perGroup).Next;

    /// <summary>
    /// <see cref="ComputeNextRunUtc"/> plus whether the earliest start was pushed back by a closed backup window (the window opens
    /// later than the work's own due time).
    /// </summary>
    internal (DateTime? Next, bool HeldByWindow) ComputeNextRun(Dictionary<string, DateTime>? perGroup)
    {
        var now = _utcNow();
        DateTime? next = null;
        var held = false;

        void Consider(DateTime due)
        {
            var start = EffectiveStartUtc(due, now);
            var candidate = start < now ? now : start;
            if (next == null || candidate < next)
            {
                next = candidate;
                held = start > due;
            }
        }

        foreach (var watchDirectory in _configManager.WatchDirectories.ToList())
        {
            if (string.IsNullOrWhiteSpace(watchDirectory.Path) || !Directory.Exists(watchDirectory.ExpandedPath))
            {
                continue;
            }

            var schedule = ResolveFolderSchedule(watchDirectory);
            if (schedule == null)
            {
                continue;
            }

            var group = NormalizeGroup(watchDirectory.ExpandedPath);
            if (FolderDueUtc(schedule, group, now) is not { } groupDue)
            {
                continue;
            }

            if (perGroup != null)
            {
                perGroup[group] = groupDue;
            }

            Consider(groupDue);
        }

        var systemBackup = _configManager.SystemBackup;
        if (SystemBackupSupported && systemBackup is { Enabled: true } && ResolveSystemSchedule() is { } systemSchedule
            && SystemDueUtc(systemSchedule, now) is { } systemDue)
        {
            Consider(systemDue);
        }

        return (next, held);
    }

    private async Task RunDueUserBackupsAsync(BackupCycleResult result, CancellationToken cancellationToken)
    {
        var now = _utcNow();

        foreach (var watchDirectory in _configManager.WatchDirectories.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(watchDirectory.Path) || !Directory.Exists(watchDirectory.ExpandedPath))
            {
                continue;
            }

            // Paused while an earlier directory was being backed up: leave the rest due.
            if (IsProtectionPaused())
            {
                result.Paused = true;
                return;
            }

            var schedule = ResolveFolderSchedule(watchDirectory);
            if (schedule == null)
            {
                continue;
            }

            var group = NormalizeGroup(watchDirectory.ExpandedPath);

            // Later of recorded backup and attempted sweep; see _lastAttemptUtc.
            var lastActivity = FolderLastActivity(schedule, group);
            if (FolderDueUtc(schedule, group, now) is not { } due || due > now)
            {
                continue;
            }

            // A backup that already started may finish after the window closes, but no new one starts outside it. Checked per folder
            // so a long sweep stops starting folders once the window has closed. The folder stays due and runs when it opens.
            if (!IsWindowOpen(_utcNow()))
            {
                result.WindowClosed = true;
                result.WindowDeferred++;
                _logger.Log($"Scheduled backup for '{watchDirectory.Path}' is due but the backup window is closed; waiting for it to open.", LogLevel.Debug);
                continue;
            }

            var reason = lastActivity == DateTime.MinValue
                ? "never backed up"
                : schedule.IsClock
                    ? $"scheduled run missed or reached, last backup {FormatAge(now - lastActivity)} ago ({schedule.Description})"
                    : $"last backup {FormatAge(now - lastActivity)} ago";

            _logger.Log($"Scheduled backup due for '{watchDirectory.Path}' ({reason}).", LogLevel.Info);

            // Before the attempt, so a throw can't cause a tight retry loop next tick.
            _lastAttemptUtc[group] = now;

            try
            {
                var backup = new Backup(_logger, _systemState, _sizeAnalyzer, _configManager, _passwordProvider, _coordinator) { FailFastWhenBusy = true };
                var backupResult = await backup.BackupDirectoryAsync(watchDirectory.ExpandedPath, watchDirectory.StorageType, null, cancellationToken).ConfigureAwait(false);
                _failureCount.Remove(group);
                _retryAfterUtc.Remove(group);
                result.DirectoriesBackedUp++;
                result.FilesSkipped += backupResult.Skipped.Count;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (RepositoryBusyException ex)
            {
                // Not a failure: another operation holds the repository. Forget the attempt so the
                // directory stays due and is retried on the next tick.
                _logger.Log($"Scheduled backup for '{watchDirectory.Path}' deferred: {ex.Message}", LogLevel.Info);
                _lastAttemptUtc.Remove(group);
                result.Deferred++;
            }
            catch (Exception ex)
            {
                // One unreachable provider should not stall the others.
                _logger.Log(
                    $"Scheduled backup failed for '{watchDirectory.Path}': {ex.Message}",
                    LogLevel.Error);

                // Retry sooner than a full interval.
                _failureCount[group] = _failureCount.GetValueOrDefault(group) + 1;
                TimeSpan retryIn;
                if (schedule.IsClock)
                {
                    // The scheduled run stays unserved (no attempt recorded) and is retried once the backoff has passed.
                    retryIn = RetryBackoff(_failureCount[group], ClockRetryCap);
                    _lastAttemptUtc.Remove(group);
                    _retryAfterUtc[group] = now + retryIn;
                }
                else
                {
                    // Back-dated so the normal due check fires after the backoff.
                    retryIn = RetryBackoff(_failureCount[group], schedule.Interval!.Value);
                    _lastAttemptUtc[group] = now - schedule.Interval.Value + retryIn;
                }

                _logger.Log($"Will retry '{watchDirectory.Path}' in {FormatAge(retryIn)}.", LogLevel.Info);

                result.DirectoriesFailed++;
                result.FailureMessages.Add($"{Path.GetFileName(watchDirectory.Path)}: {ex.Message}");
            }
        }
    }

    /// <summary>How often the scheduler applies retention to every group.</summary>
    internal static readonly TimeSpan RetentionInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// Applies retention (which also retries queued chunk deletes) to every group once a day. The last run time is
    /// persisted in the system state so a restart does not repeat it. A failure or a busy repository is retried on
    /// a later tick, not recorded as a run.
    /// </summary>
    private async Task RunDueRetentionAsync(BackupCycleResult result, CancellationToken cancellationToken)
    {
        if (!_configManager.Retention.Enabled)
        {
            return;
        }

        var now = _utcNow();
        var last = _systemState.LastRetentionRunUtc;
        if (last != DateTime.MinValue && now - last < RetentionInterval)
        {
            return;
        }

        if (_systemState.GetTotalBackupCount() == 0 || now - _lastRetentionAttemptUtc < VerifyRetryDelay)
        {
            return;
        }

        if (!IsWindowOpen(now))
        {
            result.WindowClosed = true;
            result.WindowDeferred++;
            _logger.Log("Scheduled retention is due but the backup window is closed; waiting for it to open.", LogLevel.Debug);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _logger.Log("Scheduled retention due.", LogLevel.Info);
        _lastRetentionAttemptUtc = now;

        try
        {
            var retention = new RetentionManager(_logger, _configManager, _systemState, _coordinator);
            await retention.ApplyAllAsync(cancellationToken, busyTimeout: TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            // Under the lease: taking it reloads state another process changed, which would discard an earlier assignment.
            await _coordinator.MutateStateAsync(_systemState, "Saving retention run", () =>
            {
                _systemState.LastRetentionRunUtc = now;
                return true;
            }, cancellationToken).ConfigureAwait(false);
            result.RetentionRan = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RepositoryBusyException ex)
        {
            _logger.Log($"Scheduled retention deferred: {ex.Message}", LogLevel.Info);
            _lastRetentionAttemptUtc = DateTime.MinValue;
            result.Deferred++;
        }
        catch (Exception ex)
        {
            // Unlike backups this is housekeeping: the next tick tries again and nothing is surfaced to the user.
            _logger.Log($"Scheduled retention failed: {ex.Message}", LogLevel.Warning);
            result.RetentionFailed = true;
        }
    }

    private async Task RunDueSystemBackupAsync(BackupCycleResult result, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !SystemBackupSupported)
        {
            return;
        }

        var systemBackup = _configManager.SystemBackup;
        if (systemBackup is not { Enabled: true })
        {
            return;
        }

        if (!systemBackup.IncludePrograms
            && !systemBackup.IncludeEnvironmentVariables
            && !systemBackup.IncludeWindowsSettings
            && !systemBackup.IncludeDevTools)
        {
            return;
        }

        var schedule = ResolveSystemSchedule();
        if (schedule == null)
        {
            return;
        }

        var now = _utcNow();
        var lastRun = ResolveLastSystemBackupUtc();

        if (SystemDueUtc(schedule, now) is not { } due || due > now)
        {
            return;
        }

        if (!IsWindowOpen(_utcNow()))
        {
            result.WindowClosed = true;
            result.WindowDeferred++;
            _logger.Log("Scheduled system backup is due but the backup window is closed; waiting for it to open.", LogLevel.Debug);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var reason = lastRun == DateTime.MinValue
            ? "never backed up"
            : $"last backup {FormatAge(now - lastRun)} ago";

        _logger.Log($"Scheduled system backup due ({reason}).", LogLevel.Info);

        try
        {
            var manager = new SystemBackupManager(_logger, _configManager, _systemState, _passwordProvider, _coordinator) { FailFastWhenBusy = true };
            await manager.BackupSystemAsync().ConfigureAwait(false);
            _systemFailureCount = 0;
            _systemRetryAfterUtc = DateTime.MinValue;
            _lastSystemBackupUtc = now;
            result.SystemBackupRan = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RepositoryBusyException ex)
        {
            // Not a failure and not an attempt: stay due and retry on the next tick.
            _logger.Log($"Scheduled system backup deferred: {ex.Message}", LogLevel.Info);
            result.Deferred++;
        }
        catch (Exception ex)
        {
            _logger.Log($"Scheduled system backup failed: {ex.Message}", LogLevel.Error);

            // Mark the attempt (back-dated so the retry comes after the backoff, not a full interval)
            // so a persistent failure neither retries every tick nor waits a whole interval.
            _systemFailureCount++;
            if (schedule.IsClock)
            {
                _systemRetryAfterUtc = now + RetryBackoff(_systemFailureCount, ClockRetryCap);
            }
            else
            {
                _lastSystemBackupUtc = now - schedule.Interval!.Value + RetryBackoff(_systemFailureCount, schedule.Interval.Value);
            }

            result.SystemBackupFailed = true;
            result.FailureMessages.Add($"System backup: {ex.Message}");
        }
    }

    /// <summary>
    /// Verifies snapshots per group on the configured interval. Opt-in, and local-only by
    /// default, because each run re-downloads every unique chunk. The newest snapshot is
    /// always checked; any remaining per-run budget rotates through older snapshots so an
    /// older restore point is eventually covered rather than never looked at.
    /// </summary>
    private async Task RunDueVerificationAsync(BackupCycleResult result, CancellationToken cancellationToken)
    {
        var verification = _configManager.Verification;
        if (verification is not { Enabled: true })
        {
            return;
        }

        var interval = Normalize(verification.VerifyInterval);
        if (interval == null)
        {
            return;
        }

        var now = _utcNow();

        // An unparsable mode (hand-edited config) falls back to full, the safest; the validator reports it.
        VerificationModes.TryParse(verification.Mode, out var verifyMode);
        var verifyOptions = new VerificationOptions(verifyMode, Math.Clamp(verification.SamplePercent, 1, 100));

        foreach (var group in _systemState.GetBackupGroups())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var snapshots = _systemState.GetBackupsForGroup(group)
                .Where(backup => backup.ArtifactType == BackupArtifactType.SnapshotManifest
                    && !string.IsNullOrWhiteSpace(backup.Path))
                .OrderByDescending(backup => backup.Timestamp)
                .ToList();

            if (snapshots.Count == 0)
            {
                continue;
            }

            // Last attempt per group comes from the persisted per-snapshot verification times (recorded
            // before each attempt, so failures count too): an app restart must not re-trigger a verify,
            // and a persistently failing one must not re-run every tick.
            var lastVerified = snapshots.Max(snapshot => _systemState.GetSnapshotVerifiedUtc(snapshot.Path));
            var dueAfter = _verifyFailedGroups.Contains(group)
                ? (interval.Value < VerifyRetryDelay ? interval.Value : VerifyRetryDelay)
                : interval.Value;
            if (lastVerified != DateTime.MinValue && now - lastVerified < dueAfter)
            {
                continue;
            }

            var selected = SelectSnapshotsToVerify(snapshots, verification.SnapshotsPerRun);
            if (selected.Count == 0)
            {
                continue;
            }

            if (!IsWindowOpen(_utcNow()))
            {
                result.WindowClosed = true;
                result.WindowDeferred++;
                _logger.Log($"Scheduled verification for '{group}' is due but the backup window is closed; waiting for it to open.", LogLevel.Debug);
                continue;
            }

            var attemptedAny = false;
            var attemptThrew = false;

            foreach (var snapshot in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var storageType = string.IsNullOrWhiteSpace(snapshot.StorageType)
                    ? _configManager.GlobalStorageType
                    : snapshot.StorageType;

                if (verification.LocalStorageOnly
                    && !string.Equals(storageType, "local", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                attemptedAny = true;
                _systemState.RecordSnapshotVerified(snapshot.Path, now);

                _logger.Log($"Scheduled verification due for '{group}' (snapshot {snapshot.SnapshotId}, {VerificationModes.Name(verifyOptions.Mode)} mode).", LogLevel.Info);

                try
                {
                    using var storage = await _configManager.CreateStorageAsync(storageType).ConfigureAwait(false);
                    var verifier = new SnapshotIntegrityVerifier(_logger, storage, _passwordProvider, _systemState, coordinator: _coordinator);
                    var verificationResult = await verifier.VerifyAsync(snapshot.Path, null, cancellationToken, verifyOptions).ConfigureAwait(false);

                    if (verificationResult.IsValid)
                    {
                        result.SnapshotsVerified++;
                        _logger.Log(
                            $"Scheduled verification passed for '{group}' (snapshot {verificationResult.SnapshotId}).",
                            LogLevel.Info);
                    }
                    else
                    {
                        result.VerificationsFailed++;
                        var summary = $"{Path.GetFileName(group)}: {verificationResult.Errors.Count} integrity issue(s)";
                        result.VerificationFailureMessages.Add(summary);

                        _logger.Log(
                            $"Scheduled verification FAILED for '{group}' with {verificationResult.Errors.Count} issue(s).",
                            LogLevel.Error);

                        foreach (var error in verificationResult.Errors.Take(10))
                        {
                            _logger.Log($"Verify Error: {error}", LogLevel.Error);
                        }
                    }

                    await PersistVerificationStateAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Log($"Scheduled verification failed for '{group}': {ex.Message}", LogLevel.Error);
                    attemptThrew = true;
                    result.VerificationsFailed++;
                    result.VerificationFailureMessages.Add($"{Path.GetFileName(group)}: {ex.Message}");

                    // Persist on the failure path too: a failed attempt still consumed its
                    // turn, so without this an unreachable provider would pin the rotation to
                    // the same snapshot across restarts and never reach older ones.
                    await PersistVerificationStateAsync().ConfigureAwait(false);
                }
            }
            // Only start the group's back-off once something was actually attempted, so a
            // group whose snapshots were all skipped (remote, under local-only) stays due.
            if (attemptedAny)
            {
                if (attemptThrew)
                {
                    _verifyFailedGroups.Add(group);
                }
                else
                {
                    _verifyFailedGroups.Remove(group);
                }
            }
        }
    }

    /// <summary>
    /// Persists verification bookkeeping (telemetry and the rotation marker). A failure to
    /// write state must not abort the remaining groups in the sweep.
    /// </summary>
    private async Task PersistVerificationStateAsync()
    {
        try
        {
            // Don't queue behind a running backup: the next save (or the next sweep) persists this.
            var lease = await _coordinator.TryAcquireMutationAsync(
                TimeSpan.Zero, "Saving verification results", default, _systemState).ConfigureAwait(false);
            if (lease == null)
            {
                _logger.Log("Repository busy; verification results will be saved with the next state save.", LogLevel.Debug);
                return;
            }

            await using var leaseScope = lease.ConfigureAwait(false);
            await _systemState.SaveStateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to persist verification state: {ex.Message}", LogLevel.Warning);
        }
    }

    /// <summary>
    /// Newest snapshot first, then the least-recently-verified older ones to fill the budget.
    /// </summary>
    private List<BackupInfo> SelectSnapshotsToVerify(List<BackupInfo> newestFirst, int budget)
    {
        var limit = Math.Max(1, budget);
        var selected = new List<BackupInfo> { newestFirst[0] };

        if (limit == 1 || newestFirst.Count == 1)
        {
            return selected;
        }

        // Never-verified snapshots sort first, since GetSnapshotVerifiedUtc returns MinValue.
        var rotation = newestFirst
            .Skip(1)
            .OrderBy(snapshot => _systemState.GetSnapshotVerifiedUtc(snapshot.Path))
            .ThenByDescending(snapshot => snapshot.Timestamp)
            .Take(limit - 1);

        selected.AddRange(rotation);
        return selected;
    }

    // Newest across the three groups, plus the in-memory marker so a failed run counts.
    private DateTime ResolveLastSystemBackupUtc()
    {
        var candidates = new[]
        {
            _systemState.GetLastBackupTimeForGroup("system_programs"),
            _systemState.GetLastBackupTimeForGroup("system_environment"),
            _systemState.GetLastBackupTimeForGroup("system_settings"),
            _systemState.GetLastBackupTimeForGroup("system_devtools"),
            _lastSystemBackupUtc
        };

        return candidates.Max();
    }

    /// <summary>Exponential retry delay (5 min, 10 min, ...) after the n-th consecutive failure, never beyond the interval.</summary>
    internal static TimeSpan RetryBackoff(int failures, TimeSpan interval)
    {
        var delay = InitialRetryDelay;
        for (var i = 1; i < failures && delay < interval; i++)
        {
            delay += delay;
        }

        return delay < interval ? delay : interval;
    }

    private bool HasSystemBackupSchedule()
        => SystemBackupSupported
            && _configManager.SystemBackup is { Enabled: true } systemBackup
            && (systemBackup.IncludePrograms || systemBackup.IncludeEnvironmentVariables || systemBackup.IncludeWindowsSettings || systemBackup.IncludeDevTools)
            && ResolveSystemSchedule() != null;

    private bool HasVerificationSchedule()
        => _configManager.Verification is { Enabled: true } verification
            && Normalize(verification.VerifyInterval) != null;

    private TimeSpan? ResolveUserBackupInterval() => Normalize(_configManager.BackupInterval);

    /// <summary>Clamped interval, or null when the value means "do not schedule".</summary>
    private TimeSpan? Normalize(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            return null;
        }

        return interval < MinimumInterval ? MinimumInterval : interval;
    }

    private string DescribeSystemBackupInterval()
    {
        if (!SystemBackupSupported)
        {
            return "not applicable (non-Windows)";
        }

        if (_configManager.SystemBackup is not { Enabled: true })
        {
            return "disabled";
        }

        return ResolveSystemSchedule() is { } schedule
            ? (schedule.IsInterval ? schedule.Interval!.Value.ToString() : schedule.Description)
            : "disabled";
    }

    private string DescribeVerificationInterval()
    {
        if (_configManager.Verification is not { Enabled: true })
        {
            return "disabled";
        }

        var interval = Normalize(_configManager.Verification.VerifyInterval);
        if (interval == null)
        {
            return "disabled";
        }

        return _configManager.Verification.LocalStorageOnly
            ? $"{interval} (local storage only)"
            : interval.ToString()!;
    }

    internal static string NormalizeGroup(string path)
    {
        // Must match the key Backup records history under, or the scheduler never sees
        // its own backups and re-runs every tick.
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        }
        catch
        {
            return path;
        }
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalMinutes < 1)
        {
            return "less than a minute";
        }

        if (age.TotalHours < 1)
        {
            return $"{(int)age.TotalMinutes}m";
        }

        return age.TotalDays < 1
            ? $"{(int)age.TotalHours}h"
            : $"{(int)age.TotalDays}d";
    }

    // ---- reporting (status, schedule next) --------------------------------------------------------------------------

    /// <summary>One thing the scheduler runs, with when it next starts and why it is not running right now.</summary>
    /// <param name="Kind"><c>folder</c>, <c>system</c>, <c>verification</c> or <c>retention</c>.</param>
    /// <param name="Source">Where the schedule comes from: <c>folder</c> (its own schedule), <c>default</c> (the global interval) or the setting's name.</param>
    /// <param name="DueUtc">When it is or was due by its schedule (unclamped, not moved by the window); <see cref="DateTime.MinValue"/> = never done.</param>
    /// <param name="NextStartUtc">When it will actually start: the due time, but never before now and not while the window is closed.</param>
    /// <param name="State"><c>scheduled</c>, <c>due</c>, <c>waiting-for-window</c>, <c>paused</c>, <c>busy</c>, <c>off</c>, <c>disabled</c> or <c>folder-missing</c>.</param>
    internal sealed record ScheduleTarget(
        string Kind, string? Path, string? Expression, string Description, string Source, DateTime? LastUtc, DateTime? DueUtc, DateTime? NextStartUtc, string State, string? Reason);

    /// <summary>The own schedule of a folder when it has a valid one (<c>off</c> included), else null.</summary>
    internal static BackupSchedule? OwnSchedule(string? text)
        => !string.IsNullOrWhiteSpace(text) && BackupSchedule.TryParse(text, out var schedule, out _) ? schedule : null;

    /// <summary>Time zone the schedules and the window are evaluated in.</summary>
    internal TimeZoneInfo ScheduleTimeZone => _timeZone;

    private string LocalText(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(LocalClock.AsUtc(utc), _timeZone).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>When the system backup next starts (window applied), or null when it is not scheduled on this platform/configuration.</summary>
    internal DateTime? ComputeSystemNextDueUtc(DateTime now)
    {
        if (!SystemBackupSupported || _configManager.SystemBackup is not { Enabled: true } || ResolveSystemSchedule() is not { } schedule
            || SystemDueUtc(schedule, now) is not { } due)
        {
            return null;
        }

        var start = EffectiveStartUtc(due, now);
        return start < now ? now : start;
    }

    private (string State, string? Reason, DateTime Next) Classify(DateTime due, DateTime now, bool paused, string? busyReason, BackupWindow? window)
    {
        var start = EffectiveStartUtc(due, now);
        var next = start < now ? now : start;
        var never = due == DateTime.MinValue;
        var prefix = never ? "Never done. " : string.Empty;

        if (due <= now)
        {
            if (paused)
            {
                return ("paused", $"{prefix}Protection is paused ('restore resume' ends the pause); it runs after the resume.", now);
            }

            if (window != null && !window.IsOpen(now, _timeZone))
            {
                return ("waiting-for-window", $"{prefix}Due, but the backup window is closed; it starts when the window opens at {LocalText(next)}.", next);
            }

            if (busyReason != null)
            {
                return ("busy", $"{prefix}Due now, but the repository is in use ({busyReason}); it is retried on the next sweep.", now);
            }

            return ("due", $"{prefix}Due now; the next sweep (the app, 'restore --service' or 'run-due') runs it.", now);
        }

        return start > due
            ? ("scheduled", $"Due at {LocalText(due)}, but the backup window is closed then; it starts at {LocalText(start)}.", start)
            : ("scheduled", null, due);
    }

    /// <summary>
    /// Every folder, the system backup, verification and retention with their schedules, next start and the reason they are not
    /// running now. Read-only; used by <c>schedule next</c>.
    /// </summary>
    internal List<ScheduleTarget> DescribeTargets(DateTime now, bool paused = false, string? busyReason = null)
    {
        var window = CurrentWindow();
        var targets = new List<ScheduleTarget>();

        foreach (var watch in _configManager.WatchDirectories.ToList().Where(w => !string.IsNullOrWhiteSpace(w.Path)))
        {
            var own = OwnSchedule(watch.Schedule);
            var effective = ResolveFolderSchedule(watch);
            var source = own != null ? "folder" : "default";
            var expression = own?.Expression;
            var description = own?.Description
                ?? (effective != null ? effective.Description : "Off (backupInterval is not positive)");
            var group = NormalizeGroup(watch.ExpandedPath);
            DateTime? last = effective is { IsClock: true } ? _systemState.GetLastSuccessfulRunUtc(group) : _systemState.GetLastBackupTimeForGroup(group);
            if (last == DateTime.MinValue)
            {
                last = null;
            }

            if (!Directory.Exists(watch.ExpandedPath))
            {
                targets.Add(new ScheduleTarget("folder", watch.Path, expression, description, source, last, null, null, "folder-missing", "The folder does not exist, so nothing is scheduled for it."));
                continue;
            }

            if (effective == null || FolderDueUtc(effective, group, now) is not { } due)
            {
                targets.Add(new ScheduleTarget("folder", watch.Path, expression, description, source, last, null, null, "off",
                    "No scheduled backups: the watcher and manual backups still run."));
                continue;
            }

            var (state, reason, next) = Classify(due, now, paused, busyReason, window);
            if (_failureCount.TryGetValue(group, out var failures))
            {
                reason = $"{reason} The last {failures} scheduled attempt(s) failed; retrying with backoff.".Trim();
            }

            targets.Add(new ScheduleTarget("folder", watch.Path, expression, description, source, last, due == DateTime.MinValue ? null : due, next, state, reason));
        }

        targets.Add(DescribeSystemTarget(now, paused, busyReason, window));
        targets.Add(DescribeVerificationTarget(now, paused, window));
        targets.Add(DescribeRetentionTarget(now, paused, window));
        return targets;
    }

    private ScheduleTarget DescribeSystemTarget(DateTime now, bool paused, string? busyReason, BackupWindow? window)
    {
        var own = OwnSchedule(_configManager.SystemBackup?.Schedule);
        var schedule = ResolveSystemSchedule();
        var source = own != null ? "systemBackup.schedule" : "systemBackup.backupInterval";
        var description = schedule?.Description ?? "Off";
        var systemBackup = _configManager.SystemBackup;
        DateTime? last = ResolveLastSystemBackupUtc() is var l && l != DateTime.MinValue ? l : null;

        if (!SystemBackupSupported)
        {
            return new ScheduleTarget("system", null, own?.Expression, description, source, last, null, null, "disabled", "The system backup is only available on Windows.");
        }

        if (systemBackup is not { Enabled: true })
        {
            return new ScheduleTarget("system", null, own?.Expression, description, source, last, null, null, "disabled", "systemBackup.enabled is false.");
        }

        if (!systemBackup.IncludePrograms && !systemBackup.IncludeEnvironmentVariables && !systemBackup.IncludeWindowsSettings && !systemBackup.IncludeDevTools)
        {
            return new ScheduleTarget("system", null, own?.Expression, description, source, last, null, null, "disabled", "No system backup component is selected.");
        }

        if (schedule == null || SystemDueUtc(schedule, now) is not { } due)
        {
            return new ScheduleTarget("system", null, own?.Expression, description, source, last, null, null, "off", "No scheduled system backups.");
        }

        var (state, reason, next) = Classify(due, now, paused, busyReason, window);
        return new ScheduleTarget("system", null, own?.Expression, description, source, last, due == DateTime.MinValue ? null : due, next, state, reason);
    }

    private ScheduleTarget DescribeVerificationTarget(DateTime now, bool paused, BackupWindow? window)
    {
        const string Source = "verification.verifyInterval";
        var verification = _configManager.Verification;
        if (verification is not { Enabled: true })
        {
            return new ScheduleTarget("verification", null, null, "Off", Source, null, null, null, "disabled", "verification.enabled is false.");
        }

        var interval = Normalize(verification.VerifyInterval);
        if (interval == null)
        {
            return new ScheduleTarget("verification", null, null, "Off", Source, null, null, null, "off", "verification.verifyInterval is not positive.");
        }

        var description = BackupSchedule.FromInterval(interval.Value).Description;
        DateTime? earliest = null;
        DateTime? lastAny = null;
        foreach (var group in _systemState.GetBackupGroups())
        {
            var snapshots = _systemState.GetBackupsForGroup(group)
                .Where(b => b.ArtifactType == BackupArtifactType.SnapshotManifest && !string.IsNullOrWhiteSpace(b.Path)).ToList();
            if (snapshots.Count == 0)
            {
                continue;
            }

            var lastVerified = snapshots.Max(b => _systemState.GetSnapshotVerifiedUtc(b.Path));
            var due = lastVerified == DateTime.MinValue ? DateTime.MinValue : lastVerified + interval.Value;
            earliest = earliest == null || due < earliest ? due : earliest;
            if (lastVerified != DateTime.MinValue && (lastAny == null || lastVerified > lastAny))
            {
                lastAny = lastVerified;
            }
        }

        if (earliest == null)
        {
            return new ScheduleTarget("verification", null, null, description, Source, null, null, null, "scheduled", "There are no snapshots to verify yet.");
        }

        var (state, reason, next) = Classify(earliest.Value, now, paused, null, window);
        return new ScheduleTarget("verification", null, null, description, Source, lastAny, earliest == DateTime.MinValue ? null : earliest, next, state, reason);
    }

    private ScheduleTarget DescribeRetentionTarget(DateTime now, bool paused, BackupWindow? window)
    {
        const string Source = "retention (daily)";
        var description = BackupSchedule.FromInterval(RetentionInterval).Description;
        if (!_configManager.Retention.Enabled)
        {
            return new ScheduleTarget("retention", null, null, description, Source, null, null, null, "disabled", "retention.enabled is false.");
        }

        var last = _systemState.LastRetentionRunUtc;
        DateTime? lastUtc = last == DateTime.MinValue ? null : last;
        if (_systemState.GetTotalBackupCount() == 0)
        {
            return new ScheduleTarget("retention", null, null, description, Source, lastUtc, null, null, "scheduled", "There are no backups to apply retention to yet.");
        }

        var due = last == DateTime.MinValue ? DateTime.MinValue : last + RetentionInterval;
        var (state, reason, next) = Classify(due, now, paused, null, window);
        return new ScheduleTarget("retention", null, null, description, Source, lastUtc, due == DateTime.MinValue ? null : due, next, state, reason);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }

        // Don't block Dispose on an in-flight backup; cancellation stops the loop.
        _shutdown.Dispose();
        _runLock.Dispose();
        _loop = null;
    }
}
