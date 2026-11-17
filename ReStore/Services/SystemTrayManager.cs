using System;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using ReStore.Core.src.monitoring;

namespace ReStore.Services
{
    public class SystemTrayManager : IDisposable
    {
        private readonly TaskbarIcon _taskbarIcon;
        private readonly Window _mainWindow;
        private bool _isExiting;
        private ProtectionService? _protection;
        private System.Windows.Controls.MenuItem? _startMenuItem;
        private System.Windows.Controls.MenuItem? _stopMenuItem;
        private System.Windows.Controls.MenuItem? _backupNowMenuItem;
        private System.Windows.Controls.MenuItem? _pauseMenuItem;
        private System.Windows.Controls.MenuItem? _resumeMenuItem;

        public SystemTrayManager(Window mainWindow)
        {
            _mainWindow = mainWindow;
            _taskbarIcon = new TaskbarIcon
            {
                Icon = GetApplicationIcon(),
                ToolTipText = "ReStore"
            };

            _taskbarIcon.TrayLeftMouseDown += OnTrayIconLeftClick;
            _taskbarIcon.TrayRightMouseDown += OnTrayIconRightClick;
            _taskbarIcon.TrayBalloonTipClicked += (_, __) => BalloonClicked?.Invoke();

            BuildContextMenu();
        }

        /// <summary>Binds the Start/Stop menu items and tooltip to the app-wide protection service.</summary>
        public void SetProtection(ProtectionService protection)
        {
            if (_protection != null)
            {
                _protection.StateChanged -= OnProtectionStateChanged;
                _protection.BackupNowFinished -= OnBackupNowFinished;
            }

            _protection = protection;
            _protection.StateChanged += OnProtectionStateChanged;
            _protection.BackupNowFinished += OnBackupNowFinished;
            BuildContextMenu();
        }

        private void OnProtectionStateChanged(object? sender, EventArgs e)
        {
            // Raised from whichever thread changed the state.
            _ = _mainWindow.Dispatcher.BeginInvoke(RefreshProtectionUi);
        }

        private void OnBackupNowFinished(string summary, bool failed)
        {
            _ = _mainWindow.Dispatcher.BeginInvoke(() =>
                ShowBalloonTip("Back up now", summary, failed ? BalloonIcon.Warning : BalloonIcon.Info));
        }

        private void RefreshProtectionUi()
        {
            if (_protection == null)
            {
                return;
            }

            var active = _protection.IsActive;
            if (_startMenuItem != null) _startMenuItem.IsEnabled = !active;
            if (_stopMenuItem != null) _stopMenuItem.IsEnabled = active || _protection.IsPaused;
            if (_backupNowMenuItem != null) _backupNowMenuItem.IsEnabled = !_protection.IsBackingUpNow;
            if (_pauseMenuItem != null) _pauseMenuItem.IsEnabled = active;
            if (_resumeMenuItem != null) _resumeMenuItem.IsEnabled = _protection.IsPaused;

            _taskbarIcon.ToolTipText = _protection.State switch
            {
                ProtectionState.Running => "ReStore - protection running",
                ProtectionState.PausedNeedsPassword => "ReStore - unlock needed",
                ProtectionState.Error => "ReStore - protection error",
                _ => _protection.PausedUntilUtc is { } until
                    ? $"ReStore - protection paused {ProtectionPause.Describe(until)}"
                    : "ReStore - protection stopped"
            };
        }

