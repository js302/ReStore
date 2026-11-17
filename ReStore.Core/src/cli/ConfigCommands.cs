using System.Text.Json;
using System.Text.Json.Nodes;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.cli;

/// <summary>What <c>config set-storage</c> and <c>init</c> change in one storage source.</summary>
internal sealed record StorageChange(StorageTypeInfo Info, string? Path, IReadOnlyList<(string Key, string Value)> Options, bool MakeDefault, bool Protect);

/// <summary>Configures a storage source in the config document (shared by <c>config set-storage</c> and <c>init</c>).</summary>
internal static class StorageSetup
{
    public static void Apply(JsonObject root, StorageChange change, List<string> warnings)
    {
        var info = change.Info;
        if (change.Path != null && info.Remote)
        {
            throw new ConfigEditException($"The {info.Type} storage is remote and has no folder path; use --option prefix=<folder> to keep the backups in a sub-folder.");
        }

        if (root["storageSources"] is not JsonObject sources)
        {
            sources = [];
            root["storageSources"] = sources;
        }

        // An entry still exactly as shipped in the example config holds placeholder values: start from a clean one.
        var shipped = ConfigSchema.LoadExample()["storageSources"]?[info.Type];
        var entry = sources[info.Type] is JsonObject existing && !JsonNode.DeepEquals(existing, shipped)
            ? existing
            : info.Remote ? new JsonObject() : shipped?.DeepClone() as JsonObject ?? new JsonObject();

        if (entry["options"] is not JsonObject options)
        {
            options = [];
            entry["options"] = options;
        }

        if (change.Path != null)
        {
            entry["path"] = CliSupport.NormalizeStoredPath(change.Path);
        }

        if (!info.Remote && entry["path"] is null)
        {
            throw new ConfigEditException("The local storage needs a folder: pass --path <folder>.");
        }

        foreach (var (key, value) in change.Options)
        {
            var name = StorageCatalog.CanonicalOption(info, key)
                ?? throw new ConfigEditException($"'{key}' is not an option of the {info.Type} storage. Known options: {string.Join(", ", info.Options)}.");
            options[options.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name] =
                StoredValue(name, value, change.Protect, warnings);
        }

        var missing = info.Missing(options);
        if (missing.Count > 0)
        {
            throw new ConfigEditException(
                $"The {info.Type} storage still needs {info.RequirementText} (missing: {string.Join(", ", missing)}). "
                + "Pass them with --option name=value (secrets: --option-stdin name, or an env:NAME reference).");
        }

        sources[info.Type] = entry;
        if (change.MakeDefault)
        {
            root["globalStorageType"] = info.Type;
        }
    }

    /// <summary>The value to write for a secret or plain option: protected on request, with a warning when a literal secret stays in plain text.</summary>
    public static string StoredValue(string option, string value, bool protect, List<string> warnings)
    {
        if (!SecretReferences.IsSecretOption(option) || SecretReferences.IsProtectedOrEmpty(value))
        {
            return value;
        }

        if (protect)
        {
            if (!CliServices.SecretProtector.IsAvailable)
            {
                throw new ConfigEditException("--protect needs Windows DPAPI, which is not available on this platform. Use an env:NAME reference instead.");
            }

            return SecretReferences.Protect(value, CliServices.SecretProtector);
        }

        warnings.Add($"{option} is stored in plain text in config.json; use --protect (Windows) or an env:NAME reference to keep it out of the file.");
        return value;
    }
}

/// <summary><c>config path|get|set|unset|add-watch|remove-watch|list-watch|set-schedule|clear-schedule|set-window|clear-window|set-storage|schema</c>.</summary>
internal static class ConfigCommands
{
    private static readonly OptionSpec ForceOption = new("--force", "Allow it anyway (unknown keys, folders that do not exist).");
    private static readonly OptionSpec ProtectOption = new("--protect", "Store a secret provider option as a Windows DPAPI value (dpapi:) instead of plain text.");

