using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ReStore.Core;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.telemetry;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.cli;

/// <summary>Result words of a doctor check (the JSON <c>status</c> values).</summary>
internal static class DoctorStatus
{
    public const string Ok = "ok";
    public const string Warn = "warn";
    public const string Fail = "fail";
    public const string Skip = "skip";
}

/// <summary>One line of the doctor report: what was checked, how it went and, when something is off, the command that fixes it.</summary>
internal sealed record DoctorCheck(string Id, string Title, string Status, string Detail, string? Fix = null);

/// <summary>What the telemetry check reports; a record so tests can supply it without a telemetry client.</summary>
internal sealed record DoctorTelemetryInfo(bool Enabled, string Reason, string CrashMode, int PendingCrashReports, int QueuedCrashReports);

/// <summary>
/// Everything <c>doctor</c> reaches outside the process for (disk, processes, the scheduler, PATH, the network probe) as replaceable
/// seams, so the checks run on any machine in tests. The defaults are the real, read-only implementations.
/// </summary>
internal sealed class DoctorEnvironment
{
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public bool IsWindows { get; set; } = OperatingSystem.IsWindows();

    public string Channel { get; set; } = AppChannel.Current;

    public string Architecture { get; set; } = AppChannel.Architecture;

    /// <summary>Free bytes on the volume holding the path, or null when unknown.</summary>
    public Func<string, long?> FreeBytes { get; set; } = DefaultFreeBytes;

    /// <summary>True when the ReStore app (tray) runs in this logon session: its single-instance mutex exists.</summary>
    public Func<bool> IsGuiRunning { get; set; } = DefaultIsGuiRunning;

    public Func<CancellationToken, Task<ScheduledTaskStatus>> QueryScheduledTask { get; set; } = DefaultQueryScheduledTaskAsync;

    /// <summary>Every <c>restore.exe</c> found on PATH, in PATH order.</summary>
    public Func<IReadOnlyList<string>> RestoreOnPath { get; set; } = DefaultRestoreOnPath;

    public Func<ILogger, Task<SystemState>> LoadState { get; set; } = DefaultLoadStateAsync;

    public Func<CancellationToken, Task<(bool Busy, string? Reason)>> ProbeBusy { get; set; } =
        token => ProtectionStatusBuilder.ProbeBusyAsync(RepositoryCoordinator.Instance, token);

    public Func<IConfigManager, string, CancellationToken, Task<StorageProbeResult>> ProbeStorage { get; set; } = DefaultProbeStorageAsync;

    public Func<DoctorTelemetryInfo> Telemetry { get; set; } = DefaultTelemetry;

    public Func<IConfigManager, string?> TryGetPassword { get; set; } = config => CliPasswordSources.TryGetConfiguredPassword(config);

    private static long? DefaultFreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool DefaultIsGuiRunning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            // The same name the app's single-instance mutex uses (ReStore.Services.SingleInstanceNames.MutexName).
            if (Mutex.TryOpenExisting("ReStore_SingleInstance_Mutex", out var mutex))
            {
                mutex.Dispose();
                return true;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // Access denied means it exists but belongs to another session or user; treat as not ours.
        }

        return false;
    }

    private static async Task<ScheduledTaskStatus> DefaultQueryScheduledTaskAsync(CancellationToken token) =>
        await new ScheduledTaskManager(OpsEnvironment.Runner).QueryAsync(token).ConfigureAwait(false);

    private static IReadOnlyList<string> DefaultRestoreOnPath()
    {
        var found = new List<string>();
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(entry.Trim('"'), "restore.exe");
                if (File.Exists(candidate))
                {
                    found.Add(candidate);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                // A malformed PATH entry is skipped.
            }
        }

        return found;
    }

    private static async Task<SystemState> DefaultLoadStateAsync(ILogger logger)
    {
        var state = new SystemState(logger);
        await state.LoadStateAsync().ConfigureAwait(false);
        return state;
    }

    private static async Task<StorageProbeResult> DefaultProbeStorageAsync(IConfigManager config, string storageType, CancellationToken token)
    {
        using var storage = await config.CreateStorageAsync(storageType).ConfigureAwait(false);
        return await StorageProbe.RunAsync(storage, token).ConfigureAwait(false);
    }

    private static DoctorTelemetryInfo DefaultTelemetry()
    {
        var client = ReStore.Core.src.telemetry.Telemetry.Client;
        var decision = client.Decision;
        return new DoctorTelemetryInfo(decision.Enabled, decision.Description ?? "unknown", client.CrashMode, client.CrashStore.Count(), client.QueuedCrashCount());
    }
}

