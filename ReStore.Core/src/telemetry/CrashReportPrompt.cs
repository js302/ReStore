using System.Text.Json;

namespace ReStore.Core.src.telemetry;

/// <summary>What the user picked in the "send an anonymous crash report?" prompt.</summary>
public enum CrashPromptChoice
{
    Send,
    AlwaysSend,
    DontSend,
    View
}

/// <summary>The effect of a <see cref="CrashPromptChoice"/>; <see cref="Text"/> is what <c>View</c> shows.</summary>
public sealed record CrashPromptResult(int Queued, int Deleted, string? Text);

/// <summary>The decision logic of the GUI crash prompt, free of any UI so it can be tested.</summary>
public static class CrashReportPrompt
{
    /// <summary>True when the user should be asked now: telemetry is on, crash reports are on <c>ask</c> and reports are pending.</summary>
    public static bool ShouldAsk(TelemetryClient client) =>
        client.Decision.Enabled && client.CrashMode == CrashReportMode.Ask && client.CrashStore.Count() > 0;

    /// <summary>
    /// Applies <paramref name="choice"/> to every pending report. <paramref name="saveMode"/> persists <c>telemetry.crashReports</c>
    /// (only called for <see cref="CrashPromptChoice.AlwaysSend"/>).
    /// </summary>
    public static async Task<CrashPromptResult> ApplyAsync(TelemetryClient client, CrashPromptChoice choice, Func<string, Task> saveMode)
    {
        switch (choice)
        {
            case CrashPromptChoice.Send:
                return new CrashPromptResult(client.QueueAllCrashes(), 0, null);
            case CrashPromptChoice.AlwaysSend:
                await saveMode(CrashReportMode.Always).ConfigureAwait(false);
                return new CrashPromptResult(client.QueueAllCrashes(), 0, null);
            case CrashPromptChoice.DontSend:
                return new CrashPromptResult(0, client.CrashStore.DeleteAll(), null);
            default:
                return new CrashPromptResult(0, 0, ViewText(client));
        }
    }

    /// <summary>The pending reports exactly as they would be sent (indented for reading), separated by blank lines.</summary>
    public static string ViewText(TelemetryClient client) =>
        string.Join(Environment.NewLine + Environment.NewLine, client.CrashStore.List().Select(s => Indent(client.CrashStore.Read(s.Id))).Where(t => t != null));

    /// <summary>Indents one report's JSON for display; the data is unchanged.</summary>
    public static string? Indent(string? json)
    {
        if (json == null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
