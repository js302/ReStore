using ReStore.Core.src.utils;
using ReStore.Core.src.core;
using System.Collections.Concurrent;

namespace ReStore.Core.src.monitoring;

public class FileWatcher : IDisposable
{
    private readonly IConfigManager _configManager;
    private readonly ILogger _logger;
    private readonly SystemState _systemState;
    private readonly SizeAnalyzer _sizeAnalyzer;
    private readonly Backup _backup;
    private readonly IPasswordProvider? _passwordProvider;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, DateTime> _changedFiles = new();
    private readonly ConcurrentDictionary<string, string> _renamedFiles = new(StringComparer.OrdinalIgnoreCase);

    // Watched roots that need a full directory backup because events were lost (buffer overflow / watcher error)
    // or cannot be expressed as file changes (directory rename).
    private readonly ConcurrentDictionary<string, byte> _fullBackupRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileSelectionService _fileSelectionService;
    private readonly SemaphoreSlim _backupExecutionLock = new(1, 1);

    // Cancelled on Dispose so a watcher-triggered backup stops promptly on shutdown rather
    // than running on against a disposed watcher.
    private readonly CancellationTokenSource _shutdown = new();

    private Timer? _backupTimer;
    private readonly TimeSpan _bufferTime = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait before retrying changes that were put back because the repository was busy.</summary>
    internal TimeSpan BusyRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>First retry delay after a failed batch; doubles per consecutive failing sweep up to <see cref="MaxFailureRetryDelay"/>.</summary>
    internal TimeSpan FailureRetryDelay { get; set; } = TimeSpan.FromMinutes(1);
    internal TimeSpan MaxFailureRetryDelay { get; set; } = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan DefaultMaxDebounceWait = TimeSpan.FromMinutes(5);

    // Starts when the first change of a burst arrives; bounds how long the debounce may keep restarting.
    private readonly object _timerLock = new();
    private DateTime? _debounceStartUtc;

    // Set when a change arrives or a tick is skipped while a sweep runs; the sweep then re-arms the timer.
    private volatile bool _dirtyDuringSweep;
    private int _failureStreak;

    // Not-before time for the next sweep while a retry delay (busy / failure / min snapshot interval) is
    // pending: new events must not turn a long backoff into a 10 s retry loop.
    private DateTime? _notBeforeUtc;

    /// <summary>The delay the backup timer was last armed with (test observability).</summary>
    internal TimeSpan? LastArmedDelay { get; private set; }
    private bool _isDisposed = false;
    private volatile bool _stopping;

    // When the last watcher snapshot of each watched root finished; drives watcher.minSnapshotInterval.
    // Only touched inside the _backupExecutionLock section.
    private readonly Dictionary<string, DateTime> _lastSnapshotUtc = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Clock for the minimum snapshot interval; replaceable in tests.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    // When set, the pause file is read before every sweep (see BackupScheduler): while paused, changes stay queued.
    private readonly ProtectionPauseStore? _pauseStore;

    /// <summary>How long to wait before checking again whether a pause has ended.</summary>
    internal TimeSpan PausedRecheckDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Time zone of the backup window (local time); replaceable in tests.</summary>
    internal TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Local;

    /// <summary>Longest wait before checking again whether the backup window has opened.</summary>
    internal TimeSpan WindowRecheckDelay { get; set; } = TimeSpan.FromMinutes(5);

    private string? _windowErrorLogged;

    public FileWatcher(IConfigManager configManager, ILogger logger, SystemState systemState, SizeAnalyzer sizeAnalyzer, IPasswordProvider? passwordProvider = null, RepositoryCoordinator? coordinator = null, ProtectionPauseStore? pauseStore = null)
    {
        _pauseStore = pauseStore;
        _configManager = configManager;
        _logger = logger;
        _systemState = systemState;
        _sizeAnalyzer = sizeAnalyzer;
        _passwordProvider = passwordProvider;
        _backup = new Backup(_logger, _systemState, _sizeAnalyzer, _configManager, _passwordProvider, coordinator) { FailFastWhenBusy = true };
        _fileSelectionService = new FileSelectionService(_logger, _configManager);
    }

