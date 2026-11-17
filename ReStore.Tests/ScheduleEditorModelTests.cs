using System.Globalization;
using FluentAssertions;
using ReStore.Core.src.utils;
using ReStore.Services;

namespace ReStore.Tests;

/// <summary>The logic behind the Settings schedule selector and the backup window controls (the UI itself is thin).</summary>
public class ScheduleEditorModelTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    // ---- expression -> selector ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, ScheduleMode.Default, "")]
    [InlineData("", ScheduleMode.Default, "")]
    [InlineData("  ", ScheduleMode.Default, "")]
    [InlineData("every 4h", ScheduleMode.EveryHours, "4")]
    [InlineData("every 24h", ScheduleMode.EveryHours, "24")]
    [InlineData("every 1d", ScheduleMode.EveryHours, "24")]
    [InlineData("every 90m", ScheduleMode.Custom, "every 90m")]
    [InlineData("daily 02:00", ScheduleMode.Daily, "02:00")]
    [InlineData("Daily 2:00", ScheduleMode.Daily, "02:00")]
    [InlineData("weekdays 18:30", ScheduleMode.Weekdays, "18:30")]
    [InlineData("mon-fri 18:30", ScheduleMode.Weekdays, "18:30")]
    [InlineData("daily 02:00,14:00", ScheduleMode.Custom, "daily 02:00,14:00")]
    [InlineData("weekends 10:00", ScheduleMode.Custom, "weekends 10:00")]
    [InlineData("mon,wed 07:00", ScheduleMode.Custom, "mon,wed 07:00")]
    [InlineData("weekly sun 03:00", ScheduleMode.Custom, "weekly sun 03:00")]
    [InlineData("monthly last 04:00", ScheduleMode.Custom, "monthly last 04:00")]
    [InlineData("cron 0 2 * * *", ScheduleMode.Custom, "cron 0 2 * * *")]
    [InlineData("off", ScheduleMode.Custom, "off")]
    [InlineData("daily 2pm", ScheduleMode.Custom, "daily 2pm")]
    public void FromExpression_PicksTheSimplestChoiceThatExpressesIt(string? expression, ScheduleMode mode, string text)
        => ScheduleEditorModel.FromExpression(expression).Should().Be(new ScheduleEditorValue(mode, text));

    // ---- selector -> expression ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ScheduleMode.EveryHours, "4", "every 4h")]
    [InlineData(ScheduleMode.EveryHours, "1.5", "every 90m")]
    [InlineData(ScheduleMode.EveryHours, " 24 ", "every 1d")]
    [InlineData(ScheduleMode.EveryHours, "0.5", "every 30m")]
    [InlineData(ScheduleMode.Daily, "02:00", "daily 02:00")]
    [InlineData(ScheduleMode.Daily, "2:05", "daily 02:05")]
    [InlineData(ScheduleMode.Weekdays, "18:30", "weekdays 18:30")]
    [InlineData(ScheduleMode.Custom, "MON,WED 07:15", "mon,wed 07:15")]
    [InlineData(ScheduleMode.Custom, "off", "off")]
    [InlineData(ScheduleMode.Custom, "cron  0 */6 * * *", "cron 0 */6 * * *")]
    public void TryBuild_ProducesTheNormalizedExpression(ScheduleMode mode, string text, string expected)
    {
        ScheduleEditorModel.TryBuild(mode, text, out var expression, out var error).Should().BeTrue(error);

        expression.Should().Be(expected);
    }

    [Fact]
    public void TryBuild_Default_MeansNoScheduleOfItsOwn()
    {
        ScheduleEditorModel.TryBuild(ScheduleMode.Default, "ignored", out var expression, out var error).Should().BeTrue();

        expression.Should().BeNull();
        error.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ScheduleMode.EveryHours, "", "number of hours")]
    [InlineData(ScheduleMode.EveryHours, "abc", "number of hours")]
    [InlineData(ScheduleMode.EveryHours, "0", "number of hours")]
    [InlineData(ScheduleMode.EveryHours, "-2", "number of hours")]
    [InlineData(ScheduleMode.EveryHours, "0.05", "minimum is 5m")]
    [InlineData(ScheduleMode.EveryHours, "99999", "maximum is 365d")]
    [InlineData(ScheduleMode.Daily, "", "Expected HH:mm")]
    [InlineData(ScheduleMode.Daily, "2pm", "Expected HH:mm")]
    [InlineData(ScheduleMode.Daily, "25:00", "Expected HH:mm")]
    [InlineData(ScheduleMode.Weekdays, "noon", "Expected HH:mm")]
    [InlineData(ScheduleMode.Custom, "", "Enter a schedule")]
    [InlineData(ScheduleMode.Custom, "every 1m", "minimum is 5m")]
    [InlineData(ScheduleMode.Custom, "cron 61 * * * *", "out of range")]
    public void TryBuild_RejectsInvalidInput_WithAMessageForTheUser(ScheduleMode mode, string text, string expected)
    {
        ScheduleEditorModel.TryBuild(mode, text, out var expression, out var error).Should().BeFalse();

        expression.Should().BeNull();
        error.Should().Contain(expected);
        char.IsUpper(error[0]).Should().BeTrue("messages start with a capital letter");
    }

    [Fact]
    public void EveryChoice_RoundTripsThroughTheSelector()
    {
        foreach (var expression in new[] { "every 4h", "daily 02:00", "weekdays 18:30", "every 1d", "weekly sun 03:00", "mon,wed 07:15", "cron 0 2 * * *", "off", "daily 02:00,14:00" })
        {
            var value = ScheduleEditorModel.FromExpression(expression);
            ScheduleEditorModel.TryBuild(value.Mode, value.Text, out var rebuilt, out var error).Should().BeTrue(error);
            ScheduleEditorModel.FromExpression(rebuilt).Should().Be(value);
        }
    }

    // ---- preview ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Preview_OfAClockSchedule_ListsTheNextRuns()
    {
        var now = new DateTime(2025, 1, 6, 10, 0, 0, DateTimeKind.Utc);

        var text = ScheduleEditorModel.Preview(ScheduleMode.Daily, "18:30", "Every hour", now, Utc);

        text.Should().Be("Every day at 18:30. Next runs: today 18:30, tomorrow 18:30, " + new DateTime(2025, 1, 8).ToString("ddd d MMM", CultureInfo.CurrentCulture) + " 18:30");
    }

    [Fact]
    public void Preview_OfTheDefault_SaysItFollowsTheGlobalInterval()
    {
        ScheduleEditorModel.Preview(ScheduleMode.Default, "", "Follows the global interval: every 1 hour.", DateTime.UtcNow, Utc)
            .Should().Be("Follows the global interval: every 1 hour.");
    }

    [Fact]
    public void Preview_OfInvalidInput_IsTheValidationMessage()
    {
        ScheduleEditorModel.Preview(ScheduleMode.Daily, "2pm", "x", DateTime.UtcNow, Utc).Should().Contain("Expected HH:mm at 'daily 2pm'");
    }

    [Fact]
    public void Preview_OfIntervalAndOff_ExplainsThem()
    {
        ScheduleEditorModel.Preview(ScheduleMode.EveryHours, "4", "x", DateTime.UtcNow, Utc).Should().Be("Every 4 hours, counted from the last backup.");
        ScheduleEditorModel.Preview(ScheduleMode.Custom, "off", "x", DateTime.UtcNow, Utc).Should().Contain("only backed up when its files change");
    }

    // ---- backup window ---------------------------------------------------------------------------------------------

    [Fact]
    public void Window_FromConfig_NoWindow_IsAnUncheckedDefault()
    {
        BackupWindowEditorModel.FromConfig(null).Should().Be((false, "22:00", "06:00", WindowDaysChoice.EveryDay, string.Empty));
    }

    [Theory]
    [InlineData("all", WindowDaysChoice.EveryDay, "")]
    [InlineData("weekdays", WindowDaysChoice.Weekdays, "")]
    [InlineData("Mon-Fri", WindowDaysChoice.Weekdays, "")]
    [InlineData("weekends", WindowDaysChoice.Weekends, "")]
    [InlineData("mon,wed", WindowDaysChoice.Custom, "mon,wed")]
    public void Window_FromConfig_MapsTheDays(string days, WindowDaysChoice choice, string custom)
    {
        var shown = BackupWindowEditorModel.FromConfig(new BackupWindowConfig { Enabled = true, Start = "23:00", End = "5:30", Days = days });

        shown.Should().Be((true, "23:00", "05:30", choice, custom));
    }

    [Fact]
    public void Window_FromConfig_ShowsAMalformedWindowAsItIs()
    {
        var shown = BackupWindowEditorModel.FromConfig(new BackupWindowConfig { Enabled = true, Start = "nonsense", End = "06:00", Days = "all" });

        shown.Should().Be((true, "nonsense", "06:00", WindowDaysChoice.Custom, "all"));
    }

    [Fact]
    public void Window_TryBuild_Unchecked_WithNoWindowBefore_WritesNothing()
    {
        BackupWindowEditorModel.TryBuild(false, "22:00", "06:00", WindowDaysChoice.EveryDay, "", null, out var config, out var error).Should().BeTrue();

        config.Should().BeNull();
        error.Should().BeEmpty();
    }

    [Fact]
    public void Window_TryBuild_Unchecked_KeepsAnExistingWindowSwitchedOff()
    {
        var existing = new BackupWindowConfig { Enabled = true, Start = "22:00", End = "06:00", Days = "all" };

        BackupWindowEditorModel.TryBuild(false, "21:00", "07:00", WindowDaysChoice.Weekdays, "", existing, out var config, out _).Should().BeTrue();

        config.Should().BeEquivalentTo(new { Enabled = false, Start = "21:00", End = "07:00", Days = "weekdays" });
    }

    [Fact]
    public void Window_TryBuild_Checked_NormalizesAndKeepsUnknownMembers()
    {
        var extension = new Dictionary<string, System.Text.Json.JsonElement> { ["note"] = System.Text.Json.JsonDocument.Parse("\"x\"").RootElement.Clone() };
        var existing = new BackupWindowConfig { Enabled = false, ExtensionData = extension };

        BackupWindowEditorModel.TryBuild(true, "6:00", "7:30", WindowDaysChoice.Custom, "Fri, Mon", existing, out var config, out var error).Should().BeTrue(error);

        config!.Enabled.Should().BeTrue();
        config.Start.Should().Be("06:00");
        config.End.Should().Be("07:30");
        config.Days.Should().Be("mon,fri");
        config.ExtensionData.Should().ContainKey("note");
    }

    [Theory]
    [InlineData("25:00", "06:00", WindowDaysChoice.EveryDay, "", "Backup window start")]
    [InlineData("22:00", "6am", WindowDaysChoice.EveryDay, "", "Backup window end")]
    [InlineData("22:00", "06:00", WindowDaysChoice.Custom, "", "Enter the days")]
    [InlineData("22:00", "06:00", WindowDaysChoice.Custom, "someday", "Backup window days")]
    public void Window_TryBuild_RejectsInvalidInput(string start, string end, WindowDaysChoice days, string custom, string expected)
    {
        BackupWindowEditorModel.TryBuild(true, start, end, days, custom, null, out var config, out var error).Should().BeFalse();

        config.Should().BeNull();
        error.Should().Contain(expected);
    }

    [Fact]
    public void Window_TryBuild_ValidatesEvenWhenSwitchedOff_IfAWindowAlreadyExists()
    {
        BackupWindowEditorModel.TryBuild(false, "99:00", "06:00", WindowDaysChoice.EveryDay, "", new BackupWindowConfig(), out _, out var error).Should().BeFalse();

        error.Should().Contain("Backup window start");
    }

    [Fact]
    public void Window_Preview_DescribesTheWindow_AndWarnsWhenItRestrictsNothing()
    {
        BackupWindowEditorModel.Preview(true, "22:00", "06:00", WindowDaysChoice.Weekdays, "").Should().StartWith("Open from 22:00 to 06:00 on weekdays.");
        BackupWindowEditorModel.Preview(false, "22:00", "06:00", WindowDaysChoice.EveryDay, "").Should().StartWith("Switched off.");
        BackupWindowEditorModel.Preview(true, "08:00", "08:00", WindowDaysChoice.EveryDay, "").Should().Contain("restricts nothing");
        BackupWindowEditorModel.Preview(true, "xx", "06:00", WindowDaysChoice.EveryDay, "").Should().Contain("Backup window start");
    }
}
