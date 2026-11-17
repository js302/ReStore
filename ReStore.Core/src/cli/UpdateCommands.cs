using ReStore.Core.src.core;
using ReStore.Core.src.updates;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.cli;

/// <summary><c>update check|install|skip</c>, the passive "a new version exists" notice and the daily background refresh (see docs/updates.md).</summary>
internal static class UpdateCommands
{
    /// <summary>How long <c>update check</c> may reuse its last answer; <c>--force</c> always asks GitHub.</summary>
    public static readonly TimeSpan ExplicitCacheLifetime = TimeSpan.FromHours(1);

    // Tests replace these to avoid the network and the real installer.
    internal static Func<UpdateService> CreateService { get; set; } = () => new UpdateService();
    internal static Func<UpdateInstaller> CreateInstaller { get; set; } = () => new UpdateInstaller();

    private static readonly OptionSpec DryRun = new("--dry-run", "Show what would be downloaded and installed; change nothing.");

    public static IEnumerable<CommandSpec> Specs { get; } =
    [
        new CommandSpec
        {
            Name = "update check",
            Category = CommandCategory.System,
            Summary = "Check GitHub for a newer ReStore (exit 0 whether or not one exists).",
            Description = "Contacts only GitHub; nothing about you is sent beyond the User-Agent. Reuses an answer younger than an hour unless --force. "
                + "JSON fields: current, latest, updateAvailable, critical, releaseNotesUrl, channel, canInstall, reason, installCommand, skippedVersion, checkedUtc, fromCache.",
            Options = [new OptionSpec("--force", "Ask GitHub even when the last answer is recent.")],
            Examples = ["update check", "update check --force --json"],
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            Handler = CheckAsync
        },
        new CommandSpec
        {
            Name = "update install",
            Category = CommandCategory.System,
            Summary = "Download, verify and install the latest ReStore for this install channel.",
            Description = "MSIX installs hand over to Windows (Add-AppxPackage; ReStore closes and the package is replaced). Portable installs replace the files in the install folder and keep restore.exe.old until the next start. "
                + "Both verify the SHA-256 from latest.json; MSIX also checks the package identity and publisher. Builds from source cannot self-update. "
                + "Asks y/N interactively; non-interactive runs need --yes (exit 4 otherwise). Exits 5 while another ReStore process uses the repository (see --wait).",
            Options = [DryRun],
            Examples = ["update install", "update install --dry-run --json", "update install --yes --non-interactive"],
            Mutates = true,
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            Handler = InstallAsync
        },
        new CommandSpec
        {
            Name = "update skip",
            Category = CommandCategory.System,
            Summary = "Stop offering one version (it is offered again when a newer one appears), or --clear.",
            Positionals = [new PositionalSpec("version", "The version to skip, for example 1.8.0.", Required: false)],
            Options = [new OptionSpec("--clear", "Forget the skipped version.")],
            Usage = ["update skip <version>", "update skip --clear"],
            Examples = ["update skip 1.8.0", "update skip --clear"],
            Mutates = true,
            Config = ConfigRequirement.None,
            State = StateRequirement.None,
            Validate = p => p.Positionals.Count + (p.HasOption("--clear") ? 1 : 0) != 1
                ? "Usage: update skip <version> | update skip --clear"
                : p.Positionals.Count == 1 && SemVer.ParseOrNull(p.Positionals[0]) == null ? $"'{p.Positionals[0]}' is not a version (expected MAJOR.MINOR.PATCH)." : null,
            Handler = SkipAsync
        }
    ];

    // ---- update check ---------------------------------------------------------------------------------------------------

    private static async Task<int> CheckAsync(CliContext ctx)
    {
        var service = CreateService();
        var settings = ctx.Config.Updates;
        var result = await GetResultAsync(service, settings, force: ctx.Parsed.HasOption("--force"), ctx.Cancellation).ConfigureAwait(false);
        var channel = AppChannel.Current;
        var reason = result.Result.UpdateAvailable ? UpdateInstaller.WhyNotInstallable(result.Result, channel) : channel == AppChannel.Dev ? UpdateInstaller.WhyNotInstallable(null, channel) : null;
        var canInstall = result.Result.UpdateAvailable && reason == null;
        var failed = result.Result.Status == UpdateStatus.Failed;
        var exitCode = failed ? Program.ExitCodes.Failure : Program.ExitCodes.Success;

        if (failed)
        {
            ctx.Logger.Log($"Update check failed: {result.Result.Error}", LogLevel.Error);
            ctx.Failure = new CliError(CliErrorCodes.Failure, $"Could not check for updates: {result.Result.Error}", "check the network connection, or see docs/updates.md");
        }

        if (ctx.Json)
        {
            await ctx.WriteJsonAsync(new
            {
                current = result.Result.Current,
                latest = result.Result.Latest,
                updateAvailable = result.Result.UpdateAvailable,
                critical = result.Result.Critical,
                releaseNotesUrl = result.Result.ReleaseNotesUrl,
                channel,
                canInstall,
                reason,
                installCommand = canInstall ? "restore update install" : null,
                skippedVersion = service.GetSkippedVersion(),
                checkedUtc = result.Result.CheckedUtc == default ? null : result.Result.CheckedUtc.ToString("o"),
                fromCache = result.FromCache
            }, exitCode).ConfigureAwait(false);
            return exitCode;
        }

        if (failed)
        {
            Console.Error.WriteLine($"Error: could not check for updates: {result.Result.Error}");
            return exitCode;
        }

        if (!result.Result.UpdateAvailable)
        {
            Console.WriteLine($"ReStore {result.Result.Current} is up to date.");
            return exitCode;
        }

        Console.WriteLine($"ReStore {UpdateText.Clean(result.Result.Latest)} is available (you have {UpdateText.Clean(result.Result.Current)}){(result.Result.Critical ? " - marked critical" : string.Empty)}.");
        if (!string.IsNullOrEmpty(result.Result.ReleaseNotesUrl))
        {
            Console.WriteLine($"Release notes: {UpdateText.Clean(result.Result.ReleaseNotesUrl)}");
        }

        Console.WriteLine(canInstall ? "Install it with: restore update install" : $"Cannot install automatically: {reason}");
        return exitCode;
    }

