using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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

        public string Directory { get; set; } = "";
        public string Group { get; set; } = "";
        public string Path { get; set; } = "";
        public string StorageType { get; set; } = "";
        public string? StorageBasePath { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsDiff { get; set; }
        public long SizeBytes { get; set; }
        public BackupArtifactType ArtifactType { get; set; } = BackupArtifactType.Archive;
        public List<string> ChunkIds { get; set; } = [];
        public string? ChunkStorageNamespace { get; set; }

        public string TypeLabel => CanVerify ? "Snapshot" : IsDiff ? "Differential" : "Full";
        public string SizeLabel => FormatBytes(SizeBytes);
        public string TimestampLabel => $"{Timestamp.ToUniversalTime():MMM dd, yyyy HH:mm:ss} UTC";
        public string StatusText => DateTime.UtcNow - Timestamp.ToUniversalTime() < TimeSpan.FromDays(7) ? "Recent" : "Archived";
        public Brush StatusColor => DateTime.UtcNow - Timestamp.ToUniversalTime() < TimeSpan.FromDays(7) ? _recentBrush : _archivedBrush;
        public bool CanVerify => IsSnapshotArtifactPath(Path);
        public bool CanRestore => IsSnapshotArtifactPath(Path);
        public bool CanOpenLocation => (IsLocalStorageType(StorageType) || System.IO.Path.IsPathRooted(Path))
            && TryResolveStoragePath(Path, StorageBasePath, out var resolvedPath)
            && (File.Exists(resolvedPath) || System.IO.Directory.Exists(resolvedPath));
        public Visibility OpenLocationVisibility => CanOpenLocation ? Visibility.Visible : Visibility.Collapsed;
        public string? ResolvedStoragePath => TryResolveStoragePath(Path, StorageBasePath, out var resolvedPath)
            ? resolvedPath
            : null;

        public string DisplayPath
        {
            get
            {
                if (!IsLocalStorageType(StorageType))
                {
                    return Path;
                }

                if (TryResolveStoragePath(Path, StorageBasePath, out var resolvedPath))
                {
                    return resolvedPath;
                }

                return Path;
            }
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

        private static bool IsSnapshotArtifactPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            return path.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("\\HEAD", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLocalStorageType(string? storageType)
        {
            return string.Equals(storageType, "local", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryResolveStoragePath(string? artifactPath, string? storageBasePath, out string resolvedPath)
        {
            resolvedPath = string.Empty;

            if (string.IsNullOrWhiteSpace(artifactPath))
            {
                return false;
            }

            if (System.IO.Path.IsPathRooted(artifactPath))
            {
                resolvedPath = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(artifactPath));
                return true;
            }

            if (string.IsNullOrWhiteSpace(storageBasePath))
            {
                return false;
            }

            var relativePath = artifactPath;
            if (relativePath.StartsWith("./", StringComparison.Ordinal) || relativePath.StartsWith(".\\", StringComparison.Ordinal))
            {
                relativePath = relativePath[2..];
            }

            var normalizedRelativePath = relativePath
                .Replace(System.IO.Path.AltDirectorySeparatorChar, System.IO.Path.DirectorySeparatorChar)
                .TrimStart(System.IO.Path.DirectorySeparatorChar);

            resolvedPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(storageBasePath, normalizedRelativePath));
            return true;
        }
    }

    public partial class BackupsPage : Page
    {
        private readonly ConfigManager _configManager;
        private readonly Logger _logger = new();
        private SystemState? _state;
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
            Unloaded += Page_Unloaded;

            _ = InitializeAsync();
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
        }

        private async Task InitializeAsync()
        {
            try
            {
                await _configManager.LoadAsync();
                _state = new SystemState(_logger);
                await _state.LoadStateAsync();

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

            await Task.Run(() =>
            {
                var items = new List<BackupItem>();

                foreach (var kvp in _state.BackupHistory)
                {
                    if (IsSystemBackupGroup(kvp.Key))
                    {
                        continue;
                    }

                    var directory = kvp.Key;
                    foreach (var backup in kvp.Value)
                    {
                        var storageType = string.IsNullOrWhiteSpace(backup.StorageType)
                            ? _configManager.GlobalStorageType
                            : backup.StorageType;

                        items.Add(new BackupItem
                        {
                            Directory = Path.GetFileName(directory),
                            Group = directory,
                            Path = backup.Path,
                            StorageType = storageType ?? string.Empty,
                            StorageBasePath = ResolveStorageBasePath(storageType),
                            Timestamp = backup.Timestamp,
                            IsDiff = backup.IsDiff,
                            SizeBytes = backup.SizeBytes,
                            ArtifactType = backup.ArtifactType,
                            ChunkIds = [.. backup.ChunkIds],
                            ChunkStorageNamespace = backup.ChunkStorageNamespace,
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

        private void VerifyBackup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is BackupItem backup)
            {
                _ = VerifySingleBackupAsync(backup);
            }
        }

        private void OpenBackupLocation_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is BackupItem backup)
            {
                OpenBackupLocation(backup);
            }
        }

        private async Task RestoreSingleBackupAsync(BackupItem backup)
        {
            try
            {
                if (!backup.CanRestore)
                {
                    MessageBox.Show(
                        "Restore is only available for snapshot manifest or HEAD artifacts on this page.",
                        "Restore Not Available",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
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
                    var targetPath = Path.GetDirectoryName(dialog.FileName);
                    if (string.IsNullOrEmpty(targetPath)) return;

                    var result = MessageBox.Show(
                        $"Restore backup:\n\n{backup.Directory}\n{backup.TimestampLabel}\n{backup.TypeLabel}\n\nTo: {targetPath}",
                        "Confirm Restore",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        using var storage = await CreateStorageForBackupAsync(backup);
                        var passwordProvider = App.GlobalPasswordProvider ?? new GuiPasswordProvider();
                        passwordProvider.SetEncryptionMode(false);
                        var restore = new Restore(_logger, storage, passwordProvider, _state);
                        await restore.RestoreFromBackupAsync(backup.Path, targetPath);
                        await SaveStateSafelyAsync("restore telemetry");

                        MessageBox.Show("Restore completed successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                await SaveStateSafelyAsync("restore telemetry");
                MessageBox.Show($"Restore failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task VerifySingleBackupAsync(BackupItem backup)
        {
            if (!backup.CanVerify)
            {
                MessageBox.Show(
                    "Verification is only available for snapshot manifests or HEAD references.",
                    "Verification Not Available",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var confirmation = MessageBox.Show(
                $"Verify snapshot integrity?\n\n{backup.Directory}\n{backup.TimestampLabel}\n\nPath: {backup.Path}",
                "Confirm Verification",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                using var storage = await CreateStorageForBackupAsync(backup);
                var passwordProvider = App.GlobalPasswordProvider ?? new GuiPasswordProvider();
                passwordProvider.SetEncryptionMode(false);

                var verifier = new SnapshotIntegrityVerifier(_logger, storage, passwordProvider, _state);
                var verificationResult = await verifier.VerifyAsync(backup.Path);
                await SaveStateSafelyAsync("verification telemetry");

                if (verificationResult.IsValid)
                {
                    MessageBox.Show(
                        $"Verification passed.\n\nSnapshot: {verificationResult.SnapshotId}\nFiles: {verificationResult.FileCount}\nUnique chunks: {verificationResult.UniqueChunks}",
                        "Verification Passed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var previewErrors = string.Join(
                    "\n",
                    verificationResult.Errors
                        .Take(5)
                        .Select((error, index) => $"{index + 1}. {error}"));
                var remainingErrors = verificationResult.Errors.Count > 5
                    ? $"\n...and {verificationResult.Errors.Count - 5} more issue(s)."
                    : string.Empty;

                MessageBox.Show(
                    $"Verification failed with {verificationResult.Errors.Count} issue(s).\n\n{previewErrors}{remainingErrors}",
                    "Verification Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                await SaveStateSafelyAsync("verification telemetry");
                MessageBox.Show($"Verification failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
                $"Delete this backup?\n\n{backup.Directory}\n{backup.TimestampLabel}\n\nThis action cannot be undone.",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    await DeleteBackupArtifactAsync(backup);
                    await SaveStateSafelyAsync("backup deletion");

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
                        await DeleteBackupArtifactAsync(backup);

                        _allBackups.Remove(backup);
                        _backups.Remove(backup);
                        successCount++;
                    }
                    catch
                    {
                        failCount++;
                    }
                }

                await SaveStateSafelyAsync("backup deletion");

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
                             $"Timestamp: {backup.TimestampLabel}\n\n" +
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

        private async Task SaveStateSafelyAsync(string context)
        {
            if (_state == null)
            {
                return;
            }

            try
            {
                await _state.SaveStateAsync();
            }
            catch (Exception ex)
            {
                _logger.Log($"Failed to persist {context}: {ex.Message}", LogLevel.Warning);
            }
        }

        private static bool IsSystemBackupGroup(string group)
        {
            return group.Equals("system_programs", StringComparison.OrdinalIgnoreCase)
                || group.Equals("system_environment", StringComparison.OrdinalIgnoreCase)
                || group.Equals("system_settings", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<IStorage> CreateStorageForBackupAsync(BackupItem backup)
        {
            var storageType = string.IsNullOrWhiteSpace(backup.StorageType)
                ? _configManager.GlobalStorageType
                : backup.StorageType;

            return await _configManager.CreateStorageAsync(storageType);
        }

        private async Task DeleteBackupArtifactAsync(BackupItem backup)
        {
            using var storage = await CreateStorageForBackupAsync(backup);

            if (backup.ArtifactType == BackupArtifactType.SnapshotManifest)
            {
                await DeleteSnapshotManifestBackupAsync(storage, backup);
            }
            else
            {
                await DeleteArchiveBackupAsync(storage, backup.Path);
            }

            _state?.RemoveBackupsFromGroup(backup.Group, [backup.Path]);
        }

        private async Task DeleteSnapshotManifestBackupAsync(IStorage storage, BackupItem backup)
        {
            var manifestExists = await storage.ExistsAsync(backup.Path);
            if (manifestExists)
            {
                await storage.DeleteAsync(backup.Path);
            }

            if (_state == null)
            {
                return;
            }

            var unreferencedChunkIds = _state.UnregisterChunkReferences(
                backup.StorageType,
                backup.ChunkIds,
                backup.ChunkStorageNamespace);

            foreach (var chunkId in unreferencedChunkIds)
            {
                string chunkPath;
                try
                {
                    chunkPath = SnapshotStoragePaths.GetChunkPath(chunkId, backup.ChunkStorageNamespace);
                }
                catch (ArgumentException ex)
                {
                    _logger.Log($"Invalid chunk metadata for '{chunkId}': {ex.Message}", LogLevel.Warning);
                    continue;
                }

                try
                {
                    if (await storage.ExistsAsync(chunkPath))
                    {
                        await storage.DeleteAsync(chunkPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log($"Failed deleting chunk '{chunkPath}': {ex.Message}", LogLevel.Warning);
                }
            }
        }

        private static async Task DeleteArchiveBackupAsync(IStorage storage, string backupPath)
        {
            if (await storage.ExistsAsync(backupPath))
            {
                await storage.DeleteAsync(backupPath);
            }

            if (!backupPath.EndsWith(".enc", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var metadataPath = backupPath + ".meta";
            if (await storage.ExistsAsync(metadataPath))
            {
                await storage.DeleteAsync(metadataPath);
            }
        }

        private string? ResolveStorageBasePath(string? storageType)
        {
            if (string.IsNullOrWhiteSpace(storageType))
            {
                return null;
            }

            if (!_configManager.StorageSources.TryGetValue(storageType, out var storageConfig)
                || string.IsNullOrWhiteSpace(storageConfig.Path))
            {
                return null;
            }

            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(storageConfig.Path));
        }

        private static void OpenBackupLocation(BackupItem backup)
        {
            var resolvedPath = backup.ResolvedStoragePath;
            if (!backup.CanOpenLocation || string.IsNullOrWhiteSpace(resolvedPath))
            {
                MessageBox.Show(
                    "This action is available only for local backups with existing files.",
                    "Open Location",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            try
            {
                var arguments = Directory.Exists(resolvedPath)
                    ? $"\"{resolvedPath}\""
                    : $"/select,\"{resolvedPath}\"";

                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = arguments,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open location: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
