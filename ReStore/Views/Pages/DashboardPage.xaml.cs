using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ReStore.Core.src.utils;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.backup;
using ReStore.Services;
using ReStore.Views.Windows;

namespace ReStore.Views.Pages
{
    public class BackupHistoryItem
    {
        public string Directory { get; set; } = "";
        public string Path { get; set; } = "";
        public string StorageType { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public bool IsDiff { get; set; }
        public bool CanRestore => IsSnapshotArtifactPath(Path);
        public string TypeLabel => CanRestore ? "Snapshot" : IsDiff ? "Differential" : "Full";
        public string TimeLabel => DisplayTime.Format(Timestamp);
        public string TimeTooltip => DisplayTime.Tooltip(Timestamp);

        public bool SameAs(BackupHistoryItem other) =>
            Path == other.Path && Timestamp == other.Timestamp && Directory == other.Directory
            && StorageType == other.StorageType && IsDiff == other.IsDiff;

        private static bool IsSnapshotArtifactPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            return path.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("\\HEAD", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Looks up a WPF-UI theme brush, falling back to a fixed colour when no theme resources are loaded.</summary>
    internal static class ThemeBrushes
    {
        public static Brush Get(string key, Color fallback) =>
            Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
    }

    /// <summary>One watched directory's backup freshness, for the health panel.</summary>
    public class DirectoryHealthItem
    {
        // Theme brushes keep the age text readable in both light and dark; resolved on access so a theme switch is picked up.
        private static Brush FreshBrush => ThemeBrushes.Get("SystemFillColorSuccessBrush", Color.FromRgb(15, 123, 15));
        private static Brush AgeingBrush => ThemeBrushes.Get("SystemFillColorCautionBrush", Color.FromRgb(157, 93, 0));
        private static Brush StaleBrush => ThemeBrushes.Get("SystemFillColorCriticalBrush", Color.FromRgb(196, 43, 28));

        public string DirectoryName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string? RestorePath { get; set; }
        public string StorageType { get; set; } = "";
        public DateTime? LastBackupUtc { get; set; }

        public bool CanRestore => !string.IsNullOrWhiteSpace(RestorePath);

        /// <summary>Same on-screen content; lets the list update only the rows that changed.</summary>
        public bool SameAs(DirectoryHealthItem other) =>
            FullPath == other.FullPath && RestorePath == other.RestorePath && StorageType == other.StorageType
            && LastBackupUtc == other.LastBackupUtc && AgeLabel == other.AgeLabel
            && ReferenceEquals(StalenessColor, other.StalenessColor);

        public string AgeLabel
        {
            get
            {
                if (LastBackupUtc == null)
                {
                    return "Never backed up";
                }

                var age = DateTime.UtcNow - LastBackupUtc.Value;
                if (age < TimeSpan.FromMinutes(1)) return "Just now";
                if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
                if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h ago";
                return $"{(int)age.TotalDays}d ago";
            }
        }

        /// <summary>Green under a day, amber under a week, red beyond — or never backed up.</summary>
        public Brush StalenessColor
        {
            get
            {
                if (LastBackupUtc == null)
                {
                    return StaleBrush;
                }

                var age = DateTime.UtcNow - LastBackupUtc.Value;
                if (age < TimeSpan.FromDays(1)) return FreshBrush;
                return age < TimeSpan.FromDays(7) ? AgeingBrush : StaleBrush;
            }
        }
    }

    /// <summary>One line of the Dashboard's backup-run list.</summary>
    public class RunReportItem
    {
        public RunReportItem(BackupRunReport report)
        {
            Report = report;
            TimeLabel = DisplayTime.Format(report.TimestampUtc);
            TimeTooltip = DisplayTime.Tooltip(report.TimestampUtc);
            Folder = System.IO.Path.GetFileName(report.Group.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : report.Group;
            Detail = $"{report.Summary} - {TimeSpan.FromMilliseconds(report.DurationMs):mm\\:ss}, {ByteFormatter.Format(report.BytesUploaded)} uploaded";
        }

        public BackupRunReport Report { get; }
        public string TimeLabel { get; }
        public string TimeTooltip { get; }
        public string Folder { get; }
        public string Detail { get; }

        public bool SameAs(RunReportItem other) => Report.TimestampUtc == other.Report.TimestampUtc && Report.Group == other.Report.Group;
    }

    public partial class DashboardPage : Page, ILogger
    {
        // The activity pane is a tail view, not an archive; Logger keeps the full log on disk.
        private const int MaxLogBufferChars = 256 * 1024;
        private const int KeepLogBufferChars = 128 * 1024;

        private readonly Logger _fileLogger = AppServices.Logger;
        private ConfigManager _configManager = null!;
        private SystemState? _state;
        private readonly StringBuilder _logBuffer = new();
        private readonly ObservableCollection<BackupHistoryItem> _backupHistory = new();
        private readonly ObservableCollection<DirectoryHealthItem> _directoryHealth = new();
        private readonly ObservableCollection<string> _needsAttention = new();
        private readonly ObservableCollection<string> _staleGroups = new();
        private readonly ObservableCollection<RunReportItem> _runReports = new();
        private bool _healthRefreshing;
        private System.Windows.Threading.DispatcherTimer? _statsTimer;
        private readonly FailureStreak _statsFailures = new(3);
        private bool _initialized;
        private RepositoryBusyBinding? _busyBinding;

        public DashboardPage()
        {
            InitializeComponent();

            ValidateBtn.Click += (_, __) => ValidateConfig();
            StartWatcherBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Starting the watcher", async () => { if (Protection != null) await Protection.StartAsync(); });
            StopWatcherBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Stopping the watcher", async () => { if (Protection != null) await Protection.StopAsync(); });

            // The page is cached for the app's lifetime, so subscribe once.
            if (Protection != null)
            {
                Protection.StateChanged += OnProtectionStateChanged;
                Protection.LogEmitted += OnProtectionLogEmitted;
                Protection.BackupCycleCompleted += OnScheduledBackupCompleted;
            }
            AppUpdateManager.Instance.Changed += () => _ = Dispatcher.BeginInvoke(ApplyUpdateBanner);
            UpdateNowBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Updating ReStore", () => AppUpdateManager.Instance.UpdateNowAsync());
            UpdateNotesBtn.Click += (_, __) =>
            {
                try
                {
                    AppUpdateManager.Instance.OpenReleaseNotes();
                }
                catch (Exception ex)
                {
                    ErrorReporter.Report("Opening the release notes", ex);
                }
            };
            UpdateSkipBtn.Click += (_, __) => AppUpdateManager.Instance.Skip();
            UpdateCloseBtn.Click += (_, __) => AppUpdateManager.Instance.Dismiss();
            BackupNowBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Backing up now", async () => { if (Protection != null) await Protection.BackupNowAsync(); });
            PauseBtn.Click += (_, __) =>
            {
                if (PauseBtn.ContextMenu is { } menu)
                {
                    menu.PlacementTarget = PauseBtn;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    menu.IsOpen = true;
                }
            };
            PauseHourItem.Click += async (_, __) => await ErrorReporter.GuardAsync("Pausing protection", async () => { if (Protection != null) await Protection.PauseFor(ProtectionPause.OneHour); });
            PauseTomorrowItem.Click += async (_, __) => await ErrorReporter.GuardAsync("Pausing protection", async () => { if (Protection != null) await Protection.PauseUntilTomorrowAsync(); });
            PauseIndefiniteItem.Click += async (_, __) => await ErrorReporter.GuardAsync("Pausing protection", async () => { if (Protection != null) await Protection.PauseUntilResumedAsync(); });
            ResumeBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Resuming protection", async () => { if (Protection != null) await Protection.ResumeAsync(); });
            ManualBackupBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Manual backup", () => ManualBackupAsync());
            RestoreBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Restore backup", () => RestoreBackupAsync());
            TestRestoreBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Test restore", () => TestRestoreLatestAsync());
            SystemBackupBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("System backup", () => SystemBackupAsync());
            RefreshHistoryBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Refresh backup history", () => RefreshBackupHistoryAsync());
            ClearLogsBtn.Click += (_, __) => ClearLogs();

            BackupHistoryList.ItemsSource = _backupHistory;
            DirectoryHealthList.ItemsSource = _directoryHealth;
            NeedsAttentionList.ItemsSource = _needsAttention;
            StaleList.ItemsSource = _staleGroups;
            RunReportList.ItemsSource = _runReports;

            if (OperatingSystem.IsWindows())
            {
                SystemBackupBtn.Visibility = Visibility.Visible;
            }
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Loading the page", () => Page_LoadedCoreAsync(sender, e));
        }

        private async Task Page_LoadedCoreAsync(object sender, RoutedEventArgs e)
        {
            var storyboard = new System.Windows.Media.Animation.Storyboard();

            var fadeIn = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(400),
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };

            System.Windows.Media.Animation.Storyboard.SetTargetProperty(fadeIn, new PropertyPath(UIElement.OpacityProperty));
            storyboard.Children.Add(fadeIn);
            storyboard.Begin(this);

            ApplyProtectionState();

            if (!_initialized)
            {
                _initialized = true;
                await InitializeAsync();
            }
            else
            {
                // The page is cached across navigation, so show what changed while it was away.
                UpdateStatistics();
                await RefreshBackupHistoryAsync();
            }

            StartStatsTimer();

            Wpf.Ui.Appearance.ApplicationThemeManager.Changed -= OnAppThemeChanged;
            Wpf.Ui.Appearance.ApplicationThemeManager.Changed += OnAppThemeChanged;

            _busyBinding ??= new RepositoryBusyBinding(Dispatcher, ApplyBusyState);
            ApplyUpdateBanner();
        }

        // Row and verification colours come from theme brushes resolved when bound, so re-bind on a theme switch.
        private void OnAppThemeChanged(Wpf.Ui.Appearance.ApplicationTheme currentTheme, Color systemAccent)
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                UpdateDirectoryHealth();
                UpdateStatistics();
                ApplyProtectionChip();
            });
        }

