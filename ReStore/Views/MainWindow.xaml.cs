using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Shapes;
using Wpf.Ui.Appearance;
using ReStore.Services;
using ReStore.Interop;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace ReStore.Views
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        private SolidColorBrush _navHoverBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        private SolidColorBrush _navSelectedBrush = new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));
        private SolidColorBrush _captionHoverBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));

        internal SystemTrayManager? TrayManager { get; set; }

        public void UpdateTrayManager(bool enableTray)
        {
            if (enableTray && TrayManager == null)
            {
                TrayManager = new SystemTrayManager(this);
                SetupTrayManagerActions();
            }
            else if (!enableTray && TrayManager != null)
            {
                TrayManager.Dispose();
                TrayManager = null;
            }
        }

        private void SetupTrayManagerActions()
        {
            if (TrayManager == null || App.Protection == null) return;

            TrayManager.SetProtection(App.Protection);
        }

        // Pages are created once and reused: re-creating them on every click orphaned background
        // work and discarded unsaved state.
        private Pages.DashboardPage? _dashboardPage;
        private Pages.BackupsPage? _backupsPage;
        private Pages.ActivityPage? _activityPage;
        private Pages.SystemRestorePage? _systemRestorePage;
        private Pages.SettingsPage? _settingsPage;
        private Pages.AboutPage? _aboutPage;

        private Pages.DashboardPage DashboardPage => _dashboardPage ??= new Pages.DashboardPage();
        private Pages.BackupsPage BackupsPage => _backupsPage ??= new Pages.BackupsPage();
        private Pages.ActivityPage ActivityPage => _activityPage ??= new Pages.ActivityPage();
        private Pages.SystemRestorePage SystemRestorePage => _systemRestorePage ??= new Pages.SystemRestorePage();
        private Pages.SettingsPage SettingsPage => _settingsPage ??= new Pages.SettingsPage();
        private Pages.AboutPage AboutPage => _aboutPage ??= new Pages.AboutPage();

        public MainWindow()
        {
            InitializeComponent();

            ApplyInitialPlacement();

            ApplicationThemeManager.Changed += OnThemeChanged;
            UpdateThemeColors();

            ContentFrame.Navigate(DashboardPage);

            // Coming back to the window is a good moment to notice that the CLI edited config.json meanwhile.
            Activated += (_, __) => AppServices.RequestExternalConfigCheck();

            if (App.Protection != null)
            {
                App.Protection.StateChanged += OnProtectionStateChanged;
            }

            var navDashboard = (System.Windows.Controls.Button)FindName("NavDashboard");
            var navBackups = (System.Windows.Controls.Button)FindName("NavBackups");
            var navActivity = (System.Windows.Controls.Button)FindName("NavActivity");
            var navSystemRestore = (System.Windows.Controls.Button)FindName("NavSystemRestore");
            var navSettings = (System.Windows.Controls.Button)FindName("NavSettings");
            var navAbout = (System.Windows.Controls.Button)FindName("NavAbout");
            if (navAbout != null) navAbout.Click += async (_, __) => await ErrorReporter.GuardAsync("Navigating", () => NavigateToAsync(AboutPage, navAbout));
            if (navDashboard != null) navDashboard.Click += async (_, __) => await ErrorReporter.GuardAsync("Navigating", () => NavigateToAsync(DashboardPage, navDashboard));
            if (navBackups != null) navBackups.Click += async (_, __) => await ErrorReporter.GuardAsync("Navigating", () => NavigateToAsync(BackupsPage, navBackups));
            if (navActivity != null) navActivity.Click += async (_, __) => await ErrorReporter.GuardAsync("Navigating", () => NavigateToAsync(ActivityPage, navActivity));
            StatusBar.Clicked += (_, __) => navActivity?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (navSystemRestore != null) navSystemRestore.Click += async (_, __) => await ErrorReporter.GuardAsync("Navigating", () => NavigateToAsync(SystemRestorePage, navSystemRestore));
            if (navSettings != null) navSettings.Click += async (_, __) => await ErrorReporter.GuardAsync("Navigating", () => NavigateToAsync(SettingsPage, navSettings));
            if (navDashboard != null) SetSelectedNav(navDashboard);

            var minBtn = (Button)FindName("MinButton");
            var maxBtn = (Button)FindName("MaxButton");
            var closeBtn = (Button)FindName("CloseButton");
            var maxIcon = (TextBlock)FindName("MaxIcon");
            if (minBtn != null) minBtn.Click += (_, __) => WindowState = WindowState.Minimized;
            if (maxBtn != null) maxBtn.Click += (_, __) => ToggleMaximize();
            if (closeBtn != null) closeBtn.Click += OnCloseButtonClick;

            SourceInitialized += OnSourceInitialized;
            Loaded += OnWindowLoaded;
        }

        private WindowState _lastNonMinimizedState = WindowState.Normal;

        /// <summary>Work areas of every connected monitor, in device-independent units.</summary>
        private List<PlacementRect> GetWorkAreas()
        {
            var scale = VisualTreeHelper.GetDpi(this);
            var areas = new List<PlacementRect>();

            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
                {
                    var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                    if (GetMonitorInfo(monitor, ref info))
                    {
                        var work = info.rcWork;
                        areas.Add(new PlacementRect(
                            work.Left / scale.DpiScaleX,
                            work.Top / scale.DpiScaleY,
                            (work.Right - work.Left) / scale.DpiScaleX,
                            (work.Bottom - work.Top) / scale.DpiScaleY));
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Could not enumerate monitors: {ex.Message}");
            }

            if (areas.Count == 0)
            {
                var wa = SystemParameters.WorkArea;
                areas.Add(new PlacementRect(wa.Left, wa.Top, wa.Width, wa.Height));
            }

            return areas;
        }

        /// <summary>
        /// Opens at the last saved placement when it is still on a connected screen, otherwise at 80% of the
        /// primary work area. The minimum size never exceeds the work area, so the window always fits.
        /// </summary>
        private void ApplyInitialPlacement()
        {
            var wa = SystemParameters.WorkArea;
            var primary = new PlacementRect(wa.Left, wa.Top, wa.Width, wa.Height);
            var (minWidth, minHeight) = WindowPlacementPolicy.MinimumSize(primary);
            MinWidth = minWidth;
            MinHeight = minHeight;

            var saved = AppSettings.Load().MainWindowPlacement;
            var (bounds, fromSaved) = WindowPlacementPolicy.Resolve(saved?.Bounds, GetWorkAreas(), primary);

            Width = bounds.Width;
            Height = bounds.Height;

            if (fromSaved)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = bounds.Left;
                Top = bounds.Top;
                if (saved!.Maximized)
                {
                    WindowState = WindowState.Maximized;
                }
            }
        }

        private void SavePlacement()
        {
            try
            {
                var bounds = RestoreBounds;
                if (bounds.IsEmpty)
                {
                    return;
                }

                var settings = AppSettings.Load();
                settings.MainWindowPlacement = new WindowPlacementSettings
                {
                    Left = bounds.Left,
                    Top = bounds.Top,
                    Width = bounds.Width,
                    Height = bounds.Height,
                    Maximized = (WindowState == WindowState.Minimized ? _lastNonMinimizedState : WindowState) == WindowState.Maximized
                };
                settings.Save();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Could not save window placement: {ex.Message}");
            }
        }

        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_GETMINMAXINFO = 0x0024;

            if (msg == WM_GETMINMAXINFO)
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

                if (monitor != IntPtr.Zero)
                {
                    var monitorInfo = new MONITORINFO();
                    monitorInfo.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                    GetMonitorInfo(monitor, ref monitorInfo);

                    var workArea = monitorInfo.rcWork;
                    var monitorArea = monitorInfo.rcMonitor;

                    mmi.ptMaxPosition.X = workArea.Left - monitorArea.Left;
                    mmi.ptMaxPosition.Y = workArea.Top - monitorArea.Top;
                    mmi.ptMaxSize.X = workArea.Right - workArea.Left;
                    mmi.ptMaxSize.Y = workArea.Bottom - workArea.Top;

                    Marshal.StructureToPtr(mmi, lParam, true);
                    handled = true;
                }
            }

            return IntPtr.Zero;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            SetupTaskbarJumpList();
        }

        private void OnProtectionStateChanged(object? sender, EventArgs e)
        {
            // Raised from whichever thread changed the state.
            _ = Dispatcher.BeginInvoke(SetupTaskbarJumpList);
        }

        private void SetupTaskbarJumpList()
        {
            var jumpList = new JumpList();
            jumpList.ShowFrequentCategory = false;
            jumpList.ShowRecentCategory = false;

            var exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
                return;

            var isRunning = App.Protection?.IsActive ?? false;

            if (isRunning)
            {
                var stopTask = new JumpTask
                {
                    Title = "Stop Watcher",
                    Description = "Stop the file watcher service",
                    ApplicationPath = exePath,
                    Arguments = "/stopWatcher",
                    IconResourcePath = exePath
                };
                jumpList.JumpItems.Add(stopTask);
            }
            else
            {
                var startTask = new JumpTask
                {
                    Title = "Start Watcher",
                    Description = "Start the file watcher service",
                    ApplicationPath = exePath,
                    Arguments = "/startWatcher",
                    IconResourcePath = exePath
                };
                jumpList.JumpItems.Add(startTask);
            }

            JumpList.SetJumpList(Application.Current, jumpList);
        }

        public void UpdateJumpList()
        {
            SetupTaskbarJumpList();
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            var handle = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(handle);
            if (source != null)
            {
                source.AddHook(WindowProc);
            }
            WindowEffects.SetImmersiveDarkMode(this);
            UpdateWindowState();
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            UpdateWindowState();
        }

        private void OnCloseButtonClick(object sender, RoutedEventArgs e)
        {
            var settings = AppSettings.Load();
            if (settings.MinimizeToTray && TrayManager != null)
            {
                Hide();
                TrayManager.ShowBalloonTip("ReStore", "Application minimized to tray");
            }
            else
            {
                Close();
            }
        }

        private bool _closeConfirmed;
        private bool _closePromptOpen;

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            var settings = AppSettings.Load();
            var hidesToTray = settings.MinimizeToTray && TrayManager != null && !TrayManager.IsExiting;

            // A real close (not hiding to the tray) must not silently drop typed-but-unsaved Settings.
            // The prompt is async, so cancel this close and repeat it once the user has decided. A session
            // end cannot wait for an answer, so it skips the prompt.
            if (!hidesToTray && !_closeConfirmed && !App.IsSessionEnding && _settingsPage is { HasUnsavedChanges: true })
            {
                e.Cancel = true;
                if (!_closePromptOpen)
                {
                    _ = ConfirmCloseWithUnsavedSettingsAsync(_settingsPage);
                }
                return;
            }

            SavePlacement();

            if (hidesToTray)
            {
                e.Cancel = true;
                Hide();
                TrayManager!.ShowBalloonTip("ReStore", "Application minimized to tray");
            }
            base.OnClosing(e);
        }

        private async Task ConfirmCloseWithUnsavedSettingsAsync(Pages.SettingsPage settingsPage)
        {
            _closePromptOpen = true;
            var proceed = false;
            try
            {
                if (!IsVisible || WindowState == WindowState.Minimized)
                {
                    Show();
                    WindowState = WindowState.Normal;
                }
                Activate();

                proceed = await settingsPage.ConfirmLeaveAsync(closingApp: true);
            }
            catch (Exception ex)
            {
                // Never trap the user in an app that cannot close; the edits stay in the open window on failure.
                System.Diagnostics.Trace.TraceWarning($"Unsaved-settings prompt failed: {ex}");
            }
            finally
            {
                _closePromptOpen = false;
            }

            if (proceed)
            {
                _closeConfirmed = true;
                Close();
            }
            else
            {
                TrayManager?.CancelExit();
            }
        }

        private void OnThemeChanged(ApplicationTheme currentTheme, Color systemAccent)
        {
            UpdateThemeColors();
            WindowEffects.SetImmersiveDarkMode(this);
        }

        private void UpdateThemeColors()
        {
            var isDark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

            if (isDark)
            {
                _navHoverBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
                _navSelectedBrush = new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));
                _captionHoverBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));

                Application.Current.Resources["TitleBarBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x23));
                Application.Current.Resources["SidebarBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
                Application.Current.Resources["MainWindowBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
                Application.Current.Resources["SeparatorBrush"] = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42));
            }
            else
            {
                _navHoverBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00));
                _navSelectedBrush = new SolidColorBrush(Color.FromArgb(0x4D, 0x00, 0x00, 0x00));
                _captionHoverBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0x00, 0x00, 0x00));

                Application.Current.Resources["TitleBarBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xE5));
                Application.Current.Resources["SidebarBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
                Application.Current.Resources["MainWindowBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                Application.Current.Resources["SeparatorBrush"] = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
            }

            UpdateHoverBorders();
        }

        private void UpdateHoverBorders()
        {
            UpdateBorderInVisualTree(this, "NavHover", _navHoverBrush);
            UpdateBorderInVisualTree(this, "CaptionHover", _captionHoverBrush);
        }

        private void UpdateBorderInVisualTree(DependencyObject parent, string tag, Brush brush)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Border border && border.Tag?.ToString() == tag)
                {
                    border.Background = brush;
                }
                UpdateBorderInVisualTree(child, tag, brush);
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
            }
            else if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            var maxIcon = (TextBlock)FindName("MaxIcon");
            if (maxIcon != null) maxIcon.Text = WindowState == WindowState.Maximized ? "\xE923" : "\xE922";
            UpdateWindowState();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState != WindowState.Minimized)
            {
                _lastNonMinimizedState = WindowState;
            }
            UpdateWindowState();
            WindowEffects.SetImmersiveDarkMode(this);
        }

        protected override void OnClosed(EventArgs e)
        {
            ApplicationThemeManager.Changed -= OnThemeChanged;
            if (App.Protection != null)
            {
                App.Protection.StateChanged -= OnProtectionStateChanged;
            }
            base.OnClosed(e);
        }

        private void UpdateWindowState()
        {
            var isMaximized = WindowState == WindowState.Maximized;

            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome != null)
            {
                chrome.CornerRadius = isMaximized ? new CornerRadius(0) : new CornerRadius(8);
            }

            MainGrid.Margin = new Thickness(0);
        }

        private void SetSelectedNav(Button selected)
        {
            var navDashboard = (Button)FindName("NavDashboard");
            var navBackups = (Button)FindName("NavBackups");
            var navActivity = (Button)FindName("NavActivity");
            var navSystemRestore = (Button)FindName("NavSystemRestore");
            var navSettings = (Button)FindName("NavSettings");
            var navAbout = (Button)FindName("NavAbout");
            var buttons = new[] { navDashboard, navBackups, navActivity, navSystemRestore, navSettings, navAbout };
            foreach (var btn in buttons)
            {
                if (btn == null) continue;
                if (btn == selected)
                {
                    btn.Background = _navSelectedBrush;
                    SetAccentBorderVisible(btn, true);
                }
                else
                {
                    btn.Background = Brushes.Transparent;
                    SetAccentBorderVisible(btn, false);
                }
            }
        }

        private void SetAccentBorderVisible(Button button, bool visible)
        {
            if (button.Template == null) return;
            var accentBorder = button.Template.FindName("accentBorder", button) as Rectangle;
            if (accentBorder != null)
            {
                var animation = new System.Windows.Media.Animation.DoubleAnimation
                {
                    To = visible ? 1 : 0,
                    Duration = TimeSpan.FromMilliseconds(200),
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };
                accentBorder.BeginAnimation(UIElement.OpacityProperty, animation);
            }
        }

        /// <summary>
        /// Navigation hook: leaving Settings with typed-but-unsaved values first asks Save / Discard / Cancel.
        /// Cancel keeps the current page and nav selection.
        /// </summary>
        private async Task NavigateToAsync(Page target, Button navButton)
        {
            if (ContentFrame.Content is Pages.SettingsPage current && !ReferenceEquals(current, target)
                && !await current.ConfirmLeaveAsync())
            {
                return;
            }

            NavigateWithTransition(target);
            SetSelectedNav(navButton);
        }

        private void NavigateWithTransition(object page)
        {
            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
            };

            fadeOut.Completed += (s, e) =>
            {
                ContentFrame.Navigate(page);

                var fadeIn = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = TimeSpan.FromMilliseconds(250),
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };

                var slideIn = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 20,
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(250),
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };

                ContentFrame.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                var transform = ContentFrame.RenderTransform as TranslateTransform;
                if (transform != null)
                {
                    transform.BeginAnimation(TranslateTransform.YProperty, slideIn);
                }
            };

            ContentFrame.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }
    }
}