    public static IEnumerable<CommandSpec> Specs { get; } =
    [
        new CommandSpec
        {
            Name = "config path",
            Category = CommandCategory.Configuration,
            Summary = "Print the path of config.json (with --json also the data folder).",
            Description = "Works without a valid configuration and does not create anything. JSON: configFile, dataRoot, exists.",
            Examples = ["config path", "config path --json"],
            Config = ConfigRequirement.None,
            State = StateRequirement.None,
            Handler = PathAsync
        },
        new CommandSpec
        {
            Name = "config get",
            Category = CommandCategory.Configuration,
            Summary = "Print one setting (or the whole config) by dotted key, secrets masked.",
            Description = "The key is a dotted path into config.json: retention.keepDaily, storageSources.s3.options.bucketName, watchDirectories (list items by number: watchDirectories.0.path). "
                + "Strings print without quotes, everything else as JSON. Secret provider option values are masked like in 'config show' unless --reveal. JSON: key, value, secretsRevealed.",
            Positionals = [new PositionalSpec("key", "Dotted key; leave out for the whole config.", Required: false)],
            Options = [new OptionSpec("--reveal", "Print secret values as stored.")],
            Examples = ["config get retention.keepDaily", "config get storageSources.s3.options --json", "config get watchDirectories"],
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            PayloadOnStdout = true,
            Handler = GetAsync
        },
        new CommandSpec
        {
            Name = "config set",
            Category = CommandCategory.Configuration,
            Summary = "Set a setting by dotted key; the result is validated before it is written.",
            Description = "The value is read as JSON when it is valid JSON (numbers, true/false, null, [lists], {objects}, \"quoted strings\"), otherwise it is a plain string. "
                + "Secrets should not appear on the command line: --value-stdin reads the value from stdin instead. --protect stores a secret provider option as a DPAPI value (Windows). "
                + "Unknown keys are refused unless --force (see 'config schema' for the known ones). Everything else in the file (unknown keys, env: and %VAR% values) is kept as it is. "
                + "An invalid result exits 3 and leaves the file untouched; otherwise the previous config.json is saved under backups/. JSON: key, value (secrets masked), changed, backup, warnings.",
            Positionals = [new PositionalSpec("key", "Dotted key, for example retention.keepDaily."), new PositionalSpec("value", "The new value (or use --value-stdin).", Required: false)],
            Options = [new OptionSpec("--value-stdin", "Read the value from stdin."), ProtectOption, ForceOption],
            Usage = ["config set <key> <value> [--protect] [--force]", "config set <key> --value-stdin [--protect] [--force]"],
            Examples = ["config set retention.keepDaily 7", "config set backupInterval 02:00:00", "config set telemetry.enabled false", "config set storageSources.s3.options.secretAccessKey --value-stdin --protect"],
            Mutates = true,
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            PayloadOnStdout = true,
            Validate = p => p.HasOption("--value-stdin") == (p.Positionals.Count > 1)
                ? "Give the value either as an argument or with --value-stdin." : null,
            Handler = SetAsync
        },
        new CommandSpec
        {
            Name = "config unset",
            Category = CommandCategory.Configuration,
            Summary = "Remove a setting so its default applies (validated before it is written).",
            Description = "Removes the key from config.json; a key that is not there is not an error (changed: false). Removing something the config cannot do without (watchDirectories, storageSources) exits 3 and changes nothing. JSON: key, existed, changed, backup.",
            Positionals = [new PositionalSpec("key", "Dotted key.")],
            Examples = ["config unset retention.keepYearly"],
            Mutates = true,
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            PayloadOnStdout = true,
            Handler = UnsetAsync
        },
        new CommandSpec
        {
            Name = "config add-watch",
            Category = CommandCategory.Configuration,
            Summary = "Add a folder to back up.",
            Description = "The folder is stored as a full path; a value with %VAR% references (%USERPROFILE%\\Documents) is kept as written. Duplicates are refused, and so are folders that do not exist unless --force. "
                + "--storage gives the folder its own storage type (a key of storageSources). JSON: path, expandedPath, storageType, backup.",
            Positionals = [new PositionalSpec("dir", "Folder to watch.")],
            Options = [new OptionSpec("--storage", "Storage type of this folder instead of the default (a key of storageSources).", "storageType"), ForceOption],
            Examples = ["config add-watch D:\\Photos", "config add-watch %USERPROFILE%\\Documents --storage s3"],
            Mutates = true,
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            PayloadOnStdout = true,
            Handler = AddWatchAsync
        },
        new CommandSpec
        {
            Name = "config remove-watch",
            Category = CommandCategory.Configuration,
            Summary = "Stop backing up a folder (its existing snapshots are not deleted).",
            Description = "Matches the folder like 'config add-watch' does. Removing the last folder exits 3 (a config needs at least one). JSON: path, removed, backup.",
            Positionals = [new PositionalSpec("dir", "Folder to remove.")],
            Examples = ["config remove-watch D:\\Photos"],
            Mutates = true,
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            PayloadOnStdout = true,
            Handler = RemoveWatchAsync
        },
        new CommandSpec
        {
            Name = "config list-watch",
            Category = CommandCategory.Configuration,
            Summary = "List the watched folders.",
            Description = "JSON: watchDirectories, a list of {path, expandedPath, storageType, schedule, exists}.",
            Examples = ["config list-watch --json"],
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            PayloadOnStdout = true,
            Handler = ListWatchAsync
        },
        new CommandSpec
        {
            Name = "config set-schedule",
            Category = CommandCategory.Configuration,
            Summary = "Give a watched folder (or the system backup) a backup schedule such as \"daily 02:00\".",
            Description = "The target is a watched folder (matched like 'config remove-watch') or the word system for the system backup. The expression is checked before anything is written (exit 2 with the reason when it is invalid; 'schedule preview' shows what an expression means). "
                + "Forms: off | every <N>m|h|d (at least 5m) | daily HH:mm[,HH:mm...] | weekdays HH:mm | weekends HH:mm | <days> HH:mm (mon,wed,fri or mon-fri) | weekly <day> HH:mm | monthly <1-28|last> HH:mm | cron <5 fields>. "
                + "Times are local. 'off' stops scheduled backups of the target (the watcher and manual backups still work). Without a schedule a folder follows backupInterval and the system backup systemBackup.backupInterval ('config clear-schedule'). "
                + "The result is validated and the previous config.json is saved under backups/. JSON: target, path, schedule, description, kind, nextRuns, changed, backup, warnings.",
            Positionals = [new PositionalSpec("target", "A watched folder, or 'system'."), new PositionalSpec("expression", "The schedule, quoted or as several words.", Repeatable: true)],
            Examples = ["config set-schedule D:\\Photos \"daily 02:00\"", "config set-schedule system \"weekly sun 03:00\"", "config set-schedule D:\\Archive off"],
            Mutates = true,
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            PayloadOnStdout = true,
            Validate = ValidateSetSchedule,
            Handler = SetScheduleAsync
        },
        new CommandSpec
        {
            Name = "config clear-schedule",
            Category = CommandCategory.Configuration,
            Summary = "Remove the schedule of a watched folder (or the system backup) so the interval applies again.",
            Description = "A target without a schedule is not an error (changed: false). JSON: target, path, existed, changed, backup, warnings.",
            Positionals = [new PositionalSpec("target", "A watched folder, or 'system'.")],
            Examples = ["config clear-schedule D:\\Photos", "config clear-schedule system"],
            Mutates = true,
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            PayloadOnStdout = true,
            Handler = ClearScheduleAsync
        },
        new CommandSpec
        {
            Name = "config set-window",
            Category = CommandCategory.Configuration,
            Summary = "Only start automatic backups inside a time range (the backup window), for example 22:00 to 06:00.",
            Description = "Scheduled and watcher-triggered backups, the system backup, scheduled verification and retention only START inside the window; work due outside waits for it to open (watcher changes stay queued). Manual backups ('backup', 'Back up now') always run, and a backup already running when the window closes finishes. "
                + "Times are local HH:mm; the start is inclusive, the end exclusive; the window may cross midnight (22:00 to 06:00); start equal to end means the whole day. --days says on which days a window starts: all (default), weekdays, weekends or mon,wed,fri / mon-fri. "
                + "Writes backupWindow with enabled: true. JSON: window (enabled, start, end, days, description), changed, backup, warnings.",
            Positionals = [new PositionalSpec("start", "HH:mm the window opens."), new PositionalSpec("end", "HH:mm the window closes.")],
            Options = [new OptionSpec("--days", "Days a window starts on: all, weekdays, weekends, mon,wed,fri or mon-fri (default all).", "days")],
            Examples = ["config set-window 22:00 06:00", "config set-window 23:00 05:00 --days mon-fri"],
            Mutates = true,
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            PayloadOnStdout = true,
            Validate = ValidateSetWindow,
            Handler = SetWindowAsync
        },
        new CommandSpec
        {
            Name = "config clear-window",
            Category = CommandCategory.Configuration,
            Summary = "Remove the backup window: automatic backups may start at any time again.",
            Description = "No window is not an error (changed: false). To keep the window but stop applying it, use 'config set backupWindow.enabled false'. JSON: existed, changed, backup, warnings.",
            Examples = ["config clear-window"],
            Mutates = true,
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            PayloadOnStdout = true,
            Handler = ClearWindowAsync
        },
        new CommandSpec
        {
            Name = "config set-storage",
            Category = CommandCategory.Configuration,
            Summary = "Configure a storage provider (local, s3, gdrive, dropbox, github, azure, gcp, sftp, b2).",
            Description = $"Known types: {StorageCatalog.KnownTypes}. --path is the folder of 'local' storage (remote types take --option prefix=<folder> instead). "
                + "--option name=value sets a provider option (repeatable); for secrets use --option-stdin name, which reads the value from the next line of stdin, or an env:NAME value, so nothing secret is on the command line. "
                + "Unknown types and options are refused, and so is a result that still lacks a required option. --default also makes it the default storage. "
                + "The result is validated before it is written (exit 3 and no change otherwise). Afterwards run 'probe --storage <type>'; gdrive and dropbox also need 'auth'. JSON: type, path, default, options (secrets masked), warnings, nextSteps.",
            Positionals = [new PositionalSpec("type", "Storage type.")],
            Options =
            [
                new OptionSpec("--path", "Folder of 'local' storage.", "path"),
                new OptionSpec("--option", "Provider option, name=value.", "name=value", Repeatable: true),
                new OptionSpec("--option-stdin", "Provider option whose value is the next line of stdin.", "name", Repeatable: true),
                new OptionSpec("--default", "Also make this the default storage (globalStorageType)."),
                ProtectOption
            ],
            Examples =
            [
                "config set-storage local --path D:\\Backups --default",
                "config set-storage s3 --option region=eu-west-1 --option bucketName=my-backups --option accessKeyId=AKIA... --option-stdin secretAccessKey --protect --default"
            ],
            Mutates = true,
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            PayloadOnStdout = true,
            Validate = p => ValidateStorageArguments(p),
            Handler = SetStorageAsync
        },
        new CommandSpec
        {
            Name = "config schema",
            Category = CommandCategory.Configuration,
            Summary = "Print the JSON Schema (draft 2020-12) of config.json, with a description of every key.",
            Description = "Works without a configuration. With --json the schema is the 'schema' member of the document.",
            Examples = ["config schema"],
            Config = ConfigRequirement.None,
            State = StateRequirement.None,
            PayloadOnStdout = true,
            Handler = SchemaAsync
        }
    ];

