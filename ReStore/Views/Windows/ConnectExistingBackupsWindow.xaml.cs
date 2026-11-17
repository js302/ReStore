using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;
using ReStore.Services;

namespace ReStore.Views.Windows
{
    /// <summary>
    /// Disaster recovery: scans a configured storage provider for existing ReStore backups (no local state needed),
    /// then adds them to this PC's history ("adopt", optionally under a different folder) or restores from them.
    /// </summary>
    public partial class ConnectExistingBackupsWindow : Window
    {
        private DiscoveryResult? _result;
        private string? _scannedStorageType;

        public ConnectExistingBackupsWindow()
        {
            InitializeComponent();
            Loaded += async (_, __) => await ErrorReporter.GuardAsync("Loading storage providers", LoadStoragesAsync);
        }

        /// <summary>True once at least one snapshot was added to the history.</summary>
        public bool AdoptedAny { get; private set; }

        private async Task LoadStoragesAsync()
        {
            var config = await AppServices.GetConfigManagerAsync();
            StorageCombo.ItemsSource = config.StorageSources.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
            StorageCombo.SelectedItem = config.GlobalStorageType;
            if (StorageCombo.SelectedItem == null && StorageCombo.Items.Count > 0)
            {
                StorageCombo.SelectedIndex = 0;
            }
        }

        private static IPasswordProvider PasswordProvider => App.GlobalPasswordProvider ?? new GuiPasswordProvider();

        private async void ScanBtn_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Scanning for existing backups", ScanAsync);
        }

        private async Task ScanAsync()
        {
            if (StorageCombo.SelectedItem is not string storageType)
            {
                return;
            }

            var config = await AppServices.GetConfigManagerAsync();
            DiscoveryResult? found = null;

            var progress = new OperationProgressWindow("Scanning for existing backups", AppServices.Logger);
            progress.RunModal(this, async token =>
            {
                using var storage = await config.CreateStorageAsync(storageType);
                found = await new RepositoryDiscovery(progress).DiscoverAsync(storage, PasswordProvider, token);
            });

            if (!progress.Succeeded)
            {
                if (progress.Failure != null)
                {
                    MessageBox.Show($"Could not scan '{storageType}': {progress.Failure.Message}", "Scan failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                return;
            }

            _result = found;
            _scannedStorageType = storageType;
            GroupList.ItemsSource = found!.Groups.Select(group => new GroupRow(group)).ToList();
            SnapshotList.ItemsSource = null;
            StatusText.Text = found.Groups.Count == 0
                ? "No ReStore backups were found in this storage."
                : $"Found {found.Groups.Count} folder(s) with {found.Groups.Sum(group => group.Snapshots.Count)} snapshot(s).";
            UpdateButtons();
        }

        private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = GroupList.SelectedItems.OfType<GroupRow>().ToList();
            SnapshotList.ItemsSource = selected.Count == 1
                ? selected[0].Group.Snapshots.Select(snapshot => new SnapshotRow(snapshot)).ToList()
                : null;
            MapTargetBox.IsEnabled = MapBrowseBtn.IsEnabled = selected.Count == 1;
            if (selected.Count != 1)
            {
                MapTargetBox.Text = string.Empty;
            }

            UpdateButtons();
        }

        private void SnapshotList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

        private void UpdateButtons()
        {
            AdoptBtn.IsEnabled = _result != null && GroupList.SelectedItems.Count > 0;
            RestoreBtn.IsEnabled = _result != null && SnapshotList.SelectedItem is SnapshotRow;
        }

        private void MapBrowseBtn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Folder on this PC for the adopted backups" };
            if (dialog.ShowDialog() == true)
            {
                MapTargetBox.Text = dialog.FolderName;
            }
        }

