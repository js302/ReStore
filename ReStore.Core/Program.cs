using ReStore.Core.src.cli;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.backup;
using ReStore.Core.src.storage;
using ReStore.Core.src.updates;

namespace ReStore.Core
{
    /// <summary>
    /// The <c>restore</c> command line. Commands are described by <see cref="CommandSpec"/>s in <see cref="CommandRegistry"/>
    /// (built-ins in <c>src/cli/BuiltInCommands.cs</c>, which is part of this class so its handlers stay private);
    /// parsing, help, <c>commands --json</c> and dispatch are all driven by those specs.
    /// </summary>
    public partial class Program
    {
        /// <summary>Stable process exit codes, documented in docs/usage/cli.md.</summary>
        internal static class ExitCodes
        {
            public const int Success = 0;
            public const int Failure = 1;
            public const int Usage = 2;
            public const int Config = 3;

            /// <summary>Only a person can continue: no password without a prompt, interactive sign-in, elevation or --yes needed.</summary>
            public const int NeedsUser = 4;

            /// <summary>Another ReStore process holds the repository (see <c>--wait</c>).</summary>
            public const int Busy = 5;
            public const int Cancelled = 130;
        }

        internal sealed record ParsedCommand(string Command, List<string> Positionals, Dictionary<string, List<string>> Options)
        {
            public string? GetOption(string name) => Options.TryGetValue(name, out var values) ? values[^1] : null;

            public bool HasOption(string name) => Options.ContainsKey(name);

            public bool Json => HasOption(CliGlobals.JsonFlag);

            public bool Quiet => HasOption(CliGlobals.QuietFlag);

            /// <summary>Global <c>--yes</c>: destructive actions are confirmed in advance.</summary>
            public bool Yes => HasOption(CliGlobals.YesFlag);

            /// <summary>Global <c>--non-interactive</c> as typed; see <c>CliRuntime.NonInteractive</c> for the effective value.</summary>
            public bool NonInteractive => HasOption(CliGlobals.NonInteractiveFlag);

            public bool PasswordStdin => HasOption(CliGlobals.PasswordStdinFlag);

            /// <summary>Global <c>--wait &lt;seconds&gt;</c>, or null when not given.</summary>
            public int? WaitSeconds => GetOption(CliGlobals.WaitFlag) is { } text
                && int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
        }

        private static readonly string[] SYSTEM_COMPONENT_TYPES = ["programs", "environment", "settings", "devtools", "all"];

        /// <summary>
        /// Strict argument parsing. Unknown commands/flags, missing flag values, wrong positional counts and
        /// invalid component or --conflict values are usage errors: silently ignoring a mistyped flag such as
        /// --dryrun would otherwise perform the real operation. <c>--help</c>/<c>-h</c> parse to the <c>help</c> command.
        /// </summary>
        internal static bool TryParseArguments(string[] args, out ParsedCommand? parsed, out string? error) =>
            TryParse(args, out parsed, out error, out _, out _, out _);

