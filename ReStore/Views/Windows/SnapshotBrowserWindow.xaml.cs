using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;
using ReStore.Services;

namespace ReStore.Views.Windows
{
    /// <summary>
    /// Browses one snapshot from its manifest alone: folder-by-folder listing (virtualised), search, multi-select,
    /// restore to a chosen folder or the original location, per-file version history, and comparison with another
    /// snapshot of the same backup. Every manifest read and restore runs off the UI thread.
    /// </summary>
    public partial class SnapshotBrowserWindow : Window
    {
        private const int MaxSearchResults = 2000;
        private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);

        private readonly SnapshotBrowserHost _host;
        private readonly SnapshotBrowserSource _source;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly DispatcherTimer _searchTimer;
        private SnapshotTree? _tree;
        private SnapshotTreeNode? _currentFolder;
        private int _searchGeneration;
        private bool _busy;

        public SnapshotBrowserWindow(SnapshotBrowserHost host, SnapshotBrowserSource source)
        {
            ArgumentNullException.ThrowIfNull(host);
            ArgumentNullException.ThrowIfNull(source);

            InitializeComponent();

            _host = host;
            _source = source;

            TitleText.Text = $"Browse - {source.Group}";
            Title = TitleText.Text;
            SummaryText.Text = $"{source.Group} - snapshot of {DisplayTime.Format(source.Timestamp)}";
            GroupText.Text = source.ManifestPath;

            // Typing restarts the timer, so the search runs once the user pauses, not per keystroke.
            _searchTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = SearchDebounce };
            _searchTimer.Tick += async (_, __) =>
            {
                _searchTimer.Stop();
                await ErrorReporter.GuardAsync("Search snapshot", ApplySearchAsync);
            };
            SearchBox.TextChanged += (_, __) =>
            {
                _searchTimer.Stop();
                _searchTimer.Start();
            };

