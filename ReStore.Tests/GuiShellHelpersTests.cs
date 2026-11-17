using System.Globalization;
using FluentAssertions;
using ReStore.Services;
using Xunit;

namespace ReStore.Tests;

public class NumberInputTests
{
    private static readonly CultureInfo German = new("de-DE");
    private static readonly CultureInfo English = new("en-US");

    [Theory]
    [InlineData("1,5", 1.5)]
    [InlineData("1.5", 1.5)]
    [InlineData(" 24 ", 24)]
    public void Double_accepts_comma_and_point_in_a_comma_culture(string text, double expected)
    {
        NumberInput.TryParseDouble(text, out var value, German).Should().BeTrue();
        value.Should().Be(expected);
    }

    [Fact]
    public void Double_reads_point_in_english_and_rejects_garbage()
    {
        NumberInput.TryParseDouble("1.5", out var value, English).Should().BeTrue();
        value.Should().Be(1.5);

        NumberInput.TryParseDouble("abc", out _, English).Should().BeFalse();
        NumberInput.TryParseDouble("", out _, English).Should().BeFalse();
        NumberInput.TryParseDouble(null, out _, English).Should().BeFalse();
        NumberInput.TryParseDouble("NaN", out _, English).Should().BeFalse();
        NumberInput.TryParseDouble("Infinity", out _, English).Should().BeFalse();
    }

    [Fact]
    public void Formatted_hours_round_trip_in_every_culture()
    {
        foreach (var culture in new[] { German, English, CultureInfo.InvariantCulture })
        {
            var text = NumberInput.FormatHours(1.5, culture);
            NumberInput.TryParseDouble(text, out var value, culture).Should().BeTrue(text);
            value.Should().Be(1.5);
        }

        NumberInput.FormatHours(1.5, German).Should().Be("1,5");
    }

    [Fact]
    public void Integers_reject_decimals_and_overflow()
    {
        NumberInput.TryParseInt("42", out var i, German).Should().BeTrue();
        i.Should().Be(42);
        NumberInput.TryParseInt("1,5", out _, German).Should().BeFalse();
        NumberInput.TryParseInt("99999999999", out _, German).Should().BeFalse();
        NumberInput.TryParseLong("99999999999", out var l, German).Should().BeTrue();
        l.Should().Be(99999999999);
    }
}

public class SingleInstanceTests
{
    [Fact]
    public void Pipe_name_is_scoped_by_user_and_session()
    {
        var a = SingleInstanceNames.PipeName("S-1-5-21-1-2-3-1001", 1);
        var b = SingleInstanceNames.PipeName("S-1-5-21-1-2-3-1002", 1);
        var c = SingleInstanceNames.PipeName("S-1-5-21-1-2-3-1001", 2);

        new[] { a, b, c }.Distinct().Should().HaveCount(3);
        a.Should().Be("ReStore_CommandPipe_S-1-5-21-1-2-3-1001_1");
        SingleInstanceNames.PipeName("a\\b/c", 0).Should().NotContainAny("\\", "/");
        SingleInstanceNames.PipeName(null, 0).Should().StartWith("ReStore_CommandPipe_");
    }

    [Fact]
    public void Commands_before_ready_are_queued_and_replayed_in_order()
    {
        var queue = new StartupCommandQueue();
        queue.Submit("/show");
        queue.Submit("--share \"a.txt\"");
        queue.Submit("  ");
        queue.PendingCount.Should().Be(2);

        var received = new List<string>();
        queue.MarkReady(received.Add);

        received.Should().Equal("/show", "--share \"a.txt\"");
        queue.PendingCount.Should().Be(0);
    }

    [Fact]
    public void Commands_after_ready_go_straight_to_the_handler()
    {
        var queue = new StartupCommandQueue();
        var received = new List<string>();
        queue.MarkReady(received.Add);

        queue.Submit("/startWatcher");

        received.Should().Equal("/startWatcher");
    }

    [Fact]
    public void Pending_queue_is_bounded()
    {
        var queue = new StartupCommandQueue();
        for (var i = 0; i < 500; i++)
        {
            queue.Submit("/show");
        }

        queue.PendingCount.Should().BeLessThanOrEqualTo(64);
    }
}

public class PackagedPathVirtualizationTests
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "p5c-home");
    private static readonly string Roaming = Path.Combine(Home, "AppData", "Roaming");
    private static readonly string Local = Path.Combine(Home, "AppData", "Local");

    [Theory]
    [InlineData("AppData/Roaming/Foo", true)]
    [InlineData("AppData/Local", true)]
    [InlineData("AppData/Local/Temp/x", true)]
    [InlineData("AppData/LocalLow/x", false)]
    [InlineData("AppData/Roaming2", false)]
    [InlineData("Documents", false)]
    public void Only_paths_under_appdata_are_flagged_for_packaged_apps(string relative, bool expected)
    {
        var path = Path.Combine(Home, relative.Replace('/', Path.DirectorySeparatorChar));

        PackagedPathVirtualization.IsAffected(path, isPackaged: true, Roaming, Local).Should().Be(expected);
    }

    [Fact]
    public void Empty_target_and_unpackaged_apps_are_never_flagged()
    {
        PackagedPathVirtualization.IsAffected("", isPackaged: true, Roaming, Local).Should().BeFalse();
        PackagedPathVirtualization.IsAffected(Path.Combine(Roaming, "Foo"), isPackaged: false, Roaming, Local)
            .Should().BeFalse();
    }
}
