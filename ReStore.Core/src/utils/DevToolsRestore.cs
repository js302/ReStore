using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReStore.Core.src.utils;

/// <summary>A file row of the devtools restore plan: where the archived content goes on this machine.</summary>
public sealed record DevToolsFileChange(string ItemId, string TargetPath, string ArchiveFile, string Sha256);

/// <summary>An extension row of the devtools restore plan.</summary>
public sealed record DevToolsExtensionChange(string EditorId, string ExtensionId);

/// <summary>Shared file access for devtools backup, restore and undo.</summary>
internal static class DevToolsFiles
{
    /// <summary>Reads a file that an editor may still hold open.</summary>
    public static byte[] ReadAllBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// Replaces the file through a temp file in the same folder so a crash cannot leave a half-written config. A symlinked
    /// target (dotfiles repositories) is written in place so the link is kept.
    /// </summary>
    public static void Write(string path, byte[] content)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException($"'{path}' has no folder.");
        Directory.CreateDirectory(directory);
        if (Directory.Exists(path))
        {
            throw new IOException($"'{path}' is a folder.");
        }

        if (File.Exists(path) && new FileInfo(path).LinkTarget != null)
        {
            File.WriteAllBytes(path, content);
            return;
        }

        var temp = Path.Combine(directory, $".restore-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception) { /* already moved or locked */ }
        }
    }

    public static string FormatSize(long bytes) => bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024.0:0.#} KB";
}

/// <summary>Plans the restore of a devtools backup: per file create / replace (with a size and line diff) / unchanged, and extension installs.</summary>
internal static partial class DevToolsRestorePlanner
{
    public const string ManifestFileName = "devtools_manifest.json";

    [GeneratedRegex(@"\Afiles/[0-9]{3,6}\.dat\z")]
    private static partial Regex ArchivePathPattern();

    public static async Task PlanAsync(SystemRestorePlan plan, string extractDir, IDevToolsMachine machine, ILogger logger, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(extractDir, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return;
        }

        var manifest = JsonSerializer.Deserialize<DevToolsManifest>(
            await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false), JsonDefaults.SystemBackup);
        if (manifest == null)
        {
            return;
        }

        var root = Path.GetFullPath(extractDir);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in manifest.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = DevToolsCatalog.Resolve(item.Id, machine.Locations);
            if (target == null)
            {
                plan.Rejected.Add($"{item.Id}: not a known developer-tool file");
                continue;
            }

            if (!seen.Add(item.Id))
            {
                plan.Rejected.Add($"{item.Id}: listed more than once");
                continue;
            }

            if (!ArchivePathPattern().IsMatch(item.ArchivePath ?? ""))
            {
                plan.Rejected.Add($"{item.Id}: invalid archive path");
                continue;
            }

            var archiveFile = Path.GetFullPath(Path.Combine(root, item.ArchivePath!));
            if (!archiveFile.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(archiveFile))
            {
                plan.Rejected.Add($"{item.Id}: the file is missing from the backup");
                continue;
            }

            byte[] backupBytes;
            try
            {
                backupBytes = await File.ReadAllBytesAsync(archiveFile, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                plan.Rejected.Add($"{item.Id}: cannot be read from the backup ({ex.Message})");
                continue;
            }

            var hash = DevToolsFiles.Sha256Hex(backupBytes);
            if (backupBytes.LongLength > DevToolsCatalog.MaxFileBytes
                || !hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                plan.Rejected.Add($"{item.Id}: the backed-up file is damaged (checksum or size mismatch)");
                continue;
            }

            plan.Actions.Add(PlanFile(item, target, archiveFile, hash, backupBytes, logger));
        }

