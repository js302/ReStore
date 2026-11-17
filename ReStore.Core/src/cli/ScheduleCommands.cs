using System.Globalization;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;

namespace ReStore.Core.src.cli;

/// <summary><c>schedule preview</c> and <c>schedule next</c>: check a schedule expression, and see what runs when (with the Task Scheduler commands in <c>schedule install|remove|status</c>).</summary>
internal static class ScheduleCommands
{
    private const int DefaultCount = 5;
    private const int MaxCount = 100;

    public static IEnumerable<CommandSpec> Specs { get; } =
    [
        new CommandSpec
        {
            Name = "schedule preview",
            Category = CommandCategory.Automation,
            Summary = "Check a schedule expression: what it means and its next run times (changes nothing).",
            Description = "Parses the expression like 'config set-schedule' does and prints the description and the next runs in local time, so an expression can be checked before it is saved. "
                + "Grammar (case-insensitive): off | every <N>m|h|d (at least 5m) | daily HH:mm[,HH:mm...] | weekdays HH:mm | weekends HH:mm | <days> HH:mm (mon,wed,fri or mon-fri) | weekly <day> HH:mm | monthly <1-28|last> HH:mm | cron <minute hour day-of-month month day-of-week>. "
                + "The expression may be quoted or written as several words. Times are local wall-clock times: a time skipped by the spring clock change runs at the first valid minute after it, a repeated time (autumn) runs once. "
                + "'every' schedules count from the last backup, so their listed times assume a backup right now. An invalid expression exits 2 with the reason. JSON: expression, canonical, kind, description, timeZone, nextRuns (utc, local), note.",
            Positionals = [new PositionalSpec("expression", "The schedule, for example \"weekdays 18:30\" or \"cron 0 2 * * *\".", Repeatable: true)],
            Options = [new OptionSpec("--count", $"How many run times to show (default {DefaultCount}, at most {MaxCount}).", "N")],
            Examples = ["schedule preview \"daily 02:00\"", "schedule preview \"mon,wed,fri 07:15\" --count 3", "schedule preview \"cron */15 * * * *\" --json"],
            Config = ConfigRequirement.None,
            State = StateRequirement.None,
            PayloadOnStdout = true,
            Validate = ValidatePreview,
            Handler = PreviewAsync
        },
        new CommandSpec
        {
            Name = "schedule next",
            Category = CommandCategory.Automation,
            Summary = "Show when each folder, the system backup, verification and retention run next, and why something is not running now.",
            Description = "Read-only; uses the scheduler's own rules (the same ones the app, 'restore --service' and 'run-due' use). Per target: its schedule and where it comes from, the last run, when it is due, when it will actually start (the backup window may delay it) and a state: "
                + "scheduled, due (runs on the next sweep), waiting-for-window (due, but the backup window is closed), paused, busy (the repository is in use), off, disabled or folder-missing, with a reason. "
                + "Also reports the backup window and whether protection is paused. JSON: generatedUtc, timeZone, paused, repository, backupWindow, nextDueUtc, targets.",
            Examples = ["schedule next", "schedule next --json"],
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.ReadOnly,
            PayloadOnStdout = true,
            Handler = NextAsync
        }
    ];

    private static string? ValidatePreview(Program.ParsedCommand parsed)
    {
        if (parsed.GetOption("--count") is { } count && !(int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= MaxCount))
        {
            return $"Invalid --count value '{count}'. Expected a whole number from 1 to {MaxCount}.";
        }

        return BackupSchedule.TryParse(string.Join(' ', parsed.Positionals), out _, out var error)
            ? null
            : $"Invalid schedule '{string.Join(' ', parsed.Positionals)}': {error}";
    }

