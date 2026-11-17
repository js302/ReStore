using FluentAssertions;
using ReStore.Services;

namespace ReStore.Tests;

public class WindowPlacementTests
{
    private static readonly PlacementRect Screen1080 = new(0, 0, 1920, 1040);
    private static readonly PlacementRect SecondRight = new(1920, 0, 1920, 1040);

    [Fact]
    public void DefaultBounds_IsEightyPercentOfWorkAreaAndCentred()
    {
        var bounds = WindowPlacementPolicy.DefaultBounds(Screen1080);

        bounds.Width.Should().BeApproximately(1536, 0.001);
        bounds.Height.Should().BeApproximately(832, 0.001);
        bounds.Left.Should().BeApproximately(192, 0.001);
        bounds.Top.Should().BeApproximately(104, 0.001);
    }

    [Fact]
    public void DefaultBounds_NeverBelowMinimumOnAMediumScreen()
    {
        // 1366x768 at 100%: 80% is 1093x614, below 1100x700.
        var bounds = WindowPlacementPolicy.DefaultBounds(new PlacementRect(0, 0, 1366, 728));

        bounds.Width.Should().Be(1100);
        bounds.Height.Should().Be(700);
    }

    [Fact]
    public void MinimumSize_NeverExceedsAWorkAreaTooSmallForIt()
    {
        // 1920x1080 at 150% leaves 1280x680 DIPs; 1366x768 at 150% leaves 910x485.
        WindowPlacementPolicy.MinimumSize(new PlacementRect(0, 0, 1280, 680)).Should().Be((1100d, 680d));
        WindowPlacementPolicy.MinimumSize(new PlacementRect(0, 0, 910, 485)).Should().Be((910d, 485d));
        WindowPlacementPolicy.DefaultBounds(new PlacementRect(0, 0, 910, 485)).Width.Should().Be(910);
    }

    [Fact]
    public void IsVisible_TrueWhenOnAConnectedScreen()
    {
        WindowPlacementPolicy.IsVisible(new PlacementRect(100, 100, 1200, 800), [Screen1080]).Should().BeTrue();
        WindowPlacementPolicy.IsVisible(new PlacementRect(2000, 50, 1200, 800), [Screen1080, SecondRight]).Should().BeTrue();
    }

    [Fact]
    public void IsVisible_FalseWhenItsMonitorWasDisconnected()
    {
        WindowPlacementPolicy.IsVisible(new PlacementRect(2000, 50, 1200, 800), [Screen1080]).Should().BeFalse();
    }

    [Fact]
    public void IsVisible_FalseWhenTitleBarIsAboveOrBelowTheScreen()
    {
        WindowPlacementPolicy.IsVisible(new PlacementRect(100, -400, 1200, 800), [Screen1080]).Should().BeFalse();
        WindowPlacementPolicy.IsVisible(new PlacementRect(100, 1100, 1200, 800), [Screen1080]).Should().BeFalse();
    }

    [Fact]
    public void IsVisible_FalseWhenOnlyAnEdgeSliverOverlaps()
    {
        WindowPlacementPolicy.IsVisible(new PlacementRect(-1150, 100, 1200, 800), [Screen1080]).Should().BeFalse();
        WindowPlacementPolicy.IsVisible(new PlacementRect(-1000, 100, 1200, 800), [Screen1080]).Should().BeTrue();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void IsVisible_FalseForNonFiniteValues(double bad)
    {
        WindowPlacementPolicy.IsVisible(new PlacementRect(bad, 0, 1200, 800), [Screen1080]).Should().BeFalse();
        WindowPlacementPolicy.IsVisible(new PlacementRect(0, 0, bad, 800), [Screen1080]).Should().BeFalse();
    }

    [Fact]
    public void IsVisible_FalseForEmptyOrNoScreens()
    {
        WindowPlacementPolicy.IsVisible(new PlacementRect(0, 0, 0, 0), [Screen1080]).Should().BeFalse();
        WindowPlacementPolicy.IsVisible(new PlacementRect(0, 0, 1200, 800), []).Should().BeFalse();
    }

    [Fact]
    public void Resolve_UsesSavedBoundsWhenVisible()
    {
        var saved = new PlacementRect(2000, 80, 1300, 900);

        var (bounds, fromSaved) = WindowPlacementPolicy.Resolve(saved, [Screen1080, SecondRight], Screen1080);

        fromSaved.Should().BeTrue();
        bounds.Should().Be(saved);
    }

    [Fact]
    public void Resolve_FallsBackToDefaultWhenSavedIsOffScreenOrMissing()
    {
        var offScreen = new PlacementRect(2000, 80, 1300, 900);

        WindowPlacementPolicy.Resolve(offScreen, [Screen1080], Screen1080).Should().Be((WindowPlacementPolicy.DefaultBounds(Screen1080), false));
        WindowPlacementPolicy.Resolve(null, [Screen1080], Screen1080).Should().Be((WindowPlacementPolicy.DefaultBounds(Screen1080), false));
    }

    [Fact]
    public void Resolve_RaisesATooSmallSavedSizeToTheMinimum()
    {
        var (bounds, fromSaved) = WindowPlacementPolicy.Resolve(new PlacementRect(50, 50, 300, 200), [Screen1080], Screen1080);

        fromSaved.Should().BeTrue();
        bounds.Width.Should().Be(1100);
        bounds.Height.Should().Be(700);
        bounds.Left.Should().Be(50);
    }

    [Fact]
    public void SettingsRoundTripThroughJson()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new WindowPlacementSettings { Left = 10, Top = 20, Width = 1200, Height = 800, Maximized = true });
        var back = System.Text.Json.JsonSerializer.Deserialize<WindowPlacementSettings>(json)!;

        back.Bounds.Should().Be(new PlacementRect(10, 20, 1200, 800));
        back.Maximized.Should().BeTrue();
        json.Should().NotContain("Bounds");
    }
}
