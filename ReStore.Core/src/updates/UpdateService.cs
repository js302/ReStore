using ReStore.Core.src.utils;

namespace ReStore.Core.src.updates;

/// <summary>An update the user should be told about, and the text to tell them.</summary>
public sealed record UpdateNotice(string Version, string Current, bool Critical, string? ReleaseNotesUrl)
{
    public string CliLine =>
        $"ReStore {UpdateText.Clean(Version)} is available (you have {UpdateText.Clean(Current)}). Update: restore update install"
        + (string.IsNullOrEmpty(UpdateText.Clean(ReleaseNotesUrl)) ? string.Empty : $" - notes: {UpdateText.Clean(ReleaseNotesUrl)}");
}

/// <summary>
/// Policy around <see cref="UpdateChecker"/>: when automatic checks may run (at most daily), the cache, the skipped version
/// and the "tell the user at most once a day" throttle. Explicit checks (<see cref="CheckNowAsync"/>) always run.
/// </summary>
public sealed class UpdateService
{
    public const string NoUpdateCheckEnvironmentVariable = "RESTORE_NO_UPDATE_CHECK";
    public static readonly TimeSpan AutomaticInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan FailedRetryInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan NoticeInterval = TimeSpan.FromHours(24);

    private readonly UpdateChecker _checker;
    private readonly Func<DateTime> _now;
    private readonly Func<string, string?> _environment;
    private readonly bool _disableUnderTestOverride;
    private readonly string _currentVersion;

    public UpdateService(
        UpdateChecker? checker = null,
        UpdateCache? cache = null,
        Func<DateTime>? now = null,
        Func<string, string?>? environment = null,
        bool disableUnderTestOverride = true,
        string? currentVersion = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _checker = checker ?? new UpdateChecker(environment: _environment, now: _now);
        Cache = cache ?? new UpdateCache();
        _disableUnderTestOverride = disableUnderTestOverride;
        _currentVersion = currentVersion ?? UpdateChecker.CurrentVersion;
    }

    public UpdateCache Cache { get; }

    /// <summary>Automatic checks are off for <c>updates.checkAutomatically=false</c>, <c>RESTORE_NO_UPDATE_CHECK=1</c>, a set <c>CI</c> variable and test runs.</summary>
    public bool AutomaticChecksAllowed(UpdatesConfig settings)
    {
        if (!settings.CheckAutomatically || (_disableUnderTestOverride && AppPaths.IsRootOverridden))
        {
            return false;
        }

        var optOut = _environment(NoUpdateCheckEnvironmentVariable)?.Trim();
        return !(optOut == "1" || string.Equals(optOut, "true", StringComparison.OrdinalIgnoreCase))
            && string.IsNullOrEmpty(_environment("CI"));
    }

    /// <summary>True when an automatic check is allowed and the last one is old enough.</summary>
    public bool IsAutomaticCheckDue(UpdatesConfig settings)
    {
        if (!AutomaticChecksAllowed(settings))
        {
            return false;
        }

        var cache = Cache.Load();
        if (cache.LastCheckUtc is not { } last)
        {
            return true;
        }

        var interval = cache.Result?.Status == UpdateStatus.Failed ? FailedRetryInterval : AutomaticInterval;
        // A clock set back must not silence checks forever.
        return _now() - last >= interval || last > _now();
    }

    /// <summary>The check the user asked for: always contacts GitHub and updates the cache.</summary>
    public async Task<UpdateCheckResult> CheckNowAsync(UpdatesConfig settings, CancellationToken cancellationToken = default)
    {
        var result = await _checker.CheckAsync(settings.IncludePrereleases, cancellationToken).ConfigureAwait(false);
        Cache.Update(data =>
        {
            data.LastCheckUtc = result.CheckedUtc;
            data.Result = result;
        });
        return result;
    }

    /// <summary>
    /// The background check: returns null when none was due (disabled, throttled), otherwise the fresh result.
    /// <paramref name="force"/> skips the throttle but not the opt-outs.
    /// </summary>
    public async Task<UpdateCheckResult?> CheckAutomaticallyAsync(UpdatesConfig settings, bool force = false, CancellationToken cancellationToken = default)
    {
        if (force ? !AutomaticChecksAllowed(settings) : !IsAutomaticCheckDue(settings))
        {
            return null;
        }

        return await CheckNowAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The last cached result (may be null) re-evaluated against the running version, so an installed update stops being offered.</summary>
    public UpdateCheckResult? GetCachedResult()
    {
        var cached = Cache.Load().Result;
        if (cached is not { Status: UpdateStatus.UpdateAvailable } || SemVer.ParseOrNull(cached.Latest) is not { } latest)
        {
            return cached;
        }

        return SemVer.ParseOrNull(_currentVersion) is { } current && latest <= current
            ? cached with { Status = UpdateStatus.UpToDate, Current = _currentVersion, Asset = null, Critical = false }
            : cached;
    }

    public string? GetSkippedVersion() => Cache.Load().SkippedVersion;

    /// <summary>A newer version the user has not skipped (critical releases ignore the skip).</summary>
    public UpdateCheckResult? GetPendingUpdate()
    {
        var cached = GetCachedResult();
        if (cached is not { Status: UpdateStatus.UpdateAvailable, Latest: { } latest })
        {
            return null;
        }

        var skipped = Cache.Load().SkippedVersion;
        return !cached.Critical && SameVersion(skipped, latest) ? null : cached;
    }

    /// <summary>The CLI/GUI notice for the cached update unless we told the user about it within <see cref="NoticeInterval"/>.</summary>
    public UpdateNotice? GetNotice()
    {
        if (GetPendingUpdate() is not { Latest: { } latest } pending)
        {
            return null;
        }

        var data = Cache.Load();
        if (SameVersion(data.LastNotifiedVersion, latest) && data.LastNotifiedUtc is { } at && _now() - at < NoticeInterval && at <= _now())
        {
            return null;
        }

        return new UpdateNotice(latest, _currentVersion, pending.Critical, pending.ReleaseNotesUrl);
    }

    public void MarkNotified(UpdateNotice notice) => Cache.Update(data =>
    {
        data.LastNotifiedVersion = notice.Version;
        data.LastNotifiedUtc = _now();
    });

    /// <summary>Remembers that the user does not want this version offered (<c>update skip</c>).</summary>
    public void Skip(string version) => Cache.Update(data => data.SkippedVersion = SemVer.ParseOrNull(version)?.ToString() ?? version);

    public void ClearSkip() => Cache.Update(data => data.SkippedVersion = null);

    private static bool SameVersion(string? a, string? b) =>
        a != null && b != null && SemVer.ParseOrNull(a) is { } x && SemVer.ParseOrNull(b) is { } y && x.Equals(y);
}
