using System.Windows;
using ReStore.Core.src.updates;
using ReStore.Services;

namespace ReStore.Views.Pages
{
    /// <summary>The "Updates" part of Application Behavior: the two update options (saved at once) and "Check now".</summary>
    public partial class SettingsPage
    {
        private void WireUpdateSettings()
        {
            CheckForUpdatesCheckBox.Click += async (_, __) => await ErrorReporter.GuardAsync("Changing update checks", async () =>
            {
                if (_isLoading || _configManager == null) return;

                var previous = _configManager.Updates.CheckAutomatically;
                _configManager.Updates.CheckAutomatically = CheckForUpdatesCheckBox.IsChecked == true;
                await SaveUpdateSettingAsync(() => _configManager.Updates.CheckAutomatically = previous, () => CheckForUpdatesCheckBox.IsChecked = previous);
            });

            IncludePrereleasesCheckBox.Click += async (_, __) => await ErrorReporter.GuardAsync("Changing update checks", async () =>
            {
                if (_isLoading || _configManager == null) return;

                var previous = _configManager.Updates.IncludePrereleases;
                _configManager.Updates.IncludePrereleases = IncludePrereleasesCheckBox.IsChecked == true;
                await SaveUpdateSettingAsync(() => _configManager.Updates.IncludePrereleases = previous, () => IncludePrereleasesCheckBox.IsChecked = previous);
            });

            CheckNowButton.Click += async (_, __) => await ErrorReporter.GuardAsync("Checking for updates", async () =>
            {
                CheckNowButton.IsEnabled = false;
                UpdateStatusText.Text = "Checking...";
                try
                {
                    var result = await AppUpdateManager.Instance.CheckAsync(manual: true);
                    UpdateStatusText.Text = result == null ? "A check is already running." : AppUpdateManager.Instance.DescribeStatus();
                }
                finally
                {
                    CheckNowButton.IsEnabled = true;
                }
            });

            AppUpdateManager.Instance.Changed += () => _ = Dispatcher.BeginInvoke(() => UpdateStatusText.Text = AppUpdateManager.Instance.DescribeStatus());
        }

        private async Task SaveUpdateSettingAsync(Action revertConfig, Action revertBox)
        {
            try
            {
                await SaveConfigAndApplyAsync();
            }
            catch (Exception ex) when (ex is not ProtectionApplyException)
            {
                // Not saved: put the setting and the box back.
                revertConfig();
                revertBox();
                throw;
            }
        }

        private void LoadUpdateSettings()
        {
            CheckForUpdatesCheckBox.IsChecked = _configManager.Updates.CheckAutomatically;
            IncludePrereleasesCheckBox.IsChecked = _configManager.Updates.IncludePrereleases;
            UpdateStatusText.Text = AppUpdateManager.Instance.DescribeStatus();
        }
    }
}
