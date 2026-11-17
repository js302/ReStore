using System.Text.Json;

namespace ReStore.Core.src.utils;

/// <summary>Stages the developer-tool files and the VS Code extension lists into a backup folder.</summary>
internal static class DevToolsBackup
{
    public static async Task<string> StageAsync(
        IDevToolsMachine machine, bool includeSshConfig, string stagingDir, ILogger logger, CancellationToken cancellationToken)
    {
        var manifest = new DevToolsManifest { CreatedUtc = DateTime.UtcNow };
        var filesDir = Path.Combine(stagingDir, "files");
        Directory.CreateDirectory(filesDir);

        var index = 0;
        foreach (var target in DevToolsCatalog.ListBackupTargets(machine.Locations, includeSshConfig))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(target.Path);
                if (info.Length > DevToolsCatalog.MaxFileBytes)
                {
                    logger.Log($"Skipping {target.Path}: larger than {DevToolsCatalog.MaxFileBytes / (1024 * 1024)} MB", LogLevel.Warning);
                    continue;
                }

                var content = DevToolsFiles.ReadAllBytes(target.Path);
                var archivePath = $"files/{++index:D3}.dat";
                await File.WriteAllBytesAsync(Path.Combine(stagingDir, archivePath), content, cancellationToken).ConfigureAwait(false);
                manifest.Items.Add(new DevToolsManifestItem
                {
                    Id = target.Id, Label = target.Label, SourcePath = target.Path, ArchivePath = archivePath,
                    Size = content.LongLength, Sha256 = DevToolsFiles.Sha256Hex(content)
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One locked or unreadable file must not lose the rest of the developer setup.
                logger.Log($"Skipping {target.Path}: {ex.Message}", LogLevel.Warning);
            }
        }

        var extensionCount = 0;
        foreach (var editor in DevToolsCatalog.Editors)
        {
            var ids = await machine.ListExtensionsAsync(editor.Id, cancellationToken).ConfigureAwait(false);
            if (ids is { Count: > 0 })
            {
                manifest.Extensions.Add(new DevToolsManifestExtensions { Editor = editor.Id, Ids = [.. ids] });
                extensionCount += ids.Count;
            }
        }

        await File.WriteAllTextAsync(
            Path.Combine(stagingDir, DevToolsRestorePlanner.ManifestFileName),
            JsonSerializer.Serialize(manifest, JsonDefaults.SystemBackup), cancellationToken).ConfigureAwait(false);

        return $"{manifest.Items.Count} file(s), {extensionCount} extension(s)";
    }
}