    public Task StartAsync()
    {
        _logger.Log("Starting file watcher service...");
        foreach (var watchConfig in _configManager.WatchDirectories)
        {
            var dir = watchConfig.ExpandedPath;
            if (Directory.Exists(dir))
            {
                var watcher = new FileSystemWatcher(dir)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    // The default (8 KB) overflows under bursts, silently losing events; 64 KB is the maximum.
                    InternalBufferSize = 64 * 1024,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };

                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;

                _watchers.Add(watcher);
                var storageInfo = watchConfig.StorageType != null ? $" (using {watchConfig.StorageType} storage)" : " (using global storage)";
                _logger.Log($"Watching directory: {dir}{storageInfo}");
            }
            else
            {
                _logger.Log($"Directory not found, cannot watch: {dir}", LogLevel.Warning);
            }
        }

        _backupTimer = new Timer(OnBackupTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _logger.Log("File watcher service started.");
        return Task.CompletedTask;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (_isDisposed || _stopping) return;

        // A cloud-only placeholder changes without any local data to back up.
        if (_fileSelectionService.ShouldExcludeFile(e.FullPath) || _fileSelectionService.IsCloudOnlyFile(e.FullPath))
        {
            return;
        }

        if (e.ChangeType != WatcherChangeTypes.Deleted && Directory.Exists(e.FullPath))
        {
            // A directory copied or moved in from outside the root raises only its own Created event, none
            // for the files inside it: back up the whole root. Other directory events (its timestamp changes
            // whenever something inside does) carry no information; the files raise their own events.
            if (e.ChangeType == WatcherChangeTypes.Created && FindWatchedRoot(e.FullPath) is { } createdRoot)
            {
                _logger.Log($"Directory created: {e.FullPath}; scheduling a full backup of {createdRoot}", LogLevel.Debug);
                RequestFullBackup(createdRoot);
            }

            return;
        }

        _logger.Log($"File change detected ({e.ChangeType}): {e.FullPath}", LogLevel.Debug);
        _changedFiles.AddOrUpdate(e.FullPath, DateTime.UtcNow, (key, oldValue) => DateTime.UtcNow);
        ArmDebounce();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (_isDisposed || _stopping) return;

        // Only for paths that will actually be backed up: an excluded rename's entry would
        // never be consumed and would leak for the process's lifetime.
        if (_fileSelectionService.ShouldExcludeFile(e.FullPath) || _fileSelectionService.IsCloudOnlyFile(e.FullPath))
        {
            return;
        }

        // A renamed directory moves its files without raising events for them, so the old and new
        // locations can only be reconciled by backing up the whole watched root.
        if (Directory.Exists(e.FullPath))
        {
            if (FindWatchedRoot(e.FullPath) is { } directoryRoot)
            {
                _logger.Log($"Directory rename detected: {e.OldFullPath} -> {e.FullPath}; scheduling a full backup of {directoryRoot}", LogLevel.Debug);
                RequestFullBackup(directoryRoot);
            }

            return;
        }

        _renamedFiles[e.FullPath] = e.OldFullPath;

        _logger.Log($"File rename detected: {e.OldFullPath} -> {e.FullPath}", LogLevel.Debug);
        _changedFiles.AddOrUpdate(e.FullPath, DateTime.UtcNow, (key, oldValue) => DateTime.UtcNow);
        ArmDebounce();
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.Log($"File watcher error: {e.GetException().Message}", LogLevel.Error);

        if (_isDisposed || _stopping) return;

        // Events were lost (buffer overflow, or the watcher broke): which files changed is unknown, so
        // back up the whole root the failing watcher covers (every root if it cannot be identified).
        var failedRoot = sender is FileSystemWatcher { Path.Length: > 0 } source ? FindWatchedRoot(source.Path) : null;
        if (failedRoot != null)
        {
            RequestFullBackup(failedRoot);
            return;
        }

        foreach (var watchConfig in _configManager.WatchDirectories)
        {
            if (!string.IsNullOrWhiteSpace(watchConfig.Path) && FindWatchedRoot(watchConfig.ExpandedPath) is { } root)
            {
                RequestFullBackup(root);
            }
        }
    }

