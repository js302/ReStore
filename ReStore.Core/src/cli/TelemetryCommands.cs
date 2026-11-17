using System.Diagnostics;
using System.Text.Json.Nodes;
using ReStore.Core.src.telemetry;

namespace ReStore.Core.src.cli;

/// <summary>
/// <c>telemetry status|enable|disable|preview|reset-id|clear|crash-reports|crashes</c> (see docs/telemetry.md) and the two pipeline hooks the CLI
/// calls around every command: <see cref="BeforeRun"/> (first-run notice, service start) and <see cref="AfterRunAsync"/>
/// (the <c>cli.command</c> event and a short upload flush).
/// </summary>
internal static class TelemetryCommands
{
    /// <summary>How long the end of a command may wait for an upload; only matters once an endpoint exists.</summary>
    public static readonly TimeSpan CliFlushBudget = TimeSpan.FromMilliseconds(1500);

    public static IEnumerable<CommandSpec> Specs { get; } =
    [
        new CommandSpec
        {
            Name = "telemetry status",
            Category = CommandCategory.Configuration,
            Summary = "Show whether anonymous usage statistics are on, and why not when they are off.",
            Description = "Fields: enabled, reason (config, env-optout, env-do-not-track, env-ci, dev-channel or null), reasonText, configEnabled, installId, endpointConfigured, queuedEvents, noticeShown, noticeShownUtc, noticeSurface, channel, crashReports (ask, always or never), pendingCrashReports, queuedCrashReports.",
            Examples = ["telemetry status", "telemetry status --json"],
            Config = ConfigRequirement.None,
            State = StateRequirement.None,
            Handler = StatusAsync
        },
        new CommandSpec
        {
            Name = "telemetry enable",
            Category = CommandCategory.Configuration,
            Summary = "Turn anonymous usage statistics on (telemetry.enabled in config.json).",
            Examples = ["telemetry enable"],
            Mutates = true,
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            Handler = context => SetEnabledAsync(context, true)
        },
        new CommandSpec
        {
            Name = "telemetry disable",
            Category = CommandCategory.Configuration,
            Summary = "Turn anonymous usage statistics off and delete the locally queued events.",
            Examples = ["telemetry disable"],
            Mutates = true,
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            Handler = context => SetEnabledAsync(context, false)
        },
        new CommandSpec
        {
            Name = "telemetry preview",
            Category = CommandCategory.Configuration,
            Summary = "Show the queued events exactly as they would be sent.",
            Description = "Nothing is sent in this version: no collection endpoint is configured, so events only wait in state/telemetry-queue.jsonl.",
            Examples = ["telemetry preview", "telemetry preview --json"],
            Config = ConfigRequirement.None,
            State = StateRequirement.None,
            Handler = PreviewAsync
        },
        new CommandSpec
        {
            Name = "telemetry reset-id",
            Category = CommandCategory.Configuration,
            Summary = "Replace the anonymous install id with a new random one.",
            Examples = ["telemetry reset-id"],
            Mutates = true,
            Config = ConfigRequirement.None,
            State = StateRequirement.None,
            Handler = ResetIdAsync
        },
        new CommandSpec
        {
            Name = "telemetry clear",
            Category = CommandCategory.Configuration,
            Summary = "Delete the locally queued telemetry events.",
            Examples = ["telemetry clear"],
            Mutates = true,
            Config = ConfigRequirement.None,
            State = StateRequirement.None,
            Handler = ClearAsync
        },
        new CommandSpec
        {
            Name = "telemetry crash-reports",
            Category = CommandCategory.Configuration,
            Summary = "Show or set whether a scrubbed crash report is sent along with telemetry: ask (default), always or never.",
            Description = "Crash reports also need telemetry to be on. With 'ask' a report is kept on this PC until you send it with 'telemetry crashes send'; with 'never' none is kept. Nothing leaves this PC until a collection endpoint is configured. JSON fields: crashReports, changed, enabled, reason.",
            Positionals = [new PositionalSpec("mode", "ask, always or never; omit to show the current value.", Required: false)],
            Examples = ["telemetry crash-reports", "telemetry crash-reports always", "telemetry crash-reports never --json"],
            Mutates = true,
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            Validate = p => p.Positionals.Count == 1 && CrashReportMode.Normalize(p.Positionals[0]) == null
                ? $"'{p.Positionals[0]}' is not a crash report mode. Use ask, always or never."
                : null,
            Handler = CrashModeAsync
        },
        new CommandSpec
        {
            Name = "telemetry crashes",
            Category = CommandCategory.Configuration,
            Summary = "List, show, send or delete the pending crash reports (list | show <id> | send <id|--all> | delete <id|--all>).",
            Description = "A crash report holds exception type names, scrubbed messages and method names only (docs/telemetry.md). 'show' prints exactly what would be sent. 'send' moves a report to the upload queue (it is only uploaded when a collection endpoint is configured and telemetry is on). 'delete' removes pending reports. Without an action, 'list' is used. JSON: list -> count, queued, reports; show -> id, report; send -> queued, requested; delete -> deleted.",
            Positionals =
            [
                new PositionalSpec("action", "list, show, send or delete (default list).", Required: false),
                new PositionalSpec("id", "The crash report id (see 'telemetry crashes list').", Required: false)
            ],
            Options = [new OptionSpec("--all", "With send or delete: every pending report.")],
            Examples = ["telemetry crashes list --json", "telemetry crashes show 3fa9c01b2d4e", "telemetry crashes send --all", "telemetry crashes delete 3fa9c01b2d4e"],
            Mutates = true,
            Config = ConfigRequirement.None,
            State = StateRequirement.None,
            Validate = ValidateCrashes,
            Handler = CrashesAsync
        },
    ];

