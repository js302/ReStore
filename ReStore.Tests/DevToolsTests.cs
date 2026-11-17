using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Moq;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

internal sealed class FakeDevToolsMachine(DevToolsLocations locations) : IDevToolsMachine
{
    public DevToolsLocations Locations { get; } = locations;

    /// <summary>Installed extensions per editor id; a missing editor means its CLI is not on PATH.</summary>
    public Dictionary<string, List<string>> Extensions { get; } = [];
    public List<string> Installed { get; } = [];
    public HashSet<string> FailInstall { get; } = [];

    public Task<IReadOnlyList<string>?> ListExtensionsAsync(string editorId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>?>(Extensions.TryGetValue(editorId, out var ids) ? [.. ids] : null);

    public Task<bool> InstallExtensionAsync(string editorId, string extensionId, CancellationToken cancellationToken)
    {
        Installed.Add($"{editorId}:{extensionId}");
        return Task.FromResult(!FailInstall.Contains(extensionId));
    }
}

public class DevToolsCatalogTests
{
    [Theory]
    [InlineData("ms-python.python")]
    [InlineData("GitHub.copilot-chat")]
    [InlineData("esbenp.prettier-vscode")]
    [InlineData("a1.b_2.c")]
    public void IsValidExtensionId_AcceptsMarketplaceIds(string id) => DevToolsCatalog.IsValidExtensionId(id).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("--force")]
    [InlineData("-x.y")]
    [InlineData("nodot")]
    [InlineData("pub.name; calc")]
    [InlineData("pub.name calc")]
    [InlineData("pub.name&calc")]
    [InlineData("pub/../x.y")]
    [InlineData("C:\\x.vsix")]
    [InlineData("pub.name\nother.ext")]
    public void IsValidExtensionId_RejectsOptionsPathsAndShellText(string? id) => DevToolsCatalog.IsValidExtensionId(id).Should().BeFalse();

    [Theory]
    [InlineData("python.json", true)]
    [InlineData("My Snips.code-snippets", true)]
    [InlineData("..\\evil.json", false)]
    [InlineData("../evil.json", false)]
    [InlineData("sub/evil.json", false)]
    [InlineData(".hidden.json", false)]
    [InlineData("tool.exe", false)]
    [InlineData("", false)]
    public void IsValidSnippetName_OnlyPlainSnippetFileNames(string name, bool expected)
        => DevToolsCatalog.IsValidSnippetName(name).Should().Be(expected);

    [Fact]
    public void Resolve_MapsKnownIdsUnderTheMachineRoots_AndRejectsEverythingElse()
    {
        var loc = new DevToolsLocations(@"/u", @"/u/app", @"/u/local", @"/u/docs");

        DevToolsCatalog.Resolve("git.config", loc)!.Path.Should().Be(Path.Combine("/u", ".gitconfig"));
        DevToolsCatalog.Resolve("vscode.snippets/py.json", loc)!.Path.Should().Be(Path.Combine("/u/app", "Code", "User", "snippets", "py.json"));
        DevToolsCatalog.Resolve("ps7.profile.ps1", loc)!.Path.Should().Be(Path.Combine("/u/docs", "PowerShell", "profile.ps1"));
        DevToolsCatalog.Resolve("ps7.Microsoft.PowerShellISE_profile.ps1", loc).Should().BeNull();

        foreach (var bad in new[] { "../../etc/passwd", "ssh.id_rsa", "vscode.snippets/../x.json", "vscode.snippets/", "nope.snippets/a.json", "C:\\Windows\\x", "", null })
        {
            DevToolsCatalog.Resolve(bad, loc).Should().BeNull(bad);
        }
    }
}