        /// <summary>Shows the "ReStore X is available" bar for the pending update (Update now is MSIX-only).</summary>
        private void ApplyUpdateBanner()
        {
            var manager = AppUpdateManager.Instance;
            var pending = manager.PendingForBanner;
            if (pending == null && !manager.IsInstalling)
            {
                UpdateBanner.Visibility = Visibility.Collapsed;
                return;
            }

            UpdateBanner.Visibility = Visibility.Visible;
            UpdateBannerTitle.Text = $"ReStore {pending?.Latest} is available";
            UpdateBannerDetail.Text = manager.IsInstalling
                ? manager.InstallStatus ?? "Installing the update..."
                : pending is { Critical: true }
                    ? "This update is marked critical."
                    : manager.CanInstall ? $"You have {pending?.Current}." : "Download it from the release page; this copy cannot update itself.";
            UpdateNowBtn.Visibility = manager.CanInstall ? Visibility.Visible : Visibility.Collapsed;
            UpdateNowBtn.IsEnabled = !manager.IsInstalling;
            UpdateSkipBtn.IsEnabled = !manager.IsInstalling;
            UpdateCloseBtn.IsEnabled = !manager.IsInstalling;
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Changed -= OnAppThemeChanged;
            _statsTimer?.Stop();
            _busyBinding?.Dispose();
            _busyBinding = null;
        }

        /// <summary>Buttons that start a mutating operation are unavailable while another one runs.</summary>
        private void ApplyBusyState(bool busy, string? reason)
        {
            var tip = busy ? $"{RepositoryBusyBinding.BusyMessage} {reason}".Trim() : null;
            ManualBackupBtn.IsEnabled = !busy;
            ManualBackupBtn.ToolTip = tip;
            SystemBackupBtn.IsEnabled = !busy;
            SystemBackupBtn.ToolTip = tip;
        }