    private static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static string IsoLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static string Pretty(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local).ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static async Task<int> PreviewAsync(CliContext ctx)
    {
        var text = string.Join(' ', ctx.Parsed.Positionals);
        var schedule = BackupSchedule.Parse(text);
        var count = ctx.Parsed.GetOption("--count") is { } c ? int.Parse(c, CultureInfo.InvariantCulture) : DefaultCount;
        var now = DateTime.UtcNow;

        List<DateTime> runs;
        string? note = null;
        if (schedule.IsInterval)
        {
            runs = [.. Enumerable.Range(1, count).Select(i => now + (schedule.Interval!.Value * i))];
            note = "Interval schedules count from the last backup (a folder never backed up is due at once); these times assume a backup right now.";
        }
        else
        {
            runs = schedule.NextOccurrences(now, count, TimeZoneInfo.Local);
            if (schedule.IsOff)
            {
                note = "Nothing is scheduled: the folder is backed up by the file watcher or by hand only.";
            }
            else if (runs.Count == 0)
            {
                note = "This schedule has no run within the next eight years.";
            }
        }

        var lines = new List<string> { $"Schedule: {schedule.Description}", $"Expression: {schedule.Expression}", $"Time zone: {TimeZoneInfo.Local.Id}" };
        lines.AddRange(runs.Count > 0 ? ["Next runs:", .. runs.Select(r => $"  {Pretty(r)}")] : []);
        if (note != null)
        {
            lines.Add(note);
        }

        await CliSupport.EmitAsync(
            ctx,
            new
            {
                expression = text,
                canonical = schedule.Expression,
                kind = schedule.Kind.ToString().ToLowerInvariant(),
                description = schedule.Description,
                timeZone = TimeZoneInfo.Local.Id,
                nextRuns = runs.Select(r => new { utc = Iso(r), local = IsoLocal(r) }),
                note
            },
            string.Join(Environment.NewLine, lines)).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> NextAsync(CliContext ctx)
    {
        var now = DateTime.UtcNow;
        var (busy, busyReason) = await ProtectionStatusBuilder.ProbeBusyAsync(RepositoryCoordinator.Instance, ctx.Cancellation).ConfigureAwait(false);
        using var scheduler = new BackupScheduler(ctx.Config, ctx.Logger, ctx.State, ctx.SizeAnalyzer);
        var pausedUntil = new ProtectionPauseStore().LoadActive(now);
        var targets = scheduler.DescribeTargets(now, pausedUntil != null, busy ? busyReason : null);
        var nextDue = scheduler.ComputeNextRunUtc();
        var status = ProtectionStatusBuilder.Build(ctx.Config, ctx.State, scheduler, now, busy, busyReason);
        var window = status.Window;

        var document = new
        {
            generatedUtc = Iso(now),
            timeZone = TimeZoneInfo.Local.Id,
            paused = pausedUntil != null,
            pausedUntilUtc = pausedUntil is { } until && !ProtectionPause.IsIndefinite(until) ? Iso(until) : null,
            repository = new { busy, reason = busyReason },
            backupWindow = new
            {
                configured = window.Configured,
                active = window.Active,
                openNow = window.OpenNow,
                start = window.Start,
                end = window.End,
                days = window.Days,
                description = window.Description,
                nextOpenUtc = window.NextOpenUtc is { } open ? Iso(open) : null
            },
            nextDueUtc = nextDue is { } due ? Iso(due) : null,
            targets = targets.Select(t => new
            {
                kind = t.Kind,
                path = t.Path,
                schedule = new { expression = t.Expression, description = t.Description, source = t.Source },
                lastUtc = t.LastUtc is { } last ? Iso(last) : null,
                dueUtc = t.DueUtc is { } d ? Iso(d) : null,
                nextStartUtc = t.NextStartUtc is { } next ? Iso(next) : null,
                nextStartLocal = t.NextStartUtc is { } nextLocal ? IsoLocal(nextLocal) : null,
                state = t.State,
                reason = t.Reason
            })
        };

        var text = new List<string>
        {
            pausedUntil != null ? $"Protection is paused {ProtectionPause.Describe(pausedUntil.Value)}." : "Protection is running.",
            $"Backup window: {window.Description}{(window.Active ? (window.OpenNow ? " (open now)" : $" (closed; opens {Pretty(window.NextOpenUtc!.Value)})") : string.Empty)}"
        };
        foreach (var target in targets)
        {
            var name = target.Kind == "folder" ? target.Path : target.Kind;
            var when = target.NextStartUtc is { } start ? (start <= now ? "now" : Pretty(start)) : "-";
            text.Add($"{name}: {target.State}, next {when}");
            text.Add($"    {target.Description} ({target.Source})");
            if (target.Reason != null)
            {
                text.Add($"    {target.Reason}");
            }
        }

        await CliSupport.EmitAsync(ctx, document, string.Join(Environment.NewLine, text)).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }
}
