using System.Text.Json;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.monitoring;

/// <summary>Pause-duration math and text, kept free of UI so it can be unit tested.</summary>
public static class ProtectionPause
{
    /// <summary>Stored as <see cref="ProtectionController.PausedUntilUtc"/> for "until resumed".</summary>
    public static readonly DateTime Indefinite = DateTime.MaxValue;

    public static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    /// <summary>"Until tomorrow" resumes at this local time of day.</summary>
    public static readonly TimeSpan TomorrowResumeTime = TimeSpan.FromHours(8);

    /// <summary>A "until tomorrow" pause is never shorter than this, so pausing at 07:50 does not resume in ten minutes.</summary>
    public static readonly TimeSpan MinimumTomorrowSpan = TimeSpan.FromHours(1);

    public static bool IsIndefinite(DateTime pausedUntilUtc) => pausedUntilUtc >= Indefinite;

    /// <summary>
    /// The next 08:00 in <paramref name="zone"/> (local time when null) that is at least
    /// <see cref="MinimumTomorrowSpan"/> after <paramref name="nowUtc"/>, as UTC.
    /// </summary>
    public static DateTime UntilTomorrowUtc(DateTime nowUtc, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone);
        var day = nowLocal.Date;

        for (var i = 0; i < 3; i++)
        {
            var candidateLocal = DateTime.SpecifyKind(day.AddDays(i) + TomorrowResumeTime, DateTimeKind.Unspecified);

            // A time skipped by a DST spring-forward does not exist; shift it forward past the gap.
            if (zone.IsInvalidTime(candidateLocal))
            {
                candidateLocal = candidateLocal.AddHours(1);
            }

            var candidateUtc = TimeZoneInfo.ConvertTimeToUtc(candidateLocal, zone);
            if (candidateUtc - nowUtc >= MinimumTomorrowSpan)
            {
                return candidateUtc;
            }
        }

        // Unreachable in practice (two days always contain a qualifying 08:00).
        return nowUtc + TimeSpan.FromHours(24);
    }

    /// <summary>"until resumed", or "until 14:05" / "until Tue 08:00" in local time.</summary>
    public static string Describe(DateTime pausedUntilUtc) =>
        IsIndefinite(pausedUntilUtc) ? "until resumed" : $"until {DisplayTime.Format(pausedUntilUtc)}";
}

/// <summary>Remembers an active pause across restarts (<c>state/protection_pause.json</c>).</summary>
public sealed class ProtectionPauseStore(Func<string>? path = null)
{
    private readonly Func<string> _path = path ?? (() => Path.Combine(AppPaths.StateDirectory, "protection_pause.json"));

    private sealed class Document
    {
        public DateTime? PausedUntilUtc { get; set; }
    }

    /// <summary>The stored pause end (<see cref="ProtectionPause.Indefinite"/> for "until resumed"), or null when none or unreadable.</summary>
    public DateTime? Load()
    {
        try
        {
            var file = _path();
            if (!File.Exists(file))
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(file));
            return document?.PausedUntilUtc is { } until ? DateTime.SpecifyKind(until, DateTimeKind.Utc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The stored pause end when a pause is in force at <paramref name="nowUtc"/> (indefinite, or ending later), else null.
    /// Cheap (one small file read), so a worker may call it before every unit of work: a pause or resume written by another
    /// process (<c>restore pause</c>/<c>resume</c>, the tray of the running app) is noticed without any signalling.
    /// </summary>
    public DateTime? LoadActive(DateTime nowUtc)
        => Load() is { } until && (ProtectionPause.IsIndefinite(until) || until > nowUtc) ? until : null;

    public void Save(DateTime pausedUntilUtc)
    {
        var file = _path();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        ReStore.Core.src.utils.SharedFile.WriteAllTextAtomic(file, JsonSerializer.Serialize(new Document { PausedUntilUtc = DateTime.SpecifyKind(pausedUntilUtc, DateTimeKind.Utc) }));
    }

    public void Clear()
    {
        try
        {
            File.Delete(_path());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale file is re-evaluated (and cleared) on the next launch.
        }
    }
}
