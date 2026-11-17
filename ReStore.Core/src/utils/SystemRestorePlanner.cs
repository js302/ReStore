using System.Text.Json;

namespace ReStore.Core.src.utils;

public enum PlannedChange
{
    /// <summary>Environment variable that does not exist yet.</summary>
    Add,
    /// <summary>Environment variable whose value (after the PATH-style merge) or kind differs.</summary>
    Change,
    Unchanged,
    /// <summary>Program that is not installed and can be installed via winget.</summary>
    Install,
    AlreadyInstalled,
    /// <summary>Program without a usable winget id: manual installation only.</summary>
    Unavailable,
    /// <summary>Registry export that would change the current settings.</summary>
    Import,
    /// <summary>System-managed or unusable entry that restore never touches.</summary>
    Skipped,
    /// <summary>Developer-tool file that does not exist on this machine yet.</summary>
    Create,
    /// <summary>Developer-tool file whose current content differs from the backup.</summary>
    Replace
}

/// <summary>One row of the restore preview. Only <see cref="IsChange"/> rows can be applied.</summary>
public sealed class PlannedAction
{
    public required string Id { get; init; }
    public required string Component { get; init; }
    public required PlannedChange Change { get; init; }
    public required string Name { get; init; }

    /// <summary>User/Machine (environment), HKCU/HKLM (settings), winget/manual (programs).</summary>
    public string Scope { get; init; } = "";
    public string? OldValue { get; init; }
    public string? NewValue { get; init; }
    public string Detail { get; init; } = "";
    public bool NeedsAdmin { get; init; }

    public EnvironmentVariableEntry? Variable { get; init; }
    public InstalledProgram? Program { get; init; }
    public RegistryImportItem? RegistryItem { get; init; }
    public DevToolsFileChange? DevToolsFile { get; init; }
    public DevToolsExtensionChange? DevToolsExtension { get; init; }

    /// <summary>Registry keys inside a settings export.</summary>
    public IReadOnlyList<string> Keys { get; init; } = [];

    public bool IsChange => Change is PlannedChange.Add or PlannedChange.Change or PlannedChange.Install or PlannedChange.Import
        or PlannedChange.Create or PlannedChange.Replace;
}

public sealed class SystemRestorePlan(string component)
{
    public string Component { get; } = component;
    public List<PlannedAction> Actions { get; } = [];

    /// <summary>Entries that were refused up front (not on the allow-list, missing file, ...).</summary>
    public List<string> Rejected { get; } = [];

    public IEnumerable<PlannedAction> Changes => Actions.Where(a => a.IsChange);
    public int ChangeCount => Actions.Count(a => a.IsChange);
    public int UnchangedCount => Actions.Count(a => !a.IsChange);
    public int NeedsAdminCount => Actions.Count(a => a.IsChange && a.NeedsAdmin);

    /// <summary>
    /// The applicable actions whose ids were selected, in apply order: environment variables, then settings, then
    /// programs (so a program installer can rely on variables such as <c>JAVA_HOME</c> already being set).
    /// Unknown ids and non-change rows are ignored.
    /// </summary>
    public List<PlannedAction> Select(IEnumerable<string> selectedIds)
    {
        var ids = new HashSet<string>(selectedIds, StringComparer.Ordinal);
        return [.. Actions
            .Where(a => a.IsChange && ids.Contains(a.Id))
            .OrderBy(a => ApplyOrder(a.Component))];
    }

    /// <summary>Ids of every applicable action (the preview's default selection).</summary>
    public List<string> DefaultSelection() => [.. Changes.Select(a => a.Id)];

    private static int ApplyOrder(string component) => component switch { "environment" => 0, "settings" => 1, _ => 2 };
}

