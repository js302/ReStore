namespace ReStore.Core.src.utils;

public class ConfigValidationResult
{
    public bool IsValid { get; set; } = true;
    public List<string> Errors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<string> Info { get; set; } = [];

    public void AddError(string message)
    {
        IsValid = false;
        Errors.Add(message);
    }

    public void AddWarning(string message)
    {
        Warnings.Add(message);
    }

    public void AddInfo(string message)
    {
        Info.Add(message);
    }

    public bool HasIssues => Errors.Count > 0 || Warnings.Count > 0;
}

public class ConfigValidator(ILogger logger)
{
    private readonly ILogger _logger = logger;

    /// <param name="validateWatchDirectories">False for commands that get their folder on the command line (<c>backup &lt;path&gt;</c>):
    /// the configured watch directories (possibly none) are then irrelevant and are not checked.</param>
    public ConfigValidationResult ValidateConfiguration(IConfigManager config, bool validateWatchDirectories = true)
    {
        var result = new ConfigValidationResult();

        _logger.Log("Starting configuration validation...", LogLevel.Debug);

        if (validateWatchDirectories)
        {
            ValidateWatchDirectories(config.WatchDirectories, result);
        }

        ValidateBackupSettings(config, result);
        ValidateSchedules(config, result);
        ValidateStorageSources(config.StorageSources, UsedStorageTypes(config), result);
        ValidateEncryptionSettings(config.Encryption, result);
        ValidateChunkDiffingSettings(config.ChunkDiffing ?? new ChunkDiffingConfig(), result);
        ValidateVerificationSettings(config.Verification ?? new VerificationConfig(), result);
        ValidateLoggingSettings(config.Logging ?? new LoggingConfig(), result);
        ValidateTelemetrySettings(config.Telemetry ?? new TelemetryConfig(), result);
        ValidateWatcherSettings(config.Watcher ?? new WatcherConfig(), result);
        ValidateBackupSettingsSection(config.BackupSettings ?? new BackupSettingsConfig(), result);
        ValidateExclusionSettings(config, result);

        if (result.IsValid)
        {
            _logger.Log("Configuration validation passed", LogLevel.Debug);
        }
        else
        {
            _logger.Log($"Configuration validation failed with {result.Errors.Count} errors", LogLevel.Error);
        }

        return result;
    }

    /// <summary>
    /// Validates only the named storage source. Read-only commands (restore, verify, system-restore) need a
    /// usable storage, not a fully valid config: a missing watch directory must not block a restore.
    /// </summary>
    public ConfigValidationResult ValidateStorageOnly(IConfigManager config, string storageType)
    {
        var result = new ConfigValidationResult();

        if (config.StorageSources == null || !config.StorageSources.TryGetValue(storageType, out var source) || source == null)
        {
            result.AddError($"Storage type '{storageType}' is not configured in config.json.");
            return result;
        }

        if (IsStorageSourceUnconfigured(storageType, source))
        {
            result.AddError($"Storage source '{storageType}' still has empty or placeholder option values.");
            return result;
        }

        ValidateStorageSource(storageType, source, result);
        return result;
    }

    private static void ValidateWatchDirectories(List<WatchDirectoryConfig> watchDirectories, ConfigValidationResult result)
    {
        if (watchDirectories == null || watchDirectories.Count == 0)
        {
            result.AddError("No watch directories specified. At least one directory must be configured for monitoring.");
            return;
        }

        var validDirectories = 0;
        foreach (var watchConfig in watchDirectories)
        {
            if (string.IsNullOrWhiteSpace(watchConfig.Path))
            {
                result.AddWarning("Empty watch directory entry found and will be ignored.");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(watchConfig.Schedule)
                && !ReStore.Core.src.monitoring.BackupSchedule.TryParse(watchConfig.Schedule, out _, out var scheduleError))
            {
                result.AddError($"Watch directory '{watchConfig.Path}': invalid schedule '{watchConfig.Schedule}': {scheduleError}");
            }

            try
            {
                var expandedPath = ConfigManager.ExpandConfiguredPath(watchConfig.Path);
                if (Directory.Exists(expandedPath))
                {
                    validDirectories++;
                    var storageInfo = watchConfig.StorageType != null ? $" (storage: {watchConfig.StorageType})" : " (using global storage)";
                    result.AddInfo($"Watch directory validated: {expandedPath}{storageInfo}");
                }
                else
                {
                    result.AddWarning($"Watch directory does not exist: {expandedPath}");
                }
            }
            catch (Exception ex)
            {
                result.AddError($"Invalid watch directory path '{watchConfig.Path}': {ex.Message}");
            }
        }

        if (validDirectories == 0)
        {
            result.AddError("No valid watch directories found. At least one existing directory is required.");
        }
    }

