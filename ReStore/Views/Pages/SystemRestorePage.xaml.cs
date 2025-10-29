using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ReStore.Core.src.core;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;
using ReStore.Core.src.backup;
using ReStore.Services;

namespace ReStore.Views.Pages
{
    public class SystemBackupItem
    {
        private static string? _storageBasePath;
        
        public string Type { get; set; } = "";
        public string Path { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public string TypeLabel => Type switch
        {
            "system_programs" => "Installed Programs",
            "system_environment" => "Environment Variables",
            "system_settings" => "Windows Settings",
            _ => "Unknown"
        };
        public string Icon => Type switch
        {
            "system_programs" => "💻",
            "system_environment" => "⚙️",
            "system_settings" => "🎨",
            _ => "📦"
        };
        public Brush IconColor => Type switch
        {
            "system_programs" => new SolidColorBrush(Color.FromRgb(16, 185, 129)),
            "system_environment" => new SolidColorBrush(Color.FromRgb(139, 92, 246)),
            "system_settings" => new SolidColorBrush(Color.FromRgb(245, 158, 11)),
            _ => new SolidColorBrush(Color.FromRgb(100, 100, 100))
        };
        
        public string DisplayPath
        {
            get
            {
                if (string.IsNullOrEmpty(_storageBasePath) || string.IsNullOrEmpty(Path))
                    return Path;
                
                if (Path.StartsWith("./") || Path.StartsWith(".\\"))
                {
                    var relativePath = Path.Substring(2);
                    return System.IO.Path.Combine(_storageBasePath, relativePath);
                }
                
                return System.IO.Path.Combine(_storageBasePath, Path);
            }
        }
        
        public static void SetStorageBasePath(string? basePath)
        {
            _storageBasePath = basePath;
        }
    }

    public partial class SystemRestorePage : Page
    {
        private readonly ConfigManager _configManager;
        private readonly Logger _logger = new();
        private SystemState? _state;
        private IStorage? _storage;
        private readonly ObservableCollection<SystemBackupItem> _systemBackups = new();
        private List<SystemBackupItem> _allBackups = new();