/// <summary>
/// <c>restore doctor</c>: one read-only health check, the first thing an agent or a person runs when something is wrong.
/// Each check has an id, a status (ok, warn, fail, skip), a detail and, when something is off, a concrete fix.
/// </summary>
internal static class DoctorCommands
{
    /// <summary>Free space below which the disk check warns.</summary>
    public const long LowDiskBytes = 1L << 30;

    /// <summary>How far back the "recent failures" check looks.</summary>
    public static readonly TimeSpan FailureWindow = TimeSpan.FromDays(7);

    private const int MaxDetailLength = 200;

    internal static Func<DoctorEnvironment> CreateEnvironment { get; set; } = () => new DoctorEnvironment();

    public static IEnumerable<CommandSpec> Specs { get; } =
    [
        new CommandSpec
        {
            Name = "doctor",
            Category = CommandCategory.Info,
            Summary = "Check the health of this ReStore setup: config, storage, password, protection, staleness, failures, PATH, updates.",
            Description = "Read-only: it never changes the configuration, the state or the storage (it writes and deletes one tiny temp file in the data folder to test that it is writable). "
                + "Each check has an id, a title, a status (ok, warn, fail or skip), a detail and, when something is off, a 'fix' (usually a restore command). "
                + "Exit 0 when no check failed (warnings do not fail), 1 otherwise. --probe additionally runs the storage probe (uploads, reads and deletes a tiny object) against every storage in use, which needs network for remote storages. "
                + "The update check uses the cached result only. JSON: summary {ok, warn, fail, skip}, version, channel and checks (id, title, status, detail, fix).",
            Options = [new OptionSpec("--probe", "Also run the storage probe against every storage in use (network for remote storages).")],
            Examples = ["doctor", "doctor --json", "doctor --probe --json"],
            Config = ConfigRequirement.None,
            State = StateRequirement.None,
            PayloadOnStdout = true,
            Handler = DoctorAsync
        }
    ];

    private static async Task<int> DoctorAsync(CliContext ctx)
    {
        var environment = CreateEnvironment();
        var cancellation = CancellationToken.None;

        IConfigManager? config = null;
        string? configError = null;
        if (!File.Exists(AppPaths.ConfigFile))
        {
            configError = $"No configuration file at {AppPaths.ConfigFile}.";
        }
        else
        {
            try
            {
                var manager = new ConfigManager(ctx.Logger);
                await manager.LoadAsync().ConfigureAwait(false);
                config = manager;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                configError = $"config.json could not be loaded: {Describe(ex)}";
            }
        }

        var checks = await new DoctorRunner(environment, ctx.Logger, config, configError)
            .RunAsync(ctx.Parsed.HasOption("--probe"), cancellation).ConfigureAwait(false);

        var failed = checks.Where(c => c.Status == DoctorStatus.Fail).ToList();
        var exitCode = failed.Count == 0 ? Program.ExitCodes.Success : Program.ExitCodes.Failure;
        if (failed.Count > 0)
        {
            ctx.Failure = new CliError(
                CliErrorCodes.Failure,
                $"{failed.Count} health check(s) failed: {string.Join(", ", failed.Select(c => c.Id))}.",
                "see the 'fix' of each failed check",
                [.. failed.Select(c => $"{c.Id}: {c.Detail}").Take(20)]);
        }

        var summary = new
        {
            ok = checks.Count(c => c.Status == DoctorStatus.Ok),
            warn = checks.Count(c => c.Status == DoctorStatus.Warn),
            fail = failed.Count,
            skip = checks.Count(c => c.Status == DoctorStatus.Skip)
        };

        await CliSupport.EmitAsync(
            ctx,
            new
            {
                summary,
                version = CommandRegistry.Version,
                channel = environment.Channel,
                checks = checks.Select(c => new { id = c.Id, title = c.Title, status = c.Status, detail = c.Detail, fix = c.Fix })
            },
            RenderText(checks, summary.ok, summary.warn, summary.fail),
            exitCode).ConfigureAwait(false);
        return exitCode;
    }

