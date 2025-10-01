using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ReStore.src.utils;
using ReStore.src.core;
using ReStore.src.monitoring;
using ReStore.src.storage;
using ReStore.src.backup;
using ReStore.Gui.Services;

namespace ReStore.Gui.Views.Pages
{
    public class BackupHistoryItem
    {
        public string Directory { get; set; } = "";
        public string Path { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public bool IsDiff { get; set; }
        public string TypeLabel => IsDiff ? "Differential" : "Full";
    }

    public partial class DashboardPage : Page, ILogger
    {
        private readonly Logger _fileLogger = new();
        private readonly ConfigManager _configManager;
        private SystemState? _state;
        private IStorage? _storage;
        private FileWatcher? _watcher;
        private readonly StringBuilder _logBuffer = new();
        private readonly ObservableCollection<BackupHistoryItem> _backupHistory = new();
        private System.Windows.Threading.DispatcherTimer? _statsTimer;

        public DashboardPage()
        {
            InitializeComponent();
            _configManager = new ConfigManager(this);

            ValidateBtn.Click += (_, __) => ValidateConfig();
            StartWatcherBtn.Click += async (_, __) => await StartWatcherAsync();
            StopWatcherBtn.Click += (_, __) => StopWatcher();
            ManualBackupBtn.Click += async (_, __) => await ManualBackupAsync();
            RestoreBtn.Click += async (_, __) => await RestoreBackupAsync();
            SystemBackupBtn.Click += async (_, __) => await SystemBackupAsync();
            RefreshHistoryBtn.Click += async (_, __) => await RefreshBackupHistoryAsync();
            ClearLogsBtn.Click += (_, __) => ClearLogs();

            BackupHistoryList.ItemsSource = _backupHistory;

            if (OperatingSystem.IsWindows())
            {
                SystemBackupBtn.Visibility = Visibility.Visible;
            }

            _ = InitializeAsync();
            StartStatsTimer();
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
            _statsTimer?.Stop();
            
            if (_watcher == null && _storage != null)
            {
                _storage?.Dispose();
                _storage = null;
            }
        }

        private async Task InitializeAsync()
        {
            try
            {
                await _configManager.LoadAsync();
                _state = new SystemState(this);
                await _state.LoadStateAsync();
                
                StatusText.Text = $"Config loaded: {_configManager.GetConfigFilePath()}";
                UpdateStatistics();
                await RefreshBackupHistoryAsync();
            }
            catch (Exception ex)
            {
                Log($"Failed to load config: {ex.Message}", LogLevel.Error);
            }
        }

        private void StartStatsTimer()
        {
            _statsTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _statsTimer.Tick += (_, __) => UpdateStatistics();
            _statsTimer.Start();
        }

        private void UpdateStatistics()
        {
            Dispatcher.Invoke(() =>
            {
                if (_state != null)
                {
                    var totalBackups = _state.BackupHistory.Values.Sum(list => list.Count);
                    TotalBackupsText.Text = totalBackups.ToString();

                    if (_state.LastBackupTime != DateTime.MinValue)
                    {
                        var timeSince = DateTime.UtcNow - _state.LastBackupTime;
                        if (timeSince.TotalMinutes < 1)
                            LastBackupText.Text = "Just now";
                        else if (timeSince.TotalHours < 1)
                            LastBackupText.Text = $"{(int)timeSince.TotalMinutes}m ago";
                        else if (timeSince.TotalDays < 1)
                            LastBackupText.Text = $"{(int)timeSince.TotalHours}h ago";
                        else
                            LastBackupText.Text = _state.LastBackupTime.ToLocalTime().ToString("MMM dd, HH:mm");
                    }
                    else
                    {
                        LastBackupText.Text = "Never";
                    }
                }

                WatchedDirsText.Text = _configManager.WatchDirectories.Count.ToString();

                var appSettings = AppSettings.Load();
                if (!string.IsNullOrEmpty(appSettings.DefaultStorage))
                {
                    StorageInfoText.Text = $"Storage: {appSettings.DefaultStorage}";
                }
            });
        }

        private async Task RefreshBackupHistoryAsync()
        {
            if (_state == null) return;

            await Task.Run(() =>
            {
                var items = new List<BackupHistoryItem>();
                foreach (var kvp in _state.BackupHistory)
                {
                    var directory = kvp.Key;
                    foreach (var backup in kvp.Value.OrderByDescending(b => b.Timestamp).Take(10))
                    {
                        items.Add(new BackupHistoryItem
                        {
                            Directory = System.IO.Path.GetFileName(directory),
                            Path = backup.Path,
                            Timestamp = backup.Timestamp,
                            IsDiff = backup.IsDiff
                        });
                    }
                }

                Dispatcher.Invoke(() =>
                {
                    _backupHistory.Clear();
                    foreach (var item in items.OrderByDescending(i => i.Timestamp).Take(20))
                    {
                        _backupHistory.Add(item);
                    }
                });
            });
        }

