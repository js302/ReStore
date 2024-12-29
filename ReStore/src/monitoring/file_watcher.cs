using ReStore.src.utils;
using ReStore.src.core;
using ReStore.src.storage;
using System.Collections.Concurrent;

namespace ReStore.src.monitoring;

public class FileWatcher
{
    private readonly Dictionary<string, FileSystemWatcher> _watchers = [];
    readonly ConcurrentDictionary<string, DateTime> _changedPaths = new();
    private readonly Lock _lockObject = new();
    private readonly Timer _backupTimer;
    private readonly ILogger _logger;
    private readonly IConfigManager _config;
    private readonly SystemState _state;
    private readonly IStorage _storage;
    private readonly SizeAnalyzer _sizeAnalyzer;
    private readonly CompressionUtil _compressionUtil;

    // Patterns to ignore during backup
    private static readonly string[] EXCLUDED_PATTERNS = {
        @"\\Temp\\",
        @"\\Windows\\",
        @"\\Microsoft\\",
        @"\\AppData\\Local\\Temp\\",
        @"\\Program Files\\",
        @"\\Program Files (x86)\\",
        "~$",
        ".tmp",
        ".temp",
        "desktop.ini"
    };

    private static readonly string[] EXCLUDED_EXTENSIONS = {
        ".tmp", ".temp", ".lnk", ".crdownload", ".partial", ".bak"
    };

    public FileWatcher(IConfigManager config, ILogger logger, SystemState state, IStorage storage,
                    SizeAnalyzer sizeAnalyzer, CompressionUtil compressionUtil)
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

    private static bool ShouldExcludePath(string path)
    {
        // normalize path for comparison
        path = path.Replace('/', '\\');

        // checking if the path exists
        if (!File.Exists(path) && !Directory.Exists(path))
            return true;

        // checking against excluded patterns
        if (EXCLUDED_PATTERNS.Any(pattern => path.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            return true;

        // checking file extension
        var extension = Path.GetExtension(path);
        if (EXCLUDED_EXTENSIONS.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return true;

        // check if it's a system or hidden file
        // TODO: check if this is necessary
        try
        {
            var attr = File.GetAttributes(path);
            if ((attr & FileAttributes.System) == FileAttributes.System ||
                (attr & FileAttributes.Hidden) == FileAttributes.Hidden)
                return true;
        }
        catch
        {
            // if I can't access the file, better to skip it
            return true;
        }

        return false;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!ShouldExcludePath(e.FullPath))
        {
            _changedPaths[e.FullPath] = DateTime.UtcNow;
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (!ShouldExcludePath(e.FullPath))
        {
            // only track the new name, remove the old one
            _changedPaths.TryRemove(e.OldFullPath, out _);
            _changedPaths[e.FullPath] = DateTime.UtcNow;
        }
    }

    private void OnBackupTimer(object? state)
    {
        var now = DateTime.UtcNow;

        // waiting for sometime after last change before backing up
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
                        var backup = new Backup(_logger, _state, _sizeAnalyzer, _storage);
                        await backup.BackupDirectoryAsync(Path.GetDirectoryName(path)!);
                    }
                }
            }).ConfigureAwait(false);
        }
    }
}
