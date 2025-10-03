using System.Text.Json;
using System.Reflection;
using ReStore.Core.src.storage;

namespace ReStore.Core.src.utils;

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
    ConfigValidationResult ValidateConfiguration();
    string GetConfigFilePath();
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
    private static readonly string CONFIG_PATH = GetConfigPath();
    private JsonDocument? _config;
    private ConfigValidator? _validator;

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

    public ConfigValidationResult ValidateConfiguration()
    {
        _validator ??= new ConfigValidator(logger);
        return _validator.ValidateConfiguration(this);
    }

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

    public string GetConfigFilePath()
    {
        return CONFIG_PATH;
    }

    public async Task LoadAsync()
    {
        var configDir = Path.GetDirectoryName(CONFIG_PATH)!;
        var configExists = File.Exists(CONFIG_PATH);
        
        if (!configExists)
        {
            var exampleConfigPath = Path.Combine(configDir, "config.example.json");
            var exampleExists = File.Exists(exampleConfigPath);
            
            if (!exampleExists)
            {
                await CreateDefaultConfigAsync();
            }
            else
            {
                throw new InvalidOperationException(
                    $"Configuration file not found at: {CONFIG_PATH}\n" +
                    $"An example configuration exists at: {exampleConfigPath}\n" +
                    "Please rename it to 'config.json' and configure your backup settings.");
            }
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
        var configDir = Path.GetDirectoryName(CONFIG_PATH)!;
        Directory.CreateDirectory(configDir);

        // Try to copy config.example.json from the application directory
        var examplePath = GetExampleConfigPath();
        if (examplePath != null && File.Exists(examplePath))
        {
            try
            {
                File.Copy(examplePath, CONFIG_PATH, overwrite: false);
                logger.Log($"No config.json found. Created from example config at {CONFIG_PATH}", LogLevel.Info);
                logger.Log("Please edit the config.json file to configure your backup settings.", LogLevel.Info);
                return;
            }
            catch (Exception ex)
            {
                logger.Log($"Failed to copy example config: {ex.Message}. Creating minimal config.", LogLevel.Warning);
            }
        }
        else
        {
            logger.Log("Example config not found. Creating minimal default config.", LogLevel.Warning);
        }

        WatchDirectories =
        [
            Environment.ExpandEnvironmentVariables("%USERPROFILE%\\Desktop"),
            Environment.ExpandEnvironmentVariables("%USERPROFILE%\\Documents"),
            Environment.ExpandEnvironmentVariables("%USERPROFILE%\\Pictures")
        ];

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

    private static string GetConfigPath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, "ReStore", "config.json");
    }

    private static string? GetExampleConfigPath()
    {
        var assemblyLocation = Assembly.GetExecutingAssembly().Location;
        var assemblyDirectory = Path.GetDirectoryName(assemblyLocation);
        
        if (string.IsNullOrEmpty(assemblyDirectory))
        {
            return null;
        }

        var currentDir = new DirectoryInfo(assemblyDirectory);
        DirectoryInfo? projectRoot = null;

        while (currentDir?.Parent != null)
        {
            if ((currentDir.Name == "net9.0" || currentDir.Name == "net9.0-windows")
                && currentDir.Parent?.Name == "Debug" && currentDir.Parent.Parent?.Name == "bin")
            {
                projectRoot = currentDir.Parent.Parent.Parent;
                break;
            }
            else if ((currentDir.Name == "net9.0" || currentDir.Name == "net9.0-windows")
                && currentDir.Parent?.Name == "Release" && currentDir.Parent.Parent?.Name == "bin")
            {
                projectRoot = currentDir.Parent.Parent.Parent;
                break;
            }
            else if (currentDir.GetFiles("*.csproj").Length > 0)
            {
                projectRoot = currentDir;
                break;
            }
            
            currentDir = currentDir.Parent;
        }

        if (projectRoot != null && projectRoot.Exists)
        {
            var probe = projectRoot;
            for (int i = 0; i < 3 && probe != null; i++)
            {
                var candidate = Path.Combine(probe.FullName, "config", "config.example.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                probe = probe.Parent;
            }
        }

        var inAppConfig = Path.Combine(assemblyDirectory, "config", "config.example.json");
        if (File.Exists(inAppConfig))
        {
            return inAppConfig;
        }

        return null;
    }
}
