using System.Text.Json.Nodes;

namespace ReStore.Core.src.utils;

public sealed class ConfigMigrationResult(int sourceSchemaVersion, int targetSchemaVersion)
{
    public int SourceSchemaVersion { get; } = sourceSchemaVersion;
    public int TargetSchemaVersion { get; internal set; } = targetSchemaVersion;
    public string? BackupPath { get; set; }
    public List<string> AppliedMigrations { get; } = [];
    public List<string> Warnings { get; } = [];

    public bool MigrationApplied => AppliedMigrations.Count > 0;

    public void AddMigration(string description)
    {
        AppliedMigrations.Add(description);
    }

    public void AddWarning(string warning)
    {
        Warnings.Add(warning);
    }
}

public static class ConfigSchemaManager
{
    public const int CURRENT_CONFIG_SCHEMA_VERSION = 4;

    public static ConfigMigrationResult Migrate(JsonObject configRoot)
    {
        ArgumentNullException.ThrowIfNull(configRoot);

        var sourceSchemaVersion = ReadSchemaVersion(configRoot);
        if (sourceSchemaVersion > CURRENT_CONFIG_SCHEMA_VERSION)
        {
            var unsupportedResult = new ConfigMigrationResult(sourceSchemaVersion, sourceSchemaVersion);
            unsupportedResult.AddWarning(
                $"Configuration schema version {sourceSchemaVersion} is newer than this binary supports ({CURRENT_CONFIG_SCHEMA_VERSION}).");
            return unsupportedResult;
        }

        var migrationResult = new ConfigMigrationResult(sourceSchemaVersion, sourceSchemaVersion);
        var workingSchemaVersion = sourceSchemaVersion;

        // Version-gated steps handle genuine reshaping: renames and value remappings that
        // are only meaningful when coming from an older layout.
        if (workingSchemaVersion < 2)
        {
            ApplyMigrationToSchemaV2(configRoot, migrationResult);
            workingSchemaVersion = 2;
        }

        if (workingSchemaVersion < 3)
        {
            ApplyMigrationToSchemaV3(configRoot, migrationResult);
            workingSchemaVersion = 3;
        }

        // Missing-section defaults run on every load, regardless of version. A config already
        // stamped with the current version can still be missing a block — hand-edited,
        // truncated, or written by a build that predates it — and a version-gated repair
        // would silently skip it forever.
        EnsureCurrentDefaults(configRoot, migrationResult);

        if (!HasExactIntValue(configRoot, "configSchemaVersion", CURRENT_CONFIG_SCHEMA_VERSION))
        {
            configRoot["configSchemaVersion"] = CURRENT_CONFIG_SCHEMA_VERSION;
            migrationResult.AddMigration($"Set configSchemaVersion to {CURRENT_CONFIG_SCHEMA_VERSION}.");
        }

        migrationResult.TargetSchemaVersion = CURRENT_CONFIG_SCHEMA_VERSION;
        return migrationResult;
    }

    /// <summary>
    /// Injects any missing configuration block with its documented default. Idempotent: a
    /// complete config passes through untouched and reports no migration.
    /// </summary>
    private static void EnsureCurrentDefaults(JsonObject configRoot, ConfigMigrationResult migrationResult)
    {
        if (EnsureChunkDiffingDefaults(configRoot))
        {
            migrationResult.AddMigration("Added missing chunkDiffing configuration defaults.");
        }

        if (EnsureGlobalStorageType(configRoot))
        {
            migrationResult.AddMigration("Added missing globalStorageType.");
        }

        if (EnsureRetentionDefaults(configRoot))
        {
            migrationResult.AddMigration("Added missing retention configuration defaults.");
        }

        if (EnsureEncryptionIterationDefault(configRoot))
        {
            migrationResult.AddMigration("Repaired encryption.keyDerivationIterations to a safe default.");
        }

        if (EnsureSystemBackupWindowsSettingsDefault(configRoot))
        {
            migrationResult.AddMigration("Added systemBackup.includeWindowsSettings default value.");
        }

        if (EnsureVerificationDefaults(configRoot))
        {
            migrationResult.AddMigration("Added scheduled verification configuration defaults (disabled).");
        }

        if (EnsureNotificationDefaults(configRoot))
        {
            migrationResult.AddMigration("Added notification configuration defaults.");
        }

        if (EnsureLoggingDefaults(configRoot))
        {
            migrationResult.AddMigration("Added logging.level default (Info).");
        }

        if (EnsureProtectionDefaults(configRoot))
        {
            migrationResult.AddMigration("Added protection.autoStart default (true).");
        }

        if (EnsureTelemetryDefaults(configRoot))
        {
            migrationResult.AddMigration("Added telemetry.enabled (true) and telemetry.crashReports (ask) defaults.");
        }

        if (EnsureUpdatesDefaults(configRoot))
        {
            migrationResult.AddMigration("Added updates.checkAutomatically (true) and updates.includePrereleases (false) defaults.");
        }

        if (EnsureWatcherDefaults(configRoot))
        {
            migrationResult.AddMigration("Added watcher.minSnapshotInterval / watcher.maxDebounceWait defaults.");
        }

        if (EnsureBackupSettingsDefaults(configRoot))
        {
            migrationResult.AddMigration("Added backup.deepScanIntervalDays (7) and backup.hydrateCloudFiles (false) defaults.");
        }
    }