[Collection("GlobalState")]
public class DevToolsBackupRestoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreDevTools_" + Guid.NewGuid().ToString("N"));
    private readonly string _previousRoot = AppPaths.Root;
    private readonly TestLogger _logger = new();
    private readonly DevToolsLocations _loc;
    private readonly FakeDevToolsMachine _machine;
    private readonly PlanFakeServices _fakes = new();

    public DevToolsBackupRestoreTests()
    {
        Directory.CreateDirectory(_root);
        AppPaths.OverrideRoot(Path.Combine(_root, "restore-home"));
        _loc = new DevToolsLocations(
            Path.Combine(_root, "user"), Path.Combine(_root, "user", "AppData", "Roaming"),
            Path.Combine(_root, "user", "AppData", "Local"), Path.Combine(_root, "user", "Documents"));
        _machine = new FakeDevToolsMachine(_loc);
        _fakes.DevTools = _machine;
    }

    public void Dispose()
    {
        AppPaths.OverrideRoot(_previousRoot);
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private string Put(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string GitConfig => Path.Combine(_loc.UserProfile, ".gitconfig");
    private string VsSettings => Path.Combine(_loc.AppData, "Code", "User", "settings.json");
    private string Terminal => Path.Combine(_loc.LocalAppData, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json");

    private async Task<string> StageAsync(bool includeSsh = false)
    {
        var dir = Path.Combine(_root, "stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await DevToolsBackup.StageAsync(_machine, includeSsh, dir, _logger, CancellationToken.None);
        return dir;
    }

    private Task<SystemRestorePlan> PlanAsync(string extractDir)
        => new SystemRestorePlanner(_fakes.Build(), _logger, Path.Combine(_root, "scratch")).BuildPlanAsync("devtools", extractDir);

    private static DevToolsManifest ReadManifest(string dir)
        => JsonSerializer.Deserialize<DevToolsManifest>(File.ReadAllText(Path.Combine(dir, "devtools_manifest.json")), JsonDefaults.SystemBackup)!;

    private SystemRestoreApplier Applier() => new(_fakes.Build(), _logger);

    [Fact]
    public async Task Stage_ListsEachFileWithSourcePath_Hash_AndExtensions()
    {
        Put(GitConfig, "[user]\n\tname = A\n");
        Put(VsSettings, "{ \"editor.fontSize\": 14 }");
        Put(Path.Combine(_loc.AppData, "Code", "User", "snippets", "cs.json"), "{}");
        Put(Path.Combine(_loc.AppData, "Code", "User", "snippets", "notes.txt"), "ignored");
        Put(Path.Combine(_loc.Documents, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1"), "Set-Alias g git");
        _machine.Extensions["vscode"] = ["ms-python.python", "esbenp.prettier-vscode"];

        var dir = await StageAsync();

        var manifest = ReadManifest(dir);
        manifest.Items.Select(i => i.Id).Should().BeEquivalentTo(
            "git.config", "vscode.settings.json", "vscode.snippets/cs.json", "ps5.Microsoft.PowerShell_profile.ps1");
        var git = manifest.Items.Single(i => i.Id == "git.config");
        git.SourcePath.Should().Be(GitConfig);
        File.ReadAllText(Path.Combine(dir, git.ArchivePath)).Should().Contain("name = A");
        git.Sha256.Should().HaveLength(64);
        manifest.Extensions.Should().ContainSingle().Which.Ids.Should().Equal("ms-python.python", "esbenp.prettier-vscode");
    }

    [Fact]
    public async Task Stage_NeverIncludesPrivateKeys_AndSshConfigOnlyWhenOptedIn()
    {
        Put(Path.Combine(_loc.UserProfile, ".ssh", "id_rsa"), "-----BEGIN OPENSSH PRIVATE KEY-----");
        Put(Path.Combine(_loc.UserProfile, ".ssh", "id_ed25519"), "-----BEGIN OPENSSH PRIVATE KEY-----");
        Put(Path.Combine(_loc.UserProfile, ".ssh", "known_hosts"), "host key");
        Put(Path.Combine(_loc.UserProfile, ".ssh", "config"), "Host x\n  User me\n");

        var without = await StageAsync(includeSsh: false);
        ReadManifest(without).Items.Should().BeEmpty();

        var with = await StageAsync(includeSsh: true);
        ReadManifest(with).Items.Select(i => i.Id).Should().Equal("ssh.config");
        Directory.GetFiles(with, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText).Should().NotContain(t => t.Contains("PRIVATE KEY"));
    }

    [Fact]
    public async Task Stage_SkipsMissingCli_AndFilesLargerThanTheCap()
    {
        Put(GitConfig, "x");
        await using (var big = File.Create(Put(VsSettings, "")))
        {
            big.SetLength(DevToolsCatalog.MaxFileBytes + 1);
        }

        var dir = await StageAsync();

        var manifest = ReadManifest(dir);
        manifest.Items.Select(i => i.Id).Should().Equal("git.config");
        manifest.Extensions.Should().BeEmpty("the code CLI is absent in the fake");
    }

    [Fact]
    public async Task Plan_ClassifiesCreateReplaceWithDiffAndUnchanged()
    {
        Put(GitConfig, "[user]\n\tname = A\n");
        Put(VsSettings, "{\n  \"a\": 1,\n  \"b\": 2\n}");
        Put(Terminal, "terminal");
        var dir = await StageAsync();

        File.Delete(Terminal);                                              // -> create
        Put(VsSettings, "{\n  \"a\": 1,\n  \"c\": 3,\n  \"d\": 4\n}");     // -> replace
        // git.config untouched -> unchanged

        var plan = await PlanAsync(dir);

        var byName = plan.Actions.ToDictionary(a => a.DevToolsFile!.ItemId);
        byName["terminal.store"].Change.Should().Be(PlannedChange.Create);
        byName["terminal.store"].DevToolsFile!.TargetPath.Should().Be(Terminal);
        byName["git.config"].Change.Should().Be(PlannedChange.Unchanged);
        var replace = byName["vscode.settings.json"];
        replace.Change.Should().Be(PlannedChange.Replace);
        replace.Detail.Should().Contain("+1 / -2 line(s)");
        replace.Component.Should().Be("devtools");
        plan.ChangeCount.Should().Be(2);
        plan.Rejected.Should().BeEmpty();
    }

    [Fact]
    public async Task Plan_UsesThisMachinesPaths_NotTheManifestSourcePath()
    {
        Put(GitConfig, "a");
        var dir = await StageAsync();
        var manifestPath = Path.Combine(dir, "devtools_manifest.json");
        var manifest = ReadManifest(dir);
        manifest.Items[0].SourcePath = Path.Combine(_root, "elsewhere", "stolen.txt");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonDefaults.SystemBackup));
        File.Delete(GitConfig);

        var plan = await PlanAsync(dir);

        plan.Actions.Single().DevToolsFile!.TargetPath.Should().Be(GitConfig);
    }

    [Fact]
    public async Task Plan_RejectsUnknownIds_BadArchivePaths_AndTamperedContent()
    {
        Put(GitConfig, "a");
        Put(VsSettings, "b");
        var dir = await StageAsync();
        var manifest = ReadManifest(dir);
        manifest.Items.Add(new DevToolsManifestItem { Id = "../../Windows/win.ini", ArchivePath = "files/001.dat", Sha256 = "x" });
        manifest.Items.Add(new DevToolsManifestItem { Id = "ps5.profile.ps1", ArchivePath = "../outside.txt", Sha256 = "x" });
        manifest.Items.Add(new DevToolsManifestItem { Id = "ps7.profile.ps1", ArchivePath = "files/099.dat", Sha256 = "x" });
        var vs = manifest.Items.Single(i => i.Id == "vscode.settings.json");
        File.WriteAllText(Path.Combine(dir, vs.ArchivePath), "tampered");
        File.WriteAllText(Path.Combine(dir, "devtools_manifest.json"), JsonSerializer.Serialize(manifest, JsonDefaults.SystemBackup));
        File.Delete(GitConfig);

        var plan = await PlanAsync(dir);

        plan.Actions.Select(a => a.DevToolsFile!.ItemId).Should().Equal("git.config");
        plan.Rejected.Should().HaveCount(4);
        plan.Rejected.Should().Contain(r => r.Contains("damaged"));
        plan.Rejected.Should().Contain(r => r.Contains("not a known"));
    }

    [Fact]
    public async Task Plan_Extensions_InstallPresentAndCliMissing()
    {
        _machine.Extensions["vscode"] = ["a.one", "b.two"];
        var dir = await StageAsync();
        var manifestPath = Path.Combine(dir, "devtools_manifest.json");
        var manifest = ReadManifest(dir);
        manifest.Extensions.Add(new DevToolsManifestExtensions { Editor = "vscode-insiders", Ids = ["c.three"] });
        manifest.Extensions[0].Ids.Add("--evil");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonDefaults.SystemBackup));
        _machine.Extensions["vscode"] = ["a.one", "z.other"];

        var plan = await PlanAsync(dir);

        plan.Actions.Select(a => (a.DevToolsExtension!.ExtensionId, a.Change)).Should().Equal(
            ("a.one", PlannedChange.AlreadyInstalled),
            ("b.two", PlannedChange.Install),
            ("c.three", PlannedChange.Unavailable));
        plan.Rejected.Should().ContainSingle().Which.Should().Contain("--evil");
    }

    [Fact]
    public async Task Apply_WritesSelectedFiles_InstallsOnlySelectedExtensions_AndUndoRestoresEverything()
    {
        Put(GitConfig, "git-backup");
        Put(VsSettings, "vs-backup");
        Put(Terminal, "terminal-backup");
        _machine.Extensions["vscode"] = ["a.one", "b.two"];
        var dir = await StageAsync();

        Put(GitConfig, "git-NOW");
        Put(VsSettings, "vs-NOW");
        File.Delete(Terminal);
        _machine.Extensions["vscode"] = [];
        var plan = await PlanAsync(dir);

        var selected = plan.Select(plan.Changes
            .Where(a => a.DevToolsFile?.ItemId != "vscode.settings.json" && a.DevToolsExtension?.ExtensionId != "b.two")
            .Select(a => a.Id));
        var result = new SystemRestoreResult("devtools");
        var undoDir = await Applier().ApplyAsync(plan, selected, "backup.zip", result, null);

        File.ReadAllText(GitConfig).Should().Be("git-backup");
        File.ReadAllText(Terminal).Should().Be("terminal-backup");
        File.ReadAllText(VsSettings).Should().Be("vs-NOW", "that row was not selected");
        _machine.Installed.Should().Equal("vscode:a.one");
        result.Succeeded.Should().Be(3);
        result.Failed.Should().Be(0);
        Directory.GetFiles(Path.GetDirectoryName(GitConfig)!, ".restore-*").Should().BeEmpty();

        var undo = await new SystemRestoreUndo(_fakes.Build(), _logger).UndoAsync(undoDir!);

        undo.Failed.Should().Be(0);
        File.ReadAllText(GitConfig).Should().Be("git-NOW");
        File.Exists(Terminal).Should().BeFalse("the restore created it, undo removes it");
        File.ReadAllText(VsSettings).Should().Be("vs-NOW");
        undo.InstalledPrograms.Should().ContainSingle().Which.Should().Contain("a.one");
        SystemRestoreUndo.List().Should().ContainSingle().Which.Summary.Should().Contain("developer-tool file");
    }

    [Fact]
    public async Task Apply_ReportsFailedExtensionInstall_AndKeepsGoing()
    {
        _machine.Extensions["vscode"] = ["a.one", "b.two"];
        var dir = await StageAsync();
        _machine.Extensions["vscode"] = [];
        _machine.FailInstall.Add("a.one");
        var plan = await PlanAsync(dir);

        var result = new SystemRestoreResult("devtools");
        await Applier().ApplyAsync(plan, plan.Select(plan.DefaultSelection()), null, result, null);

        result.Succeeded.Should().Be(1);
        result.Failed.Should().Be(1);
        result.Messages.Should().ContainSingle().Which.Should().Contain("a.one");
    }

    [Fact]
    public async Task Apply_NeverReplacesAFolder_AndStillWritesTheOtherFiles()
    {
        Put(GitConfig, "g");
        Put(VsSettings, "v");
        var dir = await StageAsync();
        File.Delete(GitConfig);
        File.Delete(VsSettings);
        Directory.CreateDirectory(GitConfig); // a folder where the file belongs: planner skips it, never replaces it
        var plan = await PlanAsync(dir);

        plan.Actions.Single(a => a.DevToolsFile!.ItemId == "git.config").Change.Should().Be(PlannedChange.Skipped);
        var result = new SystemRestoreResult("devtools");
        await Applier().ApplyAsync(plan, plan.Select(plan.DefaultSelection()), null, result, null);

        File.ReadAllText(VsSettings).Should().Be("v");
        Directory.Exists(GitConfig).Should().BeTrue();
    }

    [Fact]
    public async Task Undo_RefusesTamperedManifest_IdsAndFileNames()
    {
        Put(GitConfig, "backup");
        var dir = await StageAsync();
        Put(GitConfig, "now");
        var plan = await PlanAsync(dir);
        var result = new SystemRestoreResult("devtools");
        var undoDir = (await Applier().ApplyAsync(plan, plan.Select(plan.DefaultSelection()), null, result, null))!;

        var manifestPath = Path.Combine(undoDir, SystemRestoreUndo.ManifestFileName);
        var manifest = JsonSerializer.Deserialize<UndoManifest>(File.ReadAllText(manifestPath), JsonDefaults.SystemBackup)!;
        var outside = Put(Path.Combine(_root, "outside.txt"), "outside");
        manifest.Files.Add(new UndoFileEntry { Id = outside, File = "devtools-01.bak" });
        manifest.Files.Add(new UndoFileEntry { Id = "git.config", File = "..\\..\\outside.txt" });
        manifest.Files.Add(new UndoFileEntry { Id = "ps5.profile.ps1", File = null });
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonDefaults.SystemBackup));

        var undo = await new SystemRestoreUndo(_fakes.Build(), _logger).UndoAsync(undoDir);

        undo.Failed.Should().Be(2);
        File.ReadAllText(outside).Should().Be("outside");
        File.ReadAllText(GitConfig).Should().Be("now", "the valid entry restored the previous content; tampered ones were refused");
    }

    [Fact]
    public async Task Plan_Preview_FormatsForTheCli()
    {
        Put(GitConfig, "a");
        var dir = await StageAsync();
        Put(GitConfig, "b");

        var text = SystemRestorePlanFormatter.Format(await PlanAsync(dir));

        text.Should().Contain("replace").And.Contain(".gitconfig");
    }

    [Fact]
    public async Task BackupDevToolsAsync_ArchivesToDevtoolsGroup_WithDeclaredStorage()
    {
        Put(GitConfig, "[user]\n\tname = A\n");
        Put(Path.Combine(_loc.UserProfile, ".ssh", "id_rsa"), "PRIVATE KEY");
        _machine.Extensions["vscode"] = ["ms-python.python"];

        string? uploadedRemote = null;
        var copied = Path.Combine(_root, "uploaded.zip");
        var storage = new Mock<IStorage>();
        storage.Setup(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((local, remote, _) => { uploadedRemote = remote; File.Copy(local, copied, true); })
            .Returns(Task.CompletedTask);
        var config = new Mock<IConfigManager>();
        config.SetupGet(c => c.GlobalStorageType).Returns("global");
        config.SetupGet(c => c.SystemBackup).Returns(new SystemBackupConfig
        {
            IncludePrograms = false, IncludeEnvironmentVariables = false, IncludeWindowsSettings = false,
            IncludeDevTools = true, DevToolsStorageType = "dev-storage"
        });
        config.SetupGet(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        config.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = false, KeepLastPerDirectory = 10, MaxAgeDays = 30 });
        config.Setup(c => c.CreateStorageAsync("dev-storage")).ReturnsAsync(storage.Object);
        var manager = new SystemBackupManager(_logger, config.Object, new Mock<SystemState>(_logger).Object) { RestoreServices = _fakes.Build() };

        var result = await manager.BackupSystemAsync();

        result.Components.Should().ContainSingle().Which.Should().Be(new ComponentBackupResult("devtools", true));
        uploadedRemote.Should().StartWith("system_backups/devtools/devtools_backup_");
        using var zip = ZipFile.OpenRead(copied);
        zip.Entries.Select(e => e.FullName).Should().Contain("devtools_manifest.json").And.NotContain(n => n.Contains("id_rsa"));
    }
}
