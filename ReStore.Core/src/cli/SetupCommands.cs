using System.Text.Json.Nodes;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.cli;

/// <summary><c>init</c>: first-time setup in one command.</summary>
internal static class SetupCommands
{
    public static IEnumerable<CommandSpec> Specs { get; } =
    [
        new CommandSpec
        {
            Name = "init",
            Category = CommandCategory.Configuration,
            Summary = "First-time setup in one command: storage, watched folders, optional encryption and probe.",
            Description = "Creates config.json if needed and configures the storage (as 'config set-storage ... --default' does), optionally replaces the watched folders (--watch, repeatable), turns on encryption (--encrypt; the password comes from --password-stdin, RESTORE_ENCRYPTION_PASSWORD or RESTORE_ENCRYPTION_PASSWORD_FILE, is asked for twice in a terminal, and exits 4 without one) and probes the storage (--probe). "
                + "The whole resulting configuration is validated before anything is written (exit 3 otherwise). "
                + "A configuration that is already customised (other folders, another storage, encryption on) is not touched without --force (exit 2), so an agent cannot clobber a working setup. "
                + "Secrets: use --option-stdin name (the next line of stdin; lines are read before the --password-stdin password) or env:NAME values. "
                + "JSON: configFile, storage, watchDirectories, encryption, probe, backup, warnings, nextSteps.",
            Options =
            [
                new OptionSpec("--storage", "Storage type to set up and make the default (local, s3, gdrive, dropbox, github, azure, gcp, sftp, b2).", "storageType"),
                new OptionSpec("--path", "Folder of 'local' storage.", "path"),
                new OptionSpec("--option", "Provider option, name=value.", "name=value", Repeatable: true),
                new OptionSpec("--option-stdin", "Provider option whose value is the next line of stdin.", "name", Repeatable: true),
                new OptionSpec("--watch", "Folder to back up (repeatable); replaces the default folders.", "dir", Repeatable: true),
                new OptionSpec("--encrypt", "Turn on encryption."),
                new OptionSpec("--probe", "Test the storage with a real round trip afterwards."),
                new OptionSpec("--protect", "Store secret provider options as Windows DPAPI values."),
                new OptionSpec("--force", "Overwrite an existing setup; allow watched folders that do not exist.")
            ],
            Usage = ["init --storage <type> [--path <p>] [--option k=v]... [--option-stdin k]... [--watch <dir>]... [--encrypt] [--probe] [--protect] [--force]"],
            Examples =
            [
                "init --storage local --path D:\\Backups --watch %USERPROFILE%\\Documents --json",
                "init --storage s3 --option region=eu-west-1 --option bucketName=my-backups --option accessKeyId=AKIA... --option-stdin secretAccessKey --watch D:\\Data --probe",
                "init --storage local --path D:\\Backups --watch D:\\Data --encrypt --password-stdin"
            ],
            Mutates = true,
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            PayloadOnStdout = true,
            Validate = ValidateArguments,
            Handler = InitAsync
        }
    ];

    private static string? ValidateArguments(Program.ParsedCommand parsed)
    {
        var storage = parsed.GetOption("--storage");
        if (storage == null)
        {
            return "init needs --storage <type>.";
        }

        if (StorageCatalog.Find(storage) == null)
        {
            return $"Unknown storage type '{storage}'. Known types: {StorageCatalog.KnownTypes}.";
        }

        foreach (var option in parsed.Options.GetValueOrDefault("--option") ?? [])
        {
            if (option.IndexOf('=', StringComparison.Ordinal) <= 0)
            {
                return $"Invalid --option value '{option}'. Expected name=value.";
            }
        }

        return null;
    }

