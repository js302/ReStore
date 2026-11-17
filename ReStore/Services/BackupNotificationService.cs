using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.utils;

namespace ReStore.Services
{
    /// <summary>
    /// Routes scheduler outcomes to Windows toasts (packaged, with action buttons) or tray balloons, gated on the <c>notifications</c> config
    /// section and on whether Windows wants notifications suppressed. Deliberately silent on
    /// routine success. Works whether or not "minimize to tray" is on: without a tray manager a
    /// tray icon is created on demand and removed shortly after the last balloon. Notifications
    /// that Windows suppresses (Focus Assist, fullscreen) are queued and shown once it stops.
    /// Must be used on the UI thread.
    /// </summary>
    public sealed class BackupNotificationService(
        ILogger logger,
        INotificationSuppressionProbe? suppressionProbe = null,
        Func<DateTime>? utcNow = null) : IDisposable
    {
        private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan OnDemandIconLifetime = TimeSpan.FromSeconds(30);

        private readonly INotificationSuppressionProbe _suppressionProbe =
            suppressionProbe ?? new WindowsNotificationSuppressionProbe(logger);
        private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);
        private readonly NotificationQueue _queue = new();
        private readonly PeriodicNotificationGate _staleGate = new();

        private SystemTrayManager? _trayManager;
        private NotificationConfig _config = new();
        private TaskbarIcon? _onDemandIcon;
        private DispatcherTimer? _flushTimer;
        private DispatcherTimer? _iconTimer;
        private NotificationTarget _lastTarget = NotificationTarget.None;
        private bool _useToasts;

        /// <param name="useToasts">Prefer Windows toasts (ignored, balloons used, when unpackaged).</param>
        public void Configure(SystemTrayManager? trayManager, NotificationConfig? config, bool useToasts = false)
        {
            if (!ReferenceEquals(trayManager, _trayManager))
            {
                if (_trayManager != null)
                {
                    _trayManager.BalloonClicked -= OnBalloonClicked;
                }

                _trayManager = trayManager;
                if (_trayManager != null)
                {
                    _trayManager.BalloonClicked += OnBalloonClicked;

                    // The real tray icon takes over; a second one would show two ReStore icons.
                    DisposeOnDemandIcon();
                }
            }

            _config = config ?? new NotificationConfig();
            _useToasts = useToasts;
        }

        public void NotifyCycleFinished(BackupCycleResult result)
        {
            if (result == null || !_config.Enabled)
            {
                return;
            }

            var failed = result.DirectoriesFailed > 0 || result.SystemBackupFailed;
            if (_config.NotifyOnBackupFailure && failed)
            {
                var detail = result.FailureMessages.Count > 0
                    ? result.FailureMessages[0]
                    : "See the activity log for details.";

                var suffix = result.FailureMessages.Count > 1
                    ? $" (+{result.FailureMessages.Count - 1} more)"
                    : string.Empty;

                Show("Backup failed", $"{detail}{suffix}", NotificationSeverity.Error, NotificationTarget.Dashboard, [ToastAction.RetryNow, ToastAction.OpenLog]);
            }
            else if (_config.NotifyOnBackupFailure && !failed && result.FilesSkipped > 0)
            {
                Show(
                    "Backup completed with skipped files",
                    $"{result.FilesSkipped} file(s) could not be read and were skipped or carried forward. See the activity log.",
                    NotificationSeverity.Warning,
                    NotificationTarget.Dashboard,
                    [ToastAction.ViewReport]);
            }

            if (_config.NotifyOnVerificationFailure && result.VerificationsFailed > 0)
            {
                var detail = result.VerificationFailureMessages.Count > 0
                    ? result.VerificationFailureMessages[0]
                    : "A snapshot failed its integrity check.";

                Show("Backup integrity check failed", detail, NotificationSeverity.Error, NotificationTarget.Backups);
            }

            // Opt-in only: notifying on every good backup trains people to ignore the ones
            // that matter.
            if (_config.NotifyOnEveryBackupSuccess && result.DirectoriesBackedUp > 0 && !result.HasFailures)
            {
                Show("Backup completed", $"{result.DirectoriesBackedUp} folder(s) backed up.", NotificationSeverity.Info, NotificationTarget.Backups);
            }
        }

        public void NotifyRecovered(BackupCycleResult result)
        {
            if (!_config.Enabled || !_config.NotifyOnRecovery)
            {
                return;
            }

            Show("Backups working again", "A scheduled backup succeeded after previous failures.", NotificationSeverity.Info, NotificationTarget.Dashboard);
        }

        /// <summary>Encrypted backups are waiting for the password; clicking opens Settings.</summary>
        public void NotifyUnlockNeeded()
        {
            if (!_config.Enabled)
            {
                return;
            }

            Show("Encrypted backups paused", "Unlock ReStore to resume protecting your files.", NotificationSeverity.Warning, NotificationTarget.Settings, [ToastAction.Unlock]);
        }

        /// <summary>A newer ReStore exists; clicking opens the Dashboard, where the update banner is. The caller decides how often (once per version).</summary>
        public void NotifyUpdateAvailable(string version, bool critical)
        {
            if (!_config.Enabled)
            {
                return;
            }

            Show(
                $"ReStore {version} is available",
                critical ? "This update is marked critical. Open ReStore to install it." : "Open ReStore to install it or read the release notes.",
                critical ? NotificationSeverity.Warning : NotificationSeverity.Info,
                NotificationTarget.Dashboard);
        }

