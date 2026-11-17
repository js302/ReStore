using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReStore.Core.src.utils;

/// <summary>A requested config edit cannot be expressed (bad key path, wrong kind of parent, unknown key).</summary>
public sealed class ConfigEditException(string message) : Exception(message);

/// <param name="Applied">True when the edit is in effect (including an edit that changed nothing).</param>
/// <param name="Changed">True when the file was rewritten.</param>
/// <param name="Errors">Validation errors the edit introduced; the file is untouched when there are any.</param>
/// <param name="Warnings">Validation warnings the edit introduced.</param>
/// <param name="BackupPath">Where the previous config.json was kept, when the file was rewritten.</param>
public sealed record ConfigEditResult(bool Applied, bool Changed, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, string? BackupPath);

/// <summary>
/// Validate-then-apply edits of config.json made on the JSON document itself (so unknown keys, <c>env:</c> references and
/// <c>%VAR%</c> values survive untouched), like <see cref="ConfigPortability.ImportAsync"/>: the edited copy is loaded and
/// validated as a throwaway config first, and only a copy that introduces no new error replaces the file, after the previous
/// one was saved under backups/. Errors the config already had before the edit do not block it.
/// </summary>
public static class ConfigEditor
{
    /// <param name="requireValid">True to refuse any validation error, including ones the config already had (first-time setup).</param>
    public static async Task<ConfigEditResult> ApplyAsync(IConfigManager manager, Action<JsonObject> edit, bool requireValid = false)
    {
        var configPath = manager.GetConfigFilePath();
        var currentText = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
        var current = JsonNode.Parse(currentText) as JsonObject
            ?? throw new InvalidOperationException("Configuration root must be a JSON object.");

        var candidate = (JsonObject)current.DeepClone();
        edit(candidate);
        var unchanged = JsonNode.DeepEquals(current, candidate);
        if (unchanged && !requireValid)
        {
            return new ConfigEditResult(true, false, [], [], null);
        }

        // What the config already had wrong does not block an unrelated edit (unless the caller wants a fully valid result).
        var baseline = requireValid
            ? new ConfigValidationResult()
            : await ConfigPortability.ValidateCandidateAsync(current, StorageChecks, "current").ConfigureAwait(false);
        var validation = await ConfigPortability.ValidateCandidateAsync(candidate, StorageChecks, "resulting").ConfigureAwait(false);
        var newErrors = validation.Errors.Except(baseline.Errors).ToList();

        // The loader silently resets a wrong-typed or out-of-range value to its default, so the generic validator never sees it:
        // check the edited document itself against the schema (only the problems the edit introduces block it).
        var schemaBaseline = requireValid ? [] : ConfigSchemaValidator.Validate(current);
        newErrors.AddRange(ConfigSchemaValidator.Validate(candidate).Except(schemaBaseline));
        var newWarnings = validation.Warnings.Except(baseline.Warnings).ToList();
        if (newErrors.Count > 0)
        {
            return new ConfigEditResult(false, false, newErrors, newWarnings, null);
        }

        if (unchanged)
        {
            return new ConfigEditResult(true, false, [], newWarnings, null);
        }

        var backupPath = await ConfigPortability.WriteConfigAsync(configPath, currentText, candidate, "change").ConfigureAwait(false);
        try
        {
            await manager.LoadAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            await File.WriteAllTextAsync(configPath, currentText).ConfigureAwait(false);
            try
            {
                await manager.LoadAsync().ConfigureAwait(false);
            }
            catch (Exception reloadEx) when (reloadEx is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                newWarnings.Add($"The previous configuration was restored but could not be reloaded: {reloadEx.Message}");
            }

            return new ConfigEditResult(false, false, [$"The change could not be applied and the previous configuration was restored: {ex.Message}"], newWarnings, null);
        }

        return new ConfigEditResult(true, true, [], newWarnings, backupPath);
    }

