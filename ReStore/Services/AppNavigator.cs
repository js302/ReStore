using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ReStore.Core.src.monitoring;

namespace ReStore.Services
{
    /// <summary>Brings the main window forward and opens the page a notification points at.</summary>
    internal static class AppNavigator
    {
        public static void Open(NotificationTarget target)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                return;
            }

            _ = dispatcher.BeginInvoke(() =>
            {
                if (Application.Current?.MainWindow is not { } window)
                {
                    return;
                }

                if (!window.IsVisible)
                {
                    window.Show();
                }

                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                window.Activate();

                // The sidebar buttons own navigation (selection highlight included), so click the same ones.
                // Coupled to the NavDashboard/NavBackups/NavActivity/NavSettings names in MainWindow.xaml: keep them stable
                // (a MainWindow.NavigateTo API would be cleaner, but MainWindow is outside this task).
                var buttonName = target switch
                {
                    NotificationTarget.Backups => "NavBackups",
                    NotificationTarget.Settings => "NavSettings",
                    NotificationTarget.Dashboard => "NavDashboard",
                    NotificationTarget.Activity => "NavActivity",
                    _ => null
                };

                if (buttonName != null && window.FindName(buttonName) is Button button)
                {
                    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                }
            });
        }
    }
}