    /// <summary>v1 → v2: the only true reshaping is the backupType rename.</summary>
    private static void ApplyMigrationToSchemaV2(JsonObject configRoot, ConfigMigrationResult migrationResult)
    {
        if (TryGetString(configRoot, "backupType", out var backupTypeValue)
            && backupTypeValue.Equals("Differential", StringComparison.OrdinalIgnoreCase))
        {
            configRoot["backupType"] = "ChunkSnapshot";
            migrationResult.AddMigration("Mapped backupType from Differential to ChunkSnapshot.");
        }
    }

    /// <summary>
    /// v2 → v3 introduced only new blocks, which <see cref="EnsureCurrentDefaults"/> now
    /// covers for every version. Retained as an explicit no-op so the version ladder stays
    /// readable rather than skipping a number.
    /// </summary>
    private static void ApplyMigrationToSchemaV3(JsonObject configRoot, ConfigMigrationResult migrationResult)
    {
    }

    private static bool EnsureChunkDiffingDefaults(JsonObject configRoot)
    {
        var changed = false;
        var chunkDiffing = EnsureObject(configRoot, "chunkDiffing", ref changed);

        changed |= EnsureInt(chunkDiffing, "manifestVersion", 2);
        changed |= EnsureString(chunkDiffing, "compression", "auto");
        changed |= EnsureInt(chunkDiffing, "minChunkSizeKB", 32);
        changed |= EnsureInt(chunkDiffing, "targetChunkSizeKB", 128);
        changed |= EnsureInt(chunkDiffing, "maxChunkSizeKB", 512);
        changed |= EnsureInt(chunkDiffing, "rollingHashWindowSize", 64);
        changed |= EnsureInt(chunkDiffing, "maxChunksPerFile", 200_000);
        changed |= EnsureInt(chunkDiffing, "maxFilesPerSnapshot", 200_000);

        return changed;
    }

    private static bool EnsureGlobalStorageType(JsonObject configRoot)
    {
        if (TryGetString(configRoot, "globalStorageType", out var currentValue) && !string.IsNullOrWhiteSpace(currentValue))
        {
            return false;
        }

        var fallbackStorageType = "local";
        if (configRoot.TryGetPropertyValue("storageSources", out var storageSourcesNode)
            && storageSourcesNode is JsonObject storageSources
            && storageSources.Count > 0)
        {
            fallbackStorageType = storageSources.FirstOrDefault().Key ?? fallbackStorageType;
        }

        configRoot["globalStorageType"] = fallbackStorageType;
        return true;
    }

    private static bool EnsureRetentionDefaults(JsonObject configRoot)
    {
        var changed = false;
        var retention = EnsureObject(configRoot, "retention", ref changed);

        changed |= EnsureBool(retention, "enabled", false);
        changed |= EnsureInt(retention, "keepLastPerDirectory", 10);
        changed |= EnsureInt(retention, "maxAgeDays", 30);
        changed |= EnsureInt(retention, "keepHourly", 0);
        changed |= EnsureInt(retention, "keepDaily", 0);
        changed |= EnsureInt(retention, "keepWeekly", 0);
        changed |= EnsureInt(retention, "keepMonthly", 0);
        changed |= EnsureInt(retention, "keepYearly", 0);
        changed |= EnsureString(retention, "mode", RetentionModes.Any);

        return changed;
    }

    private static bool EnsureEncryptionIterationDefault(JsonObject configRoot)
    {
        var changed = false;
        var encryption = EnsureObject(configRoot, "encryption", ref changed);

        if (!TryGetInt(encryption, "keyDerivationIterations", out var iterations) || iterations <= 0)
        {
            encryption["keyDerivationIterations"] = 1_000_000;
            return true;
        }

        return changed;
    }

    private static bool EnsureSystemBackupWindowsSettingsDefault(JsonObject configRoot)
    {
        var changed = false;
        var systemBackup = EnsureObject(configRoot, "systemBackup", ref changed);

        // A config that never had the block must not silently opt in to system backup
        // (it captures environment variables, which can hold secrets).
        if (changed)
        {
            systemBackup["enabled"] = false;
        }

        changed |= EnsureBool(systemBackup, "includeWindowsSettings", true);

        return changed;
    }

    private static bool EnsureVerificationDefaults(JsonObject configRoot)
    {
        var changed = false;
        var verification = EnsureObject(configRoot, "verification", ref changed);

        changed |= EnsureBool(verification, "enabled", false);
        changed |= EnsureString(verification, "verifyInterval", "7.00:00:00");
        changed |= EnsureBool(verification, "localStorageOnly", true);
        changed |= EnsureInt(verification, "snapshotsPerRun", 2);

        return changed;
    }

