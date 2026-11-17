using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;
using ReStore.Services;
using ReStore.Views.Windows;

namespace ReStore.Views.Pages
{
    public class BackupItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private static readonly Brush _recentBrush = new SolidColorBrush(Color.FromRgb(4, 120, 87));
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
        public bool Encrypted { get; set; }
        public string? Label { get; set; }

        public string LabelDisplay => string.IsNullOrWhiteSpace(Label) ? string.Empty : Label;
        public Visibility LabelVisibility => string.IsNullOrWhiteSpace(Label) ? Visibility.Collapsed : Visibility.Visible;

        public string TypeLabel => CanVerify
            ? (Encrypted ? "Snapshot (encrypted)" : "Snapshot")
            : IsDiff ? "Differential" : "Full";
        public string SizeLabel => SizeBytes == 0 ? "Unknown" : ByteFormatter.Format(SizeBytes);
        public string TimestampLabel => DisplayTime.Format(Timestamp);
        public string TimestampTooltip => DisplayTime.Tooltip(Timestamp) + " (UTC)";
        public string StatusText => DateTime.UtcNow - Timestamp.ToUniversalTime() < TimeSpan.FromDays(7) ? "Recent" : "Archived";
        public Brush StatusColor => DateTime.UtcNow - Timestamp.ToUniversalTime() < TimeSpan.FromDays(7) ? _recentBrush : _archivedBrush;
        public bool CanVerify => IsSnapshotArtifactPath(Path);
        public bool CanRestore => IsSnapshotArtifactPath(Path);
        private bool _locationExists;
        private string? _displayPath;

        /// <summary>
        /// Whether the artifact exists on a local disk. Probed off the UI thread after the list is shown
        /// (see <see cref="NeedsLocationProbe"/>); false until then, so the button simply appears later.
        /// </summary>
        public bool CanOpenLocation => _locationExists;
        public Visibility OpenLocationVisibility => _locationExists ? Visibility.Visible : Visibility.Collapsed;

        public string? ResolvedStoragePath { get; private set; }

        /// <summary>Where the artifact lives; resolved once by <see cref="ResolvePaths"/>, never from a binding.</summary>
        public string DisplayPath => _displayPath ?? Path;

        public bool NeedsLocationProbe => ResolvedStoragePath != null
            && (IsLocalStorageType(StorageType) || System.IO.Path.IsPathRooted(Path));

        /// <summary>Computes <see cref="ResolvedStoragePath"/> and <see cref="DisplayPath"/> (no file-system access).</summary>
        public void ResolvePaths()
        {
            ResolvedStoragePath = TryResolveStoragePath(Path, StorageBasePath, out var resolvedPath) ? resolvedPath : null;
            _displayPath = IsLocalStorageType(StorageType) && ResolvedStoragePath != null ? ResolvedStoragePath : Path;
        }