        private async Task InitializeAsync()
        {
            try
            {
                _configManager = await AppServices.GetConfigManagerAsync();
                _state = await AppServices.GetSystemStateAsync();

                var configPath = _configManager.GetConfigFilePath();
                StatusText.Text = $"Config loaded: {configPath}";
                UpdateStatistics();
                await RefreshBackupHistoryAsync();

                await OfferFirstRunSetupIfNeededAsync();
                ShowTelemetryNoticeIfNeeded();
            }
            catch (Exception ex)
            {
                Log($"Failed to load config: {ex.Message}", LogLevel.Error);
                StatusText.Text = "Configuration error - see logs";

                MessageBox.Show(
                    $"ReStore could not load its configuration:\n\n{ex.Message}\n\nUse the Settings page to correct it.",
                    "Configuration Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Runs the setup wizard when the config still looks untouched. Config auto-creation
        /// means a file always exists, so "first run" is inferred from it having no usable
        /// watch directories rather than from the file being absent.
        /// </summary>
        private async Task OfferFirstRunSetupIfNeededAsync()
        {
            if (!NeedsFirstRunSetup())
            {
                return;
            }

            var wizard = new FirstRunWizardWindow(_configManager)
            {
                Owner = Window.GetWindow(this)
            };

            wizard.ShowDialog();

            if (!wizard.SetupCompleted)
            {
                Log("First-run setup skipped. Configure ReStore on the Settings page.", LogLevel.Warning);
                StatusText.Text = "Setup skipped - configure on the Settings page";
                return;
            }

            Log("First-run setup completed.", LogLevel.Info);
            StatusText.Text = "Setup complete";
            UpdateStatistics();

            // The wizard just saved watch directories and storage; start protecting them (honours a user stop).
            if (Protection != null)
            {
                await Protection.ApplyConfigChangeAsync();
            }

            foreach (var folder in wizard.FoldersToBackUpNow)
            {
                await RunFirstBackupAsync(folder);
            }

            await RefreshBackupHistoryAsync();
            UpdateStatistics();
        }

        /// <summary>The one-time telemetry notice for users who did not go through the wizard page (existing users, skipped setup).</summary>
        private void ShowTelemetryNoticeIfNeeded()
        {
            TelemetryNoticeBanner.Visibility = ReStore.Core.src.telemetry.Telemetry.Client.NoticeNeeded ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TelemetryKeepOnBtn_Click(object sender, RoutedEventArgs e)
        {
            ReStore.Core.src.telemetry.Telemetry.Client.MarkNoticeShown("gui-dialog");
            TelemetryNoticeBanner.Visibility = Visibility.Collapsed;
        }

        private async void TelemetryTurnOffBtn_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Turning off usage statistics", async () =>
            {
                if (_configManager == null)
                {
                    return;
                }

                var telemetry = ReStore.Core.src.telemetry.Telemetry.Client;
                _configManager.Telemetry.Enabled = false;
                await _configManager.SaveAsync();
                telemetry.InvalidateConfig();
                telemetry.ClearQueue();
                telemetry.MarkNoticeShown("gui-dialog");
                TelemetryNoticeBanner.Visibility = Visibility.Collapsed;
            });
        }

        private void TelemetryLearnMoreBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ReStore.Core.src.telemetry.TelemetryClient.DocsUrl) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // No browser registered.
            }
        }

        private bool NeedsFirstRunSetup()
        {
            if (_configManager == null || _state == null)
            {
                return false;
            }

            return FirstRunDetector.NeedsSetup(
                _configManager.StorageSources,
                LocalBackupDirectoryExists(),
                _state.GetTotalBackupCount());
        }

        /// <summary>
        /// The local provider needs no credentials, so an existing backup directory is the only
        /// evidence that it was ever actually used.
        /// </summary>
        private bool LocalBackupDirectoryExists()
        {
            if (_configManager == null
                || !_configManager.StorageSources.TryGetValue("local", out var localConfig)
                || string.IsNullOrWhiteSpace(localConfig.Path))
            {
                return false;
            }

            try
            {
                return System.IO.Directory.Exists(ReStore.Core.src.utils.ConfigManager.ExpandConfiguredPath(localConfig.Path));
            }
            catch (Exception ex)
            {
                Log($"Could not check local backup directory: {ex.Message}", LogLevel.Warning);
                return false;
            }
        }

        private async Task RunFirstBackupAsync(string folder)
        {
            var passwordProvider = App.GlobalPasswordProvider ?? new Services.GuiPasswordProvider();

            string? skippedSummary = null;
            var progressWindow = new OperationProgressWindow(
                $"Backing up {System.IO.Path.GetFileName(folder)}",
                _fileLogger);

            progressWindow.RunModal(Window.GetWindow(this), async token =>
            {
                var backup = new Backup(progressWindow, _state!, new SizeAnalyzer(), _configManager, passwordProvider);
                skippedSummary = (await backup.BackupDirectoryAsync(folder, null, progressWindow.BackupProgress, token)).SkippedSummary;
            });

            if (progressWindow.Succeeded)
            {
                Log($"First backup completed: {folder}{(skippedSummary != null ? $" ({skippedSummary})" : string.Empty)}", LogLevel.Info);
            }
            else if (progressWindow.Failure != null)
            {
                Log($"First backup failed for {folder}: {progressWindow.Failure.Message}", LogLevel.Error);
            }
        }

        private void StartStatsTimer()
        {
            _statsFailures.RecordSuccess();
            if (_statsTimer != null)
            {
                _statsTimer.Start();
                return;
            }

            _statsTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _statsTimer.Tick += (_, __) => OnStatsTimerTick();
            _statsTimer.Start();
        }

        private void OnStatsTimerTick()
        {
            // Config is loaded asynchronously; a tick before then (or after a failed load) has nothing to show.
            if (_configManager == null)
            {
                return;
            }

            try
            {
                UpdateStatistics();
                _statsFailures.RecordSuccess();
            }
            catch (Exception ex)
            {
                // Log quietly, never a dialog per tick; stop refreshing once it is clearly not recovering.
                _fileLogger.Log($"Dashboard refresh failed: {ex.Message}", LogLevel.Warning);
                if (_statsFailures.RecordFailure())
                {
                    _statsTimer?.Stop();
                    _fileLogger.Log("Dashboard auto-refresh stopped after repeated failures; reopen the page to retry.", LogLevel.Error);
                }
            }
        }