    private void RequestFullBackup(string root)
    {
        _fullBackupRoots[root] = 0;
        ArmDebounce();
    }

    /// <summary>
    /// Delay until the next sweep: the debounce window, shortened so that the first pending change is never
    /// postponed past <c>watcher.maxDebounceWait</c> by a steady stream of events.
    /// </summary>
    private TimeSpan ComputeDebounceDelay()
    {
        lock (_timerLock)
        {
            var now = UtcNow();
            _debounceStartUtc ??= now;

            var delay = _bufferTime;
            var maxWait = _configManager.Watcher?.MaxDebounceWait ?? DefaultMaxDebounceWait;
            if (maxWait > TimeSpan.Zero)
            {
                var left = _debounceStartUtc.Value + maxWait - now;
                if (left < delay)
                {
                    delay = left > TimeSpan.Zero ? left : TimeSpan.Zero;
                }
            }

            if (_notBeforeUtc is { } notBefore && notBefore - now > delay)
            {
                delay = notBefore - now;
            }

            return delay;
        }
    }

    private void ArmDebounce()
    {
        if (_isDisposed || _stopping) return;

        // Tells a sweep that is running right now to schedule another one when it finishes.
        _dirtyDuringSweep = true;
        ArmTimer(ComputeDebounceDelay());
    }

    private void ArmTimer(TimeSpan delay)
    {
        try
        {
            _backupTimer?.Change(delay, Timeout.InfiniteTimeSpan);
            LastArmedDelay = delay;
        }
        catch (ObjectDisposedException)
        {
            // StopAsync/Dispose raced with this call; unprocessed changes stay queued for TakePendingChanges.
        }
    }

    private void OnBackupTimer(object? state)
    {
        _ = ProcessBackupTimerAsync();
    }

    private sealed class Batch(string root)
    {
        public string Root { get; } = root;
        public List<(string Path, string? Previous)> Items { get; } = [];
        public bool Full { get; set; }
    }

    private void Requeue(Batch batch)
    {
        foreach (var (path, previousPath) in batch.Items)
        {
            _changedFiles.AddOrUpdate(path, DateTime.UtcNow, (key, oldValue) => DateTime.UtcNow);
            if (previousPath != null)
            {
                _renamedFiles[path] = previousPath;
            }
        }

        if (batch.Full)
        {
            _fullBackupRoots[batch.Root] = 0;
        }
    }

