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
    public const int CURRENT_CONFIG_SCHEMA_VERSION = 3;

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

        if (!HasExactIntValue(configRoot, "configSchemaVersion", CURRENT_CONFIG_SCHEMA_VERSION))
        {
            configRoot["configSchemaVersion"] = CURRENT_CONFIG_SCHEMA_VERSION;
            migrationResult.AddMigration($"Set configSchemaVersion to {CURRENT_CONFIG_SCHEMA_VERSION}.");
        }

        migrationResult.TargetSchemaVersion = CURRENT_CONFIG_SCHEMA_VERSION;
        return migrationResult;
    }

    private static void ApplyMigrationToSchemaV2(JsonObject configRoot, ConfigMigrationResult migrationResult)
    {
        if (TryGetString(configRoot, "backupType", out var backupTypeValue)
            && backupTypeValue.Equals("Differential", StringComparison.OrdinalIgnoreCase))
        {
            configRoot["backupType"] = "ChunkSnapshot";
            migrationResult.AddMigration("Mapped backupType from Differential to ChunkSnapshot.");
        }

        if (EnsureChunkDiffingDefaults(configRoot))
        {
            migrationResult.AddMigration("Added missing chunkDiffing configuration defaults.");
        }
    }

    private static void ApplyMigrationToSchemaV3(JsonObject configRoot, ConfigMigrationResult migrationResult)
    {
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
    }

    private static bool EnsureChunkDiffingDefaults(JsonObject configRoot)
    {
        var changed = false;
        var chunkDiffing = EnsureObject(configRoot, "chunkDiffing", ref changed);

        changed |= EnsureInt(chunkDiffing, "manifestVersion", 2);
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

        changed |= EnsureBool(systemBackup, "includeWindowsSettings", true);

        return changed;
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
                && int.TryParse(stringValue, out var parsedValue))
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
