using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReStore.Core.src.utils;

public enum ConfigImportMode
{
    /// <summary>Imported values are laid over the current config; sources, directories and options not in the file are kept.</summary>
    Merge,

    /// <summary>The imported config replaces the current one (current secrets of matching providers are still kept when the file has none).</summary>
    Replace
}

/// <summary>Outcome of <see cref="ConfigPortability.ImportAsync"/>.</summary>
public sealed record ConfigImportResult(bool Applied, List<string> Errors, List<string> Warnings, List<string> Notes);

/// <summary>
/// Export/import of the settings that are safe to move between machines: config.json without any secret option
/// values, plus the app preferences (appsettings.json) and theme.
/// </summary>
public static class ConfigPortability
{
    public const int FormatVersion = 1;
    private const string FormatMarker = "restoreSettingsExport";

    private static readonly string[] AppSettingKeys = ["ShowOnlyConfiguredProviders", "MinimizeToTray"];
    private static readonly string[] ThemeKeys = ["Preference"];

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    /// <summary>Builds the export document for the current files on disk.</summary>
    public static async Task<string> ExportAsync(IConfigManager configManager)
    {
        var configJson = await File.ReadAllTextAsync(configManager.GetConfigFilePath()).ConfigureAwait(false);
        return BuildExport(configJson, await ReadIfExistsAsync(AppPaths.AppSettingsFile).ConfigureAwait(false),
            await ReadIfExistsAsync(AppPaths.ThemeSettingsFile).ConfigureAwait(false));
    }

    /// <summary>
    /// Builds the export document. Every secret provider option is removed unless it is an <c>env:NAME</c> reference;
    /// <c>dpapi:</c> blobs are removed too because they only decrypt for the same Windows user on the same machine,
    /// and so is any option value that is a URL with embedded credentials or a SAS/presigned query (defence in depth).
    /// Identifier options such as <c>accessKeyId</c>, <c>accountName</c> or <c>username</c> are not secrets and are kept
    /// (they are needed to recognise the account); only the paired secret value is removed.
    /// </summary>
    public static string BuildExport(string configJson, string? appSettingsJson = null, string? themeJson = null)
    {
        var config = JsonNode.Parse(configJson) as JsonObject
            ?? throw new InvalidOperationException("Configuration root must be a JSON object.");
        StripSecrets(config);

        var export = new JsonObject
        {
            [FormatMarker] = FormatVersion,
            ["config"] = config
        };

        if (PickKeys(appSettingsJson, AppSettingKeys) is { } appSettings)
        {
            export["appSettings"] = appSettings;
        }

        if (PickKeys(themeJson, ThemeKeys) is { } theme)
        {
            export["theme"] = theme;
        }

        return export.ToJsonString(IndentedOptions);
    }

    private static void StripSecrets(JsonObject config)
    {
        if (config["storageSources"] is not JsonObject sources)
        {
            return;
        }

        foreach (var source in sources)
        {
            if (source.Value is not JsonObject { } sourceObject || sourceObject["options"] is not JsonObject options)
            {
                continue;
            }

            foreach (var option in options.ToList())
            {
                var text = option.Value is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
                var isSecretKey = SecretReferences.IsSecretOption(option.Key);
                if (SecretReferences.IsDpapiReference(text) || (isSecretKey && !SecretReferences.IsEnvReference(text)) || HasEmbeddedCredentials(text))
                {
                    options.Remove(option.Key);
                }
            }
        }
    }

    /// <summary>True for a URL with <c>user:password@</c> or a SAS / presigned-URL signature query.</summary>
    internal static bool HasEmbeddedCredentials(string? value) =>
        !string.IsNullOrEmpty(value) && (CredentialedUrlPattern.IsMatch(value) || SignedQueryPattern.IsMatch(value));

