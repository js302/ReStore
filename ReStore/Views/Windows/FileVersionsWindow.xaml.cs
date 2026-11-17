using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;
using ReStore.Services;

namespace ReStore.Views.Windows
{
    /// <summary>
    /// Every distinct version of one file across the backup's snapshots (consecutive identical contents collapsed),
    /// with restore of the chosen version. A version restores from the oldest snapshot that holds that content.
    /// </summary>
    public partial class FileVersionsWindow : Window
    {
        private readonly SnapshotBrowserHost _host;
        private readonly SnapshotBrowserSource _source;
        private readonly string? _originalLocation;
        private readonly string _relativePath;

        public FileVersionsWindow(
            SnapshotBrowserHost host,
            SnapshotBrowserSource source,
            string? originalLocation,
            string relativePath,
            FileVersionHistory history)
        {
            ArgumentNullException.ThrowIfNull(host);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(history);

            InitializeComponent();

            _host = host;
            _source = source;
            _originalLocation = originalLocation;
            _relativePath = relativePath;

            SummaryText.Text = relativePath;
            DetailText.Text = $"{history.Versions.Count} distinct version(s) across {history.SnapshotsScanned} snapshot(s), newest first. Select one to restore it.";
            if (history.Warnings.Count > 0)
            {
                WarningText.Text = $"{history.Warnings.Count} snapshot(s) could not be read, so some versions may be missing.";
                WarningText.Visibility = Visibility.Visible;
            }

            VersionList.ItemsSource = history.Versions.Select(version => new VersionRow(version)).ToList();
        }

        private FileVersion? SelectedVersion => (VersionList.SelectedItem as VersionRow)?.Version;

        private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var hasSelection = SelectedVersion != null;
            RestoreToBtn.IsEnabled = hasSelection;
            RestoreOriginalBtn.IsEnabled = hasSelection && _originalLocation != null;
        }

        private async void RestoreToBtn_Click(object sender, RoutedEventArgs e) =>
            await ErrorReporter.GuardAsync("Restore version", async () =>
            {
                var dialog = new OpenFolderDialog { Title = "Select restore destination folder", Multiselect = false };
                if (dialog.ShowDialog(this) != true || string.IsNullOrEmpty(dialog.FolderName))
                {
                    return;
                }

                await RestoreSelectedAsync(dialog.FolderName);
            });

        private async void RestoreOriginalBtn_Click(object sender, RoutedEventArgs e) =>
            await ErrorReporter.GuardAsync("Restore version", async () =>
            {
                var original = _originalLocation;
                if (original == null)
                {
                    return;
                }

                await RestoreSelectedAsync(original);
            });

        private async Task RestoreSelectedAsync(string targetDirectory)
        {
            if (SelectedVersion is not { } version)
            {
                return;
            }

            await _host.RestoreAsync(
                this,
                $"Restoring {Path.GetFileName(_relativePath)} ({DisplayTime.Format(version.SnapshotCreatedUtc)})",
                version.ManifestPath,
                _source.StorageType,
                targetDirectory,
                new HashSet<string>([_relativePath], StringComparer.OrdinalIgnoreCase));
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        private sealed class VersionRow(FileVersion version)
        {
            public FileVersion Version { get; } = version;

            public string BackedUpLabel => DisplayTime.Format(Version.SnapshotCreatedUtc);

            public string BackedUpTooltip => DisplayTime.Tooltip(Version.SnapshotCreatedUtc) + " (UTC)";

            public string SizeLabel => ByteFormatter.Format(Version.SizeBytes);

            public string ModifiedLabel => DisplayTime.Format(Version.LastModifiedUtc);

            public string CountLabel => Version.SnapshotCount == 1 ? "1 snapshot" : $"{Version.SnapshotCount} snapshots";
        }
    }
}
