using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReStore.Core.src.updates;

public enum UpdateStatus
{
    UpToDate,
    UpdateAvailable,
    Failed,
    Disabled
}

/// <summary>One downloadable file of a release (<c>kind</c>: <c>msix</c> or <c>cli-zip</c>).</summary>
public sealed record UpdateAsset(string Kind, string Arch, string Name, string Url, string? Sha256, long Size)
{
    public const string KindMsix = "msix";
    public const string KindCliZip = "cli-zip";
}

/// <summary>Text that came from a remote manifest and is about to be printed.</summary>
public static class UpdateText
{
    /// <summary>Removes control characters (escape sequences, carriage returns, bell...) so remote text cannot rewrite what the terminal shows.</summary>
    public static string? Clean(string? text) =>
        text == null ? null : string.Concat(text.Where(c => !char.IsControl(c)));
}

/// <summary>The <c>latest.json</c> published with each release.</summary>
public sealed record UpdateManifest(
    int SchemaVersion, string Version, string Tag, DateTime? PublishedUtc, string? ReleaseNotesUrl, bool Critical, IReadOnlyList<UpdateAsset> Assets)
{
    /// <summary>Reads and validates a manifest; returns null for anything that is not a usable schema-1 manifest.</summary>
    public static UpdateManifest? TryParse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var schema = root.TryGetProperty("schemaVersion", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var sv) ? sv : 0;
            if (schema < 1 || GetString(root, "version") is not { } versionText || SemVer.ParseOrNull(versionText) is not { } version)
            {
                return null;
            }

            var assets = new List<UpdateAsset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
                {
                    var kind = GetString(item, "kind");
                    var arch = GetString(item, "arch");
                    var name = GetString(item, "name");
                    var url = GetString(item, "url");
                    if (kind == null || arch == null || name == null || url == null)
                    {
                        continue;
                    }

                    var size = item.TryGetProperty("size", out var z) && z.ValueKind == JsonValueKind.Number && z.TryGetInt64(out var zv) ? zv : 0;
                    assets.Add(new UpdateAsset(kind.ToLowerInvariant(), arch.ToLowerInvariant(), name, url, GetString(item, "sha256")?.ToLowerInvariant(), size));
                }
            }

            DateTime? published = root.TryGetProperty("publishedUtc", out var p) && p.ValueKind == JsonValueKind.String
                && DateTime.TryParse(p.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var pd)
                ? pd : null;
            var critical = root.TryGetProperty("critical", out var c) && c.ValueKind == JsonValueKind.True;
            return new UpdateManifest(schema, version.ToString(), UpdateText.Clean(GetString(root, "tag")) ?? "v" + version, published, UpdateText.Clean(GetString(root, "releaseNotesUrl")), critical, assets);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString() : null;
}

/// <summary>The outcome of an update check. Never an exception: failures are <see cref="UpdateStatus.Failed"/> with <see cref="Error"/>.</summary>
public sealed record UpdateCheckResult
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public UpdateStatus Status { get; init; }

    public string Current { get; init; } = string.Empty;
    public string? Latest { get; init; }
    public bool Critical { get; init; }
    public string? ReleaseNotesUrl { get; init; }

    /// <summary>The asset for this install channel and architecture; null when the release has no manifest (redirect fallback) or no matching asset.</summary>
    public UpdateAsset? Asset { get; init; }

    public DateTime CheckedUtc { get; init; }
    public string? Error { get; init; }

    /// <summary>True when only the version number is known (the manifest was missing, so no assets can be installed).</summary>
    public bool VersionOnly { get; init; }

    public bool UpdateAvailable => Status == UpdateStatus.UpdateAvailable;
}

/// <summary>What <c>update-check.json</c> holds.</summary>
public sealed class UpdateCacheData
{
    public DateTime? LastCheckUtc { get; set; }
    public UpdateCheckResult? Result { get; set; }
    public string? SkippedVersion { get; set; }
    public string? LastNotifiedVersion { get; set; }
    public DateTime? LastNotifiedUtc { get; set; }
}
