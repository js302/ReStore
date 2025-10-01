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
using ReStore.src.core;
using ReStore.src.storage;
using ReStore.src.utils;
using ReStore.src.backup;
using ReStore.Gui.Services;

namespace ReStore.Gui.Views.Pages
{
    public class SystemBackupItem
    {
        public string Type { get; set; } = "";
        public string Path { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public string TypeLabel => Type == "system_programs" ? "Installed Programs" : "Environment Variables";
        public string Icon => Type == "system_programs" ? "💻" : "⚙️";
        public Brush IconColor => Type == "system_programs" 
            ? new SolidColorBrush(Color.FromRgb(16, 185, 129))
            : new SolidColorBrush(Color.FromRgb(139, 92, 246));
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

                    if (_state == null || _storage == null)
                    {
                        MessageBox.Show("System not initialized properly.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    var systemBackup = new SystemBackupManager(_logger, _storage, _state);
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
                BackupProgramsBtn.Content = "Backup Programs";
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

                    if (_state == null || _storage == null)
                    {
                        MessageBox.Show("System not initialized properly.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    var systemBackup = new SystemBackupManager(_logger, _storage, _state);
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
                BackupEnvBtn.Content = "Backup Environment";
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
                    "This will backup:\n• Installed programs list\n• Environment variables\n\nContinue?",
                    "Full System Backup",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    BackupFullSystemBtn.IsEnabled = false;
                    BackupFullSystemBtn.Content = "Backing up...";

                    if (_state == null || _storage == null)
                    {
                        MessageBox.Show("System not initialized properly.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    var systemBackup = new SystemBackupManager(_logger, _storage, _state);
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
                var typeLabel = backup.Type == "system_programs" ? "programs" : "environment variables";
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
            var typeLabel = backup.Type == "system_programs" ? "programs" : "environment variables";
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
                var typeLabel = backup.Type == "system_programs" ? "Installed Programs" : "Environment Variables";
                var details = $"Type: {typeLabel}\n\n" +
                             $"Created: {backup.Timestamp:MMM dd, yyyy HH:mm:ss}\n\n" +
                             $"Path: {backup.Path}\n\n" +
                             $"Description: This backup contains " +
                             (backup.Type == "system_programs" 
                                ? "a list of installed programs with Winget IDs and restore scripts." 
                                : "environment variables that can be restored to your system.");

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
