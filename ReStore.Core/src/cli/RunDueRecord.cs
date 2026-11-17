using System.Text.Json;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.cli;

/// <summary>
/// The outcome of the last <c>run-due</c>, written by <c>run-due</c> itself to <c>state/run-due-last.json</c>. The scheduled task
/// starts <c>run-due</c> through <c>conhost.exe --headless</c>, which does not pass the exit code on, so Task Scheduler's
/// "last result" is always 0; <c>schedule status</c> reads this file instead. Holds counts and an error code only: no paths, names or messages.
/// </summary>
internal sealed record RunDueRecord
{
    public const string FileName = "run-due-last.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public DateTime StartedUtc { get; init; }
    public DateTime FinishedUtc { get; init; }
    public int ExitCode { get; init; }

    /// <summary>The JSON <c>error.code</c> (busy, failure, config, ...) of a failed run; null when it succeeded.</summary>
    public string? ErrorCode { get; init; }

    public int BackupsSucceeded { get; init; }
    public int BackupsFailed { get; init; }
    public int Deferred { get; init; }
    public bool Paused { get; init; }
    public bool NothingDue { get; init; }
    public bool SystemBackupRan { get; init; }
    public bool SystemBackupFailed { get; init; }
    public int VerificationsSucceeded { get; init; }
    public int VerificationsFailed { get; init; }
    public bool RetentionRan { get; init; }
    public bool RetentionFailed { get; init; }

    public static string FilePath => Path.Combine(AppPaths.StateDirectory, FileName);

    /// <summary>Short human text for <c>schedule status</c>, e.g. "ok: 2 backup(s) succeeded".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (BackupsSucceeded > 0 || BackupsFailed > 0)
        {
            parts.Add($"{BackupsSucceeded} backup(s) succeeded, {BackupsFailed} failed");
        }

        if (SystemBackupRan)
        {
            parts.Add(SystemBackupFailed ? "system backup failed" : "system backup ran");
        }

        if (VerificationsSucceeded > 0 || VerificationsFailed > 0)
        {
            parts.Add($"{VerificationsSucceeded} verification(s) succeeded, {VerificationsFailed} failed");
        }

        if (RetentionRan)
        {
            parts.Add(RetentionFailed ? "retention failed" : "retention ran");
        }

        if (Deferred > 0)
        {
            parts.Add($"{Deferred} deferred (repository busy)");
        }

        if (Paused)
        {
            parts.Add("protection paused");
        }
        else if (NothingDue && parts.Count == 0)
        {
            parts.Add("nothing due");
        }

        var detail = parts.Count == 0 ? string.Empty : $": {string.Join("; ", parts)}";
        return ExitCode == 0 ? $"ok{detail}" : $"failed (exit code {ExitCode}{(ErrorCode != null ? $", {ErrorCode}" : string.Empty)}){detail}";
    }

    public void TrySaveWith(DateTime startedUtc, DateTime finishedUtc, int exitCode, string? errorCode) =>
        (this with { StartedUtc = startedUtc, FinishedUtc = finishedUtc, ExitCode = exitCode, ErrorCode = errorCode }).TrySave();

    /// <summary>Best effort: a record that cannot be written must never change the outcome of the run.</summary>
    public void TrySave()
    {
        try
        {
            var file = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            ReStore.Core.src.utils.SharedFile.WriteAllTextAtomic(file, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the file 'schedule status' just reports no result.
        }
    }

    /// <summary>The last record, or null when there is none or it cannot be read.</summary>
    public static RunDueRecord? TryLoad()
    {
        try
        {
            var file = FilePath;
            return File.Exists(file) ? JsonSerializer.Deserialize<RunDueRecord>(File.ReadAllText(file), Options) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