    internal static string RenderText(IReadOnlyList<DoctorCheck> checks, int ok, int warn, int fail)
    {
        var text = new StringBuilder();
        foreach (var check in checks)
        {
            var tag = check.Status switch
            {
                DoctorStatus.Ok => "ok  ",
                DoctorStatus.Warn => "warn",
                DoctorStatus.Fail => "FAIL",
                _ => "skip"
            };
            text.AppendLine($"[{tag}] {check.Title}: {check.Detail}");
            if (check.Fix != null && check.Status is DoctorStatus.Warn or DoctorStatus.Fail)
            {
                text.AppendLine($"       fix: {check.Fix}");
            }
        }

        text.Append(CultureInfo.InvariantCulture, $"{checks.Count} checks: {ok} ok, {warn} warning(s), {fail} failed.");
        return text.ToString();
    }

    /// <summary>A short, single-line, secret-masked description of an exception for a check detail (never a stack trace).</summary>
    internal static string Describe(Exception exception)
    {
        var inner = exception.InnerException is { } cause ? $"{exception.Message}: {cause.Message}" : exception.Message;
        return Shorten(inner);
    }

    internal static string Shorten(string text)
    {
        var oneLine = DiagnosticsReport.RedactText(text).ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= MaxDetailLength ? oneLine : oneLine[..MaxDetailLength] + "...";
    }
}

/// <summary>Runs the checks in order. Config- and state-dependent checks are skipped when that part could not be loaded.</summary>
internal sealed class DoctorRunner(DoctorEnvironment environment, ILogger logger, IConfigManager? config, string? configError)
{
    private SystemState? _state;
    private string? _stateError;

    public async Task<IReadOnlyList<DoctorCheck>> RunAsync(bool probe, CancellationToken cancellationToken)
    {
        var checks = new List<DoctorCheck>
        {
            VersionCheck(),
            UpdateCheck(),
            DataRootCheck()
        };

        var inUse = config == null ? [] : StoragesInUse(config);
        checks.Add(DiskCheck(inUse));
        checks.Add(ConfigCheck());
        checks.Add(WatchDirectoriesCheck());
        checks.Add(WatchExclusionCheck());
        checks.Add(StorageCheck(inUse));
        checks.Add(probe ? await StorageProbeCheckAsync(inUse, cancellationToken).ConfigureAwait(false) : Skip("storage-probe", "Storage probe", "Not requested; pass --probe to upload, read and delete a tiny object on every storage in use."));
        checks.Add(EncryptionCheck());
        checks.Add(OAuthCheck(inUse));
        checks.Add(await StateCheckAsync().ConfigureAwait(false));
        checks.Add(await RepositoryLockCheckAsync(cancellationToken).ConfigureAwait(false));
        checks.Add(PauseCheck());
        checks.Add(await ProtectionCheckAsync(cancellationToken).ConfigureAwait(false));
        checks.Add(StalenessCheck());
        checks.Add(FailuresCheck());
        checks.Add(PathCheck());
        checks.Add(MsixVirtualizationCheck());
        checks.Add(TelemetryCheck());
        return checks;
    }

    private static DoctorCheck Ok(string id, string title, string detail, string? fix = null) => new(id, title, DoctorStatus.Ok, detail, fix);

    private static DoctorCheck Warn(string id, string title, string detail, string? fix = null) => new(id, title, DoctorStatus.Warn, detail, fix);

    private static DoctorCheck Fail(string id, string title, string detail, string? fix = null) => new(id, title, DoctorStatus.Fail, detail, fix);

    private static DoctorCheck Skip(string id, string title, string detail) => new(id, title, DoctorStatus.Skip, detail);

    private DoctorCheck NoConfig(string id, string title) => Skip(id, title, "The configuration is not available.");

    private static string FormatBytes(long bytes) => ByteFormatter.Format(bytes);

    // ---- version and updates -----------------------------------------------------------------------------------------

    private DoctorCheck VersionCheck() =>
        Ok("version", "Version", $"ReStore {CommandRegistry.Version}, channel {environment.Channel}, {environment.Architecture}.");