    private async Task ProcessBackupTimerAsync()
    {
        if (_isDisposed || _stopping || _shutdown.IsCancellationRequested) return;

        if (_pauseStore?.LoadActive(UtcNow()) != null)
        {
            // Nothing is drained: the queued changes are backed up by the first sweep after the pause ends.
            _logger.Log("Backup of changes postponed: protection is paused.", LogLevel.Debug);
            ArmTimer(PausedRecheckDelay);
            return;
        }

        if (BackupWindowDelay() is { } windowDelay)
        {
            // Outside the backup window nothing is drained either: the changes stay queued and are backed up once it opens.
            // The floor keeps a steady stream of events from re-arming a ten second timer that would only defer again.
            lock (_timerLock)
            {
                _notBeforeUtc = UtcNow() + windowDelay;
            }

            ArmTimer(windowDelay);
            return;
        }

        if (!await _backupExecutionLock.WaitAsync(0).ConfigureAwait(false))
        {
            // The running sweep re-arms when it finishes (flag); arm now as well so the tick is not lost
            // if that sweep is already past its re-arm check.
            _dirtyDuringSweep = true;
            _logger.Log("Backup processing is already running; skipping overlapping timer tick.", LogLevel.Debug);
            if (!_isDisposed && !_stopping)
            {
                ArmTimer(ComputeDebounceDelay());
            }

            return;
        }

        List<Batch> unprocessed = [];
        var drainedPaths = new List<string>();
        var drainedRoots = new List<string>();
        var batchesBuilt = false;
        TimeSpan? rearm = null;
        try
        {
            try
            {
                _backupTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Stopping.
            }

            _dirtyDuringSweep = false;
            lock (_timerLock)
            {
                _debounceStartUtc = null;
            }

            var pathsToBackup = drainedPaths;
            foreach (var key in _changedFiles.Keys)
            {
                if (_changedFiles.TryRemove(key, out _))
                {
                    pathsToBackup.Add(key);
                }
            }

            var fullRoots = drainedRoots;
            foreach (var key in _fullBackupRoots.Keys)
            {
                if (_fullBackupRoots.TryRemove(key, out _))
                {
                    fullRoots.Add(key);
                }
            }

            if (pathsToBackup.Count == 0 && fullRoots.Count == 0)
            {
                _logger.Log("Backup timer triggered, but no pending changes.", LogLevel.Debug);
                return;
            }

            _logger.Log($"Backup buffer time elapsed. Processing {pathsToBackup.Count} changes{(fullRoots.Count > 0 ? $" and {fullRoots.Count} full folder backup(s)" : string.Empty)}.", LogLevel.Info);

            // Changes taken off the queue stay in `unprocessed` until handled, so a cancelled or crashed
            // sweep puts them back instead of dropping them.
            var batches = new Dictionary<string, Batch>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in pathsToBackup)
            {
                var previous = _renamedFiles.TryGetValue(path, out var previousPath) ? previousPath : null;
                var root = FindWatchedRoot(path);
                if (root == null)
                {
                    _logger.Log($"Ignoring change outside every watched folder: {path}", LogLevel.Warning);
                    continue;
                }

                if (!batches.TryGetValue(root, out var batch))
                {
                    batches[root] = batch = new Batch(root);
                }

                batch.Items.Add((path, previous));
            }

            foreach (var root in fullRoots)
            {
                if (!batches.TryGetValue(root, out var batch))
                {
                    batches[root] = batch = new Batch(root);
                }

                batch.Full = true;
            }

            unprocessed.AddRange(batches.Values);
            batchesBuilt = true;
            foreach (var path in pathsToBackup)
            {
                _renamedFiles.TryRemove(path, out _);
            }

            var requeued = false;
            var failed = false;
            var succeeded = false;
            TimeSpan? nextDue = null;
            var minInterval = _configManager.Watcher?.MinSnapshotInterval ?? TimeSpan.Zero;

            foreach (var batch in batches.Values)
            {
                _shutdown.Token.ThrowIfCancellationRequested();

                var rootDirectory = batch.Root;

                // Too soon after this root's last snapshot: keep the changes (a later flush picks them up)
                // and wake up when the interval has elapsed, instead of writing a full snapshot per burst.
                if (minInterval > TimeSpan.Zero && _lastSnapshotUtc.TryGetValue(rootDirectory, out var lastSnapshot))
                {
                    var remaining = minInterval - (UtcNow() - lastSnapshot);
                    if (remaining > TimeSpan.Zero)
                    {
                        _logger.Log($"Deferring {batch.Items.Count} change(s) under {rootDirectory}: minimum snapshot interval {minInterval} not yet elapsed ({remaining.TotalSeconds:0}s left).", LogLevel.Debug);
                        Requeue(batch);
                        unprocessed.Remove(batch);
                        nextDue = nextDue is { } current && current < remaining ? current : remaining;
                        continue;
                    }
                }

                var filesInGroup = batch.Items.Select(x => x.Path).ToList();
                var previousPaths = batch.Items
                    .Select(x => x.Previous)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Cast<string>()
                    .ToList();

                try
                {
                    if (batch.Full)
                    {
                        _logger.Log($"Initiating full backup of {rootDirectory} (watcher lost events)", LogLevel.Info);
                        await _backup.BackupDirectoryAsync(ResolveWatchPath(rootDirectory), null, null, _shutdown.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        _logger.Log($"Initiating backup for {filesInGroup.Count} changed files under {rootDirectory}", LogLevel.Info);
                        await _backup.BackupFilesAsync(filesInGroup, rootDirectory, null, null, _shutdown.Token, previousPaths).ConfigureAwait(false);
                    }

                    _lastSnapshotUtc[rootDirectory] = UtcNow();
                    unprocessed.Remove(batch);
                    succeeded = true;
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    throw;
                }
                catch (RepositoryBusyException ex)
                {
                    // Another operation holds the repository. Put the changes back (never drop them)
                    // and retry later instead of blocking behind it.
                    _logger.Log($"Backup for {rootDirectory} deferred: {ex.Message}", LogLevel.Info);
                    Requeue(batch);
                    unprocessed.Remove(batch);
                    requeued = true;
                }
                catch (Exception ex)
                {
                    // Also put back: dropping a failed batch would leave the changes unbacked until the same
                    // files happened to change again (or the next scheduled sweep).
                    _logger.Log($"Error during scheduled backup for {rootDirectory}: {ex.Message}; will retry.", LogLevel.Error);
                    Requeue(batch);
                    unprocessed.Remove(batch);
                    failed = true;
                }
            }

            if (failed)
            {
                _failureStreak++;
            }
            else if (succeeded)
            {
                _failureStreak = 0;
            }

            void Consider(TimeSpan delay) => rearm = rearm is { } existing && existing < delay ? existing : delay;

            if (requeued) Consider(BusyRetryDelay);
            if (failed) Consider(FailureBackoff(_failureStreak));
            if (nextDue != null) Consider(nextDue.Value);
        }
        catch (OperationCanceledException)
        {
            // Keep the unfinished changes so a restart can carry them over instead of dropping them.
            foreach (var batch in unprocessed)
            {
                foreach (var (path, previous) in batch.Items)
                {
                    _changedFiles.TryAdd(path, DateTime.UtcNow);
                    if (previous != null)
                    {
                        _renamedFiles[path] = previous;
                    }
                }

                if (batch.Full)
                {
                    _fullBackupRoots[batch.Root] = 0;
                }
            }

            _logger.Log("Watcher-triggered backup cancelled during shutdown.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _logger.Log($"Unhandled error in backup timer: {ex.Message}", LogLevel.Error);
            if (!batchesBuilt)
            {
                foreach (var path in drainedPaths)
                {
                    _changedFiles.TryAdd(path, DateTime.UtcNow);
                }

                foreach (var root in drainedRoots)
                {
                    _fullBackupRoots[root] = 0;
                }
            }

            foreach (var batch in unprocessed)
            {
                Requeue(batch);
            }

            _failureStreak++;
            rearm = FailureBackoff(_failureStreak);
        }
        finally
        {
            try
            {
                if (!_isDisposed && !_stopping)
                {
                    // Changes that arrived (or a tick that was skipped) while this sweep ran must not wait
                    // behind a longer retry delay, nor be forgotten when nothing else re-arms the timer.
                    // A pending retry delay is a floor: events that arrived meanwhile wait it out too.
                    lock (_timerLock)
                    {
                        _notBeforeUtc = rearm is { } retry ? UtcNow() + retry : null;
                    }

                    if (rearm == null && _dirtyDuringSweep)
                    {
                        rearm = ComputeDebounceDelay();
                    }

                    if (rearm != null)
                    {
                        ArmTimer(rearm.Value);
                    }
                }
            }
            finally
            {
                _backupExecutionLock.Release();
            }
        }
    }

    /// <summary>
    /// How long to wait before looking again while the backup window is closed (at most <see cref="WindowRecheckDelay"/>, so a
    /// changed window or time zone is noticed); null when no window applies or it is open.
    /// </summary>
    private TimeSpan? BackupWindowDelay()
    {
        var window = BackupWindow.FromConfig(_configManager.BackupWindow, out var error);
        if (error != null && _windowErrorLogged != error)
        {
            _windowErrorLogged = error;
            _logger.Log($"Ignoring the invalid backupWindow ({error}); watcher backups are not restricted.", LogLevel.Warning);
        }

        var now = UtcNow();
        if (window == null || window.IsOpen(now, TimeZone))
        {
            return null;
        }

        var opensIn = window.NextOpenAtOrAfter(now, TimeZone) - now;
        _logger.Log($"Backup of changes postponed: outside the backup window ({window.Describe()}); it opens in {opensIn.TotalMinutes:0} minute(s).", LogLevel.Debug);
        var delay = opensIn < WindowRecheckDelay ? opensIn : WindowRecheckDelay;
        return delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1);
    }

