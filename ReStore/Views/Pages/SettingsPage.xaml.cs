using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using ReStore.Services;
using ReStore.Core.src.backup;
using ReStore.Core.src.utils;
using ReStore.Views.Controls;

namespace ReStore.Views.Pages
{
    public partial class SettingsPage : Page
    {
        private readonly ThemeSettings _themeSettings;
        private readonly AppSettings _appSettings;
        private ConfigManager _configManager = null!;
        private bool _isLoading = true;
        private bool _syncingStorageSelection;
        private readonly ObservableCollection<WatchDirectoryConfig> _watchDirectories = new();
        private readonly ObservableCollection<string> _excludedPatterns = new();
        private readonly ObservableCollection<string> _excludedPaths = new();
        private readonly ObservableCollection<string> _excludeSystemPrograms = new();

        public SettingsPage()
        {
            InitializeComponent();
            _themeSettings = ThemeSettings.Load();
            _appSettings = AppSettings.Load();

            WatchDirectoriesList.ItemsSource = _watchDirectories;
            ExcludedPatternsList.ItemsSource = _excludedPatterns;
            ExcludedPathsList.ItemsSource = _excludedPaths;
            ExcludeSystemProgramsList.ItemsSource = _excludeSystemPrograms;

            AppServices.SettingsHaveUnsavedEdits = () => Dispatcher.CheckAccess() ? HasUnsavedChanges : Dispatcher.Invoke(() => HasUnsavedChanges);
            Loaded += (_, __) => AttachExternalChangeHandlers();
            Unloaded += (_, __) => DetachExternalChangeHandlers();

            Loaded += async (_, __) => await ErrorReporter.GuardAsync("Loading settings", async () => {
                _isLoading = true;

                _configManager = await AppServices.GetConfigManagerAsync();

                // The page instance is cached; keep what the user typed if they come back with edits pending.
                if (HasUnsavedChanges)
                {
                    _isLoading = false;
                    return;
                }

                await LoadAllSectionsAsync();

                _dirtySections.Clear();
                _isLoading = false;
            });

            WireDirtyTracking();
            WireUpdateSettings();

            ThemeSelector.SelectionChanged += (_, __) =>
            {
                // Populating the selector while the page loads must not rewrite theme.json or re-apply the theme.
                if (_isLoading) return;
                _themeSettings.Preference = ThemeSelector.SelectedIndex switch
                {
                    1 => ThemePreference.Light,
                    2 => ThemePreference.Dark,
                    _ => ThemePreference.System
                };
                _themeSettings.Save();
                _themeSettings.Apply();
            };

            RunAtStartupCheckBox.Checked += (_, __) =>
            {
                if (_isLoading) return;
                SetRunAtStartup(true);
            };

            RunAtStartupCheckBox.Unchecked += (_, __) =>
            {
                if (_isLoading) return;
                SetRunAtStartup(false);
            };

            AutoStartProtectionCheckBox.Click += async (_, __) => await ErrorReporter.GuardAsync("Changing automatic start", async () =>
            {
                if (_isLoading || _configManager == null) return;

                var enabled = AutoStartProtectionCheckBox.IsChecked == true;
                var previous = _configManager.Protection.AutoStart;
                _configManager.Protection.AutoStart = enabled;
                try
                {
                    await SaveConfigAndApplyAsync();
                }
                catch (System.Exception ex) when (ex is not ProtectionApplyException)
                {
                    // Not saved: put the setting and the box back.
                    _configManager.Protection.AutoStart = previous;
                    AutoStartProtectionCheckBox.IsChecked = previous;
                    throw;
                }
            });

            ShareTelemetryCheckBox.Click += async (_, __) => await ErrorReporter.GuardAsync("Changing usage statistics", async () =>
            {
                if (_isLoading || _configManager == null) return;

                var enabled = ShareTelemetryCheckBox.IsChecked == true;
                var previous = _configManager.Telemetry.Enabled;
                _configManager.Telemetry.Enabled = enabled;
                try
                {
                    await _configManager.SaveAsync();
                }
                catch (System.Exception)
                {
                    _configManager.Telemetry.Enabled = previous;
                    ShareTelemetryCheckBox.IsChecked = previous;
                    throw;
                }

                var telemetry = ReStore.Core.src.telemetry.Telemetry.Client;
                telemetry.InvalidateConfig();
                if (!enabled)
                {
                    telemetry.ClearQueue();
                    telemetry.CrashStore.DeleteAll();
                }
                else if (telemetry.StateStore.Load().NoticeShownUtc == null)
                {
                    // Turning it on here is an informed choice; collection starts with the next launch.
                    telemetry.MarkNoticeShown("gui-settings");
                }
            });

            MinimizeToTrayCheckBox.Checked += (_, __) =>
            {
                if (_isLoading) return;

                _appSettings.MinimizeToTray = true;
                _appSettings.Save();

                if (Application.Current.MainWindow is MainWindow mainWindow)
                {
                    mainWindow.UpdateTrayManager(true);
                }
            };

            UseToastsCheckBox.Checked += (_, __) =>
            {
                if (_isLoading) return;
                _appSettings.UseToastNotifications = true;
                _appSettings.Save();
            };

            UseToastsCheckBox.Unchecked += (_, __) =>
            {
                if (_isLoading) return;
                _appSettings.UseToastNotifications = false;
                _appSettings.Save();
            };

            MinimizeToTrayCheckBox.Unchecked += (_, __) =>
            {
                if (_isLoading) return;

                _appSettings.MinimizeToTray = false;
                _appSettings.Save();

                if (Application.Current.MainWindow is MainWindow mainWindow)
                {
                    mainWindow.UpdateTrayManager(false);
                }
            };

            ContextMenuCheckBox.Checked += (_, __) =>
            {
                if (_isLoading) return;
                SetContextMenuEnabled(true);
            };

            ContextMenuCheckBox.Unchecked += (_, __) =>
            {
                if (_isLoading) return;
                SetContextMenuEnabled(false);
            };

            StorageCombo.SelectionChanged += async (_, __) => await ErrorReporter.GuardAsync("Changing the default storage", async () => {
                if (_isLoading || _syncingStorageSelection)
                    return;

                if (StorageCombo.SelectedItem is string s)
                {
                    var baseKey = s.Split(' ')[0];
                    if (!_configManager.StorageSources.ContainsKey(baseKey))
                    {
                        MessageBox.Show(
                            $"Storage provider '{baseKey}' is not configured and cannot be used as the default backup storage.",
                            "Storage Not Configured",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        SelectStorageComboItem(_configManager.GlobalStorageType);
                        return;
                    }

                    await SetGlobalStorageTypeAsync(baseKey);
                }
            });

            ShowConfiguredOnly.Checked += async (_, __) => await ErrorReporter.GuardAsync("Updating the provider list", async () => {
                if (_isLoading) return;
                _appSettings.ShowOnlyConfiguredProviders = true;
                _appSettings.Save();
                await ReloadStorageSourcesAsync();
            });
            ShowConfiguredOnly.Unchecked += async (_, __) => await ErrorReporter.GuardAsync("Updating the provider list", async () => {
                if (_isLoading) return;
                _appSettings.ShowOnlyConfiguredProviders = false;
                _appSettings.Save();
                await ReloadStorageSourcesAsync();
            });

            OpenConfigBtn.Click += (_, __) =>
            {
                try
                {
                    var configDir = ReStore.Core.src.utils.ConfigInitializer.GetUserConfigDirectory();
                    var configPath = ReStore.Core.src.utils.ConfigInitializer.GetUserConfigPath();

                    if (System.IO.File.Exists(configPath))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select, \"{configPath}\"",
                            UseShellExecute = true
                        });
                    }
                    else
                    {
                        System.IO.Directory.CreateDirectory(configDir);
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"\"{configDir}\"",
                            UseShellExecute = true
                        });

                        MessageBox.Show(
                            $"Configuration directory opened.\n\n" +
                            $"No config.json found yet. The application will create an example configuration on next startup.",
                            "Configuration Directory",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                }
                catch (System.Exception ex)
                {
                    MessageBox.Show($"Could not open config file location: {ex.Message}");
                }
            };
            SaveLocalBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Save provider", () => SaveProviderAsync("local"));
            SaveS3Btn.Click += async (_, __) => await ErrorReporter.GuardAsync("Save provider", () => SaveProviderAsync("s3"));
            SaveGDriveBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Save provider", () => SaveProviderAsync("gdrive"));
            SaveGitHubBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Save provider", () => SaveProviderAsync("github"));
            SaveAzureBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Save provider", () => SaveProviderAsync("azure"));
            SaveGcpBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Save provider", () => SaveProviderAsync("gcp"));
            SaveDropboxBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Save provider", () => SaveProviderAsync("dropbox"));
            SaveSftpBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Save provider", () => SaveProviderAsync("sftp"));
            SaveB2Btn.Click += async (_, __) => await ErrorReporter.GuardAsync("Save provider", () => SaveProviderAsync("b2"));

            TestLocalBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Test provider", () => TestProviderAsync("local"));
            TestS3Btn.Click += async (_, __) => await ErrorReporter.GuardAsync("Test provider", () => TestProviderAsync("s3"));
            TestGDriveBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Test provider", () => TestProviderAsync("gdrive"));
            TestGitHubBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Test provider", () => TestProviderAsync("github"));
            TestAzureBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Test provider", () => TestProviderAsync("azure"));
            TestGcpBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Test provider", () => TestProviderAsync("gcp"));
            TestDropboxBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Test provider", () => TestProviderAsync("dropbox"));
            TestSftpBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Test provider", () => TestProviderAsync("sftp"));
            TestB2Btn.Click += async (_, __) => await ErrorReporter.GuardAsync("Test provider", () => TestProviderAsync("b2"));

            AddWatchDirBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Add watch directory", () => AddWatchDirectoryAsync());

            RetentionPreviewBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Preview retention", PreviewRetentionAsync);
            SaveBackupConfigBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Save backup configuration", () => SaveBackupConfigurationAsync());

            AddPatternBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Add excluded pattern", () => AddExcludedPatternAsync());
            AddPathBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Add excluded path", () => AddExcludedPathAsync());

