using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Appearance;
using ReStore.Gui.Wpf.Services;
using ReStore.Gui.Wpf.Interop;
using System.Windows.Input;

namespace ReStore.Gui.Wpf.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            ContentFrame.Navigate(new Pages.DashboardPage());

            var navDashboard = (System.Windows.Controls.Button)FindName("NavDashboard");
            var navSettings = (System.Windows.Controls.Button)FindName("NavSettings");
            if (navDashboard != null) navDashboard.Click += (_, __) => { ContentFrame.Navigate(new Pages.DashboardPage()); SetSelectedNav(navDashboard); };
            if (navSettings != null) navSettings.Click += (_, __) => { ContentFrame.Navigate(new Pages.SettingsPage()); SetSelectedNav(navSettings); };
            if (navDashboard != null) SetSelectedNav(navDashboard);

            // Theme selection moved to Settings page

            // Caption buttons
            var minBtn = (Button)FindName("MinButton");
            var maxBtn = (Button)FindName("MaxButton");
            var closeBtn = (Button)FindName("CloseButton");
            var maxIcon = (TextBlock)FindName("MaxIcon");
            if (minBtn != null) minBtn.Click += (_, __) => WindowState = WindowState.Minimized;
            if (maxBtn != null) maxBtn.Click += (_, __) =>
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                if (maxIcon != null) maxIcon.Text = WindowState == WindowState.Maximized ? "\xE923" : "\xE922";
            };
            if (closeBtn != null) closeBtn.Click += (_, __) => Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                var maxIcon = (TextBlock)FindName("MaxIcon");
                if (maxIcon != null) maxIcon.Text = WindowState == WindowState.Maximized ? "\xE923" : "\xE922";
            }
            else if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            WindowEffects.SetImmersiveDarkMode(this);
        }
        // Navigation handled via button click handlers above

        // Theme switching is handled by ApplicationThemeManager

        private void SetSelectedNav(Button selected)
        {
            var navDashboard = (Button)FindName("NavDashboard");
            var navSettings = (Button)FindName("NavSettings");
            var buttons = new[] { navDashboard, navSettings };
            foreach (var btn in buttons)
            {
                if (btn == null) continue;
                if (btn == selected)
                {
                    btn.Background = (System.Windows.Media.Brush)FindResource("CardBackgroundFillColorSecondaryBrush");
                }
                else
                {
                    btn.Background = System.Windows.Media.Brushes.Transparent;
                }
            }
        }
    }
}
