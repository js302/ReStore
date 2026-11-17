namespace ReStore.Core.src.core;

/// <summary>
/// Compact record of one backup run, kept (bounded) in <see cref="SystemState"/> so the Dashboard can show
/// what the last runs did. Plain settable properties: it is persisted as JSON and never mutated after creation.
/// </summary>
public sealed class BackupRunReport
{
    /// <summary>Distinct skip reasons kept per report; the rest are folded into <see cref="OtherReasonsLabel"/>.</summary>
    public const int MaxSkipReasons = 5;
    public const string OtherReasonsLabel = "other";
    private const int MaxReasonLength = 80;

    public DateTime TimestampUtc { get; set; }
    public string Group { get; set; } = "";
    public string StorageType { get; set; } = "";
    public int FilesAdded { get; set; }
    public int FilesChanged { get; set; }
    public int FilesRemoved { get; set; }
    public int FilesSkipped { get; set; }

    /// <summary>Skip reason to number of files, so a report stays small however many files were skipped.</summary>
    public Dictionary<string, int> SkipReasons { get; set; } = [];

    public long DurationMs { get; set; }

    /// <summary>Bytes actually sent to storage (chunks the destination did not already hold).</summary>
    public long BytesUploaded { get; set; }

    /// <summary>Stored chunk objects that were truncated or undecodable (e.g. an interrupted upload) and were uploaded again.</summary>
    public int ChunksReplaced { get; set; }

    public static BackupRunReport Create(
        string group,
        string storageType,
        IEnumerable<string> backedUpFiles,
        IReadOnlyCollection<string> previouslyTrackedFiles,
        int filesRemoved,
        IReadOnlyList<SkippedFile> skipped,
        TimeSpan duration,
        long bytesUploaded,
        DateTime timestampUtc,
        int chunksReplaced = 0)
    {
        var tracked = previouslyTrackedFiles as ISet<string> ?? new HashSet<string>(previouslyTrackedFiles, StringComparer.OrdinalIgnoreCase);
        int added = 0, changed = 0;
        foreach (var file in backedUpFiles)
        {
            if (tracked.Contains(file))
            {
                changed++;
            }
            else
            {
                added++;
            }
        }

        return new BackupRunReport
        {
            TimestampUtc = timestampUtc,
            Group = group,
            StorageType = storageType,
            FilesAdded = added,
            FilesChanged = changed,
            FilesRemoved = filesRemoved,
            FilesSkipped = skipped.Count,
            SkipReasons = SummariseReasons(skipped),
            DurationMs = (long)duration.TotalMilliseconds,
            BytesUploaded = Math.Max(0, bytesUploaded),
            ChunksReplaced = Math.Max(0, chunksReplaced)
        };
    }

    /// <summary>One line for the Dashboard / notifications, e.g. <c>3 added, 1 changed, 2 skipped (Access denied x2)</c>.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { $"{FilesAdded} added", $"{FilesChanged} changed", $"{FilesRemoved} removed" };
            if (FilesSkipped > 0)
            {
                var reasons = string.Join(", ", SkipReasons.OrderByDescending(r => r.Value).Select(r => $"{r.Key} x{r.Value}"));
                parts.Add($"{FilesSkipped} skipped" + (reasons.Length > 0 ? $" ({reasons})" : string.Empty));
            }

            if (ChunksReplaced > 0)
            {
                parts.Add($"{ChunksReplaced} damaged chunk(s) re-uploaded");
            }

            return string.Join(", ", parts);
        }
    }

    private static Dictionary<string, int> SummariseReasons(IReadOnlyList<SkippedFile> skipped)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var reason in skipped.Select(s => Normalise(s.Reason)))
        {
            counts[reason] = counts.GetValueOrDefault(reason) + 1;
        }

        if (counts.Count <= MaxSkipReasons)
        {
            return counts;
        }

        var kept = counts.OrderByDescending(r => r.Value).Take(MaxSkipReasons - 1).ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);
        kept[OtherReasonsLabel] = counts.Where(r => !kept.ContainsKey(r.Key)).Sum(r => r.Value);
        return kept;
    }

    private static string Normalise(string? reason)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();
        return text.Length <= MaxReasonLength ? text : text[..MaxReasonLength];
    }
}
