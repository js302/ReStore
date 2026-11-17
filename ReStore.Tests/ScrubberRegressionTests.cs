using FluentAssertions;
using ReStore.Core.src.telemetry;

namespace ReStore.Tests;

/// <summary>Regressions found by the generative scrubber test.</summary>
public class ScrubberRegressionTests
{
    [Theory]
    [InlineData("Cannot send to john.jdoe@corp-example.com now", "jdoe", new[] { "john", "corp-example", "jdoe" })]
    [InlineData("mail from ann.smith+tag@app-mail.example.org failed", "app-mail", new[] { "ann", "smith", "app-mail", "example.org" })]
    [InlineData("Could not open payroll-appendix-2024.docx", "app", new[] { "payroll", "2024", "endix" })]
    [InlineData("GET https://acme-prod.example.com/acme-prod/x?token=abc failed", "acme-prod", new[] { "acme", "example.com" })]
    public void ATermInsideAnEmailUrlOrFileName_DoesNotHideTheRestOfIt(string message, string term, string[] forbidden)
    {
        var scrubbed = CrashScrubber.ScrubMessage(message, [term]);

        foreach (var part in forbidden)
        {
            scrubbed.Should().NotContainEquivalentOf(part, $"'{part}' leaked from '{message}' -> '{scrubbed}'");
        }
    }
}
