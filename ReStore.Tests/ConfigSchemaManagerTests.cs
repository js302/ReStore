using FluentAssertions;
using ReStore.Core.src.utils;
using System.Text.Json.Nodes;

namespace ReStore.Tests;

public class ConfigSchemaManagerTests
{
    [Fact]
    public void Migrate_ShouldUpgradeLegacyConfigAndInjectChunkDefaults()
    {
        var root = JsonNode.Parse("""
        {
          "backupType": "Differential",
          "storageSources": {
            "local": {
              "path": "C:/Backups",
              "options": {}
            }
          }
        }
        """) as JsonObject;

        root.Should().NotBeNull();
        var result = ConfigSchemaManager.Migrate(root!);

        result.MigrationApplied.Should().BeTrue();
        result.SourceSchemaVersion.Should().Be(1);
        result.TargetSchemaVersion.Should().Be(ConfigSchemaManager.CURRENT_CONFIG_SCHEMA_VERSION);

        root!["backupType"]!.GetValue<string>().Should().Be("ChunkSnapshot");
        root["configSchemaVersion"]!.GetValue<int>().Should().Be(ConfigSchemaManager.CURRENT_CONFIG_SCHEMA_VERSION);

        var chunkDiffing = root["chunkDiffing"] as JsonObject;
        chunkDiffing.Should().NotBeNull();
        chunkDiffing!["targetChunkSizeKB"]!.GetValue<int>().Should().Be(128);
        chunkDiffing["maxFilesPerSnapshot"]!.GetValue<int>().Should().Be(200_000);
    }

    [Fact]
    public void Migrate_ShouldRepairInvalidEncryptionIterations()
    {
        var root = JsonNode.Parse("""
        {
          "configSchemaVersion": 2,
          "encryption": {
            "enabled": true,
            "keyDerivationIterations": 0
          }
        }
        """) as JsonObject;

        root.Should().NotBeNull();
        var result = ConfigSchemaManager.Migrate(root!);

        result.MigrationApplied.Should().BeTrue();

        var encryption = root!["encryption"] as JsonObject;
        encryption.Should().NotBeNull();
        encryption!["keyDerivationIterations"]!.GetValue<int>().Should().Be(1_000_000);
    }

