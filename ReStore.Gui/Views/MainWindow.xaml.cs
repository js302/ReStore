using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Shapes;
using Wpf.Ui.Appearance;
using ReStore.Gui.Services;
using ReStore.Gui.Interop;
using System.Windows.Input;

namespace ReStore.Gui.Views
{
    public partial class MainWindow : Window
    {
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
            if (TrayManager == null) return;

            TrayManager.SetWatcherActions(
                startAction: () =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        Show();
                        WindowState = WindowState.Normal;
                        Activate();
                        var frame = FindName("ContentFrame") as Frame;
                        if (frame?.Content is Pages.DashboardPage dashboard)
                        {
                            var startBtn = dashboard.FindName("StartWatcherBtn") as Button;
                            startBtn?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        }
                        else
                        {
                            frame?.Navigate(new Pages.DashboardPage());
                            Dispatcher.InvokeAsync(async () =>
                            {
                                await System.Threading.Tasks.Task.Delay(100);
                                if (frame?.Content is Pages.DashboardPage db)
                                {
                                    var startBtn = db.FindName("StartWatcherBtn") as Button;
                                    startBtn?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                                }
                            });
                        }
                    });
                },
                stopAction: () =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        var frame = FindName("ContentFrame") as Frame;
                        if (frame?.Content is Pages.DashboardPage dashboard)
                        {
                            var stopBtn = dashboard.FindName("StopWatcherBtn") as Button;
                            stopBtn?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        }
                    });
                },
                isRunning: () => WatcherService.Instance.IsRunning
            );
        }

        public MainWindow()
        {
            InitializeComponent();
            
            ApplicationThemeManager.Changed += OnThemeChanged;
            UpdateThemeColors();
            
            ContentFrame.Navigate(new Pages.DashboardPage());

            var navDashboard = (System.Windows.Controls.Button)FindName("NavDashboard");
            var navBackups = (System.Windows.Controls.Button)FindName("NavBackups");
            var navSystemRestore = (System.Windows.Controls.Button)FindName("NavSystemRestore");
            var navSettings = (System.Windows.Controls.Button)FindName("NavSettings");
            if (navDashboard != null) navDashboard.Click += (_, __) => { NavigateWithTransition(new Pages.DashboardPage()); SetSelectedNav(navDashboard); };
            if (navBackups != null) navBackups.Click += (_, __) => { NavigateWithTransition(new Pages.BackupsPage()); SetSelectedNav(navBackups); };
            if (navSystemRestore != null) navSystemRestore.Click += (_, __) => { NavigateWithTransition(new Pages.SystemRestorePage()); SetSelectedNav(navSystemRestore); };
            if (navSettings != null) navSettings.Click += (_, __) => { NavigateWithTransition(new Pages.SettingsPage()); SetSelectedNav(navSettings); };
            if (navDashboard != null) SetSelectedNav(navDashboard);

            var minBtn = (Button)FindName("MinButton");
            var maxBtn = (Button)FindName("MaxButton");
            var closeBtn = (Button)FindName("CloseButton");
            var maxIcon = (TextBlock)FindName("MaxIcon");
            if (minBtn != null) minBtn.Click += (_, __) => WindowState = WindowState.Minimized;
            if (maxBtn != null) maxBtn.Click += (_, __) => ToggleMaximize();
            if (closeBtn != null) closeBtn.Click += OnCloseButtonClick;

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

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            var settings = AppSettings.Load();
            if (settings.MinimizeToTray && TrayManager != null && !TrayManager.IsExiting)
            {
                e.Cancel = true;
                Hide();
                TrayManager.ShowBalloonTip("ReStore", "Application minimized to tray");
            }
            base.OnClosing(e);
        }

        private void OnThemeChanged(ApplicationTheme currentTheme, Color systemAccent)
        {
            UpdateThemeColors();
        }

        private void UpdateThemeColors()
        {
            var isDark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
            
            if (isDark)
            {
                _navHoverBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
                _navSelectedBrush = new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));
                _captionHoverBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
            }
            else
            {
                _navHoverBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00));
                _navSelectedBrush = new SolidColorBrush(Color.FromArgb(0x4D, 0x00, 0x00, 0x00));
                _captionHoverBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0x00, 0x00, 0x00));
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
            UpdateWindowState();
            WindowEffects.SetImmersiveDarkMode(this);
        }

        private void UpdateWindowState()
        {
            var isMaximized = WindowState == WindowState.Maximized;
            
            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome != null)
            {
                chrome.CornerRadius = isMaximized ? new CornerRadius(0) : new CornerRadius(8);
            }

            if (isMaximized)
            {
                var resizeBorderThickness = chrome?.ResizeBorderThickness.Left ?? 6;
                MainGrid.Margin = new Thickness(
                    resizeBorderThickness,
                    resizeBorderThickness, 
                    resizeBorderThickness, 
                    resizeBorderThickness
                );
            }
            else
            {
                MainGrid.Margin = new Thickness(0);
            }
        }

        private void SetSelectedNav(Button selected)
        {
            var navDashboard = (Button)FindName("NavDashboard");
            var navBackups = (Button)FindName("NavBackups");
            var navSystemRestore = (Button)FindName("NavSystemRestore");
            var navSettings = (Button)FindName("NavSettings");
            var buttons = new[] { navDashboard, navBackups, navSystemRestore, navSettings };
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
