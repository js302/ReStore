using System.ComponentModel;
using System.Text.RegularExpressions;

namespace ReStore.Core.src.utils;

/// <summary>Per-user folders the developer-tool files live in. Tests pass a fake root; production uses the environment.</summary>
public sealed record DevToolsLocations(string UserProfile, string AppData, string LocalAppData, string Documents)
{
    public static DevToolsLocations FromEnvironment() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
}

/// <summary>One file the devtools component knows how to back up and restore. <see cref="Id"/> is stable across machines.</summary>
public sealed record DevToolsTarget(string Id, string Label, string Category, string Path);

/// <summary>A VS Code flavour: its <c>%APPDATA%</c> folder and CLI shim (a .cmd file, so it is named with the extension).</summary>
public sealed record DevToolsEditor(string Id, string Label, string AppDataFolder, string Command);

/// <summary>
/// The allow-list of developer-tool files. Restore never trusts a path from an archive: it maps the item id back to a
/// location here. Private keys are never listed: from <c>.ssh</c> only the <c>config</c> file is known.
/// </summary>
public static partial class DevToolsCatalog
{
    public const string SshConfigId = "ssh.config";
    public const string GitConfigId = "git.config";
    public const long MaxFileBytes = 10 * 1024 * 1024;

    private static readonly string[] SnippetExtensions = [".json", ".code-snippets"];

    public static readonly IReadOnlyList<DevToolsEditor> Editors =
    [
        new("vscode", "VS Code", "Code", "code.cmd"),
        new("vscode-insiders", "VS Code Insiders", "Code - Insiders", "code-insiders.cmd")
    ];

    private static readonly string[] PowerShellProfiles =
        ["profile.ps1", "Microsoft.PowerShell_profile.ps1", "Microsoft.VSCode_profile.ps1", "Microsoft.PowerShellISE_profile.ps1"];

    public static DevToolsEditor? FindEditor(string editorId)
        => Editors.FirstOrDefault(e => e.Id.Equals(editorId, StringComparison.Ordinal));

    /// <summary>Every fixed (non-snippet) target, existing or not.</summary>
    public static IReadOnlyList<DevToolsTarget> FixedTargets(DevToolsLocations loc)
    {
        var targets = new List<DevToolsTarget>
        {
            new("terminal.store", "Windows Terminal settings", "terminal",
                Path.Combine(loc.LocalAppData, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json")),
            new("terminal.preview", "Windows Terminal Preview settings", "terminal",
                Path.Combine(loc.LocalAppData, "Packages", "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe", "LocalState", "settings.json")),
            new("terminal.unpackaged", "Windows Terminal settings (unpackaged)", "terminal",
                Path.Combine(loc.LocalAppData, "Microsoft", "Windows Terminal", "settings.json"))
        };

        foreach (var (prefix, label, folder) in new[] { ("ps5", "Windows PowerShell", "WindowsPowerShell"), ("ps7", "PowerShell 7", "PowerShell") })
        {
            foreach (var file in PowerShellProfiles)
            {
                // The ISE profile only exists for Windows PowerShell.
                if (prefix == "ps7" && file.Contains("ISE", StringComparison.Ordinal))
                {
                    continue;
                }

                targets.Add(new DevToolsTarget($"{prefix}.{file}", $"{label} profile ({file})", "powershell",
                    Path.Combine(loc.Documents, folder, file)));
            }
        }

        foreach (var editor in Editors)
        {
            var user = Path.Combine(loc.AppData, editor.AppDataFolder, "User");
            targets.Add(new DevToolsTarget($"{editor.Id}.settings.json", $"{editor.Label} settings", "vscode", Path.Combine(user, "settings.json")));
            targets.Add(new DevToolsTarget($"{editor.Id}.keybindings.json", $"{editor.Label} keybindings", "vscode", Path.Combine(user, "keybindings.json")));
        }

        targets.Add(new DevToolsTarget(GitConfigId, "Git global config", "git", Path.Combine(loc.UserProfile, ".gitconfig")));
        targets.Add(new DevToolsTarget(SshConfigId, "SSH client config (no keys)", "ssh", Path.Combine(loc.UserProfile, ".ssh", "config")));
        return targets;
    }

    /// <summary>Files that exist now and should go into a backup, including the VS Code snippet files.</summary>
    public static List<DevToolsTarget> ListBackupTargets(DevToolsLocations loc, bool includeSshConfig)
    {
        var result = FixedTargets(loc)
            .Where(t => includeSshConfig || t.Id != SshConfigId)
            .Where(t => File.Exists(t.Path))
            .ToList();

        foreach (var editor in Editors)
        {
            var snippets = SnippetsDirectory(loc, editor);
            if (!Directory.Exists(snippets))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(snippets).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                if (IsValidSnippetName(name))
                {
                    result.Add(new DevToolsTarget($"{editor.Id}.snippets/{name}", $"{editor.Label} snippets ({name})", "vscode", file));
                }
            }
        }

        return result;
    }

    /// <summary>Maps an archived item id to this machine's location; null for ids that are not on the allow-list.</summary>
    public static DevToolsTarget? Resolve(string? id, DevToolsLocations loc)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var fixedTarget = FixedTargets(loc).FirstOrDefault(t => t.Id.Equals(id, StringComparison.Ordinal));
        if (fixedTarget != null)
        {
            return fixedTarget;
        }

        var slash = id.IndexOf(".snippets/", StringComparison.Ordinal);
        if (slash <= 0)
        {
            return null;
        }

        var editor = FindEditor(id[..slash]);
        var name = id[(slash + ".snippets/".Length)..];
        return editor != null && IsValidSnippetName(name)
            ? new DevToolsTarget(id, $"{editor.Label} snippets ({name})", "vscode", Path.Combine(SnippetsDirectory(loc, editor), name))
            : null;
    }