        private void UpdateStatistics()
        {
            if (_state != null)
            {
                // Locked accessor: the scheduler and watcher append to BackupHistory on their
                // own threads, so enumerating it directly is not safe.
                TotalBackupsText.Text = _state.GetTotalBackupCount().ToString();

                if (_state.LastBackupTime != DateTime.MinValue)
                {
                    var timeSince = DateTime.UtcNow - _state.LastBackupTime;
                    if (timeSince.TotalMinutes < 1)
                        LastBackupText.Text = "Just now";
                    else if (timeSince.TotalHours < 1)
                        LastBackupText.Text = $"{(int)timeSince.TotalMinutes}m ago";
                    else if (timeSince.TotalDays < 1)
                        LastBackupText.Text = $"{(int)timeSince.TotalHours}h ago";
                    else
                        LastBackupText.Text = DisplayTime.Format(_state.LastBackupTime);
                }
                else
                {
                    LastBackupText.Text = "Never";
                }
            }

            WatchedDirsText.Text = _configManager.WatchDirectories.Count.ToString();
            ApplyProtectionChip();

            // Read from the already-loaded config rather than re-reading AppSettings from
            // disk; this runs on a 5-second timer.
            var defaultStorage = _configManager.GlobalStorageType;
            if (!string.IsNullOrEmpty(defaultStorage))
            {
                StorageInfoText.Text = $"Storage: {defaultStorage}";
            }

            UpdateHealthPanel();
        }

        /// <summary>
        /// Surfaces <see cref="SystemState.Telemetry"/>, which is persisted on every operation
        /// but was otherwise never shown anywhere in the app.
        /// </summary>
        private void UpdateHealthPanel()
        {
            if (_state == null || _configManager == null)
            {
                return;
            }

            var telemetry = _state.Telemetry;

            var backup = telemetry.Backup;
            if (backup.SnapshotCount > 0)
            {
                // Exact, not estimated: the difference between the stored bytes every
                // snapshot referenced and the bytes actually transferred.
                var savedBytes = backup.DedupSavedBytes;

                DedupSavingsText.Text = savedBytes > 0 ? ByteFormatter.Format(savedBytes) : "—";
                DedupDetailText.Text =
                    $"{backup.UniqueReusedChunks:N0} of {backup.UniqueChunks:N0} chunks reused across {backup.SnapshotCount:N0} snapshot(s)";
            }
            else
            {
                DedupSavingsText.Text = "—";
                DedupDetailText.Text = "No snapshots recorded yet";
            }

            var verification = telemetry.Verification;
            if (verification.RunCount > 0)
            {
                var passRate = (double)verification.SuccessCount / verification.RunCount;
                VerificationText.Text = $"{passRate:P0} passing";
                VerificationDetailText.Text =
                    $"{verification.SuccessCount:N0}/{verification.RunCount:N0} runs, last {FormatAge(verification.LastUpdatedUtc)}";
                VerificationText.Foreground = verification.SuccessCount == verification.RunCount
                    ? ThemeBrushes.Get("SystemFillColorSuccessBrush", Color.FromRgb(15, 123, 15))
                    : ThemeBrushes.Get("SystemFillColorCriticalBrush", Color.FromRgb(196, 43, 28));
            }
            else
            {
                VerificationText.Text = "Never run";
                VerificationDetailText.Text = _configManager.Verification.Enabled
                    ? "Scheduled verification is on; awaiting first run"
                    : "Scheduled verification is off";
            }

            var restore = telemetry.Restore;
            if (restore.AttemptCount > 0)
            {
                var successRate = (double)restore.SuccessCount / restore.AttemptCount;
                RestoreReliabilityText.Text = $"{successRate:P0} succeeded";
                RestoreReliabilityDetailText.Text =
                    $"{restore.SuccessCount:N0}/{restore.AttemptCount:N0} attempts, last {FormatAge(restore.LastUpdatedUtc)}";
            }
            else
            {
                RestoreReliabilityText.Text = "No attempts";
                RestoreReliabilityDetailText.Text = "Restores have not been exercised";
            }

            UpdateDirectoryHealth();
            UpdateNeedsAttention();
        }

        /// <summary>
        /// Rebuilds the folder list, run list and staleness banner off the UI thread (they read the state under its
        /// lock), then updates the bound collections in place so unchanged rows keep focus and scroll position.
        /// </summary>
        private void UpdateDirectoryHealth()
        {
            if (_healthRefreshing)
            {
                return;
            }

            _healthRefreshing = true;
            _ = RefreshHealthAsync();
        }

        private sealed record HealthData(
            List<DirectoryHealthItem> Folders,
            List<string> Stale,
            List<RunReportItem> Runs,
            BackupRunReport? LastRun);

        private async Task RefreshHealthAsync()
        {
            try
            {
                var state = _state!;
                var watchDirectories = _configManager.WatchDirectories.ToList();
                var globalStorage = _configManager.GlobalStorageType;
                var interval = _configManager.BackupInterval;

                var data = await Task.Run(() => BuildHealthData(state, watchDirectories, globalStorage, interval));
                ApplyHealthData(data);
            }
            catch (Exception ex)
            {
                _fileLogger.Log($"Dashboard health refresh failed: {ex.Message}", LogLevel.Warning);
            }
            finally
            {
                _healthRefreshing = false;
            }
        }