        private static bool TryParse(string[] args, out ParsedCommand? parsed, out string? error, out CliGlobals globals, out CommandSpec? spec, out string? group)
        {
            parsed = null;
            spec = null;
            group = null;
            globals = CliGlobals.Extract(args, out var rest, out error);
            if (error != null)
            {
                return false;
            }

            var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            AddGlobalOptions(options, globals);

            if (rest.Length == 0)
            {
                if (globals.Help)
                {
                    parsed = new ParsedCommand("help", [], options);
                    return true;
                }

                error = "Missing command.";
                return false;
            }

            var head = rest[0];
            var tokens = rest.Skip(1).ToList();
            spec = CommandRegistry.Find(head);
            if (spec == null && CommandRegistry.IsGroup(head))
            {
                group = head;

                // The subcommand is the first word that is not an option, so "config --reveal show" still works.
                var subIndex = tokens.FindIndex(t => !t.StartsWith("--", StringComparison.Ordinal));
                if (subIndex < 0)
                {
                    if (globals.Help)
                    {
                        parsed = new ParsedCommand("help", [head], options);
                        return true;
                    }

                    error = $"Missing subcommand for '{head}'. Expected one of: {string.Join(", ", CommandRegistry.SubcommandWords(head))}.";
                    return false;
                }

                var word = tokens[subIndex];
                spec = CommandRegistry.Find($"{head} {word}");
                if (spec == null)
                {
                    var hint = SuggestClosest(word, CommandRegistry.SubcommandWords(head));
                    error = $"Unknown subcommand '{word}' for '{head}'." + (hint != null ? $" Did you mean '{head} {hint}'?" : string.Empty)
                        + $" Expected one of: {string.Join(", ", CommandRegistry.SubcommandWords(head))}.";
                    return false;
                }

                tokens.RemoveAt(subIndex);
            }
            else if (spec == null)
            {
                var hint = SuggestClosest(head, CommandRegistry.TopLevelWords());
                error = $"Unknown command '{head}'." + (hint != null ? $" Did you mean '{hint}'?" : string.Empty);
                return false;
            }

            if (globals.Help)
            {
                parsed = new ParsedCommand("help", [.. spec.Name.Split(' ')], options);
                return true;
            }

            if (globals.Json && !spec.SupportsJson)
            {
                error = $"'{spec.Name}' does not support --json.";
                return false;
            }

            var positionals = new List<string>();
            var allOptionNames = spec.Options.Select(o => o.Name).Concat(CliGlobals.Documentation.Select(o => o.Name)).ToList();
            for (var index = 0; index < tokens.Count; index++)
            {
                var token = tokens[index];

                // "help --service" / "help --version": the command names that start with dashes are arguments of help.
                if (!token.StartsWith("--", StringComparison.Ordinal) || (spec.Name == "help" && CommandRegistry.Find(token) != null))
                {
                    positionals.Add(token);
                    continue;
                }

                var option = spec.Options.FirstOrDefault(o => o.Name.Equals(token, StringComparison.OrdinalIgnoreCase));
                if (option == null)
                {
                    var suggestion = SuggestClosest(token, allOptionNames);
                    error = $"Unknown option '{token}' for '{spec.Name}'." + (suggestion != null ? $" Did you mean '{suggestion}'?" : string.Empty);
                    return false;
                }

                if (!option.TakesValue)
                {
                    options[option.Name] = [];
                    continue;
                }

                if (index + 1 >= tokens.Count || tokens[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    error = $"Option '{token}' requires a value.";
                    return false;
                }

                if (!options.TryGetValue(option.Name, out var values))
                {
                    options[option.Name] = values = [];
                }

                values.Add(tokens[++index]);
            }

            if (positionals.Count < spec.MinPositionals)
            {
                error = $"Missing argument(s) for '{spec.Name}'.";
                return false;
            }

            if (positionals.Count > spec.MaxPositionals)
            {
                error = $"Unexpected argument '{positionals[spec.MaxPositionals]}' for '{spec.Name}'. Quote paths that contain spaces.";
                return false;
            }

            var candidate = new ParsedCommand(spec.Name, positionals, options);
            if (spec.Validate?.Invoke(candidate) is { } validationError)
            {
                error = validationError;
                return false;
            }

            parsed = candidate;
            return true;
        }

        private static void AddGlobalOptions(Dictionary<string, List<string>> options, CliGlobals globals)
        {
            if (globals.Json)
            {
                options[CliGlobals.JsonFlag] = [];
            }

            if (globals.Quiet)
            {
                options[CliGlobals.QuietFlag] = [];
            }

            if (globals.NonInteractive)
            {
                options[CliGlobals.NonInteractiveFlag] = [];
            }

            if (globals.Yes)
            {
                options[CliGlobals.YesFlag] = [];
            }

            if (globals.PasswordStdin)
            {
                options[CliGlobals.PasswordStdinFlag] = [];
            }

            if (globals.WaitSeconds is { } wait)
            {
                options[CliGlobals.WaitFlag] = [wait.ToString(System.Globalization.CultureInfo.InvariantCulture)];
            }
        }

        private static bool TryGetPositiveInt(Dictionary<string, List<string>> options, string name, out int value, out string? error)
        {
            value = 0;
            error = null;
            if (!options.TryGetValue(name, out var values))
            {
                return true;
            }

            if (int.TryParse(values[^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value) && value >= 1)
            {
                return true;
            }

            error = $"Invalid {name} value '{values[^1]}'. Expected a whole number of 1 or more.";
            return false;
        }

        private static bool TryGetNonNegativeInt(Dictionary<string, List<string>> options, string name, out int value, out string? error)
        {
            value = 0;
            error = null;
            if (!options.TryGetValue(name, out var values))
            {
                return true;
            }

            if (int.TryParse(values[^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            error = $"Invalid {name} value '{values[^1]}'. Expected a whole number of 0 or more.";
            return false;
        }

        private static bool TryValidateVerifyOptions(Dictionary<string, List<string>> options, out string? error)
        {
            error = null;
            var mode = VerificationMode.Full;
            if (options.TryGetValue("--mode", out var modeValues) && !VerificationModes.TryParse(modeValues[^1], out mode))
            {
                error = $"Invalid --mode value '{modeValues[^1]}'. Expected one of: full, sample, quick.";
                return false;
            }

            if (!TryGetPositiveInt(options, "--sample-percent", out var percent, out error))
            {
                return false;
            }

            if (options.ContainsKey("--sample-percent") && percent > 100)
            {
                error = $"Invalid --sample-percent value '{percent}'. Expected a whole number from 1 to 100.";
                return false;
            }

            if (!TryGetNonNegativeInt(options, "--seed", out _, out error))
            {
                return false;
            }

            if (mode != VerificationMode.Sample && (options.ContainsKey("--sample-percent") || options.ContainsKey("--seed")))
            {
                error = "--sample-percent and --seed only apply with --mode sample.";
                return false;
            }

            return true;
        }


        private static string? SuggestClosest(string input, IEnumerable<string> candidates)
        {
            static string Normalize(string value) => value.TrimStart('-').Replace("-", string.Empty).ToLowerInvariant();

            var normalizedInput = Normalize(input);
            string? best = null;
            var bestDistance = int.MaxValue;
            foreach (var candidate in candidates)
            {
                var distance = EditDistance(normalizedInput, Normalize(candidate));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            return bestDistance <= 2 ? best : null;
        }

        private static int EditDistance(string a, string b)
        {
            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++)
            {
                previous[j] = j;
            }

            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }

                (previous, current) = (current, previous);
            }

            return previous[b.Length];
        }

        public static async Task<int> Main(string[] args)
        {
            ConsoleStreams.UseUtf8ForRedirectedStreams();
            var jsonOut = Console.Out;
            UpdateInstaller.CleanupLeftovers();
            if (!TryParse(args, out var parsed, out var usageError, out var globals, out var failedSpec, out var failedGroup) || parsed == null)
            {
                return await ReportUsageErrorAsync(usageError ?? "Invalid arguments.", globals, failedSpec, failedGroup, jsonOut).ConfigureAwait(false);
            }

            var spec = CommandRegistry.Find(parsed.Command)!;
            var context = new CliContext(parsed, spec, jsonOut);

            CliRuntime.Reset();
            CliRuntime.NonInteractive = CliGlobals.ResolveNonInteractive(globals.NonInteractive, Environment.GetEnvironmentVariable, IsInputRedirected());
            CliRuntime.PasswordStdin = globals.PasswordStdin;
            CliRuntime.Yes = globals.Yes;
            Logger.ErrorObserver = context.RecordLoggedError;

            // --wait applies to the process-wide coordinator every command uses; one line on stderr says what it waits for.
            RepositoryCoordinator.Instance.ConfigureWait(
                globals.WaitSeconds is { } wait ? TimeSpan.FromSeconds(wait) : RepositoryCoordinator.DefaultCrossProcessTimeout,
                message => Console.Error.WriteLine($"Waiting for the repository: {message}"));

            UnattendedMode.Force(spec.ForceUnattended || CliRuntime.NonInteractive);
            var commandTimer = System.Diagnostics.Stopwatch.StartNew();
            TelemetryCommands.BeforeRun(context);

            try
            {
                // Keep stdout machine-readable for --json: log lines go to stderr, the document to the real stdout.
                // Commands that print their own payload to stdout (config show) also keep the log lines off it.
                if (parsed.Json || spec.PayloadOnStdout)
                {
                    Console.SetOut(Console.Error);
                }

                // help, version and commands are the answer the caller asked for: --quiet does not silence them.
                if (parsed.Quiet && spec.Category != CommandCategory.Info)
                {
                    // Only errors, on stderr. jsonOut still points at the real stdout, so --json documents are unaffected.
                    Logger.QuietConsole = true;
                    Console.SetOut(TextWriter.Null);
                }

                int exitCode;

                // Config problems of any kind (load, migration, validation) are exit 3, never an unhandled exception.
                try
                {
                    exitCode = await RunAsync(context).ConfigureAwait(false);
                }
                catch (ConfigurationFailureException ex)
                {
                    context.Logger.Log(ex.Message, LogLevel.Error);
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    exitCode = ExitCodes.Config;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Last resort: whatever escaped the pipeline is exit 1 with the JSON error document, never a runtime crash dump.
                    exitCode = MapException(ex, context);
                }

                await FinishFailureAsync(context, exitCode).ConfigureAwait(false);
                RecordRunDue(context, exitCode);
                await TelemetryCommands.AfterRunAsync(context, exitCode, commandTimer.Elapsed).ConfigureAwait(false);
                await UpdateCommands.AfterCommandAsync(context).ConfigureAwait(false);
                return exitCode;
            }
            finally
            {
                // Process-wide switches set for this run; an in-process caller (tests) must not inherit them.
                Logger.QuietConsole = false;
                Logger.ErrorObserver = null;
                Console.SetOut(jsonOut);
                RepositoryCoordinator.Instance.ConfigureWait(RepositoryCoordinator.DefaultCrossProcessTimeout);
                UnattendedMode.Force(false);
                CliRuntime.Reset();
            }
        }

        /// <summary>
        /// <c>run-due</c> leaves its own outcome in <c>state/run-due-last.json</c>: the scheduled task runs it through
        /// <c>conhost.exe --headless</c>, which hides the exit code from Task Scheduler. Written after the final state save, so
        /// the exit code is the real one (config problems, a busy repository and unexpected errors included).
        /// </summary>
        private static void RecordRunDue(CliContext context, int exitCode)
        {
            if (context.Spec.Name != "run-due")
            {
                return;
            }

            (context.RunDueOutcome ?? new RunDueRecord()).TrySaveWith(context.StartedUtc, DateTime.UtcNow, exitCode, exitCode == ExitCodes.Success ? null : context.BuildError(exitCode).Code);
        }

        /// <summary>Test seam: overrides whether stdin counts as redirected (null = ask the console).</summary>
        internal static bool? InputRedirectedOverride { get; set; }

        private static bool IsInputRedirected()
        {
            if (InputRedirectedOverride is { } forced)
            {
                return forced;
            }

            try
            {
                return Console.IsInputRedirected;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                return true;
            }
        }

        private static async Task<int> ReportUsageErrorAsync(string error, CliGlobals globals, CommandSpec? spec, string? group, TextWriter jsonOut)
        {
            Console.Error.WriteLine($"Error: {error}");
            if (globals.Json)
            {
                var hint = spec != null
                    ? $"run '{CommandRegistry.ProgramName} help {spec.Name}' for the usage"
                    : $"run '{CommandRegistry.ProgramName} help' or '{CommandRegistry.ProgramName} commands --json' for the commands";
                await jsonOut.WriteLineAsync(CliJson.Build(new { }, ExitCodes.Usage, new CliError(CliErrorCodes.Usage, error, hint))).ConfigureAwait(false);
            }
            else
            {
                await Console.Out.WriteLineAsync(
                    spec != null ? CommandRegistry.RenderUsageError(spec)
                    : group != null ? CommandRegistry.RenderGroupHelp(group)
                    : CommandRegistry.RenderTopLevelUsage()).ConfigureAwait(false);
            }

            return ExitCodes.Usage;
        }

        /// <summary>
        /// A failed command prints its hint (busy, needs a person) on stderr and, with <c>--json</c>, gets the uniform error
        /// document unless its handler already wrote a document of its own.
        /// </summary>
        private static async Task FinishFailureAsync(CliContext context, int exitCode)
        {
            if (exitCode == ExitCodes.Success)
            {
                return;
            }

            var error = context.BuildError(exitCode);
            if (!context.Json && exitCode is ExitCodes.NeedsUser or ExitCodes.Busy && error.Hint != null)
            {
                Console.Error.WriteLine($"Hint: {error.Hint}");
            }

            if (context.Json && !context.JsonWritten)
            {
                await context.WriteJsonAsync(new { }, exitCode).ConfigureAwait(false);
            }
        }

        private sealed class ConfigurationFailureException(string message) : Exception(message);

        private static T? FindCause<T>(Exception? exception) where T : Exception
        {
            for (var depth = 0; exception != null && depth < 10; depth++, exception = exception.InnerException)
            {
                if (exception is T match)
                {
                    return match;
                }

                if (exception is AggregateException aggregate && aggregate.InnerExceptions.Select(FindCause<T>).FirstOrDefault(m => m != null) is { } inner)
                {
                    return inner;
                }
            }

            return null;
        }

        /// <summary>Turns an exception from a handler into an exit code, logging the message and remembering the JSON error.</summary>
        private static int MapException(Exception ex, CliContext context)
        {
            var logger = context.Logger;
            if (ex is OperationCanceledException)
            {
                // Ctrl+C during a command, or shutdown in service mode. A cancelled backup leaves HEAD
                // untouched and a cancelled restore leaves no partial file behind.
                logger.Log("Operation cancelled.", LogLevel.Info);
                return context.Spec.Name == "--service" ? ExitCodes.Success : ExitCodes.Cancelled;
            }

            if (FindCause<CliCommandException>(ex) is { } cli)
            {
                logger.Log(cli.Message, LogLevel.Error);
                context.Failure = new CliError(cli.Code, cli.Message, cli.Hint);
                return cli.ExitCode;
            }

            if (FindCause<RepositoryBusyException>(ex) is { } busy)
            {
                logger.Log(busy.Message, LogLevel.Error);
                context.Failure = new CliError(CliErrorCodes.Busy, busy.Message, CliErrorCodes.BusyHint);
                return ExitCodes.Busy;
            }

            if (FindCause<StorageNotFoundException>(ex) is { } missing)
            {
                var notFound = DescribeNotFound(missing, context);
                logger.Log(notFound.Message, LogLevel.Error);
                context.Failure = notFound;
                return ExitCodes.Failure;
            }

            if (FindCause<StorageAuthRequiredException>(ex) is { } auth)
            {
                logger.Log(auth.Message, LogLevel.Error);
                context.Failure = new CliError(
                    CliErrorCodes.NeedsUser, auth.Message,
                    "this storage needs an interactive sign-in: run the command once in a terminal without --non-interactive (or sign in from the ReStore app), then retry");
                return ExitCodes.NeedsUser;
            }

            if (CliRuntime.PasswordUnavailableMessage is { } passwordMessage)
            {
                logger.Log(passwordMessage, LogLevel.Error);
                context.Failure = new CliError(CliErrorCodes.NeedsUser, passwordMessage, CliErrorCodes.PasswordHint);
                return ExitCodes.NeedsUser;
            }

            if (context.Spec.State == StateRequirement.None)
            {
                logger.Log($"'{context.Spec.Name}' failed: {ex.Message}", LogLevel.Error);
                ReStore.Core.src.telemetry.Telemetry.TrackUnhandled(ex);
                TelemetryCommands.RecordCrash(ex);
                return ExitCodes.Failure;
            }

            logger.Log($"An unexpected error occurred: {ex.Message}", LogLevel.Error);
            ReStore.Core.src.telemetry.Telemetry.TrackUnhandled(ex);
            TelemetryCommands.RecordCrash(ex);
            logger.Log($"Stack Trace: {ex.StackTrace}", LogLevel.Debug);
            return ExitCodes.Failure;
        }

        /// <summary>
        /// A missing manifest or HEAD (a snapshot path that does not exist, typically a typo) gets a plain message and the
        /// <c>not_found</c> error code; any other missing object says which storage and path.
        /// </summary>
        internal static CliError DescribeNotFound(StorageNotFoundException missing, CliContext context)
        {
            var path = missing.RemotePath;
            string storage;
            try
            {
                storage = context.StorageOverride ?? context.Config.GlobalStorageType;
            }
            catch (InvalidOperationException)
            {
                storage = "the configured storage";
            }

            var typedPath = context.Parsed.Positionals.FirstOrDefault();
            var isSnapshot = path != null
                && (typedPath != null && string.Equals(path.Replace('\\', '/').Trim('/'), typedPath.Replace('\\', '/').Trim('/'), StringComparison.Ordinal)
                    || path.Replace('\\', '/').EndsWith("/HEAD", StringComparison.Ordinal) || path == "HEAD");
            if (path != null && isSnapshot)
            {
                return new CliError(
                    CliErrorCodes.NotFound,
                    $"No snapshot at '{path}' in storage '{storage}'. List snapshots with: restore snapshots",
                    $"{CommandRegistry.ProgramName} snapshots lists the snapshots and their manifest paths");
            }

            return new CliError(
                CliErrorCodes.NotFound,
                path != null ? $"'{path}' was not found in storage '{storage}'." : $"A file the command needs was not found in storage '{storage}'.",
                "if snapshots or chunks went missing, run 'restore verify <manifest>' and then 'restore repair'");
        }

        /// <summary>The storage a backup of <paramref name="directory"/> uses: the matching watch directory's, else the global one (as <c>Backup</c> decides).</summary>
        private static string GetStorageTypeForDirectory(IConfigManager config, string directory)
        {
            string normalized;
            try
            {
                normalized = Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // An empty or malformed path: the command itself reports it; here the global storage is as good as any.
                return config.GlobalStorageType;
            }

            var watch = config.WatchDirectories.FirstOrDefault(w =>
                !string.IsNullOrWhiteSpace(w.Path) && Path.GetFullPath(w.ExpandedPath).Equals(normalized, StringComparison.OrdinalIgnoreCase));
            return watch?.StorageType ?? config.GlobalStorageType;
        }

        private static void MergeValidation(ConfigValidationResult target, ConfigValidationResult extra)
        {
            foreach (var error in extra.Errors.Where(e => !target.Errors.Contains(e)))
            {
                target.AddError(error);
            }

            foreach (var warning in extra.Warnings.Where(w => !target.Warnings.Contains(w)))
            {
                target.AddWarning(warning);
            }

            foreach (var info in extra.Info.Where(i => !target.Info.Contains(i)))
            {
                target.AddInfo(info);
            }
        }

        /// <summary>Test seam: invoked with the command name at the start of the pipeline (lets tests throw an unexpected exception portably).</summary>
        internal static Action<string>? PipelineTestHook { get; set; }

        private static async Task<int> RunAsync(CliContext context)
        {
            var spec = context.Spec;
            var parsed = context.Parsed;
            PipelineTestHook?.Invoke(spec.Name);

            if (spec.Config == ConfigRequirement.None)
            {
                try
                {
                    return await spec.Handler(context).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    return MapException(ex, context);
                }
            }

            var logger = context.Logger;
            ConfigSetupResult setupResult;
            var configManager = new ConfigManager(logger);
            try
            {
                setupResult = ConfigInitializer.EnsureConfigurationSetup(logger);
                await configManager.LoadAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var reason = ex.InnerException is { } inner ? $"{ex.Message}: {inner.Message}" : ex.Message;
                throw new ConfigurationFailureException($"Could not load the configuration ({configManager.GetConfigFilePath()}): {reason}");
            }

            context.Config = configManager;
            PrintConfigurationLifecycleSummary(setupResult, configManager.LastMigrationResult, logger);

            var storageOverride = context.StorageOverride;

            // Commands that only read the repository need a usable storage, not a fully valid config.
            ConfigValidationResult validationResult;
            try
            {
                validationResult = spec.Config switch
                {
                    ConfigRequirement.Full when spec.ValidateWatchDirectories => configManager.ValidateConfiguration(),
                    ConfigRequirement.Full => new ConfigValidator(logger).ValidateConfiguration(configManager, validateWatchDirectories: false),
                    ConfigRequirement.Storage => new ConfigValidator(logger).ValidateStorageOnly(configManager, storageOverride ?? configManager.GlobalStorageType),
                    ConfigRequirement.StorageOverrideOnly when storageOverride != null => new ConfigValidator(logger).ValidateStorageOnly(configManager, storageOverride),
                    _ => new ConfigValidationResult()
                };
            }
            catch (Exception ex)
            {
                throw new ConfigurationFailureException($"Configuration validation failed unexpectedly: {ex.Message}");
            }

            if (spec.Config == ConfigRequirement.Full && !spec.ValidateWatchDirectories && parsed.Positionals.Count > 0)
            {
                // The folder comes from the command line, so the storage that folder uses must be usable instead.
                MergeValidation(validationResult, new ConfigValidator(logger).ValidateStorageOnly(
                    configManager, storageOverride ?? GetStorageTypeForDirectory(configManager, parsed.Positionals[0])));
            }

            if (!validationResult.IsValid)
            {
                logger.Log("Configuration validation failed. Please fix the errors before proceeding.", LogLevel.Error);
                PrintValidationIssues(validationResult, logger);
                context.Failure = new CliError(
                    CliErrorCodes.Config, "Configuration validation failed. Please fix the errors before proceeding.",
                    "run 'restore.exe config validate' for the full report", [.. validationResult.Errors.Take(20)]);
                return ExitCodes.Config;
            }
            else if (validationResult.HasIssues)
            {
                logger.Log("Configuration validation completed with warnings.", LogLevel.Warning);
                PrintValidationIssues(validationResult, logger);
            }

            if (spec.RequiresServicePassword && GetServicePasswordError(configManager, spec.Name) is { } passwordError)
            {
                // A scheduled run cannot ask: it needs a person to provide the password (exit 4, needs_user), not a generic failure.
                logger.Log(passwordError, LogLevel.Error);
                Console.Error.WriteLine($"Error: {passwordError}");
                context.Failure = new CliError(CliErrorCodes.NeedsUser, passwordError, CliErrorCodes.PasswordHint);
                return ExitCodes.NeedsUser;
            }

            if (spec.State == StateRequirement.None)
            {
                try
                {
                    return await spec.Handler(context).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    return MapException(ex, context);
                }
            }

            var exitCode = ExitCodes.Success;
            var mutatesState = spec.State == StateRequirement.Mutating && spec.IsPreview?.Invoke(parsed) != true;
            var systemState = new SystemState(logger);
            try
            {
                await systemState.LoadStateAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Log($"Could not load the system state, exiting without changes: {ex.Message}", LogLevel.Error);
                return ExitCodes.Failure;
            }

            context.State = systemState;

            // Read-only commands persist state only if they actually changed telemetry.
            var telemetryBefore = TelemetryFingerprint(systemState);

            var cts = new CancellationTokenSource();
            context.Cancellation = cts.Token;
            ConsoleCancelEventHandler onCancel = (sender, e) =>
            {
                logger.Log("Cancellation requested. Shutting down...", LogLevel.Info);
                e.Cancel = true;
                cts.Cancel();
            };
            Console.CancelKeyPress += onCancel;

            // The document waits until the final state save is known: a run whose save is busy or fails is not "ok".
            context.BufferJson = true;
            CliError? saveFailure = null;
            try
            {
                exitCode = await spec.Handler(context).ConfigureAwait(false);

                // A prompt-less run that needed a password may have been reported by the command as a plain failure.
                if (exitCode == ExitCodes.Failure && CliRuntime.PasswordUnavailableMessage is { } passwordMessage)
                {
                    context.Failure = new CliError(CliErrorCodes.NeedsUser, passwordMessage, CliErrorCodes.PasswordHint);
                    exitCode = ExitCodes.NeedsUser;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                exitCode = MapException(ex, context);
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;

                // The service path now stops its scheduler before reaching here, so saving
                // unconditionally captures telemetry from the final cycle too.
                // Mutating commands and service mode save under a blocking lease. Read-only commands
                // (restore, verify, system-restore) save only if telemetry changed, and only try briefly.
                if (mutatesState || TelemetryFingerprint(systemState) != telemetryBefore)
                {
                    // A failed save turns success into a failure; it must not hide why the command already failed (busy, needs a person).
                    var saveOutcome = await SaveStateAtExitOutcomeAsync(systemState, logger, mutatesState, RepositoryCoordinator.Instance).ConfigureAwait(false);
                    if (saveOutcome != StateSaveOutcome.Saved && exitCode == ExitCodes.Success)
                    {
                        exitCode = saveOutcome == StateSaveOutcome.Busy ? ExitCodes.Busy : ExitCodes.Failure;
                        saveFailure = saveOutcome == StateSaveOutcome.Busy
                            ? new CliError(CliErrorCodes.Busy, "The command finished, but another ReStore process held the repository so its state was not saved.", CliErrorCodes.BusyHint)
                            : new CliError(CliErrorCodes.Failure, "The command finished, but its state could not be saved. See the log for details.");
                        context.Failure = saveFailure;
                    }
                }
                logger.Log("Application finished.", LogLevel.Info);
                await context.FlushJsonAsync(saveFailure == null ? null : exitCode, saveFailure).ConfigureAwait(false);
            }

            return exitCode;
        }

        private static async Task<int> RunServiceAsync(CliContext ctx)
        {
            var logger = ctx.Logger;
            var configManager = ctx.Config;
            var systemState = ctx.State;
            var servicePasswordProvider = CreateCliPasswordProviderCore(configManager, allowPrompt: false);
            // Both read the pause file (restore pause / resume, the app's tray) before each unit of work.
            var pauseStore = new ProtectionPauseStore();

            // The watcher only reacts to changes seen while it is running; the
            // scheduler covers the configured backupInterval so anything changed
            // while the service was down still gets captured.
            FileWatcher watcher = null!;
            BackupScheduler scheduler = null!;
            async Task StartWorkersAsync(IReadOnlyDictionary<string, string?>? carried)
            {
                watcher = new FileWatcher(configManager, logger, systemState, ctx.SizeAnalyzer, servicePasswordProvider, pauseStore: pauseStore);
                await watcher.StartAsync().ConfigureAwait(false);
                if (carried is { Count: > 0 })
                {
                    watcher.AdoptPendingChanges(carried);
                }

                scheduler = new BackupScheduler(configManager, logger, systemState, ctx.SizeAnalyzer, servicePasswordProvider, pauseStore: pauseStore);
                await scheduler.StartAsync().ConfigureAwait(false);
            }

            await StartWorkersAsync(null).ConfigureAwait(false);

            logger.Log("File watcher service running. Press Ctrl+C to stop.", LogLevel.Info);
            using var telemetryFlush = new ReStore.Core.src.telemetry.TelemetryFlushTimer();

            // config.json edited while the service runs (the CLI, the app, a text editor) is picked up; the workers restart only when
            // something they bind to (folders, schedules, storage, encryption...) changed.
            using var configMonitor = new ConfigChangeMonitor(configManager.GetConfigFilePath(), logger: logger);
            try
            {
                while (true)
                {
                    await configMonitor.WaitForChangeAsync(ctx.Cancellation).ConfigureAwait(false);
                    var reload = await ConfigReloader.ReloadIfChangedAsync(configManager).ConfigureAwait(false);
                    switch (reload.Outcome)
                    {
                        case ConfigReloadOutcome.Invalid:
                            logger.Log($"config.json changed but cannot be used ({reload.Error}); the service keeps running with the previous configuration.", LogLevel.Warning);
                            break;
                        case ConfigReloadOutcome.Reloaded:
                            logger.Log("config.json changed; the new settings are in effect (nothing needed a restart).", LogLevel.Info);
                            break;
                        case ConfigReloadOutcome.ReloadedRestartNeeded:
                            logger.Log("config.json changed; restarting the watcher and scheduler with the new configuration.", LogLevel.Info);
                            await scheduler.StopAsync().ConfigureAwait(false);
                            await watcher.StopAsync().ConfigureAwait(false);
                            var carried = watcher.TakePendingChanges();
                            scheduler.Dispose();
                            watcher.Dispose();
                            await StartWorkersAsync(carried).ConfigureAwait(false);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on Ctrl+C; stop the scheduler before the state save below.
            }

            await scheduler.StopAsync().ConfigureAwait(false);
            scheduler.Dispose();
            watcher.Dispose();
            logger.Log("File watcher service stopped.", LogLevel.Info);
            return ExitCodes.Success;
        }

        private static async Task<int> RunBackupAsync(CliContext ctx)
        {
            var parsed = ctx.Parsed;
            var configManager = ctx.Config;
            var storageOverride = ctx.StorageOverride;
            var warnings = new List<string>();
            var allowExcluded = parsed.HasOption("--allow-excluded");

            // A folder inside an excluded path backs up nothing and exits 0: refuse it up front and name the rule.
            var requestedFolder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(parsed.Positionals[0]));
            if (Directory.Exists(requestedFolder)
                && new FileSelectionService(ctx.Logger, configManager).FindExcludingRule(requestedFolder) is { } excludingRule)
            {
                if (!allowExcluded)
                {
                    throw new CliCommandException(
                        ExitCodes.Config, CliErrorCodes.Config,
                        $"Not backing up '{requestedFolder}': it is excluded by the {excludingRule}, so no file in it would be backed up.",
                        $"change or remove that rule in {AppPaths.ConfigFile} (see 'restore.exe config show'), or pass --allow-excluded to back the folder up anyway");
                }

                warnings.Add($"'{requestedFolder}' is excluded by the {excludingRule}; --allow-excluded backs it up anyway.");
            }

            var backupPasswordProvider = CreateCliPasswordProvider(configManager);
            var backup = new Backup(ctx.Logger, ctx.State, ctx.SizeAnalyzer, configManager, backupPasswordProvider) { IgnoreExcludedPathsContainingSource = allowExcluded };
            var backupResult = await backup.BackupDirectoryAsync(
                parsed.Positionals[0],
                storageOverride,
                CreateConsoleBackupProgress(),
                ctx.Cancellation).ConfigureAwait(false);
            if (backupResult.SkippedSummary is { } skippedSummary)
            {
                Console.WriteLine($"Backup {skippedSummary}. See the log for details.");
            }

            if (backupResult.FilesScanned == 0)
            {
                warnings.Add($"No files were found to back up in '{requestedFolder}': the folder is empty or every file is excluded (excludedPaths, exclusion patterns, hidden or system files, size limit).");
            }

            foreach (var warning in warnings)
            {
                ctx.Logger.Log(warning, LogLevel.Warning);
            }

            if (ctx.Json)
            {
                var source = Path.GetFullPath(Environment.ExpandEnvironmentVariables(parsed.Positionals[0]));
                var latest = ctx.State.GetLatestSnapshotForGroup(source);
                await ctx.WriteJsonAsync(new
                {
                    source,
                    storage = latest?.StorageType ?? storageOverride ?? configManager.GlobalStorageType,
                    snapshotId = latest?.SnapshotId,
                    manifestPath = latest?.ManifestPath ?? latest?.Path,
                    createdUtc = latest == null ? (DateTime?)null : latest.Timestamp.ToUniversalTime(),
                    sizeBytes = latest?.SizeBytes,
                    encrypted = latest?.Encrypted,
                    warnings,
                    skipped = new
                    {
                        count = backupResult.Skipped.Count,
                        carriedForward = backupResult.Skipped.Count(s => s.CarriedForward),
                        summary = backupResult.SkippedSummary,
                        files = backupResult.Skipped.Select(s => new { path = s.Path, reason = s.Reason, carriedForward = s.CarriedForward })
                    }
                }).ConfigureAwait(false);
            }

            return ExitCodes.Success;
        }

        private const int MaxRestoreEntriesInJson = 1000;

        private static async Task<int> RunRestoreAsync(CliContext ctx)
        {
            var parsed = ctx.Parsed;
            var logger = ctx.Logger;
            var configManager = ctx.Config;
            var cancellationToken = ctx.Cancellation;

            // The --conflict value was validated by TryParseArguments.
            TryParseConflictValue(parsed.GetOption("--conflict"), out var conflictPolicy);

            // --conflict overwrite replaces files, so it asks like the other destructive commands; without a terminal that is exit 4
            // before anything is read or written (the preview is '--dry-run').
            var confirmOverwrite = conflictPolicy == RestoreConflictPolicy.Overwrite && !parsed.HasOption("--dry-run");
            if (confirmOverwrite && !CliRuntime.Yes && CliRuntime.NonInteractive)
            {
                CliSupport.Confirm("Restoring with --conflict overwrite replaces existing files.", "re-run with --yes to confirm in advance (after a --dry-run preview)");
            }

            var restoreStorageType = ctx.StorageOverride ?? configManager.GlobalStorageType;
            var restorePasswordProvider = CreateCliPasswordProvider(configManager);
            using var storage = await configManager.CreateStorageAsync(restoreStorageType).ConfigureAwait(false);
            var restore = new Restore(logger, storage, restorePasswordProvider, ctx.State);
            var includePatterns = parsed.Options.TryGetValue("--include", out var includes) ? includes : [];
            var isDryRun = parsed.HasOption("--dry-run");

            // --map rewrites the destination; "@original" means the folder the snapshot was taken from.
            var restoreMapping = PathMapping.Parse(parsed.Options.TryGetValue("--map", out var restoreMaps) ? restoreMaps : []);
            var restoreTarget = parsed.Positionals[1];
            if (restoreTarget == "@original")
            {
                var manifestPath = await RepositoryDiscovery.ResolveManifestPathAsync(storage, parsed.Positionals[0], cancellationToken).ConfigureAwait(false);
                using var targetReader = new RepositoryManifestReader(logger, storage, restorePasswordProvider);
                restoreTarget = (await RepositoryDiscovery.LoadManifestAsync(storage, manifestPath, cancellationToken, targetReader).ConfigureAwait(false)).Manifest.Group;
            }

            restoreTarget = restoreMapping.Apply(restoreTarget);

            var preview = await restore.PreviewRestoreAsync(parsed.Positionals[0], restoreTarget, null, cancellationToken).ConfigureAwait(false);
            var selectedPaths = includePatterns.Count == 0
                ? null
                : FilterByPatterns(preview, includePatterns);

            if (selectedPaths != null && selectedPaths.Count == 0)
            {
                logger.Log(
                    $"No files in the snapshot matched the supplied --include pattern(s): {string.Join(", ", includePatterns)}. Nothing was restored.",
                    LogLevel.Error);
                return ExitCodes.Failure;
            }

            PrintRestorePreview(preview, selectedPaths, conflictPolicy);

            var entries = selectedPaths == null
                ? preview.Entries
                : [.. preview.Entries.Where(entry => selectedPaths.Contains(entry.RelativePath))];
            var previewDocument = new
            {
                files = entries.Count,
                totalBytes = entries.Sum(entry => entry.SizeBytes),
                identical = entries.Count(entry => entry.Conflict == RestoreConflictKind.Identical),
                differing = entries.Count(entry => entry.Conflict == RestoreConflictKind.Differs),
                excludedByFilter = preview.FileCount - entries.Count + preview.FilesFilteredOut
            };

            if (isDryRun)
            {
                logger.Log("Dry run requested; no files were written.", LogLevel.Info);
                if (ctx.Json)
                {
                    await ctx.WriteJsonAsync(new
                    {
                        snapshotId = preview.SnapshotId,
                        manifestPath = preview.ResolvedManifestPath,
                        target = preview.TargetDirectory,
                        dryRun = true,
                        conflictPolicy = conflictPolicy.ToString().ToLowerInvariant(),
                        preview = previewDocument,
                        entries = entries.Take(MaxRestoreEntriesInJson).Select(e => new
                        {
                            path = e.RelativePath,
                            targetPath = e.TargetPath,
                            sizeBytes = e.SizeBytes,
                            conflict = e.Conflict.ToString().ToLowerInvariant(),
                            existingSizeBytes = e.ExistingSizeBytes
                        }),
                        entriesTruncated = entries.Count > MaxRestoreEntriesInJson,
                        result = (object?)null
                    }).ConfigureAwait(false);
                }

                return ExitCodes.Success;
            }

            if (confirmOverwrite)
            {
                var existing = entries.Count(entry => entry.Conflict != RestoreConflictKind.None);
                CliSupport.ConfirmOrAbort(
                    $"Overwrite up to {existing} existing file(s) in {preview.TargetDirectory}?",
                    "re-run with --yes to confirm in advance (after a --dry-run preview)");
            }

            var outcome = await restore.RestoreFromBackupAsync(
                parsed.Positionals[0],
                restoreTarget,
                new RestoreOptions
                {
                    ConflictPolicy = conflictPolicy,
                    RelativePaths = selectedPaths
                },
                CreateConsoleRestoreProgress(),
                cancellationToken).ConfigureAwait(false);

            logger.Log(
                $"Restore finished: restored={outcome.FilesRestored}, skipped={outcome.FilesSkipped}, keptBoth={outcome.FilesKeptBoth}, overwritten={outcome.FilesOverwritten}.",
                LogLevel.Info);

            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new
                {
                    snapshotId = preview.SnapshotId,
                    manifestPath = preview.ResolvedManifestPath,
                    target = preview.TargetDirectory,
                    dryRun = false,
                    conflictPolicy = conflictPolicy.ToString().ToLowerInvariant(),
                    preview = previewDocument,
                    result = new
                    {
                        filesRestored = outcome.FilesRestored,
                        filesSkipped = outcome.FilesSkipped,
                        filesKeptBoth = outcome.FilesKeptBoth,
                        filesOverwritten = outcome.FilesOverwritten,
                        directoriesRestored = outcome.DirectoriesRestored,
                        bytesRestored = outcome.BytesRestored,
                        skippedPaths = outcome.SkippedRelativePaths.Take(MaxRestoreEntriesInJson)
                    }
                }).ConfigureAwait(false);
            }

            return ExitCodes.Success;
        }

        private static async Task<int> RunVerifyAsync(CliContext ctx)
        {
            var parsed = ctx.Parsed;
            var logger = ctx.Logger;
            var verifyStorageType = ctx.StorageOverride ?? ctx.Config.GlobalStorageType;
            var verifyPasswordProvider = CreateCliPasswordProvider(ctx.Config);
            using var storage = await ctx.Config.CreateStorageAsync(verifyStorageType).ConfigureAwait(false);
            var verifier = new SnapshotIntegrityVerifier(logger, storage, verifyPasswordProvider, ctx.State);

            // The values were validated by TryParseArguments.
            VerificationModes.TryParse(parsed.GetOption("--mode"), out var verifyMode);
            var verifyOptions = new VerificationOptions(
                verifyMode,
                parsed.GetOption("--sample-percent") is { } samplePercent ? int.Parse(samplePercent, System.Globalization.CultureInfo.InvariantCulture) : VerificationModes.DefaultSamplePercent,
                parsed.GetOption("--seed") is { } sampleSeed ? int.Parse(sampleSeed, System.Globalization.CultureInfo.InvariantCulture) : null);
            var verificationResult = await verifier.VerifyAsync(parsed.Positionals[0], null, ctx.Cancellation, verifyOptions).ConfigureAwait(false);

            var exitCode = ExitCodes.Success;
            if (verificationResult.IsValid)
            {
                logger.Log(
                    $"Snapshot verification ({VerificationModes.Name(verificationResult.Mode)}) passed for '{verificationResult.ResolvedManifestPath}' (snapshot: {verificationResult.SnapshotId}).",
                    LogLevel.Info);
                if (verificationResult.SampleSeed is { } usedSeed)
                {
                    logger.Log($"Verified {verificationResult.ChunksSampled} of {verificationResult.UniqueChunks} chunk(s) in full; repeat the same sample with --seed {usedSeed}.", LogLevel.Info);
                }
            }
            else
            {
                logger.Log(
                    $"Snapshot verification ({VerificationModes.Name(verificationResult.Mode)}) failed for '{verificationResult.ResolvedManifestPath}'. Errors: {verificationResult.Errors.Count}.",
                    LogLevel.Error);
                if (verificationResult.SampleSeed is { } failedSeed)
                {
                    logger.Log($"Sample seed {failedSeed}: repeat the same sample with --seed {failedSeed}.", LogLevel.Error);
                }

                foreach (var error in verificationResult.Errors)
                {
                    logger.Log($"Verify Error: {error}", LogLevel.Error);
                }

                exitCode = ExitCodes.Failure;
            }

            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new
                {
                    valid = verificationResult.IsValid,
                    snapshotId = verificationResult.SnapshotId,
                    manifestPath = verificationResult.ResolvedManifestPath,
                    mode = VerificationModes.Name(verificationResult.Mode),
                    encrypted = verificationResult.EncryptionEnabled,
                    manifestHashValid = verificationResult.ManifestHashValid,
                    fileCount = verificationResult.FileCount,
                    chunkReferences = verificationResult.ChunkReferences,
                    uniqueChunks = verificationResult.UniqueChunks,
                    chunksDownloaded = verificationResult.ChunksDownloaded,
                    chunksSampled = verificationResult.ChunksSampled,
                    sampleSeed = verificationResult.SampleSeed,
                    missingChunks = verificationResult.MissingChunks,
                    invalidChunks = verificationResult.InvalidChunks,
                    invalidFiles = verificationResult.InvalidFiles,
                    errors = verificationResult.Errors
                }, exitCode).ConfigureAwait(false);
            }

            return exitCode;
        }

        private static async Task<int> RunSystemBackupAsync(CliContext ctx)
        {
            if (!OperatingSystem.IsWindows())
            {
                ctx.Logger.Log("The 'system-backup' command is only supported on Windows.", LogLevel.Error);
                return ExitCodes.Failure;
            }

            var backupType = ctx.Parsed.Positionals.Count >= 1 ? ctx.Parsed.Positionals[0] : "all";
            var systemBackupPasswordProvider = CreateCliPasswordProvider(ctx.Config);
            var systemBackupManager = new SystemBackupManager(ctx.Logger, ctx.Config, ctx.State, systemBackupPasswordProvider)
            {
                StorageTypeOverride = ctx.StorageOverride
            };

            SystemBackupResult? all = null;
            switch (backupType.ToLowerInvariant())
            {
                case "programs":
                    await systemBackupManager.BackupInstalledProgramsAsync().ConfigureAwait(false);
                    break;
                case "environment":
                    await systemBackupManager.BackupEnvironmentVariablesAsync().ConfigureAwait(false);
                    break;
                case "settings":
                    await systemBackupManager.BackupWindowsSettingsAsync().ConfigureAwait(false);
                    break;
                case "devtools":
                    await systemBackupManager.BackupDevToolsAsync().ConfigureAwait(false);
                    break;
                case "all":
                default:
                    all = await systemBackupManager.BackupSystemAsync().ConfigureAwait(false);
                    break;
            }

            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new
                {
                    component = backupType.ToLowerInvariant(),
                    storage = ctx.StorageOverride,
                    components = all?.Components.Select(c => new { component = c.Component, succeeded = c.Succeeded, error = c.Error })
                }).ConfigureAwait(false);
            }

