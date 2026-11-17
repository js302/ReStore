namespace ReStore.Core.src.core;

/// <summary>
/// What to do when a restore would write over a file that already exists at the target.
/// Defaults to <see cref="Skip"/> rather than <see cref="Overwrite"/>: silently truncating
/// a user's real file is the one outcome a backup tool must not do by accident.
/// </summary>
public enum RestoreConflictPolicy
{
    Skip,
    Overwrite,

    /// <summary>Write alongside the existing file as <c>name (restored).ext</c>.</summary>
    KeepBoth,

    /// <summary>Abort the whole restore as soon as any conflict is found.</summary>
    Fail
}

public enum RestoreConflictKind
{
    None,

    /// <summary>A file exists whose size and content hash match the snapshot's.</summary>
    Identical,

    Differs
}

public sealed class RestorePreviewEntry
{
    public string RelativePath { get; init; } = string.Empty;
    public string TargetPath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DateTime LastModifiedUtc { get; init; }
    public string ContentHash { get; init; } = string.Empty;
    public RestoreConflictKind Conflict { get; init; }
    public long? ExistingSizeBytes { get; init; }
    public DateTime? ExistingLastModifiedUtc { get; init; }

    public bool Exists => Conflict != RestoreConflictKind.None;
}

/// <summary>
/// Describes a restore before any bytes are written. Built from the manifest alone, so no
/// chunk objects are fetched.
/// </summary>
public sealed class RestorePreview
{
    public string RequestedBackupPath { get; init; } = string.Empty;
    public string ResolvedManifestPath { get; init; } = string.Empty;
    public string SnapshotId { get; init; } = string.Empty;
    public DateTime SnapshotCreatedUtc { get; init; }
    public string TargetDirectory { get; init; } = string.Empty;
    public bool EncryptionEnabled { get; init; }

    /// <summary>Entries selected for restore, after any relative-path filter was applied.</summary>
    public List<RestorePreviewEntry> Entries { get; init; } = [];

    public int FilesFilteredOut { get; init; }

    public int FileCount => Entries.Count;
    public long TotalBytes => Entries.Sum(entry => entry.SizeBytes);

    public int ExistingFileCount => Entries.Count(entry => entry.Exists);
    public int IdenticalFileCount => Entries.Count(entry => entry.Conflict == RestoreConflictKind.Identical);
    public int DifferingFileCount => Entries.Count(entry => entry.Conflict == RestoreConflictKind.Differs);

    public bool HasConflicts => DifferingFileCount > 0;
}

public sealed class RestoreOutcome
{
    public int FilesRestored { get; set; }
    public int FilesSkipped { get; set; }
    public int FilesKeptBoth { get; set; }
    public int FilesOverwritten { get; set; }

    /// <summary>Empty directories recreated from a v3 manifest.</summary>
    public int DirectoriesRestored { get; set; }
    public long BytesRestored { get; set; }

    public List<string> SkippedRelativePaths { get; } = [];
}
