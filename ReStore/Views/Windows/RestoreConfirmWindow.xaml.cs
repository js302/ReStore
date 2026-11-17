using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;
using ReStore.Services;

namespace ReStore.Views.Windows
{
    /// <summary>
    /// Shows what a restore would write before it writes anything, and collects the conflict
    /// policy and file subset. Built from a <see cref="RestorePreview"/>, so opening it costs
    /// one manifest read and no chunk downloads.
    /// </summary>
    public partial class RestoreConfirmWindow : Window
    {
        private static readonly Brush DiffersBrush = new SolidColorBrush(Color.FromRgb(185, 28, 28));
        private static readonly Brush IdenticalBrush = new SolidColorBrush(Color.FromRgb(107, 114, 128));

        private readonly ObservableCollection<PreviewRow> _rows = [];

        public RestoreConfirmWindow(RestorePreview preview)
        {
            ArgumentNullException.ThrowIfNull(preview);

            InitializeComponent();

            SummaryText.Text =
                $"Restore {preview.FileCount} file(s), {ByteFormatter.Format(preview.TotalBytes)}, from snapshot of {preview.SnapshotCreatedUtc.ToLocalTime():MMM dd, yyyy HH:mm}";
            TargetText.Text = $"Destination: {preview.TargetDirectory}";

            if (preview.DifferingFileCount > 0)
            {
                ConflictWarningText.Visibility = Visibility.Visible;
                ConflictWarningText.Text =
                    $"{preview.DifferingFileCount} file(s) already exist at the destination with different contents. " +
                    "Choose below what should happen to them.";
            }
            else if (preview.IdenticalFileCount > 0)
            {
                ConflictWarningText.Visibility = Visibility.Visible;
                ConflictWarningText.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
                ConflictWarningText.Text =
                    $"{preview.IdenticalFileCount} file(s) already exist and are identical to the backup copy.";
            }

            if (PackagedPathVirtualization.IsAffected(preview.TargetDirectory, ReStore.Interop.PackageInfo.IsPackaged))
            {
                VirtualizationWarningText.Text = PackagedPathVirtualization.Warning;
                VirtualizationWarningText.Visibility = Visibility.Visible;
            }

            foreach (var entry in preview.Entries)
            {
                var row = new PreviewRow(entry);
                row.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(PreviewRow.IsSelected))
                    {
                        UpdateSelectionSummary();
                    }
                };
                _rows.Add(row);
            }

            FileList.ItemsSource = _rows;
            UpdateSelectionSummary();
        }

        public RestoreConflictPolicy ConflictPolicy { get; private set; } = RestoreConflictPolicy.Skip;

        /// <summary>
        /// Selected manifest paths, or null when everything is selected so the caller can
        /// skip filtering entirely.
        /// </summary>
        public IReadOnlySet<string>? SelectedRelativePaths { get; private set; }

        public bool Confirmed { get; private set; }

        private void UpdateSelectionSummary()
        {
            var selected = _rows.Where(row => row.IsSelected).ToList();
            var bytes = selected.Sum(row => row.SizeBytes);

            SelectionText.Text = $"{selected.Count} of {_rows.Count} files selected - {ByteFormatter.Format(bytes)}";
            RestoreBtn.IsEnabled = selected.Count > 0;
        }

        private void SelectAllBtn_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _rows)
            {
                row.IsSelected = true;
            }
        }

        private void DeselectAllBtn_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _rows)
            {
                row.IsSelected = false;
            }
        }

        private void RestoreBtn_Click(object sender, RoutedEventArgs e)
        {
            ConflictPolicy = true switch
            {
                _ when OverwriteRadio.IsChecked == true => RestoreConflictPolicy.Overwrite,
                _ when KeepBothRadio.IsChecked == true => RestoreConflictPolicy.KeepBoth,
                _ => RestoreConflictPolicy.Skip
            };

            var selected = _rows.Where(row => row.IsSelected).Select(row => row.RelativePath).ToList();

            SelectedRelativePaths = selected.Count == _rows.Count
                ? null
                : selected.ToHashSet(StringComparer.OrdinalIgnoreCase);

            Confirmed = true;
            DialogResult = true;
            Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private sealed class PreviewRow : INotifyPropertyChanged
        {
            private bool _isSelected = true;

            public PreviewRow(RestorePreviewEntry entry)
            {
                RelativePath = entry.RelativePath;
                SizeBytes = entry.SizeBytes;
                Conflict = entry.Conflict;
            }

            public string RelativePath { get; }
            public long SizeBytes { get; }
            public RestoreConflictKind Conflict { get; }

            public string SizeLabel => ByteFormatter.Format(SizeBytes);

            public string ConflictLabel => Conflict switch
            {
                RestoreConflictKind.Differs => "WOULD REPLACE",
                RestoreConflictKind.Identical => "IDENTICAL",
                _ => string.Empty
            };

            public Brush ConflictColor => Conflict == RestoreConflictKind.Differs ? DiffersBrush : IdenticalBrush;

            public Visibility ConflictVisibility =>
                Conflict == RestoreConflictKind.None ? Visibility.Collapsed : Visibility.Visible;

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value)
                    {
                        return;
                    }

                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
        }
    }
}