        public SystemRestorePage()
        {
            InitializeComponent();
            _configManager = new ConfigManager(_logger);

            SystemBackupsList.ItemsSource = _systemBackups;

            RefreshBtn.Click += async (_, __) => await LoadSystemBackupsAsync();
            BackupProgramsBtn.Click += async (_, __) => await BackupProgramsAsync();
            BackupEnvBtn.Click += async (_, __) => await BackupEnvironmentAsync();
            BackupSettingsBtn.Click += async (_, __) => await BackupWindowsSettingsAsync();
            BackupFullSystemBtn.Click += async (_, __) => await BackupFullSystemAsync();
            OpenBackupFolderBtn.Click += (_, __) => OpenBackupFolder();
            FilterTypeCombo.SelectionChanged += (_, __) => ApplyFilters();

            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            try
            {
                await _configManager.LoadAsync();
                _state = new SystemState(_logger);
                await _state.LoadStateAsync();

                var appSettings = AppSettings.Load();
                var remote = string.IsNullOrWhiteSpace(appSettings.DefaultStorage) ? "local" : appSettings.DefaultStorage;
                _storage = await _configManager.CreateStorageAsync(remote);

                if (_configManager.StorageSources.TryGetValue(remote, out var storageConfig))
                {
                    SystemBackupItem.SetStorageBasePath(storageConfig.Path);
                }

                await LoadSystemBackupsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            var storyboard = new System.Windows.Media.Animation.Storyboard();
            
            var fadeIn = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(400),
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(fadeIn, new PropertyPath(UIElement.OpacityProperty));
            storyboard.Children.Add(fadeIn);
            storyboard.Begin(this);
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            _storage?.Dispose();
        }

        private async Task LoadSystemBackupsAsync()
        {
            if (_state == null) return;

            await Task.Run(() =>
            {
                var items = new List<SystemBackupItem>();

                if (_state.BackupHistory.ContainsKey("system_programs"))
                {
                    foreach (var backup in _state.BackupHistory["system_programs"])
                    {
                        items.Add(new SystemBackupItem
                        {
                            Type = "system_programs",
                            Path = backup.Path,
                            Timestamp = backup.Timestamp
                        });
                    }
                }

                if (_state.BackupHistory.ContainsKey("system_environment"))
                {
                    foreach (var backup in _state.BackupHistory["system_environment"])
                    {
                        items.Add(new SystemBackupItem
                        {
                            Type = "system_environment",
                            Path = backup.Path,
                            Timestamp = backup.Timestamp
                        });
                    }
                }

                if (_state.BackupHistory.ContainsKey("system_settings"))
                {
                    foreach (var backup in _state.BackupHistory["system_settings"])
                    {
                        items.Add(new SystemBackupItem
                        {
                            Type = "system_settings",
                            Path = backup.Path,
                            Timestamp = backup.Timestamp
                        });
                    }
                }

                Dispatcher.Invoke(() =>
                {
                    _allBackups = items;
                    ApplyFilters();
                    UpdateStatistics();
                });
            });
        }

        private void ApplyFilters()
        {
            var filtered = _allBackups.AsEnumerable();

            if (FilterTypeCombo.SelectedItem is ComboBoxItem filterItem)
            {
                var filterTag = filterItem.Tag?.ToString();
                filtered = filterTag switch
                {
                    "programs" => filtered.Where(b => b.Type == "system_programs"),
                    "environment" => filtered.Where(b => b.Type == "system_environment"),
                    "settings" => filtered.Where(b => b.Type == "system_settings"),
                    _ => filtered
                };
            }

            filtered = filtered.OrderByDescending(b => b.Timestamp);

            _systemBackups.Clear();
            foreach (var item in filtered)
            {
                _systemBackups.Add(item);
            }

            UpdateStats();
            UpdateEmptyState();
        }

        private void UpdateStats()
        {
            var total = _systemBackups.Count;
            StatsText.Text = $"{total} system backup{(total != 1 ? "s" : "")}";
        }

        private void UpdateEmptyState()
        {
            EmptyState.Visibility = _systemBackups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateStatistics()
        {
            TotalBackupsText.Text = _allBackups.Count.ToString();

            var lastProgramBackup = _allBackups
                .Where(b => b.Type == "system_programs")
                .OrderByDescending(b => b.Timestamp)
                .FirstOrDefault();

            if (lastProgramBackup != null)
            {
                var timeSince = DateTime.UtcNow - lastProgramBackup.Timestamp;
                if (timeSince.TotalMinutes < 1)
                    LastProgramBackupText.Text = "Just now";
                else if (timeSince.TotalHours < 1)
                    LastProgramBackupText.Text = $"{(int)timeSince.TotalMinutes}m ago";
                else if (timeSince.TotalDays < 1)
                    LastProgramBackupText.Text = $"{(int)timeSince.TotalHours}h ago";
                else
                    LastProgramBackupText.Text = lastProgramBackup.Timestamp.ToLocalTime().ToString("MMM dd");
            }
            else
            {
                LastProgramBackupText.Text = "Never";
            }

            var lastEnvBackup = _allBackups
                .Where(b => b.Type == "system_environment")
                .OrderByDescending(b => b.Timestamp)
                .FirstOrDefault();

            if (lastEnvBackup != null)
            {
                var timeSince = DateTime.UtcNow - lastEnvBackup.Timestamp;
                if (timeSince.TotalMinutes < 1)
                    LastEnvBackupText.Text = "Just now";
                else if (timeSince.TotalHours < 1)
                    LastEnvBackupText.Text = $"{(int)timeSince.TotalMinutes}m ago";
                else if (timeSince.TotalDays < 1)
                    LastEnvBackupText.Text = $"{(int)timeSince.TotalHours}h ago";
                else
                    LastEnvBackupText.Text = lastEnvBackup.Timestamp.ToLocalTime().ToString("MMM dd");
            }
            else
            {
                LastEnvBackupText.Text = "Never";
            }

            var lastSettingsBackup = _allBackups
                .Where(b => b.Type == "system_settings")
                .OrderByDescending(b => b.Timestamp)
                .FirstOrDefault();

            if (lastSettingsBackup != null)
            {
                var timeSince = DateTime.UtcNow - lastSettingsBackup.Timestamp;
                if (timeSince.TotalMinutes < 1)
                    LastSettingsBackupText.Text = "Just now";
                else if (timeSince.TotalHours < 1)
                    LastSettingsBackupText.Text = $"{(int)timeSince.TotalMinutes}m ago";
                else if (timeSince.TotalDays < 1)
                    LastSettingsBackupText.Text = $"{(int)timeSince.TotalHours}h ago";
                else
                    LastSettingsBackupText.Text = lastSettingsBackup.Timestamp.ToLocalTime().ToString("MMM dd");
            }
            else
            {
                LastSettingsBackupText.Text = "Never";
            }
        }

        private async Task BackupProgramsAsync()
        {
            if (!OperatingSystem.IsWindows())
            {
                MessageBox.Show("System backup is only available on Windows.", "System Backup", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var result = MessageBox.Show(
                    "This will backup your list of installed programs.\n\nContinue?",
                    "Backup Programs",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    BackupProgramsBtn.IsEnabled = false;
                    BackupProgramsBtn.Content = "Backing up...";

                    if (_state == null)
                    {
                        MessageBox.Show("System not initialized properly.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    var systemBackup = new SystemBackupManager(_logger, _configManager, _state);
                    await systemBackup.BackupInstalledProgramsAsync();

                    await _state.SaveStateAsync();
                    await LoadSystemBackupsAsync();

                    MessageBox.Show("Programs backup completed successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Programs backup failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BackupProgramsBtn.IsEnabled = true;
                BackupProgramsBtn.Content = "💾 Backup";
            }
        }

        private async Task BackupEnvironmentAsync()
        {
            if (!OperatingSystem.IsWindows())
            {
                MessageBox.Show("System backup is only available on Windows.", "System Backup", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var result = MessageBox.Show(
                    "This will backup your environment variables.\n\nContinue?",
                    "Backup Environment",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    BackupEnvBtn.IsEnabled = false;
                    BackupEnvBtn.Content = "Backing up...";

                    if (_state == null)
                    {
                        MessageBox.Show("System not initialized properly.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    var systemBackup = new SystemBackupManager(_logger, _configManager, _state);
                    await systemBackup.BackupEnvironmentVariablesAsync();

                    await _state.SaveStateAsync();
                    await LoadSystemBackupsAsync();

                    MessageBox.Show("Environment variables backup completed successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Environment backup failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BackupEnvBtn.IsEnabled = true;
                BackupEnvBtn.Content = "💾 Backup";
            }
        }

        private async Task BackupWindowsSettingsAsync()
        {
            if (!OperatingSystem.IsWindows())
            {
                MessageBox.Show("System backup is only available on Windows.", "System Backup", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var result = MessageBox.Show(
                    "This will backup your Windows settings including:\n• Personalization (themes, colors)\n• File Explorer preferences\n• Taskbar settings\n• Regional settings\n• Mouse and keyboard preferences\n• Accessibility options\n\nContinue?",
                    "Backup Windows Settings",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    BackupSettingsBtn.IsEnabled = false;
                    BackupSettingsBtn.Content = "Backing up...";

                    if (_state == null)
                    {
                        MessageBox.Show("System not initialized properly.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    var systemBackup = new SystemBackupManager(_logger, _configManager, _state);
                    await systemBackup.BackupWindowsSettingsAsync();

                    await _state.SaveStateAsync();
                    await LoadSystemBackupsAsync();

                    MessageBox.Show("Windows settings backup completed successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Windows settings backup failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BackupSettingsBtn.IsEnabled = true;
                BackupSettingsBtn.Content = "💾 Backup";
            }
        }

        private async Task BackupFullSystemAsync()
        {
            if (!OperatingSystem.IsWindows())
            {
                MessageBox.Show("System backup is only available on Windows.", "System Backup", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var result = MessageBox.Show(
                    "This will backup:\n• Installed programs list\n• Environment variables\n• Windows settings\n\nContinue?",
                    "Full System Backup",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    BackupFullSystemBtn.IsEnabled = false;
                    BackupFullSystemBtn.Content = "Backing up...";

                    if (_state == null)
                    {
                        MessageBox.Show("System not initialized properly.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    var systemBackup = new SystemBackupManager(_logger, _configManager, _state);
                    await systemBackup.BackupSystemAsync();

                    await _state.SaveStateAsync();
                    await LoadSystemBackupsAsync();

                    MessageBox.Show("Full system backup completed successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"System backup failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BackupFullSystemBtn.IsEnabled = true;
                BackupFullSystemBtn.Content = "Backup Full System";
            }
        }

        private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is SystemBackupItem backup)
            {
                await RestoreSystemBackupAsync(backup);
            }
        }

        private Task RestoreSystemBackupAsync(SystemBackupItem backup)
        {
            if (!OperatingSystem.IsWindows())
            {
                MessageBox.Show("System restore is only available on Windows.", "System Restore", MessageBoxButton.OK, MessageBoxImage.Warning);
                return Task.CompletedTask;
            }

            try
            {
                var typeLabel = backup.Type switch
                {
                    "system_programs" => "programs",
                    "system_environment" => "environment variables",
                    "system_settings" => "Windows settings",
                    _ => "system data"
                };
                
                var result = MessageBox.Show(
                    $"Restore {typeLabel} from backup?\n\nCreated: {backup.Timestamp:MMM dd, yyyy HH:mm:ss}\n\nNote: This will download the backup and provide options for restoration.",
                    "Confirm Restore",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    if (_state == null || _storage == null)
                    {
                        MessageBox.Show("System not initialized properly.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return Task.CompletedTask;
                    }

                    var progressWindow = new Windows.RestoreProgressWindow(_storage, _state, backup.Type, backup.Path);
                    progressWindow.Owner = Window.GetWindow(this);
                    progressWindow.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"System restore failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            
            return Task.CompletedTask;
        }

        private async void DeleteBackup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is SystemBackupItem backup)
            {
                await DeleteSystemBackupAsync(backup);
            }
        }

        private async Task DeleteSystemBackupAsync(SystemBackupItem backup)
        {
            var typeLabel = backup.Type switch
            {
                "system_programs" => "programs",
                "system_environment" => "environment variables",
                "system_settings" => "Windows settings",
                _ => "system"
            };
            
            var result = MessageBox.Show(
                $"Delete this {typeLabel} backup?\n\nCreated: {backup.Timestamp:MMM dd, yyyy HH:mm:ss}\n\nThis action cannot be undone.",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    if (_storage != null)
                    {
                        await _storage.DeleteAsync(backup.Path);
                    }

                    if (_state != null && _state.BackupHistory.ContainsKey(backup.Type))
                    {
                        var toRemove = _state.BackupHistory[backup.Type]
                            .Where(b => b.Path == backup.Path)
                            .ToList();
                        
                        foreach (var b in toRemove)
                        {
                            _state.BackupHistory[backup.Type].Remove(b);
                        }

                        await _state.SaveStateAsync();
                    }

                    _allBackups.Remove(backup);
                    _systemBackups.Remove(backup);
                    UpdateStats();
                    UpdateStatistics();
                    UpdateEmptyState();

                    MessageBox.Show("Backup deleted successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Delete failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void ViewDetails_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is SystemBackupItem backup)
            {
                var typeLabel = backup.TypeLabel;
                var description = backup.Type switch
                {
                    "system_programs" => "a list of installed programs with Winget IDs and restore scripts.",
                    "system_environment" => "environment variables that can be restored to your system.",
                    "system_settings" => "Windows registry settings including personalization, File Explorer, taskbar, regional settings, and more.",
                    _ => "system backup data."
                };

                var details = $"Type: {typeLabel}\n\n" +
                             $"Created: {backup.Timestamp:MMM dd, yyyy HH:mm:ss}\n\n" +
                             $"Path: {backup.DisplayPath}\n\n" +
                             $"Description: This backup contains {description}";

                MessageBox.Show(details, "Backup Details", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OpenBackupFolder()
        {
            try
            {
                var localConfig = _configManager.StorageSources.FirstOrDefault(s => s.Key == "local").Value;
                if (localConfig != null && !string.IsNullOrEmpty(localConfig.Path))
                {
                    var systemBackupPath = Path.Combine(localConfig.Path, "backups", "system_backups");
                    
                    if (!Directory.Exists(systemBackupPath))
                    {
                        Directory.CreateDirectory(systemBackupPath);
                    }

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = systemBackupPath,
                        UseShellExecute = true
                    });
                }
                else
                {
                    MessageBox.Show("Local storage not configured.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open backup folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
