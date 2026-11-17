using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReStore.Core.src.backup;
using ReStore.Core.src.cli;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.sharing;
using ReStore.Core.src.utils;

namespace ReStore.Core
{
    /// <summary>Process-level seams of the operations commands; tests replace them, production keeps the defaults.</summary>
    internal static class OpsEnvironment
    {
        public static Func<bool> IsWindows { get; set; } = OperatingSystem.IsWindows;

        /// <summary>Whether <c>conhost.exe</c> exists (the task then runs hidden through it); null = look at the real system.</summary>
        public static Func<bool>? ConhostAvailable { get; set; }

        public static ProcessInvoker Runner { get; set; } = (file, arguments, timeout, token) => ProcessRunner.RunAsync(file, arguments, timeout, token);

        /// <summary>The executable the scheduled task runs; null when it cannot be determined.</summary>
        public static Func<string?> TaskExecutable { get; set; } = () => OpsCommandHelpers.ResolveTaskExecutable(
            AppChannel.Current, Environment.ProcessPath, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        public static Func<IConfigManager, ILogger, ShareService> CreateShareService { get; set; } = (config, logger) => new ShareService(config, logger);

        public static Func<string, string?> GetEnvironmentVariable { get; set; } = Environment.GetEnvironmentVariable;

        public static void Reset()
        {
            IsWindows = OperatingSystem.IsWindows;
            ConhostAvailable = null;
            Runner = (file, arguments, timeout, token) => ProcessRunner.RunAsync(file, arguments, timeout, token);
            TaskExecutable = () => OpsCommandHelpers.ResolveTaskExecutable(
                AppChannel.Current, Environment.ProcessPath, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            CreateShareService = (config, logger) => new ShareService(config, logger);
            GetEnvironmentVariable = Environment.GetEnvironmentVariable;
        }
    }

    /// <summary>Pure helpers of the operations commands, kept out of <see cref="Program"/> so tests can reach them directly.</summary>
    internal static class OpsCommandHelpers
    {
        /// <summary>Storage providers the Share window offers (and so the ones that can create links).</summary>
        public static readonly string[] SharingStorageTypes = ["s3", "azure", "gcp", "dropbox", "b2"];

        public static readonly TimeSpan DefaultShareExpiry = TimeSpan.FromDays(7);

        /// <summary>
        /// The program the scheduled task starts. MSIX: the app execution alias (the versioned package folder changes on every update).
        /// Portable and dev: this executable. Null when running through the <c>dotnet</c> host (the task could not start it).
        /// </summary>
        public static string? ResolveTaskExecutable(string channel, string? processPath, string localAppData)
        {
            if (channel == AppChannel.Msix)
            {
                return Path.Combine(localAppData, "Microsoft", "WindowsApps", "restore.exe");
            }

            if (string.IsNullOrWhiteSpace(processPath)
                || Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return processPath;
        }

        /// <summary>
        /// A warning when encryption is on but a task (which has no terminal) would have no password source: none of
        /// <c>RESTORE_ENCRYPTION_PASSWORD</c>, <c>RESTORE_ENCRYPTION_PASSWORD_FILE</c> (an existing file) or a remembered password file.
        /// </summary>
        public static string? PasswordWarning(IConfigManager config, Func<string, string?> getEnvironmentVariable, string rememberedPasswordFile)
        {
            if (!config.Encryption.Enabled)
            {
                return null;
            }

            static string? Resolve(Func<string, string?> get, string name)
            {
                var value = get(name);
                if (string.IsNullOrWhiteSpace(value) && OperatingSystem.IsWindows())
                {
                    // A scheduled task does not see the variables of this terminal, only the persistent ones.
                    value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
                }

                return value;
            }

            if (!string.IsNullOrEmpty(Resolve(getEnvironmentVariable, "RESTORE_ENCRYPTION_PASSWORD")))
            {
                return null;
            }

            var passwordFile = Resolve(getEnvironmentVariable, "RESTORE_ENCRYPTION_PASSWORD_FILE");
            if (!string.IsNullOrWhiteSpace(passwordFile) && File.Exists(passwordFile))
            {
                return null;
            }

            if (File.Exists(rememberedPasswordFile))
            {
                return null;
            }

            return "Encryption is enabled but no password is available without a prompt, so the scheduled 'run-due' would exit 4 (needs_user) and do no work: "
                + "set RESTORE_ENCRYPTION_PASSWORD or RESTORE_ENCRYPTION_PASSWORD_FILE as a persistent (user) environment variable, or run 'restore unlock --remember'.";
        }

        /// <summary>y/yes (any case) confirms; everything else, including an empty answer or end of input, declines.</summary>
        public static bool Confirm(string question, TextReader input, TextWriter prompt)
        {
            prompt.Write($"{question} [y/N] ");
            prompt.Flush();
            var answer = input.ReadLine()?.Trim();
            return answer != null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Maps <c>backup</c>, <c>system-restore</c>, ... to an <see cref="ActivityType"/>.</summary>
        public static bool TryParseActivityType(string? text, out ActivityType type)
            => Enum.TryParse(text?.Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true, out type)
               && Enum.IsDefined(type)
               && !char.IsDigit(text![0]);

        public static string ActivityTypeName(ActivityType type) => type switch
        {
            ActivityType.SystemBackup => "system-backup",
            ActivityType.SystemRestore => "system-restore",
            _ => type.ToString().ToLowerInvariant()
        };
    }

    // Operations that used to exist only in the app: retention apply, pause/resume, activity, storage usage, format upgrade,
    // Task Scheduler setup, diagnostics, sharing and the log. Handlers call the same Core services the app calls.
    public partial class Program
    {
        private static readonly OptionSpec StorageFilterOption = new(
            "--storage", "Only this storage type (a key of storageSources in config.json, e.g. local, s3).", "storageType");

        private static readonly string[] ActivityTypeNames = ["backup", "restore", "verify", "system-backup", "system-restore", "repair"];

        internal static IEnumerable<CommandSpec> OpsSpecs() =>
        [
            new CommandSpec
            {
                Name = "pause",
                Category = CommandCategory.Automation,
                Summary = "Pause background protection (watcher and scheduler) for a while or until resumed; 'pause status' shows it.",
                Description = "The same pause as the tray menu: stored in state/protection_pause.json, which the running app, 'restore --service' and 'restore run-due' read, so a pause written here stops them within seconds (the app) or at their next decision. "
                    + "Without an option the pause lasts until 'resume'. --until-tomorrow resumes at the next 08:00 that is at least an hour away. Manual backups ('backup', 'Back up now') still work while paused.",
                Positionals = [new PositionalSpec("status", "Write 'status' to show whether protection is paused instead of pausing.", Required: false)],
                Options =
                [
                    new OptionSpec("--for", "Pause for this long: 30m, 2h, 1d, 1h30m.", "duration"),
                    new OptionSpec("--until-tomorrow", "Pause until the next 08:00 that is at least an hour away."),
                    new OptionSpec("--indefinitely", "Pause until 'resume' (the default).")
                ],
                Usage = ["pause [--for <duration> | --until-tomorrow | --indefinitely]", "pause status"],
                Examples = ["pause --for 2h", "pause --until-tomorrow", "pause status --json"],
                Mutates = true,
                Config = ConfigRequirement.None,
                State = StateRequirement.None,
                Validate = ValidatePause,
                Handler = RunPauseAsync
            },
            new CommandSpec
            {
                Name = "resume",
                Category = CommandCategory.Automation,
                Summary = "End a pause of background protection.",
                Description = "Removes the pause written by 'pause' or the tray menu. The running app resumes protection within seconds; 'restore --service' and 'restore run-due' resume at their next decision.",
                Examples = ["resume"],
                Mutates = true,
                Config = ConfigRequirement.None,
                State = StateRequirement.None,
                Handler = RunResumeAsync
            },
            new CommandSpec
            {
                Name = "activity",
                Category = CommandCategory.Browse,
                Summary = "List recent operations (backups, restores, verifications, system backups, repairs), newest first.",
                Description = "The data of the app's Activity page: backup runs and the recorded operations with their result and message. Read-only.",
                Options =
                [
                    new OptionSpec("--limit", "How many entries to show (default 20).", "N"),
                    new OptionSpec("--type", "Only this kind of operation.", "type", AllowedValues: ActivityTypeNames),
                    new OptionSpec("--failed", "Only operations that failed.")
                ],
                Examples = ["activity --limit 10", "activity --type verify --failed --json"],
                Config = ConfigRequirement.Loaded,
                Validate = ValidateActivity,
                Handler = RunActivityAsync
            },
            new CommandSpec
            {
                Name = "usage",
                Category = CommandCategory.Browse,
                Summary = "Show how much storage the backups use (the Dashboard storage panel).",
                Description = "Sizes are worked out from the local history, so they are close, not exact. --refresh also lists chunks/ in each storage for the real stored size (the app's 'Refresh from storage'); that reads the storage and needs it reachable. "
                    + "With --storage the totals are the sums of that storage's groups (a chunk shared between groups counts in each).",
                Options = [StorageFilterOption, new OptionSpec("--refresh", "Also measure the real size by listing the storage.")],
                Examples = ["usage", "usage --refresh --json"],
                Config = ConfigRequirement.Loaded,
                Handler = RunUsageAsync
            },
            new CommandSpec
            {
                Name = "upgrade-format",
                Category = CommandCategory.Repository,
                Summary = "Write backups in the newest snapshot format (v3) from the next backup on.",
                Description = "The Settings 'Upgrade backup format...' action: sets chunkDiffing.manifestVersion to 3. It applies to every backup set, not one group, and changes nothing in the storage by itself: the next backup writes the new format, older snapshots stay readable. "
                    + "Encrypted repositories upload their data once more (chunk ids change); unencrypted ones upload nothing again. Versions of ReStore older than this one cannot read v3 snapshots. Running it again changes nothing. "
                    + "The optional group is only checked to exist. Restart the app afterwards so it reloads the configuration.",
                Positionals = [new PositionalSpec("groupKeyOrPath", "A backed-up folder (path or snapshots/ folder name) to confirm; the setting is global.", Required: false)],
                Examples = ["upgrade-format", "upgrade-format C:\\Users\\me\\Documents --json"],
                Mutates = true,
                Config = ConfigRequirement.Loaded,
                Handler = RunUpgradeFormatAsync
            },
            new CommandSpec
            {
                Name = "schedule install",
                Category = CommandCategory.Automation,
                Summary = "Create the Windows scheduled task \\ReStore\\run-due that runs 'run-due --quiet' every N minutes.",
                Description = "Runs as the current user only while logged on (no Windows password is stored), hidden, also on battery, from the moment you log on. Replaces an existing task. "
                    + "The MSIX install uses the 'restore.exe' app execution alias, a portable copy uses its own path. Encrypted repositories need a password source for unattended runs (a warning says when none exists). Windows only.",
                Options = [new OptionSpec("--every", "Minutes between runs (1-1440, default 15).", "minutes")],
                Examples = ["schedule install", "schedule install --every 30"],
                Mutates = true,
                WindowsOnly = true,
                Config = ConfigRequirement.Loaded,
                State = StateRequirement.None,
                Validate = ValidateSchedule,
                Handler = RunScheduleInstallAsync
            },
            new CommandSpec
            {
                Name = "schedule remove",
                Category = CommandCategory.Automation,
                Summary = "Delete the scheduled task \\ReStore\\run-due (nothing happens when it is not installed).",
                Examples = ["schedule remove"],
                Mutates = true,
                Destructive = true,
                WindowsOnly = true,
                Config = ConfigRequirement.None,
                State = StateRequirement.None,
                Handler = RunScheduleRemoveAsync
            },
            new CommandSpec
            {
                Name = "schedule status",
                Category = CommandCategory.Automation,
                Summary = "Show whether the scheduled task is installed, its interval, executable and last run.",
                Description = "Reads the task with schtasks.exe. Warns when encryption is enabled and a scheduled run would have no password source.",
                Examples = ["schedule status --json"],
                WindowsOnly = true,
                Config = ConfigRequirement.Loaded,
                State = StateRequirement.None,
                Handler = RunScheduleStatusAsync
            },
            new CommandSpec
            {
                Name = "diagnostics",
                Category = CommandCategory.Info,
                Summary = "Print the redacted diagnostics report (versions, OS, config without secrets, log tail), as 'Copy diagnostics' does.",
                Description = "Safe to paste into a bug report: secrets, tokens and URL credentials are masked. Works even when config.json is broken. With --json the fields are structured (config as an object, logLines as a list) and 'report' holds the same text the app copies.",
                Examples = ["diagnostics", "diagnostics --json"],
                Config = ConfigRequirement.None,
                State = StateRequirement.None,
                PayloadOnStdout = true,
                Handler = RunDiagnosticsAsync
            },
            new CommandSpec
            {
                Name = "share",
                Category = CommandCategory.BackupRestore,
                Summary = "Upload a file to a storage and print a share link (s3, azure, gcp, dropbox, b2).",
                Description = "The Share window: uploads the file to shared/<guid>/<name> in the storage and creates a link that expires after --expires (default 7d; Dropbox links do not expire). The file is not encrypted. "
                    + "Without --storage the global storage is used when it supports links, else the first configured one that does.",
                Positionals = [new PositionalSpec("file", "File to share.")],
                Options = [StorageFilterOption, new OptionSpec("--expires", "How long the link works: 1h, 2d, 7d (default 7d).", "duration")],
                Examples = ["share C:\\Users\\me\\report.pdf", "share report.pdf --storage s3 --expires 2d --json"],
                Mutates = true,
                Config = ConfigRequirement.Loaded,
                State = StateRequirement.None,
                Validate = p => p.GetOption("--expires") is { } expires && !CliDurations.TryParse(expires, out _) ? $"Invalid --expires value '{expires}'. {CliDurations.Expected}" : null,
                PayloadOnStdout = true,
                Handler = RunShareAsync
            },
            new CommandSpec
            {
                Name = "logs",
                Category = CommandCategory.Info,
                Summary = "Show where the log file is and its last lines (secrets masked).",
                Description = "Without options prints the path and the last 50 lines. --path prints only the location. The log is shared by the app and the CLI.",
                Options = [new OptionSpec("--tail", "How many of the last lines to show (default 50).", "N"), new OptionSpec("--path", "Only print the log file location.")],
                Examples = ["logs --tail 100", "logs --path"],
                Config = ConfigRequirement.None,
                State = StateRequirement.None,
                Validate = p => TryGetPositiveInt(p.Options, "--tail", out _, out var error) ? null : error,
                PayloadOnStdout = true,
                Handler = RunLogsAsync
            }
        ];

        private static CliCommandException UsageError(string message, string? hint = null) => new(ExitCodes.Usage, CliErrorCodes.Usage, message, hint);

        private static CliCommandException FailureError(string message, string? hint = null) => new(ExitCodes.Failure, CliErrorCodes.Failure, message, hint);

        private static string ResolveConfiguredStorage(IConfigManager config, string name)
        {
            var match = config.StorageSources.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
            return match ?? throw UsageError(
                $"Unknown storage type '{name}'. Configured: {(config.StorageSources.Count == 0 ? "none" : string.Join(", ", config.StorageSources.Keys))}.");
        }

        /// <summary>Asks before a destructive action: <c>--yes</c> confirms, a non-interactive run needs it (exit 4), otherwise y/N on stderr.</summary>
        private static void ConfirmDestructive(CliContext ctx, string summary)
        {
            if (ctx.Parsed.Yes || CliRuntime.Yes)
            {
                return;
            }

            if (CliRuntime.NonInteractive)
            {
                throw CliCommandException.NeedsUser(
                    $"{summary} A person must confirm it.", "re-run with --yes to confirm in advance (after a --dry-run or 'plan' preview)");
            }

            if (!OpsCommandHelpers.Confirm($"{summary} This cannot be undone. Continue?", Console.In, Console.Error))
            {
                throw FailureError("Aborted: nothing was deleted.");
            }
        }

        // ---- retention ----------------------------------------------------------------------------------------------

        private static string? ValidateRetention(ParsedCommand parsed)
        {
            var action = parsed.Positionals.Count == 1 ? parsed.Positionals[0].ToLowerInvariant() : null;
            if (action is not (null or "plan" or "apply"))
            {
                return $"Unknown retention action '{parsed.Positionals[0]}'. Expected 'apply' (delete now) or 'plan' (preview).";
            }

            if (action == null && !parsed.HasOption("--dry-run"))
            {
                return "'retention' needs an action: 'retention --dry-run' (or 'retention plan') previews, 'retention apply' deletes what the policy marks.";
            }

            return null;
        }

        private static Task<int> RunRetentionAsync(CliContext ctx)
        {
            var isApply = ctx.Parsed.Positionals.Count == 1 && ctx.Parsed.Positionals[0].Equals("apply", StringComparison.OrdinalIgnoreCase);
            return isApply && !ctx.Parsed.HasOption("--dry-run") ? RunRetentionApplyAsync(ctx) : RunRetentionPreviewAsync(ctx);
        }

        private static RetentionPlan FilterPlanToStorage(RetentionPlan plan, string? storageType) =>
            storageType == null
                ? plan
                : new RetentionPlan([.. plan.Decisions.Where(d => string.Equals(d.StorageType, storageType, StringComparison.OrdinalIgnoreCase))]);

        private static async Task<int> RunRetentionApplyAsync(CliContext ctx)
        {
            var config = ctx.Config;
            var state = ctx.State;
            var logger = ctx.Logger;
            if (!config.Retention.Enabled)
            {
                throw FailureError(
                    "Retention is disabled in the configuration (retention.enabled is false), so nothing is applied. Preview what the policy would do with 'retention plan'; enable it in config.json to apply it.");
            }

            string? storage = ctx.StorageOverride is { } storageName ? ResolveConfiguredStorage(config, storageName) : null;
            string? group = null;
            if (ctx.Parsed.GetOption("--group") is { } filter)
            {
                group = state.GetBackupGroups().FirstOrDefault(g => g.Equals(filter, StringComparison.OrdinalIgnoreCase))
                    ?? throw FailureError($"No backup group matches '{filter}'.");
            }

            var manager = new RetentionManager(logger, config, state, passwordProvider: CreateCliPasswordProvider(config));
            var planned = manager.PreviewPlans(config.Retention, group)
                .Select(p => (p.Group, Plan: FilterPlanToStorage(p.Plan, storage)))
                .Select(p => (p.Group, Doomed: p.Plan.Decisions.Where(d => !d.Keep).ToList()))
                .Where(p => p.Doomed.Count > 0)
                .ToList();
            var totalPlanned = planned.Sum(p => p.Doomed.Count);

            if (totalPlanned > 0)
            {
                foreach (var (plannedGroup, doomed) in planned)
                {
                    Console.WriteLine($"Group: {plannedGroup}");
                    foreach (var decision in doomed)
                    {
                        Console.WriteLine($"  DELETE [{decision.StorageType}] {decision.Backup.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}  {decision.Backup.Path}  ({string.Join("; ", decision.Reasons)})");
                    }
                }

                ConfirmDestructive(ctx, $"Retention will permanently delete {totalPlanned} snapshot(s) in {planned.Count} group(s)"
                    + (storage != null ? $" from storage '{storage}'" : string.Empty) + ".");

                await manager.ApplyToGroupsAsync([.. planned.Select(p => p.Group)], storage, ctx.Cancellation).ConfigureAwait(false);
            }

            var results = planned.Select(p =>
            {
                var remaining = state.GetBackupsForGroup(p.Group).Select(b => b.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return (p.Group, Rows: p.Doomed.Select(d => (Decision: d, Deleted: !remaining.Contains(d.Backup.Path))).ToList());
            }).ToList();
            var deleted = results.Sum(r => r.Rows.Count(row => row.Deleted));
            var failed = totalPlanned - deleted;

            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new
                {
                    applied = totalPlanned > 0,
                    enabled = config.Retention.Enabled,
                    mode = config.Retention.Mode,
                    planned = totalPlanned,
                    deleted,
                    failed,
                    groups = results.Select(r => new
                    {
                        group = r.Group,
                        snapshots = r.Rows.Select(row => new
                        {
                            path = row.Decision.Backup.Path,
                            storage = row.Decision.StorageType,
                            timestamp = row.Decision.Backup.Timestamp,
                            label = row.Decision.Backup.Label,
                            reasons = row.Decision.Reasons,
                            deleted = row.Deleted
                        })
                    })
                }, failed > 0 ? ExitCodes.Failure : ExitCodes.Success).ConfigureAwait(false);
                return failed > 0 ? ExitCodes.Failure : ExitCodes.Success;
            }

            if (totalPlanned == 0)
            {
                Console.WriteLine("Nothing to delete: the retention policy keeps every snapshot.");
                return ExitCodes.Success;
            }

            if (failed > 0)
            {
                logger.Log($"Retention deleted {deleted} of {totalPlanned} snapshot(s); {failed} could not be deleted (see the log).", LogLevel.Error);
                return ExitCodes.Failure;
            }

            Console.WriteLine($"Retention deleted {deleted} snapshot(s).");
            return ExitCodes.Success;
        }

        // ---- pause / resume -----------------------------------------------------------------------------------------

        private static string? ValidatePause(ParsedCommand parsed)
        {
            var modes = new[] { "--for", "--until-tomorrow", "--indefinitely" }.Count(parsed.HasOption);
            if (parsed.Positionals.Count == 1)
            {
                if (!parsed.Positionals[0].Equals("status", StringComparison.OrdinalIgnoreCase))
                {
                    return $"Unknown argument '{parsed.Positionals[0]}'. Use 'pause status', or 'pause' with --for, --until-tomorrow or --indefinitely.";
                }

                return modes > 0 ? "'pause status' takes no options." : null;
            }

            if (modes > 1)
            {
                return "--for, --until-tomorrow and --indefinitely cannot be combined.";
            }

            return parsed.GetOption("--for") is { } duration && !CliDurations.TryParse(duration, out _)
                ? $"Invalid --for value '{duration}'. {CliDurations.Expected}"
                : null;
        }

        private static object PauseDocument(DateTime? until) => until is not { } end
            ? new { paused = false, indefinite = false, untilUtc = (DateTime?)null, until = (string?)null }
            : new
            {
                paused = true,
                indefinite = ProtectionPause.IsIndefinite(end),
                untilUtc = ProtectionPause.IsIndefinite(end) ? (DateTime?)null : end,
                until = (string?)ProtectionPause.Describe(end)
            };

        private static async Task<int> RunPauseAsync(CliContext ctx)
        {
            var store = new ProtectionPauseStore();
            var parsed = ctx.Parsed;
            if (parsed.Positionals.Count == 1)
            {
                var active = store.LoadActive(DateTime.UtcNow);
                if (ctx.Json)
                {
                    await ctx.WriteJsonAsync(PauseDocument(active)).ConfigureAwait(false);
                }
                else
                {
                    Console.WriteLine(active is { } end ? $"Protection is paused {ProtectionPause.Describe(end)}. Resume it with 'restore resume'." : "Protection is not paused.");
                }

                return ExitCodes.Success;
            }

            var now = DateTime.UtcNow;
            DateTime until;
            if (parsed.GetOption("--for") is { } text && CliDurations.TryParse(text, out var duration))
            {
                until = now + duration;
            }
            else if (parsed.HasOption("--until-tomorrow"))
            {
                until = ProtectionPause.UntilTomorrowUtc(now);
            }
            else
            {
                until = ProtectionPause.Indefinite;
            }

            try
            {
                store.Save(until);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw FailureError($"Could not write the pause file: {ex.Message}");
            }

            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(PauseDocument(until)).ConfigureAwait(false);
            }
            else
            {
                Console.WriteLine($"Protection paused {ProtectionPause.Describe(until)}. The running app, 'restore --service' and 'restore run-due' pick this up on their own; end it with 'restore resume'.");
            }

            return ExitCodes.Success;
        }

        private static async Task<int> RunResumeAsync(CliContext ctx)
        {
            var store = new ProtectionPauseStore();
            var wasPaused = store.LoadActive(DateTime.UtcNow) != null;
            store.Clear();
            if (store.Load() != null)
            {
                throw FailureError("Could not remove the pause file; check the permissions of the state folder.");
            }

            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new { paused = false, wasPaused }).ConfigureAwait(false);
            }
            else
            {
                Console.WriteLine(wasPaused ? "Protection resumed." : "Protection was not paused.");
            }

            return ExitCodes.Success;
        }

