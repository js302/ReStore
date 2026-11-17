using System.Text.Json;

namespace ReStore.E2E;

/// <summary>The update check and the <c>update install</c> confirmation rule against a mirror served from 127.0.0.1 (RESTORE_UPDATE_MANIFEST_URL): the real GitHub is never contacted.</summary>
[Trait("Category", "E2E")]
public class UpdateMirrorTests
{
    private static string Manifest(string version) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        version,
        tag = "v" + version,
        publishedUtc = "2026-11-01T00:00:00Z",
        releaseNotesUrl = "https://example.invalid/notes",
        critical = false,
        assets = new[]
        {
            // https on a closed port: if anything tried to download it the test would see a failure and no request on the server.
            new { kind = "cli-zip", arch = "x64", name = $"ReStore-cli_{version}_win-x64.zip", url = "https://127.0.0.1:1/x.zip", sha256 = new string('0', 64), size = 1000 },
            new { kind = "cli-zip", arch = "arm64", name = $"ReStore-cli_{version}_win-arm64.zip", url = "https://127.0.0.1:1/a.zip", sha256 = new string('0', 64), size = 1000 },
        }
    });

    private static RunOptions Mirror(LocalHttpServer server) =>
        new() { Environment = new Dictionary<string, string?> { ["RESTORE_UPDATE_MANIFEST_URL"] = server.BaseUrl + "/latest.json" } };

    [Fact]
    public void UpdateCheck_ReadsTheMirror_AndSendsOnlyAUserAgent()
    {
        using var server = new LocalHttpServer();
        server.ServeText("/latest.json", Manifest("99.0.0"));
        using var ws = Workspace.Create();
        ws.Init();

        var json = ws.RunJson(Mirror(server), "update", "check").Ok();
        Assert.True(json.GetProperty("updateAvailable").GetBoolean());
        Assert.Equal("99.0.0", json.GetProperty("latest").GetString());
        Assert.Equal(CliContract.ProductVersion, json.GetProperty("current").GetString());

        var request = Assert.Single(server.Requests);
        Assert.Equal("/latest.json", request.Path);
        Assert.StartsWith("ReStore/", request.Headers["User-Agent"]);
        Assert.False(request.Headers.ContainsKey("Authorization") || request.Headers.ContainsKey("Cookie"));
    }

    [Fact]
    public void UpdateCheck_WhenTheMirrorIsDown_FailsCleanlyWithTheEnvelope()
    {
        using var ws = Workspace.Create();
        ws.Init();
        var result = ws.RunJson("update", "check"); // the default environment points at a closed local port
        Assert.Equal(1, result.ExitCode);
        var json = result.Json();
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("failure", json.GetProperty("error").GetProperty("code").GetString());
    }

    [NonWindowsFact]
    public void UpdateInstall_OnABuildFromSource_IsRefused_AndNothingIsDownloaded()
    {
        using var server = new LocalHttpServer();
        server.ServeText("/latest.json", Manifest("99.0.0"));
        using var ws = Workspace.Create();
        ws.Init();
        ws.RunJson(Mirror(server), "update", "install", "--yes").Failed(1, "failure");
        Assert.DoesNotContain(server.Requests, r => r.Path != "/latest.json");
    }

    /// <summary>Windows smoke test: the published portable restore.exe is the only binary whose channel can install.</summary>
    [WindowsFact]
    public void UpdateInstall_WhenNobodyCanAnswer_NeedsYes_ExitsFour_AndDownloadsNothing()
    {
        if (Environment.GetEnvironmentVariable(CliBinary.OverrideVariable) != null)
        {
            return;
        }

        using var server = new LocalHttpServer();
        server.ServeText("/latest.json", Manifest("99.0.0"));
        using var ws = Workspace.Create();
        ws.Init();

        var dry = ws.RunJson(Mirror(server), "update", "install", "--dry-run").Ok();
        Assert.True(dry.GetProperty("dryRun").GetBoolean());
        Assert.False(dry.GetProperty("installed").GetBoolean());

        var error = ws.RunJson(Mirror(server), "update", "install").Failed(4, "needs_user");
        Assert.Contains("--yes", error.GetProperty("hint").GetString());
        Assert.DoesNotContain(server.Requests, r => r.Path != "/latest.json");

        // Redirected stdin alone (no --non-interactive) makes the run non-interactive too: still no hang, still exit 4.
        var redirected = ws.Run(Mirror(server), "update", "install", "--json");
        redirected.Failed(4, "needs_user");
    }
}
