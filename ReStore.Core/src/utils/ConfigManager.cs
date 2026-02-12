using System.Text.Json;
using System.Reflection;
using ReStore.Core.src.storage;

namespace ReStore.Core.src.utils;

public interface IConfigManager
{
    List<WatchDirectoryConfig> WatchDirectories { get; }
    string GlobalStorageType { get; }
    TimeSpan BackupInterval { get; }
    long SizeThresholdMB { get; }
    Dictionary<string, StorageConfig> StorageSources { get; }
    List<string> ExcludedPatterns { get; }
    List<string> ExcludedPaths { get; }
    BackupType BackupType { get; }
    int MaxFileSizeMB { get; }
    SystemBackupConfig SystemBackup { get; }
    EncryptionConfig Encryption { get; }
    RetentionConfig Retention { get; }
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

public class WatchDirectoryConfig
{
    public string Path { get; set; } = string.Empty;
    public string? StorageType { get; set; }
}

public class StorageConfig
{
    public string Path { get; set; } = string.Empty;
    public Dictionary<string, string> Options { get; set; } = [];
}

public class SystemBackupConfig
{
    public bool Enabled { get; set; } = true;
    public bool IncludePrograms { get; set; } = true;
    public bool IncludeEnvironmentVariables { get; set; } = true;
    public bool IncludeWindowsSettings { get; set; } = true;
    public TimeSpan BackupInterval { get; set; } = TimeSpan.FromHours(24);
    public List<string> ExcludeSystemPrograms { get; set; } = [];
    public string? StorageType { get; set; }
    public string? ProgramsStorageType { get; set; }
    public string? EnvironmentStorageType { get; set; }
    public string? SettingsStorageType { get; set; }
}

public class EncryptionConfig
{
    public bool Enabled { get; set; } = false;
    public string? Salt { get; set; }
    public int KeyDerivationIterations { get; set; } = 1_000_000;
    public string? VerificationToken { get; set; }
}

public class RetentionConfig
{
    public bool Enabled { get; set; } = false;
    public int KeepLastPerDirectory { get; set; } = 10;
    public int MaxAgeDays { get; set; } = 30;
}

public class ConfigManager(ILogger logger) : IConfigManager
{
    private static readonly string CONFIG_PATH = GetConfigPath();
    private bool _isLoaded = false;
    private ConfigValidator? _validator;

    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions _readOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public List<WatchDirectoryConfig> WatchDirectories { get; private set; } = [];
    public string GlobalStorageType { get; private set; } = "local";
    public TimeSpan BackupInterval { get; private set; } = TimeSpan.FromHours(1);
    public long SizeThresholdMB { get; private set; } = 500;
    public Dictionary<string, StorageConfig> StorageSources { get; private set; } = [];
    public List<string> ExcludedPatterns { get; private set; } = [];
    public List<string> ExcludedPaths { get; private set; } = [];
    public BackupType BackupType { get; private set; } = BackupType.Incremental;
    public int MaxFileSizeMB { get; private set; } = 100;
    public SystemBackupConfig SystemBackup { get; private set; } = new();
    public EncryptionConfig Encryption { get; private set; } = new();
    public RetentionConfig Retention { get; private set; } = new();

    private readonly StorageFactory _storageFactory = new(logger);
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public ConfigValidationResult ValidateConfiguration()
    {
        _validator ??= new ConfigValidator(logger);
        return _validator.ValidateConfiguration(this);
    }

