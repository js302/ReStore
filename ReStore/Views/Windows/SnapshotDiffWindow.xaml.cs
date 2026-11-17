using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;

namespace ReStore.Views.Windows
{
    /// <summary>Shows what differs between two snapshots of one backup. Built from a manifest-only <see cref="SnapshotDiff"/>.</summary>
    public partial class SnapshotDiffWindow : Window
    {
        private static readonly Brush AddedBrush = new SolidColorBrush(Color.FromRgb(4, 120, 87));
        private static readonly Brush RemovedBrush = new SolidColorBrush(Color.FromRgb(185, 28, 28));
        private static readonly Brush ChangedBrush = new SolidColorBrush(Color.FromRgb(180, 83, 9));

        private enum Filter { All, Added, Removed, Changed }

        private readonly List<DiffRow> _all;

        public SnapshotDiffWindow(SnapshotDiff diff, string directory)
        {
            ArgumentNullException.ThrowIfNull(diff);

            InitializeComponent();

            SummaryText.Text = directory;
            RangeText.Text = $"Older: {DisplayTime.Format(diff.CreatedUtcA)}   Newer: {DisplayTime.Format(diff.CreatedUtcB)}";
            CountsText.Text = diff.HasDifferences
                ? $"{diff.Added.Count} added, {diff.Removed.Count} removed, {diff.Changed.Count} changed, {diff.UnchangedCount} unchanged"
                : $"No differences: all {diff.UnchangedCount} file(s) are identical.";

            _all =
            [
                .. diff.Added.Select(e => new DiffRow(Filter.Added, "Added", AddedBrush, e.RelativePath, ByteFormatter.Format(e.SizeBytesB ?? 0))),
                .. diff.Removed.Select(e => new DiffRow(Filter.Removed, "Removed", RemovedBrush, e.RelativePath, ByteFormatter.Format(e.SizeBytesA ?? 0))),
                .. diff.Changed.Select(e => new DiffRow(Filter.Changed, e.CaseRename ? "Case" : "Changed", ChangedBrush,
                    e.CaseRename ? $"{e.RelativePath} -> {e.RelativePathB} (letter case differs)" : e.RelativePath,
                    $"{ByteFormatter.Format(e.SizeBytesA ?? 0)} -> {ByteFormatter.Format(e.SizeBytesB ?? 0)}")),
            ];

            FilterCombo.ItemsSource = new[]
            {
                $"All ({_all.Count})",
                $"Added ({diff.Added.Count})",
                $"Removed ({diff.Removed.Count})",
                $"Changed ({diff.Changed.Count})"
            };
            FilterCombo.SelectedIndex = 0;
        }

        private void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var filter = (Filter)Math.Max(FilterCombo.SelectedIndex, 0);
            DiffList.ItemsSource = filter == Filter.All ? _all : _all.Where(row => row.Kind == filter).ToList();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        private sealed record DiffRow(Filter Kind, string KindLabel, Brush KindBrush, string RelativePath, string SizeLabel);
    }
}