    [Fact]
    public void Migrate_ShouldAddVerificationAndNotificationDefaults_WithOptInOffByDefault()
    {
        var root = JsonNode.Parse("""
        {
          "configSchemaVersion": 3,
          "backupType": "ChunkSnapshot"
        }
        """) as JsonObject;

        root.Should().NotBeNull();
        var result = ConfigSchemaManager.Migrate(root!);

        result.MigrationApplied.Should().BeTrue();
        result.SourceSchemaVersion.Should().Be(3);
        result.TargetSchemaVersion.Should().Be(ConfigSchemaManager.CURRENT_CONFIG_SCHEMA_VERSION);

        var verification = root!["verification"] as JsonObject;
        verification.Should().NotBeNull();
        verification!["enabled"]!.GetValue<bool>().Should().BeFalse("verification downloads every chunk, so it must be opt-in");
        verification["verifyInterval"]!.GetValue<string>().Should().Be("7.00:00:00");
        verification["localStorageOnly"]!.GetValue<bool>().Should().BeTrue();

        var notifications = root["notifications"] as JsonObject;
        notifications.Should().NotBeNull();
        notifications!["notifyOnBackupFailure"]!.GetValue<bool>().Should().BeTrue();
        notifications["notifyOnEveryBackupSuccess"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void Migrate_ShouldRepairMissingSections_EvenWhenAlreadyAtCurrentVersion()
    {
        // A config can carry the current version stamp and still be missing a block — hand
        // edited, truncated, or written by a build that predates the block. A version-gated
        // repair would skip this forever.
        var root = JsonNode.Parse("""
        {
          "configSchemaVersion": 4,
          "backupType": "ChunkSnapshot"
        }
        """) as JsonObject;

        root.Should().NotBeNull();
        var result = ConfigSchemaManager.Migrate(root!);

        result.MigrationApplied.Should().BeTrue("missing blocks must be repaired regardless of version");

        (root!["verification"] as JsonObject).Should().NotBeNull();
        (root["notifications"] as JsonObject).Should().NotBeNull();
        (root["chunkDiffing"] as JsonObject).Should().NotBeNull();
        (root["retention"] as JsonObject).Should().NotBeNull();
    }

    [Fact]
    public void Migrate_ShouldBeIdempotent()
    {
        var root = JsonNode.Parse("""
        {
          "backupType": "Differential",
          "storageSources": { "local": { "path": "C:/Backups", "options": {} } }
        }
        """) as JsonObject;

        root.Should().NotBeNull();

        ConfigSchemaManager.Migrate(root!).MigrationApplied.Should().BeTrue();
        var afterFirst = root!.ToJsonString();

        var second = ConfigSchemaManager.Migrate(root);

        second.MigrationApplied.Should().BeFalse("a second pass over a repaired config must change nothing");
        root.ToJsonString().Should().Be(afterFirst);
    }

    [Fact]
    public void Migrate_ShouldPreserveExistingValues_AndNotOverwriteWithDefaults()
    {
        var root = JsonNode.Parse("""
        {
          "configSchemaVersion": 4,
          "verification": {
            "enabled": true,
            "verifyInterval": "1.00:00:00",
            "localStorageOnly": false
          },
          "retention": {
            "enabled": true,
            "keepLastPerDirectory": 3,
            "maxAgeDays": 5
          }
        }
        """) as JsonObject;

        root.Should().NotBeNull();
        ConfigSchemaManager.Migrate(root!);

        var verification = root!["verification"] as JsonObject;
        verification!["enabled"]!.GetValue<bool>().Should().BeTrue();
        verification["verifyInterval"]!.GetValue<string>().Should().Be("1.00:00:00");
        verification["localStorageOnly"]!.GetValue<bool>().Should().BeFalse();

        var retention = root["retention"] as JsonObject;
        retention!["keepLastPerDirectory"]!.GetValue<int>().Should().Be(3);
        retention["maxAgeDays"]!.GetValue<int>().Should().Be(5);
    }

    [Fact]
    public void Migrate_ShouldLeaveCurrentSchemaConfigUnchanged()
    {
        var root = JsonNode.Parse("""
        {
          "configSchemaVersion": 4,
          "backupType": "ChunkSnapshot",
          "globalStorageType": "local",
          "chunkDiffing": {
            "manifestVersion": 2,
            "compression": "auto",
            "minChunkSizeKB": 32,
            "targetChunkSizeKB": 128,
            "maxChunkSizeKB": 512,
            "rollingHashWindowSize": 64,
            "maxChunksPerFile": 200000,
            "maxFilesPerSnapshot": 200000
          },
          "retention": {
            "enabled": false,
            "keepLastPerDirectory": 10,
            "maxAgeDays": 30,
            "keepHourly": 0,
            "keepDaily": 0,
            "keepWeekly": 0,
            "keepMonthly": 0,
            "keepYearly": 0,
            "mode": "any"
          },
          "verification": {
            "enabled": false,
            "verifyInterval": "7.00:00:00",
            "localStorageOnly": true,
            "snapshotsPerRun": 2
          },
          "notifications": {
            "enabled": true,
            "notifyOnBackupFailure": true,
            "notifyOnVerificationFailure": true,
            "notifyOnRecovery": true,
            "notifyOnEveryBackupSuccess": false
          },
          "logging": {
            "level": "Info"
          },
          "protection": {
            "autoStart": true
          },
          "telemetry": {
            "enabled": true,
            "crashReports": "ask"
          },
          "updates": {
            "checkAutomatically": true,
            "includePrereleases": false
          },
          "watcher": {
            "minSnapshotInterval": "00:15:00",
            "maxDebounceWait": "00:05:00"
          },
          "backup": {
            "deepScanIntervalDays": 7,
            "hydrateCloudFiles": false
          },
          "systemBackup": {
            "includeWindowsSettings": true
          },
          "encryption": {
            "keyDerivationIterations": 1000000
          }
        }
        """) as JsonObject;

        root.Should().NotBeNull();
        var beforeJson = root!.ToJsonString();

        var result = ConfigSchemaManager.Migrate(root);

        result.MigrationApplied.Should().BeFalse();
        result.SourceSchemaVersion.Should().Be(ConfigSchemaManager.CURRENT_CONFIG_SCHEMA_VERSION);
        result.TargetSchemaVersion.Should().Be(ConfigSchemaManager.CURRENT_CONFIG_SCHEMA_VERSION);
        root.ToJsonString().Should().Be(beforeJson);
    }

    [Fact]
    public void Migrate_AddsProtectionAutoStartDefault_ToOldConfig()
    {
        var root = JsonNode.Parse("{ \"configSchemaVersion\": 4 }") as JsonObject;

        var result = ConfigSchemaManager.Migrate(root!);

        result.MigrationApplied.Should().BeTrue();
        root!["protection"]!["autoStart"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Migrate_AddsLoggingLevelDefault_AndKeepsAnExplicitLevel()
    {
        var added = JsonNode.Parse("{ \"configSchemaVersion\": 4 }") as JsonObject;
        ConfigSchemaManager.Migrate(added!);
        added!["logging"]!["level"]!.GetValue<string>().Should().Be("Info");

        var explicitLevel = JsonNode.Parse("{ \"configSchemaVersion\": 4, \"logging\": { \"level\": \"Debug\" } }") as JsonObject;
        ConfigSchemaManager.Migrate(explicitLevel!);
        explicitLevel!["logging"]!["level"]!.GetValue<string>().Should().Be("Debug");
    }

    [Fact]
    public void Migrate_AddsTelemetryAndUpdatesDefaults_ToOldConfig()
    {
        var root = JsonNode.Parse("{ \"configSchemaVersion\": 4 }") as JsonObject;

        var result = ConfigSchemaManager.Migrate(root!);

        result.MigrationApplied.Should().BeTrue();
        root!["telemetry"]!["enabled"]!.GetValue<bool>().Should().BeTrue();
        root["updates"]!["checkAutomatically"]!.GetValue<bool>().Should().BeTrue();
        root["updates"]!["includePrereleases"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void Migrate_KeepsExplicitTelemetryAndUpdateChoices()
    {
        var root = JsonNode.Parse("{ \"configSchemaVersion\": 4, \"telemetry\": { \"enabled\": false }, \"updates\": { \"checkAutomatically\": false, \"includePrereleases\": true } }") as JsonObject;

        ConfigSchemaManager.Migrate(root!);

        root!["telemetry"]!["enabled"]!.GetValue<bool>().Should().BeFalse();
        root["updates"]!["checkAutomatically"]!.GetValue<bool>().Should().BeFalse();
        root["updates"]!["includePrereleases"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Migrate_KeepsAnExplicitAutoStartFalse()
    {
        var root = JsonNode.Parse("{ \"configSchemaVersion\": 4, \"protection\": { \"autoStart\": false } }") as JsonObject;

        ConfigSchemaManager.Migrate(root!);

        root!["protection"]!["autoStart"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void Migrate_ShouldAddRetentionV2Defaults_WithoutChangingExistingRetentionValues()
    {
        var root = JsonNode.Parse("""{ "configSchemaVersion": 4, "retention": { "enabled": true, "keepLastPerDirectory": 3, "maxAgeDays": 5 } }""") as JsonObject;

        ConfigSchemaManager.Migrate(root!);

        var retention = (JsonObject)root!["retention"]!;
        retention["keepLastPerDirectory"]!.GetValue<int>().Should().Be(3);
        retention["maxAgeDays"]!.GetValue<int>().Should().Be(5);
        retention["keepDaily"]!.GetValue<int>().Should().Be(0);
        retention["mode"]!.GetValue<string>().Should().Be("any");
    }
}
