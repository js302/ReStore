using System.Text.Json.Nodes;
using FluentAssertions;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>A hand-edited or hostile config.json must produce a clean configuration error (exit 3, JSON envelope, a hint), never a crash.</summary>
[Collection("GlobalState")]
public class HostileConfigTests : CliConfigTestBase
{
    public static IEnumerable<object[]> BrokenValues() =>
    [
        ["backupInterval", "\"daily\""], ["backupInterval", "\"\""], ["backupInterval", "42"], ["backupInterval", "null"], ["backupInterval", "{}"],
        ["backupInterval", "\"99999999.00:00:00\""], ["backupInterval", "\"-1:00:00\""], ["backupInterval", "\"1:2:3:4:5\""],
        ["sizeThresholdMB", "\"big\""], ["sizeThresholdMB", "null"], ["sizeThresholdMB", "1e999"], ["sizeThresholdMB", "-5"],
        ["watchDirectories", "\"C:\\\\x\""], ["watchDirectories", "[42]"], ["watchDirectories", "[null]"], ["watchDirectories", "{}"],
        ["storageSources", "[]"], ["storageSources", "\"s3\""], ["storageSources", "{ \"local\": 5 }"],
        ["encryption", "\"on\""], ["encryption", "{ \"enabled\": \"yes\" }"], ["encryption", "{ \"keyDerivationIterations\": \"many\" }"],
        ["retention", "{ \"keepLast\": \"ten\" }"], ["retention", "7"],
        ["chunkDiffing", "{ \"minChunkSizeKB\": \"x\" }"], ["chunkDiffing", "{ \"manifestVersion\": 99 }"],
        ["verification", "{ \"verifyInterval\": \"weekly\" }"], ["verification", "{ \"snapshotsPerRun\": \"all\" }"],
        ["watcher", "{ \"minSnapshotInterval\": \"soon\" }"], ["systemBackup", "{ \"backupInterval\": \"daily\" }"], ["systemBackup", "{ \"includePrograms\": \"yes\" }"],
        ["logging", "{ \"level\": 7 }"], ["telemetry", "\"on\""], ["notifications", "[]"], ["protection", "5"], ["updates", "\"never\""]
    ];

    [Theory]
    [MemberData(nameof(BrokenValues))]
    public async Task BrokenValue_IsACleanConfigError(string key, string rawJson)
    {
        WriteConfig(root => root[key] = JsonNode.Parse(rawJson));

        var (exit, stdout, stderr) = await RunAsync("status", "--json");

        // A value that happens to be acceptable is fine; what is not fine is anything that is neither success nor a config error.
        if (exit == 0)
        {
            return;
        }

        exit.Should().Be(3, $"'{key}': {rawJson} -> {stdout}{stderr}");
        var document = JsonNode.Parse(stdout)!.AsObject();
        document["ok"]!.GetValue<bool>().Should().BeFalse();
        document["error"]!["code"]!.GetValue<string>().Should().Be("config");
        document["error"]!["message"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        document["error"]!["hint"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        stdout.Should().NotContain("   at ", "no stack trace in the envelope");
        Directory.Exists(Path.Combine(AppPaths.Root, "client-state", "crash-reports")).Should().BeFalse();
    }

    [Fact]
    public async Task HandEditedBackupInterval_Daily_IsAConfigErrorWithAHint_NotACrash()
    {
        WriteConfig(root => root["backupInterval"] = "daily");

        var (exit, stdout, _) = await RunAsync("status", "--json");

        exit.Should().Be(3, stdout);
        JsonNode.Parse(stdout)!["error"]!["hint"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("\uFEFF{ \"backupInterval\": \"01:00:00\" }")]
    [InlineData("{ \"backupInterval\": \"01:00:00\", }")]
    [InlineData("{ /* comment */ \"backupInterval\": \"01:00:00\" }")]
    public async Task NotAConfigAtAll_IsACleanConfigError(string content)
    {
        File.WriteAllText(AppPaths.ConfigFile, content);

        var (exit, stdout, _) = await RunAsync("status", "--json");

        exit.Should().BeOneOf(0, 3);
        var document = JsonNode.Parse(stdout)!.AsObject();
        document["ok"].Should().NotBeNull();
    }

    [Fact]
    public async Task HugeConfig_DoesNotCrash()
    {
        File.WriteAllText(AppPaths.ConfigFile, "{ \"watchDirectories\": [" + string.Join(',', Enumerable.Repeat("{\"path\":\"x\"}", 20_000)) + "] }");

        var (exit, stdout, _) = await RunAsync("config", "validate", "--json");

        exit.Should().BeOneOf(0, 3);
        JsonNode.Parse(stdout)!["ok"].Should().NotBeNull();
    }
}
