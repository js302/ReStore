using ReStore.Core.src.backup;
using System.Text.Json;

namespace ReStore.Core.src.utils;

public sealed class UndoEnvironmentEntry
{
    public string Name { get; set; } = "";
    public EnvironmentVariableTarget Target { get; set; }

    /// <summary>False when the variable did not exist before the restore (undo removes it).</summary>
    public bool Existed { get; set; }
    public string Value { get; set; } = "";
    public string Kind { get; set; } = EnvironmentVariableKinds.String;
}

public sealed class UndoSettingsEntry
{
    public string RegistryKey { get; set; } = "";

    /// <summary>Plain file name of the exported .reg inside the undo directory; null when the key did not exist.</summary>
    public string? File { get; set; }
    public bool RequiresElevation { get; set; }
}

public sealed class UndoFileEntry
{
    /// <summary>Allow-listed devtools item id; undo maps it back to this machine's path, never to a stored path.</summary>
    public string Id { get; set; } = "";

    /// <summary>Plain file name (<c>devtools-NN.bak</c>) of the saved previous content; null when the file did not exist.</summary>
    public string? File { get; set; }
}

/// <summary>What <c>undo_manifest.json</c> records about one undo point.</summary>
public sealed class UndoManifest
{
    public int Version { get; set; } = 1;
    public DateTime CreatedUtc { get; set; }
    public string Component { get; set; } = "";
    public string? SourceBackup { get; set; }
    public List<UndoEnvironmentEntry> Environment { get; set; } = [];
    public List<UndoSettingsEntry> Settings { get; set; } = [];

    /// <summary>Developer-tool files that were about to change (absent in manifests written before devtools existed).</summary>
    public List<UndoFileEntry> Files { get; set; } = [];

    /// <summary>File (inside the undo directory) listing the programs installed before the restore, when programs were selected.</summary>
    public string? InstalledProgramsFile { get; set; }

    /// <summary>Programs the restore was about to install. They are never uninstalled by undo; they are only listed.</summary>
    public List<string> PlannedInstalls { get; set; } = [];
}

public sealed record UndoPointInfo(string Path, DateTime CreatedUtc, string Component, string Summary);

public sealed class SystemUndoResult
{
    public int Restored { get; set; }
    public int Failed { get; set; }
    public List<string> Messages { get; } = [];

    /// <summary>Programs installed by the original restore; the user may want to uninstall them manually.</summary>
    public List<string> InstalledPrograms { get; } = [];
}

/// <summary>
/// Undo points: before a restore applies anything, the CURRENT state of exactly the items about to change is saved to
/// <c>&lt;AppPaths.Root&gt;/restore-undo/&lt;timestamp&gt;/</c>. Undoing applies the saved values through the same safe
/// paths (exact environment values, allow-listed validated .reg imports).
/// </summary>
public sealed class SystemRestoreUndo(SystemRestoreServices services, ILogger logger)
{
    public const string ManifestFileName = "undo_manifest.json";

