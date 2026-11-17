using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

    /// <summary>Names of built-in exclusion presets (see <see cref="ExcludePresetCatalog"/>). Default interface member so existing implementers keep compiling.</summary>
    List<string> ExcludePresets => [];

    /// <summary>Directory-aware glob exclusions (see <see cref="ExcludePattern"/>); unlike <see cref="ExcludedPatterns"/> they can prune folders. Default interface member so existing implementers keep compiling.</summary>
    List<string> ExcludeGlobs => [];
    BackupType BackupType { get; }
    int MaxFileSizeMB { get; }
    SystemBackupConfig SystemBackup { get; }
    EncryptionConfig Encryption { get; }
    RetentionConfig Retention { get; }
    ChunkDiffingConfig ChunkDiffing { get; }
    VerificationConfig Verification { get; }
    NotificationConfig Notifications { get; }

    /// <summary>Logging behaviour (minimum level). Default interface member so existing implementers keep compiling.</summary>
    LoggingConfig Logging => new();

    /// <summary>Background-protection behaviour; a default so existing implementers need not supply it.</summary>
    ProtectionConfig Protection => new();

    /// <summary>Anonymous usage telemetry settings; a default so existing implementers need not supply it.</summary>
    TelemetryConfig Telemetry => new();

    /// <summary>Update-check settings; a default so existing implementers need not supply it.</summary>
    UpdatesConfig Updates => new();

    /// <summary>File watcher settings. Default interface member so existing implementers keep compiling.</summary>
    WatcherConfig Watcher => new();

    /// <summary>Backup behaviour settings (deep scan schedule). Default interface member so existing implementers keep compiling.</summary>
    BackupSettingsConfig BackupSettings => new();

    /// <summary>Quiet-hours window automatic work may start in; null when none is configured. Default interface member so existing implementers keep compiling.</summary>
    BackupWindowConfig? BackupWindow => null;
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
    /// <summary>The path exactly as written in config.json (may contain <c>%VAR%</c> references).</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("storageType")]
    public string? StorageType { get; set; }

    /// <summary>
    /// When this folder is backed up on a clock (see <see cref="ReStore.Core.src.monitoring.BackupSchedule"/>: <c>daily 02:00</c>,
    /// <c>every 4h</c>, <c>off</c>, ...). Null or blank uses the global <c>backupInterval</c>.
    /// </summary>
    [JsonPropertyName("schedule")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Schedule { get; set; }

    /// <summary><see cref="Path"/> with environment references expanded; use this when touching the file system.</summary>
    [JsonIgnore]
    public string ExpandedPath => ConfigManager.ExpandConfiguredPath(Path);

    /// <summary>Members this version does not know about; written back unchanged on save.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class StorageConfig
{
    /// <summary>The path exactly as written in config.json (may contain <c>%VAR%</c> references).</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>Raw provider options. Values may be <c>%VAR%</c> references (e.g. secrets); expand with <see cref="Expanded"/> at use.</summary>
    [JsonPropertyName("options")]
    public Dictionary<string, string> Options { get; set; } = [];

    /// <summary>Members this version does not know about; written back unchanged on save.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>
    /// A detached copy with environment references expanded in <see cref="Path"/> and every option value.
    /// The model itself stays raw so a save never writes an expanded secret back to disk.
    /// </summary>
    public StorageConfig Expanded() => new()
    {
        Path = ConfigManager.ExpandConfiguredPath(Path),
        Options = Options.ToDictionary(o => o.Key, o => ConfigManager.ExpandConfiguredPath(o.Value), StringComparer.Ordinal)
    };

    /// <summary>
    /// Merges <paramref name="updates"/> into <paramref name="existing"/>: updated keys overwrite, keys the
    /// caller does not mention (options the form does not show, hand-added keys) are kept. A blank value for an
    /// option that does not exist yet is not added.
    /// </summary>
    public static Dictionary<string, string> MergeOptions(
        IReadOnlyDictionary<string, string>? existing,
        IReadOnlyDictionary<string, string> updates)
    {
        var merged = existing == null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(existing.ToDictionary(o => o.Key, o => o.Value));
        foreach (var update in updates)
        {
            // A blank field for an option that was never set stays unset instead of becoming "".
            if (string.IsNullOrWhiteSpace(update.Value) && !merged.ContainsKey(update.Key))
            {
                continue;
            }

            merged[update.Key] = update.Value;
        }

        return merged;
    }
}

public class SystemBackupConfig
{
    public bool Enabled { get; set; } = true;
    public bool IncludePrograms { get; set; } = true;
    public bool IncludeEnvironmentVariables { get; set; } = true;
    public bool IncludeWindowsSettings { get; set; } = true;

    /// <summary>Opt-in: Windows Terminal, PowerShell profiles, VS Code, git config (see docs/system-backup.md).</summary>
    public bool IncludeDevTools { get; set; }

    /// <summary>Opt-in within devtools: <c>~/.ssh/config</c>. Private keys are never backed up.</summary>
    public bool IncludeSshConfig { get; set; }
    public TimeSpan BackupInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>A clock schedule for the system backup (<c>daily 03:00</c>, <c>off</c>, ...); null or blank uses <see cref="BackupInterval"/>.</summary>
    public string? Schedule { get; set; }
    public List<string> ExcludeSystemPrograms { get; set; } = [];
    public string? StorageType { get; set; }
    public string? ProgramsStorageType { get; set; }
    public string? EnvironmentStorageType { get; set; }
    public string? SettingsStorageType { get; set; }
    public string? DevToolsStorageType { get; set; }
}

/// <summary>
/// The backup window ("quiet hours"): automatic work only starts inside it. See <see cref="ReStore.Core.src.monitoring.BackupWindow"/>
/// for the exact rules (local time, start inclusive, end exclusive, may cross midnight, start == end is the whole day).
/// </summary>
public class BackupWindowConfig
{
    /// <summary>False keeps the window in the file without applying it.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Local time of day (HH:mm) the window opens.</summary>
    [JsonPropertyName("start")]
    public string Start { get; set; } = "22:00";

    /// <summary>Local time of day (HH:mm) the window closes; before <see cref="Start"/> it ends the next morning.</summary>
    [JsonPropertyName("end")]
    public string End { get; set; } = "06:00";

    /// <summary><c>all</c>, <c>weekdays</c>, <c>weekends</c> or days such as <c>mon,wed,fri</c> / <c>mon-fri</c>: the days a window starts on.</summary>
    [JsonPropertyName("days")]
    public string Days { get; set; } = "all";

    /// <summary>Members this version does not know about; written back unchanged on save.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>File watcher settings.</summary>
public class WatcherConfig
{
    /// <summary>
    /// Minimum time between two watcher-triggered snapshots. Changes inside the interval are kept and flushed
    /// once it has elapsed. Zero or negative disables the minimum.
    /// </summary>
    public TimeSpan MinSnapshotInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Longest the watcher keeps postponing a backup while changes keep arriving (the 10 s debounce restarts on
    /// every event; without a cap a continuously busy folder would never be backed up). Zero disables the cap.
    /// </summary>
    public TimeSpan MaxDebounceWait { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>Backup behaviour settings.</summary>
public class BackupSettingsConfig
{
    public const int DefaultDeepScanIntervalDays = 7;

    /// <summary>
    /// How often a group is deep-scanned. Between deep scans an unchanged file is recognised by its size and
    /// timestamps without being read; a deep scan hashes every file, which catches a change that preserved the
    /// size and timestamps. Zero means every run is a deep scan.
    /// </summary>
    public int DeepScanIntervalDays { get; set; } = DefaultDeepScanIntervalDays;

    /// <summary>
    /// Whether cloud-only placeholder files (OneDrive Files On Demand) are downloaded to be backed up. Off by default:
    /// they are skipped and reported as "cloud-only" instead of silently downloading a whole cloud drive.
    /// </summary>
    public bool HydrateCloudFiles { get; set; }
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

    /// <summary>Alias of <see cref="KeepLastPerDirectory"/> (config key <c>keepLast</c>).</summary>
    public int KeepLast
    {
        get => KeepLastPerDirectory;
        set => KeepLastPerDirectory = value;
    }

    /// <summary>Alias of <see cref="MaxAgeDays"/> (config key <c>keepWithinDays</c>).</summary>
    public int KeepWithinDays
    {
        get => MaxAgeDays;
        set => MaxAgeDays = value;
    }

    // GFS tiers: keep the newest snapshot of each of the last N hours/days/ISO weeks/months/years that have one. 0 = tier off.
    public int KeepHourly { get; set; }
    public int KeepDaily { get; set; }
    public int KeepWeekly { get; set; }
    public int KeepMonthly { get; set; }
    public int KeepYearly { get; set; }

    /// <summary>"any" (default): a snapshot is kept if any rule keeps it. "all": only if every enabled rule keeps it.</summary>
    public string Mode { get; set; } = RetentionModes.Any;
}

public static class RetentionModes
{
    public const string Any = "any";
    public const string All = "all";

    public static bool IsValid(string? mode) =>
        string.Equals(mode, Any, StringComparison.OrdinalIgnoreCase) || string.Equals(mode, All, StringComparison.OrdinalIgnoreCase);
}

public class ChunkDiffingConfig
{
    /// <summary>
    /// 3 = always write v3, 2 = always write v2, 0 = key absent: automatic (new groups and groups already on v3 write v3,
    /// existing v2 groups stay v2). Config migration fills 2 for existing configs; new configs ship 3.
    /// </summary>
    public int ManifestVersion { get; set; }

    /// <summary>"auto" (default): v3 chunks are Brotli-compressed when that pays off; "off": always stored raw. Ignored for v2.</summary>
    public string Compression { get; set; } = "auto";
    public int MinChunkSizeKB { get; set; } = 32;
    public int TargetChunkSizeKB { get; set; } = 128;
    public int MaxChunkSizeKB { get; set; } = 512;
    public int RollingHashWindowSize { get; set; } = 64;
    public int MaxChunksPerFile { get; set; } = 200_000;
    public int MaxFilesPerSnapshot { get; set; } = 200_000;
}

/// <summary>
/// Scheduled verification. Off by default: verifying downloads and decrypts every unique
/// chunk, which is a real bandwidth cost on egress-billed providers.
/// </summary>
public class VerificationConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>Zero or negative disables scheduling, matching <c>backupInterval</c>.</summary>
    public TimeSpan VerifyInterval { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Restricts scheduled verification to local providers. Defaults to true so enabling
    /// the feature cannot silently start billing a user for cloud egress.
    /// </summary>
    public bool LocalStorageOnly { get; set; } = true;

    /// <summary>
    /// Snapshots to verify per group per due cycle. The newest is always first; any remaining
    /// budget rotates through older snapshots, oldest-verified first, so bit-rot in an older
    /// restore point is eventually caught instead of never being looked at.
    /// </summary>
    public int SnapshotsPerRun { get; set; } = 2;

    /// <summary>
    /// How thoroughly a scheduled verification checks a snapshot: <c>full</c> (every chunk downloaded and hashed, the
    /// default and the behaviour before modes existed), <c>sample</c> (existence and size of every chunk, full check of
    /// a random <see cref="SamplePercent"/> of them) or <c>quick</c> (existence and stored size only, no downloads).
    /// See <see cref="ReStore.Core.src.core.VerificationModes"/>.
    /// </summary>
    public string Mode { get; set; } = "full";

    /// <summary>Percentage of chunks fully verified in <c>sample</c> mode (1 to 100).</summary>
    public int SamplePercent { get; set; } = 5;
}

/// <summary>
/// Tray notifications. Deliberately silent on routine success — notifying on every good
/// backup trains people to ignore the ones that matter.
/// </summary>
public class NotificationConfig
{
    public bool Enabled { get; set; } = true;
    public bool NotifyOnBackupFailure { get; set; } = true;
    public bool NotifyOnVerificationFailure { get; set; } = true;

    /// <summary>Notify on the first success after a run of failures.</summary>
    public bool NotifyOnRecovery { get; set; } = true;

    /// <summary>Off by default; see the type remark.</summary>
    public bool NotifyOnEveryBackupSuccess { get; set; } = false;
}

/// <summary>
/// Logging settings. <see cref="Level"/> is the minimum level written (Debug, Info, Warning, Error); lines below it are
/// dropped. Info is the default; Debug adds per-chunk detail and is much more verbose on large backups.
/// </summary>
public class LoggingConfig
{
    public const string DefaultLevel = "Info";

    public string Level { get; set; } = DefaultLevel;

    /// <summary>Parses <paramref name="value"/> (case-insensitive); false when it is not a known level.</summary>
    public static bool TryParseLevel(string? value, out LogLevel level) =>
        Enum.TryParse(value?.Trim(), ignoreCase: true, out level)
        && Enum.GetNames<LogLevel>().Contains(value!.Trim(), StringComparer.OrdinalIgnoreCase); // names only, not "3"
}

/// <summary>Background protection (file watcher + scheduler) settings.</summary>
public class ProtectionConfig
{
    /// <summary>
    /// Start protection automatically at launch. Only takes effect when at least one watch
    /// directory is configured, so a fresh install is not started before setup.
    /// </summary>
    public bool AutoStart { get; set; } = true;
}

/// <summary>
/// Anonymous usage telemetry (see docs/telemetry.md): which features are used and whether they succeed, never paths,
/// file names or file contents. On by default; users are told on first run and can turn it off here, with
/// <c>restore telemetry disable</c>, or with the RESTORE_TELEMETRY_OPTOUT / DO_NOT_TRACK environment variables.
/// </summary>
public class TelemetryConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether a scrubbed crash report may be sent along with telemetry: <c>ask</c> (default: keep it locally and ask),
    /// <c>always</c> or <c>never</c> (see <see cref="ReStore.Core.src.telemetry.CrashReportMode"/>). Crash reports also need telemetry to be on.
    /// </summary>
    public string CrashReports { get; set; } = "ask";
}

/// <summary>Checking the GitHub releases for a newer ReStore version.</summary>
public class UpdatesConfig
{
    /// <summary>Check for a new version in the background (at most once a day) and tell the user. Installing always needs a user action.</summary>
    public bool CheckAutomatically { get; set; } = true;

    /// <summary>Also offer pre-release versions.</summary>
    public bool IncludePrereleases { get; set; }
}

public partial class ConfigManager(ILogger logger, string? configPathOverride) : IConfigManager
{
    /// <summary>The usual manager: reads and writes <see cref="AppPaths.ConfigFile"/>.</summary>
    public ConfigManager(ILogger logger) : this(logger, null)
    {
    }

    /// <summary>The file this manager reads and writes; an explicit path is used to validate a candidate config without touching the real one.</summary>
    private string CONFIG_PATH => configPathOverride ?? GetConfigPath();
    private bool _isLoaded = false;
    private ConfigValidator? _validator;

    /// <summary>The post-migration root of the file last loaded; source of members we do not model.</summary>
    private JsonObject? _rawRoot;

    /// <summary>The content of config.json when this instance last read or wrote it; null before the first load.</summary>
    private ConfigFileSignature? _diskSignature;

    /// <summary>The model as it would be written right after the last load/save: the common ancestor of a three-way merge when the file changed meanwhile.</summary>
    private JsonObject? _baseNode;

    private string? _lastMigratedText;

    /// <summary>The signature of config.json as this instance last saw it.</summary>
    public ConfigFileSignature? LoadedSignature => _diskSignature;

    /// <summary>True when config.json now holds something other than what this instance loaded or wrote last (the CLI or another process edited it).</summary>
    public async Task<bool> HasChangedOnDiskAsync()
    {
        if (_diskSignature == null)
        {
            return false;
        }

        var current = await ReadDiskSignatureAsync().ConfigureAwait(false);
        return current != null && !current.SameContent(_diskSignature);
    }

    /// <summary>The signature of config.json as it is on disk now; null when it does not exist or cannot be read.</summary>
    public async Task<ConfigFileSignature?> ReadDiskSignatureAsync()
    {
        try
        {
            if (!File.Exists(CONFIG_PATH))
            {
                return null;
            }

            var text = await SharedFile.ReadAllTextAsync(CONFIG_PATH).ConfigureAwait(false);
            return ConfigFileSignature.Of(text, SafeLastWriteUtc());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private DateTime SafeLastWriteUtc()
    {
        try
        {
            return File.GetLastWriteTimeUtc(CONFIG_PATH);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

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

    /// <summary>
    /// Sets the default storage provider for anything that doesn't specify its own.
    /// Validated, because pointing the default at an unconfigured provider makes every
    /// backup relying on it fail at storage-creation time.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Empty, or not present in <see cref="StorageSources"/>.
    /// </exception>
    public void SetGlobalStorageType(string storageType)
    {
        if (string.IsNullOrWhiteSpace(storageType))
        {
            throw new ArgumentException("Global storage type cannot be null or empty.", nameof(storageType));
        }

        if (!StorageSources.ContainsKey(storageType))
        {
            throw new ArgumentException(
                $"Storage type '{storageType}' is not present in the configured storage sources.",
                nameof(storageType));
        }

        GlobalStorageType = storageType;
    }
    public TimeSpan BackupInterval { get; set; } = TimeSpan.FromHours(1);
    public long SizeThresholdMB { get; set; } = 500;
    public Dictionary<string, StorageConfig> StorageSources { get; private set; } = [];
    public List<string> ExcludedPatterns { get; private set; } = [];
    public List<string> ExcludedPaths { get; private set; } = [];
    public List<string> ExcludePresets { get; private set; } = [];
    public List<string> ExcludeGlobs { get; private set; } = [];
    public BackupType BackupType { get; set; } = BackupType.ChunkSnapshot;
    public int MaxFileSizeMB { get; set; } = 100;
    public SystemBackupConfig SystemBackup { get; private set; } = new();
    public EncryptionConfig Encryption { get; private set; } = new();
    public RetentionConfig Retention { get; private set; } = new();
    public ChunkDiffingConfig ChunkDiffing { get; private set; } = new();
    public VerificationConfig Verification { get; private set; } = new();
    public NotificationConfig Notifications { get; private set; } = new();
    public LoggingConfig Logging { get; private set; } = new();
    public ProtectionConfig Protection { get; private set; } = new();
    public TelemetryConfig Telemetry { get; private set; } = new();
    public UpdatesConfig Updates { get; private set; } = new();
    public WatcherConfig Watcher { get; private set; } = new();
    public BackupSettingsConfig BackupSettings { get; private set; } = new();
    public BackupWindowConfig? BackupWindow { get; set; }

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

        // The model keeps raw %VAR% values; expand only into a throwaway copy handed to the provider.
        return await _storageFactory.CreateStorageAsync(storageType, BuildProviderConfig(config)).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a provider from an explicit (possibly unsaved) configuration, e.g. the Settings form values for
    /// "Test connection". Nothing is read from or written to the loaded model.
    /// </summary>
    public Task<IStorage> CreateStorageAsync(string storageType, StorageConfig config) =>
        _storageFactory.CreateStorageAsync(storageType, BuildProviderConfig(config));

    /// <summary>
    /// The configuration a provider is initialised with: expanded, and with the legacy top-level
    /// <c>path</c> copied into the options when the options do not carry one. <c>dpapi:</c> and <c>env:NAME</c>
    /// option values are resolved here, only into this throwaway copy (<see cref="SecretReferences"/>).
    /// </summary>
    public static StorageConfig BuildProviderConfig(StorageConfig config, ISecretProtector? protector = null)
    {
        var expanded = config.Expanded();
        SecretReferences.ResolveOptions(expanded.Options, protector);
        if (!string.IsNullOrEmpty(expanded.Path) && !expanded.Options.ContainsKey("path"))
        {
            expanded.Options["path"] = expanded.Path;
        }

        return expanded;
    }

    public string GetConfigFilePath()
    {
        return CONFIG_PATH;
    }

    public async Task LoadAsync()
    {
        await EnsureConfigFileExistsAsync().ConfigureAwait(false);

        var jsonString = await SharedFile.ReadAllTextAsync(CONFIG_PATH).ConfigureAwait(false);
        var rootNode = JsonNode.Parse(jsonString) as JsonObject
            ?? throw new InvalidOperationException("Configuration root must be a JSON object.");

        var migrationResult = ConfigSchemaManager.Migrate(rootNode);
        if (migrationResult.MigrationApplied)
        {
            var backupPath = await PersistMigratedConfigurationAsync(jsonString, rootNode).ConfigureAwait(false);
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
        _rawRoot = rootNode.DeepClone() as JsonObject;

        ApplyLoadedRoot(rootNode);

        // What the file held when this instance read it (after our own migration rewrite, which is what is on disk now).
        _diskSignature = ConfigFileSignature.Of(migrationResult.MigrationApplied ? _lastMigratedText ?? jsonString : jsonString, SafeLastWriteUtc());
        _baseNode = BuildModelNode();
        logger.Log("Configuration loaded successfully", LogLevel.Info);
    }

    /// <summary>Fills the model from an already migrated config document.</summary>
    private void ApplyLoadedRoot(JsonObject rootNode)
    {
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
            LoadVerificationSettings(root);
            LoadNotificationSettings(root);
            LoadLoggingSettings(root);
            LoadProtectionSettings(root);
            LoadTelemetrySettings(root);
            LoadUpdatesSettings(root);
            LoadWatcherSettings(root);
            LoadBackupSettings(root);
            LoadBackupWindow(root);

            _isLoaded = true;
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
                await CreateDefaultConfigAsync().ConfigureAwait(false);
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
                    Path = element.GetString() ?? string.Empty,
                    StorageType = null
                });
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                var config = JsonSerializer.Deserialize<WatchDirectoryConfig>(element, _readOptions);
                if (config != null)
                {
                    WatchDirectories.Add(config);
                }
            }
        }
    }

    private void LoadCoreBackupSettings(JsonElement root)
    {
        BackupInterval = TimeSpan.Parse(root.GetProperty("backupInterval").GetString() ?? "01:00:00", System.Globalization.CultureInfo.InvariantCulture);
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
        }
        else
        {
            SetDefaultExcludedPaths();
        }

        // A config without the key keeps today's behaviour (its own patterns already cover ~$* and Thumbs.db);
        // only a freshly created config gets the default presets.
        ExcludeGlobs = root.TryGetProperty("excludeGlobs", out var globsElement) && globsElement.ValueKind == JsonValueKind.Array
            ? JsonSerializer.Deserialize<List<string>>(globsElement, _readOptions) ?? []
            : [];

        ExcludePresets = root.TryGetProperty("excludePresets", out var presetsElement) && presetsElement.ValueKind == JsonValueKind.Array
            ? JsonSerializer.Deserialize<List<string>>(presetsElement, _readOptions) ?? []
            : [];
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

    private void LoadBackupWindow(JsonElement root)
    {
        BackupWindow = root.TryGetProperty("backupWindow", out var windowElement) && windowElement.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<BackupWindowConfig>(windowElement, _readOptions)
            : null;
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

            if (systemBackupElement.TryGetProperty("includeDevTools", out var includeDevTools))
                SystemBackup.IncludeDevTools = includeDevTools.GetBoolean();

            if (systemBackupElement.TryGetProperty("includeSshConfig", out var includeSsh))
                SystemBackup.IncludeSshConfig = includeSsh.GetBoolean();

            if (systemBackupElement.TryGetProperty("backupInterval", out var sysBackupInterval))
                SystemBackup.BackupInterval = TimeSpan.Parse(sysBackupInterval.GetString() ?? "24:00:00", System.Globalization.CultureInfo.InvariantCulture);
            if (systemBackupElement.TryGetProperty("schedule", out var sysSchedule) && sysSchedule.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(sysSchedule.GetString()))
                SystemBackup.Schedule = sysSchedule.GetString();

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

            if (systemBackupElement.TryGetProperty("devToolsStorageType", out var devToolsStorage))
                SystemBackup.DevToolsStorageType = devToolsStorage.GetString();
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

            // The short names are aliases; when present they win (they are never written back).
            if (retentionElement.TryGetProperty("keepLast", out var keepLastAlias))
                Retention.KeepLast = keepLastAlias.GetInt32();

            if (retentionElement.TryGetProperty("keepWithinDays", out var keepWithinAlias))
                Retention.KeepWithinDays = keepWithinAlias.GetInt32();

            if (retentionElement.TryGetProperty("keepHourly", out var keepHourly))
                Retention.KeepHourly = keepHourly.GetInt32();

            if (retentionElement.TryGetProperty("keepDaily", out var keepDaily))
                Retention.KeepDaily = keepDaily.GetInt32();

            if (retentionElement.TryGetProperty("keepWeekly", out var keepWeekly))
                Retention.KeepWeekly = keepWeekly.GetInt32();

            if (retentionElement.TryGetProperty("keepMonthly", out var keepMonthly))
                Retention.KeepMonthly = keepMonthly.GetInt32();

            if (retentionElement.TryGetProperty("keepYearly", out var keepYearly))
                Retention.KeepYearly = keepYearly.GetInt32();

            if (retentionElement.TryGetProperty("mode", out var retentionMode) && retentionMode.ValueKind == JsonValueKind.String)
                Retention.Mode = (retentionMode.GetString() ?? RetentionModes.Any).Trim().ToLowerInvariant();
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

        if (chunkDiffingElement.TryGetProperty("compression", out var compression) && compression.ValueKind == JsonValueKind.String)
            ChunkDiffing.Compression = compression.GetString() ?? "auto";

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

    private void LoadVerificationSettings(JsonElement root)
    {
        Verification = new VerificationConfig();

        if (!root.TryGetProperty("verification", out var verificationElement))
        {
            return;
        }

        if (verificationElement.TryGetProperty("enabled", out var enabled))
            Verification.Enabled = enabled.GetBoolean();

        if (verificationElement.TryGetProperty("verifyInterval", out var verifyInterval)
            && TimeSpan.TryParse(verifyInterval.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var parsedInterval))
        {
            Verification.VerifyInterval = parsedInterval;
        }

        if (verificationElement.TryGetProperty("localStorageOnly", out var localOnly))
            Verification.LocalStorageOnly = localOnly.GetBoolean();

        if (verificationElement.TryGetProperty("snapshotsPerRun", out var snapshotsPerRun))
            Verification.SnapshotsPerRun = snapshotsPerRun.GetInt32();

        if (verificationElement.TryGetProperty("mode", out var verifyMode) && verifyMode.ValueKind == JsonValueKind.String)
            Verification.Mode = verifyMode.GetString() ?? "full";

        if (verificationElement.TryGetProperty("samplePercent", out var verifySamplePercent))
            Verification.SamplePercent = verifySamplePercent.GetInt32();
    }

    private void LoadWatcherSettings(JsonElement root)
    {
        Watcher = new WatcherConfig();

        if (root.TryGetProperty("watcher", out var watcherElement)
            && watcherElement.ValueKind == JsonValueKind.Object
            && watcherElement.TryGetProperty("minSnapshotInterval", out var minInterval)
            && minInterval.ValueKind == JsonValueKind.String
            && TimeSpan.TryParse(minInterval.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var parsedMinInterval))
        {
            Watcher.MinSnapshotInterval = parsedMinInterval;
        }

        if (root.TryGetProperty("watcher", out watcherElement)
            && watcherElement.ValueKind == JsonValueKind.Object
            && watcherElement.TryGetProperty("maxDebounceWait", out var maxWait)
            && maxWait.ValueKind == JsonValueKind.String
            && TimeSpan.TryParse(maxWait.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var parsedMaxWait))
        {
            Watcher.MaxDebounceWait = parsedMaxWait;
        }
    }

    private void LoadBackupSettings(JsonElement root)
    {
        BackupSettings = new BackupSettingsConfig();

        if (root.TryGetProperty("backup", out var hydrateParent)
            && hydrateParent.ValueKind == JsonValueKind.Object
            && hydrateParent.TryGetProperty("hydrateCloudFiles", out var hydrate)
            && hydrate.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            BackupSettings.HydrateCloudFiles = hydrate.GetBoolean();
        }

        if (root.TryGetProperty("backup", out var backupElement)
            && backupElement.ValueKind == JsonValueKind.Object
            && backupElement.TryGetProperty("deepScanIntervalDays", out var deepScan))
        {
            if (deepScan.ValueKind == JsonValueKind.Number && deepScan.TryGetInt32(out var deepScanDays))
            {
                BackupSettings.DeepScanIntervalDays = deepScanDays;
            }
            else
            {
                logger.Log($"backup.deepScanIntervalDays must be an integer; using the default of {BackupSettingsConfig.DefaultDeepScanIntervalDays}.", LogLevel.Warning);
            }
        }
    }

    private void LoadNotificationSettings(JsonElement root)
    {
        Notifications = new NotificationConfig();

        if (!root.TryGetProperty("notifications", out var notificationsElement))
        {
            return;
        }

        if (notificationsElement.TryGetProperty("enabled", out var enabled))
            Notifications.Enabled = enabled.GetBoolean();

        if (notificationsElement.TryGetProperty("notifyOnBackupFailure", out var onBackupFailure))
            Notifications.NotifyOnBackupFailure = onBackupFailure.GetBoolean();

        if (notificationsElement.TryGetProperty("notifyOnVerificationFailure", out var onVerifyFailure))
            Notifications.NotifyOnVerificationFailure = onVerifyFailure.GetBoolean();

        if (notificationsElement.TryGetProperty("notifyOnRecovery", out var onRecovery))
            Notifications.NotifyOnRecovery = onRecovery.GetBoolean();

        if (notificationsElement.TryGetProperty("notifyOnEveryBackupSuccess", out var onEverySuccess))
            Notifications.NotifyOnEveryBackupSuccess = onEverySuccess.GetBoolean();
    }

    private void LoadLoggingSettings(JsonElement root)
    {
        Logging = new LoggingConfig();

        if (root.TryGetProperty("logging", out var loggingElement)
            && loggingElement.ValueKind == JsonValueKind.Object
            && loggingElement.TryGetProperty("level", out var level)
            && level.ValueKind == JsonValueKind.String)
        {
            Logging.Level = level.GetString() ?? LoggingConfig.DefaultLevel;
        }

        // An unknown level is reported by the validator; the logger keeps Info meanwhile.
        if (logger is Logger concrete)
        {
            concrete.MinimumLevel = LoggingConfig.TryParseLevel(Logging.Level, out var parsed) ? parsed : LogLevel.Info;
        }
    }

    private void LoadProtectionSettings(JsonElement root)
    {
        Protection = new ProtectionConfig();

        if (root.TryGetProperty("protection", out var protectionElement)
            && protectionElement.ValueKind == JsonValueKind.Object
            && protectionElement.TryGetProperty("autoStart", out var autoStart)
            && autoStart.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            Protection.AutoStart = autoStart.GetBoolean();
        }
    }

    private void LoadTelemetrySettings(JsonElement root)
    {
        Telemetry = new TelemetryConfig();

        if (root.TryGetProperty("telemetry", out var telemetryElement)
            && telemetryElement.ValueKind == JsonValueKind.Object)
        {
            if (telemetryElement.TryGetProperty("enabled", out var enabled)
                && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                Telemetry.Enabled = enabled.GetBoolean();
            }

            if (telemetryElement.TryGetProperty("crashReports", out var crashReports)
                && crashReports.ValueKind == JsonValueKind.String
                && ReStore.Core.src.telemetry.CrashReportMode.Normalize(crashReports.GetString()) is { } mode)
            {
                Telemetry.CrashReports = mode;
            }
        }
    }

    private void LoadUpdatesSettings(JsonElement root)
    {
        Updates = new UpdatesConfig();

        if (root.TryGetProperty("updates", out var updatesElement) && updatesElement.ValueKind == JsonValueKind.Object)
        {
            if (updatesElement.TryGetProperty("checkAutomatically", out var check) && check.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                Updates.CheckAutomatically = check.GetBoolean();
            }

            if (updatesElement.TryGetProperty("includePrereleases", out var pre) && pre.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                Updates.IncludePrereleases = pre.GetBoolean();
            }
        }
    }

    public async Task SaveAsync(string configPath = "")
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (string.IsNullOrEmpty(configPath))
            {
                configPath = CONFIG_PATH;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

            var isOwnFile = string.Equals(configPath, CONFIG_PATH, StringComparison.Ordinal);
            var savedNode = BuildModelNode();
            var mergedWithDisk = false;
            if (isOwnFile && _diskSignature != null && _baseNode != null)
            {
                var merged = await TryMergeWithDiskAsync(savedNode).ConfigureAwait(false);
                if (merged != null)
                {
                    savedNode = merged;
                    mergedWithDisk = true;
                }
            }

            if (!mergedWithDisk && _rawRoot != null)
            {
                RestoreUnknownMembers(savedNode, _rawRoot);
            }

            var jsonString = savedNode.ToJsonString(_writeOptions);

            var tempPath = ConfigTempPath(configPath);
            try
            {
                await File.WriteAllTextAsync(tempPath, jsonString).ConfigureAwait(false);
                await SharedFile.MoveOverwriteAsync(tempPath, configPath).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }

            if (isOwnFile)
            {
                _rawRoot = savedNode.DeepClone() as JsonObject;
                _diskSignature = ConfigFileSignature.Of(jsonString, SafeLastWriteUtc());
                if (mergedWithDisk)
                {
                    // Other processes' edits are part of the file now: the model must show them, or the next save would revert them.
                    ApplyLoadedRoot(savedNode);
                }

                _baseNode = BuildModelNode();
            }

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

    /// <summary>A temp file name no other writer (GUI, service, CLI) can share: <c>config.json.&lt;pid&gt;.&lt;guid&gt;.tmp</c>.</summary>
    internal static string ConfigTempPath(string configPath) => SharedFile.UniqueTempPath(configPath);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file is harmless; the original failure is what matters.
        }
    }

    /// <summary>
    /// When config.json changed on disk since this instance loaded or wrote it, returns the document to write: the file as it is
    /// now with only this instance's own changes (model versus load-time snapshot) applied on top. Null when the file is unchanged
    /// (write the model as before). Throws when the combination would be invalid, instead of overwriting someone's edit.
    /// </summary>
    private async Task<JsonObject?> TryMergeWithDiskAsync(JsonObject mine)
    {
        if (!File.Exists(CONFIG_PATH))
        {
            return null;
        }

        var diskText = await SharedFile.ReadAllTextAsync(CONFIG_PATH).ConfigureAwait(false);
        if (ConfigFileSignature.Of(diskText, DateTime.MinValue).SameContent(_diskSignature))
        {
            return null;
        }

        JsonObject theirsRaw;
        try
        {
            theirsRaw = JsonNode.Parse(diskText) as JsonObject ?? throw new InvalidOperationException("Configuration root must be a JSON object.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException($"config.json was changed outside ReStore and is not valid JSON now ({ex.Message}); nothing was saved. Fix or restore the file, then reload.", ex);
        }

        // Bring the on-disk document to the same shape the model writes (defaults, migrations, normalised values) so that
        // "unchanged" is decided on values and not on formatting.
        ConfigSchemaManager.Migrate(theirsRaw);
        var probe = new ConfigManager(new ConfigPortability.SilentLogger(), CONFIG_PATH);
        probe.ApplyLoadedRoot((JsonObject)theirsRaw.DeepClone());
        var theirs = probe.BuildModelNode();

        var conflicts = new List<string>();
        var merged = ConfigMerger.Merge(_baseNode!, mine, theirs, conflicts);
        RestoreUnknownMembers(merged, theirsRaw);

        var introduced = ConfigSchemaValidator.Validate(merged).Except(ConfigSchemaValidator.Validate(theirsRaw)).ToList();
        if (introduced.Count == 0)
        {
            var before = await ConfigPortability.ValidateCandidateAsync(theirsRaw, null, "current").ConfigureAwait(false);
            var after = await ConfigPortability.ValidateCandidateAsync(merged, null, "merged").ConfigureAwait(false);
            introduced = [.. after.Errors.Except(before.Errors)];
        }

        if (introduced.Count > 0)
        {
            throw new InvalidOperationException(
                $"config.json was changed outside ReStore and combining that with your changes is not valid ({introduced[0]}); nothing was saved. Reload the configuration and try again.");
        }

        if (conflicts.Count > 0)
        {
            logger.Log(
                $"config.json was changed outside ReStore and the same setting was changed here: keeping the newer change for {string.Join(", ", conflicts)}.",
                LogLevel.Warning);
        }

        logger.Log("config.json was changed outside ReStore (for example by the CLI); those changes were kept and merged with this save.", LogLevel.Info);
        return merged;
    }

    /// <summary>The model as a config document (without members this version does not know).</summary>
    private JsonObject BuildModelNode()
    {
        var configObject = new
        {
            configSchemaVersion = ConfigSchemaManager.CURRENT_CONFIG_SCHEMA_VERSION,
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
                maxAgeDays = Retention.MaxAgeDays,
                keepHourly = Retention.KeepHourly,
                keepDaily = Retention.KeepDaily,
                keepWeekly = Retention.KeepWeekly,
                keepMonthly = Retention.KeepMonthly,
                keepYearly = Retention.KeepYearly,
                mode = Retention.Mode
            },
            chunkDiffing = new
            {
                manifestVersion = ChunkDiffing.ManifestVersion,
                compression = ChunkDiffing.Compression,
                minChunkSizeKB = ChunkDiffing.MinChunkSizeKB,
                targetChunkSizeKB = ChunkDiffing.TargetChunkSizeKB,
                maxChunkSizeKB = ChunkDiffing.MaxChunkSizeKB,
                rollingHashWindowSize = ChunkDiffing.RollingHashWindowSize,
                maxChunksPerFile = ChunkDiffing.MaxChunksPerFile,
                maxFilesPerSnapshot = ChunkDiffing.MaxFilesPerSnapshot
            },
            verification = new
            {
                enabled = Verification.Enabled,
                verifyInterval = Verification.VerifyInterval.ToString(),
                localStorageOnly = Verification.LocalStorageOnly,
                snapshotsPerRun = Verification.SnapshotsPerRun,
                mode = Verification.Mode,
                samplePercent = Verification.SamplePercent
            },
            notifications = new
            {
                enabled = Notifications.Enabled,
                notifyOnBackupFailure = Notifications.NotifyOnBackupFailure,
                notifyOnVerificationFailure = Notifications.NotifyOnVerificationFailure,
                notifyOnRecovery = Notifications.NotifyOnRecovery,
                notifyOnEveryBackupSuccess = Notifications.NotifyOnEveryBackupSuccess
            },
            logging = new
            {
                level = Logging.Level
            },
            protection = new
            {
                autoStart = Protection.AutoStart
            },
            telemetry = new
            {
                enabled = Telemetry.Enabled,
                crashReports = ReStore.Core.src.telemetry.CrashReportMode.Normalize(Telemetry.CrashReports) ?? ReStore.Core.src.telemetry.CrashReportMode.Ask
            },
            updates = new
            {
                checkAutomatically = Updates.CheckAutomatically,
                includePrereleases = Updates.IncludePrereleases
            },
            watcher = new
            {
                minSnapshotInterval = Watcher.MinSnapshotInterval.ToString(),
                maxDebounceWait = Watcher.MaxDebounceWait.ToString()
            },
            backup = new
            {
                deepScanIntervalDays = BackupSettings.DeepScanIntervalDays,
                hydrateCloudFiles = BackupSettings.HydrateCloudFiles
            },
            systemBackup = new
            {
                enabled = SystemBackup.Enabled,
                includePrograms = SystemBackup.IncludePrograms,
                includeEnvironmentVariables = SystemBackup.IncludeEnvironmentVariables,
                includeWindowsSettings = SystemBackup.IncludeWindowsSettings,
                includeDevTools = SystemBackup.IncludeDevTools,
                includeSshConfig = SystemBackup.IncludeSshConfig,
                backupInterval = SystemBackup.BackupInterval.ToString(),
                schedule = string.IsNullOrWhiteSpace(SystemBackup.Schedule) ? null : SystemBackup.Schedule,
                excludeSystemPrograms = SystemBackup.ExcludeSystemPrograms,
                storageType = SystemBackup.StorageType,
                programsStorageType = SystemBackup.ProgramsStorageType,
                environmentStorageType = SystemBackup.EnvironmentStorageType,
                settingsStorageType = SystemBackup.SettingsStorageType,
                devToolsStorageType = SystemBackup.DevToolsStorageType
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
            excludePresets = ExcludePresets,
            excludeGlobs = ExcludeGlobs,
            storageSources = StorageSources.ToDictionary(
                source => source.Key,
                source => source.Value,
                StringComparer.OrdinalIgnoreCase)
        };

        var savedNode = JsonSerializer.SerializeToNode(configObject, _writeOptions) as JsonObject
            ?? throw new InvalidOperationException("Configuration did not serialise to a JSON object.");
        if (BackupWindow != null)
        {
            savedNode["backupWindow"] = JsonSerializer.SerializeToNode(BackupWindow, _writeOptions);
        }

        // Optional members are written only when set (a null would read as "removed" and be restored from the file below).
        if (savedNode["systemBackup"] is JsonObject savedSystem && string.IsNullOrWhiteSpace(SystemBackup.Schedule))
        {
            savedSystem.Remove("schedule");
        }

        return savedNode;
    }

    /// <summary>
    /// Copies members present in the file that was loaded but not produced by the model (keys written by a
    /// newer version or added by hand) into <paramref name="saved"/>, recursing into sections both share.
    /// <c>watchDirectories</c> and <c>storageSources</c> are skipped: the model owns their contents and
    /// carries unknown members of their entries through <c>[JsonExtensionData]</c>; <c>backupWindow</c> likewise.
    /// </summary>
    private static void RestoreUnknownMembers(JsonObject saved, JsonObject original, string path = "")
    {
        foreach (var (key, originalValue) in original)
        {
            var keyPath = path.Length == 0 ? key : $"{path}.{key}";
            if (ModelOwnedMembers.Contains(keyPath))
            {
                continue;
            }

            if (!saved.TryGetPropertyValue(key, out var savedValue))
            {
                saved[key] = originalValue?.DeepClone();
            }
            else if (savedValue is JsonObject savedChild && originalValue is JsonObject originalChild)
            {
                RestoreUnknownMembers(savedChild, originalChild, keyPath);
            }
        }
    }

    // Members the model writes only when set: absent from the saved document means "removed", not "unknown".
    private static readonly HashSet<string> ModelOwnedMembers = new(StringComparer.Ordinal)
    {
        "watchDirectories", "storageSources", "backupWindow", "systemBackup.schedule"
    };

    private async Task<string> PersistMigratedConfigurationAsync(string originalJson, JsonObject migratedRoot)
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var backupPath = BuildMigrationBackupPath();
            await File.WriteAllTextAsync(backupPath, originalJson).ConfigureAwait(false);
            ConfigBackups.Prune(CONFIG_PATH);

            var migratedJson = migratedRoot.ToJsonString(_writeOptions);
            _lastMigratedText = migratedJson;
            var tempPath = ConfigTempPath(CONFIG_PATH);
            try
            {
                await File.WriteAllTextAsync(tempPath, migratedJson).ConfigureAwait(false);
                await SharedFile.MoveOverwriteAsync(tempPath, CONFIG_PATH).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }

            return backupPath;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private string BuildMigrationBackupPath()
    {
        var configDir = Path.GetDirectoryName(CONFIG_PATH)!;
        var backupDir = Path.Combine(configDir, "backups");
        Directory.CreateDirectory(backupDir);

        return Path.Combine(
            backupDir,
            $"config.pre-migration.{DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture)}_{Guid.NewGuid():N}.json");
    }

    private async Task CreateDefaultConfigAsync()
    {
        var configDir = Path.GetDirectoryName(CONFIG_PATH)!;
        Directory.CreateDirectory(configDir);

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
                Path = "%USERPROFILE%\\Desktop",
                StorageType = null
            },
            new WatchDirectoryConfig
            {
                Path = "%USERPROFILE%\\Documents",
                StorageType = null
            },
            new WatchDirectoryConfig
            {
                Path = "%USERPROFILE%\\Pictures",
                StorageType = null
            }
        ];

        GlobalStorageType = "local";

        StorageSources = new Dictionary<string, StorageConfig>
        {
            ["local"] = new StorageConfig
            {
                Path = "%USERPROFILE%\\ReStoreBackups",
                Options = new Dictionary<string, string>
                {
                    ["path"] = "%USERPROFILE%\\ReStoreBackups"
                }
            }
        };

        SetDefaultExcludedPatterns();
        SetDefaultExcludedPaths();
        ExcludePresets = [ExcludePresetCatalog.OfficeTemp, ExcludePresetCatalog.System];

        await SaveAsync().ConfigureAwait(false);
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
            "%TEMP%",
            "%LOCALAPPDATA%\\Temp",
            "%WINDIR%",
            "%ProgramFiles%",
            "%ProgramFiles(x86)%"
        ];
    }

    /// <summary>Expands <c>%VAR%</c> references (with fallbacks for common ones). Call at use, never before saving.</summary>
    public static string ExpandConfiguredPath(string value)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value);
        return WindowsEnvironmentVariablePattern().Replace(expanded, match =>
        {
            var variableName = match.Groups[1].Value;
            return ResolveEnvironmentVariable(variableName) ?? match.Value;
        });
    }

    private static string? ResolveEnvironmentVariable(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (!string.IsNullOrEmpty(value))
        {
            return value;
        }

        return variableName.ToUpperInvariant() switch
        {
            "TEMP" or "TMP" => Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            "USERPROFILE" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "LOCALAPPDATA" => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            _ => null
        };
    }

    [GeneratedRegex("%([^%]+)%")]
    private static partial Regex WindowsEnvironmentVariablePattern();

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
        return AppPaths.ConfigFile;
    }
}