        private void ClearLogs()
        {
            _logBuffer.Clear();
            LogBox.Text = string.Empty;
        }

        public void Log(string message, LogLevel level = LogLevel.Info)
        {
            _fileLogger.Log(message, level);
            var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
            Dispatcher.Invoke(() =>
            {
                _logBuffer.AppendLine(line);
                LogBox.Text = _logBuffer.ToString();
                LogBox.ScrollToEnd();
            });
        }

        private void ValidateConfig()
        {
            try
            {
                var result = _configManager.ValidateConfiguration();
                if (!result.IsValid)
                {
                    foreach (var e in result.Errors) Log($"Config Error: {e}", LogLevel.Error);
                }
                foreach (var w in result.Warnings) Log($"Config Warning: {w}", LogLevel.Warning);
                foreach (var i in result.Info) Log($"Config Info: {i}", LogLevel.Info);
                StatusText.Text = result.IsValid ? "Configuration is valid" : "Configuration has errors";
            }
            catch (Exception ex)
            {
                Log($"Validation error: {ex.Message}", LogLevel.Error);
            }
        }

        private async Task StartWatcherAsync()
        {
            try
            {
                if (_watcher != null)
                {
                    Log("Watcher already running", LogLevel.Warning);
                    return;
                }

                var result = _configManager.ValidateConfiguration();
                if (!result.IsValid)
                {
                    Log("Cannot start watcher due to config errors", LogLevel.Error);
                    return;
                }

                if (_state == null)
                {
                    _state = new SystemState(this);
                    await _state.LoadStateAsync();
                }

                var appSettings = AppSettings.Load();
                var remote = string.IsNullOrWhiteSpace(appSettings.DefaultStorage) ? "local" : appSettings.DefaultStorage;
                _storage = await _configManager.CreateStorageAsync(remote);

                var sizeAnalyzer = new SizeAnalyzer();
                var compression = new CompressionUtil();
                _watcher = new FileWatcher(_configManager, this, _state, _storage, sizeAnalyzer, compression);
                await _watcher.StartAsync();

                WatcherService.Instance.SetWatcher(_watcher);

                StatusText.Text = "Watcher running";
                WatcherStatusText.Text = "Running";
                UpdateStatusIndicator(true);
                Log("File watcher started", LogLevel.Info);
                UpdateStatistics();
            }
            catch (Exception ex)
            {
                Log($"Failed to start watcher: {ex.Message}", LogLevel.Error);
                StopWatcher();
            }
        }

        private void StopWatcher()
        {
            try
            {
                _watcher?.Dispose();
                _watcher = null;

                WatcherService.Instance.SetWatcher(null);

                StatusText.Text = "Watcher stopped";
                WatcherStatusText.Text = "Stopped";
                UpdateStatusIndicator(false);
                Log("File watcher stopped", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Log($"Error stopping watcher: {ex.Message}", LogLevel.Warning);
            }
        }

        private async Task ManualBackupAsync()
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    ValidateNames = false,
                    CheckFileExists = false,
                    CheckPathExists = true,
                    FileName = "Select Folder",
                    Title = "Select folder to backup"
                };

                if (dialog.ShowDialog() == true)
                {
                    var folderPath = System.IO.Path.GetDirectoryName(dialog.FileName);
                    if (string.IsNullOrEmpty(folderPath))
                    {
                        Log("Invalid folder selected", LogLevel.Error);
                        return;
                    }

                    Log($"Starting manual backup of: {folderPath}", LogLevel.Info);

                    if (_state == null)
                    {
                        _state = new SystemState(this);
                        await _state.LoadStateAsync();
                    }

                    if (_storage == null)
                    {
                        var appSettings = AppSettings.Load();
                        var remote = string.IsNullOrWhiteSpace(appSettings.DefaultStorage) ? "local" : appSettings.DefaultStorage;
                        _storage = await _configManager.CreateStorageAsync(remote);
                    }

                    var sizeAnalyzer = new SizeAnalyzer();
                    var backup = new Backup(this, _state, sizeAnalyzer, _storage, _configManager);
                    
                    await backup.BackupDirectoryAsync(folderPath);
                    
                    Log($"Manual backup completed: {folderPath}", LogLevel.Info);
                    await RefreshBackupHistoryAsync();
                    UpdateStatistics();
                }
            }
            catch (Exception ex)
            {
                Log($"Manual backup failed: {ex.Message}", LogLevel.Error);
            }
        }

