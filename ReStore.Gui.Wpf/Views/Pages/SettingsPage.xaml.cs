using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ReStore.Gui.Wpf.Services;
using ReStore.src.utils;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ReStore.Gui.Wpf.Views.Pages
{
    public partial class SettingsPage : Page
    {
        private readonly ThemeSettings _themeSettings;
        private readonly AppSettings _appSettings;
        private readonly ConfigManager _configManager;
        private bool _isLoading = true;
        private readonly ObservableCollection<string> _watchDirectories = new();
        private readonly ObservableCollection<string> _excludedPatterns = new();
        private readonly ObservableCollection<string> _excludedPaths = new();

        public SettingsPage()
        {
            InitializeComponent();
            _themeSettings = ThemeSettings.Load();
            _appSettings = AppSettings.Load();
            _configManager = new ConfigManager(new Logger());

            WatchDirectoriesList.ItemsSource = _watchDirectories;
            ExcludedPatternsList.ItemsSource = _excludedPatterns;
            ExcludedPathsList.ItemsSource = _excludedPaths;

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
                await ReloadStorageSourcesAsync();

                // Populate provider fields from config (if present)
                PopulateProviderFields();

                // Load backup configuration
                LoadBackupConfiguration();

                // Load watch directories
                LoadWatchDirectories();

                // Load exclusions
                LoadExclusions();

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
                    var path = _configManager.GetConfigFilePath();
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select, \"{path}\"",
                        UseShellExecute = true
                    });
                }
                catch
                {
                    MessageBox.Show("Could not open config file location.");
                }
            };

            LoadExampleBtn.Click += async (_, __) =>
            {
                try
                {
                    var configPath = _configManager.GetConfigFilePath();
                    var configDir = System.IO.Path.GetDirectoryName(configPath)!;
                    var examplePath = System.IO.Path.Combine(configDir, "config.example.json");
                    if (!System.IO.File.Exists(examplePath))
                    {
                        MessageBox.Show($"No example config found at: {examplePath}");
                        return;
                    }

                    // Backup existing config if present
                    if (System.IO.File.Exists(configPath))
                    {
                        var backupPath = System.IO.Path.Combine(configDir, $"config.backup.{DateTime.Now:yyyyMMddHHmmss}.json");
                        System.IO.File.Copy(configPath, backupPath, overwrite: false);
                    }

                    System.IO.File.Copy(examplePath, configPath, overwrite: true);
                    await ReloadStorageSourcesAsync();
                    PopulateProviderFields();
                    MessageBox.Show("Example config loaded.");
                }
                catch (System.Exception ex)
                {
                    MessageBox.Show($"Failed to load example config: {ex.Message}");
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

            // Test handlers
            TestLocalBtn.Click += async (_, __) => await TestProviderAsync("local");
            TestS3Btn.Click += async (_, __) => await TestProviderAsync("s3");
            TestGDriveBtn.Click += async (_, __) => await TestProviderAsync("gdrive");
            TestGitHubBtn.Click += async (_, __) => await TestProviderAsync("github");

            // New handlers for watch directories
            AddWatchDirBtn.Click += async (_, __) => await AddWatchDirectoryAsync();

            // New handlers for backup configuration
            SaveBackupConfigBtn.Click += async (_, __) => await SaveBackupConfigurationAsync();

            // New handlers for exclusions
            AddPatternBtn.Click += (_, __) => AddExcludedPattern();
            AddPathBtn.Click += (_, __) => AddExcludedPath();
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
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error loading backup configuration: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LoadWatchDirectories()
        {
            _watchDirectories.Clear();
            foreach (var dir in _configManager.WatchDirectories)
            {
                _watchDirectories.Add(dir);
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
                        if (!_watchDirectories.Contains(folderPath))
                        {
                            _watchDirectories.Add(folderPath);
                            _configManager.WatchDirectories.Add(folderPath);
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
                    _watchDirectories.Remove(path);
                    _configManager.WatchDirectories.Remove(path);
                    await _configManager.SaveAsync();
                    MessageBox.Show("Watch directory removed successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
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

                var configPath = _configManager.GetConfigFilePath();
                var jsonString = await System.IO.File.ReadAllTextAsync(configPath);
                
                using var doc = System.Text.Json.JsonDocument.Parse(jsonString);
                using var stream = new System.IO.MemoryStream();
                using var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true });

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
                    else
                    {
                        property.WriteTo(writer);
                    }
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

        private async System.Threading.Tasks.Task ReloadStorageSourcesAsync()
        {
            try
            {
                await _configManager.LoadAsync();
                var configured = _configManager.StorageSources.Keys.ToList();
                var known = new List<string> { "local", "s3", "gdrive", "github" };
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
    }
}