    private sealed record CheckOutcome(UpdateCheckResult Result, bool FromCache);

    private static async Task<CheckOutcome> GetResultAsync(UpdateService service, UpdatesConfig settings, bool force, CancellationToken cancellationToken)
    {
        if (!force)
        {
            var cache = service.Cache.Load();
            if (cache.Result is { Status: not UpdateStatus.Failed } && cache.LastCheckUtc is { } at && DateTime.UtcNow - at is { } age && age >= TimeSpan.Zero && age < ExplicitCacheLifetime
                && service.GetCachedResult() is { } cached)
            {
                return new CheckOutcome(cached, true);
            }
        }

        return new CheckOutcome(await service.CheckNowAsync(settings, cancellationToken).ConfigureAwait(false), false);
    }

    // ---- update install -------------------------------------------------------------------------------------------------

    private static async Task<int> InstallAsync(CliContext ctx)
    {
        var service = CreateService();
        var installer = CreateInstaller();
        var dryRun = ctx.Parsed.HasOption("--dry-run");

        if (UpdateInstaller.WhyNotInstallable(null, installer.Channel) is { } notInstallable)
        {
            return await FailAsync(ctx, notInstallable).ConfigureAwait(false);
        }

        var result = await service.CheckNowAsync(ctx.Config.Updates, ctx.Cancellation).ConfigureAwait(false);
        if (result.Status == UpdateStatus.Failed)
        {
            return await FailAsync(ctx, $"Could not check for updates: {result.Error}").ConfigureAwait(false);
        }

        if (!result.UpdateAvailable)
        {
            var message = $"ReStore {result.Current} is up to date; nothing to install.";
            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new { current = result.Current, latest = result.Latest, updateAvailable = false, installed = false, dryRun, message }).ConfigureAwait(false);
            }
            else
            {
                Console.WriteLine(message);
            }

