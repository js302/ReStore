using System.Text.Json;
using ReStore.src.storage;

namespace ReStore.src.utils;

public interface IConfigManager
{
    List<string> WatchDirectories { get; }
    TimeSpan BackupInterval { get; }
    long SizeThresholdMB { get; }
    Dictionary<string, StorageConfig> StorageSources { get; }
    List<string> ExcludedPatterns { get; }
    List<string> ExcludedPaths { get; }
    BackupType BackupType { get; }
    int MaxFileSizeMB { get; }
    Task LoadAsync();
    Task SaveAsync(string configPath = "");
    Task<IStorage> CreateStorageAsync(string storageType);
}

public enum BackupType
{
    Full,
    Incremental,
    Differential
}

public class StorageConfig
{
    public string Path { get; set; } = string.Empty;
    public Dictionary<string, string> Options { get; set; } = [];
}

public class ConfigManager(ILogger logger) : IConfigManager
{
    private const string CONFIG_PATH = "config/default_config.json";
    private JsonDocument? _config;

    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions _readOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public List<string> WatchDirectories { get; private set; } = [];
    public TimeSpan BackupInterval { get; private set; } = TimeSpan.FromHours(1);
    public long SizeThresholdMB { get; private set; } = 500;
    public Dictionary<string, StorageConfig> StorageSources { get; private set; } = [];
    public List<string> ExcludedPatterns { get; private set; } = [];
    public List<string> ExcludedPaths { get; private set; } = [];
    public BackupType BackupType { get; private set; } = BackupType.Incremental;
    public int MaxFileSizeMB { get; private set; } = 100;

    private readonly StorageFactory _storageFactory = new(logger);

    public async Task<IStorage> CreateStorageAsync(string storageType)
    {
        if (_config is null)
        {
            throw new InvalidOperationException("Configuration not loaded");
        }

        if (!StorageSources.TryGetValue(storageType, out var config))
        {
            throw new ArgumentException($"Storage type '{storageType}' not found in configuration");
        }

        // Make sure Path is included in the options
        if (!string.IsNullOrEmpty(config.Path) && !config.Options.ContainsKey("path"))
        {
            config.Options["path"] = config.Path;
        }

        return await _storageFactory.CreateStorageAsync(storageType, config);
    }

    public async Task LoadAsync()
    {
        if (!File.Exists(CONFIG_PATH))
        {
            await CreateDefaultConfigAsync();
        }

        var jsonString = await File.ReadAllTextAsync(CONFIG_PATH);
        _config = JsonDocument.Parse(jsonString);
        var root = _config.RootElement;

        try
        {
            WatchDirectories = [.. root.GetProperty("watchDirectories")
                .EnumerateArray()
                .Select(x => Environment.ExpandEnvironmentVariables(x.GetString() ?? string.Empty))];

            BackupInterval = TimeSpan.Parse(root.GetProperty("backupInterval").GetString() ?? "01:00:00");
            SizeThresholdMB = root.GetProperty("sizeThresholdMB").GetInt64();

            var storageSources = root.GetProperty("storageSources");

            StorageSources = JsonSerializer.Deserialize<Dictionary<string, StorageConfig>>(
                storageSources,
                _readOptions) ?? throw new JsonException("Failed to deserialize storage sources");

            foreach (var source in StorageSources.Values)
            {
                source.Path = Environment.ExpandEnvironmentVariables(source.Path);
                source.Options = source.Options.ToDictionary(x => x.Key, x => Environment.ExpandEnvironmentVariables(x.Value));
            }

            // Load backup configuration
            if (root.TryGetProperty("excludedPatterns", out var excludedPatternsElement))
            {
                ExcludedPatterns = JsonSerializer.Deserialize<List<string>>(excludedPatternsElement, _readOptions) ?? [];
            }
            else
            {
                SetDefaultExcludedPatterns();
            }

            if (root.TryGetProperty("excludedPaths", out var excludedPathsElement))
            {
                ExcludedPaths = JsonSerializer.Deserialize<List<string>>(excludedPathsElement, _readOptions) ?? [];
                ExcludedPaths = ExcludedPaths.Select(p => Environment.ExpandEnvironmentVariables(p)).ToList();
            }
            else
            {
                SetDefaultExcludedPaths();
            }

            if (root.TryGetProperty("backupType", out var backupTypeElement))
            {
                BackupType = Enum.Parse<BackupType>(backupTypeElement.GetString() ?? "Incremental", true);
            }

            if (root.TryGetProperty("maxFileSizeMB", out var maxFileSizeElement))
            {
                MaxFileSizeMB = maxFileSizeElement.GetInt32();
            }

            logger.Log("Configuration loaded successfully", LogLevel.Info);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to load configuration", ex);
        }
    }

    public async Task SaveAsync(string configPath = "")
    {
        if (string.IsNullOrEmpty(configPath))
        {
            configPath = CONFIG_PATH;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

            var configObject = new
            {
                watchDirectories = WatchDirectories,
                backupInterval = BackupInterval.ToString(),
                sizeThresholdMB = SizeThresholdMB,
                storageSources = StorageSources,
                excludedPatterns = ExcludedPatterns,
                excludedPaths = ExcludedPaths,
                backupType = BackupType.ToString(),
                maxFileSizeMB = MaxFileSizeMB
            };

            var jsonString = JsonSerializer.Serialize(configObject, _writeOptions);
            await File.WriteAllTextAsync(configPath, jsonString);

            logger.Log("Configuration saved successfully", LogLevel.Info);
        }
        catch (Exception ex)
        {
            logger.Log($"Error saving configuration: {ex.Message}", LogLevel.Error);
            throw;
        }
    }

    private async Task CreateDefaultConfigAsync()
    {
        // Set default watch directories
        WatchDirectories =
        [
            Environment.ExpandEnvironmentVariables("%USERPROFILE%\\Documents"),
            Environment.ExpandEnvironmentVariables("%USERPROFILE%\\Pictures")
        ];

        // Set default storage
        StorageSources = new Dictionary<string, StorageConfig>
        {
            ["local"] = new StorageConfig
            {
                Path = Environment.ExpandEnvironmentVariables("%USERPROFILE%\\ReStoreBackups"),
                Options = new Dictionary<string, string>
                {
                    ["path"] = Environment.ExpandEnvironmentVariables("%USERPROFILE%\\ReStoreBackups")
                }
            }
        };

        SetDefaultExcludedPatterns();
        SetDefaultExcludedPaths();

        await SaveAsync();
    }

    private void SetDefaultExcludedPatterns()
    {
        ExcludedPatterns =
        [
            "*.tmp",
            "*.temp",
            "~$*",
            "Thumbs.db",
            ".DS_Store",
            "*.log",
            "*.pst",
            "desktop.ini",
            "*.lnk",
            "*.crdownload",
            "*.partial"
        ];
    }

    private void SetDefaultExcludedPaths()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        ExcludedPaths =
        [
            Path.Combine(userProfile, "Documents", "Temp"),
            Path.Combine(userProfile, "Downloads"),
            Environment.ExpandEnvironmentVariables("%TEMP%"),
            Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%\\Temp"),
            Environment.ExpandEnvironmentVariables("%WINDIR%"),
            Environment.ExpandEnvironmentVariables("%ProgramFiles%"),
            Environment.ExpandEnvironmentVariables("%ProgramFiles(x86)%")
        ];
    }
}
