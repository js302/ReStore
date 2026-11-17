using ReStore.Core.src.core;

namespace ReStore.Core.src.telemetry;

/// <summary>
/// Process-wide entry point for telemetry, usable from Core, the CLI and the GUI:
/// <c>Telemetry.Track("app.start", new Dictionary&lt;string, object?&gt; { ["startMode"] = "tray" })</c>.
/// Only events and properties of <see cref="TelemetrySchema"/> are ever recorded; see docs/telemetry.md.
/// </summary>
public static class Telemetry
{
    private static readonly object Gate = new();
    private static TelemetryClient? _client;

    public static TelemetryClient Client
    {
        get
        {
            lock (Gate)
            {
                return _client ??= new TelemetryClient();
            }
        }
    }

    /// <summary>Replaces the process-wide client (tests). Pass null to go back to the default one.</summary>
    internal static void Install(TelemetryClient? client)
    {
        lock (Gate)
        {
            _client = client;
        }
    }

    /// <summary>Tells telemetry which part of the product this process is: <c>cli</c>, <c>gui</c> or <c>service</c>.</summary>
    public static void SetSurface(string surface)
    {
        if (Array.IndexOf(TelemetrySchema.Surfaces, surface) >= 0)
        {
            Client.Surface = surface;
        }
    }

    public static void Track(string name, IReadOnlyDictionary<string, object?>? properties = null) => Client.Track(name, properties);

    /// <summary>Records <c>error.unhandled</c> for an exception: only its type name, and only for System.* / ReStore.* types.</summary>
    public static void TrackUnhandled(Exception? exception)
    {
        if (exception == null)
        {
            return;
        }

        Track(TelemetrySchema.ErrorUnhandled, new Dictionary<string, object?>
        {
            ["surface"] = Client.Surface,
            ["exceptionType"] = exception.GetType().FullName
        });
    }

    /// <summary>Keeps a scrubbed crash report of <paramref name="exception"/> (pending, or queued when the user chose <c>always</c>); see <see cref="TelemetryClient.RecordCrash"/>.</summary>
    public static CrashOutcome RecordCrash(Exception? exception) => Client.RecordCrash(exception);

    /// <summary>Records one finished operation (<c>activity</c>): type, result and a duration bucket, never what it worked on.</summary>
    public static void TrackActivity(ActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Track(TelemetrySchema.Activity, new Dictionary<string, object?>
        {
            ["type"] = entry.Type,
            ["result"] = entry.Result,
            ["durationBucket"] = TelemetryBuckets.Duration(entry.Duration)
        });
    }

    /// <summary>Records a finished backup run with coarse size buckets (<c>activity</c> of type Backup).</summary>
    public static void TrackBackupRun(BackupRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Track(TelemetrySchema.Activity, new Dictionary<string, object?>
        {
            ["type"] = ActivityType.Backup,
            ["result"] = report.FilesSkipped > 0 ? ActivityResult.Warning : ActivityResult.Succeeded,
            ["durationBucket"] = TelemetryBuckets.Duration(TimeSpan.FromMilliseconds(Math.Max(0, report.DurationMs))),
            ["storageType"] = StorageTypeLabel(report.StorageType),
            ["fileCountBucket"] = TelemetryBuckets.Count(report.FilesAdded + report.FilesChanged),
            ["bytesBucket"] = TelemetryBuckets.Size(report.BytesUploaded),
            ["skippedCountBucket"] = TelemetryBuckets.Count(report.FilesSkipped)
        });
    }

    public static Task<int> FlushAsync(TimeSpan budget, CancellationToken cancellationToken = default) => Client.FlushAsync(budget, cancellationToken);

    private static string StorageTypeLabel(string? storageType)
    {
        var name = storageType?.Trim().ToLowerInvariant() ?? "";
        return Array.IndexOf(TelemetrySchema.StorageTypes, name) >= 0 ? name : "other";
    }
}
