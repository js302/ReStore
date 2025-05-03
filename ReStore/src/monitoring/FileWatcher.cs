using ReStore.src.utils;
using ReStore.src.core;
using ReStore.src.storage;
using System.Collections.Concurrent;

namespace ReStore.src.monitoring;

public class FileWatcher
{
    private readonly Dictionary<string, FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, DateTime> _changedPaths = new();
    private readonly Timer _backupTimer;
    private readonly ILogger _logger;
    private readonly IConfigManager _config;
    private readonly SystemState _state;
    private readonly IStorage _storage;
    private readonly SizeAnalyzer _sizeAnalyzer;
    private readonly CompressionUtil _compressionUtil;
    private readonly FileSelectionService _fileSelectionService;

    public FileWatcher(IConfigManager config, ILogger logger, SystemState state, IStorage storage,
                    SizeAnalyzer sizeAnalyzer, CompressionUtil compressionUtil)
    {
        _config = config;
        _logger = logger;
        _state = state;
        _storage = storage;
        _sizeAnalyzer = sizeAnalyzer;
        _compressionUtil = compressionUtil;
        _fileSelectionService = new FileSelectionService(logger, config);
        _backupTimer = new Timer(OnBackupTimer, null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task StartAsync()
    {
        foreach (var path in _config.WatchDirectories)
        {
            if (Directory.Exists(path))
            {
                AddWatcher(path);
                _logger.Log($"Added watcher for directory: {path}", LogLevel.Info);
            }
            else
            {
                _logger.Log($"Directory not found: {path}", LogLevel.Warning);
            }
        }

        // Start periodic backup timer
        _backupTimer.Change(TimeSpan.Zero, _config.BackupInterval);

        await Task.CompletedTask;
    }

    private void AddWatcher(string path)
    {
        var watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };

        watcher.Changed += OnFileChanged;
        watcher.Created += OnFileChanged;
        watcher.Deleted += OnFileChanged;
        watcher.Renamed += OnFileRenamed;

        _watchers[path] = watcher;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!_fileSelectionService.ShouldExcludeFile(e.FullPath))
        {
            _changedPaths[e.FullPath] = DateTime.UtcNow;
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (!_fileSelectionService.ShouldExcludeFile(e.FullPath))
        {
            // only track the new name, remove the old one
            _changedPaths.TryRemove(e.OldFullPath, out _);
            _changedPaths[e.FullPath] = DateTime.UtcNow;
        }
    }

    private void OnBackupTimer(object? state)
    {
        var now = DateTime.UtcNow;
        var bufferTime = TimeSpan.FromSeconds(10);

        // get paths that haven't been modified in the buffer period
        var pathsToBackup = _changedPaths
            .Where(kvp => (now - kvp.Value) > bufferTime)
            .Select(kvp => kvp.Key)
            .ToList();

        // remove paths that are going to be backed up
        foreach (var path in pathsToBackup)
        {
            _changedPaths.TryRemove(path, out _);
        }

        if (pathsToBackup.Count > 0)
        {
            _logger.Log($"Initiating backup for {pathsToBackup.Count} changed items");
            Task.Run(async () =>
            {
                foreach (var path in pathsToBackup)
                {
                    // double checking file still exists
                    if (File.Exists(path))
                    {
                        var backup = new Backup(_logger, _state, _sizeAnalyzer, _storage, _config);
                        await backup.BackupDirectoryAsync(Path.GetDirectoryName(path)!);
                    }
                }
            }).ConfigureAwait(false);
        }
    }
}