    /// <summary>The checks the generic validator does not make: the default and per-folder storage must exist and be usable.</summary>
    private static void StorageChecks(ConfigManager config, ConfigValidationResult result)
    {
        var known = string.Join(", ", config.StorageSources.Keys.Order(StringComparer.Ordinal));
        if (!config.StorageSources.ContainsKey(config.GlobalStorageType))
        {
            result.AddError($"globalStorageType '{config.GlobalStorageType}' is not a key of storageSources ({known}).");
        }
        else
        {
            foreach (var error in new ConfigValidator(new ConfigPortability.SilentLogger()).ValidateStorageOnly(config, config.GlobalStorageType).Errors)
            {
                if (!result.Errors.Contains(error))
                {
                    result.AddError(error);
                }
            }
        }

        foreach (var watch in config.WatchDirectories.Where(w => w.StorageType != null && !config.StorageSources.ContainsKey(w.StorageType)))
        {
            result.AddError($"Watch directory '{watch.Path}' uses storageType '{watch.StorageType}', which is not a key of storageSources ({known}).");
        }
    }

    // ---- dotted key paths ------------------------------------------------------------------------------------------

    /// <summary>Splits <c>retention.keepDaily</c> / <c>watchDirectories.0.path</c> into segments.</summary>
    public static string[] ParseKey(string key)
    {
        var segments = key.Split('.');
        if (string.IsNullOrWhiteSpace(key) || segments.Any(string.IsNullOrWhiteSpace))
        {
            throw new ConfigEditException($"'{key}' is not a valid key. Use a dotted path such as retention.keepDaily or storageSources.s3.options.bucketName.");
        }

        return segments;
    }

    /// <summary>The node at <paramref name="segments"/>, or null (also for a JSON null). Object keys match case-insensitively when there is no exact match.</summary>
    public static bool TryGet(JsonNode? root, IReadOnlyList<string> segments, out JsonNode? value)
    {
        var node = root;
        foreach (var segment in segments)
        {
            if (node is JsonObject obj && FindKey(obj, segment) is { } key)
            {
                node = obj[key];
            }
            else if (node is JsonArray array && TryIndex(segment, out var index) && index < array.Count)
            {
                node = array[index];
            }
            else
            {
                value = null;
                return false;
            }
        }

        value = node;
        return true;
    }

    /// <summary>Sets the node at <paramref name="segments"/>, creating missing objects on the way; arrays are addressed by existing index.</summary>
    public static void Set(JsonObject root, IReadOnlyList<string> segments, JsonNode? value)
    {
        JsonNode parent = root;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var segment = segments[i];
            JsonNode? child;
            if (parent is JsonObject obj)
            {
                var key = FindKey(obj, segment) ?? segment;
                child = obj[key];
                if (child == null)
                {
                    obj[key] = child = new JsonObject();
                }
            }
            else
            {
                child = ArrayItem((JsonArray)parent, segment, string.Join('.', segments.Take(i + 1)));
            }

            parent = child ?? throw new ConfigEditException($"'{string.Join('.', segments.Take(i + 1))}' is null; set the whole value instead.");
            if (parent is not (JsonObject or JsonArray))
            {
                throw new ConfigEditException($"'{string.Join('.', segments.Take(i + 1))}' is a plain value, not an object, so '{string.Join('.', segments)}' cannot be set.");
            }
        }

        var last = segments[^1];
        if (parent is JsonObject target)
        {
            target[FindKey(target, last) ?? last] = value;
        }
        else
        {
            var array = (JsonArray)parent;
            var index = RequireIndex(array, last, string.Join('.', segments));
            array[index] = value;
        }
    }

    /// <summary>Removes the member or array element at <paramref name="segments"/>; false when it does not exist.</summary>
    public static bool Remove(JsonObject root, IReadOnlyList<string> segments)
    {
        if (!TryGet(root, segments.Take(segments.Count - 1).ToList(), out var parent) || parent == null)
        {
            return false;
        }

        var last = segments[^1];
        if (parent is JsonObject obj && FindKey(obj, last) is { } key)
        {
            return obj.Remove(key);
        }

        if (parent is JsonArray array && TryIndex(last, out var index) && index < array.Count)
        {
            array.RemoveAt(index);
            return true;
        }

        return false;
    }

    private static string? FindKey(JsonObject obj, string segment)
    {
        if (obj.ContainsKey(segment))
        {
            return segment;
        }

        return obj.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, segment, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryIndex(string segment, out int index) =>
        int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out index);

    private static int RequireIndex(JsonArray array, string segment, string fullKey) =>
        TryIndex(segment, out var index) && index < array.Count
            ? index
            : throw new ConfigEditException($"'{fullKey}': '{segment}' is not an existing index of the list (it has {array.Count} item(s), counting from 0).");

    private static JsonNode? ArrayItem(JsonArray array, string segment, string fullKey) => array[RequireIndex(array, segment, fullKey)];
}