    // ---- Crash reports ------------------------------------------------------------------------------------------------

    private static readonly string[] CrashActions = ["list", "show", "send", "delete"];

    private static string? ValidateCrashes(Program.ParsedCommand p)
    {
        var action = p.Positionals.Count > 0 ? p.Positionals[0].ToLowerInvariant() : "list";
        if (Array.IndexOf(CrashActions, action) < 0)
        {
            return $"'{p.Positionals[0]}' is not a crashes action. Use list, show, send or delete.";
        }

        var hasId = p.Positionals.Count > 1;
        if (p.Positionals.Count > 2)
        {
            return "Too many arguments. Usage: telemetry crashes <list|show <id>|send <id|--all>|delete <id|--all>>";
        }

        return action switch
        {
            "list" when hasId || p.HasOption("--all") => "Usage: telemetry crashes list",
            "show" when !hasId || p.HasOption("--all") => "Usage: telemetry crashes show <id>",
            "send" or "delete" when hasId == p.HasOption("--all") => $"Usage: telemetry crashes {action} <id> | telemetry crashes {action} --all",
            _ => null
        };
    }

    /// <summary>
    /// Called from the CLI's top-level error path: keeps a scrubbed crash report (see <see cref="TelemetryClient.RecordCrash"/>) and, when it
    /// waits for a decision, tells the user once on stderr how to review and send it. Never prompts, never throws.
    /// </summary>
    public static void RecordCrash(Exception? exception)
    {
        try
        {
            var outcome = Telemetry.RecordCrash(exception);
            if (outcome.Disposition == CrashDisposition.Pending)
            {
                Console.Error.WriteLine(
                    $"A crash report is pending. Review: restore telemetry crashes show {outcome.Id}; send: restore telemetry crashes send {outcome.Id} "
                    + "(or 'restore telemetry crash-reports always').");
            }
        }
        catch (Exception)
        {
            // Crash reporting must never affect the error path.
        }
    }

