using ReStore.src.utils;
using ReStore.src.core;
using ReStore.src.storage;

namespace ReStore.src.monitoring;

public class FileWatcher
{
    private readonly Dictionary<string, FileSystemWatcher> _watchers = [];
    private readonly HashSet<string> _changedPaths = [];
    private readonly Lock _lockObject = new();
    private readonly Timer _backupTimer;
    private readonly ILogger _logger;
    private readonly IConfigManager _config;
    private readonly SystemState _state;
    private readonly IStorage _storage;
    private readonly SizeAnalyzer _sizeAnalyzer;
    private readonly CompressionUtil _compressionUtil;

    public FileWatcher(IConfigManager config, ILogger logger, SystemState state, IStorage storage, SizeAnalyzer sizeAnalyzer, CompressionUtil compressionUtil)
    {
        _config = config;
        _logger = logger;
        _state = state;
        _storage = storage;
        _sizeAnalyzer = sizeAnalyzer;
        _compressionUtil = compressionUtil;
        _backupTimer = new Timer(OnBackupTimer, null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task StartAsync()
    {
        foreach (var path in _config.WatchDirectories)
        {
            AddWatcher(path);
        }

        // Start periodic backup timer
        _backupTimer.Change(TimeSpan.Zero, TimeSpan.FromHours(1));

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
        lock (_lockObject)
        {
            _changedPaths.Add(e.FullPath);
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        lock (_lockObject)
        {
            _changedPaths.Add(e.OldFullPath);
            _changedPaths.Add(e.FullPath);
        }
    }

    private void OnBackupTimer(object? state)
    {
        HashSet<string> pathsToBackup;
        lock (_lockObject)
        {
            pathsToBackup = new HashSet<string>(_changedPaths);
            _changedPaths.Clear();
        }

        if (pathsToBackup.Count <= 0)
            return;
        
        _logger.Log($"Initiating backup for {pathsToBackup.Count} changed items");
        Task.Run(async () =>
        {
            // Trigger backup system for each changed path
            foreach (var path in pathsToBackup)
            {
                var backup = new Backup(_logger, _state, _sizeAnalyzer, _storage, _compressionUtil);
                await backup.BackupDirectoryAsync(path);
            }

        }).ConfigureAwait(false);
    }
}