    private static bool EnsureNotificationDefaults(JsonObject configRoot)
    {
        var changed = false;
        var notifications = EnsureObject(configRoot, "notifications", ref changed);

        changed |= EnsureBool(notifications, "enabled", true);
        changed |= EnsureBool(notifications, "notifyOnBackupFailure", true);
        changed |= EnsureBool(notifications, "notifyOnVerificationFailure", true);
        changed |= EnsureBool(notifications, "notifyOnRecovery", true);
        changed |= EnsureBool(notifications, "notifyOnEveryBackupSuccess", false);

        return changed;
    }

    private static bool EnsureLoggingDefaults(JsonObject configRoot)
    {
        var changed = false;
        var logging = EnsureObject(configRoot, "logging", ref changed);

        changed |= EnsureString(logging, "level", "Info");

        return changed;
    }

    private static bool EnsureProtectionDefaults(JsonObject configRoot)
    {
        var changed = false;
        var protection = EnsureObject(configRoot, "protection", ref changed);

        changed |= EnsureBool(protection, "autoStart", true);

        return changed;
    }

    private static bool EnsureTelemetryDefaults(JsonObject configRoot)
    {
        var changed = false;
        var telemetry = EnsureObject(configRoot, "telemetry", ref changed);

        changed |= EnsureBool(telemetry, "enabled", true);
        changed |= EnsureString(telemetry, "crashReports", "ask");

        return changed;
    }

    private static bool EnsureUpdatesDefaults(JsonObject configRoot)
    {
        var changed = false;
        var updates = EnsureObject(configRoot, "updates", ref changed);

        changed |= EnsureBool(updates, "checkAutomatically", true);
        changed |= EnsureBool(updates, "includePrereleases", false);

        return changed;
    }

    private static bool EnsureWatcherDefaults(JsonObject configRoot)
    {
        var changed = false;
        var watcher = EnsureObject(configRoot, "watcher", ref changed);

        changed |= EnsureString(watcher, "minSnapshotInterval", "00:15:00");
        changed |= EnsureString(watcher, "maxDebounceWait", "00:05:00");

        return changed;
    }

    private static bool EnsureBackupSettingsDefaults(JsonObject configRoot)
    {
        var changed = false;
        var backup = EnsureObject(configRoot, "backup", ref changed);

        changed |= EnsureInt(backup, "deepScanIntervalDays", 7);
        changed |= EnsureBool(backup, "hydrateCloudFiles", false);

        return changed;
    }

    private static bool EnsureString(JsonObject parent, string propertyName, string defaultValue)
    {
        if (TryGetString(parent, propertyName, out _))
        {
            return false;
        }

        parent[propertyName] = defaultValue;
        return true;
    }

    private static JsonObject EnsureObject(JsonObject parent, string propertyName, ref bool changed)
    {
        if (parent.TryGetPropertyValue(propertyName, out var node) && node is JsonObject existingObject)
        {
            return existingObject;
        }

        var createdObject = new JsonObject();
        parent[propertyName] = createdObject;
        changed = true;
        return createdObject;
    }

    private static bool EnsureInt(JsonObject parent, string propertyName, int defaultValue)
    {
        if (TryGetInt(parent, propertyName, out _))
        {
            return false;
        }

        parent[propertyName] = defaultValue;
        return true;
    }

    private static bool EnsureBool(JsonObject parent, string propertyName, bool defaultValue)
    {
        if (TryGetBool(parent, propertyName, out _))
        {
            return false;
        }

        parent[propertyName] = defaultValue;
        return true;
    }

    private static int ReadSchemaVersion(JsonObject configRoot)
    {
        if (TryGetInt(configRoot, "configSchemaVersion", out var explicitVersion))
        {
            return explicitVersion;
        }

        return 1;
    }

    private static bool HasExactIntValue(JsonObject parent, string propertyName, int expectedValue)
    {
        return TryGetInt(parent, propertyName, out var value) && value == expectedValue;
    }

    private static bool TryGetString(JsonObject parent, string propertyName, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetPropertyValue(propertyName, out var node) || node == null)
        {
            return false;
        }

        if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var stringValue) && !string.IsNullOrWhiteSpace(stringValue))
        {
            value = stringValue;
            return true;
        }

        return false;
    }

    private static bool TryGetInt(JsonObject parent, string propertyName, out int value)
    {
        value = 0;
        if (!parent.TryGetPropertyValue(propertyName, out var node) || node == null)
        {
            return false;
        }

        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<int>(out var intValue))
            {
                value = intValue;
                return true;
            }

            if (jsonValue.TryGetValue<string>(out var stringValue)
                && int.TryParse(stringValue, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedValue))
            {
                value = parsedValue;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetBool(JsonObject parent, string propertyName, out bool value)
    {
        value = false;
        if (!parent.TryGetPropertyValue(propertyName, out var node) || node == null)
        {
            return false;
        }

        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<bool>(out var boolValue))
            {
                value = boolValue;
                return true;
            }

            if (jsonValue.TryGetValue<string>(out var stringValue)
                && bool.TryParse(stringValue, out var parsedValue))
            {
                value = parsedValue;
                return true;
            }
        }

        return false;
    }
}
