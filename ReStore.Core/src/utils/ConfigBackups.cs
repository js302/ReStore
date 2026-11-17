using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReStore.Core.src.utils;

/// <summary>
/// The copies of config.json kept under <c>backups/config.pre-*.json</c> before a change, import or migration. They hold the same
/// secrets as config.json did, so they follow it: once a secret is protected in the live file the old copies are scrubbed, and only
/// the newest <see cref="KeepCount"/> are kept.
/// </summary>
public static class ConfigBackups
{
    public const int KeepCount = 20;

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string DirectoryFor(string configPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "backups");

    private static List<string> Files(string configPath)
    {
        var directory = DirectoryFor(configPath);
        return Directory.Exists(directory)
            ? [.. Directory.GetFiles(directory, "config.pre-*.json").OrderByDescending(File.GetLastWriteTimeUtc).ThenByDescending(f => f, StringComparer.Ordinal)]
            : [];
    }

    /// <summary>Deletes all but the newest <see cref="KeepCount"/> config backups. Best effort.</summary>
    public static void Prune(string configPath)
    {
        foreach (var old in Files(configPath).Skip(KeepCount))
        {
            SharedFile.TryDelete(old);
        }
    }

    /// <summary>
    /// The text of a backup of <paramref name="currentText"/> made while <paramref name="candidate"/> replaces it: a literal secret that
    /// the candidate stores as a reference (<c>dpapi:</c>, <c>env:</c>) is written to the backup as that reference, so the edit that
    /// protects a secret does not leave a plaintext copy of it behind.
    /// </summary>
    public static string ProtectedCopy(string currentText, JsonObject candidate)
    {
        try
        {
            if (JsonNode.Parse(currentText) is JsonObject current && Scrub(current, candidate, null))
            {
                return current.ToJsonString(IndentedOptions);
            }
        }
        catch (JsonException)
        {
            // A current file that is not JSON is kept as it is.
        }

        return currentText;
    }

    /// <summary>
    /// Scrubs the existing backups after secrets were protected: each literal secret option whose live value is a reference takes the
    /// live value, and a literal with no live counterpart is protected with <paramref name="protector"/> (the backup is deleted when
    /// that is not possible) when <paramref name="protectRemaining"/> is set. Returns the number of backups changed or removed.
    /// </summary>
    public static int ScrubExisting(string configPath, JsonObject live, ISecretProtector? protector, bool protectRemaining)
    {
        var touched = 0;
        foreach (var file in Files(configPath))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject backup)
                {
                    continue;
                }

                if (!Scrub(backup, live, protector ?? DpapiSecretProtector.Instance, protectRemaining, out var unprotectable))
                {
                    continue;
                }

                if (unprotectable)
                {
                    File.Delete(file);
                }
                else
                {
                    SharedFile.WriteAllTextAtomic(file, backup.ToJsonString(IndentedOptions));
                }

                touched++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // An unreadable backup holds nothing we can scrub; leave it.
            }
        }

        return touched;
    }

    private static bool Scrub(JsonObject copy, JsonObject live, ISecretProtector? protector) => Scrub(copy, live, protector, false, out _);

    private static bool Scrub(JsonObject copy, JsonObject live, ISecretProtector? protector, bool protectRemaining, out bool unprotectable)
    {
        unprotectable = false;
        var changed = false;
        if (copy["storageSources"] is not JsonObject sources)
        {
            return false;
        }

        foreach (var (name, source) in sources)
        {
            if (source is not JsonObject { } sourceObject || sourceObject["options"] is not JsonObject options)
            {
                continue;
            }

            var liveOptions = ((live["storageSources"] as JsonObject)?[name] as JsonObject)?["options"] as JsonObject;
            foreach (var key in options.Select(o => o.Key).ToList())
            {
                if (!SecretReferences.IsSecretOption(key) || options[key] is not JsonValue value || !value.TryGetValue<string>(out var text) || SecretReferences.IsProtectedOrEmpty(text))
                {
                    continue;
                }

                if (liveOptions?[key] is JsonValue liveValue && liveValue.TryGetValue<string>(out var liveText)
                    && !string.IsNullOrWhiteSpace(liveText) && SecretReferences.IsReference(liveText))
                {
                    options[key] = liveText;
                    changed = true;
                }
                else if (protector is { IsAvailable: true } && protectRemaining)
                {
                    options[key] = SecretReferences.Protect(text, protector);
                    changed = true;
                }
                else if (protectRemaining)
                {
                    unprotectable = true;
                    changed = true;
                }
            }
        }

        return changed;
    }
}