            return ExitCodes.Success;
        }

        private static async Task<int> RunSystemRestoreAsync(CliContext ctx)
        {
            var parsed = ctx.Parsed;
            if (!OperatingSystem.IsWindows())
            {
                ctx.Logger.Log("The 'system-restore' command is only supported on Windows.", LogLevel.Error);
                return ExitCodes.Failure;
            }

            var restoreType = parsed.Positionals.Count >= 2 ? parsed.Positionals[1] : "all";
            var systemRestorePasswordProvider = CreateCliPasswordProvider(ctx.Config);
            var systemRestoreManager = new SystemBackupManager(ctx.Logger, ctx.Config, ctx.State, systemRestorePasswordProvider);
            var planOnly = parsed.HasOption("--plan");
            if (!planOnly)
            {
                RequireSystemRestoreConfirmationWithoutTerminal();
            }

            var systemRestoreResult = await systemRestoreManager.RestoreSystemAsync(
                restoreType, parsed.Positionals[0], ctx.StorageOverride,
                new SystemRestoreOptions { PlanOnly = planOnly, SelectActions = planOnly ? null : SelectSystemRestoreActionsAsync }, ctx.Cancellation).ConfigureAwait(false);
            if (planOnly && systemRestoreResult.Plan != null)
            {
                Console.WriteLine(SystemRestorePlanFormatter.Format(systemRestoreResult.Plan, parsed.HasOption("--reveal")));
            }
            else if (systemRestoreResult.UndoPointFolder != null)
            {
                Console.WriteLine($"Undo point: {systemRestoreResult.UndoPointFolder} (revert with: restore.exe system-undo \"{systemRestoreResult.UndoPointFolder}\")");
            }

            var exitCode = systemRestoreResult.Failed > 0 ? ExitCodes.Failure : ExitCodes.Success;
            if (ctx.Json)
            {
                var plan = systemRestoreResult.Plan;
                var reveal = parsed.HasOption("--reveal");
                await ctx.WriteJsonAsync(new
                {
                    component = systemRestoreResult.Component,
                    planOnly,
                    undoPoint = systemRestoreResult.UndoPointFolder,
                    total = systemRestoreResult.Total,
                    succeeded = systemRestoreResult.Succeeded,
                    failed = systemRestoreResult.Failed,
                    skipped = systemRestoreResult.Skipped,
                    messages = systemRestoreResult.Messages,
                    plan = plan == null ? null : new
                    {
                        changes = plan.ChangeCount,
                        unchanged = plan.UnchangedCount,
                        needsAdmin = plan.NeedsAdminCount,
                        rejected = plan.Rejected,
                        valuesHidden = !reveal,
                        actions = plan.Actions.Select(a =>
                        {
                            var view = SystemRestoreValueView.Describe(a, reveal);
                            return new
                            {
                                id = a.Id,
                                component = a.Component,
                                change = a.Change,
                                isChange = a.IsChange,
                                name = a.Name,
                                scope = a.Scope,
                                changed = view.Changed,
                                oldValue = view.OldValue,
                                newValue = view.NewValue,
                                oldValueLength = view.OldLength,
                                newValueLength = view.NewLength,
                                pathAdded = view.PathAdded,
                                pathRemoved = view.PathRemoved,
                                detail = a.Detail,
                                needsAdmin = a.NeedsAdmin,
                                keys = a.Keys
                            };
                        })
                    }
                }, exitCode).ConfigureAwait(false);
            }

            return exitCode;
        }