        // ---- activity -----------------------------------------------------------------------------------------------

        private static string? ValidateActivity(ParsedCommand parsed)
        {
            if (!TryGetPositiveInt(parsed.Options, "--limit", out _, out var error))
            {
                return error;
            }

            return parsed.GetOption("--type") is { } type && !OpsCommandHelpers.TryParseActivityType(type, out _)
                ? $"Invalid --type value '{type}'. Expected one of: {string.Join(", ", ActivityTypeNames)}."
                : null;
        }

        private static async Task<int> RunActivityAsync(CliContext ctx)
        {
            var parsed = ctx.Parsed;
            var limit = parsed.GetOption("--limit") is { } limitText ? int.Parse(limitText, CultureInfo.InvariantCulture) : 20;
            ActivityType? type = parsed.GetOption("--type") is { } typeText && OpsCommandHelpers.TryParseActivityType(typeText, out var parsedType) ? parsedType : null;
            ActivityResult? result = parsed.HasOption("--failed") ? ActivityResult.Failed : null;

            var items = ActivityTimeline.Build(ctx.State, type, result, limit);
            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new
                {
                    count = items.Count,
                    items = items.Select(i => new
                    {
                        type = OpsCommandHelpers.ActivityTypeName(i.Type),
                        target = i.Target,
                        startUtc = i.StartUtc,
                        endUtc = i.EndUtc,
                        durationSeconds = (int)i.Duration.TotalSeconds,
                        result = i.Result.ToString().ToLowerInvariant(),
                        message = i.Message,
                        storage = i.StorageType
                    })
                }).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            if (items.Count == 0)
            {
                Console.WriteLine("No activity recorded yet.");
                return ExitCodes.Success;
            }