    private TimeSpan FailureBackoff(int streak)
    {
        var delay = FailureRetryDelay;
        for (var i = 1; i < streak && delay < MaxFailureRetryDelay; i++)
        {
            delay += delay;
        }

        return delay < MaxFailureRetryDelay ? delay : MaxFailureRetryDelay;
    }

    private string ResolveWatchPath(string root)
    {
        foreach (var watchConfig in _configManager.WatchDirectories)
        {
            if (!string.IsNullOrWhiteSpace(watchConfig.Path) && string.Equals(NormalizeRoot(watchConfig.ExpandedPath), root, StringComparison.OrdinalIgnoreCase))
            {
                return watchConfig.ExpandedPath;
            }
        }

        return root;
    }

    private static string NormalizeRoot(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private string? FindWatchedRoot(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);

        return _configManager.WatchDirectories
            .Select(wc => NormalizeRoot(wc.ExpandedPath))
            .Where(watchedDir => IsPathWithinRoot(normalizedPath, watchedDir))
            .OrderByDescending(watchedDir => watchedDir.Length)
            .FirstOrDefault();
    }

    private bool IsWatchedRoot(string path)
        => FindWatchedRoot(path) is { } root && string.Equals(root, NormalizeRoot(path), StringComparison.OrdinalIgnoreCase);