    private DoctorCheck UpdateCheck()
    {
        const string title = "Updates";
        try
        {
            var service = UpdateCommands.CreateService();
            var cached = service.GetCachedResult();
            var last = service.Cache.Load().LastCheckUtc;
            if (cached == null || last == null)
            {
                return Ok("update", title, "Not checked yet.", "restore update check");
            }

            var when = $"checked {last.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)} UTC";
            return cached.UpdateAvailable
                ? Warn("update", title, $"ReStore {cached.Latest} is available (you have {cached.Current}; {when}){(cached.Critical ? ", marked critical" : string.Empty)}.", "restore update install")
                : Ok("update", title, $"Up to date as of the last check ({when}).", "restore update check");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Skip("update", title, $"The update cache could not be read: {DoctorCommands.Describe(ex)}");
        }
    }

    // ---- data root and disks -----------------------------------------------------------------------------------------

    private DoctorCheck DataRootCheck()
    {
        const string title = "Data folder";
        var root = AppPaths.Root;
        if (!Directory.Exists(root))
        {
            return Warn("data-root", title, $"{root} does not exist yet; ReStore creates it on first use.", "restore init");
        }

        var probe = Path.Combine(root, $".doctor-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return Ok("data-root", title, $"{root} is writable.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("data-root", title, $"{root} is not writable: {DoctorCommands.Describe(ex)}", "check the permissions of the data folder, or free disk space");
        }
    }

    private static List<string> StoragesInUse(IConfigManager config)
    {
        var types = new List<string>();
        void Add(string? type)
        {
            if (!string.IsNullOrWhiteSpace(type) && !types.Contains(type, StringComparer.OrdinalIgnoreCase))
            {
                types.Add(type);
            }
        }

        Add(config.GlobalStorageType);
        foreach (var watch in config.WatchDirectories.Where(w => !string.IsNullOrWhiteSpace(w.Path)))
        {
            Add(watch.StorageType);
        }

        if (config.SystemBackup.Enabled)
        {
            Add(config.SystemBackup.StorageType);
        }

        return types;
    }

    private DoctorCheck DiskCheck(IReadOnlyList<string> inUse)
    {
        const string title = "Free disk space";
        var targets = new List<(string Label, string Path)> { ("data folder", AppPaths.Root) };
        if (config != null && inUse.Contains("local", StringComparer.OrdinalIgnoreCase)
            && config.StorageSources.TryGetValue("local", out var local) && !string.IsNullOrWhiteSpace(local.Path))
        {
            try
            {
                targets.Add(("local storage", Path.GetFullPath(ConfigManager.ExpandConfiguredPath(local.Path))));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // An invalid path is reported by the configuration check.
            }
        }

        var parts = new List<string>();
        var low = new List<string>();
        foreach (var (label, path) in targets)
        {
            if (environment.FreeBytes(path) is not { } free)
            {
                parts.Add($"{label}: unknown");
                continue;
            }

            parts.Add($"{label}: {FormatBytes(free)} free");
            if (free < DoctorCommands.LowDiskBytes)
            {
                low.Add(label);
            }
        }

        return low.Count > 0
            ? Warn("disk-space", title, $"Less than {FormatBytes(DoctorCommands.LowDiskBytes)} free on the {string.Join(" and ", low)} ({string.Join("; ", parts)}).", "free up disk space; 'restore retention apply' prunes old snapshots from the storage")
            : Ok("disk-space", title, string.Join("; ", parts) + ".");
    }

    // ---- configuration -----------------------------------------------------------------------------------------------

    private DoctorCheck ConfigCheck()
    {
        const string title = "Configuration";
        if (config == null)
        {
            return Fail("config", title, configError ?? "The configuration could not be loaded.", "restore init (first-time setup) or 'restore config validate'");
        }

        ConfigValidationResult result;
        var created = DirectoriesTheValidatorWouldCreate(config);
        try
        {
            result = new ConfigValidator(logger).ValidateConfiguration(config);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Fail("config", title, $"Validation failed unexpectedly: {DoctorCommands.Describe(ex)}", "restore config validate");
        }
        finally
        {
            // The validator creates a missing local storage folder; doctor never changes anything, so it takes that back.
            foreach (var directory in created)
            {
                RemoveIfNewAndEmpty(directory);
            }
        }

        var counts = $"{result.Errors.Count} error(s), {result.Warnings.Count} warning(s)";
        if (result.Errors.Count > 0)
        {
            return Fail("config", title, $"{config.GetConfigFilePath()} is invalid: {counts}. First: {DoctorCommands.Shorten(result.Errors[0])}", "restore config validate");
        }

        // Validation warnings are advisory (the default config always has some about storages that are not set up), as for 'config validate'.
        return result.Warnings.Count > 0
            ? Ok("config", title, $"{config.GetConfigFilePath()} loads and validates ({counts}). First warning: {DoctorCommands.Shorten(result.Warnings[0])}", "restore config validate")
            : Ok("config", title, $"{config.GetConfigFilePath()} loads and validates ({counts}).");
    }