            return Program.ExitCodes.Success;
        }

        InstallPlan plan;
        try
        {
            plan = installer.PreparePlan(result);
        }
        catch (UpdateInstallException ex)
        {
            return await FailAsync(ctx, ex.Message).ConfigureAwait(false);
        }

        if (dryRun)
        {
            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new
                {
                    current = result.Current,
                    latest = plan.Version,
                    updateAvailable = true,
                    installed = false,
                    dryRun = true,
                    channel = plan.Channel,
                    asset = new { name = plan.Asset.Name, url = plan.Asset.Url, size = plan.Asset.Size, sha256 = plan.Asset.Sha256 },
                    downloadPath = plan.DownloadPath,
                    message = plan.Description
                }).ConfigureAwait(false);
            }
            else
            {
                Console.WriteLine($"Dry run: ReStore {UpdateText.Clean(plan.Version)} is available (you have {UpdateText.Clean(result.Current)}).");
                Console.WriteLine(UpdateText.Clean(plan.Description));
                Console.WriteLine("Nothing was changed.");
            }

            return Program.ExitCodes.Success;
        }

        if (!CliRuntime.Yes && !Confirm(plan, result.Current))
        {
            return await FailAsync(ctx, "Cancelled; nothing was installed.").ConfigureAwait(false);
        }

        try
        {
            var progress = ctx.Parsed.Quiet ? null : new ConsoleDownloadProgress(plan.Asset.Name);
            var outcome = await installer.InstallAsync(result, new InstallOptions(), progress, ctx.Cancellation).ConfigureAwait(false);
            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new
                {
                    current = result.Current,
                    latest = outcome.Version,
                    updateAvailable = true,
                    installed = true,
                    channel = outcome.Channel,
                    restartRequired = !outcome.ProcessShouldExit,
                    logPath = outcome.LogPath,
                    message = outcome.Message
                }).ConfigureAwait(false);
            }
            else
            {
                Console.WriteLine(UpdateText.Clean(outcome.Message));
            }

            return Program.ExitCodes.Success;
        }
        catch (UpdateInstallException ex)
        {
            return await FailAsync(ctx, ex.Message).ConfigureAwait(false);
        }
    }

    private static bool Confirm(InstallPlan plan, string current)
    {
        if (CliRuntime.NonInteractive)
        {
            throw CliCommandException.NeedsUser(
                $"Installing ReStore {plan.Version} needs confirmation and this run cannot ask.", "pass --yes to install without asking");
        }

        Console.Error.WriteLine($"Install ReStore {UpdateText.Clean(plan.Version)} (you have {UpdateText.Clean(current)})? {UpdateText.Clean(plan.Description)}");
        Console.Error.Write("Continue? [y/N] ");
        var answer = Console.In.ReadLine()?.Trim();
        return answer != null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<int> FailAsync(CliContext ctx, string message, int exitCode = Program.ExitCodes.Failure)
    {
        ctx.Logger.Log(message, LogLevel.Error);
        Console.Error.WriteLine($"Error: {message}");
        if (ctx.Json)
        {
            ctx.Failure = new CliError(CliErrorCodes.ForExitCode(exitCode), message);
            await ctx.WriteJsonAsync(new { installed = false }, exitCode).ConfigureAwait(false);
        }

        return exitCode;
    }

    /// <summary>Prints a line on stderr at every 10% of the download.</summary>
    private sealed class ConsoleDownloadProgress(string name) : IProgress<DownloadProgress>
    {
        private int _lastStep = -1;

        public void Report(DownloadProgress value)
        {
            var step = value.Total > 0 ? (int)(value.Received * 10 / value.Total) : -1;
            if (step < 0 || step == _lastStep)
            {
                return;
            }

            _lastStep = step;
            Console.Error.WriteLine($"Downloading {name}: {Math.Min(100, step * 10)}%");
        }
    }

    // ---- update skip ----------------------------------------------------------------------------------------------------

    private static async Task<int> SkipAsync(CliContext ctx)
    {
        var service = CreateService();
        var clear = ctx.Parsed.HasOption("--clear");
        string? skipped = null;
        if (clear)
        {
            service.ClearSkip();
        }
        else
        {
            skipped = SemVer.ParseOrNull(ctx.Parsed.Positionals[0])!.ToString();
            service.Skip(skipped);
        }

        if (ctx.Json)
        {
            await ctx.WriteJsonAsync(new { skippedVersion = skipped, cleared = clear }).ConfigureAwait(false);
        }
        else
        {
            Console.WriteLine(clear ? "No version is skipped any more." : $"ReStore {skipped} will not be offered again.");
        }

        return Program.ExitCodes.Success;
    }

    // ---- Hooks used by Program ------------------------------------------------------------------------------------------

    /// <summary>
    /// After any command: <c>run-due</c> and <c>--service</c> refresh the cache (network, at most daily); every other human-readable
    /// command may print the one-line notice from the cache. Never throws, never uses the network on the notice path.
    /// </summary>
    internal static async Task AfterCommandAsync(CliContext ctx)
    {
        try
        {
            if (ctx.Spec.Name is "run-due" or "--service")
            {
                if (!ctx.Cancellation.IsCancellationRequested)
                {
                    await CreateService().CheckAutomaticallyAsync(ctx.Config.Updates, force: false, ctx.Cancellation).ConfigureAwait(false);
                }

                return;
            }

            if (ctx.Json || ctx.Parsed.Quiet || ctx.Spec.Group == "update")
            {
                return;
            }

            var service = CreateService();
            if (service.GetNotice() is { } notice)
            {
                Console.Error.WriteLine(notice.CliLine);
                service.MarkNotified(notice);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An update hint must never change a command's outcome.
        }
    }

    /// <summary>The <c>update</c> object of <c>status --json</c>, from the cache only.</summary>
    internal static object BuildStatusDocument()
    {
        var service = CreateService();
        var cached = service.GetCachedResult();
        var data = service.Cache.Load();
        return new
        {
            current = UpdateChecker.CurrentVersion,
            latest = cached?.Latest,
            updateAvailable = cached?.UpdateAvailable ?? false,
            critical = cached?.Critical ?? false,
            releaseNotesUrl = cached?.ReleaseNotesUrl,
            skippedVersion = data.SkippedVersion,
            lastCheckUtc = data.LastCheckUtc?.ToString("o")
        };
    }

    /// <summary>The one human-readable line of <c>status</c>.</summary>
    internal static string BuildStatusLine()
    {
        var service = CreateService();
        var cached = service.GetCachedResult();
        var last = service.Cache.Load().LastCheckUtc;
        return cached switch
        {
            { Status: UpdateStatus.UpdateAvailable } => $"Updates: ReStore {cached.Latest} is available (you have {UpdateChecker.CurrentVersion}); run 'restore update install'.",
            { Status: UpdateStatus.UpToDate } => $"Updates: up to date (checked {last?.ToLocalTime():g}).",
            _ => "Updates: not checked yet (restore update check)."
        };
    }
}
