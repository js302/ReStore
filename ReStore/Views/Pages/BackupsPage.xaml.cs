using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ReStore.Core.src.core;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;
using ReStore.Services;

namespace ReStore.Views.Pages
{
    public class BackupItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private static readonly Brush _recentBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
        private static readonly Brush _archivedBrush = new SolidColorBrush(Color.FromRgb(107, 114, 128));
        private static string? _storageBasePath;

        public string Directory { get; set; } = "";
        public string Path { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public bool IsDiff { get; set; }
        public long SizeBytes { get; set; }
        
        public string TypeLabel => IsDiff ? "Differential" : "Full";
        public string SizeLabel => FormatBytes(SizeBytes);
        public string StatusText => DateTime.UtcNow - Timestamp < TimeSpan.FromDays(7) ? "Recent" : "Archived";
        public Brush StatusColor => DateTime.UtcNow - Timestamp < TimeSpan.FromDays(7) ? _recentBrush : _archivedBrush;
        
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

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private static string FormatBytes(long bytes)
        {
            if (bytes == 0) return "Unknown";
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:0.##} {sizes[order]}";
        }
    }

    public partial class BackupsPage : Page
    {
        private readonly ConfigManager _configManager;
        private readonly Logger _logger = new();
        private SystemState? _state;
        private IStorage? _storage;
        private readonly ObservableCollection<BackupItem> _backups = [];
        private List<BackupItem> _allBackups = [];

        public BackupsPage()
        {
            InitializeComponent();
            _configManager = new ConfigManager(_logger);

            BackupsList.ItemsSource = _backups;

            SearchBox.TextChanged += (_, __) => ApplyFilters();
            FilterTypeCombo.SelectionChanged += (_, __) => ApplyFilters();
            SortCombo.SelectionChanged += (_, __) => ApplyFilters();
            RefreshBtn.Click += async (_, __) => await LoadBackupsAsync();
            
            RestoreSelectedBtn.Click += async (_, __) => await RestoreSelectedAsync();
            DeleteSelectedBtn.Click += async (_, __) => await DeleteSelectedAsync();
            SelectAllBtn.Click += (_, __) => SelectAll();
            DeselectAllBtn.Click += (_, __) => DeselectAll();

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
                    BackupItem.SetStorageBasePath(storageConfig.Path);
                }

                await LoadBackupsAsync();
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

        private async Task LoadBackupsAsync()
        {
            if (_state == null) return;

            await Task.Run(async () =>
            {
                var items = new List<BackupItem>();
                
                foreach (var kvp in _state.BackupHistory)
                {
                    var directory = kvp.Key;
                    foreach (var backup in kvp.Value)
                    {
                        long size = 0;
                        if (_storage != null)
                        {
                            try
                            {
                                size = await GetBackupSizeAsync(backup.Path);
                            }
                            catch
                            {
                                size = 0;
                            }
                        }

                        items.Add(new BackupItem
                        {
                            Directory = System.IO.Path.GetFileName(directory),
                            Path = backup.Path,
                            Timestamp = backup.Timestamp,
                            IsDiff = backup.IsDiff,
                            SizeBytes = size,
                            IsSelected = false
                        });
                    }
                }

                Dispatcher.Invoke(() =>
                {
                    _allBackups = items;
                    ApplyFilters();
                });
            });
        }

        private async Task<long> GetBackupSizeAsync(string path)
        {
            if (_storage == null) return 0;

            try
            {
                return await Task.Run(() =>
                {
                    var localPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(path));
                    
                    if (_storage.GetType().Name.Contains("LocalStorage"))
                    {
                        if (File.Exists(path))
                        {
                            return new FileInfo(path).Length;
                        }
                    }
                    
                    return 0;
                });
            }
            catch
            {
                return 0;
            }
        }

        private void ApplyFilters()
        {
            var filtered = _allBackups.AsEnumerable();

            var searchText = SearchBox.Text?.Trim().ToLower();
            if (!string.IsNullOrEmpty(searchText))
            {
                filtered = filtered.Where(b => b.Directory.ToLower().Contains(searchText) || 
                                              b.Path.ToLower().Contains(searchText));
            }

            if (FilterTypeCombo.SelectedItem is ComboBoxItem filterItem)
            {
                var filterTag = filterItem.Tag?.ToString();
                filtered = filterTag switch
                {
                    "full" => filtered.Where(b => !b.IsDiff),
                    "diff" => filtered.Where(b => b.IsDiff),
                    _ => filtered
                };
            }

            if (SortCombo.SelectedItem is ComboBoxItem sortItem)
            {
                var sortTag = sortItem.Tag?.ToString();
                filtered = sortTag switch
                {
                    "oldest" => filtered.OrderBy(b => b.Timestamp),
                    "dir_asc" => filtered.OrderBy(b => b.Directory),
                    "dir_desc" => filtered.OrderByDescending(b => b.Directory),
                    _ => filtered.OrderByDescending(b => b.Timestamp)
                };
            }

            _backups.Clear();
            foreach (var item in filtered)
            {
                _backups.Add(item);
            }

            UpdateStats();
            UpdateSelectionButtons();
        }

        private void UpdateStats()
        {
            var total = _backups.Count;
            var selected = _backups.Count(b => b.IsSelected);
            var totalSize = _backups.Sum(b => b.SizeBytes);

            if (selected > 0)
            {
                StatsText.Text = $"{selected} of {total} selected • {FormatBytes(totalSize)} total";
            }
            else
            {
                StatsText.Text = $"{total} backups • {FormatBytes(totalSize)} total";
            }
        }