    private static readonly System.Text.RegularExpressions.Regex CredentialedUrlPattern =
        new(@"^[a-z][a-z0-9+.\-]*://[^/\s:@]+:[^/\s@]+@", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex SignedQueryPattern =
        new(@"[?&](?:sig|signature|X-Amz-Signature|X-Amz-Credential|X-Amz-Security-Token)=", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Validates the export file and applies it. Nothing is changed when the file is malformed or the resulting
    /// configuration fails <see cref="ConfigValidator"/>. On success the loaded <paramref name="configManager"/>
    /// is reloaded and saved.
    /// </summary>
    public static async Task<ConfigImportResult> ImportAsync(IConfigManager configManager, string importFile, ConfigImportMode mode)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var notes = new List<string>();

        JsonObject importedConfig;
        JsonObject? importedAppSettings;
        JsonObject? importedTheme;
        try
        {
            var text = await File.ReadAllTextAsync(importFile).ConfigureAwait(false);
            var root = JsonNode.Parse(text) as JsonObject ?? throw new InvalidOperationException("The file does not contain a JSON object.");
            if (root[FormatMarker] != null)
            {
                importedConfig = root["config"] as JsonObject ?? throw new InvalidOperationException("The export has no 'config' section.");
                importedAppSettings = root["appSettings"] as JsonObject;
                importedTheme = root["theme"] as JsonObject;
            }
            else if (root["storageSources"] != null || root["watchDirectories"] != null)
            {
                importedConfig = root; // a bare config.json
                importedAppSettings = null;
                importedTheme = null;
            }
            else
            {
                throw new InvalidOperationException("The file is not a ReStore settings export.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            errors.Add($"Could not read '{importFile}': {ex.Message}");
            return new ConfigImportResult(false, errors, warnings, notes);
        }

        var configPath = configManager.GetConfigFilePath();
        var currentText = File.Exists(configPath) ? await File.ReadAllTextAsync(configPath).ConfigureAwait(false) : null;
        var current = currentText != null ? JsonNode.Parse(currentText) as JsonObject : null;

        var candidate = Combine(current, importedConfig, mode, notes);

        // Validate a throwaway copy first; the real config is only touched when this passes.
        var validation = await ValidateCandidateAsync(candidate).ConfigureAwait(false);
        errors.AddRange(validation.Errors);
        warnings.AddRange(validation.Warnings);
        if (errors.Count > 0)
        {
            return new ConfigImportResult(false, errors, warnings, notes);
        }

        var backupPath = await WriteConfigAsync(configPath, currentText, candidate).ConfigureAwait(false);
        try
        {
            await configManager.LoadAsync().ConfigureAwait(false);
            await configManager.SaveAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            if (currentText != null)
            {
                await File.WriteAllTextAsync(configPath, currentText).ConfigureAwait(false);
                try
                {
                    await configManager.LoadAsync().ConfigureAwait(false);
                }
                catch (Exception reloadEx) when (reloadEx is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                {
                    warnings.Add($"The previous configuration was restored but could not be reloaded: {reloadEx.Message}");
                }
            }

            errors.Add($"The imported configuration could not be applied and the previous one was restored: {ex.Message}");
            return new ConfigImportResult(false, errors, warnings, notes);
        }

        if (backupPath != null)
        {
            notes.Add($"Previous configuration saved at {backupPath}");
        }

        if (await OverlayFileAsync(AppPaths.AppSettingsFile, importedAppSettings, AppSettingKeys).ConfigureAwait(false))
        {
            notes.Add("Application preferences imported.");
        }

        if (await OverlayFileAsync(AppPaths.ThemeSettingsFile, importedTheme, ThemeKeys).ConfigureAwait(false))
        {
            notes.Add("Theme imported.");
        }

        return new ConfigImportResult(true, errors, warnings, notes);
    }

    /// <summary>Whether the import also changed app preference/theme files (callers reload those).</summary>
    public static bool ChangedAppPreferences(ConfigImportResult result) =>
        result.Notes.Any(note => note.StartsWith("Application preferences", StringComparison.Ordinal) || note.StartsWith("Theme", StringComparison.Ordinal));

    internal static JsonObject Combine(JsonObject? current, JsonObject imported, ConfigImportMode mode, List<string> notes)
    {
        var result = mode == ConfigImportMode.Replace || current == null
            ? (JsonObject)imported.DeepClone()
            : MergeInto((JsonObject)current.DeepClone(), imported);

        if (mode == ConfigImportMode.Replace && current != null)
        {
            KeepCurrentSecrets(current, result, notes);
        }

        return result;
    }

    private static JsonObject MergeInto(JsonObject target, JsonObject imported)
    {
        foreach (var member in imported)
        {
            var incoming = member.Value;
            if (incoming == null)
            {
                continue;
            }

            if (member.Key == "storageSources" && incoming is JsonObject sources && target[member.Key] is JsonObject targetSources)
            {
                foreach (var source in sources)
                {
                    if (source.Value is JsonObject sourceObject && targetSources[source.Key] is JsonObject existing)
                    {
                        MergeInto(existing, sourceObject);
                    }
                    else
                    {
                        targetSources[source.Key] = source.Value?.DeepClone();
                    }
                }
            }
            else if (member.Key == "watchDirectories" && incoming is JsonArray directories && target[member.Key] is JsonArray targetDirectories)
            {
                var known = targetDirectories.Select(PathOf).Where(p => p != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var directory in directories)
                {
                    var path = PathOf(directory);
                    if (path != null && known.Add(path))
                    {
                        targetDirectories.Add(directory!.DeepClone());
                    }
                }
            }
            else if (incoming is JsonObject incomingObject && target[member.Key] is JsonObject targetObject)
            {
                MergeInto(targetObject, incomingObject);
            }
            else
            {
                target[member.Key] = incoming.DeepClone();
            }
        }

        return target;
    }

    private static string? PathOf(JsonNode? directory) =>
        directory is JsonObject obj && obj["path"] is JsonValue value && value.TryGetValue<string>(out var path) ? path : null;

    private static void KeepCurrentSecrets(JsonObject current, JsonObject result, List<string> notes)
    {
        if (current["storageSources"] is not JsonObject currentSources || result["storageSources"] is not JsonObject resultSources)
        {
            return;
        }

        foreach (var source in resultSources)
        {
            if (source.Value is not JsonObject sourceObject || currentSources[source.Key] is not JsonObject currentSource
                || currentSource["options"] is not JsonObject currentOptions)
            {
                continue;
            }

            var options = sourceObject["options"] as JsonObject;
            foreach (var option in currentOptions)
            {
                if (!SecretReferences.IsSecretOption(option.Key) || option.Value == null)
                {
                    continue;
                }

                var existing = options?[option.Key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                if (!string.IsNullOrEmpty(existing))
                {
                    continue;
                }

                if (options == null)
                {
                    options = [];
                    sourceObject["options"] = options;
                }

                options[option.Key] = option.Value.DeepClone();
                notes.Add($"Kept the current secret {source.Key}.{option.Key}.");
            }
        }
    }

    /// <summary>
    /// Loads and validates <paramref name="candidate"/> as a throwaway config.json in the temp folder; <paramref name="extraChecks"/>
    /// may add errors for the loaded model (used by <see cref="ConfigEditor"/>).
    /// </summary>
    internal static async Task<ConfigValidationResult> ValidateCandidateAsync(JsonObject candidate, Action<ConfigManager, ConfigValidationResult>? extraChecks = null, string subject = "imported")
    {
        var result = new ConfigValidationResult();
        var directory = Path.Combine(Path.GetTempPath(), "restore-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "config.json");
            await File.WriteAllTextAsync(path, candidate.ToJsonString(IndentedOptions)).ConfigureAwait(false);

            var silent = new SilentLogger();
            var manager = new ConfigManager(silent, path);
            await manager.LoadAsync().ConfigureAwait(false);
            var validation = new ConfigValidator(silent).ValidateConfiguration(manager);
            result.Errors.AddRange(validation.Errors);
            result.Warnings.AddRange(validation.Warnings);
            extraChecks?.Invoke(manager, result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            result.Errors.Add($"The {subject} configuration is not valid: {ex.Message}"
                + (ex.InnerException != null ? $" ({ex.InnerException.Message})" : string.Empty));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Scratch directory in %TEMP%; nothing to do.
            }
        }

        return result;
    }

    /// <summary>Writes the candidate over the config (previous text kept under backups/ as <c>config.pre-&lt;label&gt;.*.json</c>); returns the backup path.</summary>
    internal static async Task<string?> WriteConfigAsync(string configPath, string? currentText, JsonObject candidate, string label = "import")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

        string? backupPath = null;
        if (currentText != null)
        {
            var backupDirectory = Path.Combine(Path.GetDirectoryName(configPath)!, "backups");
            Directory.CreateDirectory(backupDirectory);
            backupPath = Path.Combine(backupDirectory, $"config.pre-{label}.{DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture)}_{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(backupPath, ConfigBackups.ProtectedCopy(currentText, candidate)).ConfigureAwait(false);
        }

        var tempPath = SharedFile.UniqueTempPath(configPath);
        try
        {
            await File.WriteAllTextAsync(tempPath, candidate.ToJsonString(IndentedOptions)).ConfigureAwait(false);
            await SharedFile.MoveOverwriteAsync(tempPath, configPath).ConfigureAwait(false);
        }
        catch
        {
            SharedFile.TryDelete(tempPath);
            throw;
        }

        // Older copies follow the live file: a secret protected by this write must not stay in plain text in them, and only the newest are kept.
        ConfigBackups.ScrubExisting(configPath, candidate, null, protectRemaining: false);
        ConfigBackups.Prune(configPath);
        return backupPath;
    }

    private static async Task<string?> ReadIfExistsAsync(string path) =>
        File.Exists(path) ? await File.ReadAllTextAsync(path).ConfigureAwait(false) : null;

    /// <summary>The whitelisted members of <paramref name="json"/> (matched case-insensitively), or null when there are none.</summary>
    private static JsonObject? PickKeys(string? json, string[] keys)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return PickKeys(JsonNode.Parse(json) as JsonObject, keys);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject? PickKeys(JsonObject? source, string[] keys)
    {
        if (source == null)
        {
            return null;
        }

        var picked = new JsonObject();
        foreach (var key in keys)
        {
            var match = source.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
            if (match.Value is JsonValue)
            {
                picked[key] = match.Value.DeepClone();
            }
        }

        return picked.Count > 0 ? picked : null;
    }

    private static async Task<bool> OverlayFileAsync(string path, JsonObject? imported, string[] keys)
    {
        var picked = PickKeys(imported, keys);
        if (picked == null)
        {
            return false;
        }

        JsonObject existing = [];
        if (File.Exists(path))
        {
            try
            {
                existing = JsonNode.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(false)) as JsonObject ?? [];
            }
            catch (JsonException)
            {
                existing = [];
            }
        }

        foreach (var member in picked)
        {
            existing[member.Key] = member.Value?.DeepClone();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, existing.ToJsonString(IndentedOptions)).ConfigureAwait(false);
        return true;
    }

    internal sealed class SilentLogger : ILogger
    {
        public void Log(string message, LogLevel level = LogLevel.Info)
        {
        }
    }
}
