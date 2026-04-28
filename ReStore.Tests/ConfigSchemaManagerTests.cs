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
    public void Migrate_ShouldLeaveCurrentSchemaConfigUnchanged()
    {
        var root = JsonNode.Parse("""
        {
          "configSchemaVersion": 3,
          "backupType": "ChunkSnapshot",
          "chunkDiffing": {
            "manifestVersion": 2,
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
            "maxAgeDays": 30
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
}