    public async Task<string> CreateAsync(
        string component,
        string? sourceBackup,
        IReadOnlyList<PlannedAction> selected,
        CancellationToken cancellationToken = default)
    {
        var dir = CreateUniqueDirectory();
        var completed = false;
        try
        {
            var manifest = new UndoManifest { CreatedUtc = DateTime.UtcNow, Component = component, SourceBackup = sourceBackup };

            foreach (var action in selected.Where(a => a.Variable != null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var variable = action.Variable!;
                var existing = services.Environment.Get(variable.Name, variable.Target);
                manifest.Environment.Add(new UndoEnvironmentEntry
                {
                    Name = variable.Name,
                    Target = variable.Target,
                    Existed = existing != null,
                    Value = existing?.Value ?? "",
                    Kind = EnvironmentVariableKinds.Normalize(existing?.Kind)
                });
            }

            var index = 0;
            foreach (var action in selected.Where(a => a.RegistryItem != null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = action.RegistryItem!;
                var fileName = $"settings-{++index:D2}.reg";
                var exported = await services.RegistryExporter.ExportKeyAsync(item.RegistryKey, Path.Combine(dir, fileName), cancellationToken).ConfigureAwait(false);
                manifest.Settings.Add(new UndoSettingsEntry
                {
                    RegistryKey = item.RegistryKey,
                    File = exported ? fileName : null,
                    RequiresElevation = item.RequiresElevation
                });
            }

            var fileIndex = 0;
            foreach (var action in selected.Where(a => a.DevToolsFile != null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var change = action.DevToolsFile!;
                string? savedName = null;
                if (File.Exists(change.TargetPath))
                {
                    // Reading failures propagate: a restore never proceeds on an incomplete undo point.
                    savedName = $"devtools-{++fileIndex:D2}.bak";
                    await File.WriteAllBytesAsync(Path.Combine(dir, savedName), DevToolsFiles.ReadAllBytes(change.TargetPath), cancellationToken).ConfigureAwait(false);
                }

                manifest.Files.Add(new UndoFileEntry { Id = change.ItemId, File = savedName });
            }

            foreach (var action in selected.Where(a => a.DevToolsExtension != null))
            {
                var extension = action.DevToolsExtension!;
                manifest.PlannedInstalls.Add($"VS Code extension {extension.ExtensionId} ({extension.EditorId})");
            }

            var programs = selected.Where(a => a.Program != null).ToList();
            if (programs.Count > 0)
            {
                var installed = await services.Programs.GetInstalledProgramsAsync(cancellationToken).ConfigureAwait(false);
                manifest.InstalledProgramsFile = "installed_programs_before.json";
                await File.WriteAllTextAsync(
                    Path.Combine(dir, manifest.InstalledProgramsFile),
                    JsonSerializer.Serialize(new { programs = installed }, JsonDefaults.SystemBackup),
                    cancellationToken).ConfigureAwait(false);
                manifest.PlannedInstalls.AddRange(programs.Select(a =>
                    string.IsNullOrEmpty(a.Program!.WingetId) ? a.Name : $"{a.Name} ({a.Program.WingetId})"));
            }

            await File.WriteAllTextAsync(
                Path.Combine(dir, ManifestFileName), JsonSerializer.Serialize(manifest, JsonDefaults.SystemBackup), cancellationToken).ConfigureAwait(false);
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                // A half-written undo point must not be mistaken for a usable one.
                try { Directory.Delete(dir, true); } catch (Exception) { /* best effort */ }
            }
        }

        logger.Log($"Undo point saved to {dir}", LogLevel.Info);
        return dir;
    }

    /// <summary>Undo points, newest first. Folders without a readable manifest are ignored.</summary>
    public static List<UndoPointInfo> List()
    {
        var root = AppPaths.RestoreUndoDirectory;
        var points = new List<UndoPointInfo>();
        if (!Directory.Exists(root))
        {
            return points;
        }

        foreach (var dir in Directory.GetDirectories(root))
        {
            var manifest = TryReadManifest(dir, out _);
            if (manifest != null)
            {
                points.Add(new UndoPointInfo(dir, manifest.CreatedUtc, manifest.Component, Describe(manifest)));
            }
        }

        return [.. points.OrderByDescending(p => p.CreatedUtc)];
    }

    /// <summary>
    /// Resolves an undo point folder and refuses anything that is not a direct, non-link child of
    /// <see cref="AppPaths.RestoreUndoDirectory"/> (so <c>..</c>, other roots and junctions/symlinks are rejected).
    /// </summary>
    public static string ResolveUndoDirectory(string undoDirectory)
    {
        if (string.IsNullOrWhiteSpace(undoDirectory))
        {
            throw new ArgumentException("An undo point folder is required.", nameof(undoDirectory));
        }

        var root = Path.GetFullPath(AppPaths.RestoreUndoDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(undoDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var parent = Path.GetDirectoryName(full);
        if (parent == null || !string.Equals(parent, root, comparison))
        {
            throw new InvalidDataException($"'{undoDirectory}' is not an undo point folder under '{root}'.");
        }

        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"Undo point '{full}' does not exist.");
        }

        if (new DirectoryInfo(full).LinkTarget != null || new DirectoryInfo(root).LinkTarget != null)
        {
            throw new InvalidDataException($"'{full}' is a link; undo points must be real folders.");
        }

        return full;
    }

    /// <summary>Deletes one undo point (same containment rules as <see cref="UndoAsync"/>).</summary>
    public static void Delete(string undoDirectory)
    {
        var full = ResolveUndoDirectory(undoDirectory);
        if (TryReadManifest(full, out var error) == null)
        {
            throw new InvalidDataException($"'{full}' is not a ReStore undo point: {error}");
        }

        Directory.Delete(full, recursive: true);
    }

    public async Task<SystemUndoResult> UndoAsync(string undoDirectory, CancellationToken cancellationToken = default)
    {
        undoDirectory = ResolveUndoDirectory(undoDirectory);
        var manifest = TryReadManifest(undoDirectory, out var error)
            ?? throw new InvalidDataException($"'{undoDirectory}' is not a ReStore undo point: {error}");
        var result = new SystemUndoResult();

        RestoreEnvironment(manifest, result, cancellationToken);
        await RestoreSettingsAsync(manifest, undoDirectory, result, cancellationToken).ConfigureAwait(false);
        RestoreDevToolsFiles(manifest, undoDirectory, result, cancellationToken);

        result.InstalledPrograms.AddRange(manifest.PlannedInstalls);
        if (manifest.PlannedInstalls.Count > 0)
        {
            result.Messages.Add("Programs installed by that restore are not uninstalled. Remove them manually if you no longer want them: "
                + string.Join(", ", manifest.PlannedInstalls));
        }

        logger.Log($"Undo of {undoDirectory}: {result.Restored} restored, {result.Failed} failed", result.Failed > 0 ? LogLevel.Warning : LogLevel.Info);
        return result;
    }

    private void RestoreEnvironment(UndoManifest manifest, SystemUndoResult result, CancellationToken cancellationToken)
    {
        var store = services.Environment;
        foreach (var entry in manifest.Environment)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var label = $"{entry.Target}:{entry.Name}";
            if (entry.Target is not (EnvironmentVariableTarget.User or EnvironmentVariableTarget.Machine)
                || entry.Kind is not (EnvironmentVariableKinds.String or EnvironmentVariableKinds.ExpandString))
            {
                result.Failed++;
                result.Messages.Add($"{label}: invalid scope or kind in the undo manifest");
                continue;
            }

            if (!EnvironmentVariablesManager.IsRestorableName(entry.Name)
                || (!entry.Name.Equals("PATHEXT", StringComparison.OrdinalIgnoreCase)
                    && EnvironmentVariablesManager.ShouldSkipVariable(entry.Name, entry.Target)))
            {
                result.Failed++;
                result.Messages.Add($"{label}: not a restorable variable name");
                continue;
            }

            try
            {
                // Exact previous value: no PATH merge, otherwise entries the restore added would stay.
                if (entry.Existed)
                {
                    store.Set(entry.Name, entry.Value, entry.Target, entry.Kind);
                }
                else
                {
                    store.Remove(entry.Name, entry.Target);
                }

                result.Restored++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Failed++;
                result.Messages.Add(ex is UnauthorizedAccessException or System.Security.SecurityException
                    ? $"{label}: {ex.Message} (administrator rights are required for {entry.Target} variables)"
                    : $"{label}: {ex.Message}");
            }
        }

        if (result.Restored > 0)
        {
            store.NotifyChanged();
        }
    }

    private void RestoreDevToolsFiles(UndoManifest manifest, string dir, SystemUndoResult result, CancellationToken cancellationToken)
    {
        if (manifest.Files.Count == 0)
        {
            return;
        }

        var locations = (services.DevTools ?? throw new InvalidOperationException("No developer-tools access is configured.")).Locations;
        var root = Path.GetFullPath(dir);
        foreach (var entry in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = DevToolsCatalog.Resolve(entry.Id, locations);
            if (target == null)
            {
                result.Failed++;
                result.Messages.Add($"{entry.Id}: refused (not a known developer-tool file)");
                continue;
            }

            try
            {
                if (entry.File == null)
                {
                    // The restore created this file; undo removes it again.
                    if (File.Exists(target.Path))
                    {
                        File.Delete(target.Path);
                    }

                    result.Restored++;
                    continue;
                }

                if (!System.Text.RegularExpressions.Regex.IsMatch(entry.File, @"\Adevtools-[0-9]{2,}\.bak\z"))
                {
                    result.Failed++;
                    result.Messages.Add($"{target.Label}: refused (saved file name not allowed)");
                    continue;
                }

                var saved = Path.GetFullPath(Path.Combine(root, entry.File));
                if (!File.Exists(saved))
                {
                    result.Failed++;
                    result.Messages.Add($"{target.Label}: saved file '{entry.File}' is missing");
                    continue;
                }

                DevToolsFiles.Write(target.Path, File.ReadAllBytes(saved));
                result.Restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Failed++;
                result.Messages.Add($"{target.Label}: {ex.Message}");
            }
        }
    }

    private async Task RestoreSettingsAsync(UndoManifest manifest, string dir, SystemUndoResult result, CancellationToken cancellationToken)
    {
        var plan = new RegistryImportPlan();
        var root = Path.GetFullPath(dir);
        foreach (var entry in manifest.Settings)
        {
            if (entry.File == null)
            {
                result.Messages.Add($"{entry.RegistryKey}: did not exist before the restore; the key is left in place (undo never deletes registry keys).");
                continue;
            }

            if (!RegistryImportPolicy.IsKeyAllowed(entry.RegistryKey)
                || !string.Equals(Path.GetFileName(entry.File), entry.File, StringComparison.Ordinal)
                || !entry.File.EndsWith(".reg", StringComparison.OrdinalIgnoreCase))
            {
                result.Failed++;
                result.Messages.Add($"{entry.RegistryKey}: refused (key or file name not allowed)");
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(root, entry.File));
            if (!File.Exists(path))
            {
                result.Failed++;
                result.Messages.Add($"{entry.RegistryKey}: saved file '{entry.File}' is missing");
                continue;
            }

            var validation = RegistryImportPolicy.Validate(WindowsSettingsRestorePlanner.ReadRegText(path), entry.RegistryKey);
            if (!validation.IsValid)
            {
                result.Failed++;
                result.Messages.Add($"{entry.RegistryKey}: saved file rejected ({validation.Errors[0]})");
                continue;
            }

            plan.Items.Add(new RegistryImportItem(entry.RegistryKey, path, RegistryImportPolicy.IsMachineKey(entry.RegistryKey)));
        }

        if (plan.Items.Count == 0)
        {
            return;
        }

        var import = await services.RegistryImporter.ImportAsync(plan, cancellationToken).ConfigureAwait(false);
        result.Restored += import.Imported;
        result.Failed += import.Failures.Count;
        result.Messages.AddRange(import.Failures);
    }

    private static string Describe(UndoManifest manifest)
    {
        var parts = new List<string>();
        if (manifest.Environment.Count > 0) parts.Add($"{manifest.Environment.Count} environment variable(s)");
        if (manifest.Settings.Count > 0) parts.Add($"{manifest.Settings.Count} settings key(s)");
        if (manifest.Files.Count > 0) parts.Add($"{manifest.Files.Count} developer-tool file(s)");
        if (manifest.PlannedInstalls.Count > 0) parts.Add($"{manifest.PlannedInstalls.Count} program/extension install(s) (not undoable)");
        return parts.Count == 0 ? "nothing recorded" : string.Join(", ", parts);
    }

    private static UndoManifest? TryReadManifest(string dir, out string? error)
    {
        error = null;
        var path = Path.Combine(dir, ManifestFileName);
        try
        {
            if (!File.Exists(path))
            {
                error = $"{ManifestFileName} not found";
                return null;
            }

            var manifest = JsonSerializer.Deserialize<UndoManifest>(File.ReadAllText(path), JsonDefaults.SystemBackup);
            if (manifest == null)
            {
                error = "empty manifest";
            }

            return manifest;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return null;
        }
    }

    private static string CreateUniqueDirectory()
    {
        var baseDir = AppPaths.RestoreUndoDirectory;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var target = Path.Combine(baseDir, stamp);
        for (var i = 2; Directory.Exists(target); i++)
        {
            target = Path.Combine(baseDir, $"{stamp}-{i}");
        }

        Directory.CreateDirectory(target);
        return target;
    }
}

/// <summary>Applies the selected rows of a <see cref="SystemRestorePlan"/> after saving an undo point.</summary>
public sealed class SystemRestoreApplier(SystemRestoreServices services, ILogger logger)
{
    /// <summary>
    /// Saves the undo point (a failure aborts before anything is changed), then applies environment variables,
    /// settings and programs, in that order. Returns the undo point folder, or null when nothing was selected.
    /// </summary>
    public async Task<string?> ApplyAsync(
        SystemRestorePlan plan,
        IReadOnlyList<PlannedAction> selected,
        string? sourceBackup,
        SystemRestoreResult result,
        IProgress<SystemRestoreProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (selected.Count == 0)
        {
            return null;
        }

        progress?.Report(new SystemRestoreProgress("Saving undo point", "Recording the current state of the items about to change..."));
        var undoDir = await new SystemRestoreUndo(services, logger).CreateAsync(plan.Component, sourceBackup, selected, cancellationToken).ConfigureAwait(false);
        result.UndoPointFolder = undoDir;

        var variables = selected.Where(a => a.Variable != null).Select(a => a.Variable!).ToList();
        if (variables.Count > 0)
        {
            progress?.Report(new SystemRestoreProgress("Restoring Environment Variables", $"Applying {variables.Count} variable(s)..."));
            var applied = new EnvironmentVariablesManager(logger).ApplyVariables(variables, services.Environment, cancellationToken);
            result.Succeeded += applied.Restored;
            result.Failed += applied.Failed;
            result.Skipped += applied.Skipped;
            result.Messages.AddRange(applied.Failures);
        }

        var items = selected.Where(a => a.RegistryItem != null).Select(a => a.RegistryItem!).ToList();
        if (items.Count > 0)
        {
            progress?.Report(new SystemRestoreProgress("Restoring Windows Settings", "Importing registry settings..."));
            var importPlan = new RegistryImportPlan();
            importPlan.Items.AddRange(items);
            var import = await services.RegistryImporter.ImportAsync(importPlan, cancellationToken).ConfigureAwait(false);
            result.Succeeded += import.Imported;
            result.Failed += import.Failures.Count;
            result.Messages.AddRange(import.Failures);
        }

        await ApplyDevToolsAsync(selected, result, progress, cancellationToken).ConfigureAwait(false);

        var programs = selected.Where(a => a.Program != null).Select(a => a.Program!).ToList();
        if (programs.Count > 0)
        {
            progress?.Report(new SystemRestoreProgress("Restoring Programs", "Installing programs via Winget..."));
            var install = await services.Installer.InstallAsync(programs, cancellationToken).ConfigureAwait(false);
            result.Succeeded += install.Succeeded;
            result.Failed += install.Failed;
            if (install.Succeeded + install.Failed == 0)
            {
                result.Messages.Add(install.Error ?? "Nothing was installed (winget may be unavailable).");
            }
        }

        return undoDir;
    }

