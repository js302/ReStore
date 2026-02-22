using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ReStore.Services;
using ReStore.Core.src.utils;

namespace ReStore.Views.Pages
{
    public partial class SettingsPage : Page
    {
        private readonly ThemeSettings _themeSettings;
        private readonly AppSettings _appSettings;
        private readonly ConfigManager _configManager;
        private bool _isLoading = true;
        private readonly ObservableCollection<WatchDirectoryConfig> _watchDirectories = new();
        private readonly ObservableCollection<string> _excludedPatterns = new();
        private readonly ObservableCollection<string> _excludedPaths = new();
        private readonly ObservableCollection<string> _excludeSystemPrograms = new();

        public SettingsPage()
        {
            InitializeComponent();
            _themeSettings = ThemeSettings.Load();
            _appSettings = AppSettings.Load();
            _configManager = new ConfigManager(new Logger());

            WatchDirectoriesList.ItemsSource = _watchDirectories;
            ExcludedPatternsList.ItemsSource = _excludedPatterns;
            ExcludedPathsList.ItemsSource = _excludedPaths;
            ExcludeSystemProgramsList.ItemsSource = _excludeSystemPrograms;

            Loaded += async (_, __) =>
            {
                _isLoading = true;

                // Theme selector
                ThemeSelector.SelectedIndex = _themeSettings.Preference switch
                {
                    ThemePreference.Light => 1,
                    ThemePreference.Dark => 2,
                    _ => 0
                };

                // Storage sources
                ShowConfiguredOnly.IsChecked = _appSettings.ShowOnlyConfiguredProviders;
                MinimizeToTrayCheckBox.IsChecked = _appSettings.MinimizeToTray;
                RunAtStartupCheckBox.IsChecked = IsRunAtStartupEnabled();
                await ReloadStorageSourcesAsync();

                PopulateProviderFields();

                LoadBackupConfiguration();

                LoadWatchDirectories();
                
                LoadGlobalStorage();

                LoadExclusions();

                LoadSystemBackupConfiguration();

                LoadEncryptionConfiguration();

                LoadContextMenuConfiguration();

                _isLoading = false;
            };

            ThemeSelector.SelectionChanged += (_, __) =>
            {
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

            StorageCombo.SelectionChanged += (_, __) =>
            {
                if (StorageCombo.SelectedItem is string s)
                {
                    // Persist base key without annotation
                    var baseKey = s.Split(' ')[0];
                    _appSettings.DefaultStorage = baseKey;
                    _appSettings.Save();
                }
            };

            ShowConfiguredOnly.Checked += async (_, __) =>
            {
                _appSettings.ShowOnlyConfiguredProviders = true;
                _appSettings.Save();
                await ReloadStorageSourcesAsync();
            };
            ShowConfiguredOnly.Unchecked += async (_, __) =>
            {
                _appSettings.ShowOnlyConfiguredProviders = false;
                _appSettings.Save();
                await ReloadStorageSourcesAsync();
            };

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



            // Save handlers
            SaveLocalBtn.Click += async (_, __) => await SaveProviderAsync("local", new()
            {
                ["path"] = LocalPathBox.Text
            });

            SaveS3Btn.Click += async (_, __) => await SaveProviderAsync("s3", new()
            {
                ["accessKeyId"] = S3AccessKey.Text,
                ["secretAccessKey"] = S3SecretKey.Password,
                ["region"] = S3Region.Text,
                ["bucketName"] = S3Bucket.Text
            });

            SaveGDriveBtn.Click += async (_, __) => await SaveProviderAsync("gdrive", new()
            {
                ["client_id"] = GDClientId.Text,
                ["client_secret"] = GDClientSecret.Password,
                ["token_folder"] = GDTokenFolder.Text,
                ["backup_folder_name"] = GDBackupFolder.Text
            });

            SaveGitHubBtn.Click += async (_, __) => await SaveProviderAsync("github", new()
            {
                ["token"] = GhToken.Password,
                ["owner"] = GhOwner.Text,
                ["repo"] = GhRepo.Text
            });

            SaveAzureBtn.Click += async (_, __) => await SaveProviderAsync("azure", new()
            {
                ["connectionString"] = AzureConnectionString.Password,
                ["containerName"] = AzureContainerName.Text
            });

            SaveGcpBtn.Click += async (_, __) => await SaveProviderAsync("gcp", new()
            {
                ["bucketName"] = GcpBucketName.Text,
                ["credentialPath"] = GcpCredentialPath.Text
            });

            SaveDropboxBtn.Click += async (_, __) => await SaveProviderAsync("dropbox", new()
            {
                ["accessToken"] = DropboxAccessToken.Password,
                ["refreshToken"] = DropboxRefreshToken.Password,
                ["appKey"] = DropboxAppKey.Text,
                ["appSecret"] = DropboxAppSecret.Password
            });

            SaveSftpBtn.Click += async (_, __) => await SaveProviderAsync("sftp", new()
            {
                ["host"] = SftpHost.Text,
                ["port"] = SftpPort.Text,
                ["username"] = SftpUsername.Text,
                ["password"] = SftpPassword.Password,
                ["privateKeyPath"] = SftpKeyPath.Text,
                ["passphrase"] = SftpPassphrase.Password
            });

            SaveB2Btn.Click += async (_, __) => await SaveProviderAsync("b2", new()
            {
                ["keyId"] = B2KeyId.Text,
                ["applicationKey"] = B2AppKey.Password,
                ["serviceUrl"] = B2ServiceUrl.Text,
                ["bucketName"] = B2BucketName.Text
            });

            // Test handlers
            TestLocalBtn.Click += async (_, __) => await TestProviderAsync("local");
            TestS3Btn.Click += async (_, __) => await TestProviderAsync("s3");
            TestGDriveBtn.Click += async (_, __) => await TestProviderAsync("gdrive");
            TestGitHubBtn.Click += async (_, __) => await TestProviderAsync("github");
            TestAzureBtn.Click += async (_, __) => await TestProviderAsync("azure");
            TestGcpBtn.Click += async (_, __) => await TestProviderAsync("gcp");
            TestDropboxBtn.Click += async (_, __) => await TestProviderAsync("dropbox");
            TestSftpBtn.Click += async (_, __) => await TestProviderAsync("sftp");
            TestB2Btn.Click += async (_, __) => await TestProviderAsync("b2");

            // New handlers for watch directories
            AddWatchDirBtn.Click += async (_, __) => await AddWatchDirectoryAsync();

            // New handlers for backup configuration
            SaveBackupConfigBtn.Click += async (_, __) => await SaveBackupConfigurationAsync();

            // New handlers for exclusions
            AddPatternBtn.Click += (_, __) => AddExcludedPattern();
            AddPathBtn.Click += (_, __) => AddExcludedPath();

            // System Backup handlers
            AddSystemProgramBtn.Click += (_, __) => AddExcludeSystemProgram();
            SaveSystemBackupConfigBtn.Click += async (_, __) => await SaveSystemBackupConfigurationAsync();
        }

        private void LoadBackupConfiguration()
        {
            try
            {
                BackupTypeCombo.SelectedIndex = _configManager.BackupType switch
                {
                    BackupType.Full => 0,
                    BackupType.Incremental => 1,
                    BackupType.Differential => 2,
                    _ => 1
                };

                BackupIntervalHoursBox.Text = _configManager.BackupInterval.TotalHours.ToString("F1");
                SizeThresholdBox.Text = _configManager.SizeThresholdMB.ToString();
                MaxFileSizeBox.Text = _configManager.MaxFileSizeMB.ToString();

                RetentionEnabledCheckBox.IsChecked = _configManager.Retention.Enabled;
                RetentionKeepLastBox.Text = _configManager.Retention.KeepLastPerDirectory.ToString();
                RetentionMaxAgeDaysBox.Text = _configManager.Retention.MaxAgeDays.ToString();
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
            if (_isLoading || GlobalStorageCombo.SelectedItem is not string storageType)
                return;

            try
            {
                var prop = typeof(ConfigManager).GetProperty("GlobalStorageType");
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(_configManager, storageType);
                    await _configManager.SaveAsync();
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error updating global storage: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
                    await _configManager.SaveAsync();
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error updating path storage: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ApplyStorageToAll_Click(object sender, RoutedEventArgs e)
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
                    await _configManager.SaveAsync();
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
            foreach (var pattern in _configManager.ExcludedPatterns)
            {
                _excludedPatterns.Add(pattern);
            }

            _excludedPaths.Clear();
            foreach (var path in _configManager.ExcludedPaths)
            {
                _excludedPaths.Add(path);
            }
        }

        private async Task AddWatchDirectoryAsync()
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    ValidateNames = false,
                    CheckFileExists = false,
                    CheckPathExists = true,
                    FileName = "Select Folder",
                    Title = "Select folder to watch"
                };

                if (dialog.ShowDialog() == true)
                {
                    var folderPath = System.IO.Path.GetDirectoryName(dialog.FileName);
                    if (!string.IsNullOrEmpty(folderPath))
                    {
                        if (!_watchDirectories.Any(w => w.Path == folderPath))
                        {
                            var newConfig = new WatchDirectoryConfig 
                            { 
                                Path = folderPath,
                                StorageType = null
                            };
                            _watchDirectories.Add(newConfig);
                            _configManager.WatchDirectories.Add(newConfig);
                            await _configManager.SaveAsync();
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

        private void RemoveWatchDir_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string path)
            {
                _ = RemoveWatchDirectoryAsync(path);
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
                        await _configManager.SaveAsync();
                        MessageBox.Show("Watch directory removed successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error removing watch directory: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task SaveBackupConfigurationAsync()
        {
            try
            {
                BackupType backupType = BackupType.Incremental;
                if (BackupTypeCombo.SelectedItem is ComboBoxItem selectedType)
                {
                    backupType = selectedType.Tag?.ToString() switch
                    {
                        "Full" => BackupType.Full,
                        "Differential" => BackupType.Differential,
                        _ => BackupType.Incremental
                    };
                }

                if (!double.TryParse(BackupIntervalHoursBox.Text, out var hours) || hours <= 0)
                {
                    MessageBox.Show("Invalid backup interval. Please enter a valid number of hours.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!long.TryParse(SizeThresholdBox.Text, out var sizeThreshold) || sizeThreshold <= 0)
                {
                    MessageBox.Show("Invalid size threshold. Please enter a valid number.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!int.TryParse(MaxFileSizeBox.Text, out var maxFileSize) || maxFileSize <= 0)
                {
                    MessageBox.Show("Invalid max file size. Please enter a valid number.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var retentionEnabled = RetentionEnabledCheckBox.IsChecked == true;
                if (!int.TryParse(RetentionKeepLastBox.Text, out var keepLast) || keepLast < 0)
                {
                    MessageBox.Show("Invalid retention keep-last value. Use a number >= 0.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!int.TryParse(RetentionMaxAgeDaysBox.Text, out var maxAgeDays) || maxAgeDays < 0)
                {
                    MessageBox.Show("Invalid retention max-age value. Use a number >= 0.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (retentionEnabled && keepLast < 1)
                {
                    MessageBox.Show("Retention is enabled but Keep Last is < 1. At least one backup must be kept.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var configPath = _configManager.GetConfigFilePath();
                var jsonString = await System.IO.File.ReadAllTextAsync(configPath);
                
                using var doc = System.Text.Json.JsonDocument.Parse(jsonString);
                using var stream = new System.IO.MemoryStream();
                using var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true });

                var wroteRetention = false;

                writer.WriteStartObject();
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (property.Name == "backupType")
                    {
                        writer.WriteString("backupType", backupType.ToString());
                    }
                    else if (property.Name == "backupInterval")
                    {
                        writer.WriteString("backupInterval", TimeSpan.FromHours(hours).ToString());
                    }
                    else if (property.Name == "sizeThresholdMB")
                    {
                        writer.WriteNumber("sizeThresholdMB", sizeThreshold);
                    }
                    else if (property.Name == "maxFileSizeMB")
                    {
                        writer.WriteNumber("maxFileSizeMB", maxFileSize);
                    }
                    else if (property.Name == "retention")
                    {
                        writer.WritePropertyName("retention");
                        writer.WriteStartObject();
                        writer.WriteBoolean("enabled", retentionEnabled);
                        writer.WriteNumber("keepLastPerDirectory", keepLast);
                        writer.WriteNumber("maxAgeDays", maxAgeDays);
                        writer.WriteEndObject();
                        wroteRetention = true;
                    }
                    else
                    {
                        property.WriteTo(writer);
                    }
                }

                if (!wroteRetention)
                {
                    writer.WritePropertyName("retention");
                    writer.WriteStartObject();
                    writer.WriteBoolean("enabled", retentionEnabled);
                    writer.WriteNumber("keepLastPerDirectory", keepLast);
                    writer.WriteNumber("maxAgeDays", maxAgeDays);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                writer.Flush();

                var newJsonString = System.Text.Encoding.UTF8.GetString(stream.ToArray());
                await System.IO.File.WriteAllTextAsync(configPath, newJsonString);

                await _configManager.LoadAsync();
                LoadBackupConfiguration();

                MessageBox.Show("Backup configuration saved successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error saving backup configuration: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void AddExcludedPattern()
        {
            try
            {
                var pattern = NewPatternBox.Text?.Trim();
                if (!string.IsNullOrEmpty(pattern))
                {
                    if (!_excludedPatterns.Contains(pattern))
                    {
                        _excludedPatterns.Add(pattern);
                        _configManager.ExcludedPatterns.Add(pattern);
                        _ = _configManager.SaveAsync();
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

        private void RemovePattern_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string pattern)
            {
                _excludedPatterns.Remove(pattern);
                _configManager.ExcludedPatterns.Remove(pattern);
                _ = _configManager.SaveAsync();
            }
        }

        private void AddExcludedPath()
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    ValidateNames = false,
                    CheckFileExists = false,
                    CheckPathExists = true,
                    FileName = "Select Folder",
                    Title = "Select folder to exclude"
                };

                if (dialog.ShowDialog() == true)
                {
                    var folderPath = System.IO.Path.GetDirectoryName(dialog.FileName);
                    if (!string.IsNullOrEmpty(folderPath))
                    {
                        if (!_excludedPaths.Contains(folderPath))
                        {
                            _excludedPaths.Add(folderPath);
                            _configManager.ExcludedPaths.Add(folderPath);
                            _ = _configManager.SaveAsync();
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

        private void RemovePath_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string path)
            {
                _excludedPaths.Remove(path);
                _configManager.ExcludedPaths.Remove(path);
                _ = _configManager.SaveAsync();
            }
        }

        private void LoadSystemBackupConfiguration()
        {
            try
            {
                SystemBackupEnabledCheckBox.IsChecked = _configManager.SystemBackup.Enabled;
                SystemBackupIncludeProgramsCheckBox.IsChecked = _configManager.SystemBackup.IncludePrograms;
                SystemBackupIncludeEnvCheckBox.IsChecked = _configManager.SystemBackup.IncludeEnvironmentVariables;
                SystemBackupIntervalHoursBox.Text = _configManager.SystemBackup.BackupInterval.TotalHours.ToString("F1");

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

        private async Task SaveSystemBackupConfigurationAsync()
        {
            try
            {
                if (!double.TryParse(SystemBackupIntervalHoursBox.Text, out var hours) || hours <= 0)
                {
                    MessageBox.Show("Invalid system backup interval. Please enter a valid number of hours.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Update ConfigManager properties
                _configManager.SystemBackup.Enabled = SystemBackupEnabledCheckBox.IsChecked ?? false;
                _configManager.SystemBackup.IncludePrograms = SystemBackupIncludeProgramsCheckBox.IsChecked ?? true;
                _configManager.SystemBackup.IncludeEnvironmentVariables = SystemBackupIncludeEnvCheckBox.IsChecked ?? true;
                _configManager.SystemBackup.BackupInterval = TimeSpan.FromHours(hours);
                _configManager.SystemBackup.ExcludeSystemPrograms.Clear();
                _configManager.SystemBackup.ExcludeSystemPrograms.AddRange(_excludeSystemPrograms);

                // Save through ConfigManager
                await _configManager.SaveAsync();
                await _configManager.LoadAsync();
                LoadSystemBackupConfiguration();

                MessageBox.Show("System backup configuration saved successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error saving system backup configuration: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
                await _configManager.LoadAsync();
                var configured = _configManager.StorageSources.Keys.ToList();
                var known = new List<string> { "local", "s3", "gdrive", "github", "azure", "gcp", "dropbox", "sftp", "b2" };
                var sources = _appSettings.ShowOnlyConfiguredProviders
                    ? configured
                    : known.Union(configured).ToList();

                // Annotate unconfigured when showing all
                var annotated = sources.Select(k => configured.Contains(k) ? k : $"{k} (not configured)").ToList();
                StorageCombo.ItemsSource = annotated;

                string? desired = _appSettings.DefaultStorage;
                if (!string.IsNullOrEmpty(desired))
                {
                    var match = annotated.FirstOrDefault(i => i.StartsWith(desired));
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
            catch (System.Exception ex)
            {
                MessageBox.Show($"Failed to load configuration: {ex.Message}");
            }
        }

        private void PopulateProviderFields()
        {
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
                }
                if (_configManager.StorageSources.TryGetValue("b2", out var b2))
                {
                    b2.Options.TryGetValue("keyId", out var ki); B2KeyId.Text = ki ?? string.Empty;
                    b2.Options.TryGetValue("applicationKey", out var ak); B2AppKey.Password = ak ?? string.Empty;
                    b2.Options.TryGetValue("serviceUrl", out var su); B2ServiceUrl.Text = su ?? string.Empty;
                    b2.Options.TryGetValue("bucketName", out var bn); B2BucketName.Text = bn ?? string.Empty;
                }
            }
            catch { }
        }

        private async Task SaveProviderAsync(string key, Dictionary<string, string> options)
        {
            try
            {
                await _configManager.LoadAsync();
                if (_configManager.StorageSources.ContainsKey(key))
                {
                    _configManager.StorageSources[key].Options = options;
                    if (key == "local" && options.TryGetValue("path", out var localPath))
                    {
                        _configManager.StorageSources[key].Path = localPath;
                    }
                }
                else
                {
                    _configManager.StorageSources[key] = new StorageConfig
                    {
                        Options = options,
                        Path = key == "local" && options.TryGetValue("path", out var localPath)
                            ? localPath
                            : string.Empty
                    };
                }
                await _configManager.SaveAsync();
                MessageBox.Show("Saved.");
                await ReloadStorageSourcesAsync();
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Failed to save: {ex.Message}");
            }
        }

        private async Task TestProviderAsync(string key)
        {
            try
            {
                await _configManager.LoadAsync();
                var storage = await _configManager.CreateStorageAsync(key);
                try
                {
                    // Lightweight check: Exists on a non-existing path should not throw
                    var ok = await storage.ExistsAsync("restore_probe.txt");
                    MessageBox.Show($"{key} initialized.");
                }
                finally
                {
                    storage.Dispose();
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Test failed: {ex.Message}");
            }
        }

        private const string REGISTRY_KEY_PATH = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_NAME = "ReStore";

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

                    key.SetValue(APP_NAME, $"\"{exePath}\"");
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

        private void LoadEncryptionConfiguration()
        {
            var isEnabled = _configManager.Encryption.Enabled;
            
            EncryptionStatusText.Text = isEnabled ? "Enabled" : "Disabled";
            EncryptionStatusText.Foreground = isEnabled 
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                : (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush");
            
            ToggleEncryptionBtn.Content = isEnabled ? "Disable Encryption" : "Enable Encryption";
            EncryptionDetailsExpander.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void ToggleEncryption_Click(object sender, RoutedEventArgs e)
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
                        await _configManager.SaveAsync();
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
                        var logger = new Logger();
                        var encryptionService = new ReStore.Core.src.utils.EncryptionService(logger);
                        var verificationToken = encryptionService.CreatePasswordVerificationToken(
                            setupWindow.Password, 
                            setupWindow.Salt, 
                            _configManager.Encryption.KeyDerivationIterations);
                        
                        _configManager.Encryption.Enabled = true;
                        _configManager.Encryption.Salt = Convert.ToBase64String(setupWindow.Salt);
                        _configManager.Encryption.VerificationToken = verificationToken;
                        await _configManager.SaveAsync();
                        
                        if (App.GlobalPasswordProvider != null)
                        {
                            App.GlobalPasswordProvider.SetPassword(setupWindow.Password);
                        }
                        
                        LoadEncryptionConfiguration();
                        
                        MessageBox.Show(
                            "Encryption has been enabled successfully!\n\n" +
                            "All new backups will be encrypted with AES-256-GCM.\n\n" +
                            "IMPORTANT: Store your password securely - it cannot be recovered if lost!",
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
