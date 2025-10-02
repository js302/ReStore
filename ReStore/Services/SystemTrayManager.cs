using System;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace ReStore.Services
{
    public class SystemTrayManager : IDisposable
    {
        private readonly TaskbarIcon _taskbarIcon;
        private readonly Window _mainWindow;
        private bool _isExiting;
        private Action? _startWatcherAction;
        private Action? _stopWatcherAction;
        private Func<bool>? _isWatcherRunning;

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

            BuildContextMenu();
        }

        public void SetWatcherActions(Action startAction, Action stopAction, Func<bool> isRunning)
        {
            _startWatcherAction = startAction;
            _stopWatcherAction = stopAction;
            _isWatcherRunning = isRunning;
            BuildContextMenu();
        }

        private void BuildContextMenu()
        {
            var contextMenu = new System.Windows.Controls.ContextMenu();

            if (_startWatcherAction != null && _stopWatcherAction != null && _isWatcherRunning != null)
            {
                var startWatcherMenuItem = new System.Windows.Controls.MenuItem { Header = "Start Watcher" };
                startWatcherMenuItem.Click += (_, __) =>
                {
                    _startWatcherAction?.Invoke();
                };
                contextMenu.Items.Add(startWatcherMenuItem);

                var stopWatcherMenuItem = new System.Windows.Controls.MenuItem { Header = "Stop Watcher" };
                stopWatcherMenuItem.Click += (_, __) =>
                {
                    _stopWatcherAction?.Invoke();
                };
                contextMenu.Items.Add(stopWatcherMenuItem);

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

        public void ExitApplication()
        {
            _isExiting = true;
            _mainWindow.Close();
        }

        public bool IsExiting => _isExiting;

        private System.Drawing.Icon GetApplicationIcon()
        {
            try
            {
                var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/ReStore;component/icon.ico"))?.Stream;
                if (iconStream != null)
                {
                    return new System.Drawing.Icon(iconStream);
                }
            }
            catch { }

            return System.Drawing.SystemIcons.Application;
        }

        public void Dispose()
        {
            _taskbarIcon?.Dispose();
        }
    }
}
