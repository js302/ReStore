using ReStore.src.utils;
using ReStore.src.core;
using ReStore.src.storage;
using System.Collections.Concurrent;

namespace ReStore.src.monitoring;

public class FileWatcher : IDisposable
{
    private readonly IConfigManager _configManager;
    private readonly ILogger _logger;
    private readonly SystemState _systemState;
    private readonly IStorage _storage;
    private readonly SizeAnalyzer _sizeAnalyzer;
    private readonly CompressionUtil _compressionUtil;
    private readonly Backup _backup;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, DateTime> _changedFiles = new();
    private Timer? _backupTimer;
    private readonly TimeSpan _bufferTime = TimeSpan.FromSeconds(10);
    private bool _isDisposed = false;

    public FileWatcher(IConfigManager configManager, ILogger logger, SystemState systemState, IStorage storage, SizeAnalyzer sizeAnalyzer, CompressionUtil compressionUtil)
    {
        _configManager = configManager;
        _logger = logger;
        _systemState = systemState;
        _storage = storage;
        _sizeAnalyzer = sizeAnalyzer;
        _compressionUtil = compressionUtil;
        _backup = new Backup(_logger, _systemState, _sizeAnalyzer, _storage, _configManager);
    }

    public Task StartAsync()
    {
        _logger.Log("Starting file watcher service...");
        foreach (var dir in _configManager.WatchDirectories)
        {
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
                _logger.Log($"Watching directory: {dir}");
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
        _logger.Log($"File change detected ({e.ChangeType}): {e.FullPath}", LogLevel.Debug);
        _changedFiles.AddOrUpdate(e.FullPath, DateTime.UtcNow, (key, oldValue) => DateTime.UtcNow);
        _backupTimer?.Change(_bufferTime, Timeout.InfiniteTimeSpan);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        _logger.Log($"File rename detected: {e.OldFullPath} -> {e.FullPath}", LogLevel.Debug);
        _changedFiles.AddOrUpdate(e.FullPath, DateTime.UtcNow, (key, oldValue) => DateTime.UtcNow);
        _backupTimer?.Change(_bufferTime, Timeout.InfiniteTimeSpan);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.Log($"File watcher error: {e.GetException().Message}", LogLevel.Error);
    }

    private async void OnBackupTimer(object? state)
    {
        _backupTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        var pathsToBackup = _changedFiles.Keys.ToList();
        _changedFiles.Clear();

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

    private string? FindWatchedRoot(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);

        return _configManager.WatchDirectories
            .Select(Path.GetFullPath)
            .Where(watchedDir => normalizedPath.StartsWith(watchedDir, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(watchedDir => watchedDir.Length)
            .FirstOrDefault();
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
            _logger.Log("FileWatcher resources disposed.", LogLevel.Debug);
        }

        _isDisposed = true;
    }
}