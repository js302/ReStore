using System.Text.Json;

namespace ReStore.Core.src.telemetry;

/// <summary>
/// <c>state/telemetry.json</c>: the anonymous install id (random, unrelated to the repository id), whether and where the
/// first-run notice was shown, and the upload backoff. Reads tolerate a missing or damaged file (a fresh state is used).
/// </summary>
public sealed class TelemetryState
{
    public string? InstallId { get; set; }
    public DateTime? CreatedUtc { get; set; }
    public DateTime? NoticeShownUtc { get; set; }

    /// <summary>Where the notice was shown: <c>cli</c>, <c>gui-wizard</c> or <c>gui-dialog</c>.</summary>
    public string? NoticeSurface { get; set; }

    /// <summary>The <c>telemetry.notice</c> event is queued by the first run after the notice (nothing is collected in the run that shows it).</summary>
    public bool NoticeEventPending { get; set; }

    public int FailureCount { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
}

/// <summary>Reads and writes <see cref="TelemetryState"/> (atomic replace; last writer wins, which is fine for this data).</summary>
public sealed class TelemetryStateStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public string Path { get; } = path;

    public TelemetryState Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                return JsonSerializer.Deserialize<TelemetryState>(File.ReadAllText(Path), Options) ?? new TelemetryState();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Damaged or locked: start over, the next save repairs it.
        }

        return new TelemetryState();
    }

    /// <summary>Loads the state and makes sure it has an install id.</summary>
    public TelemetryState LoadWithId(DateTime utcNow)
    {
        var state = Load();
        if (string.IsNullOrEmpty(state.InstallId))
        {
            state.InstallId = Guid.NewGuid().ToString("D");
            state.CreatedUtc = utcNow;
            Save(state);
        }

        return state;
    }

    public bool Save(TelemetryState state)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            ReStore.Core.src.utils.SharedFile.WriteAllTextAtomic(Path, JsonSerializer.Serialize(state, Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Loads, applies <paramref name="change"/> and saves.</summary>
    public TelemetryState Update(Action<TelemetryState> change, DateTime utcNow)
    {
        var state = LoadWithId(utcNow);
        change(state);
        Save(state);
        return state;
    }
}