            AddSystemProgramBtn.Click += (_, __) => AddExcludeSystemProgram();
            SaveSystemBackupConfigBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Save system backup configuration", () => SaveSystemBackupConfigurationAsync());
        }

        private void LoadBackupConfiguration()
        {
            try
            {
                BackupTypeCombo.SelectedIndex = _configManager.BackupType switch
                {
                    BackupType.Full => 0,
                    BackupType.Incremental => 1,
                    BackupType.ChunkSnapshot => 2,
                    _ => 1
                };

                BackupIntervalHoursBox.Text = NumberInput.FormatHours(_configManager.BackupInterval.TotalHours);
                SizeThresholdBox.Text = _configManager.SizeThresholdMB.ToString();
                MaxFileSizeBox.Text = _configManager.MaxFileSizeMB.ToString();

                RetentionEnabledCheckBox.IsChecked = _configManager.Retention.Enabled;
                RetentionKeepLastBox.Text = _configManager.Retention.KeepLastPerDirectory.ToString();
                RetentionMaxAgeDaysBox.Text = _configManager.Retention.MaxAgeDays.ToString();
                RetentionKeepHourlyBox.Text = _configManager.Retention.KeepHourly.ToString();
                RetentionKeepDailyBox.Text = _configManager.Retention.KeepDaily.ToString();
                RetentionKeepWeeklyBox.Text = _configManager.Retention.KeepWeekly.ToString();
                RetentionKeepMonthlyBox.Text = _configManager.Retention.KeepMonthly.ToString();
                RetentionKeepYearlyBox.Text = _configManager.Retention.KeepYearly.ToString();
                RetentionModeCombo.SelectedIndex = string.Equals(_configManager.Retention.Mode, "all", System.StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                VerificationModeCombo.SelectedIndex = _configManager.Verification.Mode?.Trim().ToLowerInvariant() switch { "quick" => 0, "sample" => 1, _ => 2 };
                VerificationSamplePercentBox.Text = _configManager.Verification.SamplePercent.ToString();
                RetentionPreviewText.Visibility = Visibility.Collapsed;
                LoadBackupWindow();
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error loading backup configuration: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LoadWatchDirectories()
        {
            _watchDirectories.Clear();
            foreach (var watchConfig in _configManager.WatchDirectories)
            {
                _watchDirectories.Add(watchConfig);
            }
        }

        private void LoadGlobalStorage()
        {
            GlobalStorageCombo.Items.Clear();
            foreach (var storage in _configManager.StorageSources.Keys)
            {
                GlobalStorageCombo.Items.Add(storage);
            }

            var currentGlobal = _configManager.GlobalStorageType;
            if (GlobalStorageCombo.Items.Contains(currentGlobal))
            {
                GlobalStorageCombo.SelectedItem = currentGlobal;
            }
            else if (GlobalStorageCombo.Items.Count > 0)
            {
                GlobalStorageCombo.SelectedIndex = 0;
            }
        }

        private async void GlobalStorageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Changing the storage provider", () => GlobalStorageCombo_SelectionChangedCoreAsync(sender, e));
        }

        private async Task GlobalStorageCombo_SelectionChangedCoreAsync(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading || _syncingStorageSelection || GlobalStorageCombo.SelectedItem is not string storageType)
                return;

            try
            {
                await SetGlobalStorageTypeAsync(storageType);
                SelectStorageComboItem(storageType);
            }
            catch (ArgumentException)
            {
                MessageBox.Show(
                    $"Storage provider '{storageType}' is not configured and cannot be used as the default backup storage.",
                    "Storage Not Configured",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                SyncGlobalStorageComboSelection(_configManager.GlobalStorageType);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error updating global storage: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Fills every section of the page from the loaded config and preference files (initial load and after an import).</summary>
        private async Task LoadAllSectionsAsync()
        {
            ThemeSelector.SelectedIndex = _themeSettings.Preference switch
            {
                ThemePreference.Light => 1,
                ThemePreference.Dark => 2,
                _ => 0
            };

            MigrateLegacyDefaultStorage();

            ShowConfiguredOnly.IsChecked = _appSettings.ShowOnlyConfiguredProviders;
            MinimizeToTrayCheckBox.IsChecked = _appSettings.MinimizeToTray;
            UseToastsCheckBox.IsChecked = _appSettings.UseToastNotifications && Interop.PackageInfo.IsPackaged;
            UseToastsCheckBox.IsEnabled = Interop.PackageInfo.IsPackaged;
            AutoStartProtectionCheckBox.IsChecked = _configManager.Protection.AutoStart;
            ShareTelemetryCheckBox.IsChecked = _configManager.Telemetry.Enabled;
            CrashReportsCombo.SelectedItem = CrashReportsCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == _configManager.Telemetry.CrashReports)
                ?? CrashReportsCombo.Items[0];
            LoadUpdateSettings();
            ConfigureRunAtStartupToggle();
            await ReloadStorageSourcesAsync();

            PopulateProviderFields();

            LoadBackupConfiguration();

            LoadWatchDirectories();

            LoadGlobalStorage();

            LoadExclusions();

            LoadSystemBackupConfiguration();

            LoadEncryptionConfiguration();

            LoadContextMenuConfiguration();

            LoadLoggingConfiguration();
        }

        private void TelemetryWhatIsCollectedBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ReStore.Core.src.telemetry.TelemetryClient.DocsUrl) { UseShellExecute = true });
            }
            catch (System.Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // No browser registered.
            }
        }

        private async void CrashReportsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Changing crash reports", async () =>
            {
                if (_isLoading || _configManager == null || CrashReportsCombo.SelectedItem is not ComboBoxItem { Tag: string mode })
                {
                    return;
                }

                var previous = _configManager.Telemetry.CrashReports;
                if (previous == mode)
                {
                    return;
                }

                _configManager.Telemetry.CrashReports = mode;
                try
                {
                    await _configManager.SaveAsync();
                }
                catch (System.Exception)
                {
                    _configManager.Telemetry.CrashReports = previous;
                    throw;
                }

                ReStore.Core.src.telemetry.Telemetry.Client.InvalidateConfig();
            });
        }

        private void TelemetryShowDataBtn_Click(object sender, RoutedEventArgs e)
        {
            new ReStore.Views.Windows.TelemetryPreviewWindow { Owner = Window.GetWindow(this) }.ShowDialog();
        }

        /// <summary>
        /// Saves the shared config, then restarts background protection if it is running so changed watch
        /// directories, intervals, storage or encryption take effect (a user stop is respected).
        /// </summary>
        private async Task SaveConfigAndApplyAsync()
        {
            await _configManager.SaveAsync();

            if (App.Protection != null)
            {
                try
                {
                    await App.Protection.ApplyConfigChangeAsync();
                }
                catch (System.Exception ex)
                {
                    // The config is on disk; only the protection restart failed, so callers must not roll back.
                    throw new ProtectionApplyException(ex);
                }
            }
        }

        /// <summary>The configuration was saved, but restarting background protection failed.</summary>
        private sealed class ProtectionApplyException(System.Exception inner)
            : System.Exception($"The settings were saved, but restarting protection failed: {inner.Message}", inner);

        private async Task SetGlobalStorageTypeAsync(string storageType)
        {
            _configManager.SetGlobalStorageType(storageType);
            await SaveConfigAndApplyAsync();

            SyncGlobalStorageComboSelection(storageType);
        }

        private void SyncGlobalStorageComboSelection(string storageType)
        {
            if (!GlobalStorageCombo.Items.Contains(storageType) || Equals(GlobalStorageCombo.SelectedItem, storageType))
            {
                return;
            }

            _syncingStorageSelection = true;
            try
            {
                GlobalStorageCombo.SelectedItem = storageType;
            }
            finally
            {
                _syncingStorageSelection = false;
            }
        }

        private void SelectStorageComboItem(string storageType)
        {
            var match = StorageCombo.Items
                .OfType<string>()
                .FirstOrDefault(item => item.Equals(storageType, StringComparison.OrdinalIgnoreCase)
                    || item.StartsWith($"{storageType} ", StringComparison.OrdinalIgnoreCase));

            if (match == null || Equals(StorageCombo.SelectedItem, match))
            {
                return;
            }

            _syncingStorageSelection = true;
            try
            {
                StorageCombo.SelectedItem = match;
            }
            finally
            {
                _syncingStorageSelection = false;
            }
        }

        private void PathStorageCombo_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ComboBox combo || combo.Tag is not WatchDirectoryConfig watchConfig)
                return;

            var storageType = watchConfig.StorageType;
            if (string.IsNullOrEmpty(storageType))
            {
                combo.SelectedIndex = 0; // "Use Global Default"
            }
            else
            {
                foreach (ComboBoxItem item in combo.Items)
                {
                    if (item.Tag?.ToString() == storageType)
                    {
                        combo.SelectedItem = item;
                        break;
                    }
                }
            }
        }

        private async void PathStorageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Changing the storage provider", () => PathStorageCombo_SelectionChangedCoreAsync(sender, e));
        }

        private async Task PathStorageCombo_SelectionChangedCoreAsync(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading || sender is not ComboBox combo || combo.Tag is not WatchDirectoryConfig watchConfig)
                return;

            try
            {
                if (combo.SelectedItem is ComboBoxItem item)
                {
                    var storageTag = item.Tag?.ToString();
                    var newValue = string.IsNullOrEmpty(storageTag) ? null : storageTag;

                    // Only save if the value actually changed
                    if (watchConfig.StorageType == newValue)
                    {
                        return;
                    }

                    watchConfig.StorageType = newValue;
                    await SaveConfigAndApplyAsync();
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error updating path storage: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void FolderSchedule_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ScheduleEditorControl editor && editor.Tag is WatchDirectoryConfig watchConfig)
            {
                editor.DefaultDescription = DescribeDefaultInterval();
                editor.Load(watchConfig.Schedule);
            }
        }

        private string DescribeDefaultInterval()
            => _configManager == null || _configManager.BackupInterval <= TimeSpan.Zero
                ? "Follows the global backup interval."
                : $"Follows the global backup interval ({ReStore.Core.src.monitoring.BackupSchedule.FromInterval(_configManager.BackupInterval).Description.ToLowerInvariant()}).";

        private async void FolderSchedule_Committed(object? sender, EventArgs e)
        {
            await ErrorReporter.GuardAsync("Changing the backup schedule", () => FolderSchedule_CommittedCoreAsync(sender));
        }

        private async Task FolderSchedule_CommittedCoreAsync(object? sender)
        {
            if (_isLoading || sender is not ScheduleEditorControl editor || editor.Tag is not WatchDirectoryConfig watchConfig)
            {
                return;
            }

            // Only a valid schedule is ever committed; the editor shows the reason for an invalid one and nothing is saved.
            if (!editor.TryGetExpression(out var expression, out var error))
            {
                MessageBox.Show(error, "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var previous = watchConfig.Schedule;
            if (string.Equals(previous ?? string.Empty, expression ?? string.Empty, StringComparison.Ordinal))
            {
                return;
            }

            watchConfig.Schedule = expression;
            try
            {
                await SaveConfigAndApplyAsync();
                editor.MarkStored(expression);
            }
            catch (System.Exception ex) when (ex is not ProtectionApplyException)
            {
                // Roll back so a later unrelated save does not silently persist an edit that was reported as failed.
                watchConfig.Schedule = previous;
                editor.Load(previous);
                MessageBox.Show($"Error saving the backup schedule: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private WindowDaysChoice SelectedWindowDays()
            => (BackupWindowDaysCombo.SelectedItem as ComboBoxItem)?.Tag is string tag && Enum.TryParse<WindowDaysChoice>(tag, out var choice) ? choice : WindowDaysChoice.EveryDay;

        private void RefreshBackupWindowPreview()
        {
            BackupWindowCustomDaysBox.Visibility = SelectedWindowDays() == WindowDaysChoice.Custom ? Visibility.Visible : Visibility.Collapsed;
            BackupWindowPreviewText.Text = BackupWindowEditorModel.Preview(
                BackupWindowEnabledCheckBox.IsChecked == true, BackupWindowStartBox.Text, BackupWindowEndBox.Text, SelectedWindowDays(), BackupWindowCustomDaysBox.Text);
        }

        private void LoadBackupWindow()
        {
            var shown = BackupWindowEditorModel.FromConfig(_configManager.BackupWindow);
            BackupWindowEnabledCheckBox.IsChecked = shown.Enabled;
            BackupWindowStartBox.Text = shown.Start;
            BackupWindowEndBox.Text = shown.End;
            BackupWindowDaysCombo.SelectedIndex = (int)shown.Days;
            BackupWindowCustomDaysBox.Text = shown.CustomDays;
            RefreshBackupWindowPreview();
        }

        private async void ConnectExisting_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Connecting to existing backups", async () =>
            {
                _configManager ??= await AppServices.GetConfigManagerAsync();
                var window = new ReStore.Views.Windows.ConnectExistingBackupsWindow { Owner = Window.GetWindow(this) };
                window.ShowDialog();
            });
        }

        private async void UpgradeFormat_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Upgrading backup format", async () =>
            {
                _configManager ??= await AppServices.GetConfigManagerAsync();
                if (_configManager.ChunkDiffing.ManifestVersion >= ReStore.Core.src.core.SnapshotManifest.VersionV3)
                {
                    MessageBox.Show("Backups are already set to write the newest format.", "Upgrade backup format", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var message =
                    "From the next backup on, existing backup sets are written in the newest format. This keeps file dates, attributes and empty folders, " +
                    "and makes snapshot lists smaller. Older snapshots stay readable.\n\n" +
                    (_configManager.Encryption.Enabled
                        ? "Encrypted backups also hide file names, sizes and dates from the storage provider and allow changing the password without uploading anything. " +
                          "Because their chunk ids change, the next backup uploads your data once more.\n\n"
                        : "No data is uploaded again.\n\n") +
                    "Versions of ReStore older than this one cannot read snapshots in the new format. Continue?";
                if (MessageBox.Show(message, "Upgrade backup format", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    return;
                }

                _configManager.ChunkDiffing.ManifestVersion = ReStore.Core.src.core.SnapshotManifest.VersionV3;
                await SaveConfigAndApplyAsync();
            });
        }

        private static RetentionConfig CloneRetention(RetentionConfig source)
        {
            var clone = new RetentionConfig();
            CopyRetention(source, clone);
            return clone;
        }

        private static void CopyRetention(RetentionConfig source, RetentionConfig target)
        {
            target.Enabled = source.Enabled;
            target.KeepLastPerDirectory = source.KeepLastPerDirectory;
            target.MaxAgeDays = source.MaxAgeDays;
            target.KeepHourly = source.KeepHourly;
            target.KeepDaily = source.KeepDaily;
            target.KeepWeekly = source.KeepWeekly;
            target.KeepMonthly = source.KeepMonthly;
            target.KeepYearly = source.KeepYearly;
            target.Mode = source.Mode;
        }

        /// <summary>Reads and validates the retention fields from the form (shows a message and returns false when invalid).</summary>
        private bool TryReadRetentionFields(out RetentionConfig retention)
        {
            retention = new RetentionConfig { Enabled = RetentionEnabledCheckBox.IsChecked == true };

            if (!NumberInput.TryParseInt(RetentionKeepLastBox.Text, out var keepLast) || keepLast < 0)
            {
                MessageBox.Show("Invalid retention keep-last value. Use a number >= 0.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (!NumberInput.TryParseInt(RetentionMaxAgeDaysBox.Text, out var maxAgeDays) || maxAgeDays < 0)
            {
                MessageBox.Show("Invalid retention max-age value. Use a number >= 0.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var tiers = new[]
            {
                ("hourly", RetentionKeepHourlyBox), ("daily", RetentionKeepDailyBox), ("weekly", RetentionKeepWeeklyBox),
                ("monthly", RetentionKeepMonthlyBox), ("yearly", RetentionKeepYearlyBox)
            };
            var values = new int[tiers.Length];
            for (var i = 0; i < tiers.Length; i++)
            {
                if (!NumberInput.TryParseInt(tiers[i].Item2.Text, out values[i]) || values[i] < 0)
                {
                    MessageBox.Show($"Invalid retention {tiers[i].Item1} value. Use a number >= 0 (0 = off).", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            if (retention.Enabled && keepLast < 1)
            {
                MessageBox.Show("Retention is enabled but Keep Last is < 1. At least one backup must be kept.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            retention.KeepLastPerDirectory = keepLast;
            retention.MaxAgeDays = maxAgeDays;
            retention.KeepHourly = values[0];
            retention.KeepDaily = values[1];
            retention.KeepWeekly = values[2];
            retention.KeepMonthly = values[3];
            retention.KeepYearly = values[4];
            retention.Mode = (RetentionModeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? RetentionModes.Any;
            return true;
        }

        /// <summary>Dry run: shows what the policy currently typed in the form would delete, without saving or deleting anything.</summary>
        private async Task PreviewRetentionAsync()
        {
            if (!TryReadRetentionFields(out var retention))
            {
                return;
            }

            _configManager ??= await AppServices.GetConfigManagerAsync();
            var state = await AppServices.GetSystemStateAsync();
            var plans = new RetentionManager(AppServices.Logger, _configManager, state).PreviewPlans(retention);

            var text = new System.Text.StringBuilder();
            var total = 0;
            foreach (var (group, plan) in plans)
            {
                text.AppendLine(group);
                foreach (var d in plan.Decisions)
                {
                    text.AppendLine($"  {(d.Keep ? "keep  " : "DELETE")} [{d.StorageType}] {d.Backup.Timestamp.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)}  ({string.Join("; ", d.Reasons)})");
                }

                total += plan.ToDelete.Count;
            }

            text.Insert(0, plans.Count == 0
                ? "No backups yet.\r\n"
                : $"{total} backup(s) would be deleted with these settings{(retention.Enabled ? "" : " (retention is currently switched off)")}. Nothing has been changed.\r\n\r\n");
            RetentionPreviewText.Text = text.ToString();
            RetentionPreviewText.Visibility = Visibility.Visible;
        }

        private async void RepairStorage_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Repairing storage", RepairStorageAsync);
        }

        /// <summary>Preview (dry run) of what repair would delete, then, on confirmation, the real run. Uses the default storage.</summary>
        private async Task RepairStorageAsync()
        {
            _configManager ??= await AppServices.GetConfigManagerAsync();
            var state = await AppServices.GetSystemStateAsync();
            var storageType = _configManager.GlobalStorageType;
            var owner = Window.GetWindow(this);

            ReStore.Core.src.core.RepairReport? preview = null;
            var previewWindow = new ReStore.Views.Windows.OperationProgressWindow($"Checking '{storageType}'", AppServices.Logger);
            previewWindow.RunModal(owner, async token =>
            {
                using var storage = await _configManager.CreateStorageAsync(storageType);
                preview = await new ReStore.Core.src.core.RepositoryRepair(previewWindow, _configManager, state, null, App.GlobalPasswordProvider ?? new ReStore.Services.GuiPasswordProvider())
                    .RunAsync(storage, storageType, new ReStore.Core.src.core.RepairOptions { DryRun = true }, previewWindow.StatusProgress, token);
            });

            if (!previewWindow.Succeeded || preview == null)
            {
                if (previewWindow.Failure != null)
                {
                    MessageBox.Show($"Repair cannot run: {previewWindow.Failure.Message}", "Repair storage", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                return;
            }

            var summary =
                $"Storage: {storageType}\n" +
                $"Snapshots found: {preview.ManifestsScanned}\n" +
                $"Unused chunks to delete: {preview.OrphanChunks} ({ByteFormatter.Format(preview.OrphanBytes)})\n" +
                $"Kept because they are less than 24 hours old: {preview.ChunksKeptInGrace}\n\n" +
                "Repair also rebuilds this PC's chunk bookkeeping and re-adds snapshots that exist in storage but are missing from the history. Continue?";
            if (MessageBox.Show(summary, "Repair storage", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            ReStore.Core.src.core.RepairReport? result = null;
            var repairWindow = new ReStore.Views.Windows.OperationProgressWindow($"Repairing '{storageType}'", AppServices.Logger);
            repairWindow.RunModal(owner, async token =>
            {
                using var storage = await _configManager.CreateStorageAsync(storageType);
                result = await new ReStore.Core.src.core.RepositoryRepair(repairWindow, _configManager, state, null, App.GlobalPasswordProvider ?? new ReStore.Services.GuiPasswordProvider())
                    .RunAsync(storage, storageType, new ReStore.Core.src.core.RepairOptions(), repairWindow.StatusProgress, token);
            });

            if (repairWindow.Succeeded && result != null)
            {
                MessageBox.Show(
                    $"Deleted {result.ChunksDeleted} unused chunk(s) ({ByteFormatter.Format(result.BytesFreed)}); {result.OrphanSnapshotsAdopted} snapshot(s) re-added to the history.",
                    "Repair storage",
                    MessageBoxButton.OK,
                    result.ChunksDeleteFailed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            else if (repairWindow.Failure != null)
            {
                MessageBox.Show($"Repair stopped: {repairWindow.Failure.Message}", "Repair storage", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void ApplyStorageToAll_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Apply storage to all", () => ApplyStorageToAll_ClickCoreAsync(sender, e));
        }

        private async Task ApplyStorageToAll_ClickCoreAsync(object sender, RoutedEventArgs e)
        {
            if (GlobalStorageCombo.SelectedItem is not string globalStorage)
            {
                MessageBox.Show("Please select a global storage type first.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var result = MessageBox.Show(
                    $"Set all watch directories to use '{globalStorage}' storage?\n\nThis will override individual path storage settings.",
                    "Confirm Apply to All",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    foreach (var watchConfig in _configManager.WatchDirectories)
                    {
                        watchConfig.StorageType = globalStorage;
                    }
                    await SaveConfigAndApplyAsync();
                    LoadWatchDirectories();
                    MessageBox.Show("Storage settings applied to all paths.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error applying storage to all: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadExclusions()
        {
            _excludedPatterns.Clear();
            foreach (var pattern in _configManager.ExcludedPatterns.Concat(_configManager.ExcludeGlobs))
            {
                _excludedPatterns.Add(pattern);
            }

            _excludedPaths.Clear();
            foreach (var path in _configManager.ExcludedPaths)
            {
                _excludedPaths.Add(path);
            }

            PresetDevCheck.IsChecked = HasPreset(ExcludePresetCatalog.Dev);
            PresetCachesCheck.IsChecked = HasPreset(ExcludePresetCatalog.Caches);
            PresetOfficeTempCheck.IsChecked = HasPreset(ExcludePresetCatalog.OfficeTemp);
            PresetSystemCheck.IsChecked = HasPreset(ExcludePresetCatalog.System);
            HydrateCloudFilesCheck.IsChecked = _configManager.BackupSettings.HydrateCloudFiles;
        }

        private bool HasPreset(string name)
            => _configManager.ExcludePresets.Contains(name, StringComparer.OrdinalIgnoreCase);

        private async void Preset_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Change exclusion preset", () => Preset_ClickCoreAsync(sender));
        }

        private async Task Preset_ClickCoreAsync(object sender)
        {
            if (sender is CheckBox { Tag: string name } box)
            {
                _configManager.ExcludePresets.RemoveAll(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase));
                if (box.IsChecked == true)
                {
                    _configManager.ExcludePresets.Add(name);
                }

                await SaveConfigAndApplyAsync();
            }
        }

        private async void HydrateCloudFiles_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Change cloud file handling", async () =>
            {
                _configManager.BackupSettings.HydrateCloudFiles = HydrateCloudFilesCheck.IsChecked == true;
                await SaveConfigAndApplyAsync();
            });
        }

        private async Task AddWatchDirectoryAsync()
        {
            try
            {
                var dialog = new OpenFolderDialog
                {
                    Title = "Select folder to watch",
                    Multiselect = false
                };

                if (dialog.ShowDialog() == true)
                {
                    var folderPath = dialog.FolderName;
                    if (!string.IsNullOrEmpty(folderPath))
                    {
                        var pickedFull = NormalizeForCompare(folderPath);
                        if (!_watchDirectories.Any(w => NormalizeForCompare(w.ExpandedPath) == pickedFull))
                        {
                            var newConfig = new WatchDirectoryConfig
                            {
                                Path = folderPath,
                                StorageType = null
                            };
                            _watchDirectories.Add(newConfig);
                            _configManager.WatchDirectories.Add(newConfig);
                            await SaveConfigAndApplyAsync();
                            MessageBox.Show("Watch directory added successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                        else
                        {
                            MessageBox.Show("This directory is already being watched.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error adding watch directory: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string NormalizeForCompare(string path)
        {
            try
            {
                return System.IO.Path.GetFullPath(path)
                    .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
                    .ToUpperInvariant();
            }
            catch
            {
                return path.ToUpperInvariant();
            }
        }

        private async void RemoveWatchDir_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Remove watch dir", () => RemoveWatchDir_ClickCoreAsync(sender, e));
        }

        private async Task RemoveWatchDir_ClickCoreAsync(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string path)
            {
                await RemoveWatchDirectoryAsync(path);
            }
        }

        private async Task RemoveWatchDirectoryAsync(string path)
        {
            try
            {
                var result = MessageBox.Show(
                    $"Remove this directory from watch list?\n\n{path}",
                    "Confirm Remove",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    var watchConfig = _watchDirectories.FirstOrDefault(w => w.Path == path);
                    if (watchConfig != null)
                    {
                        _watchDirectories.Remove(watchConfig);
                        _configManager.WatchDirectories.Remove(watchConfig);
                        await SaveConfigAndApplyAsync();
                        MessageBox.Show("Watch directory removed successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error removing watch directory: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task<bool> SaveBackupConfigurationAsync(bool announce = true)
        {
            try
            {
                BackupType backupType = BackupType.Incremental;
                if (BackupTypeCombo.SelectedItem is ComboBoxItem selectedType)
                {
                    backupType = selectedType.Tag?.ToString() switch
                    {
                        "Full" => BackupType.Full,
                        "ChunkSnapshot" => BackupType.ChunkSnapshot,
                        "Differential" => BackupType.ChunkSnapshot,
                        _ => BackupType.Incremental
                    };
                }

                if (!NumberInput.TryParseDouble(BackupIntervalHoursBox.Text, out var hours) || hours <= 0)
                {
                    MessageBox.Show("Invalid backup interval. Please enter a valid number of hours.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                if (!NumberInput.TryParseLong(SizeThresholdBox.Text, out var sizeThreshold) || sizeThreshold <= 0)
                {
                    MessageBox.Show("Invalid size threshold. Please enter a valid number.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                if (!NumberInput.TryParseInt(MaxFileSizeBox.Text, out var maxFileSize) || maxFileSize <= 0)
                {
                    MessageBox.Show("Invalid max file size. Please enter a valid number.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                if (!TryReadRetentionFields(out var retention))
                {
                    return false;
                }

                if (!NumberInput.TryParseInt(VerificationSamplePercentBox.Text, out var samplePercent) || samplePercent < 1 || samplePercent > 100)
                {
                    MessageBox.Show("Invalid verification sample percentage. Use a number from 1 to 100.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                var verificationMode = (VerificationModeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "full";

                if (!BackupWindowEditorModel.TryBuild(
                        BackupWindowEnabledCheckBox.IsChecked == true, BackupWindowStartBox.Text, BackupWindowEndBox.Text,
                        SelectedWindowDays(), BackupWindowCustomDaysBox.Text, _configManager.BackupWindow, out var backupWindow, out var windowError))
                {
                    MessageBox.Show(windowError, "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                var previousWindow = _configManager.BackupWindow;
                var previous = (_configManager.BackupType, _configManager.BackupInterval, _configManager.SizeThresholdMB,
                    _configManager.MaxFileSizeMB, Retention: CloneRetention(_configManager.Retention),
                    VerificationMode: _configManager.Verification.Mode, VerificationSamplePercent: _configManager.Verification.SamplePercent);

                _configManager.BackupType = backupType;
                _configManager.BackupInterval = TimeSpan.FromHours(hours);
                _configManager.SizeThresholdMB = sizeThreshold;
                _configManager.MaxFileSizeMB = maxFileSize;
                CopyRetention(retention, _configManager.Retention);
                _configManager.Verification.Mode = verificationMode;
                _configManager.Verification.SamplePercent = samplePercent;
                _configManager.BackupWindow = backupWindow;

                try
                {
                    await SaveConfigAndApplyAsync();
                }
                catch (System.Exception ex) when (ex is not ProtectionApplyException)
                {
                    // Roll back so a later unrelated save does not silently persist an edit that was reported as failed.
                    _configManager.BackupType = previous.BackupType;
                    _configManager.BackupInterval = previous.BackupInterval;
                    _configManager.SizeThresholdMB = previous.SizeThresholdMB;
                    _configManager.MaxFileSizeMB = previous.MaxFileSizeMB;
                    CopyRetention(previous.Retention, _configManager.Retention);
                    _configManager.Verification.Mode = previous.VerificationMode;
                    _configManager.Verification.SamplePercent = previous.VerificationSamplePercent;
                    _configManager.BackupWindow = previousWindow;
                    throw;
                }

                LoadBackupConfiguration();
                _dirtySections.Remove(SectionBackup);

                if (announce) MessageBox.Show("Backup configuration saved successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error saving backup configuration: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private async Task AddExcludedPatternAsync()
        {
            try
            {
                var pattern = NewPatternBox.Text?.Trim();
                if (!string.IsNullOrEmpty(pattern))
                {
                    if (!_excludedPatterns.Contains(pattern))
                    {
                        _excludedPatterns.Add(pattern);

                        // Folder-style entries (trailing /, a slash or **) use the directory-aware glob list; plain
                        // file-name wildcards keep the classic list.
                        var isGlob = pattern.Contains('/') || pattern.Contains('\\') || pattern.Contains("**");
                        (isGlob ? _configManager.ExcludeGlobs : _configManager.ExcludedPatterns).Add(pattern);
                        await SaveConfigAndApplyAsync();
                        NewPatternBox.Text = string.Empty;
                    }
                    else
                    {
                        MessageBox.Show("This pattern is already in the exclusion list.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error adding pattern: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void RemovePattern_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Remove pattern", () => RemovePattern_ClickCoreAsync(sender, e));
        }

        private async Task RemovePattern_ClickCoreAsync(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string pattern)
            {
                _excludedPatterns.Remove(pattern);
                _configManager.ExcludedPatterns.Remove(pattern);
                _configManager.ExcludeGlobs.Remove(pattern);
                await SaveConfigAndApplyAsync();
            }
        }

        private async Task AddExcludedPathAsync()
        {
            try
            {
                var dialog = new OpenFolderDialog
                {
                    Title = "Select folder to exclude",
                    Multiselect = false
                };

                if (dialog.ShowDialog() == true)
                {
                    var folderPath = dialog.FolderName;
                    if (!string.IsNullOrEmpty(folderPath))
                    {
                        if (!_excludedPaths.Contains(folderPath))
                        {
                            _excludedPaths.Add(folderPath);
                            _configManager.ExcludedPaths.Add(folderPath);
                            await SaveConfigAndApplyAsync();
                        }
                        else
                        {
                            MessageBox.Show("This path is already in the exclusion list.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error adding excluded path: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void RemovePath_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Remove path", () => RemovePath_ClickCoreAsync(sender, e));
        }

        private async Task RemovePath_ClickCoreAsync(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string path)
            {
                _excludedPaths.Remove(path);
                _configManager.ExcludedPaths.Remove(path);
                await SaveConfigAndApplyAsync();
            }
        }

        private void LoadSystemBackupConfiguration()
        {
            try
            {
                SystemBackupEnabledCheckBox.IsChecked = _configManager.SystemBackup.Enabled;
                SystemBackupIncludeProgramsCheckBox.IsChecked = _configManager.SystemBackup.IncludePrograms;
                SystemBackupIncludeEnvCheckBox.IsChecked = _configManager.SystemBackup.IncludeEnvironmentVariables;
                SystemBackupIncludeSettingsCheckBox.IsChecked = _configManager.SystemBackup.IncludeWindowsSettings;
                SystemBackupIncludeDevToolsCheckBox.IsChecked = _configManager.SystemBackup.IncludeDevTools;
                SystemBackupIncludeSshCheckBox.IsChecked = _configManager.SystemBackup.IncludeSshConfig;
                SystemBackupIntervalHoursBox.Text = NumberInput.FormatHours(_configManager.SystemBackup.BackupInterval.TotalHours);
                SystemScheduleEditor.Load(_configManager.SystemBackup.Schedule);

                _excludeSystemPrograms.Clear();
                foreach (var pattern in _configManager.SystemBackup.ExcludeSystemPrograms)
                {
                    _excludeSystemPrograms.Add(pattern);
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error loading system backup configuration: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async Task<bool> SaveSystemBackupConfigurationAsync(bool announce = true)
        {
            try
            {
                if (!NumberInput.TryParseDouble(SystemBackupIntervalHoursBox.Text, out var hours) || hours <= 0)
                {
                    MessageBox.Show("Invalid system backup interval. Please enter a valid number of hours.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                if (!SystemScheduleEditor.TryGetExpression(out var systemSchedule, out var scheduleError))
                {
                    MessageBox.Show(scheduleError, "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                var sys = _configManager.SystemBackup;
                var previousSchedule = sys.Schedule;
                var previous = (sys.Enabled, sys.IncludePrograms, sys.IncludeEnvironmentVariables,
                    sys.IncludeWindowsSettings, sys.BackupInterval, Excludes: sys.ExcludeSystemPrograms.ToList(),
                    sys.IncludeDevTools, sys.IncludeSshConfig);

                _configManager.SystemBackup.Enabled = SystemBackupEnabledCheckBox.IsChecked ?? false;
                _configManager.SystemBackup.IncludePrograms = SystemBackupIncludeProgramsCheckBox.IsChecked ?? true;
                _configManager.SystemBackup.IncludeEnvironmentVariables = SystemBackupIncludeEnvCheckBox.IsChecked ?? true;
                _configManager.SystemBackup.IncludeWindowsSettings = SystemBackupIncludeSettingsCheckBox.IsChecked ?? true;
                _configManager.SystemBackup.IncludeDevTools = SystemBackupIncludeDevToolsCheckBox.IsChecked ?? false;
                _configManager.SystemBackup.IncludeSshConfig = SystemBackupIncludeSshCheckBox.IsChecked ?? false;
                _configManager.SystemBackup.BackupInterval = TimeSpan.FromHours(hours);
                _configManager.SystemBackup.Schedule = systemSchedule;
                _configManager.SystemBackup.ExcludeSystemPrograms.Clear();
                _configManager.SystemBackup.ExcludeSystemPrograms.AddRange(_excludeSystemPrograms);

                try
                {
                    await SaveConfigAndApplyAsync();
                }
                catch (System.Exception ex) when (ex is not ProtectionApplyException)
                {
                    // Roll back so a later unrelated save does not silently persist an edit that was reported as failed.
                    (sys.Enabled, sys.IncludePrograms, sys.IncludeEnvironmentVariables,
                        sys.IncludeWindowsSettings, sys.BackupInterval, _, sys.IncludeDevTools, sys.IncludeSshConfig) = previous;
                    sys.ExcludeSystemPrograms.Clear();
                    sys.ExcludeSystemPrograms.AddRange(previous.Excludes);
                    sys.Schedule = previousSchedule;
                    throw;
                }

                LoadSystemBackupConfiguration();
                _dirtySections.Remove(SectionSystem);

                if (announce) MessageBox.Show("System backup configuration saved successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error saving system backup configuration: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void AddExcludeSystemProgram()
        {
            try
            {
                var pattern = NewSystemProgramPatternBox.Text?.Trim();
                if (!string.IsNullOrEmpty(pattern))
                {
                    if (!_excludeSystemPrograms.Contains(pattern))
                    {
                        _excludeSystemPrograms.Add(pattern);
                        NewSystemProgramPatternBox.Text = string.Empty;
                    }
                    else
                    {
                        MessageBox.Show("This pattern is already in the exclusion list.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error adding system program pattern: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RemoveSystemProgram_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string pattern)
            {
                _excludeSystemPrograms.Remove(pattern);
            }
        }

        private async System.Threading.Tasks.Task ReloadStorageSourcesAsync()
        {
            try
            {
                var configured = _configManager.StorageSources.Keys.ToList();
                var known = new List<string> { "local", "s3", "gdrive", "github", "azure", "gcp", "dropbox", "sftp", "b2" };
                var sources = _appSettings.ShowOnlyConfiguredProviders
                    ? configured
                    : known.Union(configured).ToList();

                // Annotate unconfigured when showing all
                var annotated = sources.Select(k => configured.Contains(k) ? k : $"{k} (not configured)").ToList();
                _syncingStorageSelection = true;
                try
                {
                    StorageCombo.ItemsSource = annotated;

                    string? desired = _configManager.GlobalStorageType;
                    if (!string.IsNullOrEmpty(desired))
                    {
                        var match = annotated.FirstOrDefault(i => i.Equals(desired, StringComparison.OrdinalIgnoreCase)
                            || i.StartsWith($"{desired} ", StringComparison.OrdinalIgnoreCase));
                        if (match != null)
                        {
                            StorageCombo.SelectedItem = match;
                        }
                    }
                    if (StorageCombo.SelectedItem == null && annotated.Count > 0)
                    {
                        StorageCombo.SelectedIndex = 0;
                    }
                }
                finally
                {
                    _syncingStorageSelection = false;
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Failed to load configuration: {ex.Message}");
            }
        }

        private void PopulateProviderFields()
        {
            StoreSecretsEncryptedCheck.IsEnabled = ReStore.Core.src.utils.DpapiSecretProtector.Instance.IsAvailable;
            try
            {
                if (_configManager.StorageSources.TryGetValue("local", out var local))
                {
                    if (local.Options.TryGetValue("path", out var p)) LocalPathBox.Text = p;
                }
                if (_configManager.StorageSources.TryGetValue("s3", out var s3))
                {
                    s3.Options.TryGetValue("accessKeyId", out var ak); S3AccessKey.Text = ak ?? string.Empty;
                    s3.Options.TryGetValue("secretAccessKey", out var sk); S3SecretKey.Password = sk ?? string.Empty;
                    s3.Options.TryGetValue("region", out var rg); S3Region.Text = rg ?? string.Empty;
                    s3.Options.TryGetValue("bucketName", out var bn); S3Bucket.Text = bn ?? string.Empty;
                }
                if (_configManager.StorageSources.TryGetValue("gdrive", out var gd))
                {
                    gd.Options.TryGetValue("client_id", out var cid); GDClientId.Text = cid ?? string.Empty;
                    gd.Options.TryGetValue("client_secret", out var cs); GDClientSecret.Password = cs ?? string.Empty;
                    gd.Options.TryGetValue("token_folder", out var tf); GDTokenFolder.Text = tf ?? string.Empty;
                    gd.Options.TryGetValue("backup_folder_name", out var bfn); GDBackupFolder.Text = bfn ?? string.Empty;
                }
                if (_configManager.StorageSources.TryGetValue("github", out var gh))
                {
                    gh.Options.TryGetValue("token", out var tk); GhToken.Password = tk ?? string.Empty;
                    gh.Options.TryGetValue("owner", out var ow); GhOwner.Text = ow ?? string.Empty;
                    gh.Options.TryGetValue("repo", out var rp); GhRepo.Text = rp ?? string.Empty;
                }
                if (_configManager.StorageSources.TryGetValue("azure", out var az))
                {
                    az.Options.TryGetValue("connectionString", out var cs); AzureConnectionString.Password = cs ?? string.Empty;
                    az.Options.TryGetValue("containerName", out var cn); AzureContainerName.Text = cn ?? string.Empty;
                }
                if (_configManager.StorageSources.TryGetValue("gcp", out var gcp))
                {
                    gcp.Options.TryGetValue("bucketName", out var bn); GcpBucketName.Text = bn ?? string.Empty;
                    gcp.Options.TryGetValue("credentialPath", out var cp); GcpCredentialPath.Text = cp ?? string.Empty;
                }
                if (_configManager.StorageSources.TryGetValue("dropbox", out var db))
                {
                    db.Options.TryGetValue("accessToken", out var at); DropboxAccessToken.Password = at ?? string.Empty;
                    db.Options.TryGetValue("refreshToken", out var rt); DropboxRefreshToken.Password = rt ?? string.Empty;
                    db.Options.TryGetValue("appKey", out var ak); DropboxAppKey.Text = ak ?? string.Empty;
                    db.Options.TryGetValue("appSecret", out var asc); DropboxAppSecret.Password = asc ?? string.Empty;
                }
                if (_configManager.StorageSources.TryGetValue("sftp", out var sftp))
                {
                    sftp.Options.TryGetValue("host", out var h); SftpHost.Text = h ?? string.Empty;
                    sftp.Options.TryGetValue("port", out var p); SftpPort.Text = p ?? "22";
                    sftp.Options.TryGetValue("username", out var u); SftpUsername.Text = u ?? string.Empty;
                    sftp.Options.TryGetValue("password", out var pw); SftpPassword.Password = pw ?? string.Empty;
                    sftp.Options.TryGetValue("privateKeyPath", out var pk); SftpKeyPath.Text = pk ?? string.Empty;
                    sftp.Options.TryGetValue("passphrase", out var pp); SftpPassphrase.Password = pp ?? string.Empty;
                    sftp.Options.TryGetValue("hostKeyFingerprint", out var hk); SftpHostKeyFingerprint.Text = hk ?? string.Empty;
                }
                if (_configManager.StorageSources.TryGetValue("b2", out var b2))
                {
                    b2.Options.TryGetValue("keyId", out var ki); B2KeyId.Text = ki ?? string.Empty;
                    b2.Options.TryGetValue("applicationKey", out var ak); B2AppKey.Password = ak ?? string.Empty;
                    b2.Options.TryGetValue("serviceUrl", out var su); B2ServiceUrl.Text = su ?? string.Empty;
                    b2.Options.TryGetValue("bucketName", out var bn); B2BucketName.Text = bn ?? string.Empty;
                }
            }
            catch (System.Exception ex) { AppServices.Logger.Log($"Failed to populate provider fields: {ex.Message}", ReStore.Core.src.utils.LogLevel.Warning); }
        }

        /// <summary>The option values currently typed into the provider's form (unsaved).</summary>
        private Dictionary<string, string> BuildProviderFormOptions(string key) => key switch
        {
            "local" => new() { ["path"] = LocalPathBox.Text },
            "s3" => new()
            {
                ["accessKeyId"] = S3AccessKey.Text,
                ["secretAccessKey"] = S3SecretKey.Password,
                ["region"] = S3Region.Text,
                ["bucketName"] = S3Bucket.Text
            },
            "gdrive" => new()
            {
                ["client_id"] = GDClientId.Text,
                ["client_secret"] = GDClientSecret.Password,
                ["token_folder"] = GDTokenFolder.Text,
                ["backup_folder_name"] = GDBackupFolder.Text
            },
            "github" => new()
            {
                ["token"] = GhToken.Password,
                ["owner"] = GhOwner.Text,
                ["repo"] = GhRepo.Text
            },
            "azure" => new()
            {
                ["connectionString"] = AzureConnectionString.Password,
                ["containerName"] = AzureContainerName.Text
            },
            "gcp" => new()
            {
                ["bucketName"] = GcpBucketName.Text,
                ["credentialPath"] = GcpCredentialPath.Text
            },
            "dropbox" => new()
            {
                ["accessToken"] = DropboxAccessToken.Password,
                ["refreshToken"] = DropboxRefreshToken.Password,
                ["appKey"] = DropboxAppKey.Text,
                ["appSecret"] = DropboxAppSecret.Password
            },
            "sftp" => new()
            {
                ["host"] = SftpHost.Text,
                ["port"] = SftpPort.Text,
                ["username"] = SftpUsername.Text,
                ["password"] = SftpPassword.Password,
                ["privateKeyPath"] = SftpKeyPath.Text,
                ["passphrase"] = SftpPassphrase.Password,
                ["hostKeyFingerprint"] = SftpHostKeyFingerprint.Text.Trim()
            },
            "b2" => new()
            {
                ["keyId"] = B2KeyId.Text,
                ["applicationKey"] = B2AppKey.Password,
                ["serviceUrl"] = B2ServiceUrl.Text,
                ["bucketName"] = B2BucketName.Text
            },
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown storage provider")
        };

        /// <summary>
        /// The provider's configuration with the form values merged over what is already stored, so options
        /// the form does not show (endpoint, prefix, hand-added keys) survive. Raw values; nothing is expanded.
        /// </summary>
        private StorageConfig BuildProviderConfigFromForm(string key)
        {
            var form = BuildProviderFormOptions(key);
            _configManager.StorageSources.TryGetValue(key, out var existing);

            var merged = new StorageConfig
            {
                Path = existing?.Path ?? string.Empty,
                Options = StorageConfig.MergeOptions(existing?.Options, form),
                ExtensionData = existing?.ExtensionData
            };
            if (key == "local" && form.TryGetValue("path", out var localPath))
            {
                merged.Path = localPath;
            }

            return merged;
        }

        private async Task<bool> SaveProviderAsync(string key, bool announce = true)
        {
            try
            {
                var hadPrevious = _configManager.StorageSources.TryGetValue(key, out var previous);
                var toSave = BuildProviderConfigFromForm(key);
                if (StoreSecretsEncryptedCheck.IsChecked == true && ReStore.Core.src.utils.DpapiSecretProtector.Instance.IsAvailable)
                {
                    // Literal secrets become dpapi: references; env:NAME / %VAR% / already-encrypted values stay as they are.
                    ReStore.Core.src.utils.SecretReferences.ProtectSecretOptions(toSave.Options);
                }

                _configManager.StorageSources[key] = toSave;
                try
                {
                    await SaveConfigAndApplyAsync();
                }
                catch (System.Exception ex) when (ex is not ProtectionApplyException)
                {
                    if (hadPrevious) _configManager.StorageSources[key] = previous!;
                    else _configManager.StorageSources.Remove(key);
                    throw;
                }

                _dirtySections.Remove(key);
                if (announce) MessageBox.Show("Saved.", "Storage provider", MessageBoxButton.OK, MessageBoxImage.Information);
                await ReloadStorageSourcesAsync();
                return true;
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Failed to save: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private bool _probeRunning;

        private async Task TestProviderAsync(string key)
        {
            if (_probeRunning) return;
            _probeRunning = true;
            try
            {
                // Test what is on screen, not what was last saved: a real write/read/list/delete round trip.
                using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(2));
                using var storage = await _configManager.CreateStorageAsync(key, BuildProviderConfigFromForm(key));
                var result = await ReStore.Core.src.storage.StorageProbe.RunAsync(storage, timeout.Token);
                MessageBox.Show(
                    result.Success ? $"{key}: all checks passed.\n\n{result.ToDisplayText()}" : $"{key}: the test failed.\n\n{result.ToDisplayText()}",
                    "Test connection", MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (System.OperationCanceledException)
            {
                MessageBox.Show("Test failed: the storage did not respond within 2 minutes.");
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Test failed: {ex.Message}");
            }
            finally
            {
                _probeRunning = false;
            }
        }

        // ---- config.json edited outside the app (the CLI) ----------------------------------------------------------------

        private void AttachExternalChangeHandlers()
        {
            if (AppServices.ExternalChanges is not { } changes)
            {
                return;
            }

            changes.PendingChanged -= OnExternalChangePending;
            changes.Reloaded -= OnExternalConfigReloaded;
            changes.PendingChanged += OnExternalChangePending;
            changes.Reloaded += OnExternalConfigReloaded;
            ShowExternalChangeBanner(changes.Pending);
        }

        private void DetachExternalChangeHandlers()
        {
            if (AppServices.ExternalChanges is { } changes)
            {
                changes.PendingChanged -= OnExternalChangePending;
                changes.Reloaded -= OnExternalConfigReloaded;
            }
        }

        private void OnExternalChangePending(bool pending) => Dispatcher.BeginInvoke(() => ShowExternalChangeBanner(pending));

        private void ShowExternalChangeBanner(bool visible)
        {
            ExternalChangeBannerText.Text = "config.json was changed outside ReStore (e.g. by the CLI) \u2014 Reload / Keep my edits";
            ExternalChangeBanner.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>The shared configuration was reloaded from disk: show it, unless the user has typed edits that must not be replaced.</summary>
        private void OnExternalConfigReloaded() => Dispatcher.BeginInvoke(async () => await ErrorReporter.GuardAsync("Reloading settings", async () =>
        {
            if (_configManager == null || HasUnsavedChanges)
            {
                return;
            }

            var wasLoading = _isLoading;
            _isLoading = true;
            try
            {
                await LoadAllSectionsAsync();
                _dirtySections.Clear();
                if (_configManager.Logging is { } logging && LoggingConfig.TryParseLevel(logging.Level, out var level))
                {
                    AppServices.Logger.MinimumLevel = level;
                }
            }
            finally
            {
                _isLoading = wasLoading;
            }
        }));

        private async void ExternalChangeReloadBtn_Click(object sender, RoutedEventArgs e) => await ErrorReporter.GuardAsync("Reloading settings", async () =>
        {
            if (AppServices.ExternalChanges is not { } changes)
            {
                return;
            }

            // The typed edits are what the user chose to drop; the reload event then refreshes every form.
            _dirtySections.Clear();
            await changes.ReloadNowAsync();
        });

        private async void ExternalChangeKeepBtn_Click(object sender, RoutedEventArgs e) => await ErrorReporter.GuardAsync("Keeping settings edits", async () =>
        {
            if (AppServices.ExternalChanges is { } changes)
            {
                await changes.KeepMyEditsAsync();
            }
        });

        private const string SectionBackup = "backup";
        private const string SectionSystem = "system";

        // Sections with typed-but-unsaved edits (an explicit Save button): "backup", "system" or a provider key.
        private readonly HashSet<string> _dirtySections = new();

        public bool HasUnsavedChanges => _dirtySections.Count > 0;

        private void Track(string section, params Control[] controls)
        {
            void Mark(object? _, EventArgs __)
            {
                if (!_isLoading) _dirtySections.Add(section);
            }

            foreach (var control in controls)
            {
                switch (control)
                {
                    case TextBox t: t.TextChanged += (s, e) => Mark(s, e); break;
                    case PasswordBox p: p.PasswordChanged += (s, e) => Mark(s, e); break;
                    case System.Windows.Controls.Primitives.ToggleButton b:
                        b.Checked += (s, e) => Mark(s, e);
                        b.Unchecked += (s, e) => Mark(s, e);
                        break;
                    case Selector sel: sel.SelectionChanged += (s, e) => Mark(s, e); break;
                }
            }
        }

        private void WireDirtyTracking()
        {
            Track(SectionBackup, BackupTypeCombo, BackupIntervalHoursBox, SizeThresholdBox, MaxFileSizeBox,
                RetentionEnabledCheckBox, RetentionKeepLastBox, RetentionMaxAgeDaysBox,
                RetentionKeepHourlyBox, RetentionKeepDailyBox, RetentionKeepWeeklyBox, RetentionKeepMonthlyBox, RetentionKeepYearlyBox,
                RetentionModeCombo, VerificationModeCombo, VerificationSamplePercentBox,
                BackupWindowEnabledCheckBox, BackupWindowStartBox, BackupWindowEndBox, BackupWindowDaysCombo, BackupWindowCustomDaysBox);

            BackupWindowEnabledCheckBox.Checked += (_, __) => RefreshBackupWindowPreview();
            BackupWindowEnabledCheckBox.Unchecked += (_, __) => RefreshBackupWindowPreview();
            BackupWindowStartBox.TextChanged += (_, __) => RefreshBackupWindowPreview();
            BackupWindowEndBox.TextChanged += (_, __) => RefreshBackupWindowPreview();
            BackupWindowCustomDaysBox.TextChanged += (_, __) => RefreshBackupWindowPreview();
            BackupWindowDaysCombo.SelectionChanged += (_, __) => RefreshBackupWindowPreview();
            SystemScheduleEditor.Edited += (_, __) =>
            {
                if (!_isLoading) _dirtySections.Add(SectionSystem);
            };

            Track(SectionSystem, SystemBackupEnabledCheckBox, SystemBackupIncludeProgramsCheckBox,
                SystemBackupIncludeEnvCheckBox, SystemBackupIncludeSettingsCheckBox, SystemBackupIntervalHoursBox);
            _excludeSystemPrograms.CollectionChanged += (_, __) =>
            {
                if (!_isLoading) _dirtySections.Add(SectionSystem);
            };

            Track("local", LocalPathBox);
            Track("s3", S3AccessKey, S3SecretKey, S3Region, S3Bucket);
            Track("gdrive", GDClientId, GDClientSecret, GDTokenFolder, GDBackupFolder);
            Track("github", GhToken, GhOwner, GhRepo);
            Track("azure", AzureConnectionString, AzureContainerName);
            Track("gcp", GcpBucketName, GcpCredentialPath);
            Track("dropbox", DropboxAccessToken, DropboxRefreshToken, DropboxAppKey, DropboxAppSecret);
            Track("sftp", SftpHost, SftpPort, SftpUsername, SftpPassword, SftpKeyPath, SftpPassphrase, SftpHostKeyFingerprint);
            Track("b2", B2KeyId, B2AppKey, B2ServiceUrl, B2BucketName);
        }

        /// <summary>
        /// Called by the main window before it navigates away. Returns true when navigation may proceed:
        /// nothing pending, the pending edits were saved, or the user chose to discard them.
        /// </summary>
        public async Task<bool> ConfirmLeaveAsync(bool closingApp = false)
        {
            if (!HasUnsavedChanges || _configManager == null)
            {
                return true;
            }

            var sections = string.Join(", ", _dirtySections.Select(DescribeSection).OrderBy(x => x));
            var question = closingApp
                ? "Save them before closing ReStore?\n\nYes = Save, No = Discard, Cancel = keep ReStore open."
                : "Save them before leaving Settings?\n\nYes = Save, No = Discard, Cancel = stay on this page.";
            var answer = MessageBox.Show(
                $"You have unsaved changes in: {sections}.\n\n{question}",
                "Unsaved changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel)
            {
                return false;
            }

            if (answer == MessageBoxResult.No)
            {
                DiscardUnsavedChanges();
                return true;
            }

            foreach (var section in _dirtySections.ToList())
            {
                var saved = section switch
                {
                    SectionBackup => await SaveBackupConfigurationAsync(announce: false),
                    SectionSystem => await SaveSystemBackupConfigurationAsync(announce: false),
                    _ => await SaveProviderAsync(section, announce: false)
                };

                if (!saved)
                {
                    return false;
                }
            }

            return true;
        }

        private static string DescribeSection(string section) => section switch
        {
            SectionBackup => "Backup configuration",
            SectionSystem => "System backup configuration",
            _ => $"{section} storage"
        };

        /// <summary>Reloads every tracked form from the shared configuration, dropping typed-but-unsaved values.</summary>
        private void DiscardUnsavedChanges()
        {
            var wasLoading = _isLoading;
            _isLoading = true;
            try
            {
                PopulateProviderFields();
                LoadBackupConfiguration();
                LoadSystemBackupConfiguration();
            }
            finally
            {
                _isLoading = wasLoading;
            }

            _dirtySections.Clear();
        }

        /// <summary>
        /// The default storage lives in config.json (<c>globalStorageType</c>); the old appsettings copy is
        /// dropped. Earlier versions wrote both together, and the config value is the one backups used.
        /// </summary>
        private void MigrateLegacyDefaultStorage()
        {
            if (_appSettings.ConsumeLegacyDefaultStorage())
            {
                _appSettings.Save();
            }
        }

        private const string REGISTRY_KEY_PATH = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_NAME = "ReStore";

        /// <summary>
        /// Unpackaged installs use the HKCU Run key. A packaged (MSIX) install virtualizes that key away, so it
        /// uses the manifest's startup task instead; its state can be overridden by the user or policy.
        /// </summary>
        private async void ConfigureRunAtStartupToggle()
        {
            if (!Interop.PackageInfo.IsPackaged)
            {
                RunAtStartupCheckBox.IsEnabled = true;
                RunAtStartupCheckBox.IsChecked = IsRunAtStartupEnabled();
                RunAtStartupNote.Text = StartupTaskUi.DefaultNote;
                return;
            }

            try
            {
                ApplyStartupTaskState(await Interop.StartupTaskService.GetStatusAsync());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Reading startup task state failed: {ex}");
                RunAtStartupCheckBox.IsEnabled = false;
                RunAtStartupCheckBox.IsChecked = false;
                RunAtStartupNote.Text = "The startup state could not be determined. Manage ReStore under Windows Settings > Apps > Startup.";
            }
        }

        private void ApplyStartupTaskState(StartupTaskStatus status)
        {
            var ui = StartupTaskUi.Describe(status);
            var wasLoading = _isLoading;
            _isLoading = true;
            try
            {
                RunAtStartupCheckBox.IsChecked = ui.IsChecked;
            }
            finally
            {
                _isLoading = wasLoading;
            }
            RunAtStartupCheckBox.IsEnabled = ui.CanToggle;
            RunAtStartupNote.Text = ui.Note;
        }

        private async void SetStartupTask(bool enabled)
        {
            try
            {
                ApplyStartupTaskState(await Interop.StartupTaskService.SetEnabledAsync(enabled));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to update startup setting: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                ConfigureRunAtStartupToggle();
            }
        }

        private bool IsRunAtStartupEnabled()
        {
            try
            {
                if (!OperatingSystem.IsWindows()) return false;

                using var key = Registry.CurrentUser.OpenSubKey(REGISTRY_KEY_PATH, false);
                return key?.GetValue(APP_NAME) != null;
            }
            catch
            {
                return false;
            }
        }

        private void SetRunAtStartup(bool enabled)
        {
            if (Interop.PackageInfo.IsPackaged)
            {
                SetStartupTask(enabled);
                return;
            }

            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    MessageBox.Show("Run at startup is only supported on Windows.", "Not Supported", MessageBoxButton.OK, MessageBoxImage.Information);
                    RunAtStartupCheckBox.IsChecked = false;
                    return;
                }

                using var key = Registry.CurrentUser.OpenSubKey(REGISTRY_KEY_PATH, true);
                if (key == null)
                {
                    MessageBox.Show("Unable to access registry for startup configuration.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (enabled)
                {
                    var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (string.IsNullOrEmpty(exePath))
                    {
                        MessageBox.Show("Unable to determine application path.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    key.SetValue(APP_NAME, $"\"{exePath}\" {StartupMode.StartupArgument}");
                    MessageBox.Show("ReStore will now start automatically when you log in to Windows.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    key.DeleteValue(APP_NAME, false);
                    MessageBox.Show("ReStore will no longer start automatically at Windows startup.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Failed to update startup setting: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                RunAtStartupCheckBox.IsChecked = IsRunAtStartupEnabled();
            }
        }

        private void LoadContextMenuConfiguration()
        {
            if (!FileContextMenuService.IsSupported)
            {
                // Packaged (MSIX): the registry verb would launch a path the package cannot provide, so the
                // option is switched off and explained rather than offered and broken.
                ContextMenuCheckBox.IsChecked = false;
                ContextMenuCheckBox.IsEnabled = false;
                ContextMenuStatusText.Text = "";
                ContextMenuPackagedNote.Visibility = Visibility.Visible;
                return;
            }

            var isRegistered = FileContextMenuService.IsContextMenuEnabled();

            _appSettings.ContextMenuEnabled = isRegistered;
            _appSettings.Save();

            ContextMenuCheckBox.IsChecked = isRegistered;
            ContextMenuStatusText.Text = isRegistered ? "✓ Registered" : "";
        }

        private void SetContextMenuEnabled(bool enabled)
        {
            if (enabled)
            {
                var (success, errorMessage) = FileContextMenuService.RegisterContextMenu();
                if (success)
                {
                    _appSettings.ContextMenuEnabled = true;
                    _appSettings.Save();
                    ContextMenuStatusText.Text = "✓ Registered";
                    MessageBox.Show(
                        "Context menu registered successfully!\n\nYou can now right-click any file in Windows Explorer and select 'Share with ReStore'.",
                        "Success",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    ContextMenuCheckBox.IsChecked = false;
                    MessageBox.Show(errorMessage ?? "Failed to register context menu.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                var (success, errorMessage) = FileContextMenuService.UnregisterContextMenu();
                if (success)
                {
                    _appSettings.ContextMenuEnabled = false;
                    _appSettings.Save();
                    ContextMenuStatusText.Text = "";
                    MessageBox.Show(
                        "Context menu unregistered successfully.\n\nThe 'Share with ReStore' option has been removed from the Windows Explorer context menu.",
                        "Success",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    ContextMenuCheckBox.IsChecked = true;
                    MessageBox.Show(errorMessage ?? "Failed to unregister context menu.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void LoadLoggingConfiguration()
        {
            var level = _configManager.Logging.Level;
            var index = -1;
            for (var i = 0; i < LogLevelCombo.Items.Count; i++)
            {
                if (LogLevelCombo.Items[i] is ComboBoxItem item && string.Equals(item.Content as string, level, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                }
            }

            LogLevelCombo.SelectedIndex = index >= 0 ? index : 1;
        }

        private async void LogLevelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Changing the log level", async () =>
            {
                if (_isLoading || _configManager == null || LogLevelCombo.SelectedItem is not ComboBoxItem { Content: string name }) return;
                if (!LoggingConfig.TryParseLevel(name, out var level)) return;

                // Persist first; only a saved level is applied live (and the combo reverts if saving fails).
                var previous = _configManager.Logging.Level;
                _configManager.Logging.Level = name;
                try
                {
                    await _configManager.SaveAsync();
                }
                catch
                {
                    _configManager.Logging.Level = previous;
                    _isLoading = true;
                    try { LoadLoggingConfiguration(); }
                    finally { _isLoading = false; }
                    throw;
                }

                AppServices.Logger.MinimumLevel = level;
            });
        }

        private async void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Opening the log folder", () =>
            {
                var directory = ReStore.Core.src.utils.AppPaths.LogsDirectory;
                System.IO.Directory.CreateDirectory(directory);
                var startInfo = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                startInfo.ArgumentList.Add(directory);
                System.Diagnostics.Process.Start(startInfo);
                return Task.CompletedTask;
            });
        }

        private async void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Copying diagnostics", async () =>
            {
                AppServices.Logger.Flush();
                var version = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
                var report = await DiagnosticsReport.BuildAsync(_configManager, version);
                Clipboard.SetText(report);
                MessageBox.Show("Diagnostics copied to the clipboard. Secrets have been removed.", "Copy diagnostics", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        private async void ExportSettings_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Exporting settings", async () =>
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Export settings",
                    Filter = "ReStore settings (*.json)|*.json",
                    FileName = "restore-settings.json"
                };
                if (dialog.ShowDialog() != true) return;

                await System.IO.File.WriteAllTextAsync(dialog.FileName, await ConfigPortability.ExportAsync(_configManager));
                MessageBox.Show("Settings exported. Passwords, keys and tokens were not included.", "Export settings", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        private async void ImportSettings_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Importing settings", async () =>
            {
                var dialog = new OpenFileDialog { Title = "Import settings", Filter = "ReStore settings (*.json)|*.json" };
                if (dialog.ShowDialog() != true) return;

                var choice = MessageBox.Show(
                    "Merge the imported settings into your current configuration?\n\n" +
                    "Yes = merge (keep what is not in the file)\nNo = replace the whole configuration\nCancel = do nothing\n\n" +
                    "Your current passwords and keys are kept for matching storage providers. A backup of the current config.json is saved first." +
                    (HasUnsavedChanges ? "\n\nUnsaved edits on this page will be discarded." : string.Empty),
                    "Import settings", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (choice == MessageBoxResult.Cancel) return;

                var result = await ConfigPortability.ImportAsync(
                    _configManager, dialog.FileName, choice == MessageBoxResult.Yes ? ConfigImportMode.Merge : ConfigImportMode.Replace);
                if (!result.Applied)
                {
                    MessageBox.Show("Nothing was imported; your configuration is unchanged.\n\n" + string.Join("\n", result.Errors),
                        "Import settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                ReloadPreferencesAfterImport();
                _isLoading = true;
                try
                {
                    await LoadAllSectionsAsync();
                    _dirtySections.Clear();
                }
                finally
                {
                    _isLoading = false;
                }

                if (_configManager.Logging is { } logging && LoggingConfig.TryParseLevel(logging.Level, out var level))
                {
                    AppServices.Logger.MinimumLevel = level;
                }

                if (App.Protection != null)
                {
                    try
                    {
                        await App.Protection.ApplyConfigChangeAsync();
                    }
                    catch (System.Exception ex)
                    {
                        throw new ProtectionApplyException(ex);
                    }
                }

                MessageBox.Show("Settings imported.\n\n" + string.Join("\n", result.Notes) + (result.Warnings.Count > 0 ? "\n\nWarnings:\n" + string.Join("\n", result.Warnings) : string.Empty),
                    "Import settings", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        /// <summary>Re-reads appsettings.json and theme.json after an import and applies them to the running app.</summary>
        private void ReloadPreferencesAfterImport()
        {
            var theme = ThemeSettings.Load();
            _themeSettings.Preference = theme.Preference;
            _themeSettings.Apply();

            var app = AppSettings.Load();
            _appSettings.ShowOnlyConfiguredProviders = app.ShowOnlyConfiguredProviders;
            _appSettings.MinimizeToTray = app.MinimizeToTray;
            _appSettings.UseToastNotifications = app.UseToastNotifications;
            UseToastsCheckBox.IsChecked = app.UseToastNotifications && Interop.PackageInfo.IsPackaged;
            if (Application.Current.MainWindow is MainWindow mainWindow)
            {
                mainWindow.UpdateTrayManager(_appSettings.MinimizeToTray);
            }
        }

        private void LoadEncryptionConfiguration()
        {
            var isEnabled = _configManager.Encryption.Enabled;

            EncryptionStatusText.Text = isEnabled ? "Enabled" : "Disabled";
            EncryptionStatusText.Foreground = isEnabled
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                : (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush");

            ToggleEncryptionBtn.Content = isEnabled ? "Disable Encryption" : "Enable Encryption";
            ChangePasswordBtn.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;
            EncryptionKdfText.Text = $"• Key Derivation: PBKDF2-SHA256 ({ReStore.Core.src.utils.EncryptionService.DefaultKeyDerivationIterations:N0} iterations)";
            EncryptionDetailsExpander.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void ChangePassword_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Change encryption password", ChangePasswordAsync);
        }

        /// <summary>Re-wraps the data key of an encrypted v3 repository at the default storage; no backup data is uploaded again.</summary>
        private async Task ChangePasswordAsync()
        {
            _configManager ??= await AppServices.GetConfigManagerAsync();
            var storageType = _configManager.GlobalStorageType;

            var dialog = new Windows.ChangePasswordWindow { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || dialog.CurrentPassword == null || dialog.NewPassword == null)
            {
                return;
            }

            ReStore.Core.src.core.PasswordChangeResult? result = null;
            string? problem = null;
            var progress = new ReStore.Views.Windows.OperationProgressWindow("Changing the encryption password", AppServices.Logger);
            progress.RunModal(Window.GetWindow(this), async token =>
            {
                using var storage = await _configManager.CreateStorageAsync(storageType);
                try
                {
                    result = await ReStore.Core.src.core.RepositoryPasswordService.ChangePasswordAsync(
                        storage, _configManager, AppServices.Logger, dialog.CurrentPassword, dialog.NewPassword, dialog.CreateRecoveryKey,
                        await AppServices.GetSystemStateAsync(), null, token);
                }
                catch (ReStore.Core.src.core.WrongRepositoryPasswordException)
                {
                    problem = "The current password is incorrect.";
                }
            });

            if (!progress.Succeeded)
            {
                if (progress.Failure != null)
                {
                    MessageBox.Show($"The password could not be changed: {progress.Failure.Message}", "Change password", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                return;
            }

            if (problem != null)
            {
                MessageBox.Show(problem, "Change password", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (result == null || !result.Changed)
            {
                MessageBox.Show(
                    "This storage does not hold an encrypted backup in the newest format, so its password cannot be changed without uploading everything again.\n\n" +
                    "Use \"Upgrade backup format...\" first (the next backup uploads the data once under the new scheme).",
                    "Change password", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            App.GlobalPasswordProvider?.SetPassword(dialog.NewPassword);
            var message = "The password was changed. Other computers that back up to this storage must be given the new password.";
            if (result.RecoveryKey != null)
            {
                message += $"\n\nRecovery key (shown only now - write it down and store it safely):\n\n{result.RecoveryKey}";
            }

            MessageBox.Show(message, "Change password", MessageBoxButton.OK, MessageBoxImage.Information);
            await OfferKeyFileCopyAsync(storageType);
        }

        /// <summary>Offers to save a copy of the repository key file (wrapped slots only, no plaintext secret) somewhere offline.</summary>
        private async Task OfferKeyFileCopyAsync(string storageType)
        {
            if (MessageBox.Show(
                    "Save a copy of the repository key file now?\n\nWithout this file (kept in your storage under keys/) encrypted backups cannot be read. " +
                    "The copy holds no readable secret, but keep it somewhere offline.",
                    "Key file copy", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "repository.keys.json", Filter = "Key file (*.json)|*.json" };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            using var storage = await _configManager.CreateStorageAsync(storageType);
            await System.IO.File.WriteAllBytesAsync(dialog.FileName, await ReStore.Core.src.core.RepositoryKeyStore.ExportAsync(storage));
        }

        private async void ToggleEncryption_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Toggle encryption", () => ToggleEncryption_ClickCoreAsync(sender, e));
        }

        private async Task ToggleEncryption_ClickCoreAsync(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_configManager.Encryption.Enabled)
                {
                    var result = MessageBox.Show(
                        "Are you sure you want to disable encryption?\n\n" +
                        "New backups will no longer be encrypted.\n" +
                        "Existing encrypted backups will still require the password to restore.",
                        "Disable Encryption",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        _configManager.Encryption.Enabled = false;
                        await SaveConfigAndApplyAsync();
                        LoadEncryptionConfiguration();

                        MessageBox.Show(
                            "Encryption has been disabled.\n\n" +
                            "New backups will not be encrypted.",
                            "Encryption Disabled",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                }
                else
                {
                    var setupWindow = new Windows.EncryptionSetupWindow();
                    if (setupWindow.ShowDialog() == true && setupWindow.Password != null && setupWindow.Salt != null)
                    {
                        ReStore.Core.src.utils.EncryptionConfigurator.Enable(
                            _configManager, AppServices.Logger, setupWindow.Password, setupWindow.Salt);
                        await SaveConfigAndApplyAsync();

                        if (App.GlobalPasswordProvider != null)
                        {
                            App.GlobalPasswordProvider.SetPassword(setupWindow.Password);
                        }

                        LoadEncryptionConfiguration();

                        MessageBox.Show(
                            "Encryption has been enabled successfully!\n\n" +
                            "All new backups will be encrypted with AES-256-GCM.\n\n" +
                            "IMPORTANT: Store your password securely - it cannot be recovered if lost!\n" +
                            "After the first backup, keep an offline copy of the key file (Change password... offers it, or use 'restore export-key').",
                            "Encryption Enabled",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Failed to toggle encryption: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