        await PlanExtensionsAsync(plan, manifest, machine, cancellationToken).ConfigureAwait(false);
    }

    private static PlannedAction PlanFile(DevToolsManifestItem item, DevToolsTarget target, string archiveFile, string hash, byte[] backupBytes, ILogger logger)
    {
        var change = new DevToolsFileChange(item.Id, target.Path, archiveFile, hash);

        PlannedAction Make(PlannedChange kind, string? oldValue, string detail) => new()
        {
            Id = $"devtools:{item.Id}", Component = "devtools", Change = kind, Name = target.Label, Scope = target.Category,
            OldValue = oldValue, NewValue = DevToolsFiles.FormatSize(backupBytes.LongLength),
            Detail = detail + RunsCodeNote(target), DevToolsFile = change
        };

        try
        {
            if (Directory.Exists(target.Path))
            {
                return Make(PlannedChange.Skipped, null, $"{target.Path} is a folder; not touched");
            }

            if (!File.Exists(target.Path))
            {
                return Make(PlannedChange.Create, null, $"create {target.Path}");
            }

            var current = DevToolsFiles.ReadAllBytes(target.Path);
            if (current.AsSpan().SequenceEqual(backupBytes))
            {
                return Make(PlannedChange.Unchanged, DevToolsFiles.FormatSize(current.LongLength), "already identical");
            }

            var (added, removed) = LineDiff(current, backupBytes);
            return Make(PlannedChange.Replace, DevToolsFiles.FormatSize(current.LongLength),
                $"replace {target.Path}: +{added} / -{removed} line(s), {DevToolsFiles.FormatSize(current.LongLength)} -> {DevToolsFiles.FormatSize(backupBytes.LongLength)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable (not absent): offer the replace; applying refuses when the undo point cannot be saved.
            logger.Log($"Could not read current {target.Path}: {ex.Message}", LogLevel.Warning);
            return Make(PlannedChange.Replace, null, $"replace {target.Path}; current file unreadable ({ex.Message})");
        }
    }

    private static string RunsCodeNote(DevToolsTarget target) => target.Category switch
    {
        "powershell" => " [a PowerShell profile runs at every PowerShell start; review it]",
        "ssh" => " [SSH config can run commands (ProxyCommand); review it]",
        _ => ""
    };

    /// <summary>Lines present only in the backup (added) and only in the current file (removed), as a multiset difference.</summary>
    internal static (int Added, int Removed) LineDiff(byte[] current, byte[] backup)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in Lines(current))
        {
            counts[line] = counts.GetValueOrDefault(line) + 1;
        }

        var added = 0;
        foreach (var line in Lines(backup))
        {
            if (counts.TryGetValue(line, out var n) && n > 0)
            {
                counts[line] = n - 1;
            }
            else
            {
                added++;
            }
        }

        return (added, counts.Values.Sum());
    }

    private static IEnumerable<string> Lines(byte[] content)
        => System.Text.Encoding.UTF8.GetString(content).Replace("\r\n", "\n").Split('\n');

    private static async Task PlanExtensionsAsync(SystemRestorePlan plan, DevToolsManifest manifest, IDevToolsMachine machine, CancellationToken cancellationToken)
    {
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in manifest.Extensions)
        {
            var editor = DevToolsCatalog.FindEditor(set.Editor);
            if (editor == null)
            {
                plan.Rejected.Add($"extensions of '{set.Editor}': unknown editor");
                continue;
            }

            var ids = new List<string>();
            foreach (var id in set.Ids)
            {
                if (DevToolsCatalog.IsValidExtensionId(id))
                {
                    ids.Add(id);
                }
                else
                {
                    plan.Rejected.Add($"{editor.Label} extension '{id}': not a valid extension id");
                }
            }

            if (ids.Count == 0)
            {
                continue;
            }

            var installed = await machine.ListExtensionsAsync(editor.Id, cancellationToken).ConfigureAwait(false);
            foreach (var id in ids)
            {
                var rowId = $"devtools-ext:{editor.Id}:{id.ToLowerInvariant()}";
                if (!usedIds.Add(rowId))
                {
                    continue;
                }

                var (change, detail) = installed == null
                    ? (PlannedChange.Unavailable, $"{editor.Command} not found; install manually")
                    : installed.Contains(id, StringComparer.OrdinalIgnoreCase)
                        ? (PlannedChange.AlreadyInstalled, "already installed")
                        : (PlannedChange.Install, $"{editor.Command} --install-extension {id}");

                plan.Actions.Add(new PlannedAction
                {
                    Id = rowId, Component = "devtools", Change = change, Name = id, Scope = $"{editor.Label} extension",
                    Detail = detail, DevToolsExtension = new DevToolsExtensionChange(editor.Id, id)
                });
            }
        }
    }
}
