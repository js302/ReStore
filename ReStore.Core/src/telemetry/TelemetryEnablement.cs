using System.Text.Json;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.telemetry;

/// <summary>Whether telemetry is on, and when it is not, why (a stable code plus a sentence for people).</summary>
public sealed record TelemetryDecision(bool Enabled, string? Reason, string? Description)
{
    public static TelemetryDecision On { get; } = new(true, null, null);
}

/// <summary>The rules that switch telemetry off. All of them must pass for any event to be recorded.</summary>
public static class TelemetryEnablement
{
    public const string OptOutVariable = "RESTORE_TELEMETRY_OPTOUT";
    public const string DoNotTrackVariable = "DO_NOT_TRACK";
    public const string CiVariable = "CI";
    public const string DevVariable = "RESTORE_TELEMETRY_DEV";

    public const string ReasonConfig = "config";
    public const string ReasonOptOut = "env-optout";
    public const string ReasonDoNotTrack = "env-do-not-track";
    public const string ReasonCi = "env-ci";
    public const string ReasonDevChannel = "dev-channel";
    public const string ReasonTestOverride = "test-override";

    public static TelemetryDecision Evaluate(bool configEnabled, Func<string, string?> getEnv, string channel, bool testRootOverride)
    {
        if (testRootOverride)
        {
            return new(false, ReasonTestOverride, "ReStore is running with a test data-root override.");
        }

        if (!configEnabled)
        {
            return new(false, ReasonConfig, "Turned off in the configuration (telemetry.enabled is false).");
        }

        if (IsTruthy(getEnv(OptOutVariable)))
        {
            return new(false, ReasonOptOut, $"The {OptOutVariable} environment variable is set.");
        }

        if (IsTruthy(getEnv(DoNotTrackVariable)))
        {
            return new(false, ReasonDoNotTrack, $"The {DoNotTrackVariable} environment variable is set.");
        }

        if (IsTruthy(getEnv(CiVariable)))
        {
            return new(false, ReasonCi, "Running in a CI environment (the CI environment variable is set).");
        }

        if (channel == AppChannel.Dev && getEnv(DevVariable) != "1")
        {
            return new(false, ReasonDevChannel, $"This is a development build (set {DevVariable}=1 to test telemetry).");
        }

        return TelemetryDecision.On;
    }

    public static bool IsTruthy(string? value) =>
        value != null && value.Trim().ToLowerInvariant() is "1" or "true" or "yes";

    /// <summary>Reads <c>telemetry.crashReports</c> from a config file without loading the whole configuration; <c>ask</c> when absent, invalid or unreadable.</summary>
    public static string ReadConfigCrashReports(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return CrashReportMode.Ask;
            }

            using var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("telemetry", out var telemetry)
                && telemetry.ValueKind == JsonValueKind.Object
                && telemetry.TryGetProperty("crashReports", out var mode)
                && mode.ValueKind == JsonValueKind.String
                && CrashReportMode.Normalize(mode.GetString()) is { } normalized)
            {
                return normalized;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall through to the default.
        }

        return CrashReportMode.Ask;
    }

    /// <summary>Reads <c>telemetry.enabled</c> from a config file without loading the whole configuration; true (the default) when absent or unreadable.</summary>
    public static bool ReadConfigEnabled(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return true;
            }

            using var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return !(document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("telemetry", out var telemetry)
                && telemetry.ValueKind == JsonValueKind.Object
                && telemetry.TryGetProperty("enabled", out var enabled)
                && enabled.ValueKind == JsonValueKind.False);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return true;
        }
    }
}