        public void SetLocationExists(bool exists)
        {
            if (_locationExists == exists)
            {
                return;
            }

            _locationExists = exists;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanOpenLocation)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OpenLocationVisibility)));
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

            try
            {
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
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException or System.Security.SecurityException)
            {
                // A malformed stored path must not break the row template.
                resolvedPath = string.Empty;
                return false;
            }
        }
    }

    public partial class BackupsPage : Page
    {
        private readonly Logger _logger = AppServices.Logger;
        private ConfigManager _configManager = null!;
        private SystemState? _state;
        private ObservableCollection<BackupItem> _items = [];
        private ICollectionView? _view;
        private List<BackupItem> _visible = [];
        private long _visibleSize;
        private string _storageBreakdown = string.Empty;
        private readonly HashSet<BackupItem> _selected = [];
        private bool _suppressSelectionEvents;
        private int _loadGeneration;
        private readonly DispatcherTimer _searchDebounce;
        private bool _initialized;
        private RepositoryBusyBinding? _busyBinding;
        private bool _repositoryBusy;
        private bool _mutatingOperationInProgress;

        private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);

        public BackupsPage()
        {
            InitializeComponent();

            // Typing restarts the timer, so the list is filtered once the user pauses, not per keystroke.
            _searchDebounce = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = SearchDebounce };
            _searchDebounce.Tick += (_, __) =>
            {
                _searchDebounce.Stop();
                ApplyFilters();
            };

            Unloaded += (_, __) =>
            {
                _busyBinding?.Dispose();
                _busyBinding = null;
                _searchDebounce.Stop();
            };

            BackupsList.ItemsSource = _items;
            _view = CollectionViewSource.GetDefaultView(_items);

            SearchBox.TextChanged += (_, __) =>
            {
                _searchDebounce.Stop();
                _searchDebounce.Start();
            };
            FilterTypeCombo.SelectionChanged += (_, __) => ApplyFilters();
            FilterStorageCombo.SelectionChanged += (_, __) => ApplyFilters();
            SortCombo.SelectionChanged += (_, __) => ApplyFilters();
            RefreshBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Load backups", () => LoadBackupsAsync());

            RestoreSelectedBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Restore selected", () => RestoreSelectedAsync());
            DeleteSelectedBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Delete selected", () => DeleteSelectedAsync());
            SelectAllBtn.Click += (_, __) => SelectAll();
            DeselectAllBtn.Click += (_, __) => DeselectAll();
        }

        private async Task InitializeAsync()
        {
            try
            {
                _configManager = await AppServices.GetConfigManagerAsync();
                _state = await AppServices.GetSystemStateAsync();

                await LoadBackupsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Loading the page", () => Page_LoadedCoreAsync(sender, e));
        }

        private async Task Page_LoadedCoreAsync(object sender, RoutedEventArgs e)
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

            if (!_initialized)
            {
                _initialized = true;
                await InitializeAsync();
            }
            else
            {
                // The page is cached across navigation, so show what changed while it was away.
                await LoadBackupsAsync();
            }

            _busyBinding ??= new RepositoryBusyBinding(Dispatcher, ApplyBusyState);
        }

        /// <summary>Delete is unavailable while another operation holds the repository.</summary>
        private void ApplyBusyState(bool busy, string? reason)
        {
            _repositoryBusy = busy;
            UpdateSelectionButtons();
            DeleteSelectedBtn.ToolTip = busy ? $"{RepositoryBusyBinding.BusyMessage} {reason}".Trim() : null;
        }

        /// <summary>
        /// Row buttons cannot be disabled individually, so a mutating handler calls this first. It also
        /// blocks a second click on the same row (or batch) while the first delete is still running (G2).
        /// </summary>
        private bool TryBeginMutatingOperation()
        {
            if (_mutatingOperationInProgress || RepositoryCoordinator.Instance.IsBusy)
            {
                var reason = RepositoryCoordinator.Instance.CurrentReason;
                MessageBox.Show(
                    $"{RepositoryBusyBinding.BusyMessage}{(reason == null ? string.Empty : $"\n\n{reason}")}\n\nTry again when it has finished.",
                    "ReStore",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return false;
            }

            _mutatingOperationInProgress = true;
            return true;
        }

        private async Task LoadBackupsAsync()
        {
            if (_state == null) return;

            var generation = ++_loadGeneration;
            var configManager = _configManager;

            // Everything that touches the disk or walks the whole history happens off the UI thread.
            var items = await Task.Run(() =>
            {
                var list = new List<BackupItem>();

                foreach (var kvp in _state.GetBackupHistorySnapshot())
                {
                    if (IsSystemBackupGroup(kvp.Key))
                    {
                        continue;
                    }

                    var directory = kvp.Key;
                    foreach (var backup in kvp.Value)
                    {
                        var storageType = string.IsNullOrWhiteSpace(backup.StorageType)
                            ? configManager.GlobalStorageType
                            : backup.StorageType;

                        var item = new BackupItem
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
                            Encrypted = backup.Encrypted,
                            Label = backup.Label,
                            IsSelected = false
                        };
                        item.ResolvePaths();
                        list.Add(item);
                    }
                }

                return list;
            });

            if (generation != _loadGeneration)
            {
                return; // a newer load superseded this one
            }

            foreach (var backup in _items)
            {
                backup.PropertyChanged -= BackupItem_PropertyChanged;
            }

            foreach (var backup in items)
            {
                backup.PropertyChanged += BackupItem_PropertyChanged;
            }

            _selected.Clear();
            _items = new ObservableCollection<BackupItem>(items);
            BackupsList.ItemsSource = _items;
            _view = CollectionViewSource.GetDefaultView(_items);

            RefreshStorageFilterOptions();
            ApplyFilters();

            _ = ProbeLocationsAsync(items, generation);
        }

        /// <summary>
        /// Checks, in the background, which local artifacts exist so the "open location" button can show.
        /// Results are applied in batches on the UI thread; a slow or offline disk delays buttons, not the page.
        /// </summary>
        private async Task ProbeLocationsAsync(List<BackupItem> items, int generation)
        {
            const int batchSize = 100;

            try
            {
                await Task.Run(async () =>
                {
                    var batch = new List<BackupItem>(batchSize);

                    foreach (var item in items)
                    {
                        if (generation != _loadGeneration)
                        {
                            return;
                        }

                        if (!item.NeedsLocationProbe)
                        {
                            continue;
                        }

                        var path = item.ResolvedStoragePath!;
                        bool exists;
                        try
                        {
                            exists = File.Exists(path) || System.IO.Directory.Exists(path);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            _logger.Log($"Could not check '{path}': {ex.Message}", LogLevel.Debug);
                            exists = false;
                        }

                        if (exists)
                        {
                            batch.Add(item);
                        }

                        if (batch.Count >= batchSize)
                        {
                            await ApplyProbedAsync(batch, generation);
                            batch = new List<BackupItem>(batchSize);
                        }
                    }

                    await ApplyProbedAsync(batch, generation);
                });
            }
            catch (Exception ex)
            {
                _logger.Log($"Could not check backup locations: {ex.Message}", LogLevel.Warning);
            }
        }

        private async Task ApplyProbedAsync(List<BackupItem> existing, int generation)
        {
            if (existing.Count == 0)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (generation != _loadGeneration)
                {
                    return;
                }

                foreach (var item in existing)
                {
                    item.SetLocationExists(true);
                }
            });
        }

        private void BackupItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(BackupItem.IsSelected) || _suppressSelectionEvents || sender is not BackupItem item)
            {
                return;
            }

            if (item.IsSelected)
            {
                _selected.Add(item);
            }
            else
            {
                _selected.Remove(item);
            }

            UpdateStats();
            UpdateSelectionButtons();
        }

        /// <summary>
        /// Applies the search / type / provider filters and the sort through the collection view (one refresh,
        /// no per-item collection changes). Rows that drop out of view are deselected so "Selected" always
        /// means "selected and visible".
        /// </summary>
        private void ApplyFilters()
        {
            if (_view == null)
            {
                return;
            }

            var searchText = SearchBox.Text?.Trim();
            var filterTag = (FilterTypeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var storageTag = (FilterStorageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var sortTag = (SortCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var now = DateTime.UtcNow;

            bool Matches(BackupItem b)
            {
                if (!string.IsNullOrEmpty(searchText)
                    && !b.Directory.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                    && !b.Path.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var typeMatches = filterTag switch
                {
                    "encrypted" => b.Encrypted,
                    "plain" => !b.Encrypted,
                    "recent" => now - b.Timestamp.ToUniversalTime() <= TimeSpan.FromDays(7),
                    "old" => now - b.Timestamp.ToUniversalTime() > TimeSpan.FromDays(30),
                    _ => true
                };
                if (!typeMatches)
                {
                    return false;
                }

                return storageTag is null or "all"
                    || string.Equals(b.StorageType, storageTag, StringComparison.OrdinalIgnoreCase);
            }

            using (_view.DeferRefresh())
            {
                _view.Filter = o => o is BackupItem b && Matches(b);
                _view.SortDescriptions.Clear();
                _view.SortDescriptions.Add(sortTag switch
                {
                    "oldest" => new SortDescription(nameof(BackupItem.Timestamp), ListSortDirection.Ascending),
                    "dir_asc" => new SortDescription(nameof(BackupItem.Directory), ListSortDirection.Ascending),
                    "dir_desc" => new SortDescription(nameof(BackupItem.Directory), ListSortDirection.Descending),
                    "size_desc" => new SortDescription(nameof(BackupItem.SizeBytes), ListSortDirection.Descending),
                    _ => new SortDescription(nameof(BackupItem.Timestamp), ListSortDirection.Descending)
                });
            }

            _visible = [.. _view.Cast<BackupItem>()];
            _visibleSize = _visible.Sum(b => b.SizeBytes);

            if (_selected.Count > 0)
            {
                var visibleSet = new HashSet<BackupItem>(_visible);
                SetSelection(_selected.Where(b => !visibleSet.Contains(b)).ToList(), selected: false);
            }

            _storageBreakdown = BuildStorageBreakdown();
            UpdateStats();
            UpdateSelectionButtons();
        }

        /// <summary>Rebuilds the provider dropdown from what history actually contains.</summary>
        private void RefreshStorageFilterOptions()
        {
            var previousTag = (FilterStorageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";

            FilterStorageCombo.Items.Clear();
            FilterStorageCombo.Items.Add(new ComboBoxItem { Content = "All Providers", Tag = "all" });

            foreach (var storageType in _items
                .Select(backup => backup.StorageType)
                .Where(storageType => !string.IsNullOrWhiteSpace(storageType))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(storageType => storageType, StringComparer.OrdinalIgnoreCase))
            {
                FilterStorageCombo.Items.Add(new ComboBoxItem { Content = storageType, Tag = storageType });
            }

            var restored = FilterStorageCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), previousTag, StringComparison.OrdinalIgnoreCase));

            // Changing the selection re-applies the filters; the caller applies them again, which is cheap.
            FilterStorageCombo.SelectedItem = restored ?? FilterStorageCombo.Items[0];
        }

        /// <summary>O(1): the visible total and size are cached by <see cref="ApplyFilters"/>; only the selected count changes here.</summary>
        private void UpdateStats()
        {
            var total = _visible.Count;
            var selected = _selected.Count;

            if (selected > 0)
            {
                StatsText.Text = $"{selected} of {total} selected • {ByteFormatter.Format(_visibleSize)} total";
            }
            else
            {
                StatsText.Text = $"{total} backups • {ByteFormatter.Format(_visibleSize)} total";
            }

            StorageBreakdownText.Text = _storageBreakdown;
        }

        private string BuildStorageBreakdown()
        {
            if (_visible.Count == 0)
            {
                return string.Empty;
            }

            var breakdown = _visible
                .GroupBy(backup => string.IsNullOrWhiteSpace(backup.StorageType) ? "unknown" : backup.StorageType,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => (group.Key, Count: group.Count(), Size: group.Sum(backup => backup.SizeBytes)))
                .OrderByDescending(group => group.Size)
                .Select(group => $"{group.Key}: {group.Count} • {ByteFormatter.Format(group.Size)}");

            return "By provider — " + string.Join("   |   ", breakdown);
        }

        private void UpdateSelectionButtons()
        {
            var hasSelection = _selected.Count > 0;
            RestoreSelectedBtn.IsEnabled = hasSelection;
            DeleteSelectedBtn.IsEnabled = hasSelection && !_repositoryBusy;
        }

        /// <summary>Changes many rows' selection with a single stats/buttons update (not one per row).</summary>
        private void SetSelection(IReadOnlyCollection<BackupItem> items, bool selected)
        {
            _suppressSelectionEvents = true;
            try
            {
                foreach (var backup in items)
                {
                    backup.IsSelected = selected;
                    if (selected)
                    {
                        _selected.Add(backup);
                    }
                    else
                    {
                        _selected.Remove(backup);
                    }
                }
            }
            finally
            {
                _suppressSelectionEvents = false;
            }
        }

        private void SelectAll()
        {
            SetSelection(_visible, selected: true);
            UpdateStats();
            UpdateSelectionButtons();
        }

        private void DeselectAll()
        {
            SetSelection(_selected.ToList(), selected: false);
            UpdateStats();
            UpdateSelectionButtons();
        }

        private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Restore backup", () => RestoreBackup_ClickCoreAsync(sender, e));
        }

        private async Task RestoreBackup_ClickCoreAsync(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is BackupItem backup)
            {
                await RestoreSingleBackupAsync(backup);
            }
        }

        private async void BrowseBackup_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Browse backup", () => BrowseBackup_ClickCoreAsync(sender, e));
        }

        private Task BrowseBackup_ClickCoreAsync(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is BackupItem backup)
            {
                OpenSnapshotBrowser(backup);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Opens the snapshot browser on one backup. Its comparison choices are the other snapshots of the same
        /// directory on the same storage (manifests only; HEAD pointers are not separate snapshots).
        /// </summary>
        private void OpenSnapshotBrowser(BackupItem backup)
        {
            if (!backup.CanRestore)
            {
                return;
            }

            var others = _items
                .Where(item => !ReferenceEquals(item, backup)
                    && item.Group == backup.Group
                    && string.Equals(item.StorageType, backup.StorageType, StringComparison.OrdinalIgnoreCase)
                    && item.Path.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(item.Path, backup.Path, StringComparison.Ordinal))
                .OrderByDescending(item => item.Timestamp)
                .Select(item => new SnapshotChoice(item.Path, item.Timestamp, item.Label))
                .ToList();

            var host = new SnapshotBrowserHost(
                _logger,
                storageType => CreateStorageAsync(storageType),
                _state,
                () => SaveStateSafelyAsync("restore telemetry"));
            var source = new SnapshotBrowserSource(backup.Directory, backup.Group, backup.Path, backup.StorageType, backup.Timestamp, others);

            new SnapshotBrowserWindow(host, source) { Owner = Window.GetWindow(this) }.ShowDialog();
        }

        private async void VerifyBackup_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Verify backup", () => VerifyBackup_ClickCoreAsync(sender, e));
        }

        private async Task VerifyBackup_ClickCoreAsync(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is BackupItem backup)
            {
                await VerifySingleBackupAsync(backup);
            }
        }

        private async void OpenBackupLocation_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Open backup location", async () =>
            {
                if (sender is Button button && button.Tag is BackupItem backup)
                {
                    await OpenBackupLocationAsync(backup);
                }
            });
        }

        private enum RestoreStepResult { Completed, Cancelled, Failed }

        /// <summary>
        /// Restores one backup. With <paramref name="batchTargetPath"/> (restore-selected) the destination is
        /// already chosen and confirmed, so there is no folder picker, preview or result dialog: the caller
        /// reports one summary at the end.
        /// </summary>
        private async Task<(RestoreStepResult Result, string? Error)> RestoreSingleBackupAsync(BackupItem backup, string? batchTargetPath = null)
        {
            var isBatch = batchTargetPath != null;

            try
            {
                if (!backup.CanRestore)
                {
                    if (isBatch)
                    {
                        return (RestoreStepResult.Failed, "Restore is only available for snapshot manifest or HEAD artifacts.");
                    }

                    MessageBox.Show(
                        "Restore is only available for snapshot manifest or HEAD artifacts on this page.",
                        "Restore Not Available",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return (RestoreStepResult.Cancelled, null);
                }

                var targetPath = batchTargetPath;
                if (targetPath == null)
                {
                    var dialog = new OpenFolderDialog
                    {
                        Title = "Select restore destination folder",
                        Multiselect = false
                    };

                    if (dialog.ShowDialog() != true)
                    {
                        return (RestoreStepResult.Cancelled, null);
                    }

                    targetPath = dialog.FolderName;
                    if (string.IsNullOrEmpty(targetPath)) return (RestoreStepResult.Cancelled, null);
                }

                var passwordProvider = App.GlobalPasswordProvider ?? new GuiPasswordProvider();

                RestoreConfirmWindow? confirm = null;
                if (!isBatch)
                {
                    // Preview first: the user sees exactly which existing files are at risk
                    // before anything is written.
                    RestorePreview preview;
                    using (var previewStorage = await CreateStorageForBackupAsync(backup))
                    {
                        var previewRestore = new Restore(_logger, previewStorage, passwordProvider, _state);
                        preview = await previewRestore.PreviewRestoreAsync(backup.Path, targetPath);
                    }

                    confirm = new RestoreConfirmWindow(preview) { Owner = Window.GetWindow(this) };
                    if (confirm.ShowDialog() != true || !confirm.Confirmed)
                    {
                        return (RestoreStepResult.Cancelled, null);
                    }
                }

                var progressWindow = new OperationProgressWindow($"Restoring {backup.Directory}", _logger);
                var restoreTarget = targetPath;
                var backupStorageType = backup.StorageType;
                // Read the confirm dialog here: the operation below runs on a pool thread.
                var restoreOptions = confirm == null
                    ? RestoreOptions.Default
                    : new RestoreOptions
                    {
                        ConflictPolicy = confirm.ConflictPolicy,
                        RelativePaths = confirm.SelectedRelativePaths
                    };
                var backupPath = backup.Path;
                progressWindow.RunModal(Window.GetWindow(this), async token =>
                {
                    using var storage = await CreateStorageAsync(backupStorageType);
                    var restore = new Restore(progressWindow, storage, passwordProvider, _state);

                    var outcome = await restore.RestoreFromBackupAsync(
                        backupPath,
                        restoreTarget,
                        restoreOptions,
                        progressWindow.RestoreProgress,
                        token);

                    progressWindow.Log(
                        $"Restored {outcome.FilesRestored} file(s); skipped {outcome.FilesSkipped}, kept both {outcome.FilesKeptBoth}, overwrote {outcome.FilesOverwritten}.",
                        LogLevel.Info);
                });

                await SaveStateSafelyAsync("restore telemetry");

                if (progressWindow.Succeeded)
                {
                    if (!isBatch)
                    {
                        MessageBox.Show("Restore completed successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return (RestoreStepResult.Completed, null);
                }

                if (progressWindow.Failure != null)
                {
                    if (!isBatch)
                    {
                        MessageBox.Show($"Restore failed: {progressWindow.Failure.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    return (RestoreStepResult.Failed, progressWindow.Failure.Message);
                }

                return (RestoreStepResult.Cancelled, null);
            }
            catch (Exception ex)
            {
                await SaveStateSafelyAsync("restore telemetry");
                if (!isBatch)
                {
                    MessageBox.Show($"Restore failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return (RestoreStepResult.Failed, ex.Message);
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

            var samplePercent = Math.Clamp(_configManager.Verification.SamplePercent, 1, 100);
            var choice = VerifyOptionsWindow.Ask(
                Window.GetWindow(this),
                $"How should this snapshot be checked?\n\n{backup.Directory}\n{backup.TimestampLabel}",
                samplePercent);

            if (choice == null)
            {
                return;
            }

            if (choice == VerifyChoice.TestRestore)
            {
                await TestRestoreRunner.RunAsync(
                    Window.GetWindow(this),
                    _logger,
                    backup.Directory,
                    backup.Path,
                    () => CreateStorageAsync(backup.StorageType),
                    _state);
                return;
            }

            var verifyOptions = new VerificationOptions(
                choice switch
                {
                    VerifyChoice.Quick => VerificationMode.Quick,
                    VerifyChoice.Sample => VerificationMode.Sample,
                    _ => VerificationMode.Full
                },
                samplePercent);

            try
            {
                var passwordProvider = App.GlobalPasswordProvider ?? new GuiPasswordProvider();

                SnapshotVerificationResult? verificationResult = null;
                var verifyPath = backup.Path;
                var backupStorageType = backup.StorageType;

                var progressWindow = new OperationProgressWindow($"Verifying {backup.Directory}", _logger);
                progressWindow.RunModal(Window.GetWindow(this), async token =>
                {
                    using var storage = await CreateStorageAsync(backupStorageType);
                    var verifier = new SnapshotIntegrityVerifier(progressWindow, storage, passwordProvider, _state);
                    verificationResult = await verifier.VerifyAsync(verifyPath, progressWindow.VerificationProgress, token, verifyOptions);
                });

                await SaveStateSafelyAsync("verification telemetry");

                if (progressWindow.WasCancelled)
                {
                    return;
                }

                if (progressWindow.Failure != null)
                {
                    MessageBox.Show($"Verification failed: {progressWindow.Failure.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (verificationResult == null)
                {
                    return;
                }

                if (verificationResult.IsValid)
                {
                    MessageBox.Show(
                        $"Verification passed ({VerificationModes.Name(verificationResult.Mode)}).\n\nSnapshot: {verificationResult.SnapshotId}\nFiles: {verificationResult.FileCount}\nUnique chunks: {verificationResult.UniqueChunks}"
                            + (verificationResult.Mode == VerificationMode.Sample ? $"\nChunks fully verified: {verificationResult.ChunksSampled} (seed {verificationResult.SampleSeed})" : string.Empty)
                            + (verificationResult.Mode == VerificationMode.Full ? string.Empty : "\n\nThis was not a full verification: run a Full check to prove every file can be rebuilt."),
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

        private async void LabelBackup_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Label backup", () => LabelBackup_ClickCoreAsync(sender, e));
        }

        private async Task LabelBackup_ClickCoreAsync(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not BackupItem backup)
            {
                return;
            }

            var dialog = new TextInputWindow(
                "Label restore point",
                $"Name this restore point so it is easy to find later.\n\n{backup.Directory} — {backup.TimestampLabel}",
                backup.Label,
                "For example: 'before Windows reinstall'. Leave empty to remove the label.")
            {
                Owner = Window.GetWindow(this)
            };

            if (dialog.ShowDialog() != true || _state == null)
            {
                return;
            }

            var label = string.IsNullOrWhiteSpace(dialog.Value) ? null : dialog.Value.Trim();

            try
            {
                var found = await RepositoryCoordinator.Instance.MutateStateAsync(
                    _state,
                    "Labelling a backup",
                    () => _state.SetBackupLabel(backup.Group, backup.Path, label));

                if (!found)
                {
                    MessageBox.Show("Could not find this backup in the history.", "Label", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"Failed to persist backup label: {ex.Message}", LogLevel.Error);
                MessageBox.Show($"Could not save the label: {ex.Message}", "Label", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await LoadBackupsAsync();
        }

        private async Task RestoreSelectedAsync()
        {
            var selected = _visible.Where(b => _selected.Contains(b)).ToList();
            if (selected.Count == 0) return;

            if (selected.Count == 1)
            {
                await RestoreSingleBackupAsync(selected[0]);
                return;
            }

            var confirm = MessageBox.Show(
                $"Restore {selected.Count} backup(s)?\n\nYou will choose one destination folder. Each backup is restored into its own subfolder (<directory>\\<date-time>) so nothing already there is overwritten.",
                "Confirm Restore",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            var dialog = new OpenFolderDialog
            {
                Title = $"Select the folder to restore {selected.Count} backups into",
                Multiselect = false
            };
            if (dialog.ShowDialog() != true || string.IsNullOrEmpty(dialog.FolderName))
            {
                return;
            }

            var summary = new BatchOperationSummary();
            var usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cancelled = false;

            foreach (var backup in selected)
            {
                var folder = BatchOperationSummary.RestoreFolderName(backup.Directory, backup.Timestamp, usedFolders);
                var (result, error) = await RestoreSingleBackupAsync(backup, Path.Combine(dialog.FolderName, folder));

                if (result == RestoreStepResult.Completed)
                {
                    summary.AddSuccess();
                }
                else if (result == RestoreStepResult.Failed)
                {
                    summary.AddFailure($"{backup.Directory} ({backup.TimestampLabel})", error ?? "Unknown error.");
                }
                else
                {
                    cancelled = true;
                    break; // the user cancelled: do not start the remaining restores
                }
            }

            var text = summary.Format("Restored") + (cancelled ? "\n\nCancelled; the remaining backups were not restored." : string.Empty);
            MessageBox.Show(
                text,
                "Restore",
                MessageBoxButton.OK,
                summary.HasFailures || cancelled ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        private async void DeleteBackup_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Delete backup", () => DeleteBackup_ClickCoreAsync(sender, e));
        }

        private async Task DeleteBackup_ClickCoreAsync(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is BackupItem backup)
            {
                await DeleteSingleBackupAsync(backup);
            }
        }

        private async Task DeleteSingleBackupAsync(BackupItem backup)
        {
            if (!TryBeginMutatingOperation())
            {
                return;
            }

            try
            {
                await DeleteSingleBackupCoreAsync(backup);
            }
            finally
            {
                _mutatingOperationInProgress = false;
            }
        }

        private async Task DeleteSingleBackupCoreAsync(BackupItem backup)
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
                    if (await DeleteBackupArtifactAsync(backup))
                    {
                        _items.Remove(backup);
                        _selected.Remove(backup);
                        ApplyFilters();

                        MessageBox.Show("Backup deleted successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        // Already gone from the history, or the storage delete failed (logged).
                        await LoadBackupsAsync();
                        MessageBox.Show(
                            "The backup could not be deleted, or it had already been removed. See the log for details.",
                            "Delete",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Delete failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async Task DeleteSelectedAsync()
        {
            if (!TryBeginMutatingOperation())
            {
                return;
            }

            try
            {
                await DeleteSelectedCoreAsync();
            }
            finally
            {
                _mutatingOperationInProgress = false;
            }
        }

        private async Task DeleteSelectedCoreAsync()
        {
            var selected = _visible.Where(b => _selected.Contains(b)).ToList();
            if (selected.Count == 0) return;

            var result = MessageBox.Show(
                $"Delete {selected.Count} backup(s)?\n\nThis action cannot be undone.",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            var summary = new BatchOperationSummary();

            foreach (var backup in selected)
            {
                var label = $"{backup.Directory} ({backup.TimestampLabel})";
                try
                {
                    // Each delete goes through RetentionManager (idempotent, serialised, saves state itself),
                    // so a crash mid-batch loses nothing already done and a repeat cannot double-decrement.
                    if (await DeleteBackupArtifactAsync(backup))
                    {
                        summary.AddSuccess();
                    }
                    else
                    {
                        summary.AddFailure(label, "Could not be deleted, or it had already been removed. See the log for details.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log($"Failed to delete backup '{backup.Path}': {ex.Message}", LogLevel.Error);
                    summary.AddFailure(label, ex.Message);
                }
            }

            // The history is the source of truth after a batch, whether or not every delete succeeded.
            await LoadBackupsAsync();

            MessageBox.Show(
                summary.Format("Deleted"),
                summary.HasFailures ? "Delete completed with errors" : "Success",
                MessageBoxButton.OK,
                summary.HasFailures ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        private void ShowDetails_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is BackupItem backup)
            {
                var details = $"Directory: {backup.Directory}\n\n" +
                             (string.IsNullOrWhiteSpace(backup.Label) ? "" : $"Label: {backup.Label}\n\n") +
                             $"Path: {backup.DisplayPath}\n\n" +
                             $"Timestamp: {backup.TimestampLabel}\n\n" +
                             $"Type: {backup.TypeLabel}\n\n" +
                             $"Size: {backup.SizeLabel}\n\n" +
                             $"Status: {backup.StatusText}";

                MessageBox.Show(details, "Backup Details", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async Task SaveStateSafelyAsync(string context)
        {
            if (_state == null)
            {
                return;
            }

            try
            {
                // Under the repository lease so a stale in-memory copy never overwrites another process's state.
                await RepositoryCoordinator.Instance.MutateStateAsync(_state, $"Saving {context}", () => true);
            }
            catch (Exception ex)
            {
                _logger.Log($"Failed to persist {context}: {ex.Message}", LogLevel.Error);
            }
        }

        private static bool IsSystemBackupGroup(string group)
        {
            return group.Equals("system_programs", StringComparison.OrdinalIgnoreCase)
                || group.Equals("system_environment", StringComparison.OrdinalIgnoreCase)
                || group.Equals("system_settings", StringComparison.OrdinalIgnoreCase)
                || group.Equals("system_devtools", StringComparison.OrdinalIgnoreCase);
        }

        private Task<IStorage> CreateStorageForBackupAsync(BackupItem backup) => CreateStorageAsync(backup.StorageType);

        /// <summary>Takes the storage type as a value so pool-thread callers read the item on the UI thread first.</summary>
        private async Task<IStorage> CreateStorageAsync(string? backupStorageType)
        {
            var storageType = string.IsNullOrWhiteSpace(backupStorageType)
                ? _configManager.GlobalStorageType
                : backupStorageType;

            return await _configManager.CreateStorageAsync(storageType);
        }

        /// <summary>
        /// Deletes through the core retention API, which serialises with every other operation and is
        /// idempotent (chunk reference counts are decremented once however often this is invoked).
        /// </summary>
        private async Task<bool> DeleteBackupArtifactAsync(BackupItem backup)
        {
            if (_state == null)
            {
                return false;
            }

            var retention = new RetentionManager(_logger, _configManager, _state, null, App.GlobalPasswordProvider);
            // Off the UI thread; the item's values are read here, before hopping.
            var group = backup.Group;
            var path = backup.Path;
            return await Task.Run(() => retention.DeleteBackupAsync(group, path));
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

        private static async Task OpenBackupLocationAsync(BackupItem backup)
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
                // One probe on click, off the UI thread (the path may be on a slow or offline disk).
                var isDirectory = await Task.Run(() => System.IO.Directory.Exists(resolvedPath));

                var arguments = isDirectory
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