        private const string SystemRestoreHint = "re-run with --yes to confirm in advance (after a --plan preview)";

        /// <summary>Without a terminal and without <c>--yes</c> a system restore must not start: exit 4 before anything is downloaded or changed.</summary>
        internal static void RequireSystemRestoreConfirmationWithoutTerminal()
        {
            if (!CliRuntime.Yes && CliRuntime.NonInteractive)
            {
                CliSupport.Confirm("system-restore changes this machine (environment variables).", SystemRestoreHint);
            }
        }

        /// <summary>
        /// The CLI's confirmation for <c>system-restore</c>: names the component and the number of changes from the plan, then returns the
        /// rows to apply. Only environment variables are applied on the CLI (programs and settings are reported), so only those rows
        /// are returned; declining throws (exit 1) before anything is changed.
        /// </summary>
        internal static Task<IReadOnlyCollection<string>?> SelectSystemRestoreActionsAsync(SystemRestorePlan plan)
        {
            var ids = plan.Changes.Where(a => a.Component == "environment").Select(a => a.Id).ToList();
            var otherChanges = plan.Changes.Count(a => a.Component != "environment");
            if (otherChanges > 0)
            {
                // The command line applies environment variables only; programs and Windows settings are applied from the GUI.
                Console.Error.WriteLine(
                    $"Note: {otherChanges} program/settings change(s) in this backup are not applied from the command line; "
                    + "see them with --plan and apply them from the ReStore app (System Restore page).");
            }

            if (ids.Count > 0)
            {
                CliSupport.ConfirmOrAbort($"Apply {ids.Count} environment variable change(s) from the {plan.Component} backup to this machine?", SystemRestoreHint);
            }

            return Task.FromResult<IReadOnlyCollection<string>?>(ids);
        }

