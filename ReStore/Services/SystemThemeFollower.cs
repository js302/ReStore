using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ReStore.Services
{
    /// <summary>
    /// Re-applies the Windows theme while the saved preference is "System", so switching Windows between light
    /// and dark restyles the running app. Explicit Light/Dark preferences are left alone.
    /// </summary>
    public sealed class SystemThemeFollower : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private bool _disposed;

        public SystemThemeFollower(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
        {
            // Raised on a SystemEvents thread; the theme switch must run on the UI thread.
            if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle))
            {
                return;
            }

            _ = _dispatcher.BeginInvoke(() =>
            {
                if (_disposed)
                {
                    return;
                }

                var theme = ThemeSettings.Load();
                if (theme.Preference == ThemePreference.System)
                {
                    theme.Apply();
                }
            });
        }

        public void Dispose()
        {
            _disposed = true;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
    }
}
