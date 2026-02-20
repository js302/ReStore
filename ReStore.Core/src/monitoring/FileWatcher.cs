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
    private readonly FileSelectionService _fileSelectionService;
    private readonly SemaphoreSlim _backupExecutionLock = new(1, 1);
    private Timer? _backupTimer;
    private readonly TimeSpan _bufferTime = TimeSpan.FromSeconds(10);
    private bool _isDisposed = false;

    public FileWatcher(IConfigManager configManager, ILogger logger, SystemState systemState, SizeAnalyzer sizeAnalyzer, IPasswordProvider? passwordProvider = null)
    {
        _configManager = configManager;
        _logger = logger;
        _systemState = systemState;
        _sizeAnalyzer = sizeAnalyzer;
        _passwordProvider = passwordProvider;
        _backup = new Backup(_logger, _systemState, _sizeAnalyzer, _configManager, _passwordProvider);
        _fileSelectionService = new FileSelectionService(_logger, _configManager);
    }

    public Task StartAsync()
    {
        _logger.Log("Starting file watcher service...");
        foreach (var watchConfig in _configManager.WatchDirectories)
        {
            var dir = watchConfig.Path;
            if (Directory.Exists(dir))
            {
                var watcher = new FileSystemWatcher(dir)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
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
        if (_isDisposed) return;

        if (_fileSelectionService.ShouldExcludeFile(e.FullPath))
        {
            return;
        }

        _logger.Log($"File change detected ({e.ChangeType}): {e.FullPath}", LogLevel.Debug);
        _changedFiles.AddOrUpdate(e.FullPath, DateTime.UtcNow, (key, oldValue) => DateTime.UtcNow);
        _backupTimer?.Change(_bufferTime, Timeout.InfiniteTimeSpan);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (_isDisposed) return;

        if (_fileSelectionService.ShouldExcludeFile(e.FullPath))
        {
            return;
        }

        _logger.Log($"File rename detected: {e.OldFullPath} -> {e.FullPath}", LogLevel.Debug);
        _changedFiles.AddOrUpdate(e.FullPath, DateTime.UtcNow, (key, oldValue) => DateTime.UtcNow);
        _backupTimer?.Change(_bufferTime, Timeout.InfiniteTimeSpan);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.Log($"File watcher error: {e.GetException().Message}", LogLevel.Error);
    }

    private void OnBackupTimer(object? state)
    {
        _ = ProcessBackupTimerAsync();
    }

    private async Task ProcessBackupTimerAsync()
    {
        if (_isDisposed) return;

        if (!await _backupExecutionLock.WaitAsync(0))
        {
            _logger.Log("Backup processing is already running; skipping overlapping timer tick.", LogLevel.Debug);
            return;
        }

        try
        {
            _backupTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            var pathsToBackup = new List<string>();
            foreach (var key in _changedFiles.Keys)
            {
                if (_changedFiles.TryRemove(key, out _))
                {
                    pathsToBackup.Add(key);
                }
            }

            if (!pathsToBackup.Any())
            {
                _logger.Log("Backup timer triggered, but no pending changes.", LogLevel.Debug);
                return;
            }

            _logger.Log($"Backup buffer time elapsed. Processing {pathsToBackup.Count} changes.", LogLevel.Info);

            var groupedFiles = pathsToBackup
                .Select(path => new { Path = path, Root = FindWatchedRoot(path) })
                .Where(x => x.Root != null)
                .GroupBy(x => x.Root!)
                .ToList();

            foreach (var group in groupedFiles)
            {
                var rootDirectory = group.Key;
                var filesInGroup = group.Select(x => x.Path).ToList();

                _logger.Log($"Initiating backup for {filesInGroup.Count} changed files under {rootDirectory}", LogLevel.Info);
                try
                {
                    await _backup.BackupFilesAsync(filesInGroup, rootDirectory);
                }
                catch (Exception ex)
                {
                    _logger.Log($"Error during scheduled backup for {rootDirectory}: {ex.Message}", LogLevel.Error);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Unhandled error in backup timer: {ex.Message}", LogLevel.Error);
        }
        finally
        {
            _backupExecutionLock.Release();
        }
    }

    private string? FindWatchedRoot(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);

        return _configManager.WatchDirectories
            .Select(wc => Path.GetFullPath(wc.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Where(watchedDir => IsPathWithinRoot(normalizedPath, watchedDir))
            .OrderByDescending(watchedDir => watchedDir.Length)
            .FirstOrDefault();
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
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
            _backupTimer?.Dispose();
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _watchers.Clear();
            _changedFiles.Clear();
            _backupExecutionLock.Dispose();
            _logger.Log("FileWatcher resources disposed.", LogLevel.Debug);
        }

        _isDisposed = true;
    }
}