            Loaded += async (_, __) => await ErrorReporter.GuardAsync("Open snapshot", LoadAsync);
        }

        private async Task LoadAsync()
        {
            SetBusy(true, "Loading snapshot...");
            try
            {
                var token = _cancellation.Token;
                var manifestPath = _source.ManifestPath;
                var storageType = _source.StorageType;
                _tree = await Task.Run(async () =>
                {
                    using var storage = await _host.CreateStorageAsync(storageType);
                    return await new SnapshotBrowser(_host.Logger, storage, null, App.GlobalPasswordProvider ?? new GuiPasswordProvider()).LoadTreeAsync(manifestPath, token);
                }, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                SetBusy(false, "Could not read this snapshot.");
                System.Windows.MessageBox.Show(this, $"Could not read the snapshot: {ex.Message}", "Browse", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }

            GroupText.Text = $"Original location: {_tree.Group}";
            SearchBox.IsEnabled = true;
            CompareBtn.IsEnabled = _source.OtherSnapshots.Count > 0;
            ShowFolder(_tree.Root);
            SetBusy(false, null);
            UpdateSelectionState();
        }

        private void SetBusy(bool busy, string? message)
        {
            _busy = busy;
            if (message != null)
            {
                StatusText.Text = message;
            }

            EntryList.IsEnabled = !busy;
            UpdateSelectionState();
        }

        private void ShowFolder(SnapshotTreeNode folder)
        {
            _currentFolder = folder;
            EntryList.ItemsSource = folder.Children.Select(node => new BrowserRow(node, showFullPath: false)).ToList();
            BreadcrumbText.Text = "/" + folder.RelativePath;
            UpdateUpButton();
            StatusText.Text = $"{folder.Children.Count} item(s) here - {folder.FileCount} file(s), {ByteFormatter.Format(folder.SizeBytes)} in total";
        }

        private void UpdateUpButton() => UpBtn.IsEnabled = _currentFolder is { RelativePath.Length: > 0 } || IsSearching;

        private bool IsSearching => !string.IsNullOrWhiteSpace(SearchBox.Text);

        private async Task ApplySearchAsync()
        {
            if (_tree == null)
            {
                return;
            }

            var query = SearchBox.Text;
            var generation = ++_searchGeneration;
            if (string.IsNullOrWhiteSpace(query))
            {
                ShowFolder(_currentFolder ?? _tree.Root);
                return;
            }

            var tree = _tree;
            var results = await Task.Run(() => tree.Search(query, MaxSearchResults));
            if (generation != _searchGeneration)
            {
                return; // a newer query superseded this one
            }

            EntryList.ItemsSource = results.Select(node => new BrowserRow(node, showFullPath: true)).ToList();
            BreadcrumbText.Text = $"Search: {query.Trim()}";
            UpBtn.IsEnabled = true;
            StatusText.Text = results.Count >= MaxSearchResults
                ? $"First {MaxSearchResults} matches shown - refine the search to narrow them."
                : $"{results.Count} match(es).";
        }

        private void UpBtn_Click(object sender, RoutedEventArgs e)
        {
            if (IsSearching)
            {
                // Leave the search, back to the folder that was open.
                SearchBox.Text = string.Empty;
                return;
            }

            if (_tree == null || _currentFolder == null || _currentFolder.RelativePath.Length == 0)
            {
                return;
            }

            var path = _currentFolder.RelativePath;
            var slash = path.LastIndexOf('/');
            ShowFolder(_tree.Find(slash < 0 ? string.Empty : path[..slash]) ?? _tree.Root);
        }

        private void EntryList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedFolder();

        private void EntryList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OpenSelectedFolder();
                e.Handled = true;
            }
            else if (e.Key == Key.Back)
            {
                UpBtn_Click(sender, e);
                e.Handled = true;
            }
        }

        private void OpenSelectedFolder()
        {
            if (_tree == null || EntryList.SelectedItems.Count != 1 || EntryList.SelectedItem is not BrowserRow { Node.IsDirectory: true } row)
            {
                return;
            }

            _searchTimer.Stop();
            _searchGeneration++;
            if (IsSearching)
            {
                SearchBox.Text = string.Empty;
                _searchTimer.Stop();
            }

            ShowFolder(row.Node);
        }

        private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionState();

        private List<SnapshotTreeNode> SelectedNodes() =>
            [.. EntryList.SelectedItems.OfType<BrowserRow>().Select(row => row.Node)];

        private void UpdateSelectionState()
        {
            var selected = SelectedNodes();
            var hasSelection = selected.Count > 0 && !_busy && _tree != null;
            RestoreToBtn.IsEnabled = hasSelection;
            RestoreOriginalBtn.IsEnabled = hasSelection && SnapshotBrowser.ResolveOriginalLocation(_source.Group, _tree!.Group) != null;
            VersionsBtn.IsEnabled = !_busy && _tree is { GroupKey: not null } && selected is [{ IsDirectory: false }];
            CompareBtn.IsEnabled = !_busy && _tree != null && _source.OtherSnapshots.Count > 0;

            if (selected.Count > 0)
            {
                var top = SnapshotTree.TopLevel(selected);
                StatusText.Text = $"{selected.Count} selected - {top.Sum(n => n.IsDirectory ? n.FileCount : 1)} file(s), {ByteFormatter.Format(top.Sum(n => n.SizeBytes))}";
            }
        }

        private async void RestoreToBtn_Click(object sender, RoutedEventArgs e) =>
            await ErrorReporter.GuardAsync("Restore selection", async () =>
            {
                var dialog = new OpenFolderDialog { Title = "Select restore destination folder", Multiselect = false };
                if (dialog.ShowDialog(this) != true || string.IsNullOrEmpty(dialog.FolderName))
                {
                    return;
                }

                await RestoreSelectionAsync(dialog.FolderName);
            });

        private async void RestoreOriginalBtn_Click(object sender, RoutedEventArgs e) =>
            await ErrorReporter.GuardAsync("Restore selection", async () =>
            {
                var original = _tree == null ? null : SnapshotBrowser.ResolveOriginalLocation(_source.Group, _tree.Group);
                if (original == null)
                {
                    System.Windows.MessageBox.Show(this, "The original location of this backup is not a usable folder path on this PC. Use \"Restore to...\" instead.", "Restore", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                await RestoreSelectionAsync(original);
            });

        private async Task RestoreSelectionAsync(string targetDirectory)
        {
            if (_tree == null)
            {
                return;
            }

            var selected = SelectedNodes();
            if (selected.Count == 0)
            {
                return;
            }

            SetBusy(true, "Collecting files...");
            HashSet<string> paths;
            try
            {
                paths = await Task.Run(() => SnapshotTree.CollectFilePaths(selected));
            }
            finally
            {
                SetBusy(false, null);
            }

            await _host.RestoreAsync(
                this,
                $"Restoring from {_source.Directory}",
                _tree.ManifestPath,
                _source.StorageType,
                targetDirectory,
                paths);
            UpdateSelectionState();
        }

        private async void VersionsBtn_Click(object sender, RoutedEventArgs e) =>
            await ErrorReporter.GuardAsync("Show versions", ShowVersionsAsync);

        private Task ShowVersionsAsync()
        {
            if (_tree?.GroupKey is not { } groupKey || SelectedNodes() is not [{ IsDirectory: false } file])
            {
                return Task.CompletedTask;
            }

            var relativePath = file.RelativePath;
            var storageType = _source.StorageType;
            FileVersionHistory? history = null;
            var progress = new OperationProgressWindow($"Reading the history of {file.Name}", _host.Logger);
            progress.RunModal(this, async token =>
            {
                using var storage = await _host.CreateStorageAsync(storageType);
                history = await new SnapshotBrowser(progress, storage, null, App.GlobalPasswordProvider ?? new GuiPasswordProvider()).GetFileVersionsAsync(groupKey, relativePath, token);
            });

            if (!progress.Succeeded || history == null)
            {
                if (progress.Failure != null)
                {
                    System.Windows.MessageBox.Show(this, $"Could not read the file history: {progress.Failure.Message}", "Versions", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                return Task.CompletedTask;
            }

            if (history.Versions.Count == 0)
            {
                System.Windows.MessageBox.Show(this, "No versions of this file were found.", "Versions", MessageBoxButton.OK, MessageBoxImage.Information);
                return Task.CompletedTask;
            }

            new FileVersionsWindow(_host, _source, SnapshotBrowser.ResolveOriginalLocation(_source.Group, _tree.Group), relativePath, history) { Owner = this }.ShowDialog();
            return Task.CompletedTask;
        }

        private void CompareBtn_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu
            {
                PlacementTarget = CompareBtn,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
                MaxHeight = 360
            };

            foreach (var choice in _source.OtherSnapshots)
            {
                var item = new MenuItem
                {
                    Header = string.IsNullOrWhiteSpace(choice.Label)
                        ? DisplayTime.Format(choice.Timestamp)
                        : $"{DisplayTime.Format(choice.Timestamp)}  ({choice.Label})",
                    Tag = choice
                };
                item.Click += async (_, __) => await ErrorReporter.GuardAsync("Compare snapshots", () => CompareWithAsync(choice));
                menu.Items.Add(item);
            }

            menu.IsOpen = true;
        }

        private Task CompareWithAsync(SnapshotChoice other)
        {
            if (_tree == null)
            {
                return Task.CompletedTask;
            }

            // The older snapshot is the baseline (A), so "added" always means "appeared later".
            var otherIsOlder = other.Timestamp <= _tree.CreatedUtc;
            var pathA = otherIsOlder ? other.ManifestPath : _tree.ManifestPath;
            var pathB = otherIsOlder ? _tree.ManifestPath : other.ManifestPath;
            var storageType = _source.StorageType;
            SnapshotDiff? diff = null;
            var progress = new OperationProgressWindow("Comparing snapshots", _host.Logger);
            progress.RunModal(this, async token =>
            {
                using var storage = await _host.CreateStorageAsync(storageType);
                diff = await new SnapshotBrowser(progress, storage, null, App.GlobalPasswordProvider ?? new GuiPasswordProvider()).DiffAsync(pathA, pathB, token);
            });

            if (!progress.Succeeded || diff == null)
            {
                if (progress.Failure != null)
                {
                    System.Windows.MessageBox.Show(this, $"Could not compare the snapshots: {progress.Failure.Message}", "Compare", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                return Task.CompletedTask;
            }

            new SnapshotDiffWindow(diff, _source.Group) { Owner = this }.ShowDialog();
            return Task.CompletedTask;
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosed(EventArgs e)
        {
            _searchTimer.Stop();
            _cancellation.Cancel();
            _cancellation.Dispose();
            base.OnClosed(e);
        }

        private sealed class BrowserRow(SnapshotTreeNode node, bool showFullPath)
        {
            public SnapshotTreeNode Node { get; } = node;

            public string Glyph => Node.IsDirectory ? "\uE8B7" : "\uE8A5";

            public string Name => showFullPath ? Node.RelativePath : Node.Name;

            public string RelativePath => Node.RelativePath;

            public string SizeLabel => Node.IsDirectory
                ? $"{Node.FileCount} file(s), {ByteFormatter.Format(Node.SizeBytes)}"
                : ByteFormatter.Format(Node.SizeBytes);

            public string ModifiedLabel => Node.LastModifiedUtc is { } time ? DisplayTime.Format(time) : string.Empty;
        }
    }
}
