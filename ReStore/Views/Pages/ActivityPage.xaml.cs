using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;
using ReStore.Services;

namespace ReStore.Views.Pages
{
    /// <summary>One row of the Activity list.</summary>
    public sealed class ActivityRow(ActivityItem item)
    {
        public ActivityItem Item { get; } = item;
        public string TimeLabel => DisplayTime.Format(Item.EndUtc);
        public string TimeTooltip => DisplayTime.Tooltip(Item.EndUtc) + " (UTC)";

        public string TypeLabel => Item.Type switch
        {
            ActivityType.SystemBackup => "System backup",
            ActivityType.SystemRestore => "System restore",
            var other => other.ToString()
        };

        public string ResultLabel => Item.Result.ToString();
        public string Target => Item.Target;
        public string Message => Item.Message;
    }

    /// <summary>Recent operations (backup runs and the bounded activity log), filterable, with log access.</summary>
    public partial class ActivityPage : Page
    {
        private readonly ObservableCollection<ActivityRow> _rows = [];
        private bool _wired;
        private RepositoryBusyBinding? _busyBinding;
        private bool _wasBusy;

        public ActivityPage()
        {
            InitializeComponent();

            ActivityList.ItemsSource = _rows;
            ActivityList.SelectionChanged += (_, _) => CopyBtn.IsEnabled = ActivityList.SelectedItem != null;
            TypeFilter.SelectionChanged += async (_, _) => await ErrorReporter.GuardAsync("Filtering activity", RefreshAsync);
            ResultFilter.SelectionChanged += async (_, _) => await ErrorReporter.GuardAsync("Filtering activity", RefreshAsync);
            RefreshBtn.Click += async (_, _) => await ErrorReporter.GuardAsync("Refreshing activity", RefreshAsync);
            CopyBtn.Click += (_, _) => CopySelected();
            OpenLogBtn.Click += (_, _) => OpenLog();
            _wired = true;
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            _busyBinding?.Dispose();
            _busyBinding = null;
        }

        private async void OnBusyChanged(bool busy, string? reason)
        {
            // An operation just finished: show it without waiting for a manual refresh.
            var finished = _wasBusy && !busy;
            _wasBusy = busy;
            if (finished && IsLoaded)
            {
                await ErrorReporter.GuardAsync("Refreshing activity", RefreshAsync);
            }
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            _busyBinding ??= new RepositoryBusyBinding(Dispatcher, OnBusyChanged);
            await ErrorReporter.GuardAsync("Loading activity", RefreshAsync);
        }

        private async Task RefreshAsync()
        {
            if (!_wired)
            {
                return;
            }

            var state = await AppServices.GetSystemStateAsync();
            var type = (TypeFilter.SelectedItem as ComboBoxItem)?.Tag is string t && Enum.TryParse<ActivityType>(t, out var parsedType) ? parsedType : (ActivityType?)null;
            var result = (ResultFilter.SelectedItem as ComboBoxItem)?.Tag is string r && Enum.TryParse<ActivityResult>(r, out var parsedResult) ? parsedResult : (ActivityResult?)null;

            var selected = (ActivityList.SelectedItem as ActivityRow)?.Item;
            var items = ActivityTimeline.Build(state, type, result);
            _rows.Clear();
            foreach (var item in items)
            {
                _rows.Add(new ActivityRow(item));
            }

            if (selected != null)
            {
                ActivityList.SelectedItem = _rows.FirstOrDefault(row => row.Item == selected);
            }

            EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = _rows.Count == 0 ? string.Empty : $"{_rows.Count} operation(s)";
        }

        private void CopySelected()
        {
            if (ActivityList.SelectedItem is not ActivityRow row)
            {
                return;
            }

            try
            {
                Clipboard.SetText(row.Item.ToDetailsText());
                StatusText.Text = "Details copied.";
            }
            catch (Exception)
            {
                // The clipboard can be locked by another process.
                StatusText.Text = "Could not copy: the clipboard is in use.";
            }
        }

        private void OpenLog()
        {
            var logFile = AppPaths.LogFile;
            var info = File.Exists(logFile)
                ? new ProcessStartInfo("explorer.exe", $"/select,\"{logFile}\"") { UseShellExecute = true }
                : Directory.Exists(AppPaths.LogsDirectory)
                    ? new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogsDirectory}\"") { UseShellExecute = true }
                    : null;

            if (info == null)
            {
                StatusText.Text = "There is no log yet.";
                return;
            }

            Process.Start(info);
        }
    }
}