    private static List<string> DirectoriesTheValidatorWouldCreate(IConfigManager config)
    {
        var missing = new List<string>();
        foreach (var source in config.StorageSources.Where(s => string.Equals(s.Key, "local", StringComparison.OrdinalIgnoreCase)).Select(s => s.Value))
        {
            try
            {
                var path = Path.GetFullPath(ConfigManager.ExpandConfiguredPath(source.Path));
                if (!Directory.Exists(path))
                {
                    missing.Add(path);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Reported by the validation itself.
            }
        }

        if (config.StorageSources.TryGetValue("gdrive", out var drive) && drive.Options.TryGetValue("token_folder", out var folder) && !string.IsNullOrWhiteSpace(folder))
        {
            try
            {
                var parent = Path.GetDirectoryName(ConfigManager.ExpandConfiguredPath(folder));
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                {
                    missing.Add(parent);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Reported by the validation itself.
            }
        }

        return missing;
    }

    private static void RemoveIfNewAndEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory, recursive: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leaving an empty folder behind is harmless.
        }
    }

    private DoctorCheck WatchDirectoriesCheck()
    {
        const string title = "Watch directories";
        if (config == null)
        {
            return NoConfig("watch-directories", title);
        }

        var watches = config.WatchDirectories.Where(w => !string.IsNullOrWhiteSpace(w.Path)).ToList();
        if (watches.Count == 0)
        {
            return Warn("watch-directories", title, "No folders are configured to be backed up.", "restore config add-watch <dir>");
        }

        var missing = watches.Where(w => !Directory.Exists(w.ExpandedPath)).Select(w => w.Path).ToList();
        return missing.Count > 0
            ? Warn("watch-directories", title, $"{missing.Count} of {watches.Count} folder(s) do not exist: {DoctorCommands.Shorten(string.Join(", ", missing))}.", "restore config remove-watch <dir>, or create or reconnect the folder")
            : Ok("watch-directories", title, $"{watches.Count} folder(s) configured and present.");
    }

    private DoctorCheck WatchExclusionCheck()
    {
        const string title = "Watch directories vs. exclusions";
        if (config == null)
        {
            return NoConfig("watch-exclusions", title);
        }

        var selection = new FileSelectionService(logger, config);
        var excluded = new List<string>();
        foreach (var watch in config.WatchDirectories.Where(w => !string.IsNullOrWhiteSpace(w.Path)))
        {
            if (selection.FindExcludingRule(watch.ExpandedPath) is { } rule)
            {
                excluded.Add($"{watch.Path} ({rule})");
            }
        }

        return excluded.Count > 0
            ? Warn("watch-exclusions", title, $"Backing up these folders would save nothing: {DoctorCommands.Shorten(string.Join("; ", excluded))}.", "restore config get excludedPaths, then remove the rule with 'restore config unset' or 'restore config set'")
            : Ok("watch-exclusions", title, "No watch directory lies inside an excluded path or pattern.");
    }

    // ---- storage -----------------------------------------------------------------------------------------------------

