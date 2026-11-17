using System.Windows;
using System.Windows.Controls;
using ReStore.Core.src.utils;
using ReStore.Services;

namespace ReStore.Views.Windows
{
    public partial class UndoPointsWindow : Window
    {
        private sealed record UndoRow(UndoPointInfo Point)
        {
            public string Title => $"{Point.Component} restore, {Point.CreatedUtc.ToLocalTime():g}";
            public string Summary => Point.Summary;
        }

        public UndoPointsWindow()
        {
            InitializeComponent();
            Reload();
        }

        private void Reload()
        {
            var rows = SystemRestoreUndo.List().Select(p => new UndoRow(p)).ToList();
            PointsList.ItemsSource = rows;
            EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void Undo_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Undo system restore", () => UndoCoreAsync(sender));
        }

        private async Task UndoCoreAsync(object sender)
        {
            if (sender is not Button { Tag: UndoRow row } || !OperatingSystem.IsWindows())
            {
                return;
            }

            var confirm = MessageBox.Show(
                $"Put back the values saved before the {row.Title} ({row.Summary})?\n\nMachine-wide items need administrator approval.\n\nNote: registry values the restore added to a settings key are not removed, and installed programs are not uninstalled.",
                "Undo System Restore", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            var logger = AppServices.Logger;
            var result = await Task.Run(() => new SystemRestoreUndo(SystemRestoreServices.CreateDefault(logger), logger).UndoAsync(row.Point.Path));
            var message = $"{result.Restored} item(s) restored, {result.Failed} failed.";
            if (result.Messages.Count > 0)
            {
                message += "\n\n" + string.Join("\n", result.Messages);
            }

            MessageBox.Show(message, "Undo System Restore", MessageBoxButton.OK, result.Failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Delete undo point", () =>
            {
                if (sender is Button { Tag: UndoRow row }
                    && MessageBox.Show($"Delete the undo point for the {row.Title}? It can no longer be undone afterwards.",
                        "Delete Undo Point", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    SystemRestoreUndo.Delete(row.Point.Path);
                    Reload();
                }

                return Task.CompletedTask;
            });
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
