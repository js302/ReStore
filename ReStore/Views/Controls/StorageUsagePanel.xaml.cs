using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.utils;
using ReStore.Services;
using ReStore.Views.Windows;

namespace ReStore.Views.Controls
{
    public sealed class StorageUsageRow(GroupStorageUsage usage)
    {
        public string Group => usage.Group;
        public string Storage => usage.StorageType;
        public string Snapshots => usage.SnapshotCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public string Original => ByteFormatter.Format(usage.LatestLogicalBytes);
        public string Stored => (usage.HasEstimates ? "~" : "") + ByteFormatter.Format(usage.StoredBytes);
        public string DedupSaved => ByteFormatter.Format(usage.DedupSavedBytes);
        public string CompressionSaved => ByteFormatter.Format(usage.CompressionSavedBytes);
        public string Reclaimable => usage.ReclaimableBytes > 0 ? ByteFormatter.Format(usage.ReclaimableBytes) : "-";
    }

    /// <summary>
    /// Dashboard panel: logical vs stored size, dedup and compression savings, a growth sparkline, the per-folder
    /// table and the "retention would free" estimate. Numbers come from local state (no remote calls) until the
    /// user asks "Refresh from storage". The maths is <see cref="StorageUsageCalculator"/> (unit-tested in Core).
    /// </summary>
    public partial class StorageUsagePanel : UserControl
    {
        private StorageUsageReport? _report;
        private bool _loading;
        private RepositoryBusyBinding? _busyBinding;
        private bool _wasBusy;

        public StorageUsagePanel()
        {
            InitializeComponent();
            RefreshBtn.Click += async (_, _) => await ErrorReporter.GuardAsync("Refreshing storage usage", RefreshAsync);
            MeasureBtn.Click += async (_, _) => await ErrorReporter.GuardAsync("Measuring storage", MeasureAsync);
            PreviewBtn.Click += async (_, _) => await ErrorReporter.GuardAsync("Previewing retention", PreviewRetentionAsync);
            SettingsBtn.Click += (_, _) => AppNavigator.Open(NotificationTarget.Settings);
        }

        private async void Control_Loaded(object sender, RoutedEventArgs e)
        {
            // An operation finishing while the Dashboard is open changes the numbers; unbind again when unloaded.
            _busyBinding ??= new RepositoryBusyBinding(Dispatcher, OnBusyChanged);
            Unloaded -= Control_Unloaded;
            Unloaded += Control_Unloaded;
            await ErrorReporter.GuardAsync("Loading storage usage", RefreshAsync);
        }

        private void Control_Unloaded(object sender, RoutedEventArgs e)
        {
            _busyBinding?.Dispose();
            _busyBinding = null;
        }

        private async void OnBusyChanged(bool busy, string? reason)
        {
            var finished = _wasBusy && !busy;
            _wasBusy = busy;
            if (finished && IsLoaded)
            {
                await ErrorReporter.GuardAsync("Refreshing storage usage", RefreshAsync);
            }
        }

        /// <summary>Recomputes from local state. Safe to call often; overlapping calls are ignored.</summary>
        public async Task RefreshAsync()
        {
            if (_loading)
            {
                return;
            }

            _loading = true;
            try
            {
                var config = await AppServices.GetConfigManagerAsync();
                var state = await AppServices.GetSystemStateAsync();
                var retention = config.Retention;
                var storage = config.GlobalStorageType;
                _report = await Task.Run(() => StorageUsageCalculator.ComputeAsync(state, retention, storage, DateTime.UtcNow));
                Render(_report);
            }
            finally
            {
                _loading = false;
            }
        }

        private void Render(StorageUsageReport report)
        {
            var empty = report.TotalSnapshots == 0;
            EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            UsageContent.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            MeasureBtn.IsEnabled = !empty;
            if (empty)
            {
                return;
            }

            var approx = report.HasEstimates ? "~" : "";
            StoredText.Text = approx + ByteFormatter.Format(report.TotalStoredBytes);
            LogicalText.Text = ByteFormatter.Format(report.TotalLatestLogicalBytes);
            DedupText.Text = ByteFormatter.Format(report.TotalDedupSavedBytes);
            CompressionText.Text = ByteFormatter.Format(report.TotalCompressionSavedBytes);
            SnapshotsText.Text = report.TotalSnapshots.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var notes = new List<string> { "Sizes are worked out from local history (average chunk size per snapshot), so they are close but not exact. \"Refresh from storage\" measures the real size." };
            if (report.HasEstimates)
            {
                notes.Add("Marked ~: some snapshots were made before chunk sizes were recorded, so their size is estimated from the original file size.");
            }

            if (report.SnapshotsWithoutChunkList > 0)
            {
                notes.Add($"{report.SnapshotsWithoutChunkList} snapshot(s) have no local chunk list and are left out of the stored size.");
            }

            EstimateText.Text = string.Join(" ", notes);

            ReclaimText.Text = report.TotalReclaimableSnapshots == 0
                ? "Retention would not delete anything right now."
                : $"Retention would delete {report.TotalReclaimableSnapshots} snapshot(s) and free about {ByteFormatter.Format(report.TotalReclaimableBytes)}"
                    + (report.RetentionEnabled ? "." : " (retention is currently switched off).");

            GroupList.ItemsSource = report.Groups.Select(g => new StorageUsageRow(g)).ToList();
            DrawGrowth();
        }

