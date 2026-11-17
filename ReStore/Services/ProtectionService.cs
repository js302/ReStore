using System.Windows;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.utils;

namespace ReStore.Services
{
    /// <summary>
    /// App-wide owner of the file watcher and backup scheduler. Lives for the whole process, so protection
    /// does not depend on which page is showing, and is stopped explicitly on exit. The lifecycle logic is
    /// <see cref="ProtectionController"/> (unit-tested in Core); this class wires it to the shared config,
    /// state, password provider, activity log and tray notifications.
    /// Events are raised on whichever thread caused them; UI subscribers marshal with Dispatcher.BeginInvoke.
    /// </summary>
    public sealed class ProtectionService : ILogger
    {
        private readonly ConfigManager _config;
        private readonly SystemState _state;
        private readonly IPasswordProvider _passwordProvider;
        private readonly Logger _fileLogger;
        private readonly BackupNotificationService _notifications;
        private readonly ProtectionController _controller;
        private Timer? _stalenessTimer;
        private Timer? _pauseSyncTimer;
        private readonly ProtectionPauseStore _pauseStore = new();

        public ProtectionService(ConfigManager config, SystemState state, GuiPasswordProvider passwordProvider, Logger fileLogger)
        {
            _config = config;
            _state = state;
            _passwordProvider = passwordProvider;
            _fileLogger = fileLogger;
            _notifications = new BackupNotificationService(fileLogger);

            _controller = new ProtectionController(
                CreateWatcher,
                CreateScheduler,
                this,
                autoStartEnabled: () => _config.Protection.AutoStart && HasWatchDirectories(),
                validate: Validate,
                fingerprint: () => ProtectionFingerprint.Compute(_config),
                pauseStore: _pauseStore);

            _controller.StateChanged += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
            passwordProvider.BackgroundUnlockNeeded += _controller.MarkNeedsPassword;
            passwordProvider.Unlocked += _controller.MarkPasswordAvailable;

            // Backups that silently stop are the worst failure, and the scheduler only reports sweeps that ran.
            _stalenessTimer = new Timer(_ => CheckStaleness(), null, StalenessFirstCheck, StalenessInterval);

            // 'restore pause' / 'restore resume' (another process) write the same pause file the tray uses; pick it up.
            _pauseSyncTimer = new Timer(_ => SyncPauseFromStore(), null, PauseSyncInterval, PauseSyncInterval);
        }

        private static readonly TimeSpan PauseSyncInterval = TimeSpan.FromSeconds(10);

        private async void SyncPauseFromStore()
        {
            try
            {
                await _controller.SyncPauseFromStoreAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _fileLogger.Log($"Checking for a pause set by the command line failed: {ex.Message}", LogLevel.Debug);
            }
        }

        private static readonly TimeSpan StalenessFirstCheck = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan StalenessInterval = TimeSpan.FromHours(1);

        /// <summary>The groups currently overdue (2x the backup interval or the longest gap of its schedule, or 2 days with no interval).</summary>
        public List<StaleGroup> FindStaleGroups() => BackupStaleness.Evaluate(
            _config.WatchDirectories.Where(w => !string.IsNullOrWhiteSpace(w.Path))
                .Select(w => (SafeFullPath(w.ExpandedPath), BackupStaleness.IntervalFor(w.Schedule, _config.BackupInterval, DateTime.UtcNow))),
            _state,
            DateTime.UtcNow);

        private void CheckStaleness()
        {
            // Config is owned by the UI thread, so read it there rather than from the timer thread.
            OnUi(() =>
            {
                try
                {
                    var stale = FindStaleGroups();
                    if (stale.Count > 0)
                    {
                        Notifier().NotifyStale(stale);
                    }
                }
                catch (Exception ex)
                {
                    _fileLogger.Log($"Staleness check failed: {ex.Message}", LogLevel.Debug);
                }
            });
        }