    /// <summary>Why the configuration already counts as set up, or null when it is still the shipped default.</summary>
    internal static string? CustomisationOf(JsonObject current)
    {
        var shipped = ConfigSchema.LoadExample();
        static HashSet<string> WatchPaths(JsonNode? node) => node is JsonArray list
            ? list.Select(item => item switch
            {
                JsonObject o when o["path"] is JsonValue v && v.TryGetValue<string>(out var p) => p,
                JsonValue v when v.TryGetValue<string>(out var p) => p,
                _ => string.Empty
            }).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        if (!WatchPaths(current["watchDirectories"]).SetEquals(WatchPaths(shipped["watchDirectories"]))
            || (current["watchDirectories"] as JsonArray)?.OfType<JsonObject>().Any(w => w["storageType"] != null) == true)
        {
            return "it already watches other folders than the defaults";
        }

        if (current["globalStorageType"]?.GetValue<string>() is { } global && global != shipped["globalStorageType"]!.GetValue<string>())
        {
            return $"its default storage is '{global}'";
        }

        if (!JsonNode.DeepEquals(current["storageSources"], shipped["storageSources"]))
        {
            return "a storage has already been configured";
        }

        if (current["encryption"]?["enabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out var on) && on)
        {
            return "encryption is enabled";
        }

        return null;
    }

    private static async Task<int> InitAsync(CliContext ctx)
    {
        var parsed = ctx.Parsed;
        var info = StorageCatalog.Find(parsed.GetOption("--storage")!)!;
        var force = parsed.HasOption("--force");

        var current = JsonNode.Parse(await File.ReadAllTextAsync(ctx.Config.GetConfigFilePath()).ConfigureAwait(false)) as JsonObject
            ?? throw CliSupport.ConfigProblem("config.json is not a JSON object.");
        if (!force && CustomisationOf(current) is { } reason)
        {
            throw CliSupport.Usage(
                $"This computer already has a ReStore setup ({reason}); init would overwrite it.",
                "use 'restore.exe config set-storage' / 'config add-watch' to change it, or pass --force to run init anyway");
        }

        var watch = new List<string>();
        foreach (var directory in parsed.Options.GetValueOrDefault("--watch") ?? [])
        {
            string stored;
            try
            {
                stored = CliSupport.NormalizeStoredPath(directory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw CliSupport.Usage($"'{directory}' is not a valid folder path: {ex.Message}");
            }

            var expanded = ConfigManager.ExpandConfiguredPath(stored);
            if (!Directory.Exists(expanded) && !force)
            {
                throw CliSupport.Usage($"The folder '{expanded}' does not exist.", "create it first, or pass --force to watch it anyway");
            }

            if (!watch.Any(w => CliSupport.NormalizeForCompare(ConfigManager.ExpandConfiguredPath(w)) == CliSupport.NormalizeForCompare(expanded)))
            {
                watch.Add(stored);
            }
        }

        // Stdin lines (option values) come first, then the password, in the order the options are documented.
        var options = CliSupport.ParsePairs(parsed.Options.GetValueOrDefault("--option") ?? [], "--option");
        foreach (var name in parsed.Options.GetValueOrDefault("--option-stdin") ?? [])
        {
            var line = CliSupport.ReadStdinLine()
                ?? throw CliSupport.Usage($"--option-stdin {name}: stdin has no more lines.", "pipe one line per --option-stdin, in the order given, then the password for --password-stdin");
            options.Add((name, line));
        }

        var warnings = new List<string>();
        string? password = null;
        var encryptRequested = parsed.HasOption("--encrypt");
        if (encryptRequested)
        {
            if (ctx.Config.Encryption.Enabled)
            {
                warnings.Add("Encryption was already enabled and is left as it is.");
            }
            else
            {
                password = EncryptionCommands.AcquireNewPassword();
                if (PasswordStrength.Evaluate(password).IsWeak)
                {
                    warnings.Add("The password is weak. Use 12 or more characters mixing upper and lower case, digits and symbols.");
                }
            }
        }

        var change = new StorageChange(info, parsed.GetOption("--path"), options, MakeDefault: true, parsed.HasOption("--protect"));
        var encryptionEdit = password != null ? EncryptionCommands.EnableEdit(ctx.Config, ctx.Logger, password) : null;
        var result = await CliSupport.EditConfigAsync(ctx, root =>
        {
            StorageSetup.Apply(root, change, warnings);
            if (watch.Count > 0)
            {
                root["watchDirectories"] = new JsonArray([.. watch.Select(w => (JsonNode)new JsonObject { ["path"] = w, ["storageType"] = null })]);
            }

            encryptionEdit?.Invoke(root);
        }, requireValid: true).ConfigureAwait(false);
        if (!result.Applied)
        {
            return await CliSupport.RejectAsync(ctx, result).ConfigureAwait(false);
        }

        if (password != null)
        {
            RememberedPassword.Clear();
        }

        warnings.AddRange(result.Warnings);
        var config = ctx.Config;
        var source = config.StorageSources[info.Type];

        object? probeDocument = null;
        var exitCode = Program.ExitCodes.Success;
        if (parsed.HasOption("--probe"))
        {
            (probeDocument, exitCode) = await ProbeAsync(ctx, info.Type).ConfigureAwait(false);
        }

        var firstFolder = config.WatchDirectories.FirstOrDefault()?.Path;
        var nextSteps = new List<string>();
        var excludedWatch = false;
        foreach (var watched in config.WatchDirectories.Where(w => !string.IsNullOrWhiteSpace(w.Path)))
        {
            if (CliSupport.ExclusionWarning(ctx.Logger, config, watched.ExpandedPath) is { } exclusionWarning)
            {
                warnings.Add(exclusionWarning);
                if (ctx.Json)
                {
                    // The text output below prints the warnings itself; with --json they would otherwise be only inside the document.
                    ctx.Logger.Log(exclusionWarning, LogLevel.Warning);
                }

                excludedWatch = true;
            }
        }

        if (excludedWatch)
        {
            nextSteps.Add("restore.exe config get excludedPaths   (then e.g. restore.exe config set excludedPaths \"[]\" to lift the rule)");
        }

        if (info.NextStep != null)
        {
            nextSteps.Add(info.NextStep);
        }

        if (!parsed.HasOption("--probe"))
        {
            nextSteps.Add($"restore.exe probe --storage {info.Type}");
        }

        nextSteps.Add(firstFolder != null ? $"restore.exe backup {(firstFolder.Contains(' ', StringComparison.Ordinal) ? $"\"{firstFolder}\"" : firstFolder)}" : "restore.exe backup <folder>");
        nextSteps.Add("restore.exe schedule install");
        if (config.Encryption.Enabled)
        {
            nextSteps.Add("restore.exe unlock --remember");
        }

        var document = new
        {
            configFile = config.GetConfigFilePath(),
            storage = new { type = info.Type, path = info.Remote ? null : source.Path, @default = true, options = source.Options.ToDictionary(o => o.Key, o => SecretReferences.Redact(o.Key, o.Value)) },
            watchDirectories = config.WatchDirectories.Select(w => w.Path).ToList(),
            encryption = new { enabled = config.Encryption.Enabled },
            probe = probeDocument,
            backup = result.BackupPath,
            warnings,
            notes = config.Encryption.Enabled && password != null ? EncryptionCommands.SetupNotes : [],
            nextSteps
        };

        var text = new System.Text.StringBuilder();
        text.AppendLine($"ReStore is set up: {info.Type} storage (default), {config.WatchDirectories.Count} watched folder(s){(config.Encryption.Enabled ? ", encryption on" : string.Empty)}.");
        text.AppendLine($"Configuration: {config.GetConfigFilePath()}");
        if (parsed.HasOption("--probe"))
        {
            text.AppendLine(exitCode == Program.ExitCodes.Success ? "Storage probe: passed." : "Storage probe: FAILED (run 'restore.exe probe' for the steps).");
        }

        foreach (var warning in warnings)
        {
            text.AppendLine($"Warning: {warning}");
        }

        if (password != null)
        {
            foreach (var note in EncryptionCommands.SetupNotes)
            {
                text.AppendLine(note);
            }
        }

        foreach (var step in nextSteps)
        {
            text.AppendLine($"Next: {step}");
        }

        await CliSupport.EmitAsync(ctx, document, text.ToString().TrimEnd(), exitCode).ConfigureAwait(false);
        return exitCode;
    }

    private static async Task<(object Document, int ExitCode)> ProbeAsync(CliContext ctx, string type)
    {
        try
        {
            using var storage = await ctx.Config.CreateStorageAsync(type).ConfigureAwait(false);
            var result = await StorageProbe.RunAsync(storage, ctx.Cancellation).ConfigureAwait(false);
            return (
                new { storage = type, success = result.Success, cleanedUp = result.CleanedUp, steps = result.Steps.Select(s => new { name = s.Name, passed = s.Passed, skipped = s.Skipped, elapsedMs = s.ElapsedMs, error = s.Error }) },
                result.Success ? Program.ExitCodes.Success : Program.ExitCodes.Failure);
        }
        catch (StorageAuthRequiredException ex)
        {
            ctx.Failure = new CliError(CliErrorCodes.NeedsUser, ex.Message,
                "this storage needs a sign-in first (restore.exe auth gdrive / auth dropbox), then run 'restore.exe probe'");
            Console.Error.WriteLine($"Error: {ex.Message}");
            return (new { storage = type, success = false, needsUser = true, message = ex.Message }, Program.ExitCodes.NeedsUser);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            Console.Error.WriteLine($"Error: the storage probe failed: {ex.Message}");
            return (new { storage = type, success = false, message = ex.Message }, Program.ExitCodes.Failure);
        }
    }
}