    private async Task ApplyDevToolsAsync(
        IReadOnlyList<PlannedAction> selected,
        SystemRestoreResult result,
        IProgress<SystemRestoreProgress>? progress,
        CancellationToken cancellationToken)
    {
        var files = selected.Where(a => a.DevToolsFile != null).ToList();
        var extensions = selected.Where(a => a.DevToolsExtension != null).ToList();
        if (files.Count + extensions.Count == 0)
        {
            return;
        }

        var machine = services.DevTools ?? throw new InvalidOperationException("No developer-tools access is configured.");
        progress?.Report(new SystemRestoreProgress("Restoring Developer Tools", $"Writing {files.Count} file(s)..."));
        foreach (var action in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var change = action.DevToolsFile!;
            try
            {
                // Targets were resolved from the allow-list when planning; the content must still match its checksum.
                var content = await File.ReadAllBytesAsync(change.ArchiveFile, cancellationToken).ConfigureAwait(false);
                if (!DevToolsFiles.Sha256Hex(content).Equals(change.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("the backed-up file changed after the preview");
                }

                DevToolsFiles.Write(change.TargetPath, content);
                result.Succeeded++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                logger.Log($"Could not restore {change.TargetPath}: {ex.Message}", LogLevel.Warning);
                result.Failed++;
                result.Messages.Add($"{action.Name}: {ex.Message}");
            }
        }

        foreach (var action in extensions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = action.DevToolsExtension!;
            progress?.Report(new SystemRestoreProgress("Restoring Developer Tools", $"Installing extension {extension.ExtensionId}..."));
            if (!DevToolsCatalog.IsValidExtensionId(extension.ExtensionId))
            {
                result.Failed++;
                result.Messages.Add($"{extension.ExtensionId}: not a valid extension id");
            }
            else if (await machine.InstallExtensionAsync(extension.EditorId, extension.ExtensionId, cancellationToken).ConfigureAwait(false))
            {
                result.Succeeded++;
            }
            else
            {
                result.Failed++;
                result.Messages.Add($"Extension {extension.ExtensionId} could not be installed.");
            }
        }
    }
}