    public async Task<IStorage> CreateStorageAsync(string storageType)
    {
        if (!_isLoaded)
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
        using var jsonDoc = JsonDocument.Parse(jsonString);
        var root = jsonDoc.RootElement;

        try
        {
            // Load watch directories with optional per-path storage
            var watchDirsElement = root.GetProperty("watchDirectories");
            WatchDirectories = [];

            foreach (var element in watchDirsElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    // Legacy format: just a string path
                    WatchDirectories.Add(new WatchDirectoryConfig
                    {
                        Path = Environment.ExpandEnvironmentVariables(element.GetString() ?? string.Empty),
                        StorageType = null
                    });
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    // New format: object with path and storageType
                    var config = JsonSerializer.Deserialize<WatchDirectoryConfig>(element, _readOptions);
                    if (config != null)
                    {
                        config.Path = Environment.ExpandEnvironmentVariables(config.Path);
                        WatchDirectories.Add(config);
                    }
                }
            }

            BackupInterval = TimeSpan.Parse(root.GetProperty("backupInterval").GetString() ?? "01:00:00");
            SizeThresholdMB = root.GetProperty("sizeThresholdMB").GetInt64();

            var storageSources = root.GetProperty("storageSources");

            StorageSources = JsonSerializer.Deserialize<Dictionary<string, StorageConfig>>(
                storageSources,
                _readOptions) ?? throw new JsonException("Failed to deserialize storage sources");

            // Load global storage type AFTER StorageSources so the fallback works
            if (root.TryGetProperty("globalStorageType", out var globalStorageElement))
            {
                GlobalStorageType = globalStorageElement.GetString() ?? "local";
            }
            else
            {
                GlobalStorageType = StorageSources.Keys.FirstOrDefault() ?? "local";
            }

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

            // Load system backup configuration
            if (root.TryGetProperty("systemBackup", out var systemBackupElement))
            {
                SystemBackup = new SystemBackupConfig();

                if (systemBackupElement.TryGetProperty("enabled", out var enabled))
                    SystemBackup.Enabled = enabled.GetBoolean();

                if (systemBackupElement.TryGetProperty("includePrograms", out var includePrograms))
                    SystemBackup.IncludePrograms = includePrograms.GetBoolean();

                if (systemBackupElement.TryGetProperty("includeEnvironmentVariables", out var includeEnv))
                    SystemBackup.IncludeEnvironmentVariables = includeEnv.GetBoolean();

                if (systemBackupElement.TryGetProperty("includeWindowsSettings", out var includeSettings))
                    SystemBackup.IncludeWindowsSettings = includeSettings.GetBoolean();

                if (systemBackupElement.TryGetProperty("backupInterval", out var sysBackupInterval))
                    SystemBackup.BackupInterval = TimeSpan.Parse(sysBackupInterval.GetString() ?? "24:00:00");

                if (systemBackupElement.TryGetProperty("excludeSystemPrograms", out var excludePrograms))
                {
                    SystemBackup.ExcludeSystemPrograms = JsonSerializer.Deserialize<List<string>>(excludePrograms, _readOptions) ?? [];
                }

                if (systemBackupElement.TryGetProperty("storageType", out var storageType))
                    SystemBackup.StorageType = storageType.GetString();

                if (systemBackupElement.TryGetProperty("programsStorageType", out var programsStorage))
                    SystemBackup.ProgramsStorageType = programsStorage.GetString();

                if (systemBackupElement.TryGetProperty("environmentStorageType", out var envStorage))
                    SystemBackup.EnvironmentStorageType = envStorage.GetString();

                if (systemBackupElement.TryGetProperty("settingsStorageType", out var settingsStorage))
                    SystemBackup.SettingsStorageType = settingsStorage.GetString();
            }
            else
            {
                SetDefaultSystemBackupConfig();
            }

            // Load encryption configuration
            if (root.TryGetProperty("encryption", out var encryptionElement))
            {
                Encryption = new EncryptionConfig();

                if (encryptionElement.TryGetProperty("enabled", out var encEnabled))
                    Encryption.Enabled = encEnabled.GetBoolean();

                if (encryptionElement.TryGetProperty("salt", out var salt))
                    Encryption.Salt = salt.GetString();

                if (encryptionElement.TryGetProperty("keyDerivationIterations", out var iterations))
                    Encryption.KeyDerivationIterations = iterations.GetInt32();

                if (encryptionElement.TryGetProperty("verificationToken", out var verificationToken))
                    Encryption.VerificationToken = verificationToken.GetString();
            }

            // Load retention configuration
            if (root.TryGetProperty("retention", out var retentionElement))
            {
                Retention = new RetentionConfig();

                if (retentionElement.TryGetProperty("enabled", out var retentionEnabled))
                    Retention.Enabled = retentionEnabled.GetBoolean();

                if (retentionElement.TryGetProperty("keepLastPerDirectory", out var keepLast))
                    Retention.KeepLastPerDirectory = keepLast.GetInt32();

                if (retentionElement.TryGetProperty("maxAgeDays", out var maxAgeDays))
                    Retention.MaxAgeDays = maxAgeDays.GetInt32();
            }

            _isLoaded = true;
            logger.Log("Configuration loaded successfully", LogLevel.Info);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to load configuration", ex);
        }
    }