    private DoctorCheck StorageCheck(IReadOnlyList<string> inUse)
    {
        const string title = "Storage";
        if (config == null)
        {
            return NoConfig("storage", title);
        }

        if (string.IsNullOrWhiteSpace(config.GlobalStorageType))
        {
            return Fail("storage", title, "No default storage is configured (globalStorageType is empty).", "restore config set-storage <type> --default");
        }

        var problems = new List<string>();
        var fixes = new List<string>();
        foreach (var type in inUse)
        {
            if (!config.StorageSources.TryGetValue(type, out var source))
            {
                problems.Add($"'{type}' is selected but has no entry in storageSources");
                fixes.Add($"restore config set-storage {type}");
                continue;
            }

            var info = StorageCatalog.Find(type);
            if (info == null)
            {
                problems.Add($"'{type}' is not a known storage type");
                continue;
            }

            var options = new JsonObject();
            foreach (var option in source.Expanded().Options)
            {
                options[option.Key] = option.Value;
            }

            var missing = info.Missing(options);
            if (type.Equals("local", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(source.Path))
            {
                problems.Add("'local' has no path");
                fixes.Add("restore config set-storage local --path <dir>");
            }
            else if (missing.Count > 0)
            {
                problems.Add($"'{type}' is missing {string.Join(", ", missing)}");
                fixes.Add($"restore config set-storage {type} (see 'restore help config set-storage')");
            }
        }

        return problems.Count > 0
            ? Fail("storage", title, $"Default storage '{config.GlobalStorageType}'. {string.Join("; ", problems)}.", string.Join("; ", fixes.Distinct()))
            : Ok("storage", title, $"Default storage '{config.GlobalStorageType}'; in use: {string.Join(", ", inUse)}.");
    }

    private async Task<DoctorCheck> StorageProbeCheckAsync(IReadOnlyList<string> inUse, CancellationToken cancellationToken)
    {
        const string title = "Storage probe";
        if (config == null)
        {
            return NoConfig("storage-probe", title);
        }

        var passed = new List<string>();
        var failed = new List<string>();
        foreach (var type in inUse)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            try
            {
                var result = await environment.ProbeStorage(config, type, timeout.Token).ConfigureAwait(false);
                if (result.Success)
                {
                    passed.Add(type);
                }
                else
                {
                    var step = result.Steps.First(s => !s.Passed);
                    failed.Add($"{type}: {step.Name} failed{(step.Error == null ? string.Empty : " - " + DoctorCommands.Shorten(step.Error))}");
                }
            }
            catch (StorageAuthRequiredException)
            {
                failed.Add($"{type}: needs a sign-in");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                failed.Add($"{type}: timed out");
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
            {
                failed.Add($"{type}: {DoctorCommands.Describe(ex)}");
            }
        }

        return failed.Count > 0
            ? Fail("storage-probe", title, string.Join("; ", failed) + (passed.Count > 0 ? $". Passed: {string.Join(", ", passed)}." : "."), "restore probe --storage <type> for the full step list; for OAuth storages run 'restore auth gdrive' or 'restore auth dropbox'")
            : Ok("storage-probe", title, $"Round trip passed for {string.Join(", ", passed)}.");
    }

    // ---- encryption and sign-in --------------------------------------------------------------------------------------

    private DoctorCheck EncryptionCheck()
    {
        const string title = "Encryption";
        if (config == null)
        {
            return NoConfig("encryption", title);
        }

        if (!config.Encryption.Enabled)
        {
            return Ok("encryption", title, "Not enabled.");
        }

        return environment.TryGetPassword(config) != null
            ? Ok("encryption", title, "Enabled; the password is available without a prompt (environment, password file or remembered).")
            : Warn(
                "encryption", title,
                "Enabled, but no password is available without a prompt, so scheduled and service runs fail with exit 4.",
                $"set {CliPasswordSources.PasswordVariable} or {CliPasswordSources.PasswordFileVariable}, or (Windows) run 'restore unlock --remember' once");
    }

    private DoctorCheck OAuthCheck(IReadOnlyList<string> inUse)
    {
        const string title = "Sign-ins";
        if (config == null)
        {
            return NoConfig("oauth", title);
        }

        var results = new List<string>();
        var fixes = new List<string>();
        foreach (var provider in new[] { "gdrive", "dropbox" })
        {
            if (!inUse.Contains(provider, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            config.StorageSources.TryGetValue(provider, out var source);
            var present = provider == "gdrive"
                ? DriveTokenPresent(source)
                : AuthCommands.HasValue(source, "refreshToken") || AuthCommands.HasValue(source, "accessToken");
            results.Add($"{provider}: {(present ? "token present" : "not signed in")}");
            if (!present)
            {
                fixes.Add($"restore auth {provider} (needs the user: exit 4 without a terminal)");
            }
        }

        if (results.Count == 0)
        {
            return Skip("oauth", title, "No Google Drive or Dropbox storage is in use.");
        }

        return fixes.Count > 0
            ? Fail("oauth", title, string.Join("; ", results) + ".", string.Join("; ", fixes))
            : Ok("oauth", title, string.Join("; ", results) + ".");
    }

    private static bool DriveTokenPresent(StorageConfig? source)
    {
        try
        {
            return ReStore.Core.src.storage.google.DriveStorage.HasStoredToken(AuthCommands.TokenFolderOf(source));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    // ---- state, lock, protection -------------------------------------------------------------------------------------

    private async Task<DoctorCheck> StateCheckAsync()
    {
        const string title = "State file";
        try
        {
            _state = await environment.LoadState(logger).ConfigureAwait(false);
            return Ok("state", title, "Loads.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _stateError = DoctorCommands.Describe(ex);
            return Fail("state", title, $"The system state could not be loaded: {_stateError}", "do not edit it by hand; restore a copy from the 'backups' folder of the data folder, or run 'restore diagnostics' and report it");
        }
    }

    private async Task<DoctorCheck> RepositoryLockCheckAsync(CancellationToken cancellationToken)
    {
        const string title = "Repository lock";
        try
        {
            var (busy, reason) = await environment.ProbeBusy(cancellationToken).ConfigureAwait(false);
            return busy
                ? Warn("repository-lock", title, $"Busy: {DoctorCommands.Shorten(reason ?? "another ReStore process is using the repository")}.", "wait for it to finish, or pass --wait <seconds> to the next command")
                : Ok("repository-lock", title, "Free.");
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            return Warn("repository-lock", title, $"Could not check: {DoctorCommands.Describe(ex)}");
        }
    }

    private DoctorCheck PauseCheck()
    {
        const string title = "Protection pause";
        var until = new ProtectionPauseStore().LoadActive(environment.UtcNow());
        return until is { } end
            ? Warn("pause", title, $"Background protection is paused {ProtectionPause.Describe(end)}; scheduled backups do not run.", "restore resume")
            : Ok("pause", title, "Not paused.");
    }

    private async Task<DoctorCheck> ProtectionCheckAsync(CancellationToken cancellationToken)
    {
        const string title = "Automatic backups";
        var active = new List<string>();
        var notes = new List<string>();
        if (environment.IsGuiRunning())
        {
            active.Add("the ReStore app is running");
        }

        if (environment.IsWindows)
        {
            try
            {
                var task = await environment.QueryScheduledTask(cancellationToken).ConfigureAwait(false);
                if (task.Installed && task.Enabled != false)
                {
                    active.Add($"scheduled task {ScheduledTaskManager.TaskName} (every {task.IntervalMinutes?.ToString(CultureInfo.InvariantCulture) ?? "?"} min)");
                }
                else if (task.Installed)
                {
                    notes.Add("the scheduled task exists but is disabled");
                }
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
            {
                notes.Add($"the scheduled task could not be queried ({DoctorCommands.Describe(ex)})");
            }
        }

        if (active.Count > 0)
        {
            return Ok("protection", title, $"Active: {string.Join("; ", active)}.");
        }

        var detail = "Nothing runs backups automatically: the app is not running and no scheduled task is installed"
            + (notes.Count > 0 ? $" ({string.Join("; ", notes)})" : string.Empty)
            + ". A 'restore --service' process you started yourself is not detected.";
        return Warn(
            "protection", title, detail,
            environment.IsWindows ? "restore schedule install" : "run 'restore run-due --quiet' from cron or a systemd timer");
    }

    // ---- history -----------------------------------------------------------------------------------------------------

    private DoctorCheck StalenessCheck()
    {
        const string title = "Stale backups";
        if (config == null)
        {
            return NoConfig("staleness", title);
        }

        if (_state == null)
        {
            return Skip("staleness", title, $"The state is not available ({_stateError}).");
        }

        var groups = config.WatchDirectories
            .Where(w => !string.IsNullOrWhiteSpace(w.Path))
            .Select(w => BackupScheduler.NormalizeGroup(w.ExpandedPath))
            .ToList();
        var stale = BackupStaleness.Evaluate(groups, _state, config.BackupInterval, environment.UtcNow());
        if (stale.Count == 0)
        {
            return Ok("staleness", title, groups.Count == 0 ? "No folders to check." : "Every folder with a backup was backed up within twice its interval.");
        }

        var worst = stale.OrderByDescending(s => s.Age).First();
        return Warn(
            "staleness", title,
            $"{stale.Count} folder(s) have no successful backup for more than twice the interval ({FormatAge(worst.Age)} for the oldest): {DoctorCommands.Shorten(string.Join(", ", stale.Select(s => s.Group)))}.",
            "restore backup <dir> --json to back up now; 'restore status --json' shows why a folder is overdue");
    }

    private static string FormatAge(TimeSpan age) =>
        age.TotalDays >= 1 ? $"{(int)age.TotalDays} day(s)" : $"{(int)age.TotalHours} hour(s)";

    private DoctorCheck FailuresCheck()
    {
        const string title = "Recent failures";
        if (_state == null)
        {
            return Skip("failures", title, $"The state is not available ({_stateError}).");
        }

        var since = environment.UtcNow() - DoctorCommands.FailureWindow;
        var failures = _state.GetRecentActivity().Where(a => a.Result == ActivityResult.Failed && a.EndUtc >= since).ToList();
        if (failures.Count == 0)
        {
            return Ok("failures", title, "No failed operations in the last 7 days.");
        }

        var newest = failures[0];
        return Warn(
            "failures", title,
            $"{failures.Count} failed operation(s) in the last 7 days. Newest ({newest.Type}, {newest.EndUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)} UTC): {DoctorCommands.Shorten(newest.Message)}",
            "restore activity --json for the list");
    }

    // ---- this machine ------------------------------------------------------------------------------------------------

    internal static (string Status, string Detail, string? Fix) ClassifyPath(IReadOnlyList<string> found)
    {
        if (found.Count == 0)
        {
            return (DoctorStatus.Warn, "restore.exe is not on PATH in this process, so other programs cannot run 'restore'.",
                "open a new terminal (PATH changes need one); portable copy: re-run the install.ps1 one-liner; MSIX: check Settings > Apps > Advanced app settings > App execution aliases");
        }

        bool IsAlias(string path) => path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase);
        var alias = found.FirstOrDefault(IsAlias);
        var portable = found.FirstOrDefault(p => !IsAlias(p));
        if (alias != null && portable != null)
        {
            var winner = IsAlias(found[0]) ? "the MSIX alias" : "the portable copy";
            return (DoctorStatus.Warn,
                $"Both the MSIX alias ({alias}) and a portable copy ({portable}) are on PATH; {winner} wins, the other is shadowed and may be a different version.",
                "remove one: 'powershell -File install.ps1 -Uninstall' for the portable copy, or turn the alias off in App execution aliases");
        }

        return (DoctorStatus.Ok, $"restore.exe resolves to {found[0]}.", null);
    }

    private DoctorCheck PathCheck()
    {
        const string title = "PATH";
        if (!environment.IsWindows)
        {
            return Skip("path", title, "Only checked on Windows.");
        }

        var (status, detail, fix) = ClassifyPath(environment.RestoreOnPath());
        return new DoctorCheck("path", title, status, detail, fix);
    }

    private DoctorCheck MsixVirtualizationCheck()
    {
        const string title = "MSIX file redirection";
        if (environment.Channel != AppChannel.Msix)
        {
            return Skip("msix-virtualization", title, "Only relevant for the MSIX install.");
        }

        return config is { SystemBackup: { Enabled: true, IncludeDevTools: true } }
            ? Warn(
                "msix-virtualization", title,
                "This is the MSIX install and dev-tools settings are part of system backups; restoring them writes into AppData folders that Windows may redirect for a packaged app, so the files can land in a private copy.",
                "use the portable CLI for 'system-restore' of dev tools: irm https://github.com/js302/ReStore/releases/latest/download/install.ps1 | iex")
            : Ok("msix-virtualization", title, "MSIX install; no dev-tools restores are configured.");
    }

    private DoctorCheck TelemetryCheck()
    {
        const string title = "Telemetry and crash reports";
        try
        {
            var info = environment.Telemetry();
            var detail = $"Anonymous usage statistics are {(info.Enabled ? "on" : "off")} ({info.Reason}); crash reports: {info.CrashMode}.";
            if (info.PendingCrashReports > 0 || info.QueuedCrashReports > 0)
            {
                return Warn(
                    "telemetry", title,
                    detail + $" {info.PendingCrashReports} crash report(s) pending, {info.QueuedCrashReports} queued; they are only sent when the user agrees.",
                    "restore telemetry crashes list; ask the user before 'restore telemetry crashes send' (never send without asking)");
            }

            return Ok("telemetry", title, detail);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Skip("telemetry", title, $"Could not read the telemetry status: {DoctorCommands.Describe(ex)}");
        }
    }
}