    /// <summary>The system backup schedule and the backup window (the per-folder schedules are checked with the watch directories).</summary>
    private static void ValidateSchedules(IConfigManager config, ConfigValidationResult result)
    {
        var systemSchedule = config.SystemBackup?.Schedule;
        if (!string.IsNullOrWhiteSpace(systemSchedule)
            && !ReStore.Core.src.monitoring.BackupSchedule.TryParse(systemSchedule, out _, out var scheduleError))
        {
            result.AddError($"systemBackup.schedule: invalid schedule '{systemSchedule}': {scheduleError}");
        }

        if (config.BackupWindow is { } window
            && !ReStore.Core.src.monitoring.BackupWindow.TryCreate(window.Start, window.End, window.Days, out _, out var windowError))
        {
            result.AddError($"backupWindow is invalid: {windowError}");
        }
    }

    private static void ValidateBackupSettings(IConfigManager config, ConfigValidationResult result)
    {
        if (config.BackupInterval <= TimeSpan.Zero)
        {
            result.AddError("Backup interval must be greater than zero.");
        }
        else if (config.BackupInterval < TimeSpan.FromMinutes(1))
        {
            result.AddWarning("Backup interval is less than 1 minute. This may cause excessive system load.");
        }
        else if (config.BackupInterval > TimeSpan.FromDays(7))
        {
            result.AddWarning("Backup interval is greater than 7 days. Consider more frequent backups for data protection.");
        }

        if (config.SizeThresholdMB <= 0)
        {
            result.AddError("Size threshold must be greater than zero.");
        }
        else if (config.SizeThresholdMB < 10)
        {
            result.AddWarning("Size threshold is very low (< 10MB). This may trigger warnings for small directories.");
        }

        if (config.MaxFileSizeMB <= 0)
        {
            result.AddError("Maximum file size must be greater than zero.");
        }
        else if (config.MaxFileSizeMB > 1000)
        {
            result.AddWarning("Maximum file size is very large (> 1GB). Large files may impact backup performance.");
        }

        if (config.MaxFileSizeMB > config.SizeThresholdMB)
        {
            result.AddWarning("Maximum file size is larger than size threshold. This configuration may be inconsistent.");
        }

        if (!Enum.IsDefined(typeof(BackupType), config.BackupType))
        {
            result.AddError($"Invalid backup type: {config.BackupType}");
        }

        if (config.Retention.Enabled)
        {
            if (config.Retention.KeepLastPerDirectory < 1)
            {
                result.AddError("Retention is enabled but keepLastPerDirectory is < 1. At least one backup must be kept.");
            }

            if (config.Retention.MaxAgeDays < 0)
            {
                result.AddError("Retention maxAgeDays cannot be negative.");
            }

            if (config.Retention.MaxAgeDays == 0)
            {
                result.AddInfo("Retention is enabled with maxAgeDays=0 (age-based deletion disabled). Only keepLastPerDirectory will apply.");
            }

            var retention = config.Retention;
            foreach (var (name, value) in new[]
            {
                ("keepHourly", retention.KeepHourly), ("keepDaily", retention.KeepDaily), ("keepWeekly", retention.KeepWeekly),
                ("keepMonthly", retention.KeepMonthly), ("keepYearly", retention.KeepYearly)
            })
            {
                if (value < 0)
                {
                    result.AddError($"Retention {name} cannot be negative (use 0 to turn the tier off).");
                }
            }

            if (!RetentionModes.IsValid(retention.Mode))
            {
                result.AddError($"Retention mode '{retention.Mode}' is invalid. Use 'any' or 'all'.");
            }
            else if (string.Equals(retention.Mode, RetentionModes.All, StringComparison.OrdinalIgnoreCase)
                && retention.MaxAgeDays == 0 && retention.KeepHourly <= 0 && retention.KeepDaily <= 0
                && retention.KeepWeekly <= 0 && retention.KeepMonthly <= 0 && retention.KeepYearly <= 0)
            {
                result.AddWarning("Retention mode 'all' with maxAgeDays=0 and no GFS tiers has only keepLastPerDirectory to apply, so it behaves the same as 'any'.");
            }
            else if (string.Equals(retention.Mode, RetentionModes.All, StringComparison.OrdinalIgnoreCase)
                && (retention.KeepHourly > 0 || retention.KeepDaily > 0 || retention.KeepWeekly > 0 || retention.KeepMonthly > 0 || retention.KeepYearly > 0))
            {
                result.AddInfo("Retention mode 'all' keeps a snapshot only if every enabled rule (keep last, max age and each GFS tier) keeps it; this is much stricter than the default 'any'. Preview it with 'restore retention --dry-run'.");
            }
        }
        else
        {
            result.AddWarning(
                "Retention is disabled. Snapshots are kept indefinitely and storage use will grow without bound. " +
                $"Ready-to-use defaults are keepLastPerDirectory={config.Retention.KeepLastPerDirectory} and maxAgeDays={config.Retention.MaxAgeDays}.");
        }
    }

