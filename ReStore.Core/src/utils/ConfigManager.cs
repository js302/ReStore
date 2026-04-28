using System.Text.Json;
using System.Text.Json.Nodes;
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
    ChunkDiffingConfig ChunkDiffing { get; }
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
    ChunkSnapshot
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

public class ChunkDiffingConfig
{
    public int ManifestVersion { get; set; } = 2;
    public int MinChunkSizeKB { get; set; } = 32;
    public int TargetChunkSizeKB { get; set; } = 128;
    public int MaxChunkSizeKB { get; set; } = 512;
    public int RollingHashWindowSize { get; set; } = 64;
    public int MaxChunksPerFile { get; set; } = 200_000;
    public int MaxFilesPerSnapshot { get; set; } = 200_000;
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
    public BackupType BackupType { get; private set; } = BackupType.ChunkSnapshot;
    public int MaxFileSizeMB { get; private set; } = 100;
    public SystemBackupConfig SystemBackup { get; private set; } = new();
    public EncryptionConfig Encryption { get; private set; } = new();
    public RetentionConfig Retention { get; private set; } = new();
    public ChunkDiffingConfig ChunkDiffing { get; private set; } = new();

    private readonly StorageFactory _storageFactory = new(logger);
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public ConfigMigrationResult? LastMigrationResult { get; private set; }

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
        await EnsureConfigFileExistsAsync();

        var jsonString = await File.ReadAllTextAsync(CONFIG_PATH);
        var rootNode = JsonNode.Parse(jsonString) as JsonObject
            ?? throw new InvalidOperationException("Configuration root must be a JSON object.");

        var migrationResult = ConfigSchemaManager.Migrate(rootNode);
        if (migrationResult.MigrationApplied)
        {
            var backupPath = await PersistMigratedConfigurationAsync(jsonString, rootNode);
            migrationResult.BackupPath = backupPath;

            logger.Log(
                $"Configuration migrated from schema v{migrationResult.SourceSchemaVersion} to v{migrationResult.TargetSchemaVersion}.",
                LogLevel.Info);

            foreach (var migration in migrationResult.AppliedMigrations)
            {
                logger.Log($"Config Migration: {migration}", LogLevel.Info);
            }

            logger.Log($"Pre-migration backup saved at: {backupPath}", LogLevel.Info);
        }

        foreach (var warning in migrationResult.Warnings)
        {
            logger.Log($"Config Migration Warning: {warning}", LogLevel.Warning);
        }

        LastMigrationResult = migrationResult;

        var normalizedJson = rootNode.ToJsonString(_writeOptions);
        using var jsonDoc = JsonDocument.Parse(normalizedJson);
        var root = jsonDoc.RootElement;

