using System.Text.Json.Nodes;
using FluentAssertions;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>I1: CLI config edits are checked against config.schema.json, so a value the loader would reset is refused (exit 3, file unchanged).</summary>
public class CliConfigSchemaValidationTests : CliConfigTestBase
{
    [Theory]
    [InlineData("retention.keepWeekly", "abc", "retention.keepWeekly")]
    [InlineData("retention.keepDaily", "-1", "minimum")]
    [InlineData("retention.keepDaily", "99999999999", "retention.keepDaily")]
    [InlineData("telemetry.crashReports", "maybe", "enum")]
    [InlineData("backupType", "Weird", "enum")]
    [InlineData("chunkDiffing.rollingHashWindowSize", "4", "minimum")]
    [InlineData("chunkDiffing.rollingHashWindowSize", "9999", "maximum")]
    [InlineData("retention.enabled", "\"yes please\"", "boolean")]
    [InlineData("verification.verifyInterval", "\"\"", "empty")]
    public async Task ConfigSet_InvalidValue_IsRefusedAndFileUnchanged(string key, string value, string rule)
    {
        WriteConfig();
        var before = File.ReadAllText(AppPaths.ConfigFile);

        var (exit, stdout, _) = await RunAsync("config", "set", key, value, "--json");

        exit.Should().Be(3);
        var doc = Json(stdout);
        doc["applied"]!.GetValue<bool>().Should().BeFalse();
        string.Join("|", doc["errors"]!.AsArray().Select(e => e!.GetValue<string>())).Should().Contain(key).And.Contain(rule);
        File.ReadAllText(AppPaths.ConfigFile).Should().Be(before);
    }

    [Fact]
    public async Task ConfigSet_ValidValues_StillWork()
    {
        WriteConfig();

        (await RunAsync("config", "set", "retention.keepWeekly", "4")).ExitCode.Should().Be(0);
        (await RunAsync("config", "set", "telemetry.crashReports", "never")).ExitCode.Should().Be(0);
        (await RunAsync("config", "set", "backupType", "Full")).ExitCode.Should().Be(0);
        ReadConfig()["retention"]!["keepWeekly"]!.GetValue<int>().Should().Be(4);
    }

    [Fact]
    public async Task ConfigUnset_ThatLeavesNoNewProblem_Works_AndExistingProblemsDoNotBlockUnrelatedEdits()
    {
        WriteConfig(root => root["retention"]!["keepDaily"] = "junk");

        (await RunAsync("config", "set", "retention.keepMonthly", "2")).ExitCode.Should().Be(0, "the pre-existing problem is not introduced by this edit");
        (await RunAsync("config", "set", "retention.keepYearly", "x")).ExitCode.Should().Be(3);
        (await RunAsync("config", "unset", "retention.keepDaily")).ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task SetWindowAndSchedule_StillValid()
    {
        WriteConfig();

        (await RunAsync("config", "set-window", "22:00", "06:00")).ExitCode.Should().Be(0);
        (await RunAsync("config", "set-schedule", "system", "daily 03:00")).ExitCode.Should().Be(0);
        (await RunAsync("config", "set-schedule", WatchedDirectory, "daily 02:00")).ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task ConfigSet_UnknownKey_StillNeedsForce()
    {
        WriteConfig();

        (await RunAsync("config", "set", "nonsense.key", "1")).ExitCode.Should().Be(2);
        (await RunAsync("config", "set", "nonsense.key", "1", "--force")).ExitCode.Should().Be(0);
    }

    [Fact]
    public void ShippedExampleConfig_PassesTheSchemaValidator()
    {
        ConfigSchemaValidator.Validate(ConfigSchema.LoadExample()).Should().BeEmpty();
    }

    [Fact]
    public void Validator_ChecksTypeEnumRangeRequiredAndNullable()
    {
        var doc = JsonNode.Parse("""
            { "retention": { "keepDaily": 1.5, "mode": "ANY" }, "watchDirectories": [ { "storageType": 3 } ], "backupType": null }
            """)!.AsObject();

        var errors = ConfigSchemaValidator.Validate(doc);

        errors.Should().Contain(e => e.StartsWith("retention.keepDaily"));
        errors.Should().NotContain(e => e.StartsWith("retention.mode"), "enum text compares case-insensitively like the loader");
        errors.Should().Contain(e => e.Contains("watchDirectories[0]") && e.Contains("required"));
        errors.Should().Contain(e => e.StartsWith("watchDirectories[0].storageType"));
        errors.Should().Contain(e => e.StartsWith("backupType"));
    }

    [Fact]
    public async Task ConfigValidate_OnTheShippedExample_DoesNotWarnAboutPlaceholderStorages()
    {
        WritePristineConfig();

        var (_, stdout, stderr) = await RunAsync("config", "validate", "--json");

        var text = stdout + stderr;
        text.Should().NotContain("GCP credential file not found");
        text.Should().NotContain("path\\to\\key.json");
    }
}
