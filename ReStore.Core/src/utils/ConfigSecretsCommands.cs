using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReStore.Core.src.utils;

/// <summary>Implementation of the <c>config show</c> and <c>config protect</c> CLI commands.</summary>
public static class ConfigSecretsCommands
{
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The config document with secret provider options masked as <c>***</c>. References stay visible
    /// (<c>env:NAME</c> as written, <c>dpapi:</c> blobs shortened to <c>dpapi:…</c>).
    /// </summary>
    public static string RedactConfigJson(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("Configuration root must be a JSON object.");

        if (root["storageSources"] is JsonObject sources)
        {
            foreach (var source in sources)
            {
                if (source.Value is not JsonObject sourceObject || sourceObject["options"] is not JsonObject options)
                {
                    continue;
                }

                foreach (var option in options.ToList())
                {
                    if (option.Value is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        options[option.Key] = SecretReferences.Redact(option.Key, text);
                    }
                    else if (option.Value != null && SecretReferences.IsSecretOption(option.Key))
                    {
                        // A secret key holding a number, bool, object or array must not leak either.
                        options[option.Key] = SecretReferences.RedactedValue;
                    }
                }
            }
        }

        return root.ToJsonString(IndentedOptions);
    }

    /// <summary>
    /// Rewrites literal secret options of every storage source to <c>dpapi:</c> references and saves.
    /// Returns the changed <c>source.option</c> names (never values); nothing is saved when none changed.
    /// </summary>
    public static async Task<List<string>> ProtectAsync(IConfigManager configManager, ISecretProtector? protector = null)
    {
        var changed = new List<string>();
        foreach (var (name, source) in configManager.StorageSources)
        {
            changed.AddRange(SecretReferences.ProtectSecretOptions(source.Options, protector).Select(option => $"{name}.{option}"));
        }

        if (changed.Count > 0)
        {
            await configManager.SaveAsync().ConfigureAwait(false);
        }

        // The copies under backups/ hold the secrets the file held before: protect them too (or delete the ones that cannot be).
        try
        {
            if (JsonNode.Parse(await File.ReadAllTextAsync(configManager.GetConfigFilePath()).ConfigureAwait(false)) is JsonObject live)
            {
                ConfigBackups.ScrubExisting(configManager.GetConfigFilePath(), live, protector, protectRemaining: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // The live file is protected either way; an unreadable one cannot be used to scrub copies.
        }

        return changed;
    }
}