        private static HealthData BuildHealthData(
            SystemState state, List<WatchDirectoryConfig> watchDirectories, string? globalStorage, TimeSpan interval)
        {
            var items = new List<DirectoryHealthItem>();
            var groups = new List<(string Group, TimeSpan? Interval)>();

            foreach (var watchDirectory in watchDirectories)
            {
                if (string.IsNullOrWhiteSpace(watchDirectory.Path))
                {
                    continue;
                }

                string group;
                try
                {
                    group = System.IO.Path.GetFullPath(ReStore.Core.src.utils.ConfigManager.ExpandConfiguredPath(watchDirectory.Path));
                }
                catch
                {
                    group = watchDirectory.Path;
                }

                groups.Add((group, BackupStaleness.IntervalFor(watchDirectory.Schedule, interval, DateTime.UtcNow)));
                var lastBackup = state.GetLastBackupTimeForGroup(group);
                var newestSnapshot = state.GetLatestSnapshotForGroup(group);

                items.Add(new DirectoryHealthItem
                {
                    DirectoryName = System.IO.Path.GetFileName(group.TrimEnd(
                        System.IO.Path.DirectorySeparatorChar,
                        System.IO.Path.AltDirectorySeparatorChar)) is { Length: > 0 } name ? name : group,
                    FullPath = group,
                    RestorePath = newestSnapshot?.Path,
                    StorageType = newestSnapshot?.StorageType ?? watchDirectory.StorageType ?? globalStorage ?? "",
                    LastBackupUtc = lastBackup == DateTime.MinValue ? null : lastBackup
                });
            }

            var stale = BackupStaleness.Evaluate(groups, state, DateTime.UtcNow)
                .Select(s => $"'{s.Group}' - last successful backup {DisplayTime.Format(s.LastSuccessUtc)} ({FormatAge(s.LastSuccessUtc)}).")
                .ToList();

            var runs = state.GetRecentBackupRuns(10);
            return new HealthData(
                [.. items.OrderBy(item => item.LastBackupUtc ?? DateTime.MinValue)],
                stale,
                [.. runs.Select(r => new RunReportItem(r))],
                runs.FirstOrDefault());
        }

        private void ApplyHealthData(HealthData data)
        {
            CollectionSync.Apply(_directoryHealth, data.Folders, (a, b) => a.SameAs(b));
            CollectionSync.Apply(_runReports, data.Runs, (a, b) => a.SameAs(b));
            CollectionSync.Apply(_staleGroups, data.Stale);
            StaleBanner.Visibility = data.Stale.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            LastRunText.Text = data.LastRun is { } last
                ? $"Last run {DisplayTime.Format(last.TimestampUtc)}: {last.Summary}"
                    + (last.FilesSkipped > 0 ? $" - completed with {last.FilesSkipped} skipped" : string.Empty)
                : "No backup has run yet";
            LastRunText.ToolTip = data.LastRun is { } l ? DisplayTime.Tooltip(l.TimestampUtc) : null;

            var staleCount = data.Folders.Count(item =>
                item.LastBackupUtc == null || DateTime.UtcNow - item.LastBackupUtc.Value > TimeSpan.FromDays(7));

            HealthSummaryText.Text = staleCount == 0
                ? $"{data.Folders.Count} watched folder(s), all current"
                : $"{staleCount} of {data.Folders.Count} watched folder(s) need attention";

            UpdateNeedsAttention();
        }

        private void UpdateNeedsAttention()
        {
            var attention = new List<string>();

            var telemetry = _state!.Telemetry;

            foreach (var (category, count) in telemetry.Restore.FailureCategoryCounts.OrderByDescending(entry => entry.Value))
            {
                attention.Add($"Restore failures — {DescribeFailureCategory(category)}: {count:N0}");
            }

            var verification = telemetry.Verification;
            if (verification.MissingChunks > 0)
            {
                attention.Add($"Verification found {verification.MissingChunks:N0} missing chunk object(s) — affected snapshots may not restore.");
            }

            if (verification.InvalidChunks > 0)
            {
                attention.Add($"Verification found {verification.InvalidChunks:N0} corrupted chunk(s).");
            }

            if (verification.InvalidFiles > 0)
            {
                attention.Add($"Verification could not reconstruct {verification.InvalidFiles:N0} file(s).");
            }

            if (!_configManager.Retention.Enabled)
            {
                attention.Add("Retention is off — old snapshots are kept forever and storage will grow without bound.");
            }

            foreach (var stale in _directoryHealth.Where(item => item.LastBackupUtc == null))
            {
                attention.Add($"'{stale.DirectoryName}' has never been backed up.");
            }

            CollectionSync.Apply(_needsAttention, attention);
            NeedsAttentionPanel.Visibility = _needsAttention.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string DescribeFailureCategory(string category) => category switch
        {
            "missing-artifact" => "missing snapshot artifact",
            "decryption-integrity-failure" => "wrong password or tampered data",
            "manifest-integrity-failure" => "manifest integrity",
            "chunk-validation-failure" => "corrupted chunk",
            "file-validation-failure" => "restored file mismatch",
            "restore-conflict" => "destination conflict",
            _ => category
        };

        private static string FormatAge(DateTime utcTimestamp)
        {
            if (utcTimestamp == DateTime.MinValue)
            {
                return "never";
            }

            var age = DateTime.UtcNow - utcTimestamp;
            if (age < TimeSpan.FromMinutes(1)) return "just now";
            if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
            if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h ago";
            return $"{(int)age.TotalDays}d ago";
        }

        private async void HealthRestoreBtn_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Restoring the folder", () => HealthRestoreBtn_ClickCoreAsync(sender, e));
        }