        private static string SafeFullPath(string path)
        {
            try
            {
                return System.IO.Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        /// <summary>Unlock prompt for encrypted backups that cannot run in the background; shown even without a tray icon.</summary>
        public void NotifyUnlockNeeded() => OnUi(() => Notifier().NotifyUnlockNeeded());

        /// <summary>Toast (or tray balloon) that a newer ReStore is available, subject to the notification settings.</summary>
        public void NotifyUpdateAvailable(string version, bool critical) => OnUi(() => Notifier().NotifyUpdateAvailable(version, critical));

        public ProtectionState State => _controller.State;
        public DateTime? NextRunUtc => _controller.NextRunUtc;
        public bool NextRunHeldByWindow => _controller.NextRunHeldByWindow;
        public DateTime? PausedUntilUtc => _controller.PausedUntilUtc;
        public string? LastError => _controller.LastError;

        /// <summary>True while the watcher and scheduler exist, including the needs-password state.</summary>
        public bool IsActive => State is ProtectionState.Running or ProtectionState.PausedNeedsPassword;

        public event EventHandler? StateChanged;

        /// <summary>A sweep did work; stats and history are worth refreshing.</summary>
        public event Action? BackupCycleCompleted;

        /// <summary>A log line from the watcher/scheduler, for the Dashboard activity pane (already written to the log file).</summary>
        public event Action<string, LogLevel>? LogEmitted;

        public Task StartAsync() => _controller.StartAsync();
        public Task StopAsync() => _controller.StopAsync();
        public Task RestartAsync() => _controller.RestartAsync();
        public Task PauseFor(TimeSpan duration) => _controller.PauseFor(duration);
        public Task PauseUntilTomorrowAsync() => _controller.PauseUntilAsync(ProtectionPause.UntilTomorrowUtc(DateTime.UtcNow));
        public Task PauseUntilResumedAsync() => _controller.PauseUntilAsync(ProtectionPause.Indefinite);

        /// <summary>Ends a pause (or a stop) and starts protection.</summary>
        public Task ResumeAsync() => _controller.StartAsync();

        /// <summary>True while protection is paused (finite or until resumed) and has not resumed yet.</summary>
        public bool IsPaused => PausedUntilUtc != null;

        public Task AutoStartAsync() => _controller.AutoStartAsync();

        /// <summary>Re-enters a pause saved by the previous run; call before <see cref="AutoStartAsync"/>.</summary>
        public Task RestorePauseAsync() => _controller.RestorePauseAsync();

        private readonly CancellationTokenSource _backupNowCts = new();
        private int _backupNowRunning;

        /// <summary>True while <see cref="BackupNowAsync"/> is running.</summary>
        public bool IsBackingUpNow => Volatile.Read(ref _backupNowRunning) != 0;

        /// <summary>A "Back up now" run ended: a one-line summary and whether it had failures.</summary>
        public event Action<string, bool>? BackupNowFinished;

        /// <summary>
        /// Backs up every configured watch directory now, whether or not it is due, even while protection is
        /// paused or stopped. Does nothing if a run is already in progress.
        /// </summary>
        public async Task BackupNowAsync()
        {
            if (Interlocked.CompareExchange(ref _backupNowRunning, 1, 0) != 0)
            {
                return;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            var result = new BackupCycleResult();
            var token = _backupNowCts.Token;
            try
            {
                var directories = _config.WatchDirectories
                    .Where(w => !string.IsNullOrWhiteSpace(w.Path) && System.IO.Directory.Exists(w.ExpandedPath))
                    .ToList();
                if (directories.Count == 0)
                {
                    Log("Back up now: no watched folders exist.", LogLevel.Warning);
                    BackupNowFinished?.Invoke("No watched folders to back up.", true);
                    return;
                }

                Log($"Back up now: {directories.Count} folder(s).", LogLevel.Info);
                foreach (var watchDirectory in directories)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var backup = new Backup(this, _state, new SizeAnalyzer(), _config, _passwordProvider);
                        var backupResult = await backup.BackupDirectoryAsync(watchDirectory.ExpandedPath, watchDirectory.StorageType, null, token).ConfigureAwait(false);
                        result.DirectoriesBackedUp++;
                        result.FilesSkipped += backupResult.Skipped.Count;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log($"Back up now failed for '{watchDirectory.Path}': {ex.Message}", LogLevel.Error);
                        result.DirectoriesFailed++;
                        result.FailureMessages.Add($"{System.IO.Path.GetFileName(watchDirectory.Path.TrimEnd('\\', '/'))}: {ex.Message}");
                    }
                }

                BackupCycleCompleted?.Invoke();
                OnUi(() => Notifier().NotifyCycleFinished(result));
                var summary = result.DirectoriesFailed > 0
                    ? $"{result.DirectoriesFailed} of {directories.Count} folder(s) failed."
                    : $"Backed up {result.DirectoriesBackedUp} folder(s).";
                BackupNowFinished?.Invoke(summary, result.DirectoriesFailed > 0);
            }
            catch (OperationCanceledException)
            {
                Log("Back up now cancelled.", LogLevel.Warning);
            }
            finally
            {
                Volatile.Write(ref _backupNowRunning, 0);
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Call after a config save that touches watch directories, intervals, storage or encryption.</summary>
        public Task ApplyConfigChangeAsync() => _controller.ApplyConfigChangeAsync();

        /// <summary>Stops for app exit / sign-out, cancelling in-flight work, then saves state.</summary>
        public async Task ShutdownAsync(TimeSpan timeout)
        {
            _stalenessTimer?.Dispose();
            _stalenessTimer = null;
            _pauseSyncTimer?.Dispose();
            _pauseSyncTimer = null;
            _backupNowCts.Cancel();
            await _controller.ShutdownAsync(timeout).ConfigureAwait(false);

            try
            {
                // Under the lease so a state file another process (CLI) changed is reloaded, not overwritten.
                var lease = await RepositoryCoordinator.Instance
                    .TryAcquireMutationAsync(TimeSpan.FromSeconds(2), "Saving state at shutdown", default, _state).ConfigureAwait(false);
                if (lease == null)
                {
                    _fileLogger.Log("Repository busy at shutdown; state was not saved.", LogLevel.Warning);
                    return;
                }

                await using (lease.ConfigureAwait(false))
                {
                    await _state.SaveStateAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _fileLogger.Log($"Saving state at shutdown failed: {ex.Message}", LogLevel.Warning);
            }
        }

        public void Log(string message, LogLevel level = LogLevel.Info)
        {
            _fileLogger.Log(message, level);
            LogEmitted?.Invoke(message, level);
        }

        private bool HasWatchDirectories() =>
            _config.WatchDirectories.Any(w => !string.IsNullOrWhiteSpace(w.Path));

        private string? Validate()
        {
            var result = _config.ValidateConfiguration();
            return result.IsValid ? null : string.Join("; ", result.Errors);
        }

        private IProtectionWorker CreateWatcher() =>
            new FileWatcherWorker(new FileWatcher(_config, this, _state, new SizeAnalyzer(), _passwordProvider, pauseStore: _pauseStore));

        private IProtectionWorker CreateScheduler()
        {
            // Runs alongside the watcher so the configured backupInterval is honoured for changes that
            // happened while the app was closed.
            var scheduler = new BackupScheduler(_config, this, _state, new SizeAnalyzer(), _passwordProvider, pauseStore: _pauseStore);
            scheduler.BackupCycleCompleted += () => BackupCycleCompleted?.Invoke();
            scheduler.BackupCycleFinished += result => OnUi(() => Notifier().NotifyCycleFinished(result));
            scheduler.BackupRecovered += result => OnUi(() => Notifier().NotifyRecovered(result));
            return new SchedulerWorker(scheduler);
        }

        /// <summary>Removes the on-demand tray icon and logs unsent notifications. Call on the UI thread at exit (not from ShutdownAsync, which runs on a pool thread while the UI thread waits).</summary>
        public void DisposeNotifications()
        {
            try
            {
                _notifications.Dispose();
            }
            catch (Exception ex)
            {
                _fileLogger.Log($"Disposing notifications failed: {ex.Message}", LogLevel.Debug);
            }
        }

        private BackupNotificationService Notifier()
        {
            var tray = (Application.Current?.MainWindow as Views.MainWindow)?.TrayManager;
            _notifications.Configure(tray, _config.Notifications, AppSettings.Load().UseToastNotifications);
            return _notifications;
        }

        private static void OnUi(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                return;
            }

            _ = dispatcher.BeginInvoke(action);
        }
    }
}
