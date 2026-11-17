using System.Xml.Linq;
using FluentAssertions;
using ReStore.Core.src.monitoring;

namespace ReStore.Tests;

public class ToastContentTests
{
    [Theory]
    [InlineData(ToastAction.Open, NotificationTarget.Dashboard)]
    [InlineData(ToastAction.RetryNow, NotificationTarget.Dashboard)]
    [InlineData(ToastAction.OpenLog, NotificationTarget.Activity)]
    [InlineData(ToastAction.Unlock, NotificationTarget.Settings)]
    [InlineData(ToastAction.BackUpNow, NotificationTarget.None)]
    [InlineData(ToastAction.ViewReport, NotificationTarget.Backups)]
    public void Arguments_RoundTrip(ToastAction action, NotificationTarget target)
    {
        var raw = ToastActivationArgs.Build(action, target);

        ToastActivationArgs.TryParse(raw, out var parsed).Should().BeTrue();
        parsed.Should().Be(new ToastActivation(action, target));
    }

    [Theory]
    [InlineData("\"restore-toast:action=retry&target=dashboard\"", ToastAction.RetryNow, NotificationTarget.Dashboard)]
    [InlineData("RESTORE-TOAST://?action=UNLOCK&target=settings", ToastAction.Unlock, NotificationTarget.Settings)]
    [InlineData("restore-toast:action=log", ToastAction.OpenLog, NotificationTarget.None)]
    [InlineData("restore-toast:action=backup&target=bogus", ToastAction.BackUpNow, NotificationTarget.None)]
    [InlineData("restore-toast:action=report&target=7", ToastAction.ViewReport, NotificationTarget.None)]
    public void Parse_IsTolerantOfQuotesCaseAndSlashes(string raw, ToastAction action, NotificationTarget target)
    {
        ToastActivationArgs.TryParse(raw, out var parsed).Should().BeTrue();
        parsed.Should().Be(new ToastActivation(action, target));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/show")]
    [InlineData("--share x")]
    [InlineData("restore-toast:")]
    [InlineData("restore-toast:action=format-disk")]
    [InlineData("restore-toast:target=dashboard")]
    public void Parse_RejectsEverythingElse(string? raw)
    {
        ToastActivationArgs.TryParse(raw, out _).Should().BeFalse();
    }

    [Fact]
    public void IsToastArgument_RecognisesMalformedToastUris()
    {
        ToastActivationArgs.IsToastArgument("restore-toast:action=zzz").Should().BeTrue();
        ToastActivationArgs.IsToastArgument("/show").Should().BeFalse();
    }

    [Fact]
    public void Authorization_RequiresTheExactToken()
    {
        var token = ToastActivationArgs.CurrentToken;
        token.Should().HaveLength(32);

        ToastActivationArgs.TryParse(ToastActivationArgs.Build(ToastAction.RetryNow, NotificationTarget.Dashboard, token), out var valid);
        ToastActivationArgs.IsAuthorized(valid, token).Should().BeTrue();

        ToastActivationArgs.TryParse("restore-toast:action=retry&target=dashboard", out var missing);
        ToastActivationArgs.IsAuthorized(missing, token).Should().BeFalse();

        ToastActivationArgs.TryParse("restore-toast:action=retry&target=dashboard&t=00000000000000000000000000000000", out var wrong);
        ToastActivationArgs.IsAuthorized(wrong, token).Should().BeFalse();

        // A toast from a previous run carries that run's token.
        ToastActivationArgs.TryParse(ToastActivationArgs.Build(ToastAction.Unlock, NotificationTarget.Settings, "AAAA"), out var stale);
        stale.Token.Should().Be("AAAA");
        ToastActivationArgs.IsAuthorized(stale, token).Should().BeFalse();

        ToastActivationArgs.IsAuthorized(valid, string.Empty).Should().BeFalse();
    }

    [Fact]
    public void Xml_CarriesTheTokenOnLaunchAndButtons()
    {
        var doc = XDocument.Parse(ToastXmlBuilder.Build("t", "m", NotificationTarget.Dashboard, [ToastAction.RetryNow], "abc123"));
        doc.Root!.Attribute("launch")!.Value.Should().EndWith("&t=abc123");
        doc.Descendants("action").Single().Attribute("arguments")!.Value.Should().EndWith("&t=abc123");
    }

    [Fact]
    public void Xml_HasTextsLaunchArgumentAndButtons()
    {
        var xml = ToastXmlBuilder.Build("Backup failed", "disk <full> & \"bad\"", NotificationTarget.Dashboard,
            [ToastAction.RetryNow, ToastAction.OpenLog]);

        var doc = XDocument.Parse(xml);
        var toast = doc.Root!;
        toast.Attribute("launch")!.Value.Should().Be("restore-toast:action=open&target=dashboard");
        toast.Attribute("activationType")!.Value.Should().Be("protocol");
        toast.Descendants("text").Select(t => t.Value).Should().Equal("Backup failed", "disk <full> & \"bad\"");
        var buttons = toast.Descendants("action").ToList();
        buttons.Select(b => b.Attribute("content")!.Value).Should().Equal("Retry now", "Open log");
        buttons.Select(b => b.Attribute("arguments")!.Value).Should().Equal(
            "restore-toast:action=retry&target=dashboard", "restore-toast:action=log&target=dashboard");
        buttons.Should().OnlyContain(b => b.Attribute("activationType")!.Value == "protocol");
    }

    [Fact]
    public void Xml_WithoutActions_OmitsActionsElement()
    {
        var doc = XDocument.Parse(ToastXmlBuilder.Build("t", "m", NotificationTarget.None, null));
        doc.Descendants("actions").Should().BeEmpty();

        var openOnly = XDocument.Parse(ToastXmlBuilder.Build("t", "m", NotificationTarget.None, [ToastAction.Open]));
        openOnly.Descendants("actions").Should().BeEmpty();
    }
}