    public async Task SaveAsync(string configPath = "")
    {
        await _saveLock.WaitAsync();
        try
        {
            if (string.IsNullOrEmpty(configPath))
            {
                configPath = CONFIG_PATH;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

            var configObject = new
            {
                watchDirectories = WatchDirectories,
                globalStorageType = GlobalStorageType,
                backupInterval = BackupInterval.ToString(),
                sizeThresholdMB = SizeThresholdMB,
                maxFileSizeMB = MaxFileSizeMB,
                backupType = BackupType.ToString(),
                retention = new
                {
                    enabled = Retention.Enabled,
                    keepLastPerDirectory = Retention.KeepLastPerDirectory,
                    maxAgeDays = Retention.MaxAgeDays
                },
                systemBackup = new
                {
                    enabled = SystemBackup.Enabled,
                    includePrograms = SystemBackup.IncludePrograms,
                    includeEnvironmentVariables = SystemBackup.IncludeEnvironmentVariables,
                    includeWindowsSettings = SystemBackup.IncludeWindowsSettings,
                    backupInterval = SystemBackup.BackupInterval.ToString(),
                    excludeSystemPrograms = SystemBackup.ExcludeSystemPrograms,
                    storageType = SystemBackup.StorageType,
                    programsStorageType = SystemBackup.ProgramsStorageType,
                    environmentStorageType = SystemBackup.EnvironmentStorageType,
                    settingsStorageType = SystemBackup.SettingsStorageType
                },
                encryption = new
                {
                    enabled = Encryption.Enabled,
                    salt = Encryption.Salt,
                    keyDerivationIterations = Encryption.KeyDerivationIterations,
                    verificationToken = Encryption.VerificationToken
                },
                excludedPatterns = ExcludedPatterns,
                excludedPaths = ExcludedPaths,
                storageSources = StorageSources
            };

            var jsonString = JsonSerializer.Serialize(configObject, _writeOptions);

            var tempPath = configPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, jsonString);
            File.Move(tempPath, configPath, overwrite: true);

            logger.Log("Configuration saved successfully", LogLevel.Info);
        }
        catch (Exception ex)
        {
            logger.Log($"Error saving configuration: {ex.Message}", LogLevel.Error);
            throw;
        }
        finally
        {
            _saveLock.Release();
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
            new WatchDirectoryConfig
            {
                Path = Environment.ExpandEnvironmentVariables("%USERPROFILE%\\Desktop"),
                StorageType = null
            },
            new WatchDirectoryConfig
            {
                Path = Environment.ExpandEnvironmentVariables("%USERPROFILE%\\Documents"),
                StorageType = null
            },
            new WatchDirectoryConfig
            {
                Path = Environment.ExpandEnvironmentVariables("%USERPROFILE%\\Pictures"),
                StorageType = null
            }
        ];

        GlobalStorageType = "local";

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

    private void SetDefaultSystemBackupConfig()
    {
        SystemBackup = new SystemBackupConfig
        {
            Enabled = false,
            IncludePrograms = false,
            IncludeEnvironmentVariables = false,
            IncludeWindowsSettings = false,
            BackupInterval = TimeSpan.FromHours(24),
            ExcludeSystemPrograms =
            [
                "Microsoft Visual C++",
                "Microsoft .NET",
                "Windows SDK",
                "KB[0-9]+",
                "Update for",
                "Security Update"
            ]
        };
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