        private async Task HealthRestoreBtn_ClickCoreAsync(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not DirectoryHealthItem health)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(health.RestorePath))
            {
                MessageBox.Show("This folder has no snapshot to restore yet.", "Restore", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await RestoreWithPreviewAsync(new BackupHistoryItem
            {
                Directory = health.DirectoryName,
                Path = health.RestorePath,
                StorageType = health.StorageType,
                Timestamp = health.LastBackupUtc ?? DateTime.UtcNow
            });
        }

        private async Task RefreshBackupHistoryAsync()
        {
            if (_state == null) return;

            var state = _state;
            var globalStorage = _configManager.GlobalStorageType;

            // Off the UI thread, and only the newest entries per group are copied under the state lock.
            var items = await Task.Run(() =>
            {
                var list = new List<BackupHistoryItem>();
                foreach (var kvp in state.GetRecentBackups(10))
                {
                    if (IsSystemBackupGroup(kvp.Key))
                    {
                        continue;
                    }

                    var directory = kvp.Key;
                    foreach (var backup in kvp.Value)
                    {
                        if (backup.ArtifactType != BackupArtifactType.SnapshotManifest
                            && !backup.Path.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase)
                            && !backup.Path.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase)
                            && !backup.Path.EndsWith("\\HEAD", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var storageType = string.IsNullOrWhiteSpace(backup.StorageType)
                            ? globalStorage
                            : backup.StorageType;

                        list.Add(new BackupHistoryItem
                        {
                            Directory = System.IO.Path.GetFileName(directory),
                            Path = backup.Path,
                            StorageType = storageType ?? globalStorage,
                            Timestamp = backup.Timestamp,
                            IsDiff = backup.IsDiff
                        });
                    }
                }

                return list.OrderByDescending(i => i.Timestamp).Take(20).ToList();
            });

            CollectionSync.Apply(_backupHistory, items, (a, b) => a.SameAs(b));
        }

        private void ClearLogs()
        {
            _logBuffer.Clear();
            LogBox.Text = string.Empty;
        }

        public void Log(string message, LogLevel level = LogLevel.Info)
        {
            _fileLogger.Log(message, level);
            var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
            Dispatcher.Invoke(() => AppendToLogPane(line));
        }

        private void AppendToLogPane(string line)
        {
            // Append rather than reassigning Text, which would rebuild the whole string
            // per message, and keep only a bounded tail so a long run cannot grow without
            // limit.
            _logBuffer.AppendLine(line);
            TrimLogBufferIfNeeded();
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        }

        /// <summary>Keeps the visible log to a bounded tail; the full log is on disk.</summary>
        private void TrimLogBufferIfNeeded()
        {
            if (_logBuffer.Length <= MaxLogBufferChars)
            {
                return;
            }

            var retained = _logBuffer.ToString(_logBuffer.Length - KeepLogBufferChars, KeepLogBufferChars);
            var firstLineBreak = retained.IndexOf('\n');
            if (firstLineBreak >= 0 && firstLineBreak + 1 < retained.Length)
            {
                retained = retained[(firstLineBreak + 1)..];
            }

            _logBuffer.Clear();
            _logBuffer.Append(retained);
            LogBox.Text = retained;
        }

        private void ValidateConfig()
        {
            try
            {
                var result = _configManager.ValidateConfiguration();
                if (!result.IsValid)
                {
                    foreach (var e in result.Errors) Log($"Config Error: {e}", LogLevel.Error);
                }
                foreach (var w in result.Warnings) Log($"Config Warning: {w}", LogLevel.Warning);
                foreach (var i in result.Info) Log($"Config Info: {i}", LogLevel.Info);
                StatusText.Text = result.IsValid ? "Configuration is valid" : "Configuration has errors";
            }
            catch (Exception ex)
            {
                Log($"Validation error: {ex.Message}", LogLevel.Error);
            }
        }

        private ProtectionService? Protection => App.Protection;

        /// <summary>
        /// Protection events arrive on the watcher/scheduler threads; everything here hops to the UI thread
        /// with BeginInvoke so a busy UI never blocks a backup.
        /// </summary>
        private void OnProtectionStateChanged(object? sender, EventArgs e)
        {
            _ = Dispatcher.BeginInvoke(ApplyProtectionState);
        }

        private void OnProtectionLogEmitted(string message, LogLevel level)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
            _ = Dispatcher.BeginInvoke(() => AppendToLogPane(line));
        }

        private void OnScheduledBackupCompleted()
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                UpdateStatistics();
                await RefreshBackupHistoryAsync();
            });
        }

        private void ApplyProtectionState()
        {
            var protection = Protection;
            var state = protection?.State ?? ProtectionState.Stopped;
            var active = protection?.IsActive ?? false;

            (StatusText.Text, WatcherStatusText.Text) = state switch
            {
                ProtectionState.Running => ("Watcher running", "Running"),
                ProtectionState.PausedNeedsPassword => ("Encrypted backups paused - unlock needed", "Needs unlock"),
                ProtectionState.Error => ($"Watcher error: {protection?.LastError}", "Error"),
                _ => (protection?.PausedUntilUtc is { } until
                        ? $"Watcher paused {ProtectionPause.Describe(until)}"
                        : "Watcher stopped",
                    "Stopped")
            };

            UpdateStatusIndicator(state == ProtectionState.Running);
            ApplyProtectionChip();
            StartWatcherBtn.IsEnabled = protection != null && !active;
            StopWatcherBtn.IsEnabled = protection != null && active;

            var isPaused = protection?.IsPaused ?? false;
            ResumeBtn.Visibility = isPaused ? Visibility.Visible : Visibility.Collapsed;
            PauseBtn.Visibility = isPaused ? Visibility.Collapsed : Visibility.Visible;
            PauseBtn.IsEnabled = protection != null && active;
            BackupNowBtn.IsEnabled = protection != null && !protection.IsBackingUpNow;
        }

        /// <summary>Header chip: protection state and, while it is running, when the scheduler next runs.</summary>
        private void ApplyProtectionChip()
        {
            var protection = Protection;
            var state = protection?.State ?? ProtectionState.Stopped;
            var paused = protection?.PausedUntilUtc != null && state == ProtectionState.Stopped;

            var (label, hex) = state switch
            {
                ProtectionState.Running => ("Running", "#10B981"),
                ProtectionState.PausedNeedsPassword => ("Needs password", "#F59E0B"),
                ProtectionState.Error => ("Error", "#EF4444"),
                _ => (paused ? "Paused" : "Stopped", "#6B7280")
            };

            var color = (Color)ColorConverter.ConvertFromString(hex);
            ProtectionChipText.Text = label;
            ProtectionChipDot.Fill = new SolidColorBrush(color);
            ProtectionChip.BorderBrush = new SolidColorBrush(color);
            ProtectionChip.Background = new SolidColorBrush(Color.FromArgb(0x1A, color.R, color.G, color.B));
            ProtectionChip.ToolTip = state == ProtectionState.Error ? protection?.LastError : null;

            if (protection?.NextRunUtc is { } next && state is ProtectionState.Running or ProtectionState.PausedNeedsPassword)
            {
                NextRunText.Text = GlobalStatus.NextRunText(next, protection.NextRunHeldByWindow);
                NextRunText.ToolTip = DisplayTime.Tooltip(next);
            }
            else
            {
                NextRunText.Text = paused && protection?.PausedUntilUtc is { } until
                    ? (ProtectionPause.IsIndefinite(until) ? "Paused until you resume" : $"Resumes {DisplayTime.Format(until)}")
                    : string.Empty;
                NextRunText.ToolTip = null;
            }
        }

        private async Task ManualBackupAsync()
        {
            try
            {
                var dialog = new OpenFolderDialog
                {
                    Title = "Select folder to backup",
                    Multiselect = false
                };

                if (dialog.ShowDialog() == true)
                {
                    var folderPath = dialog.FolderName;
                    if (string.IsNullOrEmpty(folderPath))
                    {
                        Log("Invalid folder selected", LogLevel.Error);
                        return;
                    }

                    Log($"Starting manual backup of: {folderPath}", LogLevel.Info);

                    _state ??= await AppServices.GetSystemStateAsync();

                    var sizeAnalyzer = new SizeAnalyzer();
                    var passwordProvider = App.GlobalPasswordProvider ?? new Services.GuiPasswordProvider();

                    string? skippedSummary = null;
                    var progressWindow = new OperationProgressWindow(
                        $"Backing up {System.IO.Path.GetFileName(folderPath)}",
                        _fileLogger);

                    progressWindow.RunModal(Window.GetWindow(this), async token =>
                    {
                        var backup = new Backup(progressWindow, _state, sizeAnalyzer, _configManager, passwordProvider);
                        skippedSummary = (await backup.BackupDirectoryAsync(folderPath, null, progressWindow.BackupProgress, token)).SkippedSummary;
                    });

                    if (progressWindow.Succeeded)
                    {
                        Log($"Manual backup completed: {folderPath}{(skippedSummary != null ? $" ({skippedSummary})" : string.Empty)}", LogLevel.Info);
                        if (skippedSummary != null)
                        {
                            MessageBox.Show($"Backup {skippedSummary}. See the log for details.", "Backup", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                    else if (progressWindow.WasCancelled)
                    {
                        Log($"Manual backup cancelled: {folderPath}", LogLevel.Warning);
                    }
                    else if (progressWindow.Failure != null)
                    {
                        Log($"Manual backup failed: {progressWindow.Failure.Message}", LogLevel.Error);
                        MessageBox.Show($"Backup failed: {progressWindow.Failure.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }

                    await RefreshBackupHistoryAsync();
                    UpdateStatistics();
                }
            }
            catch (Exception ex)
            {
                Log($"Manual backup failed: {ex.Message}", LogLevel.Error);
            }
        }

        private async Task RestoreBackupAsync()
        {
            if (_backupHistory.Count == 0)
            {
                MessageBox.Show("No backups available to restore.", "Restore", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var latestBackup = _backupHistory.FirstOrDefault(item => item.CanRestore);
            if (latestBackup == null)
            {
                Log("No restorable snapshot found in history", LogLevel.Warning);
                return;
            }

            await RestoreWithPreviewAsync(latestBackup);
        }

        /// <summary>Restores a few random files of the newest snapshot to a temporary folder, compares them with the backup and deletes them.</summary>
        private async Task TestRestoreLatestAsync()
        {
            var latestBackup = _backupHistory.FirstOrDefault(item => item.CanRestore);
            if (latestBackup == null)
            {
                MessageBox.Show("No snapshot is available to test yet. Run a backup first.", "Test Restore", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _state ??= await AppServices.GetSystemStateAsync();
            var storageType = latestBackup.StorageType;
            await TestRestoreRunner.RunAsync(
                Window.GetWindow(this),
                _fileLogger,
                latestBackup.Directory,
                latestBackup.Path,
                () => CreateStorageAsync(storageType),
                _state);
        }

        private async void RestoreBackupBtn_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Restoring the backup", () => RestoreBackupBtn_ClickCoreAsync(sender, e));
        }

        private async Task RestoreBackupBtn_ClickCoreAsync(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is BackupHistoryItem backup)
            {
                await RestoreWithPreviewAsync(backup);
            }
        }

        private async Task RestoreWithPreviewAsync(BackupHistoryItem backup)
        {
            try
            {
                if (!backup.CanRestore)
                {
                    MessageBox.Show("This history item is not a snapshot artifact and cannot be restored here.", "Restore", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var dialog = new OpenFolderDialog
                {
                    Title = "Select restore destination folder",
                    Multiselect = false
                };

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                var targetPath = dialog.FolderName;
                if (string.IsNullOrEmpty(targetPath))
                {
                    Log("Invalid restore destination", LogLevel.Error);
                    return;
                }

                _state ??= await AppServices.GetSystemStateAsync();

                var passwordProvider = App.GlobalPasswordProvider ?? new Services.GuiPasswordProvider();

                RestorePreview preview;
                using (var previewStorage = await CreateStorageForHistoryItemAsync(backup))
                {
                    var previewRestore = new Restore(this, previewStorage, passwordProvider, _state);
                    preview = await previewRestore.PreviewRestoreAsync(backup.Path, targetPath);
                }

                var confirm = new OperationConfirm(preview, Window.GetWindow(this));
                if (!confirm.Confirmed)
                {
                    return;
                }

                var progressWindow = new OperationProgressWindow($"Restoring {backup.Directory}", _fileLogger);
                // Read the confirm dialog here: the operation below runs on a pool thread.
                var restoreOptions = new RestoreOptions
                {
                    ConflictPolicy = confirm.ConflictPolicy,
                    RelativePaths = confirm.SelectedRelativePaths
                };
                var backupPath = backup.Path;
                var backupStorageType = backup.StorageType;
                progressWindow.RunModal(Window.GetWindow(this), async token =>
                {
                    using var storage = await CreateStorageAsync(backupStorageType);
                    var restore = new Restore(progressWindow, storage, passwordProvider, _state);

                    var outcome = await restore.RestoreFromBackupAsync(
                        backupPath,
                        targetPath,
                        restoreOptions,
                        progressWindow.RestoreProgress,
                        token);

                    progressWindow.Log(
                        $"Restored {outcome.FilesRestored} file(s); skipped {outcome.FilesSkipped}, kept both {outcome.FilesKeptBoth}, overwrote {outcome.FilesOverwritten}.",
                        LogLevel.Info);
                });

                await SaveStateSafelyAsync();

                if (progressWindow.Succeeded)
                {
                    Log($"Restore completed: {targetPath}", LogLevel.Info);
                    MessageBox.Show("Restore completed successfully!", "Restore", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (progressWindow.WasCancelled)
                {
                    Log("Restore cancelled.", LogLevel.Warning);
                }
                else if (progressWindow.Failure != null)
                {
                    Log($"Restore failed: {progressWindow.Failure.Message}", LogLevel.Error);
                    MessageBox.Show($"Restore failed: {progressWindow.Failure.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                await SaveStateSafelyAsync();

                Log($"Restore failed: {ex.Message}", LogLevel.Error);
                MessageBox.Show($"Restore failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task SaveStateSafelyAsync()
        {
            if (_state == null)
            {
                return;
            }

            try
            {
                // Under the repository lease so a stale in-memory copy never overwrites another process's state.
                await RepositoryCoordinator.Instance.MutateStateAsync(_state, "Saving restore results", () => true);
            }
            catch (Exception saveEx)
            {
                Log($"Failed to persist restore telemetry: {saveEx.Message}", LogLevel.Error);
            }
        }

        /// <summary>Wraps the confirm dialog so both entry points read the same way.</summary>
        private sealed class OperationConfirm
        {
            public OperationConfirm(RestorePreview preview, Window? owner)
            {
                var window = new RestoreConfirmWindow(preview);
                if (owner != null)
                {
                    window.Owner = owner;
                }

                Confirmed = window.ShowDialog() == true && window.Confirmed;
                ConflictPolicy = window.ConflictPolicy;
                SelectedRelativePaths = window.SelectedRelativePaths;
            }

            public bool Confirmed { get; }
            public RestoreConflictPolicy ConflictPolicy { get; }
            public IReadOnlySet<string>? SelectedRelativePaths { get; }
        }

        private static bool IsSystemBackupGroup(string group)
        {
            return group.Equals("system_programs", StringComparison.OrdinalIgnoreCase)
                || group.Equals("system_environment", StringComparison.OrdinalIgnoreCase)
                || group.Equals("system_settings", StringComparison.OrdinalIgnoreCase)
                || group.Equals("system_devtools", StringComparison.OrdinalIgnoreCase);
        }

        private Task<IStorage> CreateStorageForHistoryItemAsync(BackupHistoryItem backup) => CreateStorageAsync(backup.StorageType);

        /// <summary>Takes the storage type as a value so pool-thread callers read the item on the UI thread first.</summary>
        private async Task<IStorage> CreateStorageAsync(string? backupStorageType)
        {
            var storageType = string.IsNullOrWhiteSpace(backupStorageType)
                ? _configManager.GlobalStorageType
                : backupStorageType;

            return await _configManager.CreateStorageAsync(storageType);
        }

        private async Task SystemBackupAsync()
        {
            if (!OperatingSystem.IsWindows())
            {
                MessageBox.Show("System backup is only available on Windows.", "System Backup", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // Mirrors what BackupSystemAsync actually runs, which is driven by the
                // systemBackup.include* switches rather than a fixed list.
                var components = new List<string>();
                if (_configManager.SystemBackup.IncludePrograms)
                {
                    components.Add("• Installed programs list");
                }

                if (_configManager.SystemBackup.IncludeEnvironmentVariables)
                {
                    components.Add("• Environment variables");
                }

                if (_configManager.SystemBackup.IncludeWindowsSettings)
                {
                    components.Add("• Windows settings");
                }

                if (_configManager.SystemBackup.IncludeDevTools)
                {
                    components.Add("• Developer tools settings (terminal, PowerShell profiles, VS Code, git)");
                }

                if (components.Count == 0)
                {
                    MessageBox.Show(
                        "No system backup components are enabled. Enable at least one on the Settings page.",
                        "System Backup",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var result = MessageBox.Show(
                    $"This will backup:\n{string.Join("\n", components)}\n\nContinue?",
                    "System Backup",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    Log("Starting system backup...", LogLevel.Info);

                    _state ??= await AppServices.GetSystemStateAsync();

                    var passwordProvider = App.GlobalPasswordProvider ?? new Services.GuiPasswordProvider();
                    var state = _state;
                    var configManager = _configManager;
                    var progressWindow = new OperationProgressWindow("System backup", _fileLogger);
                    progressWindow.RunModal(Window.GetWindow(this), async token =>
                    {
                        progressWindow.StatusProgress.Report("Backing up the system...");
                        var systemBackup = new SystemBackupManager(progressWindow, configManager, state, passwordProvider);
                        await systemBackup.BackupSystemAsync(token);
                    });

                    if (progressWindow.Succeeded)
                    {
                        Log("System backup completed", LogLevel.Info);
                    }
                    else if (progressWindow.WasCancelled)
                    {
                        Log("System backup cancelled", LogLevel.Warning);
                    }

                    await RefreshBackupHistoryAsync();
                    UpdateStatistics();

                    if (progressWindow.Succeeded)
                    {
                        MessageBox.Show("System backup completed successfully!", "System Backup", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else if (progressWindow.Failure != null)
                    {
                        Log($"System backup failed: {progressWindow.Failure.Message}", LogLevel.Error);
                        MessageBox.Show($"System backup failed: {progressWindow.Failure.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"System backup failed: {ex.Message}", LogLevel.Error);
                MessageBox.Show($"System backup failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateStatusIndicator(bool isRunning)
        {
            Dispatcher.Invoke(() =>
            {
                var color = isRunning ? System.Windows.Media.Color.FromRgb(16, 185, 129) : System.Windows.Media.Color.FromRgb(239, 68, 68);
                var colorAnimation = new System.Windows.Media.Animation.ColorAnimation
                {
                    To = color,
                    Duration = TimeSpan.FromMilliseconds(300),
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
                };

                var brush = StatusIndicator.Fill as System.Windows.Media.SolidColorBrush;
                brush?.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, colorAnimation);

                if (StatusIndicator.Effect is System.Windows.Media.Effects.DropShadowEffect effect)
                {
                    effect.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.ColorProperty, colorAnimation);
                }
            });
        }
    }
}
