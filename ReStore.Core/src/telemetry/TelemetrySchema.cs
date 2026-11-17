using ReStore.Core.src.cli;
using ReStore.Core.src.core;

namespace ReStore.Core.src.telemetry;

/// <summary>
/// The allow-list of telemetry: the only events that exist and, per event, the only properties and the values they may take.
/// Everything else is dropped by <see cref="TryBuild"/> before it reaches the queue. String values must come from a closed
/// set (or pass a strict validator); numbers and booleans are range-checked. This is the enforcement point of the
/// "never paths, file names, host names or raw messages" rule, documented in docs/telemetry.md.
/// </summary>
public static class TelemetrySchema
{
    public const string CliCommand = "cli.command";
    public const string AppStart = "app.start";
    public const string ServiceStart = "service.start";
    public const string Activity = "activity";
    public const string ErrorUnhandled = "error.unhandled";
    public const string Notice = "telemetry.notice";

    public static readonly string[] Surfaces = ["cli", "gui", "service"];
    public static readonly string[] StartModes = ["normal", "tray", "startup"];
    public static readonly string[] StorageTypes = ["local", "gdrive", "s3", "github", "dropbox", "sftp", "azure", "gcp", "b2", "other"];

    private const int MaxStringLength = 120;

    /// <summary>One property rule: turns a raw value into the value that is sent, or rejects it.</summary>
    private sealed class Rule(Func<object?, object?> normalize)
    {
        public object? Normalize(object? value) => normalize(value);

        public static Rule OneOf(IEnumerable<string> allowed)
        {
            var set = allowed.ToArray();
            return new Rule(value => AsString(value) is { } text && set.Contains(text, StringComparer.Ordinal) ? text : null);
        }

        public static Rule Bool() => new(value => value is bool ? value : null);

        public static Rule Int(int min, int max) => new(value => value switch
        {
            int i when i >= min && i <= max => i,
            long l when l >= min && l <= max => (int)l,
            _ => null
        });

        public static Rule Text(Func<string, string?> validate) => new(value => AsString(value) is { } text ? validate(text) : null);
    }

    private static string? AsString(object? value) => value switch
    {
        string text => text,
        Enum e => e.ToString(),
        _ => null
    };

    private static readonly Lazy<Dictionary<string, Dictionary<string, Rule>>> Events = new(() => new(StringComparer.Ordinal)
    {
        [CliCommand] = new(StringComparer.Ordinal)
        {
            ["command"] = Rule.Text(text => CommandRegistry.Find(text) is { } spec ? spec.Name : null),
            ["exitCode"] = Rule.Int(0, 255),
            ["durationBucket"] = Rule.OneOf(TelemetryBuckets.Durations),
            ["json"] = Rule.Bool(),
            ["flags"] = Rule.Text(NormalizeFlags)
        },
        [AppStart] = new(StringComparer.Ordinal) { ["startMode"] = Rule.OneOf(StartModes) },
        [ServiceStart] = new(StringComparer.Ordinal),
        [Activity] = new(StringComparer.Ordinal)
        {
            ["type"] = Rule.OneOf(Enum.GetNames<ActivityType>()),
            ["result"] = Rule.OneOf(Enum.GetNames<ActivityResult>()),
            ["durationBucket"] = Rule.OneOf(TelemetryBuckets.Durations),
            ["storageType"] = Rule.OneOf(StorageTypes),
            ["encrypted"] = Rule.Bool(),
            ["manifestVersion"] = Rule.Int(1, 99),
            ["fileCountBucket"] = Rule.OneOf(TelemetryBuckets.Counts),
            ["bytesBucket"] = Rule.OneOf(TelemetryBuckets.Bytes),
            ["skippedCountBucket"] = Rule.OneOf(TelemetryBuckets.Counts)
        },
        [ErrorUnhandled] = new(StringComparer.Ordinal)
        {
            ["surface"] = Rule.OneOf(Surfaces),
            ["exceptionType"] = Rule.Text(NormalizeExceptionType)
        },
        [Notice] = new(StringComparer.Ordinal)
    });

    public static IReadOnlyCollection<string> EventNames => Events.Value.Keys;

    /// <summary>The properties an event may carry, for documentation and tests.</summary>
    public static IReadOnlyCollection<string> PropertyNames(string eventName) =>
        Events.Value.TryGetValue(eventName, out var rules) ? rules.Keys : [];

    /// <summary>
    /// Filters <paramref name="properties"/> through the allow-list. Returns false when the event itself is not allowed.
    /// Unknown properties and values that fail their rule are dropped (never sent), the rest are normalised.
    /// </summary>
    public static bool TryBuild(string? eventName, IReadOnlyDictionary<string, object?>? properties, out SortedDictionary<string, object> clean)
    {
        clean = new SortedDictionary<string, object>(StringComparer.Ordinal);
        if (eventName == null || !Events.Value.TryGetValue(eventName, out var rules))
        {
            return false;
        }

        if (properties == null)
        {
            return true;
        }

        foreach (var (key, raw) in properties)
        {
            if (!rules.TryGetValue(key, out var rule) || raw == null)
            {
                continue;
            }

            var value = rule.Normalize(raw);
            if (value == null || (value is string text && !IsSafeText(text)))
            {
                continue;
            }

            clean[key] = value;
        }

        return true;
    }

    /// <summary>Last line of defence for string values: short and free of anything path-like.</summary>
    private static bool IsSafeText(string text) =>
        text.Length <= MaxStringLength && text.IndexOfAny(['\\', '/', ':', '\r', '\n', '"']) < 0;

    private static string? NormalizeFlags(string text)
    {
        if (text.Length == 0)
        {
            return "";
        }

        var known = KnownFlags.Value;
        var flags = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return flags.All(known.Contains) ? string.Join(',', flags.Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal)) : null;
    }

    private static readonly Lazy<HashSet<string>> KnownFlags = new(() =>
        new HashSet<string>(
            CommandRegistry.All.SelectMany(s => s.Options.Select(o => o.Name)).Concat(CliGlobals.Documentation.Select(o => o.Name)),
            StringComparer.Ordinal));

    private static string? NormalizeExceptionType(string text)
    {
        if (!text.StartsWith("System.", StringComparison.Ordinal) && !text.StartsWith("ReStore.", StringComparison.Ordinal))
        {
            return null;
        }

        // A type name: identifier characters, dots, nested-type plus and generic arity backticks only.
        return text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '`') ? text : null;
    }
}
