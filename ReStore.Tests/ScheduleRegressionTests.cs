using FluentAssertions;
using ReStore.Core.src.monitoring;

namespace ReStore.Tests;

/// <summary>Regressions found by the generative schedule tests.</summary>
[Collection("GlobalState")]
public class ScheduleRegressionTests : CliConfigTestBase
{
    [Theory]
    [InlineData("mon-sun 09:15")]
    [InlineData("sun-sat 09:15,10:00")]
    [InlineData("mon,tue,wed,thu,fri,sat,sun 07:00")]
    [InlineData("sat-fri 23:59")]
    public void EveryDayWrittenAsAList_HasAnExpressionThatParsesAgain(string text)
    {
        var schedule = BackupSchedule.Parse(text);

        BackupSchedule.TryParse(schedule.Expression, out var again, out var error).Should().BeTrue($"'{schedule.Expression}' must parse: {error}");
        again!.Expression.Should().Be(schedule.Expression);
        again.Description.Should().Be(schedule.Description);
    }

    [Fact]
    public async Task SetSchedule_WithEveryDayAsAList_StoresSomethingTheConfigCanLoadAgain()
    {
        WriteConfig();

        var (setExit, setOut, setErr) = await RunAsync("config", "set-schedule", WatchedDirectory, "mon-sun 09:15", "--json");
        var (validateExit, validateOut, _) = await RunAsync("config", "validate", "--json");
        var (statusExit, statusOut, _) = await RunAsync("status", "--json");

        setExit.Should().Be(0, setOut + setErr);
        validateExit.Should().Be(0, validateOut);
        statusExit.Should().Be(0, statusOut);
    }
}