    /// <summary>
    /// The storage types something actually writes to: the default storage, the storage of each watched folder and, when system backup is
    /// on, its storages. The shipped example lists placeholder sources for every provider; those must not warn on every command.
    /// </summary>
    public static HashSet<string> UsedStorageTypes(IConfigManager config)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? type)
        {
            if (!string.IsNullOrWhiteSpace(type))
            {
                used.Add(type);
            }
        }

        Add(config.GlobalStorageType);
        foreach (var watch in config.WatchDirectories ?? [])
        {
            Add(watch?.StorageType);
        }

        if (config.SystemBackup is { Enabled: true } system)
        {
            Add(system.StorageType);
            Add(system.ProgramsStorageType);
            Add(system.EnvironmentStorageType);
            Add(system.SettingsStorageType);
            Add(system.DevToolsStorageType);
        }

        return used;
    }

    private void ValidateStorageSources(Dictionary<string, StorageConfig> storageSources, HashSet<string> usedStorageTypes, ConfigValidationResult result)
    {
        if (storageSources == null || storageSources.Count == 0)
        {
            result.AddError("No storage sources configured. At least one storage source is required.");
            return;
        }

        var configuredSourcesCount = 0;

        foreach (var kvp in storageSources)
        {
            var sourceName = kvp.Key;
            var config = kvp.Value;

            if (string.IsNullOrWhiteSpace(sourceName))
            {
                result.AddError("Storage source name cannot be empty.");
                continue;
            }

            // With nothing known to be in use every source is checked, as before.
            if (usedStorageTypes.Count > 0 && !usedStorageTypes.Contains(sourceName))
            {
                result.AddInfo($"Storage source '{sourceName}' is not used by the default storage, a watched folder or system backup; it is not validated.");
                continue;
            }

            var wasConfigured = !IsStorageSourceUnconfigured(sourceName, config);
            ValidateStorageSource(sourceName, config, result);

            if (wasConfigured)
            {
                configuredSourcesCount++;
            }
        }

        if (configuredSourcesCount == 0)
        {
            result.AddError("No storage sources are properly configured. At least one storage source (e.g., 'local') must be configured.");
        }
    }

    private void ValidateStorageSource(string sourceName, StorageConfig config, ConfigValidationResult result)
    {
        if (config == null)
        {
            result.AddError($"Storage source '{sourceName}' configuration is null.");
            return;
        }

        // Only Local storage uses the top-level path (as its root); remote providers ignore it (see StorageBase).
        if (string.IsNullOrWhiteSpace(config.Path) && string.Equals(sourceName, "local", StringComparison.OrdinalIgnoreCase))
        {
            result.AddError($"Storage source '{sourceName}' must have a path specified.");
            return;
        }

        if (IsStorageSourceUnconfigured(sourceName, config))
        {
            result.AddInfo($"Storage source '{sourceName}' is not configured (skipped validation).");
            return;
        }

        ValidatePrefixOption(sourceName, config, result);

        switch (sourceName.ToLowerInvariant())
        {
            case "local":
                ValidateLocalStorage(sourceName, config, result);
                break;
            case "gdrive":
                ValidateGoogleDriveStorage(sourceName, config, result);
                break;
            case "s3":
                ValidateS3Storage(sourceName, config, result);
                break;
            case "github":
                ValidateGitHubStorage(sourceName, config, result);
                break;
            case "azure":
                ValidateAzureStorage(sourceName, config, result);
                break;
            case "gcp":
                ValidateGcpStorage(sourceName, config, result);
                break;
            case "dropbox":
                ValidateDropboxStorage(sourceName, config, result);
                break;
            case "sftp":
                ValidateSftpStorage(sourceName, config, result);
                break;
            case "b2":
                ValidateB2Storage(sourceName, config, result);
                break;
            default:
                result.AddWarning($"Unknown storage type '{sourceName}'. Make sure this storage provider is supported.");
                break;
        }
    }

    private static void ValidatePrefixOption(string sourceName, StorageConfig config, ConfigValidationResult result)
    {
        if (config.Options == null || !config.Options.TryGetValue("prefix", out var prefix) || string.IsNullOrWhiteSpace(prefix))
        {
            return;
        }

        try
        {
            ReStore.Core.src.storage.StorageBase.NormalizePrefix(prefix);
        }
        catch (ArgumentException ex)
        {
            result.AddError($"Storage source '{sourceName}' has an invalid 'prefix' option: {ex.Message}");
        }
    }

    /// <summary>Options that may legitimately be empty, so an empty value does not mean "not configured".</summary>
    private static readonly HashSet<string> OptionalOptionKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "prefix", "path", "hostKeyFingerprint", "passphrase", "credentialPath", "token_folder", "backup_folder_name"
    };

    private static bool IsStorageSourceUnconfigured(string sourceName, StorageConfig config)
    {
        if (sourceName.ToLowerInvariant() == "local")
        {
            return false;
        }

        return config.Options.Where(opt => !OptionalOptionKeys.Contains(opt.Key)).Any(opt =>
            string.IsNullOrWhiteSpace(opt.Value) ||
            opt.Value.StartsWith("your_", StringComparison.OrdinalIgnoreCase) ||
            opt.Value.Contains("your_", StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateLocalStorage(string sourceName, StorageConfig config, ConfigValidationResult result)
    {
        try
        {
            // The configured path is the storage root itself, which LocalStorage creates on
            // initialize. Checking its parent instead passed roots that could not be created
            // and rejected drive roots, whose parent is null.
            var expandedPath = Path.GetFullPath(ConfigManager.ExpandConfiguredPath(config.Path));

            if (Directory.Exists(expandedPath))
            {
                result.AddInfo($"Local storage directory validated: {expandedPath}");
                return;
            }

            try
            {
                Directory.CreateDirectory(expandedPath);
                result.AddInfo($"Local storage directory created: {expandedPath}");
            }
            catch (Exception ex)
            {
                result.AddError($"Cannot create local storage directory '{expandedPath}': {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            result.AddError($"Local storage '{sourceName}' path validation failed: {ex.Message}");
        }
    }

    private static void ValidateGoogleDriveStorage(string sourceName, StorageConfig config, ConfigValidationResult result)
    {
        var requiredOptions = new[] { "client_id", "client_secret" };
        var missingOptions = requiredOptions.Where(opt =>
            !config.Options.ContainsKey(opt) ||
            string.IsNullOrWhiteSpace(config.Options[opt]) ||
            config.Options[opt].Contains("your_")).ToList();

        if (missingOptions.Count != 0)
        {
            result.AddWarning($"Google Drive storage '{sourceName}' is not configured (missing: {string.Join(", ", missingOptions)})");
            return;
        }

        if (config.Options.TryGetValue("token_folder", out string? value))
        {
            try
            {
                var tokenFolder = ConfigManager.ExpandConfiguredPath(value);
                var tokenDir = Path.GetDirectoryName(tokenFolder);
                if (!string.IsNullOrEmpty(tokenDir) && !Directory.Exists(tokenDir))
                {
                    Directory.CreateDirectory(tokenDir);
                    result.AddInfo($"Google Drive token directory created: {tokenDir}");
                }
            }
            catch (Exception ex)
            {
                result.AddWarning($"Cannot create Google Drive token directory: {ex.Message}");
            }
        }
    }

    private static void ValidateS3Storage(string sourceName, StorageConfig config, ConfigValidationResult result)
    {
        var requiredOptions = new[] { "accessKeyId", "secretAccessKey", "region", "bucketName" };
        var missingOptions = requiredOptions.Where(opt =>
            !config.Options.ContainsKey(opt) ||
            string.IsNullOrWhiteSpace(config.Options[opt]) ||
            config.Options[opt].Contains("your_")).ToList();

        if (missingOptions.Count != 0)
        {
            result.AddWarning($"S3 storage '{sourceName}' is not configured (missing: {string.Join(", ", missingOptions)})");
            return;
        }

        // serviceUrl points the S3 provider at an S3-compatible service (MinIO, Wasabi, R2, ...): its regions are not AWS regions.
        var hasServiceUrl = config.Options.TryGetValue("serviceUrl", out var serviceUrl) && !string.IsNullOrWhiteSpace(serviceUrl);
        if (hasServiceUrl
            && !(Uri.TryCreate(serviceUrl, UriKind.Absolute, out var endpoint) && endpoint.Scheme is "http" or "https"))
        {
            result.AddError($"S3 storage '{sourceName}' has an invalid serviceUrl: '{serviceUrl}' (expected an http:// or https:// URL)");
        }

        if (!hasServiceUrl && config.Options.TryGetValue("region", out string? region) && !region.Contains("your_"))
        {
            if (!IsValidAwsRegion(region))
            {
                result.AddWarning($"S3 storage '{sourceName}' has potentially invalid AWS region: {region}");
            }
        }

        if (config.Options.TryGetValue("bucketName", out string? bucketName) && !bucketName.Contains("your_"))
        {
            if (!IsValidS3BucketName(bucketName))
            {
                result.AddError($"S3 storage '{sourceName}' has invalid bucket name: {bucketName}");
            }
        }
    }

    private static void ValidateGitHubStorage(string sourceName, StorageConfig config, ConfigValidationResult result)
    {
        var requiredOptions = new[] { "token", "repo", "owner" };
        var missingOptions = requiredOptions.Where(opt =>
            !config.Options.ContainsKey(opt) ||
            string.IsNullOrWhiteSpace(config.Options[opt]) ||
            config.Options[opt].Contains("your_")).ToList();

        if (missingOptions.Count != 0)
        {
            result.AddWarning($"GitHub storage '{sourceName}' is not configured (missing: {string.Join(", ", missingOptions)})");
            return;
        }
    }

    private static void ValidateAzureStorage(string sourceName, StorageConfig config, ConfigValidationResult result)
    {
        var requiredOptions = new[] { "connectionString", "containerName" };
        var missingOptions = requiredOptions.Where(opt =>
            !config.Options.ContainsKey(opt) ||
            string.IsNullOrWhiteSpace(config.Options[opt]) ||
            config.Options[opt].Contains("your_")).ToList();

        if (missingOptions.Count != 0)
        {
            result.AddWarning($"Azure storage '{sourceName}' is not configured (missing: {string.Join(", ", missingOptions)})");
            return;
        }
    }

    private static void ValidateGcpStorage(string sourceName, StorageConfig config, ConfigValidationResult result)
    {
        var requiredOptions = new[] { "bucketName" };
        var missingOptions = requiredOptions.Where(opt =>
            !config.Options.ContainsKey(opt) ||
            string.IsNullOrWhiteSpace(config.Options[opt]) ||
            config.Options[opt].Contains("your_")).ToList();

        if (missingOptions.Count != 0)
        {
            result.AddWarning($"GCP storage '{sourceName}' is not configured (missing: {string.Join(", ", missingOptions)})");
            return;
        }

        if (config.Options.TryGetValue("credentialPath", out string? path))
        {
            if (!string.IsNullOrWhiteSpace(path) && !File.Exists(ConfigManager.ExpandConfiguredPath(path)))
            {
                result.AddWarning($"GCP credential file not found: {path}");
            }
        }
    }

    private static void ValidateDropboxStorage(string sourceName, StorageConfig config, ConfigValidationResult result)
    {
        // Dropbox needs either accessToken OR (appKey + appSecret + refreshToken)
        var hasAccessToken = config.Options.ContainsKey("accessToken") && !string.IsNullOrWhiteSpace(config.Options["accessToken"]);
        var hasRefreshToken = config.Options.ContainsKey("refreshToken") && !string.IsNullOrWhiteSpace(config.Options["refreshToken"]);
        var hasAppKey = config.Options.ContainsKey("appKey") && !string.IsNullOrWhiteSpace(config.Options["appKey"]);
        var hasAppSecret = config.Options.ContainsKey("appSecret") && !string.IsNullOrWhiteSpace(config.Options["appSecret"]);

        if (!hasAccessToken && !(hasRefreshToken && hasAppKey && hasAppSecret))
        {
            result.AddWarning($"Dropbox storage '{sourceName}' is not configured correctly. Requires either 'accessToken' OR ('refreshToken', 'appKey', and 'appSecret').");
        }
    }

    private static void ValidateSftpStorage(string sourceName, StorageConfig config, ConfigValidationResult result)
    {
        var requiredOptions = new[] { "host", "username" };
        var missingOptions = requiredOptions.Where(opt =>
            !config.Options.ContainsKey(opt) ||
            string.IsNullOrWhiteSpace(config.Options[opt]) ||
            config.Options[opt].Contains("your_")).ToList();

        if (missingOptions.Count != 0)
        {
            result.AddWarning($"SFTP storage '{sourceName}' is not configured (missing: {string.Join(", ", missingOptions)})");
            return;
        }

        // Needs password OR privateKeyPath
        var hasPassword = config.Options.ContainsKey("password") && !string.IsNullOrWhiteSpace(config.Options["password"]);
        var hasKey = config.Options.ContainsKey("privateKeyPath") && !string.IsNullOrWhiteSpace(config.Options["privateKeyPath"]);

        if (!hasPassword && !hasKey)
        {
            result.AddWarning($"SFTP storage '{sourceName}' requires either 'password' or 'privateKeyPath'.");
        }

        if (hasKey && !File.Exists(ConfigManager.ExpandConfiguredPath(config.Options["privateKeyPath"])))
        {
            result.AddWarning($"SFTP private key file not found: {config.Options["privateKeyPath"]}");
        }

        if (config.Options.TryGetValue("hostKeyFingerprint", out var hostKeyFingerprint) &&
            !string.IsNullOrWhiteSpace(hostKeyFingerprint) &&
            !ReStore.Core.src.storage.sftp.SftpHostKey.IsValid(hostKeyFingerprint))
        {
            result.AddWarning($"SFTP storage '{sourceName}' has an invalid 'hostKeyFingerprint'; expected a SHA256 fingerprint such as 'SHA256:AbC...' (as printed by ssh-keygen -lf).");
        }
    }

    private static void ValidateB2Storage(string sourceName, StorageConfig config, ConfigValidationResult result)
    {
        var requiredOptions = new[] { "keyId", "applicationKey", "bucketName" };
        var missingOptions = requiredOptions.Where(opt =>
            !config.Options.ContainsKey(opt) ||
            string.IsNullOrWhiteSpace(config.Options[opt]) ||
            config.Options[opt].Contains("your_")).ToList();

        if (missingOptions.Count != 0)
        {
            result.AddWarning($"Backblaze B2 storage '{sourceName}' is not configured (missing: {string.Join(", ", missingOptions)})");
            return;
        }
    }

    private static void ValidateExclusionSettings(IConfigManager config, ConfigValidationResult result)
    {
        if (config.ExcludedPatterns != null)
        {
            foreach (var pattern in config.ExcludedPatterns)
            {
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    result.AddWarning("Empty exclusion pattern found and will be ignored.");
                    continue;
                }

                try
                {
                    _ = ExcludePattern.ParseLegacy(pattern);
                }
                catch (Exception)
                {
                    result.AddWarning($"Potentially invalid exclusion pattern: {pattern}");
                }
            }
        }

        foreach (var glob in config.ExcludeGlobs ?? [])
        {
            if (string.IsNullOrWhiteSpace(glob) || ExcludePattern.Parse(glob) == null)
            {
                result.AddWarning("Empty exclusion glob found and will be ignored.");
            }
        }

        foreach (var preset in config.ExcludePresets ?? [])
        {
            if (!ExcludePresetCatalog.IsKnown(preset))
            {
                result.AddWarning($"Unknown exclude preset '{preset}' will be ignored (known: {string.Join(", ", ExcludePresetCatalog.Names)}).");
            }
        }

        ValidateDestinationOverlap(config, result);

        if (config.ExcludedPaths != null)
        {
            foreach (var path in config.ExcludedPaths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    result.AddWarning("Empty exclusion path found and will be ignored.");
                    continue;
                }

                try
                {
                    var expandedPath = ConfigManager.ExpandConfiguredPath(path);
                    if (Directory.Exists(expandedPath))
                    {
                        result.AddInfo($"Exclusion path validated: {expandedPath}");
                    }
                    else
                    {
                        result.AddInfo($"Exclusion path does not exist (will be ignored): {expandedPath}");
                    }
                }
                catch (Exception ex)
                {
                    result.AddWarning($"Invalid exclusion path '{path}': {ex.Message}");
                }
            }
        }
    }

    /// <summary>A destination (or ReStore's data directory) inside a watched folder is excluded automatically; one that contains a watched folder cannot be.</summary>
    private static void ValidateDestinationOverlap(IConfigManager config, ConfigValidationResult result)
    {
        if (config.WatchDirectories == null || config.StorageSources == null)
        {
            return;
        }

        var watched = new List<string>();
        foreach (var watch in config.WatchDirectories)
        {
            if (string.IsNullOrWhiteSpace(watch.Path))
            {
                continue;
            }

            try
            {
                watched.Add(Path.GetFullPath(watch.ExpandedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch (Exception)
            {
                // Reported by the watch directory checks.
            }
        }

        foreach (var destination in FileSelectionService.GetDestinationRoots(config))
        {
            foreach (var watch in watched)
            {
                if (IsSameOrInside(watch, destination))
                {
                    result.AddWarning($"Watched folder '{watch}' is inside the backup destination '{destination}'; this would back up the backup, so the destination is not excluded automatically. Move one of them.");
                }
                else if (IsSameOrInside(destination, watch))
                {
                    result.AddWarning($"The backup destination '{destination}' is inside the watched folder '{watch}'; it is excluded from backups automatically.");
                }
            }
        }
    }

    private static bool IsSameOrInside(string path, string root)
        => path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void ValidateEncryptionSettings(EncryptionConfig encryption, ConfigValidationResult result)
    {
        if (!encryption.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(encryption.Salt))
        {
            result.AddError("Encryption is enabled but no salt is configured.");
        }
        else
        {
            try
            {
                var saltBytes = Convert.FromBase64String(encryption.Salt);
                if (saltBytes.Length == 0)
                {
                    result.AddError("Encryption salt cannot be empty.");
                }
            }
            catch (FormatException)
            {
                result.AddError("Encryption salt must be a valid Base64 string.");
            }
        }

        if (encryption.KeyDerivationIterations < 1)
        {
            result.AddError("Encryption key derivation iterations must be greater than zero.");
        }

        if (!string.IsNullOrWhiteSpace(encryption.VerificationToken))
        {
            try
            {
                _ = Convert.FromBase64String(encryption.VerificationToken);
            }
            catch (FormatException)
            {
                result.AddError("Encryption verification token must be a valid Base64 string.");
            }
        }
    }

    private static void ValidateWatcherSettings(WatcherConfig watcher, ConfigValidationResult result)
    {
        if (watcher.MinSnapshotInterval < TimeSpan.Zero)
        {
            result.AddError("watcher.minSnapshotInterval must not be negative (use 00:00:00 to disable the minimum).");
        }
        else if (watcher.MinSnapshotInterval > TimeSpan.FromDays(1))
        {
            result.AddWarning("watcher.minSnapshotInterval is longer than 1 day; watched changes will be backed up very late.");
        }

        if (watcher.MaxDebounceWait < TimeSpan.Zero)
        {
            result.AddError("watcher.maxDebounceWait must not be negative (use 00:00:00 to disable the cap).");
        }
        else if (watcher.MaxDebounceWait > TimeSpan.FromHours(1))
        {
            result.AddWarning("watcher.maxDebounceWait is longer than 1 hour; a continuously changing folder will be backed up very late.");
        }
    }

    private static void ValidateBackupSettingsSection(BackupSettingsConfig backup, ConfigValidationResult result)
    {
        if (backup.DeepScanIntervalDays < 0)
        {
            result.AddError("backup.deepScanIntervalDays must not be negative (use 0 to deep-scan on every run).");
        }
        else if (backup.DeepScanIntervalDays > 90)
        {
            result.AddWarning("backup.deepScanIntervalDays is longer than 90 days; a change that preserved a file's size and timestamps could go unnoticed for that long.");
        }
    }

    private static void ValidateLoggingSettings(LoggingConfig logging, ConfigValidationResult result)
    {
        if (!LoggingConfig.TryParseLevel(logging.Level, out _))
        {
            result.AddError($"logging.level '{logging.Level}' is not valid. Use Debug, Info, Warning or Error.");
        }
    }

    private static void ValidateTelemetrySettings(TelemetryConfig telemetry, ConfigValidationResult result)
    {
        if (ReStore.Core.src.telemetry.CrashReportMode.Normalize(telemetry.CrashReports) == null)
        {
            result.AddError($"telemetry.crashReports '{telemetry.CrashReports}' is not valid. Use ask, always or never.");
        }
    }

    private static void ValidateVerificationSettings(VerificationConfig verification, ConfigValidationResult result)
    {
        if (!verification.Enabled)
        {
            result.AddInfo("Scheduled verification is disabled. Backups are not periodically checked for bit-rot or dropped objects.");
            return;
        }

        if (verification.VerifyInterval <= TimeSpan.Zero)
        {
            result.AddError("verification.verifyInterval must be greater than zero when verification is enabled.");
            return;
        }

        if (verification.VerifyInterval < TimeSpan.FromHours(1))
        {
            result.AddWarning("verification.verifyInterval is less than 1 hour. Verification re-downloads every unique chunk and is expensive.");
        }

        if (!verification.LocalStorageOnly)
        {
            result.AddWarning(
                "Scheduled verification is enabled for remote providers. Every run downloads and decrypts all unique chunks, which incurs egress cost on metered storage.");
        }

        if (verification.SnapshotsPerRun < 1)
        {
            result.AddError("verification.snapshotsPerRun must be at least 1.");
        }
        else if (verification.SnapshotsPerRun > 10)
        {
            result.AddWarning(
                $"verification.snapshotsPerRun is {verification.SnapshotsPerRun}. Each snapshot re-downloads every unique chunk it references, so a high value multiplies the transfer cost of one cycle.");
        }

        if (!ReStore.Core.src.core.VerificationModes.TryParse(verification.Mode, out _))
        {
            result.AddError($"verification.mode '{verification.Mode}' is not valid. Use full, sample or quick.");
        }

        if (verification.SamplePercent is < 1 or > 100)
        {
            result.AddError("verification.samplePercent must be between 1 and 100.");
        }
    }

    private static void ValidateChunkDiffingSettings(ChunkDiffingConfig chunkDiffing, ConfigValidationResult result)
    {
        if (chunkDiffing.ManifestVersion is not (0 or 2 or 3))
        {
            result.AddError("chunkDiffing.manifestVersion must be 2 or 3 for chunk snapshot manifests.");
        }

        if (!string.Equals(chunkDiffing.Compression, "auto", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(chunkDiffing.Compression, "off", StringComparison.OrdinalIgnoreCase))
        {
            result.AddError("chunkDiffing.compression must be \"auto\" or \"off\".");
        }

        if (chunkDiffing.MinChunkSizeKB < 1)
        {
            result.AddError("chunkDiffing.minChunkSizeKB must be greater than zero.");
        }

        if (chunkDiffing.TargetChunkSizeKB < 1)
        {
            result.AddError("chunkDiffing.targetChunkSizeKB must be greater than zero.");
        }

        if (chunkDiffing.MaxChunkSizeKB < 1)
        {
            result.AddError("chunkDiffing.maxChunkSizeKB must be greater than zero.");
        }

        if (chunkDiffing.MinChunkSizeKB > chunkDiffing.TargetChunkSizeKB)
        {
            result.AddError("chunkDiffing.minChunkSizeKB must be less than or equal to targetChunkSizeKB.");
        }

        if (chunkDiffing.TargetChunkSizeKB > chunkDiffing.MaxChunkSizeKB)
        {
            result.AddError("chunkDiffing.targetChunkSizeKB must be less than or equal to maxChunkSizeKB.");
        }

        if (chunkDiffing.RollingHashWindowSize < 8 || chunkDiffing.RollingHashWindowSize > 512)
        {
            result.AddError("chunkDiffing.rollingHashWindowSize must be between 8 and 512.");
        }

        if (chunkDiffing.MaxChunksPerFile < 1)
        {
            result.AddError("chunkDiffing.maxChunksPerFile must be greater than zero.");
        }

        if (chunkDiffing.MaxFilesPerSnapshot < 1)
        {
            result.AddError("chunkDiffing.maxFilesPerSnapshot must be greater than zero.");
        }

        if (chunkDiffing.MaxChunkSizeKB > 8192)
        {
            result.AddWarning("chunkDiffing.maxChunkSizeKB is very large (> 8MB) and may reduce deduplication effectiveness.");
        }

        if (chunkDiffing.MaxFilesPerSnapshot > 1_000_000)
        {
            result.AddWarning("chunkDiffing.maxFilesPerSnapshot is very large (> 1,000,000) and may increase manifest processing time.");
        }
    }

    private static bool IsValidAwsRegion(string region)
    {
        if (string.IsNullOrWhiteSpace(region)) return false;

        var validRegions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // US
            "us-east-1", "us-east-2", "us-west-1", "us-west-2",

            // Africa
            "af-south-1",

            // Asia Pacific
            "ap-east-1", "ap-east-2",
            "ap-south-1", "ap-south-2",
            "ap-southeast-1", "ap-southeast-2", "ap-southeast-3", "ap-southeast-4", "ap-southeast-5", "ap-southeast-6", "ap-southeast-7",
            "ap-northeast-1", "ap-northeast-2", "ap-northeast-3",

            // Canada
            "ca-central-1", "ca-west-1",

            // Europe
            "eu-central-1", "eu-central-2",
            "eu-west-1", "eu-west-2", "eu-west-3",
            "eu-south-1", "eu-south-2",
            "eu-north-1",

            // Israel
            "il-central-1",

            // Mexico
            "mx-central-1",

            // Middle East
            "me-south-1", "me-central-1",

            // South America
            "sa-east-1"
        };

        // Prefer explicit known list, but keep a conservative prefix fallback for future regions
        return validRegions.Contains(region.Trim())
               || region.StartsWith("us-", StringComparison.OrdinalIgnoreCase)
               || region.StartsWith("eu-", StringComparison.OrdinalIgnoreCase)
               || region.StartsWith("ap-", StringComparison.OrdinalIgnoreCase)
               || region.StartsWith("sa-", StringComparison.OrdinalIgnoreCase)
               || region.StartsWith("ca-", StringComparison.OrdinalIgnoreCase)
               || region.StartsWith("af-", StringComparison.OrdinalIgnoreCase)
               || region.StartsWith("il-", StringComparison.OrdinalIgnoreCase)
               || region.StartsWith("mx-", StringComparison.OrdinalIgnoreCase)
               || region.StartsWith("me-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidS3BucketName(string bucketName)
    {
        if (string.IsNullOrWhiteSpace(bucketName) || bucketName.Length < 3 || bucketName.Length > 63)
            return false;

        if (bucketName.StartsWith('-') || bucketName.EndsWith('-') || bucketName.StartsWith('.') || bucketName.EndsWith('.'))
            return false;

        if (bucketName.Contains("..") || bucketName.Contains(".-") || bucketName.Contains("-."))
            return false;

        return bucketName.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '.');
    }
}
