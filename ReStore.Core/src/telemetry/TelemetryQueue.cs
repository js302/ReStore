using System.Text;
using System.Text.Json.Nodes;

namespace ReStore.Core.src.telemetry;

/// <summary>
/// The local, bounded event queue (<c>state/telemetry-queue.jsonl</c>, one JSON event per line). Several processes (GUI,
/// CLI, service) may append at once: every operation opens the file exclusively for a moment, retrying for a short time and
/// giving up silently. Only whole lines are appended, a partial last line (a crash mid-write) is ignored when reading and
/// terminated before the next append, and when the queue grows past its bounds the oldest events are dropped.
/// </summary>
public sealed class TelemetryQueue(string path, int maxEvents = TelemetryQueue.DefaultMaxEvents, long maxBytes = TelemetryQueue.DefaultMaxBytes)
{
    public const int DefaultMaxEvents = 2000;
    public const long DefaultMaxBytes = 1024 * 1024;

    // The bounds are only checked once the file is this big, so a typical append never reads the file.
    private const long CheckThresholdBytes = 64 * 1024;
    private static readonly TimeSpan LockBudget = TimeSpan.FromMilliseconds(200);
    private static readonly UTF8Encoding Utf8 = new(false);

    public string Path { get; } = path;

    /// <summary>Appends one event (a single-line JSON object). False when the file stayed locked or could not be written.</summary>
    public bool Append(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Contains('\n') || line.Contains('\r'))
        {
            return false;
        }

        return WithExclusiveFile(stream =>
        {
            var needsLeadingNewline = false;
            if (stream.Length > 0)
            {
                stream.Seek(-1, SeekOrigin.End);
                needsLeadingNewline = stream.ReadByte() != '\n';
            }

            stream.Seek(0, SeekOrigin.End);
            var bytes = Utf8.GetBytes((needsLeadingNewline ? "\n" : "") + line + "\n");
            stream.Write(bytes, 0, bytes.Length);

            if (stream.Length >= Math.Min(CheckThresholdBytes, maxBytes / 4))
            {
                EnforceBounds(stream);
            }
        });
    }

    /// <summary>The complete events in the queue, oldest first. Blank, partial or non-object lines are skipped.</summary>
    public List<string> ReadAll()
    {
        var lines = new List<string>();
        WithExclusiveFile(stream => lines = ParseLines(ReadText(stream)), create: false);
        return lines;
    }

    public int Count() => ReadAll().Count;

    /// <summary>Removes the given events (as returned by <see cref="ReadAll"/>); events appended meanwhile stay.</summary>
    public bool Remove(IEnumerable<string> sent)
    {
        // Identical lines (same second, same content) are removed once per sent copy, so unsent duplicates stay.
        var remove = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in sent)
        {
            remove[line] = remove.GetValueOrDefault(line) + 1;
        }

        if (remove.Count == 0)
        {
            return true;
        }

        return WithExclusiveFile(stream =>
        {
            var kept = new List<string>();
            foreach (var line in ParseLines(ReadText(stream)))
            {
                if (remove.TryGetValue(line, out var pending) && pending > 0)
                {
                    remove[line] = pending - 1;
                }
                else
                {
                    kept.Add(line);
                }
            }

            Rewrite(stream, kept);
        }, create: false);
    }

    public bool Clear()
    {
        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void EnforceBounds(FileStream stream)
    {
        var lines = ParseLines(ReadText(stream));
        if (lines.Count <= maxEvents && stream.Length <= maxBytes)
        {
            return;
        }

        // Drop the oldest events down to 80% of the bounds so this does not run on every append.
        var keepEvents = Math.Max(1, maxEvents * 8 / 10);
        var budget = maxBytes * 8 / 10;
        var kept = new List<string>();
        long size = 0;
        for (var i = lines.Count - 1; i >= 0 && kept.Count < keepEvents; i--)
        {
            size += Utf8.GetByteCount(lines[i]) + 1;
            if (size > budget && kept.Count > 0)
            {
                break;
            }

            kept.Add(lines[i]);
        }

        kept.Reverse();
        Rewrite(stream, kept);
    }

    private static void Rewrite(FileStream stream, List<string> lines)
    {
        var bytes = Utf8.GetBytes(lines.Count == 0 ? "" : string.Join("\n", lines) + "\n");
        stream.SetLength(0);
        stream.Seek(0, SeekOrigin.Begin);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string ReadText(FileStream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Utf8, false, 4096, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static List<string> ParseLines(string text)
    {
        var result = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '{' || line[^1] != '}')
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(line) is JsonObject)
                {
                    result.Add(line);
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // A torn write: skip it.
            }
        }

        return result;
    }

    private bool WithExclusiveFile(Action<FileStream> action, bool create = true)
    {
        var deadline = DateTime.UtcNow + LockBudget;
        while (true)
        {
            try
            {
                if (!create && !File.Exists(Path))
                {
                    return true;
                }

                var directory = System.IO.Path.GetDirectoryName(Path);
                if (create && !string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using var stream = new FileStream(Path, create ? FileMode.OpenOrCreate : FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                action(stream);
                return true;
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    return false;
                }

                Thread.Sleep(5);
            }
        }
    }
}
