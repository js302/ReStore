namespace ReStore.Core.src.core;

/// <summary>
/// Inputs and counters of one change-detection pass over a group: when the scan started (the reference for the
/// "racy timestamp" rule), whether this pass is a deep scan that reads every file, and what the pass did.
/// </summary>
public sealed class ChangeScanContext
{
    /// <summary>
    /// A recorded timestamp this close to the moment it was recorded cannot be trusted: the file may have been
    /// modified again within the filesystem's timestamp granularity without the timestamp moving.
    /// </summary>
    public static readonly TimeSpan RacyWindow = TimeSpan.FromSeconds(2);

    public DateTime ScanStartUtc { get; init; } = DateTime.UtcNow;

    /// <summary>True: hash every file whose size is unchanged instead of trusting size and timestamps.</summary>
    public bool DeepScan { get; init; }

    /// <summary>
    /// How a file's content hash is computed for the deep-scan comparison; null = plain SHA-256. Encrypted v3 repositories
    /// record keyed (HMAC) hashes, so the comparison has to use the same keyed hash.
    /// </summary>
    public Func<string, string>? ContentHasher { get; init; }

    public int FilesScanned { get; set; }

    /// <summary>Files read and hashed to decide whether they changed.</summary>
    public int FilesHashed { get; set; }

    /// <summary>Files judged unchanged from their size and timestamps alone, without being read.</summary>
    public int FilesReused { get; set; }

    public int FilesChanged { get; set; }

    /// <summary>Files that had to be read to be verified but could not be; they are reported as changed.</summary>
    public int FilesUnverified { get; set; }
}