    private static async Task<int> CrashModeAsync(CliContext context)
    {
        var client = Telemetry.Client;
        var changed = false;
        if (context.Parsed.Positionals.Count == 1)
        {
            var mode = CrashReportMode.Normalize(context.Parsed.Positionals[0])!;
            changed = context.Config.Telemetry.CrashReports != mode;
            context.Config.Telemetry.CrashReports = mode;
            await context.Config.SaveAsync().ConfigureAwait(false);
        }

        var current = context.Config.Telemetry.CrashReports;
        var decision = client.Decision;
        if (context.Json)
        {
            await context.WriteJsonAsync(new { crashReports = current, changed, enabled = decision.Enabled, reason = decision.Reason }).ConfigureAwait(false);
            return Program.ExitCodes.Success;
        }

        var text = $"Crash reports: {current}" + current switch
        {
            CrashReportMode.Always => " (a scrubbed report is queued with telemetry after a crash)",
            CrashReportMode.Never => " (no crash report is kept)",
            _ => " (a report is kept on this PC until you send it)"
        };
        if (!decision.Enabled)
        {
            text += $"{Environment.NewLine}Currently inactive: {decision.Description}";
        }

        await Console.Out.WriteLineAsync(text).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> CrashesAsync(CliContext context)
    {
        var positionals = context.Parsed.Positionals;
        var action = positionals.Count > 0 ? positionals[0].ToLowerInvariant() : "list";
        var id = positionals.Count > 1 ? positionals[1].ToLowerInvariant() : null;
        var all = context.Parsed.HasOption("--all");
        var client = Telemetry.Client;
        var store = client.CrashStore;

        switch (action)
        {
            case "show":
            {
                var json = store.Read(id!) ?? throw NoSuchCrash(id!);
                if (context.Json)
                {
                    await context.WriteJsonAsync(new { id, report = System.Text.Json.Nodes.JsonNode.Parse(json) }).ConfigureAwait(false);
                }
                else
                {
                    await Console.Out.WriteLineAsync(json).ConfigureAwait(false);
                }

                return Program.ExitCodes.Success;
            }

            case "send":
            {
                if (!client.Decision.Enabled)
                {
                    throw new CliCommandException(
                        Program.ExitCodes.Failure, CliErrorCodes.Failure,
                        $"Telemetry is off ({client.Decision.Description}), so crash reports cannot be sent.",
                        "run 'restore telemetry status' to see why, or 'restore telemetry crashes delete' to remove the report");
                }

                var ids = all ? store.List().Select(s => s.Id).ToList() : [id!];
                if (!all && store.Read(id!) == null)
                {
                    throw NoSuchCrash(id!);
                }

                var queued = ids.Count(client.QueueCrash);
                if (context.Json)
                {
                    await context.WriteJsonAsync(new { queued, requested = ids.Count, endpointConfigured = client.Endpoint != null }, queued == ids.Count ? 0 : Program.ExitCodes.Failure).ConfigureAwait(false);
                }
                else
                {
                    await Console.Out.WriteLineAsync($"Queued {queued} of {ids.Count} crash report(s) for sending."
                        + (client.Endpoint == null ? " No collection server is configured in this version, so they stay on this PC." : "")).ConfigureAwait(false);
                }

                return queued == ids.Count ? Program.ExitCodes.Success : Program.ExitCodes.Failure;
            }

            case "delete":
            {
                if (!all && store.Read(id!) == null)
                {
                    throw NoSuchCrash(id!);
                }

                var deleted = all ? store.DeleteAll() : (store.Delete(id!) ? 1 : 0);
                if (context.Json)
                {
                    await context.WriteJsonAsync(new { deleted }).ConfigureAwait(false);
                }
                else
                {
                    await Console.Out.WriteLineAsync($"Deleted {deleted} crash report(s).").ConfigureAwait(false);
                }

                return Program.ExitCodes.Success;
            }

            default:
            {
                var reports = store.List();
                var queued = client.QueuedCrashCount();
                if (context.Json)
                {
                    await context.WriteJsonAsync(new
                    {
                        count = reports.Count,
                        queued,
                        reports = reports.Select(r => new { id = r.Id, createdUtc = r.CreatedUtc, exceptionType = r.ExceptionType, exceptionCount = r.ExceptionCount, bytes = r.Bytes })
                    }).ConfigureAwait(false);
                    return Program.ExitCodes.Success;
                }

                if (reports.Count == 0)
                {
                    await Console.Out.WriteLineAsync($"No crash reports are pending. Queued for sending: {queued}.").ConfigureAwait(false);
                    return Program.ExitCodes.Success;
                }

                foreach (var report in reports)
                {
                    await Console.Out.WriteLineAsync($"{report.Id}  {report.CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}Z  {report.ExceptionType ?? "(unknown)"}").ConfigureAwait(false);
                }

                await Console.Out.WriteLineAsync($"Pending: {reports.Count}. Queued for sending: {queued}. Review one with: restore telemetry crashes show <id>").ConfigureAwait(false);
                return Program.ExitCodes.Success;
            }
        }
    }

    private static CliCommandException NoSuchCrash(string id) =>
        new(Program.ExitCodes.Failure, CliErrorCodes.Failure, $"There is no pending crash report '{id}'.", "list them with 'restore telemetry crashes list'");

    // ---- Pipeline hooks -----------------------------------------------------------------------------------------------

    /// <summary>Before a command runs: sets the surface, tells a first-time user about telemetry (stderr only) and records a service start.</summary>
    public static void BeforeRun(CliContext context)
    {
        try
        {
            var spec = context.Spec;
            var isService = spec.Name == "--service";
            var client = Telemetry.Client;
            client.Surface = isService ? "service" : "cli";

            // The notice goes to stderr so --json documents are untouched; --quiet postpones it to a run that may print it.
            var quiet = context.Parsed.Quiet;
            var informational = spec.Category == CommandCategory.Info || spec.Group == "telemetry" || isService;
            if (!quiet && !informational && client.NoticeNeeded)
            {
                Console.Error.WriteLine(TelemetryClient.NoticeText);
                client.MarkNoticeShown("cli");
            }

            if (isService)
            {
                Telemetry.Track(TelemetrySchema.ServiceStart);
            }
        }
        catch (Exception)
        {
            // Telemetry must never affect a command.
        }
    }

    /// <summary>After a command: records <c>cli.command</c> (names, exit code and buckets only) and uploads queued events briefly.</summary>
    public static async Task AfterRunAsync(CliContext context, int exitCode, TimeSpan elapsed)
    {
        try
        {
            var spec = context.Spec;
            if (spec.Group == "telemetry")
            {
                // Managing telemetry is not itself recorded (it would also change what 'telemetry preview' shows).
                return;
            }

            var flags = string.Join(',', context.Parsed.Options.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Telemetry.Track(TelemetrySchema.CliCommand, new Dictionary<string, object?>
            {
                ["command"] = spec.Name,
                ["exitCode"] = exitCode,
                ["durationBucket"] = TelemetryBuckets.Duration(elapsed),
                ["json"] = context.Json,
                ["flags"] = flags
            });

            await Telemetry.FlushAsync(CliFlushBudget).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Telemetry must never affect a command.
        }
    }

    // ---- Handlers -----------------------------------------------------------------------------------------------------

    private static async Task<int> StatusAsync(CliContext context)
    {
        var client = Telemetry.Client;
        var decision = client.Decision;
        var state = client.StateStore.Load();
        var queued = client.Queue.Count();
        var endpoint = client.Endpoint != null;
        var crashMode = client.CrashMode;
        var pendingCrashes = client.CrashStore.Count();
        var queuedCrashes = client.QueuedCrashCount();

        if (context.Json)
        {
            await context.WriteJsonAsync(new
            {
                enabled = decision.Enabled,
                reason = decision.Reason,
                reasonText = decision.Description,
                configEnabled = client.ConfigEnabled,
                installId = state.InstallId,
                endpointConfigured = endpoint,
                queuedEvents = queued,
                noticeShown = state.NoticeShownUtc != null,
                noticeShownUtc = state.NoticeShownUtc,
                noticeSurface = state.NoticeSurface,
                channel = ReStore.Core.src.utils.AppChannel.Current,
                crashReports = crashMode,
                pendingCrashReports = pendingCrashes,
                queuedCrashReports = queuedCrashes
            }).ConfigureAwait(false);
            return Program.ExitCodes.Success;
        }

        var lines = new List<string>
        {
            $"Anonymous usage statistics: {(decision.Enabled ? "on" : "off")}"
        };
        if (!decision.Enabled)
        {
            lines.Add($"  Why:               {decision.Description}");
        }

        lines.Add($"  Install id:        {state.InstallId ?? "(none yet)"}");
        lines.Add($"  Collection server: {(endpoint ? "configured" : "none configured in this version (events stay on this PC)")}");
        lines.Add($"  Queued events:     {queued}");
        lines.Add($"  Crash reports:     {crashMode} ({pendingCrashes} pending, {queuedCrashes} queued)");
        lines.Add($"  Notice shown:      {(state.NoticeShownUtc != null ? $"yes ({state.NoticeSurface ?? "unknown"})" : "no")}");
        lines.Add("  Details:           https://github.com/js302/ReStore/blob/main/docs/telemetry.md");
        await Console.Out.WriteLineAsync(string.Join(Environment.NewLine, lines)).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> SetEnabledAsync(CliContext context, bool enabled)
    {
        var client = Telemetry.Client;
        context.Config.Telemetry.Enabled = enabled;
        await context.Config.SaveAsync().ConfigureAwait(false);
        client.InvalidateConfig();

        var queueCleared = false;
        if (enabled)
        {
            // Turning it on explicitly counts as having been told.
            if (client.StateStore.Load().NoticeShownUtc == null)
            {
                client.MarkNoticeShown("cli");
            }
        }
        else
        {
            queueCleared = client.ClearQueue();
            client.CrashStore.DeleteAll();
        }

        var decision = client.Decision;
        if (context.Json)
        {
            await context.WriteJsonAsync(new
            {
                enabled = decision.Enabled,
                configEnabled = enabled,
                reason = decision.Reason,
                queueCleared
            }).ConfigureAwait(false);
            return Program.ExitCodes.Success;
        }

        await Console.Out.WriteLineAsync(enabled
            ? "Anonymous usage statistics are on." + (decision.Enabled ? "" : $" (Currently inactive: {decision.Description})")
            : "Anonymous usage statistics are off. Queued events were deleted.").ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> PreviewAsync(CliContext context)
    {
        var events = Telemetry.Client.Preview();
        if (context.Json)
        {
            var array = new JsonArray([.. events.Select(e => JsonNode.Parse(e))]);
            await context.WriteJsonAsync(new { count = events.Count, events = array }).ConfigureAwait(false);
            return Program.ExitCodes.Success;
        }

        if (events.Count == 0)
        {
            await Console.Out.WriteLineAsync("No telemetry events are queued.").ConfigureAwait(false);
            return Program.ExitCodes.Success;
        }

        foreach (var line in events)
        {
            await Console.Out.WriteLineAsync(line).ConfigureAwait(false);
        }

        return Program.ExitCodes.Success;
    }

    private static async Task<int> ResetIdAsync(CliContext context)
    {
        var id = Telemetry.Client.ResetInstallId();
        if (context.Json)
        {
            await context.WriteJsonAsync(new { installId = id }).ConfigureAwait(false);
        }
        else
        {
            await Console.Out.WriteLineAsync($"New anonymous install id: {id}").ConfigureAwait(false);
        }

        return Program.ExitCodes.Success;
    }

    private static async Task<int> ClearAsync(CliContext context)
    {
        var queue = Telemetry.Client.Queue;
        var removed = queue.Count();
        var cleared = queue.Clear();
        if (context.Json)
        {
            await context.WriteJsonAsync(new { cleared, removed }).ConfigureAwait(false);
        }
        else
        {
            await Console.Out.WriteLineAsync(cleared ? $"Deleted {removed} queued event(s)." : "Could not delete the telemetry queue.").ConfigureAwait(false);
        }

        return cleared ? Program.ExitCodes.Success : Program.ExitCodes.Failure;
    }
}