        private void BuildContextMenu()
        {
            var contextMenu = new System.Windows.Controls.ContextMenu();

            _startMenuItem = null;
            _stopMenuItem = null;
            _backupNowMenuItem = null;
            _pauseMenuItem = null;
            _resumeMenuItem = null;

            if (_protection != null)
            {
                var protection = _protection;

                _startMenuItem = new System.Windows.Controls.MenuItem { Header = "Start Watcher" };
                _startMenuItem.Click += async (_, __) => await ErrorReporter.GuardAsync("Starting the watcher", () => protection.StartAsync());
                contextMenu.Items.Add(_startMenuItem);

                _stopMenuItem = new System.Windows.Controls.MenuItem { Header = "Stop Watcher" };
                _stopMenuItem.Click += async (_, __) => await ErrorReporter.GuardAsync("Stopping the watcher", () => protection.StopAsync());
                contextMenu.Items.Add(_stopMenuItem);

                contextMenu.Items.Add(new System.Windows.Controls.Separator());

                _backupNowMenuItem = new System.Windows.Controls.MenuItem { Header = "Back up now" };
                _backupNowMenuItem.Click += async (_, __) => await ErrorReporter.GuardAsync("Backing up now", () => protection.BackupNowAsync());
                contextMenu.Items.Add(_backupNowMenuItem);

                _pauseMenuItem = new System.Windows.Controls.MenuItem { Header = "Pause protection" };
                AddPauseItem(_pauseMenuItem, "For 1 hour", () => protection.PauseFor(ProtectionPause.OneHour));
                AddPauseItem(_pauseMenuItem, "Until tomorrow (8:00)", () => protection.PauseUntilTomorrowAsync());
                AddPauseItem(_pauseMenuItem, "Until I resume", () => protection.PauseUntilResumedAsync());
                contextMenu.Items.Add(_pauseMenuItem);

                _resumeMenuItem = new System.Windows.Controls.MenuItem { Header = "Resume protection" };
                _resumeMenuItem.Click += async (_, __) => await ErrorReporter.GuardAsync("Resuming protection", () => protection.ResumeAsync());
                contextMenu.Items.Add(_resumeMenuItem);

                contextMenu.Items.Add(new System.Windows.Controls.Separator());
            }

            var showMenuItem = new System.Windows.Controls.MenuItem { Header = "Show Window" };
            showMenuItem.Click += (_, __) => ShowWindow();
            contextMenu.Items.Add(showMenuItem);

            contextMenu.Items.Add(new System.Windows.Controls.Separator());

            var exitMenuItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
            exitMenuItem.Click += (_, __) => ExitApplication();
            contextMenu.Items.Add(exitMenuItem);

            _taskbarIcon.ContextMenu = contextMenu;
            RefreshProtectionUi();
        }

        private static void AddPauseItem(System.Windows.Controls.MenuItem parent, string header, Func<Task> action)
        {
            var item = new System.Windows.Controls.MenuItem { Header = header };
            item.Click += async (_, __) => await ErrorReporter.GuardAsync("Pausing protection", action);
            parent.Items.Add(item);
        }

        private void OnTrayIconLeftClick(object? sender, RoutedEventArgs e)
        {
            if (_mainWindow.WindowState == WindowState.Minimized || !_mainWindow.IsVisible)
            {
                ShowWindow();
            }
            else
            {
                HideWindow();
            }
        }

        private void OnTrayIconRightClick(object? sender, RoutedEventArgs e)
        {
            if (_taskbarIcon.ContextMenu != null)
            {
                _taskbarIcon.ContextMenu.IsOpen = true;
            }
        }

        /// <summary>The user clicked the balloon this icon last showed.</summary>
        public event Action? BalloonClicked;

        public void ShowWindow()
        {
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }

        public void HideWindow()
        {
            _mainWindow.Hide();
        }

        public void ShowBalloonTip(string title, string message)
        {
            _taskbarIcon.ShowBalloonTip(title, message, Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
        }

        public void ShowBalloonTip(string title, string message, BalloonIcon icon)
        {
            _taskbarIcon.ShowBalloonTip(title, message, icon);
        }

        public void ExitApplication()
        {
            _isExiting = true;
            _mainWindow.Close();
        }

        /// <summary>The user declined to close (for example chose Cancel on the unsaved-settings prompt).</summary>
        public void CancelExit()
        {
            _isExiting = false;
        }

        public bool IsExiting => _isExiting;

        private System.Drawing.Icon GetApplicationIcon()
        {
            return AppIcon.Load();
        }

        public void Dispose()
        {
            if (_protection != null)
            {
                _protection.StateChanged -= OnProtectionStateChanged;
                _protection.BackupNowFinished -= OnBackupNowFinished;
                _protection = null;
            }

            _taskbarIcon?.Dispose();
        }
    }
}