            foreach (var item in items)
            {
                var message = string.IsNullOrWhiteSpace(item.Message) ? string.Empty : $"  {item.Message}";
                Console.WriteLine($"{DisplayTime.Format(item.EndUtc)}  {item.Result,-9} {OpsCommandHelpers.ActivityTypeName(item.Type),-14} {item.Target}{message}");
            }

            return ExitCodes.Success;
        }

        // ---- usage --------------------------------------------------------------------------------------------------

        private static async Task<int> RunUsageAsync(CliContext ctx)
        {
            var config = ctx.Config;
            string? storage = ctx.StorageOverride is { } name ? ResolveConfiguredStorage(config, name) : null;
            var report = await StorageUsageCalculator.ComputeAsync(ctx.State, config.Retention, config.GlobalStorageType, DateTime.UtcNow, ctx.Cancellation).ConfigureAwait(false);
            var groups = report.Groups.Where(g => storage == null || g.StorageType.Equals(storage, StringComparison.OrdinalIgnoreCase)).ToList();

            // Without a filter the report's own totals (shared chunks counted once); with one, the sums of the selected groups.
            var totals = storage == null
                ? new
                {
                    storedBytes = report.TotalStoredBytes, plainBytes = report.TotalPlainBytes, latestLogicalBytes = report.TotalLatestLogicalBytes,
                    allSnapshotsLogicalBytes = report.TotalAllSnapshotsLogicalBytes, dedupSavedBytes = report.TotalDedupSavedBytes,
                    compressionSavedBytes = report.TotalCompressionSavedBytes, reclaimableBytes = report.TotalReclaimableBytes,
                    reclaimableSnapshots = report.TotalReclaimableSnapshots, snapshots = report.TotalSnapshots, hasEstimates = report.HasEstimates
                }
                : new
                {
                    storedBytes = groups.Sum(g => g.StoredBytes), plainBytes = groups.Sum(g => g.PlainBytes), latestLogicalBytes = groups.Sum(g => g.LatestLogicalBytes),
                    allSnapshotsLogicalBytes = groups.Sum(g => g.AllSnapshotsLogicalBytes), dedupSavedBytes = groups.Sum(g => g.DedupSavedBytes),
                    compressionSavedBytes = groups.Sum(g => g.CompressionSavedBytes), reclaimableBytes = groups.Sum(g => g.ReclaimableBytes),
                    reclaimableSnapshots = groups.Sum(g => g.ReclaimableSnapshots), snapshots = groups.Sum(g => g.SnapshotCount), hasEstimates = groups.Any(g => g.HasEstimates)
                };

            var measured = new List<(string Storage, MeasuredStorage? Result, string? Error)>();
            if (ctx.Parsed.HasOption("--refresh"))
            {
                var storageTypes = groups.Select(g => g.StorageType).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var storageType in storageTypes)
                {
                    ctx.Cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        var lease = await RepositoryCoordinator.Instance.AcquireReadAsync("Measuring storage usage", ctx.Cancellation).ConfigureAwait(false);
                        await using var leaseScope = lease.ConfigureAwait(false);
                        using var storageInstance = await config.CreateStorageAsync(storageType).ConfigureAwait(false);
                        measured.Add((storageType, await StorageUsageCalculator.MeasureAsync(storageInstance, ctx.Cancellation).ConfigureAwait(false), null));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && FindCause<RepositoryBusyException>(ex) == null)
                    {
                        measured.Add((storageType, null, DiagnosticsReport.RedactText(ex.Message)));
                    }
                }
            }

            var measureFailed = measured.Any(m => m.Result == null);
            var exitCode = measureFailed ? ExitCodes.Failure : ExitCodes.Success;
            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new
                {
                    estimated = true,
                    retentionEnabled = report.RetentionEnabled,
                    storage,
                    totals,
                    snapshotsWithoutChunkList = report.SnapshotsWithoutChunkList,
                    groups = groups.Select(g => new
                    {
                        group = g.Group,
                        storage = g.StorageType,
                        snapshots = g.SnapshotCount,
                        latestLogicalBytes = g.LatestLogicalBytes,
                        allSnapshotsLogicalBytes = g.AllSnapshotsLogicalBytes,
                        storedBytes = g.StoredBytes,
                        plainBytes = g.PlainBytes,
                        dedupSavedBytes = g.DedupSavedBytes,
                        compressionSavedBytes = g.CompressionSavedBytes,
                        reclaimableBytes = g.ReclaimableBytes,
                        reclaimableSnapshots = g.ReclaimableSnapshots,
                        hasEstimates = g.HasEstimates
                    }),
                    measured = measured.Select(m => new
                    {
                        storage = m.Storage,
                        chunkCount = m.Result?.ChunkCount,
                        bytes = m.Result?.Bytes,
                        error = m.Error
                    })
                }, exitCode).ConfigureAwait(false);
                return exitCode;
            }

            if (totals.snapshots == 0)
            {
                Console.WriteLine("No backups yet.");
                return ExitCodes.Success;
            }

            var approx = totals.hasEstimates ? "~" : string.Empty;
            Console.WriteLine($"Stored in storage:   {approx}{ByteFormatter.Format(totals.storedBytes)}");
            Console.WriteLine($"Latest versions:     {ByteFormatter.Format(totals.latestLogicalBytes)}");
            Console.WriteLine($"Saved by dedup:      {ByteFormatter.Format(totals.dedupSavedBytes)}");
            Console.WriteLine($"Saved by compression:{ByteFormatter.Format(totals.compressionSavedBytes),11}");
            Console.WriteLine($"Snapshots:           {totals.snapshots}");
            Console.WriteLine(totals.reclaimableSnapshots == 0
                ? "Retention would not delete anything right now."
                : $"Retention would delete {totals.reclaimableSnapshots} snapshot(s) and free about {ByteFormatter.Format(totals.reclaimableBytes)}"
                    + (report.RetentionEnabled ? "." : " (retention is currently switched off)."));
            foreach (var g in groups)
            {
                Console.WriteLine($"  {g.Group} [{g.StorageType}]: {g.SnapshotCount} snapshot(s), stored {ByteFormatter.Format(g.StoredBytes)}, latest {ByteFormatter.Format(g.LatestLogicalBytes)}");
            }

            Console.WriteLine("Sizes are worked out from local history, so they are close but not exact; --refresh measures the storage.");
            foreach (var (measuredStorage, result, error) in measured)
            {
                Console.WriteLine(result is { } r
                    ? $"Measured {measuredStorage}: {ByteFormatter.Format(r.Bytes)} in {r.ChunkCount:N0} chunk(s)"
                    : $"Could not measure {measuredStorage}: {error}");
            }

            return exitCode;
        }

        // ---- upgrade-format -----------------------------------------------------------------------------------------

        private static async Task<int> RunUpgradeFormatAsync(CliContext ctx)
        {
            var config = ctx.Config;
            var logger = ctx.Logger;
            string? group = null;
            if (ctx.Parsed.Positionals.Count == 1)
            {
                var wanted = ctx.Parsed.Positionals[0];
                group = ctx.State.GetBackupGroups().FirstOrDefault(g =>
                    g.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                    || SnapshotStoragePaths.GetHeadPath(g).Split('/')[1].Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    ?? throw FailureError($"No backup group matches '{wanted}' (see 'restore snapshots').");
            }

            var changed = config.ChunkDiffing.ManifestVersion < SnapshotManifest.VersionV3;
            if (changed)
            {
                config.ChunkDiffing.ManifestVersion = SnapshotManifest.VersionV3;
                await config.SaveAsync().ConfigureAwait(false);
                logger.Log("Backup format upgraded: chunkDiffing.manifestVersion set to 3.", LogLevel.Info);
            }

            var encrypted = config.Encryption.Enabled;
            const string Explanation = "From the next backup on, backup sets are written in the newest format (v3): it keeps file dates, attributes and empty folders and makes snapshot lists smaller. "
                + "Older snapshots stay readable. Versions of ReStore older than this one cannot read v3 snapshots. The setting applies to every backup set.";
            var uploadNote = encrypted
                ? "Encrypted backups also hide file names, sizes and dates from the storage provider and allow changing the password without uploading anything; because their chunk ids change, the next backup uploads your data once more."
                : "No data is uploaded again.";

            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new
                {
                    changed,
                    alreadyUpgraded = !changed,
                    manifestVersion = config.ChunkDiffing.ManifestVersion,
                    group,
                    encrypted,
                    reuploadsData = encrypted,
                    explanation = $"{Explanation} {uploadNote}"
                }).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            Console.WriteLine(changed ? "Backup format upgraded." : "Backups are already set to write the newest format; nothing changed.");
            Console.WriteLine(Explanation);
            Console.WriteLine(uploadNote);
            if (changed)
            {
                Console.WriteLine("Restart the ReStore app (if it is running) so it uses the new setting.");
            }

            return ExitCodes.Success;
        }

        // ---- schedule -----------------------------------------------------------------------------------------------

        private static string? ValidateSchedule(ParsedCommand parsed) =>
            parsed.GetOption("--every") is { } every
            && !(int.TryParse(every, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes is >= 1 and <= ScheduledTaskManager.MaxIntervalMinutes)
                ? $"Invalid --every value '{every}'. Expected a whole number of minutes from 1 to {ScheduledTaskManager.MaxIntervalMinutes}."
                : null;

        private static void RequireWindows()
        {
            if (!OpsEnvironment.IsWindows())
            {
                throw FailureError("The scheduled task uses Windows Task Scheduler and is only available on Windows. On other systems schedule 'restore run-due --quiet' with cron or systemd.");
            }
        }

        private static async Task<int> RunScheduleInstallAsync(CliContext ctx)
        {
            RequireWindows();
            var minutes = ctx.Parsed.GetOption("--every") is { } every ? int.Parse(every, CultureInfo.InvariantCulture) : ScheduledTaskManager.DefaultIntervalMinutes;
            var executable = OpsEnvironment.TaskExecutable()
                ?? throw FailureError("Cannot tell which restore.exe the task should run (this process was started through the 'dotnet' host). Run the installed or portable restore.exe.");
            var warning = OpsCommandHelpers.PasswordWarning(ctx.Config, OpsEnvironment.GetEnvironmentVariable, RememberedPassword.FilePath);

            var manager = new ScheduledTaskManager(OpsEnvironment.Runner, conhostAvailable: OpsEnvironment.ConhostAvailable);
            var hidden = manager.UsesConhost;
            var error = await manager.InstallAsync(executable, minutes, ctx.Cancellation).ConfigureAwait(false);
            if (error != null)
            {
                throw FailureError($"Could not create the scheduled task: {error}");
            }

            if (warning != null)
            {
                ctx.Logger.Log(warning, LogLevel.Warning);
            }

            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new
                {
                    installed = true,
                    taskName = ScheduledTaskManager.TaskName,
                    intervalMinutes = minutes,
                    executable,
                    arguments = ScheduledTaskManager.TaskArguments,
                    hidden,
                    launcher = hidden ? ScheduledTaskManager.ConhostCommand : null,
                    warnings = warning == null ? Array.Empty<string>() : [warning]
                }).ConfigureAwait(false);
            }
            else
            {
                Console.WriteLine($"Scheduled task {ScheduledTaskManager.TaskName} installed: runs '{executable} {ScheduledTaskManager.TaskArguments}'{(hidden ? " without a window (through conhost --headless)" : string.Empty)} every {minutes} minute(s) while you are logged on. 'restore schedule status' shows how the last run went.");
            }

            return ExitCodes.Success;
        }

        private static async Task<int> RunScheduleRemoveAsync(CliContext ctx)
        {
            RequireWindows();
            var (removed, error) = await new ScheduledTaskManager(OpsEnvironment.Runner, conhostAvailable: OpsEnvironment.ConhostAvailable).RemoveAsync(ctx.Cancellation).ConfigureAwait(false);
            if (error != null)
            {
                throw FailureError($"Could not delete the scheduled task: {error}");
            }

            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new { installed = false, removed, taskName = ScheduledTaskManager.TaskName }).ConfigureAwait(false);
            }
            else
            {
                Console.WriteLine(removed ? $"Scheduled task {ScheduledTaskManager.TaskName} removed." : $"Scheduled task {ScheduledTaskManager.TaskName} was not installed.");
            }

            return ExitCodes.Success;
        }

        private static async Task<int> RunScheduleStatusAsync(CliContext ctx)
        {
            RequireWindows();
            var status = await new ScheduledTaskManager(OpsEnvironment.Runner, conhostAvailable: OpsEnvironment.ConhostAvailable).QueryAsync(ctx.Cancellation).ConfigureAwait(false);
            var last = RunDueRecord.TryLoad();
            var warnings = new List<string>();
            if (status.Installed && OpsCommandHelpers.PasswordWarning(ctx.Config, OpsEnvironment.GetEnvironmentVariable, RememberedPassword.FilePath) is { } warning)
            {
                warnings.Add(warning);
            }

            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new
                {
                    installed = status.Installed,
                    taskName = ScheduledTaskManager.TaskName,
                    enabled = status.Enabled,
                    intervalMinutes = status.IntervalMinutes,
                    executable = status.Executable,
                    arguments = status.Arguments,
                    state = status.State,
                    lastRun = status.LastRun,
                    lastRunUtc = status.LastRunUtc,
                    // Task Scheduler's own result is always 0 for a task started through conhost, so run-due's own record wins.
                    lastResult = last?.ExitCode ?? status.LastResult,
                    lastResultText = last != null ? last.Describe() : status.LastResultText,
                    lastResultSource = last != null ? "run-due" : "task-scheduler",
                    lastRunResult = last == null ? null : new
                    {
                        startedUtc = last.StartedUtc,
                        finishedUtc = last.FinishedUtc,
                        exitCode = last.ExitCode,
                        ok = last.ExitCode == 0,
                        errorCode = last.ErrorCode,
                        summary = last.Describe(),
                        backups = new { succeeded = last.BackupsSucceeded, failed = last.BackupsFailed },
                        systemBackup = new { ran = last.SystemBackupRan, failed = last.SystemBackupFailed },
                        verification = new { succeeded = last.VerificationsSucceeded, failed = last.VerificationsFailed },
                        retention = new { ran = last.RetentionRan, failed = last.RetentionFailed },
                        deferred = last.Deferred,
                        paused = last.Paused,
                        nothingDue = last.NothingDue
                    },
                    nextRun = status.NextRun,
                    warnings
                }).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            if (!status.Installed)
            {
                Console.WriteLine($"Scheduled task {ScheduledTaskManager.TaskName} is not installed. Create it with 'restore schedule install'.");
                return ExitCodes.Success;
            }

            Console.WriteLine($"Scheduled task {ScheduledTaskManager.TaskName} is installed{(status.Enabled == false ? " but disabled" : string.Empty)}.");
            Console.WriteLine($"  Runs: {status.Executable} {status.Arguments}".TrimEnd() + (status.IntervalMinutes is { } every ? $" every {every} minute(s)" : string.Empty));
            Console.WriteLine($"  State: {status.State ?? "unknown"}");
            Console.WriteLine(last != null
                ? $"  Last run: {last.StartedUtc.ToLocalTime():g} - {last.Describe()}"
                : $"  Last run: {status.LastRun ?? "unknown"} ({status.LastResultText ?? "no result"}; 'restore run-due' has not recorded a result yet)");
            Console.WriteLine($"  Next run: {status.NextRun ?? "unknown"}");
            foreach (var text in warnings)
            {
                ctx.Logger.Log(text, LogLevel.Warning);
            }

            return ExitCodes.Success;
        }

        // ---- diagnostics / logs -------------------------------------------------------------------------------------

        private static async Task<int> RunDiagnosticsAsync(CliContext ctx)
        {
            string? configJson = null;
            try
            {
                configJson = await File.ReadAllTextAsync(AppPaths.ConfigFile, ctx.Cancellation).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Reported as "could not be read" in the report.
            }

            var logLines = DiagnosticsReport.ReadLogTail(AppPaths.LogFile, DiagnosticsReport.DefaultLogLines);
            var report = DiagnosticsReport.Build(CommandRegistry.FullVersion, configJson, logLines);
            if (!ctx.Json)
            {
                await ctx.JsonOut.WriteLineAsync(report).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            JsonNode? config = null;
            string? configProblem = configJson == null ? "config.json could not be read" : null;
            if (configJson != null)
            {
                try
                {
                    // Provider secrets are masked by key; free-text secrets inside any string value by pattern (not on the whole
                    // document: the text patterns would also hit keys such as "verificationToken" and break the JSON).
                    config = RedactStrings(JsonNode.Parse(ConfigSecretsCommands.RedactConfigJson(configJson)));
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    configProblem = "config.json is not valid JSON; not included";
                }
            }

            await ctx.WriteJsonAsync(new
            {
                generatedUtc = DateTime.UtcNow,
                version = CommandRegistry.Version,
                informationalVersion = CommandRegistry.FullVersion,
                channel = AppChannel.Current,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                architecture = AppChannel.Architecture,
                config,
                configProblem,
                logLines = logLines.Select(DiagnosticsReport.RedactText),
                report
            }).ConfigureAwait(false);
            return ExitCodes.Success;
        }

        private static JsonNode? RedactStrings(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var property in obj.ToList())
                    {
                        obj[property.Key] = RedactStrings(property.Value);
                    }

                    return obj;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        array[i] = RedactStrings(array[i]);
                    }

                    return array;
                case JsonValue value when value.TryGetValue<string>(out var text):
                    return JsonValue.Create(DiagnosticsReport.RedactText(text));
                default:
                    return node;
            }
        }

        private static async Task<int> RunLogsAsync(CliContext ctx)
        {
            var path = AppPaths.LogFile;
            var exists = File.Exists(path);
            var pathOnly = ctx.Parsed.HasOption("--path");
            var tail = ctx.Parsed.GetOption("--tail") is { } text ? int.Parse(text, CultureInfo.InvariantCulture) : 50;
            var lines = pathOnly ? [] : DiagnosticsReport.ReadLogTail(path, tail).Select(DiagnosticsReport.RedactText).ToList();

            if (ctx.Json)
            {
                long? size = null;
                if (exists)
                {
                    try { size = new FileInfo(path).Length; } catch (IOException) { /* unreadable size */ }
                }

                await ctx.WriteJsonAsync(new { path, exists, sizeBytes = size, lines = pathOnly ? null : lines }).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            if (pathOnly)
            {
                await ctx.JsonOut.WriteLineAsync(path).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            Console.Error.WriteLine(exists ? $"Log file: {path}" : $"No log file yet (expected at {path}).");
            foreach (var line in lines)
            {
                await ctx.JsonOut.WriteLineAsync(line).ConfigureAwait(false);
            }

            return ExitCodes.Success;
        }

        // ---- share --------------------------------------------------------------------------------------------------

        private static async Task<int> RunShareAsync(CliContext ctx)
        {
            var config = ctx.Config;
            var file = Path.GetFullPath(Environment.ExpandEnvironmentVariables(ctx.Parsed.Positionals[0]));
            if (!File.Exists(file))
            {
                throw FailureError($"File not found: {file}");
            }

            var expires = ctx.Parsed.GetOption("--expires") is { } expiresText && CliDurations.TryParse(expiresText, out var parsedExpiry)
                ? parsedExpiry
                : OpsCommandHelpers.DefaultShareExpiry;

            var supported = config.StorageSources.Keys.Where(k => OpsCommandHelpers.SharingStorageTypes.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
            string storageType;
            if (ctx.StorageOverride is { } requested)
            {
                storageType = ResolveConfiguredStorage(config, requested);
                if (!supported.Contains(storageType, StringComparer.OrdinalIgnoreCase))
                {
                    throw UsageError($"Storage '{storageType}' cannot create share links. Sharing works with: {string.Join(", ", OpsCommandHelpers.SharingStorageTypes)}.");
                }
            }
            else
            {
                storageType = supported.FirstOrDefault(k => k.Equals(config.GlobalStorageType, StringComparison.OrdinalIgnoreCase))
                    ?? supported.FirstOrDefault()
                    ?? throw FailureError($"No configured storage supports share links. Sharing works with: {string.Join(", ", OpsCommandHelpers.SharingStorageTypes)}.");
            }

            string link;
            try
            {
                link = await OpsEnvironment.CreateShareService(config, ctx.Logger).ShareFileAsync(file, storageType, expires).ConfigureAwait(false);
            }
            catch (NotSupportedException ex)
            {
                throw FailureError(ex.Message);
            }

            var neverExpires = storageType.Equals("dropbox", StringComparison.OrdinalIgnoreCase);
            var note = neverExpires ? "Dropbox shared links do not expire automatically." : $"Link expires in {CliDurations.Format(expires)}.";
            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new
                {
                    file,
                    storage = storageType,
                    link,
                    expires = neverExpires ? null : CliDurations.Format(expires),
                    expiresUtc = neverExpires ? (DateTime?)null : DateTime.UtcNow + expires,
                    note
                }).ConfigureAwait(false);
            }
            else
            {
                await ctx.JsonOut.WriteLineAsync(link).ConfigureAwait(false);
                Console.Error.WriteLine(note);
            }

            return ExitCodes.Success;
        }
    }
}