/// <summary>
/// Diffs the contents of a system backup against the current machine without changing anything. All machine access goes
/// through <see cref="SystemRestoreServices"/> so tests can use fakes.
/// </summary>
public sealed class SystemRestorePlanner(SystemRestoreServices services, ILogger logger, string scratchDirectory)
{
    public async Task<SystemRestorePlan> BuildPlanAsync(string component, string extractDir, CancellationToken cancellationToken = default)
    {
        var plan = new SystemRestorePlan(component);
        switch (component)
        {
            case "environment":
                await PlanEnvironmentAsync(plan, extractDir).ConfigureAwait(false);
                break;
            case "programs":
                await PlanProgramsAsync(plan, extractDir, cancellationToken).ConfigureAwait(false);
                break;
            case "settings":
                await PlanSettingsAsync(plan, extractDir, cancellationToken).ConfigureAwait(false);
                break;
            case "devtools":
                await DevToolsRestorePlanner.PlanAsync(plan, extractDir,
                    services.DevTools ?? throw new InvalidOperationException("No developer-tools access is configured."), logger, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentException($"Unsupported system restore component: {component}", nameof(component));
        }

        return plan;
    }

    private async Task PlanEnvironmentAsync(SystemRestorePlan plan, string extractDir)
    {
        var jsonPath = Path.Combine(extractDir, "environment_variables.json");
        if (!File.Exists(jsonPath))
        {
            return;
        }

        var variables = await EnvironmentVariablesManager.LoadVariablesFromJsonAsync(jsonPath).ConfigureAwait(false);
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variable in variables)
        {
            var id = UniqueId(usedIds, $"env:{variable.Target}:{variable.Name.ToUpperInvariant()}");
            var needsAdmin = variable.Target == EnvironmentVariableTarget.Machine;

            PlannedAction Make(PlannedChange change, string? oldValue, string? newValue, string detail) => new()
            {
                Id = id, Component = "environment", Change = change, Name = variable.Name, Scope = variable.TargetName,
                OldValue = oldValue, NewValue = newValue, Detail = detail, NeedsAdmin = needsAdmin, Variable = variable
            };

            if (!EnvironmentVariablesManager.IsRestorableName(variable.Name))
            {
                plan.Actions.Add(Make(PlannedChange.Skipped, null, variable.Value, "unusable variable name"));
                continue;
            }

            if (!variable.Name.Equals("PATHEXT", StringComparison.OrdinalIgnoreCase)
                && EnvironmentVariablesManager.ShouldSkipVariable(variable.Name, variable.Target))
            {
                plan.Actions.Add(Make(PlannedChange.Skipped, null, variable.Value, "system-managed variable, never restored"));
                continue;
            }

            StoredEnvironmentValue? existing;
            try
            {
                existing = services.Environment.Get(variable.Name, variable.Target);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Log($"Could not read {variable.TargetName} variable {variable.Name}: {ex.Message}", LogLevel.Debug);
                existing = null;
            }

            var kind = EnvironmentVariableKinds.Normalize(variable.Kind);
            var value = variable.Value;
            var merged = false;
            if (EnvironmentVariableMerger.IsListVariable(variable.Name, variable.Value))
            {
                value = EnvironmentVariableMerger.Merge(existing?.Value, variable.Value);
                merged = existing != null;
                if (existing?.Kind == EnvironmentVariableKinds.ExpandString)
                {
                    kind = EnvironmentVariableKinds.ExpandString;
                }
            }

            if (existing == null)
            {
                plan.Actions.Add(Make(PlannedChange.Add, null, value, "new variable"));
            }
            else if (string.Equals(existing.Value, value, StringComparison.Ordinal)
                     && EnvironmentVariableKinds.Normalize(existing.Kind) == kind)
            {
                plan.Actions.Add(Make(PlannedChange.Unchanged, existing.Value, existing.Value, "already has this value"));
            }
            else
            {
                plan.Actions.Add(Make(PlannedChange.Change, existing.Value, value, merged ? "merged with the current value" : "replaces the current value"));
            }
        }
    }

    private async Task PlanProgramsAsync(SystemRestorePlan plan, string extractDir, CancellationToken cancellationToken)
    {
        var jsonPath = Path.Combine(extractDir, "installed_programs.json");
        if (!File.Exists(jsonPath))
        {
            return;
        }

        var data = JsonSerializer.Deserialize<ProgramsFile>(
            await File.ReadAllTextAsync(jsonPath, cancellationToken).ConfigureAwait(false), JsonDefaults.SystemBackup);
        var programs = data?.Programs ?? [];
        if (programs.Count == 0)
        {
            return;
        }

        if (programs.All(p => string.IsNullOrWhiteSpace(p.Name) && string.IsNullOrWhiteSpace(p.WingetId)))
        {
            throw new InvalidDataException(
                $"'{jsonPath}' contains {programs.Count} program entries but none has a name or winget id; the file is malformed.");
        }

        // Only query the machine when there is something to compare.
        var installed = await services.Programs.GetInstalledProgramsAsync(cancellationToken).ConfigureAwait(false);
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var program in programs)
        {
            var installable = program.IsWingetAvailable && WingetHelper.IsValidId(program.WingetId);
            var label = string.IsNullOrWhiteSpace(program.Name) ? program.WingetId : program.Name;
            var id = UniqueId(usedIds, $"program:{(installable ? program.WingetId : label)}".ToLowerInvariant());
            var isInstalled = installed.Any(cp =>
                (!string.IsNullOrWhiteSpace(program.Name) && cp.Name.Equals(program.Name, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(program.WingetId) && cp.WingetId.Equals(program.WingetId, StringComparison.OrdinalIgnoreCase)));

            var (change, detail) = isInstalled
                ? (PlannedChange.AlreadyInstalled, "already installed")
                : installable
                    ? (PlannedChange.Install, $"winget install {program.WingetId}")
                    : (PlannedChange.Unavailable, "no usable winget id; install manually");

            plan.Actions.Add(new PlannedAction
            {
                Id = id, Component = "programs", Change = change, Name = label,
                Scope = installable ? "winget" : "manual", NewValue = program.Version, Detail = detail, Program = program
            });
        }
    }