    internal static string? ValidateStorageArguments(Program.ParsedCommand parsed)
    {
        if (StorageCatalog.Find(parsed.Positionals[0]) == null)
        {
            return $"Unknown storage type '{parsed.Positionals[0]}'. Known types: {StorageCatalog.KnownTypes}.";
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

    // ---- handlers ----------------------------------------------------------------------------------------------------

    private static async Task<int> PathAsync(CliContext ctx)
    {
        await CliSupport.EmitAsync(
            ctx,
            new { configFile = AppPaths.ConfigFile, dataRoot = AppPaths.Root, exists = File.Exists(AppPaths.ConfigFile) },
            AppPaths.ConfigFile).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> SchemaAsync(CliContext ctx)
    {
        if (ctx.Json)
        {
            await ctx.WriteJsonAsync(new { schema = JsonNode.Parse(ConfigSchema.Text) }).ConfigureAwait(false);
        }
        else
        {
            await ctx.JsonOut.WriteLineAsync(ConfigSchema.Text).ConfigureAwait(false);
        }

        return Program.ExitCodes.Success;
    }

    private static async Task<int> GetAsync(CliContext ctx)
    {
        var reveal = ctx.Parsed.HasOption("--reveal");
        var text = await File.ReadAllTextAsync(ctx.Config.GetConfigFilePath()).ConfigureAwait(false);
        var root = JsonNode.Parse(reveal ? text : ConfigSecretsCommands.RedactConfigJson(text));

        var key = ctx.Parsed.Positionals.Count > 0 ? ctx.Parsed.Positionals[0] : null;
        JsonNode? value = root;
        if (key != null)
        {
            string[] segments;
            try
            {
                segments = ConfigEditor.ParseKey(key);
            }
            catch (ConfigEditException ex)
            {
                throw CliSupport.Usage(ex.Message);
            }

            if (!ConfigEditor.TryGet(root, segments, out value))
            {
                var known = ConfigSchema.Resolve(segments).Known;
                throw CliSupport.Usage(
                    known ? $"'{key}' is not set in config.json (the default applies)." : $"'{key}' is not in config.json and is not a known key.",
                    "run 'restore.exe config get' for the whole file or 'restore.exe config schema' for the known keys");
            }
        }

        if (ctx.Json)
        {
            await ctx.WriteJsonAsync(new { key, file = ctx.Config.GetConfigFilePath(), secretsRevealed = reveal, value = value?.DeepClone() }).ConfigureAwait(false);
        }
        else
        {
            await ctx.JsonOut.WriteLineAsync(Render(value)).ConfigureAwait(false);
        }

        return Program.ExitCodes.Success;
    }

    private static string Render(JsonNode? value) => value switch
    {
        null => "null",
        JsonValue v when v.TryGetValue<string>(out var text) => text,
        _ => value.ToJsonString(CliJson.Options)
    };

    /// <summary>Reads the JSON value of <c>config set</c>: valid JSON as such, anything else as a string.</summary>
    internal static JsonNode? ParseValue(string text, ConfigKeyInfo info)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return JsonValue.Create(text);
        }

        // A setting that is text stays text even when it looks like a number or a bool (a port, a bucket named 2024).
        return parsed is not null and not JsonObject and not JsonArray && info.AllowsString && !(parsed is JsonValue v && v.TryGetValue<string>(out _))
            ? JsonValue.Create(text)
            : parsed;
    }

    private static bool IsSecretOptionKey(IReadOnlyList<string> segments) =>
        segments.Count == 4 && string.Equals(segments[0], "storageSources", StringComparison.OrdinalIgnoreCase)
        && string.Equals(segments[2], "options", StringComparison.OrdinalIgnoreCase) && SecretReferences.IsSecretOption(segments[3]);

    private static async Task<int> SetAsync(CliContext ctx)
    {
        var parsed = ctx.Parsed;
        var key = parsed.Positionals[0];
        string[] segments;
        try
        {
            segments = ConfigEditor.ParseKey(key);
        }
        catch (ConfigEditException ex)
        {
            throw CliSupport.Usage(ex.Message);
        }

        var info = ConfigSchema.Resolve(segments);
        if (!info.Known && !parsed.HasOption("--force"))
        {
            throw CliSupport.Usage($"'{key}' is not a known setting.", "run 'restore.exe config schema' for the known keys, or pass --force to write it anyway");
        }

        var rawValue = parsed.HasOption("--value-stdin") ? CliSupport.ReadStdinToEnd() : parsed.Positionals[1];
        if (parsed.HasOption("--value-stdin") && rawValue.Length == 0)
        {
            throw CliSupport.Usage("--value-stdin was given but stdin is empty.");
        }

        string[] canonical = [.. info.Canonical];
        var value = ParseValue(rawValue, info);
        var secret = IsSecretOptionKey(canonical);
        var warnings = new List<string>();
        if (secret && value is JsonValue textValue && textValue.TryGetValue<string>(out var secretText))
        {
            try
            {
                value = JsonValue.Create(StorageSetup.StoredValue(canonical[3], secretText, parsed.HasOption("--protect"), warnings));
            }
            catch (ConfigEditException ex)
            {
                throw CliSupport.Usage(ex.Message);
            }
        }
        else if (parsed.HasOption("--protect"))
        {
            throw CliSupport.Usage("--protect only applies to secret provider options (storageSources.<type>.options.<name>, for example secretAccessKey, password, token).");
        }

        var result = await CliSupport.EditConfigAsync(ctx, root => ConfigEditor.Set(root, canonical, value?.DeepClone())).ConfigureAwait(false);
        if (!result.Applied)
        {
            return await CliSupport.RejectAsync(ctx, result).ConfigureAwait(false);
        }

        warnings.AddRange(result.Warnings);
        var shown = secret ? (value is JsonValue v && v.TryGetValue<string>(out var s) ? JsonValue.Create(SecretReferences.Redact(canonical[3], s)) : JsonValue.Create(SecretReferences.RedactedValue)) : value;
        await CliSupport.EmitAsync(
            ctx,
            new { key = string.Join('.', canonical), value = shown?.DeepClone(), changed = result.Changed, backup = result.BackupPath, warnings },
            (result.Changed ? $"Set {string.Join('.', canonical)} = {(secret ? "(hidden)" : shown?.ToJsonString(CliJson.Compact) ?? "null")}" : $"{key} already has that value; nothing changed.")
                + (result.BackupPath != null ? $"{Environment.NewLine}Previous config saved at {result.BackupPath}" : string.Empty)
                + string.Concat(warnings.Select(w => $"{Environment.NewLine}Warning: {w}"))).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> UnsetAsync(CliContext ctx)
    {
        var key = ctx.Parsed.Positionals[0];
        string[] segments;
        try
        {
            segments = ConfigEditor.ParseKey(key);
        }
        catch (ConfigEditException ex)
        {
            throw CliSupport.Usage(ex.Message);
        }

        var existed = false;
        var result = await CliSupport.EditConfigAsync(ctx, root => existed = ConfigEditor.Remove(root, segments)).ConfigureAwait(false);
        if (!result.Applied)
        {
            return await CliSupport.RejectAsync(ctx, result).ConfigureAwait(false);
        }

        await CliSupport.EmitAsync(
            ctx,
            new { key, existed, changed = result.Changed, backup = result.BackupPath, warnings = result.Warnings },
            (existed ? $"Removed {key}; its default applies now." : $"{key} is not set; nothing changed.")
                + (result.BackupPath != null ? $"{Environment.NewLine}Previous config saved at {result.BackupPath}" : string.Empty)).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> AddWatchAsync(CliContext ctx)
    {
        var parsed = ctx.Parsed;
        string stored;
        try
        {
            stored = CliSupport.NormalizeStoredPath(parsed.Positionals[0]);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw CliSupport.Usage($"'{parsed.Positionals[0]}' is not a valid folder path: {ex.Message}");
        }

        var expanded = ConfigManager.ExpandConfiguredPath(stored);
        var compare = CliSupport.NormalizeForCompare(expanded);
        if (ctx.Config.WatchDirectories.Any(w => CliSupport.NormalizeForCompare(w.ExpandedPath) == compare))
        {
            throw CliSupport.Usage($"'{stored}' is already watched.", "run 'restore.exe config list-watch' to see the watched folders");
        }

        if (!Directory.Exists(expanded) && !parsed.HasOption("--force"))
        {
            throw CliSupport.Usage($"The folder '{expanded}' does not exist.", "create it first, or pass --force to add it anyway");
        }

        var storage = parsed.GetOption("--storage");
        if (storage != null && !ctx.Config.StorageSources.ContainsKey(storage))
        {
            throw CliSupport.Usage($"Storage type '{storage}' is not configured (configured: {string.Join(", ", ctx.Config.StorageSources.Keys)}).", "configure it with 'restore.exe config set-storage'");
        }

        var result = await CliSupport.EditConfigAsync(ctx, root =>
        {
            if (root["watchDirectories"] is not JsonArray list)
            {
                list = [];
                root["watchDirectories"] = list;
            }

            list.Add(new JsonObject { ["path"] = stored, ["storageType"] = storage });
        }).ConfigureAwait(false);
        if (!result.Applied)
        {
            return await CliSupport.RejectAsync(ctx, result).ConfigureAwait(false);
        }

        var warnings = new List<string>(result.Warnings);
        if (CliSupport.ExclusionWarning(ctx.Logger, ctx.Config, expanded) is { } exclusionWarning)
        {
            warnings.Add(exclusionWarning);
            ctx.Logger.Log(exclusionWarning, LogLevel.Warning);
        }

        await CliSupport.EmitAsync(
            ctx,
            new { path = stored, expandedPath = expanded, storageType = storage, added = true, backup = result.BackupPath, warnings },
            $"Watching {stored}" + (storage != null ? $" (storage: {storage})" : string.Empty)
                + (result.BackupPath != null ? $"{Environment.NewLine}Previous config saved at {result.BackupPath}" : string.Empty)
                + string.Concat(result.Warnings.Select(w => $"{Environment.NewLine}Warning: {w}"))).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> RemoveWatchAsync(CliContext ctx)
    {
        string compare;
        try
        {
            compare = CliSupport.NormalizeForCompare(ConfigManager.ExpandConfiguredPath(CliSupport.NormalizeStoredPath(ctx.Parsed.Positionals[0])));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw CliSupport.Usage($"'{ctx.Parsed.Positionals[0]}' is not a valid folder path: {ex.Message}");
        }

        var index = ctx.Config.WatchDirectories.FindIndex(w => CliSupport.NormalizeForCompare(w.ExpandedPath) == compare);
        if (index < 0)
        {
            throw CliSupport.Usage($"'{ctx.Parsed.Positionals[0]}' is not a watched folder.", "run 'restore.exe config list-watch' to see the watched folders");
        }

        var path = ctx.Config.WatchDirectories[index].Path;
        var result = await CliSupport.EditConfigAsync(ctx, root =>
        {
            if (root["watchDirectories"] is JsonArray list && index < list.Count)
            {
                list.RemoveAt(index);
            }
        }).ConfigureAwait(false);
        if (!result.Applied)
        {
            return await CliSupport.RejectAsync(ctx, result).ConfigureAwait(false);
        }

        await CliSupport.EmitAsync(
            ctx,
            new { path, removed = true, backup = result.BackupPath, warnings = result.Warnings },
            $"No longer watching {path}. Its existing snapshots stay in the storage."
                + (result.BackupPath != null ? $"{Environment.NewLine}Previous config saved at {result.BackupPath}" : string.Empty)).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> ListWatchAsync(CliContext ctx)
    {
        var items = ctx.Config.WatchDirectories.Select(w => new
        {
            path = w.Path,
            expandedPath = w.ExpandedPath,
            storageType = w.StorageType,
            schedule = string.IsNullOrWhiteSpace(w.Schedule) ? null : w.Schedule,
            exists = Directory.Exists(w.ExpandedPath)
        }).ToList();

        await CliSupport.EmitAsync(
            ctx,
            new { watchDirectories = items },
            items.Count == 0
                ? "No folders are watched."
                : string.Join(Environment.NewLine, items.Select(i => $"{i.path}{(i.storageType != null ? $"  (storage: {i.storageType})" : string.Empty)}{(i.schedule != null ? $"  (schedule: {i.schedule})" : string.Empty)}{(i.exists ? string.Empty : "  [missing]")}"))).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    // ---- schedules and the backup window ---------------------------------------------------------------------------

    private static string? ValidateSetSchedule(Program.ParsedCommand parsed)
    {
        var expression = string.Join(' ', parsed.Positionals.Skip(1));
        if (expression.Length == 0)
        {
            return "Give the schedule after the target, for example: config set-schedule system \"daily 03:00\".";
        }

        return BackupSchedule.TryParse(expression, out _, out var error)
            ? null
            : $"Invalid schedule '{expression}': {error} Run 'restore.exe schedule preview \"<expression>\"' to check an expression.";
    }

    private static string? ValidateSetWindow(Program.ParsedCommand parsed)
        => BackupWindow.TryCreate(parsed.Positionals[0], parsed.Positionals[1], parsed.GetOption("--days"), out _, out var error) ? null : error;

    private const string SystemTarget = "system";

    /// <summary>The index of the watched folder named by <paramref name="target"/>, or -1 for the system backup; a usage error when it is neither.</summary>
    private static int ResolveScheduleTarget(CliContext ctx, string target)
    {
        if (string.Equals(target.Trim(), SystemTarget, StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        string compare;
        try
        {
            compare = CliSupport.NormalizeForCompare(ConfigManager.ExpandConfiguredPath(CliSupport.NormalizeStoredPath(target)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw CliSupport.Usage($"'{target}' is not a valid folder path: {ex.Message}");
        }

        var index = ctx.Config.WatchDirectories.FindIndex(w => CliSupport.NormalizeForCompare(w.ExpandedPath) == compare);
        return index >= 0
            ? index
            : throw CliSupport.Usage($"'{target}' is not a watched folder (and is not 'system').", "run 'restore.exe config list-watch' to see the watched folders");
    }

    private static string DescribeTarget(CliContext ctx, int index) => index < 0 ? SystemTarget : ctx.Config.WatchDirectories[index].Path;

    private static async Task<int> SetScheduleAsync(CliContext ctx)
    {
        var index = ResolveScheduleTarget(ctx, ctx.Parsed.Positionals[0]);
        var schedule = BackupSchedule.Parse(string.Join(' ', ctx.Parsed.Positionals.Skip(1)));
        var target = DescribeTarget(ctx, index);

        var result = await CliSupport.EditConfigAsync(ctx, root =>
        {
            JsonObject? entry;
            if (index < 0)
            {
                if (root["systemBackup"] is not JsonObject system)
                {
                    system = [];
                    root["systemBackup"] = system;
                }

                entry = system;
            }
            else
            {
                var list = root["watchDirectories"] as JsonArray
                    ?? throw new ConfigEditException("config.json has no watchDirectories list.");
                if (index >= list.Count)
                {
                    throw new ConfigEditException($"The watched folder '{target}' is not in the watchDirectories list of config.json.");
                }

                // The oldest configs list a folder as a plain string; it becomes an object so it can carry a schedule.
                if (list[index] is JsonValue plain && plain.TryGetValue<string>(out var plainPath))
                {
                    list[index] = new JsonObject { ["path"] = plainPath, ["storageType"] = null };
                }

                entry = list[index] as JsonObject
                    ?? throw new ConfigEditException($"The watched folder '{target}' is not an object in config.json; edit it with 'config set watchDirectories.{index}.schedule'.");
            }

            entry["schedule"] = schedule.Expression;
        }).ConfigureAwait(false);
        if (!result.Applied)
        {
            return await CliSupport.RejectAsync(ctx, result).ConfigureAwait(false);
        }

        var runs = schedule.NextOccurrences(DateTime.UtcNow, 3, TimeZoneInfo.Local);
        await CliSupport.EmitAsync(
            ctx,
            new
            {
                target = index < 0 ? SystemTarget : "folder",
                path = index < 0 ? null : target,
                schedule = schedule.Expression,
                description = schedule.Description,
                kind = schedule.Kind.ToString().ToLowerInvariant(),
                nextRuns = runs.Select(r => r.ToString("o", System.Globalization.CultureInfo.InvariantCulture)),
                changed = result.Changed,
                backup = result.BackupPath,
                warnings = result.Warnings
            },
            (result.Changed ? $"{target}: {schedule.Description} ({schedule.Expression})" : $"{target} already has that schedule ({schedule.Expression}); nothing changed.")
                + (result.BackupPath != null ? $"{Environment.NewLine}Previous config saved at {result.BackupPath}" : string.Empty)
                + string.Concat(result.Warnings.Select(w => $"{Environment.NewLine}Warning: {w}"))).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> ClearScheduleAsync(CliContext ctx)
    {
        var index = ResolveScheduleTarget(ctx, ctx.Parsed.Positionals[0]);
        var target = DescribeTarget(ctx, index);
        var existed = false;

        var result = await CliSupport.EditConfigAsync(ctx, root =>
        {
            var entry = index < 0 ? root["systemBackup"] as JsonObject : (root["watchDirectories"] as JsonArray)?[index] as JsonObject;
            existed = entry != null && ConfigEditor.Remove(entry, ["schedule"]);
        }).ConfigureAwait(false);
        if (!result.Applied)
        {
            return await CliSupport.RejectAsync(ctx, result).ConfigureAwait(false);
        }

        await CliSupport.EmitAsync(
            ctx,
            new { target = index < 0 ? SystemTarget : "folder", path = index < 0 ? null : target, existed, changed = result.Changed, backup = result.BackupPath, warnings = result.Warnings },
            (existed ? $"{target} has no schedule now; the interval applies." : $"{target} has no schedule; nothing changed.")
                + (result.BackupPath != null ? $"{Environment.NewLine}Previous config saved at {result.BackupPath}" : string.Empty)).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> SetWindowAsync(CliContext ctx)
    {
        var parsed = ctx.Parsed;
        var window = BackupWindow.TryCreate(parsed.Positionals[0], parsed.Positionals[1], parsed.GetOption("--days"), out var created, out var error)
            ? created
            : throw CliSupport.Usage(error);

        var result = await CliSupport.EditConfigAsync(ctx, root =>
        {
            if (root["backupWindow"] is not JsonObject entry)
            {
                entry = [];
                root["backupWindow"] = entry;
            }

            entry["enabled"] = true;
            entry["start"] = window.StartText;
            entry["end"] = window.EndText;
            entry["days"] = window.DaysText;
        }).ConfigureAwait(false);
        if (!result.Applied)
        {
            return await CliSupport.RejectAsync(ctx, result).ConfigureAwait(false);
        }

        var warnings = new List<string>(result.Warnings);
        if (window.IsAlwaysOpen)
        {
            warnings.Add("Start equals end and every day is selected, so the window is open all the time and restricts nothing.");
        }

        await CliSupport.EmitAsync(
            ctx,
            new
            {
                window = new { enabled = true, start = window.StartText, end = window.EndText, days = window.DaysText, description = window.Describe() },
                changed = result.Changed,
                backup = result.BackupPath,
                warnings
            },
            (result.Changed ? $"Backup window set: {window.Describe()}." : $"The backup window is already {window.Describe()}; nothing changed.")
                + (result.BackupPath != null ? $"{Environment.NewLine}Previous config saved at {result.BackupPath}" : string.Empty)
                + string.Concat(warnings.Select(w => $"{Environment.NewLine}Warning: {w}"))).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> ClearWindowAsync(CliContext ctx)
    {
        var existed = false;
        var result = await CliSupport.EditConfigAsync(ctx, root => existed = ConfigEditor.Remove(root, ["backupWindow"])).ConfigureAwait(false);
        if (!result.Applied)
        {
            return await CliSupport.RejectAsync(ctx, result).ConfigureAwait(false);
        }

        await CliSupport.EmitAsync(
            ctx,
            new { existed, changed = result.Changed, backup = result.BackupPath, warnings = result.Warnings },
            (existed ? "The backup window is removed; automatic backups may start at any time." : "There is no backup window; nothing changed.")
                + (result.BackupPath != null ? $"{Environment.NewLine}Previous config saved at {result.BackupPath}" : string.Empty)).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> SetStorageAsync(CliContext ctx)
    {
        var parsed = ctx.Parsed;
        var info = StorageCatalog.Find(parsed.Positionals[0])!;
        var options = CliSupport.ParsePairs(parsed.Options.GetValueOrDefault("--option") ?? [], "--option");
        foreach (var name in parsed.Options.GetValueOrDefault("--option-stdin") ?? [])
        {
            var line = CliSupport.ReadStdinLine()
                ?? throw CliSupport.Usage($"--option-stdin {name}: stdin has no more lines.", "pipe one line per --option-stdin, in the order given");
            options.Add((name, line));
        }

        var warnings = new List<string>();
        var change = new StorageChange(info, parsed.GetOption("--path"), options, parsed.HasOption("--default"), parsed.HasOption("--protect"));
        var result = await CliSupport.EditConfigAsync(ctx, root => StorageSetup.Apply(root, change, warnings)).ConfigureAwait(false);
        if (!result.Applied)
        {
            return await CliSupport.RejectAsync(ctx, result).ConfigureAwait(false);
        }

        warnings.AddRange(result.Warnings);
        var source = ctx.Config.StorageSources[info.Type];
        var shownOptions = source.Options.ToDictionary(o => o.Key, o => SecretReferences.Redact(o.Key, o.Value));
        var nextSteps = new List<string>();
        if (info.NextStep != null)
        {
            nextSteps.Add(info.NextStep);
        }

        nextSteps.Add($"restore.exe probe --storage {info.Type}");

        await CliSupport.EmitAsync(
            ctx,
            new { type = info.Type, path = info.Remote ? null : source.Path, @default = ctx.Config.GlobalStorageType == info.Type, options = shownOptions, backup = result.BackupPath, warnings, nextSteps },
            $"Configured the {info.Type} storage{(ctx.Config.GlobalStorageType == info.Type ? " (default)" : string.Empty)}."
                + string.Concat(warnings.Select(w => $"{Environment.NewLine}Warning: {w}"))
                + string.Concat(nextSteps.Select(s => $"{Environment.NewLine}Next: {s}"))).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }
}
