using System.Text.Json.Nodes;
using FluentAssertions;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// <c>schedule preview|next</c>, <c>config set-schedule|clear-schedule|set-window|clear-window</c>, and <c>status</c> / <c>run-due</c> with schedules
/// and a backup window, run in-process against a temp data root. A closed window is built from the real clock (two hours ahead).
/// </summary>
public class CliScheduleCommandsTests : CliConfigTestBase
{
    private int ManifestCount() =>
        Directory.Exists(StorageDirectory) ? Directory.GetFiles(StorageDirectory, "*.manifest.json", SearchOption.AllDirectories).Length : 0;

    private void Seed() => File.WriteAllText(Path.Combine(WatchedDirectory, "doc.txt"), "document " + new string('d', 500));

    private static string Hhmm(DateTime local) => local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A window that is closed right now: it opens in two hours and closes in three.</summary>
    private static (string Start, string End) ClosedWindow() => (Hhmm(DateTime.Now.AddHours(2)), Hhmm(DateTime.Now.AddHours(3)));

    // ---- schedule preview ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Preview_DescribesAClockSchedule_AndListsTheNextRuns()
    {
        var (exit, stdout, stderr) = await RunAsync("schedule", "preview", "daily 12:00", "--json");

        exit.Should().Be(0, stdout + stderr);
        var doc = Json(stdout);
        doc["ok"]!.GetValue<bool>().Should().BeTrue();
        doc["canonical"]!.GetValue<string>().Should().Be("daily 12:00");
        doc["kind"]!.GetValue<string>().Should().Be("calendar");
        doc["description"]!.GetValue<string>().Should().Be("Every day at 12:00");
        doc["timeZone"]!.GetValue<string>().Should().Be(TimeZoneInfo.Local.Id);
        var runs = doc["nextRuns"]!.AsArray();
        runs.Should().HaveCount(5, "five is the default");
        runs.Select(r => r!["local"]!.GetValue<string>()).Should().OnlyContain(t => t.Contains("T12:00:00"));
        var utc = runs.Select(r => DateTime.Parse(r!["utc"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime()).ToList();
        utc.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        utc[0].Should().BeAfter(DateTime.UtcNow);
        utc[0].Should().BeBefore(DateTime.UtcNow.AddDays(1.1));
    }

    [Fact]
    public async Task Preview_AcceptsTheExpressionAsSeveralWords_AndCount()
    {
        var (exit, stdout, _) = await RunAsync("schedule", "preview", "mon-fri", "18:30", "--count", "3", "--json");

        exit.Should().Be(0);
        var doc = Json(stdout);
        doc["canonical"]!.GetValue<string>().Should().Be("weekdays 18:30");
        doc["description"]!.GetValue<string>().Should().Be("Every weekday at 18:30");
        doc["nextRuns"]!.AsArray().Should().HaveCount(3);
        doc["nextRuns"]!.AsArray().Select(r => DateTime.Parse(r!["local"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture).DayOfWeek)
            .Should().OnlyContain(d => d != DayOfWeek.Saturday && d != DayOfWeek.Sunday);
    }

    [Fact]
    public async Task Preview_OfACronExpression_ListsItsRuns()
    {
        var (exit, stdout, _) = await RunAsync("schedule", "preview", "cron */15 * * * *", "--count", "4", "--json");

        exit.Should().Be(0);
        var doc = Json(stdout);
        doc["kind"]!.GetValue<string>().Should().Be("cron");
        var utc = doc["nextRuns"]!.AsArray().Select(r => DateTime.Parse(r!["utc"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime()).ToList();
        utc.Zip(utc.Skip(1), (a, b) => b - a).Should().OnlyContain(gap => gap == TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task Preview_OfAnIntervalAndOff_ExplainsWhatTheTimesMean()
    {
        var (_, every, _) = await RunAsync("schedule", "preview", "every 4h", "--count", "2", "--json");
        var interval = Json(every);
        interval["kind"]!.GetValue<string>().Should().Be("interval");
        interval["note"]!.GetValue<string>().Should().Contain("count from the last backup");
        interval["nextRuns"]!.AsArray().Should().HaveCount(2);

        var (offExit, off, _) = await RunAsync("schedule", "preview", "off", "--json");
        offExit.Should().Be(0);
        var offDoc = Json(off);
        offDoc["kind"]!.GetValue<string>().Should().Be("off");
        offDoc["nextRuns"]!.AsArray().Should().BeEmpty();
        offDoc["note"]!.GetValue<string>().Should().Contain("watcher");
    }

    [Theory]
    [InlineData("daily 2pm", "expected HH:mm at 'daily 2pm'")]
    [InlineData("every 1m", "minimum is 5m")]
    [InlineData("cron 61 * * * *", "out of range")]
    [InlineData("fortnightly 10:00", "unknown schedule")]
    public async Task Preview_OfAnInvalidExpression_ExitsWithUsage_AndTheParseError(string expression, string expected)
    {
        var (exit, stdout, stderr) = await RunAsync("schedule", "preview", expression, "--json");

        exit.Should().Be(2, stdout + stderr);
        var doc = Json(stdout);
        doc["ok"]!.GetValue<bool>().Should().BeFalse();
        doc["error"]!["code"]!.GetValue<string>().Should().Be("usage");
        doc["error"]!["message"]!.GetValue<string>().Should().Contain(expected);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("x")]
    public async Task Preview_RejectsABadCount(string count)
    {
        var (exit, stdout, stderr) = await RunAsync("schedule", "preview", "daily 02:00", "--count", count, "--json");

        exit.Should().Be(2, stdout + stderr);
        Json(stdout)["error"]!["message"]!.GetValue<string>().Should().Contain("--count");
    }

    [Fact]
    public async Task Preview_NeedsNoConfiguration_AndWritesNothing()
    {
        var (exit, stdout, _) = await RunAsync("schedule", "preview", "weekly sun 03:00");

        exit.Should().Be(0);
        stdout.Should().Contain("Every Sunday at 03:00").And.Contain("Next runs:");
        File.Exists(AppPaths.ConfigFile).Should().BeFalse();
    }

    // ---- config set-schedule / clear-schedule ----------------------------------------------------------------------

    [Fact]
    public async Task SetSchedule_StoresTheNormalizedExpression_OnTheFolder_AndKeepsTheRest()
    {
        WriteConfig(root => root["watchDirectories"]![0]!["futureFlag"] = "keep me");

        var (exit, stdout, stderr) = await RunAsync("config", "set-schedule", WatchedDirectory, "Weekdays  6:30", "--json");

        exit.Should().Be(0, stdout + stderr);
        var doc = Json(stdout);
        doc["target"]!.GetValue<string>().Should().Be("folder");
        doc["path"]!.GetValue<string>().Should().Be(WatchedDirectory);
        doc["schedule"]!.GetValue<string>().Should().Be("weekdays 06:30");
        doc["description"]!.GetValue<string>().Should().Be("Every weekday at 06:30");
        doc["changed"]!.GetValue<bool>().Should().BeTrue();
        doc["nextRuns"]!.AsArray().Should().HaveCount(3);
        doc["backup"]!.GetValue<string>().Should().NotBeNullOrEmpty();

        var folder = ReadConfig()["watchDirectories"]![0]!;
        folder["schedule"]!.GetValue<string>().Should().Be("weekdays 06:30");
        folder["futureFlag"]!.GetValue<string>().Should().Be("keep me");
        BackupFiles().Should().HaveCount(1);
    }

    [Fact]
    public async Task SetSchedule_AcceptsOff_AndTheSameValueAgainChangesNothing()
    {
        WriteConfig();

        (await RunAsync("config", "set-schedule", WatchedDirectory, "off")).ExitCode.Should().Be(0);
        ReadConfig()["watchDirectories"]![0]!["schedule"]!.GetValue<string>().Should().Be("off");

        var (exit, stdout, _) = await RunAsync("config", "set-schedule", WatchedDirectory, "OFF", "--json");
        exit.Should().Be(0);
        Json(stdout)["changed"]!.GetValue<bool>().Should().BeFalse();
        BackupFiles().Should().HaveCount(1, "an unchanged edit writes no second backup");
    }

    [Fact]
    public async Task SetSchedule_ForTheSystemBackup_WritesSystemBackupSchedule()
    {
        WriteConfig();

        var (exit, stdout, stderr) = await RunAsync("config", "set-schedule", "SYSTEM", "weekly sun 03:00", "--json");

        exit.Should().Be(0, stdout + stderr);
        var doc = Json(stdout);
        doc["target"]!.GetValue<string>().Should().Be("system");
        doc["path"].Should().BeNull();
        ReadConfig()["systemBackup"]!["schedule"]!.GetValue<string>().Should().Be("weekly sun 03:00");
        ReadConfig()["watchDirectories"]![0]!.AsObject().ContainsKey("schedule").Should().BeFalse();
    }

    [Fact]
    public async Task SetSchedule_RejectsAnInvalidExpression_WithExit2_AndTouchesNothing()
    {
        WriteConfig();
        var before = File.ReadAllText(AppPaths.ConfigFile);

        var (exit, stdout, stderr) = await RunAsync("config", "set-schedule", WatchedDirectory, "daily 2pm", "--json");

        exit.Should().Be(2, stdout + stderr);
        var message = Json(stdout)["error"]!["message"]!.GetValue<string>();
        message.Should().Contain("expected HH:mm at 'daily 2pm'").And.Contain("schedule preview");
        File.ReadAllText(AppPaths.ConfigFile).Should().Be(before);
        BackupFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task SetSchedule_RejectsAnUnknownFolder_AndAMissingExpression()
    {
        WriteConfig();

        var (unknownExit, unknownOut, _) = await RunAsync("config", "set-schedule", Path.Combine(Root, "nope"), "daily 02:00", "--json");
        unknownExit.Should().Be(2);
        Json(unknownOut)["error"]!["message"]!.GetValue<string>().Should().Contain("is not a watched folder");

        var (missingExit, missingOut, _) = await RunAsync("config", "set-schedule", WatchedDirectory, "--json");
        missingExit.Should().Be(2);
        Json(missingOut)["error"]!["message"]!.GetValue<string>().Should().Contain("schedule");
    }

    [Fact]
    public async Task SetSchedule_TurnsAPlainStringFolderEntryIntoAnObject()
    {
        WriteConfig(root => root["watchDirectories"] = new JsonArray((JsonNode)WatchedDirectory));

        var (exit, stdout, stderr) = await RunAsync("config", "set-schedule", WatchedDirectory, "daily 02:00", "--json");

        exit.Should().Be(0, stdout + stderr);
        var folder = ReadConfig()["watchDirectories"]![0]!.AsObject();
        folder["path"]!.GetValue<string>().Should().Be(WatchedDirectory);
        folder["schedule"]!.GetValue<string>().Should().Be("daily 02:00");
    }

    [Fact]
    public async Task ClearSchedule_RemovesIt_AndANoOpChangesNothing()
    {
        WriteConfig(root =>
        {
            root["watchDirectories"]![0]!["schedule"] = "daily 02:00";
            root["systemBackup"]!["schedule"] = "daily 03:00";
        });

        var (exit, stdout, _) = await RunAsync("config", "clear-schedule", WatchedDirectory, "--json");
        exit.Should().Be(0);
        var doc = Json(stdout);
        doc["existed"]!.GetValue<bool>().Should().BeTrue();
        doc["changed"]!.GetValue<bool>().Should().BeTrue();
        ReadConfig()["watchDirectories"]![0]!.AsObject().ContainsKey("schedule").Should().BeFalse();
        ReadConfig()["systemBackup"]!["schedule"]!.GetValue<string>().Should().Be("daily 03:00", "only the named target is cleared");

        var (againExit, again, _) = await RunAsync("config", "clear-schedule", WatchedDirectory, "--json");
        againExit.Should().Be(0);
        Json(again)["existed"]!.GetValue<bool>().Should().BeFalse();
        Json(again)["changed"]!.GetValue<bool>().Should().BeFalse();

        (await RunAsync("config", "clear-schedule", "system")).ExitCode.Should().Be(0);
        ReadConfig()["systemBackup"]!.AsObject().ContainsKey("schedule").Should().BeFalse();
    }

    [Fact]
    public async Task ListWatch_ShowsTheSchedule()
    {
        WriteConfig(root => root["watchDirectories"]![0]!["schedule"] = "daily 02:00");

        var (_, json, _) = await RunAsync("config", "list-watch", "--json");
        Json(json)["watchDirectories"]![0]!["schedule"]!.GetValue<string>().Should().Be("daily 02:00");

        var (_, text, _) = await RunAsync("config", "list-watch");
        text.Should().Contain("(schedule: daily 02:00)");
    }

    [Fact]
    public async Task ConfigSet_OfAScheduleKey_IsValidatedLikeTheDedicatedCommand()
    {
        WriteConfig();

        var (badExit, badOut, _) = await RunAsync("config", "set", "watchDirectories.0.schedule", "daily 2pm", "--json");
        badExit.Should().Be(3, "the validator refuses the result");
        Json(badOut)["errors"]!.AsArray().Select(e => e!.GetValue<string>()).Should().Contain(e => e.Contains("invalid schedule 'daily 2pm'"));

        var (goodExit, _, _) = await RunAsync("config", "set", "watchDirectories.0.schedule", "daily 02:00");
        goodExit.Should().Be(0);
        ReadConfig()["watchDirectories"]![0]!["schedule"]!.GetValue<string>().Should().Be("daily 02:00");

        var (windowExit, windowOut, _) = await RunAsync("config", "set", "backupWindow.start", "25:00", "--json");
        windowExit.Should().Be(3);
        Json(windowOut)["errors"]!.AsArray().Select(e => e!.GetValue<string>()).Should().Contain(e => e.Contains("backup window start"));
    }

    // ---- config set-window / clear-window --------------------------------------------------------------------------

    [Fact]
    public async Task SetWindow_WritesAnEnabledWindow()
    {
        WriteConfig();

        var (exit, stdout, stderr) = await RunAsync("config", "set-window", "22:00", "6:00", "--json");

        exit.Should().Be(0, stdout + stderr);
        var doc = Json(stdout);
        doc["window"]!["enabled"]!.GetValue<bool>().Should().BeTrue();
        doc["window"]!["start"]!.GetValue<string>().Should().Be("22:00");
        doc["window"]!["end"]!.GetValue<string>().Should().Be("06:00");
        doc["window"]!["days"]!.GetValue<string>().Should().Be("all");
        doc["window"]!["description"]!.GetValue<string>().Should().Be("Open from 22:00 to 06:00 every day");
        var window = ReadConfig()["backupWindow"]!;
        window["enabled"]!.GetValue<bool>().Should().BeTrue();
        window["start"]!.GetValue<string>().Should().Be("22:00");
        window["end"]!.GetValue<string>().Should().Be("06:00");
        window["days"]!.GetValue<string>().Should().Be("all");
    }

    [Fact]
    public async Task SetWindow_WithDays_NormalizesThem_AndEnablesAShippedDisabledWindow()
    {
        WriteConfig(root => root["backupWindow"] = new JsonObject { ["enabled"] = false, ["start"] = "01:00", ["end"] = "02:00", ["days"] = "all", ["note"] = "mine" });

        var (exit, stdout, _) = await RunAsync("config", "set-window", "23:00", "05:00", "--days", "Mon-Fri", "--json");

        exit.Should().Be(0, stdout);
        Json(stdout)["window"]!["days"]!.GetValue<string>().Should().Be("weekdays");
        var window = ReadConfig()["backupWindow"]!;
        window["enabled"]!.GetValue<bool>().Should().BeTrue();
        window["days"]!.GetValue<string>().Should().Be("weekdays");
        window["note"]!.GetValue<string>().Should().Be("mine");
    }

    [Fact]
    public async Task SetWindow_WarnsWhenItRestrictsNothing()
    {
        WriteConfig();

        var (exit, stdout, _) = await RunAsync("config", "set-window", "00:00", "00:00", "--json");

        exit.Should().Be(0);
        Json(stdout)["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).Should().Contain(w => w.Contains("restricts nothing"));
    }

    [Theory]
    [InlineData("25:00", "06:00", null, "backup window start")]
    [InlineData("22:00", "6am", null, "backup window end")]
    [InlineData("22:00", "06:00", "someday", "backup window days")]
    public async Task SetWindow_RejectsBadValues_WithExit2_AndTouchesNothing(string start, string end, string? days, string expected)
    {
        WriteConfig();
        var before = File.ReadAllText(AppPaths.ConfigFile);
        string[] args = days == null ? ["config", "set-window", start, end, "--json"] : ["config", "set-window", start, end, "--days", days, "--json"];

        var (exit, stdout, _) = await RunAsync(args);

        exit.Should().Be(2);
        Json(stdout)["error"]!["message"]!.GetValue<string>().Should().Contain(expected);
        File.ReadAllText(AppPaths.ConfigFile).Should().Be(before);
    }

    [Fact]
    public async Task ClearWindow_RemovesTheWindow_AndANoOpChangesNothing()
    {
        WriteConfig();
        await RunAsync("config", "set-window", "22:00", "06:00");

        var (exit, stdout, _) = await RunAsync("config", "clear-window", "--json");
        exit.Should().Be(0);
        Json(stdout)["existed"]!.GetValue<bool>().Should().BeTrue();
        ReadConfig().ContainsKey("backupWindow").Should().BeFalse();

        var (againExit, again, _) = await RunAsync("config", "clear-window", "--json");
        againExit.Should().Be(0);
        Json(again)["existed"]!.GetValue<bool>().Should().BeFalse();
        Json(again)["changed"]!.GetValue<bool>().Should().BeFalse();
    }

    // ---- schedule next ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Next_ListsEveryTarget_WithItsScheduleAndState()
    {
        Seed();
        WriteConfig(root =>
        {
            root["watchDirectories"]![0]!["schedule"] = "daily 02:00";
            root["verification"] = new JsonObject { ["enabled"] = true, ["verifyInterval"] = "7.00:00:00" };
        });

        var (exit, stdout, stderr) = await RunAsync("schedule", "next", "--json");

        exit.Should().Be(0, stdout + stderr);
        var doc = Json(stdout);
        doc["paused"]!.GetValue<bool>().Should().BeFalse();
        doc["backupWindow"]!["active"]!.GetValue<bool>().Should().BeFalse();
        doc["backupWindow"]!["openNow"]!.GetValue<bool>().Should().BeTrue();
        var targets = doc["targets"]!.AsArray();
        targets.Select(t => t!["kind"]!.GetValue<string>()).Should().Equal("folder", "system", "verification", "retention");

        var folder = targets[0]!;
        folder["path"]!.GetValue<string>().Should().Be(WatchedDirectory);
        folder["schedule"]!["expression"]!.GetValue<string>().Should().Be("daily 02:00");
        folder["schedule"]!["description"]!.GetValue<string>().Should().Be("Every day at 02:00");
        folder["schedule"]!["source"]!.GetValue<string>().Should().Be("folder");
        folder["state"]!.GetValue<string>().Should().Be("due", "a folder that was never backed up is due at once");
        folder["reason"]!.GetValue<string>().Should().Contain("Never done");

        targets[1]!["state"]!.GetValue<string>().Should().Be("disabled", "the system backup is off in this config (and Windows only)");
        targets[2]!["state"]!.GetValue<string>().Should().Be("scheduled");
        targets[2]!["reason"]!.GetValue<string>().Should().Contain("no snapshots");
    }

    [Fact]
    public async Task Next_ShowsTheDefaultIntervalFolder_AndAnOffFolder()
    {
        var second = Path.Combine(Root, "second");
        Directory.CreateDirectory(second);
        WriteConfig(root => root["watchDirectories"] = new JsonArray(
            new JsonObject { ["path"] = WatchedDirectory, ["storageType"] = null },
            new JsonObject { ["path"] = second, ["storageType"] = null, ["schedule"] = "off" }));

        var (_, stdout, _) = await RunAsync("schedule", "next", "--json");

        var targets = Json(stdout)["targets"]!.AsArray();
        targets[0]!["schedule"]!["source"]!.GetValue<string>().Should().Be("default");
        targets[0]!["schedule"]!["expression"].Should().BeNull();
        targets[0]!["schedule"]!["description"]!.GetValue<string>().Should().Be("Every hour");
        targets[1]!["state"]!.GetValue<string>().Should().Be("off");
        targets[1]!["nextStartUtc"].Should().BeNull();
    }

    [Fact]
    public async Task Next_WhenTheWindowIsClosed_SaysDueWorkWaitsForIt()
    {
        var (start, end) = ClosedWindow();
        WriteConfig(root => root["backupWindow"] = new JsonObject { ["enabled"] = true, ["start"] = start, ["end"] = end, ["days"] = "all" });

        var (exit, stdout, stderr) = await RunAsync("schedule", "next", "--json");

        exit.Should().Be(0, stdout + stderr);
        var doc = Json(stdout);
        doc["backupWindow"]!["active"]!.GetValue<bool>().Should().BeTrue();
        doc["backupWindow"]!["openNow"]!.GetValue<bool>().Should().BeFalse();
        var opens = DateTime.Parse(doc["backupWindow"]!["nextOpenUtc"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
        opens.Should().BeCloseTo(DateTime.UtcNow.AddHours(2), TimeSpan.FromMinutes(3));

        var folder = doc["targets"]![0]!;
        folder["state"]!.GetValue<string>().Should().Be("waiting-for-window");
        folder["reason"]!.GetValue<string>().Should().Contain("backup window is closed");
        DateTime.Parse(folder["nextStartUtc"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime()
            .Should().BeCloseTo(opens, TimeSpan.FromSeconds(1));
        DateTime.Parse(doc["nextDueUtc"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime()
            .Should().BeCloseTo(opens, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Next_WhilePaused_SaysSo()
    {
        WriteConfig();
        await RunAsync("pause", "--json");

        var (_, stdout, _) = await RunAsync("schedule", "next", "--json");

        var doc = Json(stdout);
        doc["paused"]!.GetValue<bool>().Should().BeTrue();
        doc["targets"]![0]!["state"]!.GetValue<string>().Should().Be("paused");
        doc["targets"]![0]!["reason"]!.GetValue<string>().Should().Contain("paused");
    }

    [Fact]
    public async Task Next_AfterABackup_ShowsAScheduledNextRun()
    {
        Seed();
        WriteConfig(root => root["watchDirectories"]![0]!["schedule"] = "daily 12:00");
        (await RunAsync("run-due")).ExitCode.Should().Be(0);

        var (_, stdout, _) = await RunAsync("schedule", "next", "--json");

        var folder = Json(stdout)["targets"]![0]!;
        folder["state"]!.GetValue<string>().Should().Be("scheduled");
        folder["lastUtc"].Should().NotBeNull();
        var next = DateTime.Parse(folder["nextStartUtc"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
        next.Should().BeAfter(DateTime.UtcNow);
        folder["nextStartLocal"]!.GetValue<string>().Should().Contain("T12:00:00");
    }

    [Fact]
    public async Task Next_PrintsReadableText()
    {
        WriteConfig(root => root.Remove("backupWindow"));

        var (exit, stdout, _) = await RunAsync("schedule", "next");

        exit.Should().Be(0);
        stdout.Should().Contain("Protection is running.").And.Contain("Backup window: No backup window").And.Contain(WatchedDirectory);

        // The shipped example carries a switched-off window, which restricts nothing either.
        WriteConfig(root => root["backupWindow"] = new JsonObject { ["enabled"] = false, ["start"] = "22:00", ["end"] = "06:00", ["days"] = "all" });
        var (_, shipped, _) = await RunAsync("schedule", "next");
        shipped.Should().Contain("switched off");
    }

    // ---- status ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Status_ReportsTheFolderSchedule_TheNextDueTime_AndTheWindow()
    {
        Seed();
        var (start, end) = ClosedWindow();
        WriteConfig(root =>
        {
            root["watchDirectories"]![0]!["schedule"] = "weekdays 18:30";
            root["backupWindow"] = new JsonObject { ["enabled"] = true, ["start"] = start, ["end"] = end, ["days"] = "all" };
            root["systemBackup"]!["schedule"] = "weekly sun 03:00";
        });

        var (exit, stdout, stderr) = await RunAsync("status", "--json");

        exit.Should().Be(0, stdout + stderr);
        var doc = Json(stdout);
        doc["backupWindow"]!["active"]!.GetValue<bool>().Should().BeTrue();
        doc["backupWindow"]!["openNow"]!.GetValue<bool>().Should().BeFalse();
        doc["backupWindow"]!["start"]!.GetValue<string>().Should().Be(start);
        doc["systemBackup"]!["schedule"]!.GetValue<string>().Should().Be("weekly sun 03:00");
        doc["systemBackup"]!["scheduleDescription"]!.GetValue<string>().Should().Be("Every Sunday at 03:00");
        var group = doc["groups"]![0]!;
        group["schedule"]!["expression"]!.GetValue<string>().Should().Be("weekdays 18:30");
        group["schedule"]!["description"]!.GetValue<string>().Should().Be("Every weekday at 18:30");
        group["schedule"]!["source"]!.GetValue<string>().Should().Be("folder");
        group["overdue"]!.GetValue<bool>().Should().BeTrue("never backed up");
        group["waitingForWindow"]!.GetValue<bool>().Should().BeTrue();
        doc["nextDueUtc"].Should().NotBeNull();
    }

    [Fact]
    public async Task Status_WithoutSchedulesOrWindow_KeepsTheOldShape_AndAddsTheNewFields()
    {
        WriteConfig(root => root.Remove("backupWindow"));

        var (exit, stdout, _) = await RunAsync("status", "--json");

        exit.Should().Be(0);
        var doc = Json(stdout);
        doc["backupInterval"]!.GetValue<string>().Should().Be(ReadConfig()["backupInterval"]!.GetValue<string>());
        doc["backupWindow"]!["configured"]!.GetValue<bool>().Should().BeFalse();
        doc["backupWindow"]!["openNow"]!.GetValue<bool>().Should().BeTrue();
        var group = doc["groups"]![0]!;
        group["schedule"]!["source"]!.GetValue<string>().Should().Be("default");
        group["schedule"]!["expression"].Should().BeNull();
        group["waitingForWindow"]!.GetValue<bool>().Should().BeFalse();
    }

    // ---- run-due ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RunDue_OutsideTheWindow_BacksNothingUp_AndSaysWhy_ThenRunsWhenTheWindowIsOpen()
    {
        Seed();
        var (start, end) = ClosedWindow();
        WriteConfig(root => root["backupWindow"] = new JsonObject { ["enabled"] = true, ["start"] = start, ["end"] = end, ["days"] = "all" });

        var (exit, stdout, stderr) = await RunAsync("run-due", "--json");

        exit.Should().Be(0, stdout + stderr);
        var closed = Json(stdout);
        closed["backups"]!["succeeded"]!.GetValue<int>().Should().Be(0);
        closed["backupWindow"]!["closed"]!.GetValue<bool>().Should().BeTrue();
        closed["backupWindow"]!["deferred"]!.GetValue<int>().Should().BeGreaterThanOrEqualTo(1);
        closed["nothingDue"]!.GetValue<bool>().Should().BeFalse("work is waiting for the window");
        ManifestCount().Should().Be(0);

        (await RunAsync("config", "set-window", "00:00", "00:00")).ExitCode.Should().Be(0);
        var (openExit, openOut, openErr) = await RunAsync("run-due", "--json");

        openExit.Should().Be(0, openErr);
        var open = Json(openOut);
        open["backups"]!["succeeded"]!.GetValue<int>().Should().Be(1, "the due work was only waiting");
        open["backupWindow"]!["closed"]!.GetValue<bool>().Should().BeFalse();
        ManifestCount().Should().Be(1);
    }

    [Fact]
    public async Task RunDue_FollowsAFolderSchedule_ASecondRunRightAfterHasNothingDue()
    {
        Seed();
        WriteConfig(root => root["watchDirectories"]![0]!["schedule"] = "daily 03:00");

        var (firstExit, first, _) = await RunAsync("run-due", "--json");
        firstExit.Should().Be(0);
        Json(first)["backups"]!["succeeded"]!.GetValue<int>().Should().Be(1);

        var (_, second, _) = await RunAsync("run-due", "--json");
        var doc = Json(second);
        doc["backups"]!["succeeded"]!.GetValue<int>().Should().Be(0);
        doc["nothingDue"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task RunDue_NeverBacksUpAnOffFolder()
    {
        Seed();
        WriteConfig(root => root["watchDirectories"]![0]!["schedule"] = "off");

        var (exit, stdout, _) = await RunAsync("run-due", "--json");

        exit.Should().Be(0);
        var doc = Json(stdout);
        doc["backups"]!["succeeded"]!.GetValue<int>().Should().Be(0);
        doc["nothingDue"]!.GetValue<bool>().Should().BeTrue();
        ManifestCount().Should().Be(0);
    }

    [Fact]
    public async Task RunDue_WithAnOldConfig_BehavesAsBefore()
    {
        Seed();
        WriteConfig(root =>
        {
            root.Remove("backupWindow");
            root["backupInterval"] = "00:30:00";
        });

        var (_, first, _) = await RunAsync("run-due", "--json");
        Json(first)["backups"]!["succeeded"]!.GetValue<int>().Should().Be(1);
        Json(first)["backupWindow"]!["closed"]!.GetValue<bool>().Should().BeFalse();

        var (_, second, _) = await RunAsync("run-due", "--json");
        Json(second)["backups"]!["succeeded"]!.GetValue<int>().Should().Be(0, "inside the 30 minute interval");
    }

    // ---- manual backups are never held back --------------------------------------------------------------------------

    [Fact]
    public async Task ManualBackup_RunsEvenWhenTheWindowIsClosed()
    {
        Seed();
        var (start, end) = ClosedWindow();
        WriteConfig(root => root["backupWindow"] = new JsonObject { ["enabled"] = true, ["start"] = start, ["end"] = end, ["days"] = "all" });

        var (exit, stdout, stderr) = await RunAsync("backup", WatchedDirectory);

        exit.Should().Be(0, stdout + stderr);
        ManifestCount().Should().Be(1);
    }

    // ---- the registry ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheCommandReference_ListsTheNewCommands()
    {
        var (_, stdout, _) = await RunAsync("commands", "--json");

        var names = Json(stdout)["commands"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToList();
        names.Should().Contain(["schedule preview", "schedule next", "config set-schedule", "config clear-schedule", "config set-window", "config clear-window"]);
        var preview = Json(stdout)["commands"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "schedule preview")!;
        preview["windowsOnly"]!.GetValue<bool>().Should().BeFalse();
        preview["mutates"]!.GetValue<bool>().Should().BeFalse();
    }
}
