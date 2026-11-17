using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ReStore.Core.src.utils;

/// <summary>What config.json looked like when a <see cref="ConfigManager"/> last read or wrote it (to notice edits made by someone else).</summary>
/// <param name="Length">Size of the text in bytes.</param>
/// <param name="LastWriteUtc">Last-write time of the file (informational: timestamps are too coarse and too easy to touch to decide anything).</param>
/// <param name="Hash">SHA-256 of the text; this decides whether the content changed.</param>
public sealed record ConfigFileSignature(long Length, DateTime LastWriteUtc, string Hash)
{
    /// <summary>True when both describe the same content (the time stamp is ignored).</summary>
    public bool SameContent(ConfigFileSignature? other) => other != null && other.Length == Length && other.Hash == Hash;

    public static ConfigFileSignature Of(string text, DateTime lastWriteUtc)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new ConfigFileSignature(bytes.Length, lastWriteUtc, Convert.ToHexString(SHA256.HashData(bytes)));
    }
}

/// <summary>
/// Three-way merge of config.json documents: <c>base</c> (what this process loaded or last wrote), <c>mine</c> (what this
/// process wants to write now) and <c>theirs</c> (what is on disk now, written by the CLI or another process). A member that
/// only one side changed takes that side's value, so an unrelated save never reverts someone else's edit. When both changed the
/// same member the newer edit (mine) wins and the path is reported, except for the encryption block, which is never written
/// back from a stale copy (the salt and verification token change together with the password).
/// </summary>
public static class ConfigMerger
{
    private const string EncryptionPath = "encryption";
    private const string WatchDirectoriesPath = "watchDirectories";

    private readonly record struct Slot(bool Present, JsonNode? Node)
    {
        public static readonly Slot Missing = new(false, null);

        public bool Same(Slot other) => Present == other.Present && JsonNode.DeepEquals(Node, other.Node);
    }

    /// <param name="conflicts">Receives the dotted path of each member both sides changed differently (mine was kept).</param>
    public static JsonObject Merge(JsonObject baseNode, JsonObject mine, JsonObject theirs, List<string> conflicts)
    {
        return MergeObjects(string.Empty, baseNode, mine, theirs, conflicts);
    }

    private static JsonObject MergeObjects(string path, JsonObject b, JsonObject m, JsonObject t, List<string> conflicts)
    {
        var result = new JsonObject();
        foreach (var key in t.Select(p => p.Key).Concat(m.Select(p => p.Key)).Distinct(StringComparer.Ordinal))
        {
            var childPath = path.Length == 0 ? key : $"{path}.{key}";
            var merged = MergeSlot(childPath, SlotOf(b, key), SlotOf(m, key), SlotOf(t, key), conflicts);
            if (merged.Present)
            {
                result[key] = merged.Node?.DeepClone();
            }
        }

        return result;
    }

    private static Slot SlotOf(JsonObject obj, string key) => obj.TryGetPropertyValue(key, out var node) ? new Slot(true, node) : Slot.Missing;

    private static Slot MergeSlot(string path, Slot b, Slot m, Slot t, List<string> conflicts)
    {
        if (m.Same(b))
        {
            return t;
        }

        if (t.Same(b) || m.Same(t))
        {
            return m;
        }

        // Both changed it, differently.
        if (path == EncryptionPath)
        {
            return t;
        }

        if (m.Node is JsonObject mine && t.Node is JsonObject theirs)
        {
            return new Slot(true, MergeObjects(path, b.Node as JsonObject ?? [], mine, theirs, conflicts));
        }

        if (path == WatchDirectoriesPath && m.Node is JsonArray mineList && t.Node is JsonArray theirList
            && TryMergeWatchDirectories(b.Node as JsonArray ?? [], mineList, theirList, conflicts, out var mergedList))
        {
            return new Slot(true, mergedList);
        }

        conflicts.Add(path);
        return m;
    }

    private static string? KeyOf(JsonNode? entry) =>
        entry is JsonObject obj && obj["path"] is JsonValue v && v.TryGetValue<string>(out var path)
            ? path.Trim().TrimEnd('\\', '/').ToUpperInvariant()
            : null;

    private static bool TryIndex(JsonArray list, out Dictionary<string, JsonNode?> index, out List<string> order)
    {
        index = [];
        order = [];
        foreach (var entry in list)
        {
            var key = KeyOf(entry);
            if (key == null || !index.TryAdd(key, entry))
            {
                return false;
            }

            order.Add(key);
        }

        return true;
    }

    /// <summary>Folders are matched by path, so a schedule set on one folder and a new folder added elsewhere both survive.</summary>
    private static bool TryMergeWatchDirectories(JsonArray b, JsonArray m, JsonArray t, List<string> conflicts, out JsonArray merged)
    {
        merged = [];
        if (!TryIndex(b, out var baseIndex, out _) || !TryIndex(m, out var mineIndex, out var mineOrder) || !TryIndex(t, out var theirIndex, out var theirOrder))
        {
            return false;
        }

        foreach (var key in theirOrder.Concat(mineOrder).Distinct(StringComparer.Ordinal))
        {
            Slot Of(Dictionary<string, JsonNode?> index) => index.TryGetValue(key, out var node) ? new Slot(true, node) : Slot.Missing;
            var entry = MergeSlot($"{WatchDirectoriesPath}[{key}]", Of(baseIndex), Of(mineIndex), Of(theirIndex), conflicts);
            if (entry.Present)
            {
                merged.Add(entry.Node?.DeepClone());
            }
        }

        return true;
    }
}