        private static async Task<int> RunSystemUndoAsync(CliContext ctx)
        {
            var parsed = ctx.Parsed;
            if (parsed.HasOption("--list"))
            {
                var points = SystemRestoreUndo.List();
                foreach (var point in points)
                {
                    Console.WriteLine($"{point.Path}  [{point.Component}, {point.CreatedUtc.ToLocalTime():g}]  {point.Summary}");
                }

                Console.WriteLine(points.Count == 0 ? "No undo points." : $"{points.Count} undo point(s).");
                if (ctx.Json)
                {
                    await ctx.WriteJsonAsync(new
                    {
                        points = points.Select(p => new { path = p.Path, createdUtc = p.CreatedUtc.ToUniversalTime(), component = p.Component, summary = p.Summary })
                    }).ConfigureAwait(false);
                }

                return ExitCodes.Success;
            }

            if (parsed.GetOption("--delete") is { } deleteDir)
            {
                SystemRestoreUndo.Delete(deleteDir);
                Console.WriteLine($"Deleted undo point {deleteDir}.");
                if (ctx.Json)
                {
                    await ctx.WriteJsonAsync(new { deleted = deleteDir }).ConfigureAwait(false);
                }

                return ExitCodes.Success;
            }

            if (!OperatingSystem.IsWindows())
            {
                ctx.Logger.Log("The 'system-undo' command is only supported on Windows.", LogLevel.Error);
                return ExitCodes.Failure;
            }

            var undoResult = await new SystemRestoreUndo(SystemRestoreServices.CreateDefault(ctx.Logger), ctx.Logger)
                .UndoAsync(parsed.Positionals[0], ctx.Cancellation).ConfigureAwait(false);
            foreach (var message in undoResult.Messages)
            {
                Console.WriteLine(message);
            }

            Console.WriteLine($"Undo finished: {undoResult.Restored} restored, {undoResult.Failed} failed.");
            var exitCode = undoResult.Failed > 0 ? ExitCodes.Failure : ExitCodes.Success;
            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new
                {
                    undoPoint = parsed.Positionals[0],
                    restored = undoResult.Restored,
                    failed = undoResult.Failed,
                    messages = undoResult.Messages,
                    installedPrograms = undoResult.InstalledPrograms
                }, exitCode).ConfigureAwait(false);
            }

            return exitCode;
        }

        private static readonly System.Text.Json.JsonSerializerOptions CliJsonOptions = CliJson.Options;

        private static string? Iso(DateTime? utc) => utc?.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture);

        private static string Local(DateTime? utc) => utc == null ? "never" : utc.Value.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);

        /// <summary>
        /// <c>status</c>: read-only snapshot of protection. Always exits 0 unless it could not be produced, so monitoring
        /// scripts read the document (or the stale/busy fields) instead of the exit code.
        /// </summary>
        private static async Task<int> RunStatusAsync(CliContext ctx)
        {
            var configManager = ctx.Config;
            var cancellationToken = ctx.Cancellation;
            var (busy, busyReason) = await ProtectionStatusBuilder.ProbeBusyAsync(RepositoryCoordinator.Instance, cancellationToken).ConfigureAwait(false);
            using var scheduler = new BackupScheduler(configManager, ctx.Logger, ctx.State, ctx.SizeAnalyzer);
            var status = ProtectionStatusBuilder.Build(configManager, ctx.State, scheduler, DateTime.UtcNow, busy, busyReason);

            if (ctx.Json)
            {
                var document = new
                {
                    generatedUtc = Iso(status.GeneratedUtc),
                    storageType = status.StorageType,
                    encryptionEnabled = status.EncryptionEnabled,
                    autoStart = status.AutoStart,
                    backupInterval = status.BackupInterval.ToString(),
                    nextDueUtc = Iso(status.NextDueUtc),
                    backupWindow = new
                    {
                        configured = status.Window.Configured,
                        active = status.Window.Active,
                        openNow = status.Window.OpenNow,
                        start = status.Window.Start,
                        end = status.Window.End,
                        days = status.Window.Days,
                        description = status.Window.Description,
                        nextOpenUtc = Iso(status.Window.NextOpenUtc)
                    },
                    repository = new { busy = status.RepositoryBusy, reason = status.RepositoryBusyReason },
                    update = UpdateCommands.BuildStatusDocument(),
                    systemBackup = new
                    {
                        enabled = status.SystemBackupEnabled,
                        interval = status.SystemBackupInterval.ToString(),
                        schedule = status.SystemBackupSchedule,
                        scheduleDescription = status.SystemBackupScheduleDescription,
                        nextDueUtc = Iso(status.SystemBackupNextDueUtc),
                        lastBackupUtc = Iso(status.LastSystemBackupUtc)
                    },
                    retention = new { enabled = status.RetentionEnabled, lastRunUtc = Iso(status.LastRetentionRunUtc) },
                    verification = new
                    {
                        enabled = status.VerificationEnabled,
                        mode = status.VerificationMode,
                        samplePercent = status.VerificationSamplePercent,
                        interval = status.VerifyInterval.ToString(),
                        lastVerifiedUtc = Iso(status.Verify.LastVerifiedUtc),
                        lastResult = status.Verify.LastResult,
                        lastMessage = status.Verify.LastMessage
                    },
                    staleGroups = status.StaleGroups.Select(group => group.Path),
                    groups = status.Groups.Select(group => new
                    {
                        path = group.Path,
                        storageType = group.StorageType,
                        exists = group.Exists,
                        snapshotCount = group.SnapshotCount,
                        lastSnapshotUtc = Iso(group.LastSnapshotUtc),
                        lastSuccessUtc = Iso(group.LastSuccessUtc),
                        nextDueUtc = Iso(group.NextDueUtc),
                        schedule = new { expression = group.Schedule, description = group.ScheduleDescription, source = group.ScheduleSource },
                        waitingForWindow = group.WaitingForWindow,
                        overdue = group.Overdue,
                        stale = group.Stale,
                        lastFailure = group.LastFailure == null ? null : new
                        {
                            timeUtc = Iso(group.LastFailure.EndUtc),
                            message = group.LastFailure.Message,
                            afterLastSuccess = group.LastSuccessUtc == null || group.LastFailure.EndUtc > group.LastSuccessUtc
                        },
                        lastRun = group.LastRun == null ? null : new
                        {
                            timestampUtc = Iso(group.LastRun.TimestampUtc),
                            filesAdded = group.LastRun.FilesAdded,
                            filesChanged = group.LastRun.FilesChanged,
                            filesRemoved = group.LastRun.FilesRemoved,
                            filesSkipped = group.LastRun.FilesSkipped,
                            bytesUploaded = group.LastRun.BytesUploaded
                        }
                    })
                };
                await ctx.WriteJsonAsync(document).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            Console.WriteLine($"Storage: {status.StorageType}   Encryption: {(status.EncryptionEnabled ? "on" : "off")}   Background protection at launch: {(status.AutoStart ? "on" : "off")}");
            Console.WriteLine($"Repository: {(status.RepositoryBusy ? $"busy ({status.RepositoryBusyReason})" : "idle")}");
            Console.WriteLine(UpdateCommands.BuildStatusLine());
            Console.WriteLine($"Backup interval: {(status.BackupInterval > TimeSpan.Zero ? status.BackupInterval.ToString() : "disabled")}   Next backup due: {Local(status.NextDueUtc)}");
            Console.WriteLine($"Backup window: {status.Window.Description}{(status.Window.Active ? (status.Window.OpenNow ? " (open now)" : $" (closed; opens {Local(status.Window.NextOpenUtc)})") : string.Empty)}");
            if (status.Groups.Count == 0)
            {
                Console.WriteLine("No watched folders are configured.");
            }

            foreach (var group in status.Groups)
            {
                var flags = string.Join(", ", new[] { group.Exists ? null : "folder missing", group.Stale ? "STALE" : null, group.WaitingForWindow ? "waiting for the backup window" : group.Overdue ? "due now" : null }.Where(f => f != null));
                Console.WriteLine($"  {group.Path} [{group.StorageType}]{(flags.Length > 0 ? $"  ({flags})" : string.Empty)}");
                if (group.ScheduleSource == "folder")
                {
                    Console.WriteLine($"    schedule: {group.ScheduleDescription}");
                }
                Console.WriteLine($"    last backup: {Local(group.LastSnapshotUtc)}   last successful run: {Local(group.LastSuccessUtc)}   snapshots: {group.SnapshotCount}   next due: {Local(group.NextDueUtc)}");
                if (group.LastFailure != null)
                {
                    Console.WriteLine($"    last failed backup: {Local(group.LastFailure.EndUtc)}: {group.LastFailure.Message}{(group.LastSuccessUtc != null && group.LastFailure.EndUtc <= group.LastSuccessUtc ? " (since succeeded)" : string.Empty)}");
                }

                if (group.LastRun != null)
                {
                    Console.WriteLine($"    last run: +{group.LastRun.FilesAdded} added, {group.LastRun.FilesChanged} changed, {group.LastRun.FilesRemoved} removed, {group.LastRun.FilesSkipped} skipped");
                }
            }

            Console.WriteLine($"System backup: {(status.SystemBackupEnabled ? $"{(status.SystemBackupSchedule != null ? status.SystemBackupScheduleDescription : $"every {status.SystemBackupInterval}")}, last {Local(status.LastSystemBackupUtc)}" : "disabled")}");
            Console.WriteLine($"Retention: {(status.RetentionEnabled ? $"enabled, last run {Local(status.LastRetentionRunUtc)}" : "disabled")}");
            Console.WriteLine($"Verification: {(status.VerificationEnabled ? $"every {status.VerifyInterval} ({status.VerificationMode}{(status.VerificationMode.Equals("sample", StringComparison.OrdinalIgnoreCase) ? $", {status.VerificationSamplePercent}%" : string.Empty)})" : "scheduled verification disabled")}, last {Local(status.Verify.LastVerifiedUtc)}{(status.Verify.LastResult is { } lastResult ? $" ({lastResult})" : string.Empty)}");
            if (status.StaleGroups.Count > 0)
            {
                Console.WriteLine($"{status.StaleGroups.Count} folder(s) have had no successful backup for too long.");
            }

            return ExitCodes.Success;
        }

        /// <summary>
        /// <c>run-due</c>: one <see cref="BackupScheduler"/> sweep, then exit. Exit 0 when nothing failed (including nothing
        /// due and work deferred because the repository was busy), 1 when a backup, system backup, verification or the
        /// retention pass failed.
        /// </summary>
        private static async Task<int> RunDueAsync(CliContext ctx)
        {
            var logger = ctx.Logger;
            var passwordProvider = CreateCliPasswordProviderCore(ctx.Config, allowPrompt: false);
            using var scheduler = new BackupScheduler(ctx.Config, logger, ctx.State, ctx.SizeAnalyzer, passwordProvider, pauseStore: new ProtectionPauseStore());
            var cycle = await scheduler.RunDueOnceAsync(ctx.Cancellation).ConfigureAwait(false);

            var failed = cycle.HasFailures || cycle.RetentionFailed;
            var nothingDue = !cycle.DidWork && !failed && cycle.Deferred == 0 && !cycle.RetentionRan && cycle.WindowDeferred == 0;
            var exitCode = failed ? ExitCodes.Failure : ExitCodes.Success;
            ctx.RunDueOutcome = new RunDueRecord
            {
                BackupsSucceeded = cycle.DirectoriesBackedUp,
                BackupsFailed = cycle.DirectoriesFailed,
                Deferred = cycle.Deferred,
                Paused = cycle.Paused,
                NothingDue = nothingDue,
                SystemBackupRan = cycle.SystemBackupRan,
                SystemBackupFailed = cycle.SystemBackupFailed,
                VerificationsSucceeded = cycle.SnapshotsVerified,
                VerificationsFailed = cycle.VerificationsFailed,
                RetentionRan = cycle.RetentionRan,
                RetentionFailed = cycle.RetentionFailed
            };

            if (ctx.Json)
            {
                var document = new
                {
                    ok = !failed,
                    exitCode,
                    nothingDue,
                    backups = new { succeeded = cycle.DirectoriesBackedUp, failed = cycle.DirectoriesFailed, filesSkipped = cycle.FilesSkipped },
                    systemBackup = new { ran = cycle.SystemBackupRan, failed = cycle.SystemBackupFailed },
                    verification = new { succeeded = cycle.SnapshotsVerified, failed = cycle.VerificationsFailed },
                    retention = new { ran = cycle.RetentionRan, failed = cycle.RetentionFailed },
                    deferred = cycle.Deferred,
                    paused = cycle.Paused,
                    backupWindow = new { closed = cycle.WindowClosed, deferred = cycle.WindowDeferred },
                    failures = cycle.FailureMessages.Concat(cycle.VerificationFailureMessages)
                };
                await ctx.WriteJsonAsync(document, exitCode).ConfigureAwait(false);
                return exitCode;
            }

            if (cycle.Paused)
            {
                logger.Log("Protection is paused (restore pause); nothing was run. 'restore resume' ends the pause.", LogLevel.Info);
            }
            else if (nothingDue)
            {
                logger.Log(cycle.WindowClosed ? "Nothing is due (the backup window is closed)." : "Nothing is due.", LogLevel.Info);
            }
            else
            {
                logger.Log(
                    $"run-due finished: backups ok={cycle.DirectoriesBackedUp} failed={cycle.DirectoriesFailed}; system backup ran={cycle.SystemBackupRan} failed={cycle.SystemBackupFailed}; " +
                    $"verified={cycle.SnapshotsVerified} failed={cycle.VerificationsFailed}; retention ran={cycle.RetentionRan} failed={cycle.RetentionFailed}; deferred (repository busy)={cycle.Deferred}; waiting for the backup window={cycle.WindowDeferred}.",
                    failed ? LogLevel.Error : LogLevel.Info);
            }

            return exitCode;
        }

        private static async Task<int> RunTestRestoreAsync(CliContext ctx)
        {
            var parsed = ctx.Parsed;
            var configManager = ctx.Config;
            var logger = ctx.Logger;
            var storageOverride = ctx.StorageOverride;
            var cancellationToken = ctx.Cancellation;
            var fileCount = parsed.GetOption("--files") is { } files ? int.Parse(files, System.Globalization.CultureInfo.InvariantCulture) : SnapshotTestRestore.DefaultFileCount;
            int? seed = parsed.GetOption("--seed") is { } seedText ? int.Parse(seedText, System.Globalization.CultureInfo.InvariantCulture) : null;

            var passwordProvider = CreateCliPasswordProvider(configManager);
            using var storage = await configManager.CreateStorageAsync(storageOverride ?? configManager.GlobalStorageType).ConfigureAwait(false);
            var result = await new SnapshotTestRestore(logger, storage, passwordProvider, ctx.State)
                .RunAsync(parsed.Positionals[0], fileCount, seed, null, cancellationToken).ConfigureAwait(false);

            if (ctx.Json)
            {
                var document = new
                {
                    ok = result.Passed,
                    passed = result.Passed,
                    snapshotId = result.SnapshotId,
                    manifest = result.ResolvedManifestPath,
                    seed = result.Seed,
                    filesInSnapshot = result.FilesInSnapshot,
                    filesTested = result.FilesTested,
                    filesPassed = result.FilesPassed,
                    bytesRestored = result.BytesRestored,
                    reason = result.Error,
                    files = result.Files.Select(file => new { path = file.RelativePath, sizeBytes = file.SizeBytes, passed = file.Passed, error = file.Error })
                };
                if (result.Missing != null)
                {
                    ctx.Failure ??= DescribeNotFound(result.Missing, ctx);
                }
                else if (!result.Passed && result.Error != null)
                {
                    ctx.Failure ??= new CliError(CliErrorCodes.Failure, result.Error);
                }

                await ctx.WriteJsonAsync(document, result.Passed ? ExitCodes.Success : ExitCodes.Failure).ConfigureAwait(false);
            }
            else
            {
                foreach (var file in result.Files)
                {
                    Console.WriteLine($"  {(file.Passed ? "OK  " : "FAIL")} {file.RelativePath}{(file.Error != null ? $": {file.Error}" : string.Empty)}");
                }

                if (result.Passed)
                {
                    logger.Log($"Test restore passed: {result.FilesPassed} of {result.FilesTested} sampled file(s) restored and matched the manifest (seed {result.Seed}).", LogLevel.Info);
                }
                else
                {
                    logger.Log($"Test restore FAILED: {result.FilesPassed} of {result.FilesTested} sampled file(s) passed (seed {result.Seed}). {result.Error}", LogLevel.Error);
                }
            }

            return result.Passed ? ExitCodes.Success : ExitCodes.Failure;
        }

        private static async Task<int> RunRetentionPreviewAsync(CliContext ctx)
        {
            var parsed = ctx.Parsed;
            var configManager = ctx.Config;
            var logger = ctx.Logger;
            var systemState = ctx.State;
            var filter = parsed.GetOption("--group");
            string? group = null;
            if (filter != null)
            {
                group = systemState.GetBackupGroups().FirstOrDefault(g => g.Equals(filter, StringComparison.OrdinalIgnoreCase));
                if (group == null)
                {
                    logger.Log($"No backup group matches '{filter}'.", LogLevel.Error);
                    return ExitCodes.Failure;
                }
            }

            var storageFilter = ctx.StorageOverride is { } storageName ? ResolveConfiguredStorage(configManager, storageName) : null;
            var plans = new RetentionManager(logger, configManager, systemState).PreviewPlans(configManager.Retention, group)
                .Select(p => (p.Group, Plan: FilterPlanToStorage(p.Plan, storageFilter)))
                .ToList();

            if (ctx.Json)
            {
                var document = new
                {
                    enabled = configManager.Retention.Enabled,
                    mode = configManager.Retention.Mode,
                    groups = plans.Select(p => new
                    {
                        group = p.Group,
                        snapshots = p.Plan.Decisions.Select(d => new
                        {
                            path = d.Backup.Path,
                            storage = d.StorageType,
                            timestamp = d.Backup.Timestamp,
                            label = d.Backup.Label,
                            action = d.Keep ? "keep" : "delete",
                            reasons = d.Reasons
                        })
                    })
                };
                await ctx.WriteJsonAsync(document).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            if (!configManager.Retention.Enabled)
            {
                Console.WriteLine("Note: retention is disabled in the configuration; nothing is deleted automatically. Showing what the configured policy would do if enabled.");
            }

            var totalDelete = 0;
            foreach (var (planGroup, plan) in plans)
            {
                Console.WriteLine($"Group: {planGroup}");
                foreach (var decision in plan.Decisions)
                {
                    Console.WriteLine($"  {(decision.Keep ? "keep  " : "DELETE")} [{decision.StorageType}] {decision.Backup.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}  {decision.Backup.Path}  ({string.Join("; ", decision.Reasons)})");
                }

                totalDelete += plan.ToDelete.Count;
            }

            Console.WriteLine($"Dry run: {totalDelete} snapshot(s) would be deleted. Nothing was changed.");
            return ExitCodes.Success;
        }

        private static async Task<int> RunSnapshotsAsync(CliContext ctx)
        {
            var parsed = ctx.Parsed;
            var configManager = ctx.Config;
            var logger = ctx.Logger;
            var cancellationToken = ctx.Cancellation;
            var storageType = ctx.StorageOverride ?? configManager.GlobalStorageType;
            using var storage = await configManager.CreateStorageAsync(storageType).ConfigureAwait(false);
            var result = await new RepositoryDiscovery(logger).DiscoverAsync(storage, CreateCliPasswordProvider(configManager), cancellationToken).ConfigureAwait(false);

            var filter = parsed.GetOption("--group");
            var groups = result.Groups
                .Where(g => filter == null
                    || g.GroupKey.Equals(filter, StringComparison.OrdinalIgnoreCase)
                    || g.OriginalPath.Equals(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (filter != null && groups.Count == 0)
            {
                logger.Log($"No backup group matches '{filter}'. Run 'snapshots' without --group to list them.", LogLevel.Error);
                return ExitCodes.Failure;
            }

            if (ctx.Json)
            {
                var document = new
                {
                    storage = storageType,
                    groups = groups.Select(g => new
                    {
                        key = g.GroupKey,
                        originalPath = g.OriginalPath,
                        machine = g.MachineName,
                        installId = g.InstallId,
                        hasIndex = g.HasIndex,
                        encrypted = g.Encrypted,
                        warnings = g.Warnings,
                        snapshots = g.Snapshots.Select(s => new
                        {
                            id = s.SnapshotId,
                            manifestPath = s.ManifestPath,
                            createdUtc = s.CreatedUtc,
                            fileCount = s.FileCount,
                            totalBytes = s.TotalBytes,
                            mode = s.BackupMode,
                            encrypted = s.Encrypted,
                            manifestVersion = s.ManifestVersion,
                            head = s.IsHead
                        })
                    }),
                    warnings = result.Warnings
                };
                await ctx.WriteJsonAsync(document).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            if (groups.Count == 0)
            {
                Console.WriteLine($"No backups found in storage '{storageType}'.");
                return ExitCodes.Success;
            }

            foreach (var group in groups)
            {
                Console.WriteLine();
                Console.WriteLine($"{group.OriginalPath}  [{group.GroupKey}]{(group.Encrypted ? " (encrypted)" : string.Empty)}");
                if (group.MachineName != null)
                {
                    Console.WriteLine($"  Written by {group.MachineName}");
                }

                foreach (var snapshot in group.Snapshots)
                {
                    Console.WriteLine($"  {snapshot.CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)} UTC  {snapshot.FileCount,6} files  {ByteFormatter.Format(snapshot.TotalBytes),10}{(snapshot.IsHead ? "  HEAD" : string.Empty)}  {snapshot.ManifestPath}");
                }

                foreach (var warning in group.Warnings)
                {
                    Console.WriteLine($"  Warning: {warning}");
                }
            }

            foreach (var warning in result.Warnings)
            {
                Console.WriteLine($"Warning: {warning}");
            }

            return ExitCodes.Success;
        }

        /// <summary>
        /// ls / versions / diff: read-only, manifest-only (no chunk is fetched and no password is needed, because
        /// manifests are not encrypted). Human output goes to stdout; with --json the document is the only stdout.
        /// </summary>
        private static async Task<int> RunBrowseAsync(CliContext ctx)
        {
            var parsed = ctx.Parsed;
            var configManager = ctx.Config;
            var logger = ctx.Logger;
            var cancellationToken = ctx.Cancellation;
            var storageType = ctx.StorageOverride ?? configManager.GlobalStorageType;
            using var storage = await configManager.CreateStorageAsync(storageType).ConfigureAwait(false);
            var browser = new SnapshotBrowser(logger, storage, null, CreateCliPasswordProvider(configManager));
            var json = ctx.Json;

            switch (parsed.Command)
            {
                case "ls":
                {
                    var tree = await browser.LoadTreeAsync(parsed.Positionals[0], cancellationToken).ConfigureAwait(false);
                    var requested = parsed.GetOption("--path") ?? string.Empty;
                    var node = tree.Find(requested);
                    if (node == null)
                    {
                        throw CliCommandException.NotFound(
                            $"'{requested}' does not exist in snapshot {tree.SnapshotId}.",
                            $"{CommandRegistry.ProgramName} ls {parsed.Positionals[0]} lists the top level of the snapshot");
                    }

                    var entries = node.IsDirectory ? node.Children : [node];
                    if (json)
                    {
                        var document = new
                        {
                            snapshotId = tree.SnapshotId,
                            manifestPath = tree.ManifestPath,
                            group = tree.Group,
                            createdUtc = tree.CreatedUtc,
                            path = node.RelativePath,
                            entries = entries.Select(e => new
                            {
                                name = e.Name,
                                relativePath = e.RelativePath,
                                type = e.IsDirectory ? "directory" : "file",
                                sizeBytes = e.SizeBytes,
                                fileCount = e.IsDirectory ? e.FileCount : (int?)null,
                                lastModifiedUtc = e.LastModifiedUtc,
                                contentHash = e.IsDirectory ? null : e.ContentHash
                            })
                        };
                        await ctx.WriteJsonAsync(document).ConfigureAwait(false);
                        return ExitCodes.Success;
                    }

                    Console.WriteLine($"Snapshot {tree.SnapshotId} ({tree.CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)} UTC) of {tree.Group}");
                    Console.WriteLine($"/{node.RelativePath}");
                    foreach (var entry in entries)
                    {
                        var time = entry.LastModifiedUtc is { } t ? $"{t.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}" : new string(' ', 19);
                        var label = entry.IsDirectory ? entry.Name + "/" : entry.Name;
                        Console.WriteLine($"  {(entry.IsDirectory ? "d" : "-")}  {ByteFormatter.Format(entry.SizeBytes),10}  {time}  {label}");
                    }

                    Console.WriteLine($"{entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}");
                    return ExitCodes.Success;
                }

                case "versions":
                {
                    var history = await browser.GetFileVersionsAsync(parsed.Positionals[0], parsed.Positionals[1], cancellationToken).ConfigureAwait(false);
                    foreach (var warning in history.Warnings)
                    {
                        Console.Error.WriteLine($"Warning: could not read {warning}");
                    }

                    if (history.Versions.Count == 0)
                    {
                        throw history.SnapshotsScanned == 0
                            ? CliCommandException.NotFound(
                                $"No snapshots found for group '{parsed.Positionals[0]}'. Run 'snapshots' to list the groups.",
                                $"{CommandRegistry.ProgramName} snapshots lists the groups and their snapshots")
                            : CliCommandException.NotFound(
                                $"'{parsed.Positionals[1]}' is not in any of the {history.SnapshotsScanned} snapshot(s) of this group.",
                                $"{CommandRegistry.ProgramName} ls <snapshot> lists the files of a snapshot");
                    }

                    if (json)
                    {
                        var document = new
                        {
                            group = parsed.Positionals[0],
                            relativePath = parsed.Positionals[1],
                            snapshotsScanned = history.SnapshotsScanned,
                            warnings = history.Warnings,
                            versions = history.Versions.Select(v => new
                            {
                                snapshotId = v.SnapshotId,
                                manifestPath = v.ManifestPath,
                                snapshotCreatedUtc = v.SnapshotCreatedUtc,
                                sizeBytes = v.SizeBytes,
                                lastModifiedUtc = v.LastModifiedUtc,
                                contentHash = v.ContentHash,
                                snapshotCount = v.SnapshotCount
                            })
                        };
                        await ctx.WriteJsonAsync(document).ConfigureAwait(false);
                        return ExitCodes.Success;
                    }

                    Console.WriteLine($"{parsed.Positionals[1]}: {history.Versions.Count} version(s) across {history.SnapshotsScanned} snapshot(s), newest first");
                    foreach (var version in history.Versions)
                    {
                        Console.WriteLine(
                            $"  {version.SnapshotCreatedUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)} UTC  {ByteFormatter.Format(version.SizeBytes),10}  modified {version.LastModifiedUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}  x{version.SnapshotCount}  {version.ManifestPath}");
                    }

                    return ExitCodes.Success;
                }

                default:
                {
                    var diff = await browser.DiffAsync(parsed.Positionals[0], parsed.Positionals[1], cancellationToken).ConfigureAwait(false);
                    if (json)
                    {
                        static object Entries(IEnumerable<SnapshotDiffEntry> entries) => entries.Select(e => new
                        {
                            relativePath = e.RelativePath,
                            relativePathB = e.RelativePathB,
                            caseRename = e.CaseRename,
                            sizeBytesA = e.SizeBytesA,
                            sizeBytesB = e.SizeBytesB,
                            lastModifiedUtcA = e.LastModifiedUtcA,
                            lastModifiedUtcB = e.LastModifiedUtcB
                        }).ToList();

                        var document = new
                        {
                            a = new { snapshotId = diff.SnapshotIdA, createdUtc = diff.CreatedUtcA },
                            b = new { snapshotId = diff.SnapshotIdB, createdUtc = diff.CreatedUtcB },
                            addedCount = diff.Added.Count,
                            removedCount = diff.Removed.Count,
                            changedCount = diff.Changed.Count,
                            unchangedCount = diff.UnchangedCount,
                            added = Entries(diff.Added),
                            removed = Entries(diff.Removed),
                            changed = Entries(diff.Changed)
                        };
                        await ctx.WriteJsonAsync(document).ConfigureAwait(false);
                        return ExitCodes.Success;
                    }

                    Console.WriteLine($"A: {diff.SnapshotIdA} ({diff.CreatedUtcA.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)} UTC)");
                    Console.WriteLine($"B: {diff.SnapshotIdB} ({diff.CreatedUtcB.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)} UTC)");
                    Console.WriteLine($"Added {diff.Added.Count}, removed {diff.Removed.Count}, changed {diff.Changed.Count}, unchanged {diff.UnchangedCount}");
                    foreach (var entry in diff.Added)
                    {
                        Console.WriteLine($"  + {entry.RelativePath}  ({ByteFormatter.Format(entry.SizeBytesB ?? 0)})");
                    }

                    foreach (var entry in diff.Removed)
                    {
                        Console.WriteLine($"  - {entry.RelativePath}  ({ByteFormatter.Format(entry.SizeBytesA ?? 0)})");
                    }

                    foreach (var entry in diff.Changed)
                    {
                        var renamed = entry.CaseRename ? $" -> {entry.RelativePathB} [letter case only differs]" : string.Empty;
                        Console.WriteLine($"  ~ {entry.RelativePath}{renamed}  ({ByteFormatter.Format(entry.SizeBytesA ?? 0)} -> {ByteFormatter.Format(entry.SizeBytesB ?? 0)})");
                    }

                    return ExitCodes.Success;
                }
            }
        }

        private static async Task<int> RunAdoptAsync(CliContext ctx)
        {
            var parsed = ctx.Parsed;
            var configManager = ctx.Config;
            var logger = ctx.Logger;
            var systemState = ctx.State;
            var cancellationToken = ctx.Cancellation;
            var storageType = ctx.StorageOverride ?? configManager.GlobalStorageType;
            var mapping = PathMapping.Parse(parsed.Options.TryGetValue("--map", out var maps) ? maps : []);
            using var storage = await configManager.CreateStorageAsync(storageType).ConfigureAwait(false);

            var discovered = await new RepositoryDiscovery(logger).DiscoverAsync(storage, CreateCliPasswordProvider(configManager), cancellationToken).ConfigureAwait(false);
            if (discovered.Groups.Count == 0)
            {
                logger.Log($"No backups found in storage '{storageType}'; nothing to adopt.", LogLevel.Warning);
                if (ctx.Json)
                {
                    await ctx.WriteJsonAsync(new { storage = storageType, adopted = 0, alreadyKnown = 0, failed = 0, messages = Array.Empty<string>() }).ConfigureAwait(false);
                }

                return ExitCodes.Success;
            }

            var adopted = await new RepositoryAdopter(logger, configManager, systemState, null, CreateCliPasswordProvider(configManager)).AdoptAsync(storage, storageType, discovered.Groups, mapping, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Adopted {adopted.SnapshotsAdopted} snapshot(s); {adopted.SnapshotsAlreadyKnown} were already known; {adopted.SnapshotsFailed} failed.");
            foreach (var message in adopted.Messages)
            {
                Console.WriteLine($"  {message}");
            }

            var adoptExit = adopted.SnapshotsFailed > 0 ? ExitCodes.Failure : ExitCodes.Success;
            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new
                {
                    storage = storageType,
                    adopted = adopted.SnapshotsAdopted,
                    alreadyKnown = adopted.SnapshotsAlreadyKnown,
                    failed = adopted.SnapshotsFailed,
                    messages = adopted.Messages
                }, adoptExit).ConfigureAwait(false);
            }

            return adoptExit;
        }

        /// <summary>export-key / import-key: copies the repository key file (wrapped slots only) out of / back into the storage.</summary>
        private static async Task<int> RunKeyFileTransferAsync(CliContext ctx)
        {
            var parsed = ctx.Parsed;
            var configManager = ctx.Config;
            var logger = ctx.Logger;
            var systemState = ctx.State;
            var cancellationToken = ctx.Cancellation;
            var storageType = ctx.StorageOverride ?? configManager.GlobalStorageType;
            using var storage = await configManager.CreateStorageAsync(storageType).ConfigureAwait(false);
            var path = parsed.Positionals[0];
            try
            {
                if (parsed.Command == "export-key")
                {
                    // Shared lease: the key file is not rewritten while it is being read.
                    var read = await RepositoryCoordinator.Instance.AcquireReadAsync("Exporting a key file", cancellationToken).ConfigureAwait(false);
                    await using var readScope = read.ConfigureAwait(false);
                    await File.WriteAllBytesAsync(path, await RepositoryKeyStore.ExportAsync(storage, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                    Console.WriteLine($"Key file saved to '{path}'. It contains no plaintext secret, but keep it offline: without it encrypted v3 backups cannot be read.");
                    if (ctx.Json)
                    {
                        await ctx.WriteJsonAsync(new { action = "export", storage = storageType, file = Path.GetFullPath(path) }).ConfigureAwait(false);
                    }
                }
                else
                {
                    var keyBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                    var mutation = await RepositoryCoordinator.Instance.AcquireMutationAsync("Importing a key file", cancellationToken, systemState).ConfigureAwait(false);
                    await using var mutationScope = mutation.ConfigureAwait(false);
                    await RepositoryKeyStore.ImportAsync(storage, keyBytes, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine("Key file restored to the storage.");
                    if (ctx.Json)
                    {
                        await ctx.WriteJsonAsync(new { action = "import", storage = storageType, file = Path.GetFullPath(path) }).ConfigureAwait(false);
                    }
                }

                return ExitCodes.Success;
            }
            catch (Exception ex) when (ex is not RepositoryBusyException && ex is InvalidOperationException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                logger.Log(ex.Message, LogLevel.Error);
                return ExitCodes.Failure;
            }
        }

        /// <summary>
        /// change-password / recovery-key for encrypted v3 repositories: only the key file's slots change, never a chunk or manifest.
        /// </summary>
        private static async Task<int> RunKeyCommandAsync(CliContext ctx)
        {
            var parsed = ctx.Parsed;
            var configManager = ctx.Config;
            var logger = ctx.Logger;
            var systemState = ctx.State;
            var cancellationToken = ctx.Cancellation;
            var storageType = ctx.StorageOverride ?? configManager.GlobalStorageType;
            using var storage = await configManager.CreateStorageAsync(storageType).ConfigureAwait(false);

            var keyFile = await RepositoryKeyStore.ReadAsync(storage, cancellationToken).ConfigureAwait(false);
            if (keyFile == null)
            {
                logger.Log("This storage has no repository key file: it is not an encrypted v3 repository. "
                    + "Passwords of encrypted v2 repositories cannot be changed without re-uploading (set chunkDiffing.manifestVersion to 3 and back up again to upgrade).", LogLevel.Error);
                return ExitCodes.Failure;
            }

            var provider = CreateCliPasswordProvider(configManager);
            var current = await provider.GetPasswordAsync(PasswordPurpose.Decrypt).ConfigureAwait(false);
            if (string.IsNullOrEmpty(current))
            {
                logger.Log("The current password is required.", LogLevel.Error);
                return ExitCodes.Failure;
            }

            try
            {
                if (parsed.Command == "recovery-key")
                {
                    var mutation = await RepositoryCoordinator.Instance.AcquireMutationAsync("Adding a recovery key", cancellationToken, systemState).ConfigureAwait(false);
                    await using var mutationScope = mutation.ConfigureAwait(false);
                    var recoveryKey = await RepositoryKeyStore.AddRecoveryKeyAsync(storage, current, cancellationToken).ConfigureAwait(false);

                    // The key is this command's result and exists nowhere else: --quiet (which silences Console.Out) must never swallow it.
                    // The warning goes to stderr; the key goes to stdout (plain) or into the JSON document.
                    Console.Error.WriteLine("Recovery key created. It is shown once: store it somewhere safe; it unlocks this repository without the password.");
                    if (ctx.Json)
                    {
                        await ctx.WriteJsonAsync(new { storage = storageType, recoveryKey }).ConfigureAwait(false);
                    }
                    else
                    {
                        await ctx.JsonOut.WriteLineAsync(recoveryKey).ConfigureAwait(false);
                    }

                    return ExitCodes.Success;
                }

                var next = Environment.GetEnvironmentVariable("RESTORE_NEW_ENCRYPTION_PASSWORD");
                if (string.IsNullOrWhiteSpace(next))
                {
                    if (CliRuntime.NonInteractive)
                    {
                        throw CliCommandException.NeedsUser(
                            "The new encryption password is required but cannot be asked for without a terminal.",
                            "set RESTORE_NEW_ENCRYPTION_PASSWORD to the new password");
                    }

                    Console.Error.Write("Enter new encryption password: ");
                    next = ReadPasswordFromConsole();
                    Console.Error.Write("Confirm new encryption password: ");
                    if (!string.Equals(next, ReadPasswordFromConsole(), StringComparison.Ordinal))
                    {
                        logger.Log("The passwords do not match.", LogLevel.Error);
                        return ExitCodes.Failure;
                    }
                }

                var result = await RepositoryPasswordService.ChangePasswordAsync(
                    storage, configManager, logger, current, next, createRecoveryKey: false, systemState, null, cancellationToken).ConfigureAwait(false);
                if (!result.Changed)
                {
                    logger.Log("This storage has no repository key file: it is not an encrypted v3 repository.", LogLevel.Error);
                    return ExitCodes.Failure;
                }

                if (RememberedPassword.Exists)
                {
                    // A remembered password would be the old one: keep "unlock --remember" working with the new one.
                    try
                    {
                        RememberedPassword.Save(next, CliServices.SecretProtector);
                        Console.WriteLine("The remembered password was updated to the new one.");
                    }
                    catch (PlatformNotSupportedException)
                    {
                        RememberedPassword.Clear();
                        Console.WriteLine("The remembered password was removed (it cannot be updated on this platform).");
                    }
                }

                Console.WriteLine("Tip: keep an offline copy of the key file now: restore export-key <file>.");
                Console.WriteLine("Password changed. Other computers must update their configured password. Snapshots written before the encrypted v3 upgrade (v2) still need the old password.");
                if (ctx.Json)
                {
                    await ctx.WriteJsonAsync(new { storage = storageType, passwordChanged = true }).ConfigureAwait(false);
                }

                return ExitCodes.Success;
            }
            catch (Exception ex) when (ex is WrongRepositoryPasswordException or ArgumentException)
            {
                logger.Log(ex.Message, LogLevel.Error);
                return ExitCodes.Failure;
            }
        }

        private static async Task<int> RunRepairAsync(CliContext ctx)
        {
            var parsed = ctx.Parsed;
            var configManager = ctx.Config;
            var logger = ctx.Logger;
            var systemState = ctx.State;
            var cancellationToken = ctx.Cancellation;
            var storageType = ctx.StorageOverride ?? configManager.GlobalStorageType;
            var options = new RepairOptions
            {
                DryRun = parsed.HasOption("--dry-run"),
                GracePeriod = parsed.GetOption("--grace-hours") is { } hours
                    ? TimeSpan.FromHours(double.Parse(hours, System.Globalization.CultureInfo.InvariantCulture))
                    : RepairOptions.DefaultGracePeriod
            };

            const string repairHint = "re-run with --yes to confirm in advance (after a --dry-run preview)";
            if (!options.DryRun && !CliRuntime.Yes && CliRuntime.NonInteractive)
            {
                CliSupport.Confirm("Repair permanently deletes unreferenced chunks from the storage.", repairHint);
            }

            if (!options.DryRun)
            {
                Console.WriteLine("Tip: run with --dry-run first to preview what would be deleted. Repair should run while other machines sharing this storage are idle.");
            }

            using var storage = await configManager.CreateStorageAsync(storageType).ConfigureAwait(false);
            RepairReport report;
            try
            {
                if (!options.DryRun && !CliRuntime.Yes)
                {
                    // Interactive: a preview pass (deletes nothing) names what the real run would delete.
                    var preview = await new RepositoryRepair(logger, configManager, systemState, null, CreateCliPasswordProvider(configManager))
                        .RunAsync(storage, storageType, new RepairOptions { DryRun = true, GracePeriod = options.GracePeriod }, null, cancellationToken).ConfigureAwait(false);
                    CliSupport.ConfirmOrAbort(
                        $"Delete {preview.OrphanChunks} unreferenced chunk(s) ({ByteFormatter.Format(preview.OrphanBytes)}) from the {storageType} storage?",
                        repairHint);
                }

                report = await new RepositoryRepair(logger, configManager, systemState, null, CreateCliPasswordProvider(configManager))
                    .RunAsync(storage, storageType, options, new Progress<string>(message => Console.WriteLine(message)), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RepositoryRepairAbortedException or NotSupportedException)
            {
                logger.Log(ex.Message, LogLevel.Error);
                return ExitCodes.Failure;
            }

            Console.WriteLine();
            Console.WriteLine(report.DryRun ? "Repair preview (nothing was changed):" : "Repair finished:");
            Console.WriteLine($"  Snapshots scanned      : {report.ManifestsScanned}");
            Console.WriteLine($"  Chunks in storage      : {report.ChunksListed} ({report.ChunksReferenced} referenced)");
            Console.WriteLine($"  Orphan chunks          : {report.OrphanChunks} ({ByteFormatter.Format(report.OrphanBytes)})");
            Console.WriteLine($"  Kept (grace/unknown age): {report.ChunksKeptInGrace}");
            if (!report.DryRun)
            {
                Console.WriteLine($"  Deleted                : {report.ChunksDeleted} ({ByteFormatter.Format(report.BytesFreed)}), {report.ChunksDeleteFailed} failed");
                Console.WriteLine($"  Snapshots re-adopted   : {report.OrphanSnapshotsAdopted}");
                Console.WriteLine($"  Pending deletes cleared: {report.PendingDeletesCompleted}");
            }

            foreach (var message in report.Messages)
            {
                Console.WriteLine($"  {message}");
            }

            var repairExit = report.ChunksDeleteFailed > 0 ? ExitCodes.Failure : ExitCodes.Success;
            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new
                {
                    storage = storageType,
                    dryRun = report.DryRun,
                    manifestsScanned = report.ManifestsScanned,
                    chunksListed = report.ChunksListed,
                    chunksReferenced = report.ChunksReferenced,
                    orphanChunks = report.OrphanChunks,
                    orphanBytes = report.OrphanBytes,
                    chunksKeptInGrace = report.ChunksKeptInGrace,
                    chunksDeleted = report.ChunksDeleted,
                    chunksDeleteFailed = report.ChunksDeleteFailed,
                    bytesFreed = report.BytesFreed,
                    orphanSnapshotsAdopted = report.OrphanSnapshotsAdopted,
                    pendingDeletesCompleted = report.PendingDeletesCompleted,
                    messages = report.Messages
                }, repairExit).ConfigureAwait(false);
            }

            return repairExit;
        }

        private static string TelemetryFingerprint(SystemState state) =>
            System.Text.Json.JsonSerializer.Serialize(new { state.Telemetry, state.SnapshotVerificationTimes });

        /// <summary>
        /// Service mode never prompts. If backups will be encrypted and no password is available from the
        /// environment, returns the message to fail with; otherwise null.
        /// </summary>
        internal static string? GetServicePasswordError(IConfigManager config, string command = "--service")
        {
            if (!config.Encryption.Enabled
                || CliPasswordSources.TryGetConfiguredPassword(config) != null)
            {
                return null;
            }

            return $"Encryption is enabled but no password is available. {command} never prompts: set the {PASSWORD_ENVIRONMENT_VARIABLE} environment variable "
                + $"(or {CliPasswordSources.PasswordFileVariable} to a file whose first line is the password), or run 'restore.exe unlock --remember' once (Windows), and start again.";
        }

        /// <summary>
        /// Final state save. Mutating commands already saved under their lease; this captures telemetry and the
        /// last scheduler cycle, and takes the lease so state another process changed is reloaded first. A
        /// read-only command only tries briefly: if the repository is busy it logs a warning, skips the save
        /// (telemetry is best effort) and still succeeds. Returns false when a required save failed.
        /// </summary>
        internal static async Task<bool> SaveStateAtExitAsync(
            SystemState systemState, ILogger logger, bool mutatedState, RepositoryCoordinator coordinator) =>
            await SaveStateAtExitOutcomeAsync(systemState, logger, mutatedState, coordinator).ConfigureAwait(false) == StateSaveOutcome.Saved;

        internal enum StateSaveOutcome
        {
            Saved,

            /// <summary>The repository stayed busy for the whole wait (<c>--wait</c>): exit 5, not a generic failure.</summary>
            Busy,
            Failed
        }

        internal static async Task<StateSaveOutcome> SaveStateAtExitOutcomeAsync(
            SystemState systemState, ILogger logger, bool mutatedState, RepositoryCoordinator coordinator)
        {
            try
            {
                var lease = mutatedState
                    ? await coordinator.AcquireMutationAsync("Saving system state", default, systemState).ConfigureAwait(false)
                    : await coordinator.TryAcquireMutationAsync(TimeSpan.FromSeconds(2), "Saving system state", default, systemState).ConfigureAwait(false);

                if (lease == null)
                {
                    logger.Log("Another operation is running; restore/verify telemetry was not saved.", LogLevel.Warning);
                    return StateSaveOutcome.Saved;
                }

                await using var leaseScope = lease.ConfigureAwait(false);
                await systemState.SaveStateAsync().ConfigureAwait(false);
                return StateSaveOutcome.Saved;
            }
            catch (Exception saveEx)
            {
                logger.Log($"Failed to save system state: {saveEx.Message}", LogLevel.Error);
                return FindCause<RepositoryBusyException>(saveEx) != null ? StateSaveOutcome.Busy : StateSaveOutcome.Failed;
            }
        }

        private static bool TryParseConflictValue(string? value, out RestoreConflictPolicy policy)
        {
            policy = RestoreConflictPolicy.Skip;
            if (value == null)
            {
                return true;
            }

            switch (value.ToLowerInvariant())
            {
                case "skip":
                    return true;
                case "overwrite":
                    policy = RestoreConflictPolicy.Overwrite;
                    return true;
                case "keepboth":
                    policy = RestoreConflictPolicy.KeepBoth;
                    return true;
                case "fail":
                    policy = RestoreConflictPolicy.Fail;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryParseConflictPolicy(string[] arguments, out RestoreConflictPolicy policy)
        {
            // Safest default: never replace a file the user already has.
            policy = RestoreConflictPolicy.Skip;

            var index = Array.FindIndex(arguments, a => a.Equals("--conflict", StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return true;
            }

            if (index + 1 >= arguments.Length)
            {
                return false;
            }

            switch (arguments[index + 1].ToLowerInvariant())
            {
                case "skip":
                    policy = RestoreConflictPolicy.Skip;
                    return true;
                case "overwrite":
                    policy = RestoreConflictPolicy.Overwrite;
                    return true;
                case "keepboth":
                    policy = RestoreConflictPolicy.KeepBoth;
                    return true;
                case "fail":
                    policy = RestoreConflictPolicy.Fail;
                    return true;
                default:
                    return false;
            }
        }

        private static List<string> GetRepeatedOptionValues(string[] arguments, string optionName)
        {
            var values = new List<string>();

            for (var index = 0; index < arguments.Length - 1; index++)
            {
                if (arguments[index].Equals(optionName, StringComparison.OrdinalIgnoreCase)
                    && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    values.Add(arguments[index + 1]);
                }
            }

            return values;
        }

        private static HashSet<string> FilterByPatterns(RestorePreview preview, List<string> patterns)
        {
            return preview.Entries
                .Select(entry => entry.RelativePath)
                .Where(relativePath => patterns.Any(pattern => MatchesGlob(relativePath, pattern)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Glob match over '/'-separated manifest paths. '**' spans separators, '*' and '?'
        /// do not. Note: this is the <c>restore --include</c> matcher over manifest-relative paths and differs from the
        /// backup exclusion matcher (<c>ExcludedPatterns</c>/<c>ExcludedPaths</c>, which match absolute, OS-separated
        /// paths and file names); the two are intentionally not unified.
        /// </summary>
        internal static bool MatchesGlob(string relativePath, string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return false;
            }

            var normalizedPath = relativePath.Replace('\\', '/');
            var normalizedPattern = pattern.Replace('\\', '/').TrimStart('/');

            var regex = new System.Text.StringBuilder("^");
            for (var index = 0; index < normalizedPattern.Length; index++)
            {
                var character = normalizedPattern[index];
                switch (character)
                {
                    case '/' when normalizedPattern.AsSpan(index + 1).StartsWith("**"):
                        // "/**" makes the separator itself optional, so "docs/**" matches
                        // "docs" as well as everything beneath it.
                        regex.Append("(?:/.*)?");
                        index += 2;

                        // "docs/**/x" should also match "docs/x"; consume the separator that
                        // the optional group already accounts for.
                        if (index + 1 < normalizedPattern.Length && normalizedPattern[index + 1] == '/')
                        {
                            index++;
                            regex.Append('/');
                        }
                        break;
                    case '*':
                        if (index + 1 < normalizedPattern.Length && normalizedPattern[index + 1] == '*')
                        {
                            regex.Append(".*");
                            index++;
                        }
                        else
                        {
                            regex.Append("[^/]*");
                        }
                        break;
                    case '?':
                        regex.Append("[^/]");
                        break;
                    default:
                        regex.Append(System.Text.RegularExpressions.Regex.Escape(character.ToString()));
                        break;
                }
            }

            regex.Append('$');

            return System.Text.RegularExpressions.Regex.IsMatch(
                normalizedPath,
                regex.ToString(),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static void PrintRestorePreview(
            RestorePreview preview,
            IReadOnlySet<string>? selectedPaths,
            RestoreConflictPolicy conflictPolicy)
        {
            var entries = selectedPaths == null
                ? preview.Entries
                : [.. preview.Entries.Where(entry => selectedPaths.Contains(entry.RelativePath))];

            var totalBytes = entries.Sum(entry => entry.SizeBytes);
            var differing = entries.Count(entry => entry.Conflict == RestoreConflictKind.Differs);
            var identical = entries.Count(entry => entry.Conflict == RestoreConflictKind.Identical);

            Console.WriteLine();
            Console.WriteLine($"Restore preview for snapshot {preview.SnapshotId} ({preview.SnapshotCreatedUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)} UTC)");
            Console.WriteLine($"  Target directory : {preview.TargetDirectory}");
            Console.WriteLine($"  Files to restore : {entries.Count}");
            Console.WriteLine($"  Total size       : {ByteFormatter.Format(totalBytes)}");
            Console.WriteLine($"  Already present  : {identical} identical, {differing} differing");
            Console.WriteLine($"  Conflict policy  : {conflictPolicy}");

            if (preview.FilesFilteredOut > 0 || (selectedPaths != null && preview.FileCount != entries.Count))
            {
                Console.WriteLine($"  Excluded by filter: {preview.FileCount - entries.Count + preview.FilesFilteredOut}");
            }

            if (differing > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"  These existing files differ from the snapshot ({conflictPolicy} will be applied):");
                foreach (var entry in entries.Where(e => e.Conflict == RestoreConflictKind.Differs).Take(10))
                {
                    Console.WriteLine($"    {entry.RelativePath} (on disk {ByteFormatter.Format(entry.ExistingSizeBytes ?? 0)}, snapshot {ByteFormatter.Format(entry.SizeBytes)})");
                }

                if (differing > 10)
                {
                    Console.WriteLine($"    ...and {differing - 10} more.");
                }
            }

            Console.WriteLine();
        }

        private static IProgress<BackupProgress> CreateConsoleBackupProgress()
        {
            var lastReport = string.Empty;

            return new Progress<BackupProgress>(progress =>
            {
                var line = $"[{progress.Phase}] {progress.FilesDone}/{progress.FilesTotal} files ({progress.Fraction:P0})";
                if (line == lastReport)
                {
                    return;
                }

                lastReport = line;
                Console.WriteLine(line);
            });
        }

        private static IProgress<RestoreProgress> CreateConsoleRestoreProgress()
        {
            var lastReport = string.Empty;

            return new Progress<RestoreProgress>(progress =>
            {
                var line = $"[{progress.Phase}] {progress.FilesDone}/{progress.FilesTotal} files ({progress.Fraction:P0})";
                if (line == lastReport)
                {
                    return;
                }

                lastReport = line;
                Console.WriteLine(line);
            });
        }

        private static string? GetStorageOverride(string[] arguments)
        {
            var storageIndex = Array.FindIndex(arguments, a => a.Equals("--storage", StringComparison.OrdinalIgnoreCase));
            if (storageIndex >= 0 && storageIndex + 1 < arguments.Length)
            {
                return arguments[storageIndex + 1];
            }

            return null;
        }

        private const string PASSWORD_ENVIRONMENT_VARIABLE = CliPasswordSources.PasswordVariable;

        private static IPasswordProvider CreateCliPasswordProvider(IConfigManager config) =>
            CreateCliPasswordProviderCore(config, allowPrompt: true);

        private static IPasswordProvider CreateCliPasswordProviderCore(IConfigManager config, bool allowPrompt)
        {
            // Decrypt-mode requests are not gated on config: restore/verify use the snapshot's own
            // EncryptionEnabled/salt/iterations from its manifest, even if config.json differs.
            return new VerifiedPasswordProvider(
                config,
                new Logger(),
                request => PromptForPasswordAsync(request, allowPrompt, config),
                message => Console.Error.WriteLine(message));
        }

        internal static Task<string?> PromptForPasswordAsync(PasswordPromptRequest request, bool allowPrompt = true, IConfigManager? config = null)
        {
            // Sources that need nobody: --password-stdin, the environment variable, the password file, the remembered password.
            if (CliPasswordSources.TryGetConfiguredPassword(config) is { } configured)
            {
                return Task.FromResult<string?>(configured);
            }

            if (!allowPrompt)
            {
                // Unattended (--service): never block on the console.
                return Task.FromResult<string?>(null);
            }

            if (CliRuntime.NonInteractive)
            {
                throw PasswordNeededException();
            }

            Console.Error.Write(request.IsConfirmation
                ? "Confirm encryption password: "
                : "Enter encryption password: ");
            return Task.FromResult<string?>(ReadPasswordFromConsole());
        }

        private static CliCommandException PasswordNeededException()
        {
            const string message = "The encryption password is required but cannot be asked for without a terminal.";
            CliRuntime.PasswordUnavailableMessage = message;
            return CliCommandException.NeedsUser(message, CliErrorCodes.PasswordHint);
        }

        internal static string ReadPasswordFromConsole()
        {
            var password = new System.Text.StringBuilder();
            while (true)
            {
                ConsoleKeyInfo key;
                try
                {
                    key = Console.ReadKey(intercept: true);
                }
                catch (InvalidOperationException)
                {
                    // stdin is redirected or there is no console: nobody can type the password.
                    throw PasswordNeededException();
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    Console.Error.WriteLine();
                    break;
                }
                if (key.Key == ConsoleKey.Backspace && password.Length > 0)
                {
                    password.Length--;
                    Console.Error.Write("\b \b");
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    password.Append(key.KeyChar);
                    Console.Error.Write('*');
                }
            }
            return password.ToString();
        }

        private static void PrintConfigurationLifecycleSummary(
            ConfigSetupResult setupResult,
            ConfigMigrationResult? migrationResult,
            Logger logger)
        {
            if (setupResult.ConfigCreated)
            {
                logger.Log($"Configuration initialized at: {ConfigInitializer.GetUserConfigPath()}", LogLevel.Info);
            }

            if (setupResult.ExampleConfigUpdated)
            {
                logger.Log($"Updated local config.example.json reference at: {ConfigInitializer.GetUserExampleConfigPath()}", LogLevel.Debug);
            }

            if (migrationResult == null)
            {
                return;
            }

            if (migrationResult.MigrationApplied)
            {
                logger.Log(
                    $"Configuration migration applied ({migrationResult.SourceSchemaVersion} -> {migrationResult.TargetSchemaVersion}).",
                    LogLevel.Info);

                if (!string.IsNullOrWhiteSpace(migrationResult.BackupPath))
                {
                    logger.Log($"Migration backup file: {migrationResult.BackupPath}", LogLevel.Info);
                }
            }

            foreach (var warning in migrationResult.Warnings)
            {
                logger.Log(warning, LogLevel.Warning);
            }
        }

        private static async Task<int> RunConfigAsync(CliContext ctx)
        {
            var parsed = ctx.Parsed;
            var configManager = ctx.Config;
            var logger = ctx.Logger;
            var output = ctx.JsonOut;

            if (parsed.Command == "config show")
            {
                var json = await File.ReadAllTextAsync(configManager.GetConfigFilePath()).ConfigureAwait(false);
                var shown = parsed.HasOption("--reveal") ? json : ConfigSecretsCommands.RedactConfigJson(json);
                if (ctx.Json)
                {
                    await ctx.WriteJsonAsync(new
                    {
                        file = configManager.GetConfigFilePath(),
                        secretsRevealed = parsed.HasOption("--reveal"),
                        config = System.Text.Json.Nodes.JsonNode.Parse(shown)
                    }).ConfigureAwait(false);
                }
                else
                {
                    output.WriteLine(shown);
                }

                return ExitCodes.Success;
            }

            if (parsed.Command == "config export")
            {
                var exportFile = Path.GetFullPath(parsed.Positionals[0]);
                await File.WriteAllTextAsync(exportFile, await ConfigPortability.ExportAsync(configManager).ConfigureAwait(false)).ConfigureAwait(false);
                if (ctx.Json)
                {
                    await ctx.WriteJsonAsync(new { file = exportFile, secretsRemoved = true }).ConfigureAwait(false);
                }
                else
                {
                    output.WriteLine($"Exported settings to {exportFile} (secret values removed; re-enter them after importing on another machine).");
                }

                return ExitCodes.Success;
            }

            if (parsed.Command == "config import")
            {
                var importMode = parsed.HasOption("--replace") ? ConfigImportMode.Replace : ConfigImportMode.Merge;
                var importResult = await ConfigPortability.ImportAsync(configManager, Path.GetFullPath(parsed.Positionals[0]), importMode).ConfigureAwait(false);
                if (!ctx.Json)
                {
                    foreach (var warning in importResult.Warnings)
                    {
                        output.WriteLine($"Warning: {warning}");
                    }
                }

                if (!importResult.Applied)
                {
                    foreach (var importError in importResult.Errors)
                    {
                        logger.Log($"Import failed: {importError}", LogLevel.Error);
                    }

                    if (ctx.Json)
                    {
                        await ctx.WriteJsonAsync(
                            new { applied = false, mode = importMode.ToString().ToLowerInvariant(), errors = importResult.Errors, warnings = importResult.Warnings },
                            ExitCodes.Config).ConfigureAwait(false);
                    }
                    else
                    {
                        output.WriteLine("Nothing was imported; the current configuration is unchanged.");
                    }

                    return ExitCodes.Config;
                }

                if (ctx.Json)
                {
                    await ctx.WriteJsonAsync(new
                    {
                        applied = true,
                        mode = importMode.ToString().ToLowerInvariant(),
                        warnings = importResult.Warnings,
                        notes = importResult.Notes
                    }).ConfigureAwait(false);
                    return ExitCodes.Success;
                }

                foreach (var note in importResult.Notes)
                {
                    output.WriteLine(note);
                }

                output.WriteLine($"Imported settings ({importMode.ToString().ToLowerInvariant()}).");
                return ExitCodes.Success;
            }

            if (!DpapiSecretProtector.Instance.IsAvailable)
            {
                logger.Log("'config protect' needs Windows DPAPI and is only supported on Windows. Use env:NAME references on other platforms.", LogLevel.Error);
                return ExitCodes.Failure;
            }

            var changed = await ConfigSecretsCommands.ProtectAsync(configManager).ConfigureAwait(false);
            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new { changed = changed.Count, options = changed }).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            output.WriteLine(changed.Count == 0
                ? "No plain secret options found; nothing changed."
                : $"Encrypted {changed.Count} secret option(s) for this Windows user: {string.Join(", ", changed)}");
            return ExitCodes.Success;
        }

        private static async Task<int> RunProbeAsync(CliContext ctx)
        {
            var configManager = ctx.Config;
            var storageType = ctx.StorageOverride ?? configManager.GlobalStorageType;
            var json = ctx.Json;
            var logger = ctx.Logger;
            using var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            Console.CancelKeyPress += onCancel;
            try
            {
                StorageProbeResult result;
                try
                {
                    using var storage = await configManager.CreateStorageAsync(storageType).ConfigureAwait(false);
                    result = await StorageProbe.RunAsync(storage, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    logger.Log("Probe cancelled.", LogLevel.Warning);
                    return ExitCodes.Cancelled;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not CliCommandException and not StorageAuthRequiredException)
                {
                    result = new StorageProbeResult(string.Empty, [new StorageProbeStep("connect", false, false, 0, ex.Message)], true);
                }

                var exitCode = result.Success ? ExitCodes.Success : ExitCodes.Failure;
                if (json)
                {
                    await ctx.WriteJsonAsync(
                        new { storage = storageType, success = result.Success, cleanedUp = result.CleanedUp, key = result.Key, steps = result.Steps },
                        exitCode, CliJson.CamelCaseOptions).ConfigureAwait(false);
                }
                else
                {
                    Console.WriteLine($"Storage probe for '{storageType}': {(result.Success ? "passed" : "FAILED")}");
                    Console.WriteLine(result.ToDisplayText());
                }

                return exitCode;
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }
        }

        private static async Task<int> ValidateConfigurationAsync(CliContext ctx)
        {
            var exitCode = ValidateConfigurationCore(ctx.Config, ctx.Logger, out var result);
            if (ctx.Json)
            {
                if (!result.IsValid)
                {
                    ctx.Failure = new CliError(
                        CliErrorCodes.Config, $"Configuration validation failed with {result.Errors.Count} error(s).",
                        "fix the listed errors in config.json, then run 'restore.exe config validate' again", [.. result.Errors.Take(20)]);
                }

                await ctx.WriteJsonAsync(new
                {
                    valid = result.IsValid,
                    configFile = ctx.Config.GetConfigFilePath(),
                    errors = result.Errors,
                    warnings = result.Warnings,
                    info = result.Info
                }, exitCode).ConfigureAwait(false);
            }

            return exitCode;
        }

        private static int ValidateConfiguration(IConfigManager configManager, ILogger logger) =>
            ValidateConfigurationCore(configManager, logger, out _);

        private static int ValidateConfigurationCore(IConfigManager configManager, ILogger logger, out ConfigValidationResult result)
        {
            logger.Log($"Configuration file location: {configManager.GetConfigFilePath()}", LogLevel.Info);
            logger.Log("Running comprehensive configuration validation...", LogLevel.Info);

            result = configManager.ValidateConfiguration();

            PrintValidationResults(result, logger);

            if (result.IsValid)
            {
                Console.WriteLine("\nConfiguration is valid and ready to use!");
                if (result.Warnings.Count > 0)
                {
                    Console.WriteLine($"Found {result.Warnings.Count} warning(s) that should be reviewed.");
                }

                return ExitCodes.Success;
            }

            Console.WriteLine($"\nConfiguration validation failed with {result.Errors.Count} error(s).");
            Console.WriteLine("Please fix the errors above before using ReStore.");
            return ExitCodes.Config;
        }

        /// <summary>
        /// Errors and warnings of a validation pass; the info messages (dozens of "validated" lines) only go to the
        /// debug log. <c>config validate</c> prints everything through <see cref="PrintValidationResults"/>.
        /// </summary>
        private static void PrintValidationIssues(ConfigValidationResult result, ILogger logger)
        {
            PrintValidationResults(new ConfigValidationResult
            {
                IsValid = result.IsValid,
                Errors = result.Errors,
                Warnings = result.Warnings
            }, logger);

            foreach (var info in result.Info)
            {
                logger.Log($"Config Info: {info}", LogLevel.Debug);
            }
        }

        private static void PrintValidationResults(ConfigValidationResult result, ILogger logger)
        {
            if (result.Errors.Count > 0)
            {
                Console.WriteLine("\nERRORS:");
                foreach (var error in result.Errors)
                {
                    Console.WriteLine($"{error}");
                    logger.Log($"Config Error: {error}", LogLevel.Error);
                }
            }

            if (result.Warnings.Count > 0)
            {
                Console.WriteLine("\nWARNINGS:");
                foreach (var warning in result.Warnings)
                {
                    Console.WriteLine($"{warning}");
                    logger.Log($"Config Warning: {warning}", LogLevel.Warning);
                }
            }

            // Print info messages (only in validation mode, not during startup)
            if (result.Info.Count > 0)
            {
                Console.WriteLine("\nINFO:");
                foreach (var info in result.Info)
                {
                    Console.WriteLine($"{info}");
                    logger.Log($"Config Info: {info}", LogLevel.Info);
                }
            }
        }
    }
}