        private void GrowthCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawGrowth();

        private void DrawGrowth()
        {
            var growth = _report?.Growth ?? [];
            GrowthLine.Points.Clear();
            var width = GrowthCanvas.ActualWidth;
            var height = GrowthCanvas.ActualHeight;
            if (growth.Count == 0 || width <= 0 || height <= 0)
            {
                GrowthCaption.Text = growth.Count == 0 ? "No history yet." : string.Empty;
                return;
            }

            var max = Math.Max(1, growth.Max(p => p.CumulativeStoredBytes));
            var first = growth[0].TimestampUtc;
            var span = (growth[^1].TimestampUtc - first).TotalSeconds;
            for (var i = 0; i < growth.Count; i++)
            {
                var x = growth.Count == 1 ? width / 2 : span > 0
                    ? (growth[i].TimestampUtc - first).TotalSeconds / span * width
                    : i * width / (growth.Count - 1);
                var y = height - (growth[i].CumulativeStoredBytes / (double)max * (height - 4)) - 2;
                GrowthLine.Points.Add(new Point(x, y));
            }

            if (growth.Count == 1)
            {
                // A single point draws nothing as a line; make it a short flat segment.
                GrowthLine.Points.Add(new Point(width / 2 + 1, GrowthLine.Points[0].Y));
            }

            GrowthCaption.Text = $"{DisplayTime.Format(first)} to {DisplayTime.Format(growth[^1].TimestampUtc)}: "
                + $"{ByteFormatter.Format(growth[0].CumulativeStoredBytes)} to {ByteFormatter.Format(growth[^1].CumulativeStoredBytes)} "
                + $"across {growth.Count} snapshot(s). History older than the oldest kept snapshot is not shown.";
        }

        private async Task PreviewRetentionAsync()
        {
            var config = await AppServices.GetConfigManagerAsync();
            var state = await AppServices.GetSystemStateAsync();
            var plans = new RetentionManager(AppServices.Logger, config, state).PreviewPlans(config.Retention);

            var text = new StringBuilder();
            var total = 0;
            foreach (var (group, plan) in plans)
            {
                text.AppendLine(group);
                foreach (var d in plan.Decisions)
                {
                    text.AppendLine($"  {(d.Keep ? "keep  " : "DELETE")} [{d.StorageType}] {DisplayTime.Format(d.Backup.Timestamp)}  ({string.Join("; ", d.Reasons)})");
                }

                total += plan.ToDelete.Count;
            }

            text.Insert(0, plans.Count == 0
                ? "No backups yet.\r\n"
                : $"{total} backup(s) would be deleted with the saved retention settings{(config.Retention.Enabled ? "" : " (retention is currently switched off)")}. Nothing has been changed.\r\n\r\n");
            PreviewBox.Text = text.ToString();
            PreviewBox.Visibility = Visibility.Visible;
        }

        /// <summary>Lists <c>chunks/</c> in each storage the history uses (read lease) for the real stored size.</summary>
        private async Task MeasureAsync()
        {
            if (_report == null || _report.Groups.Count == 0)
            {
                return;
            }

            var config = await AppServices.GetConfigManagerAsync();
            var storageTypes = _report.Groups.Select(g => g.StorageType).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var measured = new List<(string Storage, MeasuredStorage? Result, string? Error)>();

            var window = new OperationProgressWindow("Measuring storage", AppServices.Logger);
            window.RunModal(Window.GetWindow(this), async token =>
            {
                foreach (var storageType in storageTypes)
                {
                    token.ThrowIfCancellationRequested();
                    window.StatusProgress.Report($"Listing chunks in '{storageType}'...");
                    try
                    {
                        await using var lease = await RepositoryCoordinator.Instance.AcquireReadAsync("Measuring storage usage", token);
                        using var storage = await config.CreateStorageAsync(storageType);
                        measured.Add((storageType, await StorageUsageCalculator.MeasureAsync(storage, token), null));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        measured.Add((storageType, null, ex.Message));
                    }
                }
            });

            if (measured.Count == 0 || window.WasCancelled)
            {
                return;
            }

            MeasuredText.Text = "Measured in storage (every chunk object, including any not referenced by a snapshot): "
                + string.Join("; ", measured.Select(m => m.Result is { } r
                    ? $"{m.Storage} {ByteFormatter.Format(r.Bytes)} in {r.ChunkCount:N0} chunk(s)"
                    : $"{m.Storage} could not be listed ({m.Error})"));
            MeasuredText.Visibility = Visibility.Visible;
        }
    }
}
