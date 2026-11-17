using ReStore.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using ReStore.Core.src.utils;

namespace ReStore.Views.Windows
{
    /// <summary>
    /// Three-step first-run setup: what to back up, where it goes (with a live connection
    /// test), and optional encryption.
    /// </summary>
    public partial class FirstRunWizardWindow : Window
    {
        private readonly ConfigManager _configManager;
        private readonly ObservableCollection<FolderChoice> _folders = [];
        private readonly ObservableCollection<CredentialField> _credentials = [];

        private int _step;
        private bool _connectionTested;
        private bool _connectionOk;
        private string? _encryptionPassword;
        private byte[]? _encryptionSalt;

        public FirstRunWizardWindow(ConfigManager configManager)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));

            InitializeComponent();

            FolderList.ItemsSource = _folders;
            CredentialFields.ItemsSource = _credentials;

            SeedDefaultFolders();
            SeedProviders();
            foreach (var box in PresetBoxes)
            {
                box.IsChecked = _configManager.ExcludePresets.Contains((string)box.Tag, StringComparer.OrdinalIgnoreCase);
            }

            UpdateStepChrome();
        }

        private IEnumerable<System.Windows.Controls.CheckBox> PresetBoxes => [PresetOfficeTempCheck, PresetSystemCheck, PresetCachesCheck, PresetDevCheck];

        /// <summary>True when the user completed the wizard and config was saved.</summary>
        public bool SetupCompleted { get; private set; }

        /// <summary>Folders to back up immediately, when the user asked for that.</summary>
        public List<string> FoldersToBackUpNow { get; } = [];

        private void SeedDefaultFolders()
        {
            var candidates = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            };

            foreach (var path in candidates.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct())
            {
                _folders.Add(new FolderChoice(path) { IsSelected = Directory.Exists(path) });
            }

            UpdateFolderSummary();
        }

        private void SeedProviders()
        {
            // "local" first: it is the only provider that needs no credentials, so it is the
            // one a first-time user can actually finish with.
            var providerKeys = _configManager.StorageSources.Keys
                .OrderBy(key => key.Equals("local", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (providerKeys.Count == 0)
            {
                providerKeys.Add("local");
            }

            foreach (var key in providerKeys)
            {
                ProviderCombo.Items.Add(key);
            }

            ProviderCombo.SelectedIndex = 0;
        }

        private string SelectedProvider => ProviderCombo.SelectedItem?.ToString() ?? "local";

        private void ProviderCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            _connectionTested = false;
            _connectionOk = false;
            TestResultText.Text = string.Empty;

            var provider = SelectedProvider;
            var isLocal = provider.Equals("local", StringComparison.OrdinalIgnoreCase);

            LocalPathPanel.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;

            if (isLocal)
            {
                var existing = _configManager.StorageSources.TryGetValue(provider, out var config)
                    ? config.Path
                    : string.Empty;

                LocalPathBox.Text = string.IsNullOrWhiteSpace(existing)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ReStoreBackups")
                    : Environment.ExpandEnvironmentVariables(existing);
            }

            ProviderHintText.Text = provider.ToLowerInvariant() switch
            {
                "local" => "Backs up to a folder on this machine or an attached drive. Use a different physical disk than the one you are protecting.",
                "github" => "Small backups only: each file is buffered in memory as base64 and the GitHub Contents API rejects anything over 100 MB.",
                "gdrive" => "Requires an OAuth client ID and secret from the Google Cloud console.",
                "s3" => "Requires an access key, secret, region and bucket that already exists.",
                "azure" => "Requires a storage account connection string and a container name.",
                "gcp" => "Requires a bucket name and a service-account key file.",
                "dropbox" => "Requires an access token, or an app key/secret plus refresh token.",
                "sftp" => "Requires a host and username, plus either a password or a private key.",
                "b2" => "Requires a key ID, application key and bucket name.",
                _ => "Fill in the credentials this provider needs, then test the connection."
            };

            BuildCredentialFields(provider);
        }

        private void BuildCredentialFields(string provider)
        {
            _credentials.Clear();

            if (provider.Equals("local", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!_configManager.StorageSources.TryGetValue(provider, out var config))
            {
                return;
            }

            foreach (var (key, value) in config.Options.OrderBy(option => option.Key, StringComparer.OrdinalIgnoreCase))
            {
                // Placeholder values from the example config would otherwise look like real
                // credentials in the fields.
                var isPlaceholder = string.IsNullOrWhiteSpace(value)
                    || value.Contains("your_", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("your-", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("optional-", StringComparison.OrdinalIgnoreCase);

                _credentials.Add(new CredentialField(key, isPlaceholder ? string.Empty : value));
            }
        }

        private void AddFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select a folder to back up",
                Multiselect = true
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            foreach (var folder in dialog.FolderNames)
            {
                if (_folders.Any(choice => string.Equals(choice.Path, folder, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _folders.Add(new FolderChoice(folder) { IsSelected = true });
            }

            UpdateFolderSummary();
        }

        private void UpdateFolderSummary()
        {
            var selected = _folders.Count(choice => choice.IsSelected);
            FolderSummaryText.Text = selected == 0
                ? "No folders selected yet."
                : $"{selected} folder(s) will be backed up.";
        }

        private void BrowseLocalBtn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select the folder where backups should be stored",
                Multiselect = false
            };

            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                LocalPathBox.Text = dialog.FolderName;
                _connectionTested = false;
                TestResultText.Text = string.Empty;
            }
        }

        private async void TestConnectionBtn_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Testing the connection", () => TestConnectionBtn_ClickCoreAsync(sender, e));
        }

        private async Task TestConnectionBtn_ClickCoreAsync(object sender, RoutedEventArgs e)
        {
            TestConnectionBtn.IsEnabled = false;
            TestResultText.Text = "Testing...";

            try
            {
                ApplyDestinationToConfig();

                using var storage = await _configManager.CreateStorageAsync(SelectedProvider);

                // Same lightweight probe the Settings page uses: Exists on a path that is
                // not there must succeed without throwing.
                await storage.ExistsAsync("restore_probe.txt");

                _connectionOk = true;
                TestResultText.Text = $"{SelectedProvider} is reachable.";
            }
            catch (Exception ex)
            {
                _connectionOk = false;
                TestResultText.Text = $"Failed: {ex.Message}";
            }
            finally
            {
                _connectionTested = true;
                TestConnectionBtn.IsEnabled = true;
            }
        }

        private void EnableEncryptionCheck_Click(object sender, RoutedEventArgs e)
        {
            if (EnableEncryptionCheck.IsChecked != true)
            {
                _encryptionPassword = null;
                _encryptionSalt = null;
                EncryptionStatusText.Text = string.Empty;
                return;
            }

            var setup = new EncryptionSetupWindow { Owner = this };
            if (setup.ShowDialog() == true && !string.IsNullOrEmpty(setup.Password) && setup.Salt != null)
            {
                _encryptionPassword = setup.Password;
                _encryptionSalt = setup.Salt;
                EncryptionStatusText.Text = "Encryption password set. Store it somewhere safe.";
                return;
            }

            EnableEncryptionCheck.IsChecked = false;
            _encryptionPassword = null;
            _encryptionSalt = null;
            EncryptionStatusText.Text = string.Empty;
        }

        private void BackBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_step == 0)
            {
                return;
            }

            _step--;
            UpdateStepChrome();
        }

        private async void NextBtn_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Moving to the next step", () => NextBtn_ClickCoreAsync(sender, e));
        }

        private async Task NextBtn_ClickCoreAsync(object sender, RoutedEventArgs e)
        {
            if (!ValidateCurrentStep())
            {
                return;
            }

            if (_step < 2)
            {
                _step++;
                UpdateStepChrome();
                return;
            }

            await FinishAsync();
        }

        private bool ValidateCurrentStep()
        {
            switch (_step)
            {
                case 0:
                    if (!_folders.Any(choice => choice.IsSelected))
                    {
                        MessageBox.Show(
                            "Select at least one folder to back up.",
                            "Nothing selected",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        return false;
                    }

                    var missing = _folders.Where(choice => choice.IsSelected && !Directory.Exists(choice.Path)).ToList();
                    if (missing.Count > 0)
                    {
                        var proceed = MessageBox.Show(
                            $"{missing.Count} selected folder(s) do not exist yet. Keep them anyway?",
                            "Folders not found",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);

                        if (proceed != MessageBoxResult.Yes)
                        {
                            return false;
                        }
                    }

                    return true;

                case 1:
                    if (SelectedProvider.Equals("local", StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrWhiteSpace(LocalPathBox.Text))
                    {
                        MessageBox.Show(
                            "Choose a folder where backups should be stored.",
                            "Destination required",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        return false;
                    }

                    // A failed or untested connection needs an explicit override, so nobody
                    // finishes setup with a destination that cannot actually be written to.
                    if (!_connectionTested || !_connectionOk)
                    {
                        var message = _connectionTested
                            ? "The connection test failed. Continue anyway? Backups will fail until this is fixed."
                            : "The connection has not been tested yet. Continue without testing?";

                        var proceed = MessageBox.Show(
                            message,
                            "Connection not verified",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);

                        return proceed == MessageBoxResult.Yes;
                    }

                    return true;

                default:
                    return true;
            }
        }

        private void UpdateStepChrome()
        {
            FoldersStep.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
            DestinationStep.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
            ProtectionStep.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;

            (StepTitleText.Text, StepSubtitleText.Text) = _step switch
            {
                0 => ("What should ReStore back up?", "Pick the folders you want protected."),
                1 => ("Where should backups go?", "Choose a destination and confirm ReStore can reach it."),
                _ => ("Protect your backups", "Optional, but decide now rather than later.")
            };

            StepCounterText.Text = $"Step {_step + 1} of 3";
            BackBtn.IsEnabled = _step > 0;
            NextBtn.Content = _step == 2 ? "Finish" : "Next";

            if (_step == 0)
            {
                UpdateFolderSummary();
            }
        }

        private void ApplyDestinationToConfig()
        {
            var provider = SelectedProvider;

            if (!_configManager.StorageSources.TryGetValue(provider, out var storageConfig))
            {
                storageConfig = new StorageConfig();
                _configManager.StorageSources[provider] = storageConfig;
            }

            if (provider.Equals("local", StringComparison.OrdinalIgnoreCase))
            {
                var path = LocalPathBox.Text.Trim();
                storageConfig.Path = path;
                storageConfig.Options["path"] = path;
            }
            else
            {
                foreach (var field in _credentials)
                {
                    storageConfig.Options[field.Key] = field.Value ?? string.Empty;
                }

                // Same default as Settings: literal secrets are saved as dpapi: values on Windows.
                if (ReStore.Core.src.utils.DpapiSecretProtector.Instance.IsAvailable)
                {
                    ReStore.Core.src.utils.SecretReferences.ProtectSecretOptions(storageConfig.Options);
                }
            }

            _configManager.SetGlobalStorageType(provider);
        }

        private async Task FinishAsync()
        {
            NextBtn.IsEnabled = false;

            try
            {
                ApplyDestinationToConfig();

                var selectedFolders = _folders.Where(choice => choice.IsSelected).Select(choice => choice.Path).ToList();

                _configManager.WatchDirectories.Clear();
                foreach (var folder in selectedFolders)
                {
                    _configManager.WatchDirectories.Add(new WatchDirectoryConfig
                    {
                        Path = folder,
                        StorageType = null
                    });
                }

                if (EnableEncryptionCheck.IsChecked == true && _encryptionSalt != null && !string.IsNullOrEmpty(_encryptionPassword))
                {
                    EncryptionConfigurator.Enable(_configManager, ReStore.Services.AppServices.Logger, _encryptionPassword, _encryptionSalt);
                }
                else
                {
                    _configManager.Encryption.Enabled = false;
                }

                _configManager.Retention.Enabled = EnableRetentionCheck.IsChecked == true;
                _configManager.Telemetry.Enabled = TelemetryCheck.IsChecked == true;

                // Presets this version does not offer a checkbox for stay as they are.
                _configManager.ExcludePresets.RemoveAll(name => PresetBoxes.Any(box => string.Equals((string)box.Tag, name, StringComparison.OrdinalIgnoreCase)));
                _configManager.ExcludePresets.AddRange(PresetBoxes.Where(box => box.IsChecked == true).Select(box => (string)box.Tag));

                await _configManager.SaveAsync();

                // The wizard is the first-run notice: collection (if kept on) starts with the next launch.
                var telemetry = ReStore.Core.src.telemetry.Telemetry.Client;
                telemetry.InvalidateConfig();
                if (telemetry.StateStore.Load().NoticeShownUtc == null)
                {
                    telemetry.MarkNoticeShown("gui-wizard");
                }

                if (RunFirstBackupCheck.IsChecked == true)
                {
                    FoldersToBackUpNow.AddRange(selectedFolders.Where(Directory.Exists));
                }

                if (_encryptionPassword != null && App.GlobalPasswordProvider != null)
                {
                    // Seeds the session so the first backup does not immediately re-prompt.
                    App.GlobalPasswordProvider.SetPassword(_encryptionPassword);
                }

                SetupCompleted = true;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Could not save the configuration: {ex.Message}",
                    "Setup failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                NextBtn.IsEnabled = true;
            }
        }

        private void TelemetryLearnMoreLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ReStore.Core.src.telemetry.TelemetryClient.DocsUrl) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // No browser registered; the address is also in the docs.
            }
        }

        private void ConnectExistingBtn_Click(object sender, RoutedEventArgs e)
        {
            var window = new ConnectExistingBackupsWindow { Owner = this };
            window.ShowDialog();

            if (window.AdoptedAny)
            {
                MessageBox.Show(
                    "Your existing backups were added to this PC's history. You can finish setup now to start backing up again, or skip it and set up folders later in Settings.",
                    "Backups connected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        private void SkipBtn_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "ReStore will start with default settings and back nothing up until you configure it on the Settings page. Skip setup?",
                "Skip setup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            DialogResult = false;
            Close();
        }

        private sealed class FolderChoice(string path) : INotifyPropertyChanged
        {
            private bool _isSelected;

            public string Path { get; } = path;

            public string DisplayName => System.IO.Path.GetFileName(
                Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))
                is { Length: > 0 } name ? name : Path;

            public string StatusLabel => Directory.Exists(Path) ? string.Empty : "not found";

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value)
                    {
                        return;
                    }

                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
        }

        private sealed class CredentialField(string key, string value)
        {
            public string Key { get; } = key;
            public string Label { get; } = key;
            public string Value { get; set; } = value;
        }
    }
}
