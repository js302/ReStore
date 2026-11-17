using System.ComponentModel;
using System.Windows;
using ReStore.Core.src.utils;

namespace ReStore.Views.Windows
{
    /// <summary>One row of the restore preview.</summary>
    public sealed class RestorePreviewRow(PlannedAction action) : INotifyPropertyChanged
    {
        private bool _isSelected = action.IsChange;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id => action.Id;
        public string Name => action.Name;
        public string Scope => action.Scope;
        public bool CanSelect => action.IsChange;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value || !CanSelect)
                {
                    return;
                }

                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public string ChangeLabel => action.Change switch
        {
            PlannedChange.Add => "Add",
            PlannedChange.Change => "Change",
            PlannedChange.Unchanged => "Unchanged",
            PlannedChange.Install => "Install",
            PlannedChange.AlreadyInstalled => "Installed",
            PlannedChange.Unavailable => "Manual",
            PlannedChange.Import => "Import",
            PlannedChange.Create => "Create",
            PlannedChange.Replace => "Replace",
            _ => "Skipped"
        };

        public string ValueSummary => action.Change switch
        {
            PlannedChange.Add => $"(not set) → {Shorten(action.NewValue)}",
            PlannedChange.Change => $"{Shorten(action.OldValue)} → {Shorten(action.NewValue)}",
            PlannedChange.Import => action.Keys.Count > 0 ? string.Join("; ", action.Keys) : action.Detail,
            PlannedChange.Create or PlannedChange.Replace => action.Detail,
            PlannedChange.Install => string.IsNullOrEmpty(action.NewValue) ? action.Detail : $"{action.Detail} (v{action.NewValue})",
            _ => action.Detail
        };

        public string Notes => action.NeedsAdmin ? "Needs admin" : action.IsChange ? "" : action.Detail;

        private static string Shorten(string? value)
            => string.IsNullOrEmpty(value) ? "" : value.Length <= 80 ? value : value[..80] + "...";
    }

    public partial class RestorePreviewWindow : Window
    {
        private readonly List<RestorePreviewRow> _rows;

        public RestorePreviewWindow(SystemRestorePlan plan)
        {
            InitializeComponent();
            // Environment variables first, then settings, then programs: the order they are applied in.
            _rows = [.. plan.Actions.OrderBy(a => a.Component switch { "environment" => 0, "settings" => 1, _ => 2 }).Select(a => new RestorePreviewRow(a))];
            foreach (var row in _rows)
            {
                row.PropertyChanged += (_, __) => UpdateCounts();
            }

            PreviewGrid.ItemsSource = _rows;
            SummaryText.Text = $"Restore {plan.Component} backup: {plan.ChangeCount} change(s), {plan.UnchangedCount} already match";
            UpdateCounts();
        }

        /// <summary>Ids of the checked rows (valid after the dialog returned true).</summary>
        public IReadOnlyCollection<string> SelectedIds => [.. _rows.Where(r => r.CanSelect && r.IsSelected).Select(r => r.Id)];

        private void UpdateCounts()
        {
            var selected = _rows.Count(r => r.CanSelect && r.IsSelected);
            var total = _rows.Count(r => r.CanSelect);
            var admin = _rows.Count(r => r.CanSelect && r.IsSelected && r.Notes == "Needs admin");
            CountText.Text = $"{selected} of {total} change(s) selected" + (admin > 0 ? $", {admin} need administrator rights" : "");
            RestoreBtn.IsEnabled = selected > 0;
        }

        private void SelectAllBtn_Click(object sender, RoutedEventArgs e) => SetAll(true);

        private void SelectNoneBtn_Click(object sender, RoutedEventArgs e) => SetAll(false);

        private void SetAll(bool value)
        {
            foreach (var row in _rows)
            {
                row.IsSelected = value;
            }
        }

        private void RestoreBtn_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