        private void UpdateSelectionButtons()
        {
            var hasSelection = _backups.Any(b => b.IsSelected);
            RestoreSelectedBtn.IsEnabled = hasSelection;
            DeleteSelectedBtn.IsEnabled = hasSelection;
        }

        private void SelectAll()
        {
            foreach (var backup in _backups)
            {
                backup.IsSelected = true;
            }
            UpdateStats();
            UpdateSelectionButtons();
        }

        private void DeselectAll()
        {
            foreach (var backup in _backups)
            {
                backup.IsSelected = false;
            }
            UpdateStats();
            UpdateSelectionButtons();
        }

        private void RestoreBackup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is BackupItem backup)
            {
                _ = RestoreSingleBackupAsync(backup);
            }
        }

        private async Task RestoreSingleBackupAsync(BackupItem backup)
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
                    var targetPath = Path.GetDirectoryName(dialog.FileName);
                    if (string.IsNullOrEmpty(targetPath)) return;

                    var result = MessageBox.Show(
                        $"Restore backup:\n\n{backup.Directory}\n{backup.Timestamp:MMM dd, yyyy HH:mm:ss}\n{backup.TypeLabel}\n\nTo: {targetPath}",
                        "Confirm Restore",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        if (_storage == null) return;

                        var passwordProvider = App.GlobalPasswordProvider ?? new Services.GuiPasswordProvider();
                        passwordProvider.SetEncryptionMode(false);
                        var restore = new Restore(_logger, _storage, passwordProvider);
                        await restore.RestoreFromBackupAsync(backup.Path, targetPath);

                        MessageBox.Show("Restore completed successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Restore failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task RestoreSelectedAsync()
        {
            var selected = _backups.Where(b => b.IsSelected).ToList();
            if (selected.Count == 0) return;

            var result = MessageBox.Show(
                $"Restore {selected.Count} backup(s)?\n\nYou will be prompted for a destination folder for each backup.",
                "Confirm Restore",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                foreach (var backup in selected)
                {
                    await RestoreSingleBackupAsync(backup);
                }
            }
        }

        private void DeleteBackup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is BackupItem backup)
            {
                _ = DeleteSingleBackupAsync(backup);
            }
        }

        private async Task DeleteSingleBackupAsync(BackupItem backup)
        {
            var result = MessageBox.Show(
                $"Delete this backup?\n\n{backup.Directory}\n{backup.Timestamp:MMM dd, yyyy HH:mm:ss}\n\nThis action cannot be undone.",
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
                        
                        // If encrypted, also delete the metadata file
                        if (backup.Path.EndsWith(".enc", StringComparison.OrdinalIgnoreCase))
                        {
                            var metadataPath = backup.Path + ".meta";
                            try
                            {
                                await _storage.DeleteAsync(metadataPath);
                            }
                            catch (Exception metaEx)
                            {
                                _logger.Log($"Warning: Failed to delete metadata file: {metaEx.Message}", LogLevel.Warning);
                            }
                        }
                    }

                    if (_state != null)
                    {
                        foreach (var kvp in _state.BackupHistory.ToList())
                        {
                            var toRemove = kvp.Value.Where(b => b.Path == backup.Path).ToList();
                            foreach (var b in toRemove)
                            {
                                kvp.Value.Remove(b);
                            }
                        }
                        await _state.SaveStateAsync();
                    }

                    _allBackups.Remove(backup);
                    _backups.Remove(backup);
                    UpdateStats();

                    MessageBox.Show("Backup deleted successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Delete failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async Task DeleteSelectedAsync()
        {
            var selected = _backups.Where(b => b.IsSelected).ToList();
            if (selected.Count == 0) return;

            var result = MessageBox.Show(
                $"Delete {selected.Count} backup(s)?\n\nThis action cannot be undone.",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                var successCount = 0;
                var failCount = 0;

                foreach (var backup in selected)
                {
                    try
                    {
                        if (_storage != null)
                        {
                            await _storage.DeleteAsync(backup.Path);
                            
                            // If encrypted, also delete the metadata file
                            if (backup.Path.EndsWith(".enc", StringComparison.OrdinalIgnoreCase))
                            {
                                var metadataPath = backup.Path + ".meta";
                                try
                                {
                                    await _storage.DeleteAsync(metadataPath);
                                }
                                catch
                                {
                                    // Silently ignore metadata deletion errors in batch operations
                                }
                            }
                        }

                        if (_state != null)
                        {
                            foreach (var kvp in _state.BackupHistory.ToList())
                            {
                                var toRemove = kvp.Value.Where(b => b.Path == backup.Path).ToList();
                                foreach (var b in toRemove)
                                {
                                    kvp.Value.Remove(b);
                                }
                            }
                        }

                        _allBackups.Remove(backup);
                        _backups.Remove(backup);
                        successCount++;
                    }
                    catch
                    {
                        failCount++;
                    }
                }

                if (_state != null)
                {
                    await _state.SaveStateAsync();
                }

                UpdateStats();

                if (failCount > 0)
                {
                    MessageBox.Show($"Deleted {successCount} backup(s). Failed to delete {failCount}.", "Partial Success", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show($"Successfully deleted {successCount} backup(s).", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void ShowDetails_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is BackupItem backup)
            {
                var details = $"Directory: {backup.Directory}\n\n" +
                             $"Path: {backup.DisplayPath}\n\n" +
                             $"Timestamp: {backup.Timestamp:MMM dd, yyyy HH:mm:ss}\n\n" +
                             $"Type: {backup.TypeLabel}\n\n" +
                             $"Size: {backup.SizeLabel}\n\n" +
                             $"Status: {backup.StatusText}";

                MessageBox.Show(details, "Backup Details", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes == 0) return "0 B";
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:0.##} {sizes[order]}";
        }
    }
}