        private async void AdoptBtn_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Adding backups to the history", AdoptAsync);
        }

        private async Task AdoptAsync()
        {
            var groups = GroupList.SelectedItems.OfType<GroupRow>().Select(row => row.Group).ToList();
            if (groups.Count == 0 || _scannedStorageType == null)
            {
                return;
            }

            var mapping = new PathMapping();
            var target = MapTargetBox.Text.Trim();
            if (groups.Count == 1 && target.Length > 0)
            {
                mapping = PathMapping.Parse([$"{groups[0].OriginalPath}={target}"]);
            }

            var config = await AppServices.GetConfigManagerAsync();
            var state = await AppServices.GetSystemStateAsync();
            var storageType = _scannedStorageType;
            AdoptResult? adopted = null;

            var progress = new OperationProgressWindow("Adding existing backups to the history", AppServices.Logger);
            progress.RunModal(this, async token =>
            {
                using var storage = await config.CreateStorageAsync(storageType);
                adopted = await new RepositoryAdopter(progress, config, state, null, PasswordProvider).AdoptAsync(storage, storageType, groups, mapping, token);
            });

            if (progress.Failure != null)
            {
                MessageBox.Show($"Could not add the backups: {progress.Failure.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (adopted == null)
            {
                return;
            }

            AdoptedAny |= adopted.SnapshotsAdopted > 0;
            MessageBox.Show(
                $"Added {adopted.SnapshotsAdopted} snapshot(s) to the history ({adopted.SnapshotsAlreadyKnown} were already there, {adopted.SnapshotsFailed} failed).\n\nThey now appear on the Backups page. To keep backing these folders up, add them under Settings.",
                "Backups connected",
                MessageBoxButton.OK,
                adopted.SnapshotsFailed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        private async void RestoreBtn_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Restoring a snapshot", RestoreAsync);
        }

        private async Task RestoreAsync()
        {
            if (SnapshotList.SelectedItem is not SnapshotRow row || _scannedStorageType == null)
            {
                return;
            }

            var dialog = new OpenFolderDialog { Title = "Select restore destination folder", Multiselect = false };
            if (dialog.ShowDialog() != true || string.IsNullOrEmpty(dialog.FolderName))
            {
                return;
            }

            var targetPath = dialog.FolderName;
            var config = await AppServices.GetConfigManagerAsync();
            var state = await AppServices.GetSystemStateAsync();
            var storageType = _scannedStorageType;
            var manifestPath = row.Snapshot.ManifestPath;

            RestorePreview preview;
            using (var previewStorage = await config.CreateStorageAsync(storageType))
            {
                preview = await new Restore(AppServices.Logger, previewStorage, PasswordProvider, state).PreviewRestoreAsync(manifestPath, targetPath);
            }

            var confirm = new RestoreConfirmWindow(preview) { Owner = this };
            if (confirm.ShowDialog() != true || !confirm.Confirmed)
            {
                return;
            }

            var progress = new OperationProgressWindow("Restoring from existing backups", AppServices.Logger);
            // Read the confirm dialog here: the operation below runs on a pool thread.
            var restoreOptions = new RestoreOptions { ConflictPolicy = confirm.ConflictPolicy, RelativePaths = confirm.SelectedRelativePaths };
            progress.RunModal(this, async token =>
            {
                using var storage = await config.CreateStorageAsync(storageType);
                var restore = new Restore(progress, storage, PasswordProvider, state);
                var outcome = await restore.RestoreFromBackupAsync(
                    manifestPath,
                    targetPath,
                    restoreOptions,
                    progress.RestoreProgress,
                    token);
                progress.Log($"Restored {outcome.FilesRestored} file(s); skipped {outcome.FilesSkipped}, kept both {outcome.FilesKeptBoth}, overwrote {outcome.FilesOverwritten}.", LogLevel.Info);
            });

            await ActivityRecorder.TryPersistAsync(state);

            if (progress.Succeeded)
            {
                MessageBox.Show("Restore completed successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (progress.Failure != null)
            {
                MessageBox.Show($"Restore failed: {progress.Failure.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        private sealed class GroupRow(DiscoveredGroup group)
        {
            public DiscoveredGroup Group { get; } = group;

            public string Title => Group.OriginalPath;

            public string Detail =>
                $"{Group.Snapshots.Count} snapshot(s)" +
                (Group.MachineName != null ? $" from {Group.MachineName}" : string.Empty) +
                (Group.Encrypted ? ", encrypted" : string.Empty) +
                (Group.Warnings.Count > 0 ? $", {Group.Warnings.Count} warning(s)" : string.Empty);
        }

        private sealed class SnapshotRow(DiscoveredSnapshot snapshot)
        {
            public DiscoveredSnapshot Snapshot { get; } = snapshot;

            public string Title => $"{Snapshot.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)}{(Snapshot.IsHead ? "  (latest)" : string.Empty)}";

            public string Detail => $"{Snapshot.FileCount} files, {ByteFormatter.Format(Snapshot.TotalBytes)}";
        }
    }
}