        private async Task RestoreBackupAsync()
        {
            try
            {
                if (_backupHistory.Count == 0)
                {
                    MessageBox.Show("No backups available to restore.", "Restore", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var dialog = new OpenFileDialog
                {
                    ValidateNames = false,
                    CheckFileExists = false,
                    CheckPathExists = false,
                    FileName = "Select Folder",
                    Title = "Select restore destination folder"
                };

                if (dialog.ShowDialog() == true)
                {
                    var targetPath = System.IO.Path.GetDirectoryName(dialog.FileName);
                    if (string.IsNullOrEmpty(targetPath))
                    {
                        Log("Invalid restore destination", LogLevel.Error);
                        return;
                    }

                    var latestBackup = _backupHistory.FirstOrDefault();
                    if (latestBackup == null)
                    {
                        Log("No backup selected", LogLevel.Error);
                        return;
                    }

                    var result = MessageBox.Show(
                        $"Restore from:\n{latestBackup.Directory}\n\nBackup date: {latestBackup.Timestamp:MMM dd, yyyy HH:mm:ss}\nType: {latestBackup.TypeLabel}\n\nTo: {targetPath}",
                        "Confirm Restore",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        Log($"Starting restore to: {targetPath}", LogLevel.Info);

                        if (_state == null)
                        {
                            _state = new SystemState(this);
                            await _state.LoadStateAsync();
                        }

                        if (_storage == null)
                        {
                            var appSettings = AppSettings.Load();
                            var remote = string.IsNullOrWhiteSpace(appSettings.DefaultStorage) ? "local" : appSettings.DefaultStorage;
                            _storage = await _configManager.CreateStorageAsync(remote);
                        }

                        var restore = new Restore(this, _state, _storage);
                        await restore.RestoreFromBackupAsync(latestBackup.Path, targetPath);

                        Log($"Restore completed: {targetPath}", LogLevel.Info);
                        MessageBox.Show("Restore completed successfully!", "Restore", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Restore failed: {ex.Message}", LogLevel.Error);
                MessageBox.Show($"Restore failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RestoreBackupBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string backupPath)
            {
                _ = RestoreSpecificBackupAsync(backupPath);
            }
        }

        private async Task RestoreSpecificBackupAsync(string backupPath)
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    ValidateNames = false,
                    CheckFileExists = false,
                    CheckPathExists = false,
                    FileName = "Select Folder",
                    Title = "Select restore destination folder"
                };

                if (dialog.ShowDialog() == true)
                {
                    var targetPath = System.IO.Path.GetDirectoryName(dialog.FileName);
                    if (string.IsNullOrEmpty(targetPath))
                    {
                        Log("Invalid restore destination", LogLevel.Error);
                        return;
                    }

                    var result = MessageBox.Show(
                        $"Restore backup to:\n{targetPath}",
                        "Confirm Restore",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        Log($"Starting restore to: {targetPath}", LogLevel.Info);

                        if (_state == null)
                        {
                            _state = new SystemState(this);
                            await _state.LoadStateAsync();
                        }

                        if (_storage == null)
                        {
                            var appSettings = AppSettings.Load();
                            var remote = string.IsNullOrWhiteSpace(appSettings.DefaultStorage) ? "local" : appSettings.DefaultStorage;
                            _storage = await _configManager.CreateStorageAsync(remote);
                        }

                        var restore = new Restore(this, _state, _storage);
                        await restore.RestoreFromBackupAsync(backupPath, targetPath);

                        Log($"Restore completed: {targetPath}", LogLevel.Info);
                        MessageBox.Show("Restore completed successfully!", "Restore", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Restore failed: {ex.Message}", LogLevel.Error);
                MessageBox.Show($"Restore failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task SystemBackupAsync()
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
                    "System Backup",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    Log("Starting system backup...", LogLevel.Info);

                    if (_state == null)
                    {
                        _state = new SystemState(this);
                        await _state.LoadStateAsync();
                    }

                    if (_storage == null)
                    {
                        var appSettings = AppSettings.Load();
                        var remote = string.IsNullOrWhiteSpace(appSettings.DefaultStorage) ? "local" : appSettings.DefaultStorage;
                        _storage = await _configManager.CreateStorageAsync(remote);
                    }

                    var systemBackup = new SystemBackupManager(this, _storage, _state);
                    await systemBackup.BackupSystemAsync();

                    Log("System backup completed", LogLevel.Info);
                    await RefreshBackupHistoryAsync();
                    UpdateStatistics();
                    MessageBox.Show("System backup completed successfully!", "System Backup", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Log($"System backup failed: {ex.Message}", LogLevel.Error);
                MessageBox.Show($"System backup failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateStatusIndicator(bool isRunning)
        {
            Dispatcher.Invoke(() =>
            {
                var color = isRunning ? System.Windows.Media.Color.FromRgb(16, 185, 129) : System.Windows.Media.Color.FromRgb(239, 68, 68);
                var colorAnimation = new System.Windows.Media.Animation.ColorAnimation
                {
                    To = color,
                    Duration = TimeSpan.FromMilliseconds(300),
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
                };
                
                var brush = StatusIndicator.Fill as System.Windows.Media.SolidColorBrush;
                if (brush != null)
                {
                    brush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, colorAnimation);
                }
                
                if (StatusIndicator.Effect is System.Windows.Media.Effects.DropShadowEffect effect)
                {
                    effect.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.ColorProperty, colorAnimation);
                }
            });
        }
    }
}