    private async Task PlanSettingsAsync(SystemRestorePlan plan, string extractDir, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(extractDir, "settings_manifest.json");
        if (!File.Exists(manifestPath))
        {
            return;
        }

        var manifest = JsonSerializer.Deserialize<WindowsSettingsExport>(
            await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false), JsonDefaults.SystemBackup);
        if (manifest == null || manifest.ExportedFiles.Count == 0)
        {
            return;
        }

        var import = WindowsSettingsRestorePlanner.BuildPlan(manifest, extractDir);
        plan.Rejected.AddRange(import.Rejected);

        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in import.Items)
        {
            var backupText = WindowsSettingsRestorePlanner.ReadRegText(item.FilePath);
            var keys = RegistryImportPolicy.Validate(backupText, item.RegistryKey).Keys;
            string? current;
            try
            {
                current = await ReadCurrentRegTextAsync(item.RegistryKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unreadable (not absent): offer the import; applying it will refuse when the undo point cannot be saved.
                logger.Log($"Could not read current state of {item.RegistryKey}: {ex.Message}", LogLevel.Warning);
                plan.Actions.Add(new PlannedAction
                {
                    Id = UniqueId(usedIds, $"settings:{item.RegistryKey}"),
                    Component = "settings", Change = PlannedChange.Import, Name = item.RegistryKey,
                    Scope = item.RequiresElevation ? "HKLM" : "HKCU", Detail = $"{keys.Count} key(s); current state unreadable ({ex.Message})",
                    NeedsAdmin = item.RequiresElevation, RegistryItem = item, Keys = keys
                });
                continue;
            }

            PlannedChange change;
            string detail;
            if (current == null)
            {
                change = PlannedChange.Import;
                detail = $"{keys.Count} key(s); the key does not exist yet";
            }
            else if (RegTextContains(current, backupText))
            {
                change = PlannedChange.Unchanged;
                detail = "current settings already match";
            }
            else
            {
                change = PlannedChange.Import;
                detail = $"{keys.Count} key(s) will be updated";
            }

            plan.Actions.Add(new PlannedAction
            {
                Id = UniqueId(usedIds, $"settings:{item.RegistryKey}"),
                Component = "settings", Change = change, Name = item.RegistryKey,
                Scope = item.RequiresElevation ? "HKLM" : "HKCU", Detail = detail,
                NeedsAdmin = item.RequiresElevation, RegistryItem = item, Keys = keys
            });
        }
    }

    private async Task<string?> ReadCurrentRegTextAsync(string registryKey, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(scratchDirectory);
        var file = Path.Combine(scratchDirectory, $"current-{Guid.NewGuid():N}.reg");
        try
        {
            return await services.RegistryExporter.ExportKeyAsync(registryKey, file, cancellationToken).ConfigureAwait(false)
                && File.Exists(file)
                ? WindowsSettingsRestorePlanner.ReadRegText(file)
                : null;
        }
        finally
        {
            try { File.Delete(file); } catch (Exception) { /* scratch directory is removed by the pipeline */ }
        }
    }

    /// <summary>True when every line of the backup export (values and keys) already appears in the current export.</summary>
    internal static bool RegTextContains(string currentText, string backupText)
    {
        var current = new HashSet<string>(NormalizedLines(currentText), StringComparer.OrdinalIgnoreCase);
        return NormalizedLines(backupText).All(current.Contains);
    }

    private static IEnumerable<string> NormalizedLines(string text)
        => text.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim().TrimStart('\uFEFF'))
            .Where(l => l.Length > 0 && l[0] != ';'
                && !l.Equals("Windows Registry Editor Version 5.00", StringComparison.OrdinalIgnoreCase)
                && !l.Equals("REGEDIT4", StringComparison.OrdinalIgnoreCase));

    private static string UniqueId(HashSet<string> used, string id)
    {
        var candidate = id;
        for (var i = 2; !used.Add(candidate); i++)
        {
            candidate = $"{id}#{i}";
        }

        return candidate;
    }

    private sealed class ProgramsFile
    {
        public List<InstalledProgram>? Programs { get; set; }
    }
}