    public static bool IsValidSnippetName(string? name)
        => !string.IsNullOrEmpty(name)
            && name.Length <= 128
            && name == Path.GetFileName(name)
            && !name.StartsWith('.')
            && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !name.Contains('/') && !name.Contains('\\')
            && SnippetExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static string SnippetsDirectory(DevToolsLocations loc, DevToolsEditor editor)
        => Path.Combine(loc.AppData, editor.AppDataFolder, "User", "snippets");

    /// <summary>
    /// A Marketplace extension id (<c>publisher.name</c>). It is passed as one argument after <c>--install-extension</c>,
    /// and the pattern keeps anything that could be read as an option (leading dash) or a path out.
    /// </summary>
    public static bool IsValidExtensionId(string? id)
        => !string.IsNullOrEmpty(id) && id.Length <= 150 && ExtensionIdPattern().IsMatch(id);

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9\-_]*\.[A-Za-z0-9][A-Za-z0-9\-_.]*\z")]
    private static partial Regex ExtensionIdPattern();
}

/// <summary>Machine access of the devtools component (paths and the VS Code CLI); tests substitute a fake.</summary>
public interface IDevToolsMachine
{
    DevToolsLocations Locations { get; }

    /// <summary>Installed extension ids of one editor, or null when its CLI is not available.</summary>
    Task<IReadOnlyList<string>?> ListExtensionsAsync(string editorId, CancellationToken cancellationToken);

    /// <summary>Runs <c>--install-extension</c> for an already validated id. Returns false when the install failed.</summary>
    Task<bool> InstallExtensionAsync(string editorId, string extensionId, CancellationToken cancellationToken);
}

internal sealed class DefaultDevToolsMachine(ILogger logger) : IDevToolsMachine
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);

    public DevToolsLocations Locations { get; } = DevToolsLocations.FromEnvironment();

    public async Task<IReadOnlyList<string>?> ListExtensionsAsync(string editorId, CancellationToken cancellationToken)
    {
        var editor = DevToolsCatalog.FindEditor(editorId) ?? throw new ArgumentException($"Unknown editor '{editorId}'.", nameof(editorId));
        try
        {
            var run = await ProcessRunner.RunAsync(editor.Command, ["--list-extensions"], ListTimeout, cancellationToken).ConfigureAwait(false);
            if (run.TimedOut || run.ExitCode != 0)
            {
                logger.Log($"{editor.Command} --list-extensions did not succeed (timed out: {run.TimedOut}, exit code {run.ExitCode}); extensions skipped.", LogLevel.Warning);
                return null;
            }

            return [.. run.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(DevToolsCatalog.IsValidExtensionId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            logger.Log($"{editor.Command} is not available ({ex.Message}); extensions skipped.", LogLevel.Info);
            return null;
        }
    }

    public async Task<bool> InstallExtensionAsync(string editorId, string extensionId, CancellationToken cancellationToken)
    {
        var editor = DevToolsCatalog.FindEditor(editorId) ?? throw new ArgumentException($"Unknown editor '{editorId}'.", nameof(editorId));
        if (!DevToolsCatalog.IsValidExtensionId(extensionId))
        {
            throw new ArgumentException($"'{extensionId}' is not a valid extension id.", nameof(extensionId));
        }

        try
        {
            var run = await ProcessRunner.RunAsync(editor.Command, ["--install-extension", extensionId], InstallTimeout, cancellationToken).ConfigureAwait(false);
            if (run.TimedOut || run.ExitCode != 0)
            {
                logger.Log($"Installing {extensionId} failed (timed out: {run.TimedOut}, exit code {run.ExitCode}): {run.StandardError.Trim()}", LogLevel.Warning);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            logger.Log($"{editor.Command} is not available ({ex.Message}).", LogLevel.Warning);
            return false;
        }
    }
}

/// <summary>What <c>devtools_manifest.json</c> lists. The source path is informational; restore resolves targets by id.</summary>
public sealed class DevToolsManifest
{
    public int Version { get; set; } = 1;
    public DateTime CreatedUtc { get; set; }
    public List<DevToolsManifestItem> Items { get; set; } = [];
    public List<DevToolsManifestExtensions> Extensions { get; set; } = [];
}

public sealed class DevToolsManifestItem
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string SourcePath { get; set; } = "";

    /// <summary>Path inside the archive (<c>files/NNN.dat</c>).</summary>
    public string ArchivePath { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class DevToolsManifestExtensions
{
    public string Editor { get; set; } = "";
    public List<string> Ids { get; set; } = [];
}
