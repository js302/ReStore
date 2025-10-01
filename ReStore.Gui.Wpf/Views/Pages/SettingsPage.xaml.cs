using System.Linq;
using System.Windows;
using System.Windows.Controls;
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

        public SettingsPage()
        {
            InitializeComponent();
            _themeSettings = ThemeSettings.Load();
            _appSettings = AppSettings.Load();
            _configManager = new ConfigManager(new Logger());

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