        try
        {
            LoadWatchDirectories(root);
            LoadCoreBackupSettings(root);
            LoadStorageSettings(root);
            LoadExclusions(root);
            LoadBackupTypeAndLimits(root);
            LoadSystemBackupSettings(root);
            LoadEncryptionSettings(root);
            LoadRetentionSettings(root);
            LoadChunkDiffingSettings(root);

            _isLoaded = true;
            logger.Log("Configuration loaded successfully", LogLevel.Info);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to load configuration", ex);
        }
    }

    private async Task EnsureConfigFileExistsAsync()
    {
        var configDir = Path.GetDirectoryName(CONFIG_PATH)!;
        var configExists = File.Exists(CONFIG_PATH);

        if (!configExists)
        {
            var exampleConfigPath = Path.Combine(configDir, "config.example.json");
            var exampleExists = File.Exists(exampleConfigPath);

            if (exampleExists)
            {
                File.Copy(exampleConfigPath, CONFIG_PATH, overwrite: false);
                logger.Log($"No config.json found. Created from local example at {CONFIG_PATH}", LogLevel.Info);
            }
            else
            {
                await CreateDefaultConfigAsync();
            }
        }
    }

    private void LoadWatchDirectories(JsonElement root)
    {
        var watchDirsElement = root.GetProperty("watchDirectories");
        WatchDirectories = [];

        foreach (var element in watchDirsElement.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                WatchDirectories.Add(new WatchDirectoryConfig
                {
                    Path = Environment.ExpandEnvironmentVariables(element.GetString() ?? string.Empty),
                    StorageType = null
                });
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                var config = JsonSerializer.Deserialize<WatchDirectoryConfig>(element, _readOptions);
                if (config != null)
                {
                    config.Path = Environment.ExpandEnvironmentVariables(config.Path);
                    WatchDirectories.Add(config);
                }
            }
        }
    }

    private void LoadCoreBackupSettings(JsonElement root)
    {
        BackupInterval = TimeSpan.Parse(root.GetProperty("backupInterval").GetString() ?? "01:00:00");
        SizeThresholdMB = root.GetProperty("sizeThresholdMB").GetInt64();
    }

    private void LoadStorageSettings(JsonElement root)
    {
        var storageSources = root.GetProperty("storageSources");

        StorageSources = JsonSerializer.Deserialize<Dictionary<string, StorageConfig>>(
            storageSources,
            _readOptions) ?? throw new JsonException("Failed to deserialize storage sources");

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
    }

    private void LoadExclusions(JsonElement root)
    {
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
    }

    private void LoadBackupTypeAndLimits(JsonElement root)
    {
        if (root.TryGetProperty("backupType", out var backupTypeElement))
        {
            var backupTypeValue = backupTypeElement.GetString() ?? nameof(BackupType.ChunkSnapshot);
            if (backupTypeValue.Equals("Differential", StringComparison.OrdinalIgnoreCase))
            {
                backupTypeValue = nameof(BackupType.ChunkSnapshot);
            }

            BackupType = Enum.Parse<BackupType>(backupTypeValue, true);
        }

        if (root.TryGetProperty("maxFileSizeMB", out var maxFileSizeElement))
        {
            MaxFileSizeMB = maxFileSizeElement.GetInt32();
        }
    }

    private void LoadSystemBackupSettings(JsonElement root)
    {
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
    }

    private void LoadEncryptionSettings(JsonElement root)
    {
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
    }

    private void LoadRetentionSettings(JsonElement root)
    {
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
    }

    private void LoadChunkDiffingSettings(JsonElement root)
    {
        ChunkDiffing = new ChunkDiffingConfig();

        if (!root.TryGetProperty("chunkDiffing", out var chunkDiffingElement))
        {
            return;
        }

        if (chunkDiffingElement.TryGetProperty("manifestVersion", out var manifestVersion))
            ChunkDiffing.ManifestVersion = manifestVersion.GetInt32();

        if (chunkDiffingElement.TryGetProperty("minChunkSizeKB", out var minChunkSizeKB))
            ChunkDiffing.MinChunkSizeKB = minChunkSizeKB.GetInt32();

        if (chunkDiffingElement.TryGetProperty("targetChunkSizeKB", out var targetChunkSizeKB))
            ChunkDiffing.TargetChunkSizeKB = targetChunkSizeKB.GetInt32();

        if (chunkDiffingElement.TryGetProperty("maxChunkSizeKB", out var maxChunkSizeKB))
            ChunkDiffing.MaxChunkSizeKB = maxChunkSizeKB.GetInt32();

        if (chunkDiffingElement.TryGetProperty("rollingHashWindowSize", out var rollingHashWindowSize))
            ChunkDiffing.RollingHashWindowSize = rollingHashWindowSize.GetInt32();

        if (chunkDiffingElement.TryGetProperty("maxChunksPerFile", out var maxChunksPerFile))
            ChunkDiffing.MaxChunksPerFile = maxChunksPerFile.GetInt32();

        if (chunkDiffingElement.TryGetProperty("maxFilesPerSnapshot", out var maxFilesPerSnapshot))
            ChunkDiffing.MaxFilesPerSnapshot = maxFilesPerSnapshot.GetInt32();
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
                configSchemaVersion = ConfigSchemaManager.CURRENT_CONFIG_SCHEMA_VERSION,
                watchDirectories = WatchDirectories.Select(watchDirectory => new
                {
                    path = watchDirectory.Path,
                    storageType = watchDirectory.StorageType
                }),
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
                chunkDiffing = new
                {
                    manifestVersion = ChunkDiffing.ManifestVersion,
                    minChunkSizeKB = ChunkDiffing.MinChunkSizeKB,
                    targetChunkSizeKB = ChunkDiffing.TargetChunkSizeKB,
                    maxChunkSizeKB = ChunkDiffing.MaxChunkSizeKB,
                    rollingHashWindowSize = ChunkDiffing.RollingHashWindowSize,
                    maxChunksPerFile = ChunkDiffing.MaxChunksPerFile,
                    maxFilesPerSnapshot = ChunkDiffing.MaxFilesPerSnapshot
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
                storageSources = StorageSources.ToDictionary(
                    source => source.Key,
                    source => new
                    {
                        path = source.Value.Path,
                        options = source.Value.Options
                    },
                    StringComparer.OrdinalIgnoreCase)
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

    private async Task<string> PersistMigratedConfigurationAsync(string originalJson, JsonObject migratedRoot)
    {
        await _saveLock.WaitAsync();
        try
        {
            var backupPath = BuildMigrationBackupPath();
            await File.WriteAllTextAsync(backupPath, originalJson);

            var migratedJson = migratedRoot.ToJsonString(_writeOptions);
            var tempPath = CONFIG_PATH + ".tmp";
            await File.WriteAllTextAsync(tempPath, migratedJson);
            File.Move(tempPath, CONFIG_PATH, overwrite: true);

            return backupPath;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private static string BuildMigrationBackupPath()
    {
        var configDir = Path.GetDirectoryName(CONFIG_PATH)!;
        var backupDir = Path.Combine(configDir, "backups");
        Directory.CreateDirectory(backupDir);

        return Path.Combine(
            backupDir,
            $"config.pre-migration.{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}.json");
    }

    private async Task CreateDefaultConfigAsync()
    {
        var configDir = Path.GetDirectoryName(CONFIG_PATH)!;
        Directory.CreateDirectory(configDir);

        // Try to copy config.example.json from the application directory
        var examplePath = ConfigInitializer.ResolveApplicationExampleConfigPath();
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
}
