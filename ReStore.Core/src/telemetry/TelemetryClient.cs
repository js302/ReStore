using System.Collections;
using System.Text.Json;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.telemetry;

/// <summary>Everything a <see cref="TelemetryClient"/> depends on; defaults are the real process, tests inject their own.</summary>
public sealed class TelemetryClientOptions
{
    public string Surface { get; set; } = "cli";
    public Func<string, string?> GetEnv { get; set; } = Environment.GetEnvironmentVariable;
    public Func<IEnumerable<string>> GetEnvNames { get; set; } = () => Environment.GetEnvironmentVariables().Keys.Cast<object>().Select(k => k.ToString() ?? "");
    public string Channel { get; set; } = AppChannel.Current;
    public Func<bool> IsTestRootOverride { get; set; } = () => AppPaths.IsRootOverridden;
    public Func<string> StateDirectory { get; set; } = () => AppPaths.StateDirectory;
    public Func<string> ConfigFile { get; set; } = () => AppPaths.ConfigFile;

    /// <summary>Overrides reading <c>telemetry.enabled</c> from the config file.</summary>
    public Func<bool>? ConfigEnabled { get; set; }

    /// <summary>Overrides reading <c>telemetry.crashReports</c> from the config file.</summary>
    public Func<string>? ConfigCrashReports { get; set; }

    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>Overrides the sender (tests); by default an <see cref="HttpTelemetrySender"/> for the configured endpoint, if any.</summary>
    public Func<ITelemetrySender?>? SenderFactory { get; set; }

    public string AppVersion { get; set; } = AboutInfo.VersionOf(typeof(TelemetryClient).Assembly);
    public bool? Interactive { get; set; }
}

/// <summary>
/// Records allow-listed events into the local queue and uploads them when an endpoint exists. Safe to call from anywhere:
/// <see cref="Track"/> never throws and only does a short file append.
/// </summary>
public sealed class TelemetryClient
{
    public const string DocsUrl = "https://github.com/js302/ReStore/blob/main/docs/telemetry.md";

    public const string NoticeText =
        "ReStore collects anonymous usage statistics (which commands/features are used, success or failure, durations). "
        + "It never collects file names, paths or file contents. "
        + "Turn it off with `restore telemetry disable` or RESTORE_TELEMETRY_OPTOUT=1. "
        + "Details: " + DocsUrl;

    private const int BatchSize = 50;

    private readonly TelemetryClientOptions _options;
    private readonly string _sessionId = Guid.NewGuid().ToString("N")[..16];
    private readonly object _lock = new();
    private string? _agent;
    private bool _agentResolved;
    private bool _noticeShownThisRun;
    private bool _noticeEventHandled;

    // Config is re-read only when the file changed.
    private (DateTime Stamp, long Length, bool Enabled)? _configCache;

    // Crash reports per process: a crashing loop must not flood the pending folder.
    private const int MaxCrashesPerRun = 5;
    private readonly HashSet<string> _crashKeys = [];

    public TelemetryClient(TelemetryClientOptions? options = null)
    {
        _options = options ?? new TelemetryClientOptions();
        Surface = _options.Surface;
    }

    public string Surface { get; set; }

    public string SessionId => _sessionId;

    public TelemetryStateStore StateStore => new(Path.Combine(_options.StateDirectory(), "telemetry.json"));

    public TelemetryQueue Queue => new(Path.Combine(_options.StateDirectory(), "telemetry-queue.jsonl"));

    /// <summary>The upload endpoint, or null when none is configured (events then stay in the local queue).</summary>
    public Uri? Endpoint => TelemetryEndpoint.Resolve(_options.GetEnv);