    private static bool IsPathWithinRoot(string path, string root)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Stops raising events, cancels any in-flight backup and waits for it to unwind. Changes not yet
    /// backed up stay queued (see <see cref="TakePendingChanges"/>). Call <see cref="Dispose()"/> afterwards.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed || _stopping)
        {
            return;
        }

        _stopping = true;

        _backupTimer?.Dispose();
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();

        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }

        // The sweep holds this lock for its whole run, so acquiring it means it has finished.
        await _backupExecutionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        _backupExecutionLock.Release();
    }

    /// <summary>Removes and returns changes (path, previous path if renamed) that were seen but not backed up.</summary>
    public IReadOnlyDictionary<string, string?> TakePendingChanges()
    {
        var pending = new Dictionary<string, string?>();
        foreach (var key in _changedFiles.Keys)
        {
            if (_changedFiles.TryRemove(key, out _))
            {
                pending[key] = _renamedFiles.TryRemove(key, out var previous) ? previous : null;
            }
        }

        // A pending full-folder backup travels as the root path itself (a root is never a changed file).
        foreach (var root in _fullBackupRoots.Keys)
        {
            if (_fullBackupRoots.TryRemove(root, out _))
            {
                pending[root] = null;
            }
        }

        return pending;
    }

    /// <summary>Queues changes carried over from a previous watcher and schedules a backup for them.</summary>
    public void AdoptPendingChanges(IReadOnlyDictionary<string, string?> pending)
    {
        if (_isDisposed || _stopping || pending.Count == 0)
        {
            return;
        }

        foreach (var (path, previous) in pending)
        {
            if (previous == null && Directory.Exists(path))
            {
                // A carried-over full-backup request (a root path, never a changed file).
                if (IsWatchedRoot(path))
                {
                    _fullBackupRoots[NormalizeRoot(path)] = 0;
                }
                else
                {
                    _logger.Log($"Ignoring carried-over full backup request for a folder that is no longer watched: {path}", LogLevel.Warning);
                }

                continue;
            }

            _changedFiles.TryAdd(path, DateTime.UtcNow);
            if (previous != null)
            {
                _renamedFiles[path] = previous;
            }
        }

        ArmDebounce();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            _logger.Log("Disposing FileWatcher resources...", LogLevel.Debug);

            // Signal first, so an in-flight backup stops at its next checkpoint instead of
            // continuing against watchers that are about to go away.
            try
            {
                _shutdown.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down.
            }

            _backupTimer?.Dispose();
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _watchers.Clear();
            _changedFiles.Clear();
            _renamedFiles.Clear();
            _fullBackupRoots.Clear();

            // Not disposing _backupExecutionLock: an in-flight backup still holds it and
            // releases it in its finally, which would throw on a disposed semaphore.
            _shutdown.Dispose();
            _logger.Log("FileWatcher resources disposed.", LogLevel.Debug);
        }

        _isDisposed = true;
    }
}