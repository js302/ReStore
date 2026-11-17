using ReStore.Core.src.utils;
namespace ReStore.Core.src.backup;

public sealed record ComponentBackupResult(string Component, bool Succeeded, string? Error = null);

/// <summary>Per-component outcome of <see cref="SystemBackupManager.BackupSystemAsync"/>.</summary>
public sealed class SystemBackupResult
{
    public List<ComponentBackupResult> Components { get; } = [];
    public bool HasFailures => Components.Any(c => !c.Succeeded);
    public IEnumerable<ComponentBackupResult> Failures => Components.Where(c => !c.Succeeded);
}

/// <summary>Thrown after every component was attempted when at least one failed; carries the per-component results.</summary>
public sealed class SystemBackupException(SystemBackupResult result) : Exception(BuildMessage(result))
{
    public SystemBackupResult Result { get; } = result;

    private static string BuildMessage(SystemBackupResult result)
        => "System backup finished with failures: " +
           string.Join("; ", result.Failures.Select(f => $"{f.Component}: {f.Error}"));
}

/// <summary>A question the restore pipeline asks the user (GUI) before changing the system.</summary>
public sealed record RestorePrompt(string Title, string Message, bool IsWarning);

/// <summary>Progress update from the restore pipeline. Counts are null when unchanged/unknown.</summary>
public sealed record SystemRestoreProgress(string Status, string Detail, int? Total = null, int? Succeeded = null, int? Failed = null);

/// <summary>Callbacks for <see cref="SystemBackupManager.RestoreSystemAsync(string, string, string?, SystemRestoreOptions?, CancellationToken)"/>.</summary>
public sealed class SystemRestoreOptions
{
    /// <summary>
    /// Asks before changing the system. When null (CLI), environment variables are restored (previous behaviour)
    /// while program installs and Windows-settings imports are not run and only reported.
    /// </summary>
    public Func<RestorePrompt, Task<bool>>? Confirm { get; init; }

    public IProgress<SystemRestoreProgress>? Progress { get; init; }

    /// <summary>Build the plan and return it in <see cref="SystemRestoreResult.Plan"/> without changing anything (CLI <c>--plan</c>).</summary>
    public bool PlanOnly { get; init; }

    /// <summary>
    /// Preview hook (GUI): receives the plan and returns the ids of the rows to apply, or null to cancel. When set it replaces
    /// the per-component <see cref="Confirm"/> prompts, because the preview is the confirmation.
    /// </summary>
    public Func<SystemRestorePlan, Task<IReadOnlyCollection<string>?>>? SelectActions { get; init; }
}

public sealed class SystemRestoreResult(string component)
{
    public string Component { get; } = component;

    /// <summary>Durable copy of the archive contents (including scripts for manual use).</summary>
    public string? ScriptsFolder { get; set; }

    /// <summary>What the backup would change on this machine (also set when <see cref="SystemRestoreOptions.PlanOnly"/>).</summary>
    public SystemRestorePlan? Plan { get; set; }

    /// <summary>Folder with the saved previous state, when a restore applied changes.</summary>
    public string? UndoPointFolder { get; set; }

    /// <summary>Storage path of the backup that was restored (recorded in the undo point).</summary>
    public string? SourceBackup { get; set; }

    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public List<string> Messages { get; } = [];
}