        /// <summary>At most once a day per group, so a long outage does not nag.</summary>
        public void NotifyStale(IEnumerable<StaleGroup> staleGroups)
        {
            if (!_config.Enabled || !_config.NotifyOnBackupFailure)
            {
                return;
            }

            foreach (var stale in staleGroups)
            {
                if (!_staleGate.TryAcquire(stale.Group, _utcNow()))
                {
                    continue;
                }

                var name = System.IO.Path.GetFileName(stale.Group.TrimEnd('\\', '/')) is { Length: > 0 } leaf ? leaf : stale.Group;
                Show(
                    "Backups are out of date",
                    $"'{name}' has not been backed up successfully for {(stale.Age.TotalDays >= 1 ? $"{(int)stale.Age.TotalDays} day(s)" : $"{(int)stale.Age.TotalHours} hour(s)")}.",
                    NotificationSeverity.Warning,
                    NotificationTarget.Dashboard,
                    [ToastAction.BackUpNow]);
            }
        }

        private void Show(string title, string message, NotificationSeverity severity, NotificationTarget target, IReadOnlyList<ToastAction>? actions = null)
        {
            try
            {
                var notification = new PendingNotification(title, message, severity, target, _utcNow(), actions);
                if (_suppressionProbe.IsSuppressed())
                {
                    _queue.Enqueue(notification);
                    StartFlushTimer();
                    return;
                }

                if (_queue.Count > 0)
                {
                    _queue.Enqueue(notification);
                    Flush();
                    return;
                }

                Display(notification);
            }
            catch (Exception ex)
            {
                // A failed toast must never take down a backup cycle.
                logger.Log($"Failed to show notification '{title}': {ex.Message}", LogLevel.Warning);
            }
        }

        private void StartFlushTimer()
        {
            if (_flushTimer == null)
            {
                _flushTimer = new DispatcherTimer { Interval = FlushInterval };
                _flushTimer.Tick += (_, __) =>
                {
                    try
                    {
                        Flush();
                    }
                    catch (Exception ex)
                    {
                        logger.Log($"Flushing queued notifications failed: {ex.Message}", LogLevel.Debug);
                    }
                };
            }

            _flushTimer.Start();
        }

        /// <summary>Shows what was held back, if Windows accepts notifications again.</summary>
        private void Flush()
        {
            if (_queue.Count == 0)
            {
                _flushTimer?.Stop();
                return;
            }

            if (_suppressionProbe.IsSuppressed())
            {
                return;
            }

            _flushTimer?.Stop();
            var now = _utcNow();
            if (NotificationQueue.Coalesce(_queue.Drain(now), now) is { } notification)
            {
                Display(notification);
            }
        }

        private void Display(PendingNotification notification)
        {
            _lastTarget = notification.Target;
            if (_useToasts && ToastNotifier.IsAvailable)
            {
                try
                {
                    ToastNotifier.Show(notification);
                    return;
                }
                catch (Exception ex)
                {
                    // Fall through to the balloon so the user still hears about it.
                    logger.Log($"Windows toast failed, using a tray balloon instead: {ex.Message}", LogLevel.Warning);
                }
            }

            var icon = notification.Severity switch
            {
                NotificationSeverity.Error => BalloonIcon.Error,
                NotificationSeverity.Warning => BalloonIcon.Warning,
                _ => BalloonIcon.Info
            };

            if (_trayManager != null)
            {
                _trayManager.ShowBalloonTip(notification.Title, notification.Message, icon);
                return;
            }

            EnsureOnDemandIcon().ShowBalloonTip(notification.Title, notification.Message, icon);
            RestartOnDemandIconTimer();
        }

        private TaskbarIcon EnsureOnDemandIcon()
        {
            if (_onDemandIcon != null)
            {
                return _onDemandIcon;
            }

            _onDemandIcon = new TaskbarIcon { Icon = AppIcon.Load(), ToolTipText = "ReStore" };
            _onDemandIcon.TrayBalloonTipClicked += (_, __) => OnBalloonClicked();
            _onDemandIcon.TrayLeftMouseDown += (_, __) => AppNavigator.Open(NotificationTarget.Dashboard);
            return _onDemandIcon;
        }

        // The icon only has to outlive the balloon (and a click on it).
        private void RestartOnDemandIconTimer()
        {
            if (_iconTimer == null)
            {
                _iconTimer = new DispatcherTimer { Interval = OnDemandIconLifetime };
                _iconTimer.Tick += (_, __) => DisposeOnDemandIcon();
            }

            _iconTimer.Stop();
            _iconTimer.Start();
        }

        private void DisposeOnDemandIcon()
        {
            _iconTimer?.Stop();
            _onDemandIcon?.Dispose();
            _onDemandIcon = null;
        }

        /// <summary>
        /// Exit: removes the on-demand tray icon (no ghost icon) and stops the timers. Anything still queued is
        /// written to the log so it is not lost silently.
        /// </summary>
        public void Dispose()
        {
            _flushTimer?.Stop();
            DisposeOnDemandIcon();

            if (_trayManager != null)
            {
                _trayManager.BalloonClicked -= OnBalloonClicked;
                _trayManager = null;
            }

            foreach (var pending in _queue.Drain(_utcNow()))
            {
                logger.Log($"Notification not shown before exit: {pending.Title} - {pending.Message}", LogLevel.Warning);
            }
        }

        private void OnBalloonClicked() => AppNavigator.Open(_lastTarget);
    }
}
