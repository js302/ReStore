namespace ReStore.Core.src.core;

/// <summary>A file (or directory) a snapshot could not read, and what the snapshot did about it.</summary>
/// <param name="Path">Absolute path of the file or directory.</param>
/// <param name="Reason">Why it could not be read.</param>
/// <param name="CarriedForward">True when the previous snapshot's copy was kept in this snapshot.</param>
public sealed record SkippedFile(string Path, string Reason, bool CarriedForward);

/// <summary>Outcome of a backup run; lists everything the snapshot had to skip.</summary>
public sealed class BackupResult
{
    public static BackupResult Empty { get; } = new([]);

    public BackupResult(IReadOnlyList<SkippedFile> skipped)
    {
        Skipped = skipped;
    }

    public IReadOnlyList<SkippedFile> Skipped { get; }

    /// <summary>Files the scan selected for the snapshot (after exclusions); null when the run did not scan a folder.</summary>
    public int? FilesScanned { get; init; }

    public bool HasSkipped => Skipped.Count > 0;

    /// <summary>One-line summary for CLI/GUI/log output, or null when nothing was skipped.</summary>
    public string? SkippedSummary => HasSkipped
        ? $"completed with {Skipped.Count} skipped file(s) ({Skipped.Count(s => s.CarriedForward)} carried forward from the previous snapshot)"
        : null;
}