    public bool ConfigEnabled
    {
        get
        {
            if (_options.ConfigEnabled is { } provider)
            {
                return provider();
            }

            try
            {
                var file = _options.ConfigFile();
                var info = new FileInfo(file);
                var key = info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, -1L);
                lock (_lock)
                {
                    if (_configCache is { } cached && cached.Stamp == key.Item1 && cached.Length == key.Item2)
                    {
                        return cached.Enabled;
                    }

                    var enabled = TelemetryEnablement.ReadConfigEnabled(file);
                    _configCache = (key.Item1, key.Item2, enabled);
                    return enabled;
                }
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    /// <summary>The current <c>telemetry.crashReports</c> value (<c>ask</c>, <c>always</c> or <c>never</c>); read from the config file every time.</summary>
    public string CrashMode
    {
        get
        {
            try
            {
                return CrashReportMode.Normalize(_options.ConfigCrashReports?.Invoke()) ?? TelemetryEnablement.ReadConfigCrashReports(_options.ConfigFile());
            }
            catch (Exception)
            {
                return CrashReportMode.Ask;
            }
        }
    }

    /// <summary>The pending (not yet agreed to) crash reports.</summary>
    public CrashReportStore CrashStore => new(Path.Combine(_options.StateDirectory(), "crash-reports"));

    /// <summary>Forget the cached config value (after the config was rewritten in-process).</summary>
    public void InvalidateConfig()
    {
        lock (_lock)
        {
            _configCache = null;
        }
    }

    public TelemetryDecision Decision =>
        TelemetryEnablement.Evaluate(ConfigEnabled, _options.GetEnv, _options.Channel, _options.IsTestRootOverride());

    /// <summary>True when the first-run notice should be shown now: telemetry would be on and nobody has seen the notice yet.</summary>
    public bool NoticeNeeded
    {
        get
        {
            try
            {
                return Decision.Enabled && !_noticeShownThisRun && StateStore.Load().NoticeShownUtc == null;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Records that the notice was shown on <paramref name="noticeSurface"/>. Nothing is queued until the next run.</summary>
    public void MarkNoticeShown(string noticeSurface)
    {
        try
        {
            var now = _options.UtcNow();
            StateStore.Update(state =>
            {
                state.NoticeShownUtc ??= now;
                state.NoticeSurface ??= noticeSurface;
                state.NoticeEventPending = true;
            }, now);
            _noticeShownThisRun = true;
        }
        catch (Exception)
        {
            // Never fail the caller; the notice is shown again next time.
        }
    }

    /// <summary>Queues an event. No-op when disabled, before the notice was shown (or in the run that showed it); never throws.</summary>
    public void Track(string name, IReadOnlyDictionary<string, object?>? properties = null)
    {
        try
        {
            if (!TelemetrySchema.TryBuild(name, properties, out var clean) || !Decision.Enabled || _noticeShownThisRun)
            {
                return;
            }

            var now = _options.UtcNow();
            var store = StateStore;
            var state = store.Load();
            if (state.NoticeShownUtc == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(state.InstallId))
            {
                state = store.LoadWithId(now);
            }

            var queue = Queue;
            if (state.NoticeEventPending && !_noticeEventHandled && name != TelemetrySchema.Notice)
            {
                _noticeEventHandled = true;
                queue.Append(BuildEvent(TelemetrySchema.Notice, new SortedDictionary<string, object>(), state.InstallId!, now));
                store.Update(s => s.NoticeEventPending = false, now);
            }

            queue.Append(BuildEvent(name, clean, state.InstallId!, now));
        }
        catch (Exception)
        {
            // Telemetry must never affect the app.
        }
    }

    private string BuildEvent(string name, SortedDictionary<string, object> properties, string installId, DateTime now)
    {
        var document = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["ts"] = now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["installId"] = installId,
            ["sessionId"] = _sessionId,
            ["appVersion"] = _options.AppVersion,
            ["channel"] = _options.Channel,
            ["arch"] = AppChannel.Architecture,
            ["osVersion"] = Environment.OSVersion.Version.ToString(3),
            ["surface"] = Surface,
            ["interactive"] = _options.Interactive ?? DefaultInteractive(),
            ["props"] = properties
        };
        if (ResolveAgent() is { } agent)
        {
            document["agent"] = agent;
        }

        return JsonSerializer.Serialize(document);
    }

    private bool DefaultInteractive()
    {
        if (Surface == "gui")
        {
            return true;
        }

        if (Surface == "service")
        {
            return false;
        }

        try
        {
            return !Console.IsInputRedirected && !Console.IsOutputRedirected;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private string? ResolveAgent()
    {
        if (!_agentResolved)
        {
            _agent = TelemetryAgent.Detect(_options.GetEnv, _options.GetEnvNames);
            _agentResolved = true;
        }

        return _agent;
    }

    /// <summary>
    /// Builds the scrubbed crash report of <paramref name="exception"/> and keeps it as a pending report (<c>ask</c>) or queues it for
    /// upload (<c>always</c>). Does nothing when telemetry is off for any reason, when crash reports are <c>never</c>, or for a repeat of
    /// a crash already recorded by this process. Never throws.
    /// </summary>
    public CrashOutcome RecordCrash(Exception? exception)
    {
        try
        {
            if (exception == null || !Decision.Enabled)
            {
                return CrashOutcome.None;
            }

            var mode = CrashMode;
            if (mode == CrashReportMode.Never)
            {
                return CrashOutcome.None;
            }

            var key = exception.GetType().FullName + "|" + (new System.Diagnostics.StackTrace(exception, false).GetFrame(0)?.GetMethod()?.Name ?? "");
            lock (_lock)
            {
                if (_crashKeys.Count >= MaxCrashesPerRun || !_crashKeys.Add(key))
                {
                    return CrashOutcome.None;
                }
            }

            var now = _options.UtcNow();
            var terms = CrashScrubber.CollectConfigTerms(_options.ConfigFile());
            var report = CrashReportBuilder.Build(exception, new CrashReportContext
            {
                AppVersion = _options.AppVersion,
                Channel = _options.Channel,
                CreatedUtc = now,
                InstallId = StateStore.LoadWithId(now).InstallId,
                SessionId = _sessionId,
                Surface = Surface,
                SensitiveTerms = terms
            });

            var store = CrashStore;
            if (!store.Save(report.Id, report.ToJson()))
            {
                return CrashOutcome.None;
            }

            if (mode == CrashReportMode.Always && QueueCrash(report.Id))
            {
                return new CrashOutcome(report.Id, mode, CrashDisposition.Queued);
            }

            return new CrashOutcome(report.Id, mode, CrashDisposition.Pending);
        }
        catch (Exception)
        {
            return CrashOutcome.None;
        }
    }

    /// <summary>Moves a pending crash report into the upload queue (the user's explicit decision, or <c>always</c>). False when telemetry is off, the report is gone or the queue is locked.</summary>
    public bool QueueCrash(string id)
    {
        try
        {
            if (!Decision.Enabled)
            {
                return false;
            }

            var store = CrashStore;
            var json = store.Read(id);
            if (json == null || !Queue.Append(json))
            {
                return false;
            }

            store.Delete(id);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Queues every pending crash report; returns how many were queued.</summary>
    public int QueueAllCrashes() => CrashStore.List().Count(summary => QueueCrash(summary.Id));

    /// <summary>The number of queued (not yet uploaded) crash reports.</summary>
    public int QueuedCrashCount() => Queue.ReadAll().Count(line => line.Contains("\"name\":\"" + CrashReport.EventName + "\"", StringComparison.Ordinal));

    /// <summary>The queued events exactly as they would be sent.</summary>
    public List<string> Preview() => Queue.ReadAll();

    /// <summary>Deletes every queued event (<c>telemetry clear</c>, <c>disable</c>).</summary>
    public bool ClearQueue() => Queue.Clear();

    /// <summary>Replaces the install id with a fresh random one and returns it.</summary>
    public string ResetInstallId()
    {
        var now = _options.UtcNow();
        return StateStore.Update(state =>
        {
            state.InstallId = Guid.NewGuid().ToString("D");
            state.CreatedUtc = now;
        }, now).InstallId!;
    }

    /// <summary>
    /// Sends queued events in batches while the budget lasts. Does nothing when disabled, without an endpoint, or while a
    /// previous failure's backoff has not elapsed. Events are removed only after a 2xx; any failure backs off (2^n minutes, max 6 h).
    /// </summary>
    public async Task<int> FlushAsync(TimeSpan budget, CancellationToken cancellationToken = default)
    {
        var sent = 0;
        try
        {
            if (!Decision.Enabled)
            {
                return 0;
            }

            var sender = CreateSender(out var ownedSender);
            if (sender == null)
            {
                return 0;
            }

            try
            {
                var now = _options.UtcNow();
                var store = StateStore;
                if (store.Load().NextAttemptUtc is { } next && now < next)
                {
                    return 0;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(budget);
                var queue = Queue;
                while (!timeout.IsCancellationRequested)
                {
                    var batch = queue.ReadAll().Take(BatchSize).ToList();
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    var ok = await sender.SendAsync(batch, timeout.Token).ConfigureAwait(false);
                    if (!ok)
                    {
                        RecordFailure(store, now);
                        break;
                    }

                    queue.Remove(batch);
                    sent += batch.Count;
                    store.Update(s => { s.FailureCount = 0; s.NextAttemptUtc = null; }, now);
                }
            }
            finally
            {
                if (ownedSender)
                {
                    (sender as IDisposable)?.Dispose();
                }
            }
        }
        catch (Exception)
        {
            // Uploading is best effort.
        }

        return sent;
    }

    private void RecordFailure(TelemetryStateStore store, DateTime now) =>
        store.Update(s =>
        {
            s.FailureCount = Math.Min(s.FailureCount + 1, 20);
            s.NextAttemptUtc = now + TimeSpan.FromMinutes(Math.Min(Math.Pow(2, s.FailureCount), 360));
        }, now);

    private ITelemetrySender? CreateSender(out bool owned)
    {
        owned = false;
        if (_options.SenderFactory is { } factory)
        {
            return factory();
        }

        if (Endpoint is not { } endpoint)
        {
            return null;
        }

        owned = true;
        return new HttpTelemetrySender(endpoint, userAgent: _options.AppVersion);
    }
}

/// <summary>Detects which AI agent / tool launched the process, from well-known environment variables, as a short closed-ish label.</summary>
public static class TelemetryAgent
{
    public const string OverrideVariable = "RESTORE_AGENT";

    public static string? Detect(Func<string, string?> getEnv, Func<IEnumerable<string>> getEnvNames)
    {
        if (getEnv(OverrideVariable) is { } custom)
        {
            var clean = Sanitize(custom);
            if (clean.Length > 0)
            {
                return clean;
            }
        }

        if (!string.IsNullOrEmpty(getEnv("CLAUDECODE")))
        {
            return "claude-code";
        }

        if (!string.IsNullOrEmpty(getEnv("CURSOR_TRACE_ID")))
        {
            return "cursor";
        }

        if (getEnv("TERM_PROGRAM") == "vscode")
        {
            return "vscode";
        }

        if (!string.IsNullOrEmpty(getEnv("GEMINI_CLI")))
        {
            return "gemini-cli";
        }

        return getEnvNames().Any(name => name.StartsWith("CODEX_", StringComparison.Ordinal)) ? "codex" : null;
    }

    /// <summary>Lower-cases and keeps only [a-z0-9-], at most 32 characters.</summary>
    public static string Sanitize(string value)
    {
        var chars = value.Trim().ToLowerInvariant().Where(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-').Take(32);
        return new string([.. chars]);
    }
}
