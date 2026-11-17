using System.Net;
using System.Text;
using FluentAssertions;
using ReStore.Core.src.updates;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>Hostile or broken <c>latest.json</c>: never an exception out of the checker, never an installable asset that is not https + a full SHA-256.</summary>
[Collection("GlobalState")]
public class HostileUpdateMetadataTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Manifest(string version = "1.8.0", string asset = "", string extra = "") =>
        $$"""{ "schemaVersion": 1, "version": "{{version}}", "tag": "v{{version}}", {{extra}} "assets": [ {{asset}} ] }""";

    private static string Asset(string url = "https://github.com/js302/ReStore/releases/download/v1.8.0/x.zip", string? sha = Sha, string name = "x.zip", string size = "10", string kind = "cli-zip") =>
        $$"""{ "kind": "{{kind}}", "arch": "x64", "name": "{{name}}", "url": "{{url}}", {{(sha == null ? "" : $"\"sha256\": \"{sha}\",")}} "size": {{size}} }""";

    private static UpdateChecker Checker(Func<HttpRequestMessage, HttpResponseMessage> respond, string current = "1.7.0") =>
        new(new FakeHttpHandler((request, _) => Task.FromResult(respond(request))), current, AppChannel.Portable, "x64", TimeSpan.FromSeconds(5), _ => null);

    public static IEnumerable<object[]> NotManifests() =>
    [
        [""], ["   "], ["null"], ["[]"], ["42"], ["\"text\""], ["true"], ["{"], ["{\"version\":"], ["<html><body>404</body></html>"],
        ["{}"], ["{\"schemaVersion\":1}"], ["{\"schemaVersion\":1,\"version\":42}"], ["{\"schemaVersion\":1,\"version\":\"not-a-version\"}"],
        ["{\"schemaVersion\":\"1\",\"version\":\"1.8.0\"}"], ["{\"schemaVersion\":0,\"version\":\"1.8.0\"}"], ["{\"schemaVersion\":-3,\"version\":\"1.8.0\"}"],
        ["{\"schemaVersion\":1.5,\"version\":\"1.8.0\"}"], ["{\"schemaVersion\":99999999999,\"version\":\"1.8.0\"}"],
        ["{\"schemaVersion\":1,\"version\":\"\"}"], ["{\"schemaVersion\":1,\"version\":null}"], ["{\"schemaVersion\":1,\"version\":[\"1.8.0\"]}"],
        ["{\"schemaVersion\":1,\"version\":\"1.8.0\u0000\"}"], ["﻿{ not json"]
    ];

    [Theory]
    [MemberData(nameof(NotManifests))]
    public void TryParse_RejectsWhatIsNotAManifest_WithoutThrowing(string json)
    {
        var act = () => UpdateManifest.TryParse(json);

        act.Should().NotThrow();
        act().Should().BeNull();
    }

    [Fact]
    public void TryParse_SurvivesDeepNestingAndHugeDocuments()
    {
        var deep = new string('[', 100_000) + new string(']', 100_000);
        var wide = "{\"schemaVersion\":1,\"version\":\"1.8.0\",\"assets\":[" + string.Join(',', Enumerable.Repeat("{\"kind\":1}", 50_000)) + "]}";

        UpdateManifest.TryParse(deep).Should().BeNull();
        var parsed = UpdateManifest.TryParse(wide);
        parsed.Should().NotBeNull();
        parsed!.Assets.Should().BeEmpty("assets with wrong field types are skipped");
    }

    [Fact]
    public void TryParse_ToleratesWrongTypesInsideAssets_AndUnknownFields()
    {
        var json = Manifest(asset: string.Join(',',
            "42", "null", "[]", "\"x\"",
            """{ "kind": 1, "arch": "x64", "name": "n", "url": "https://x/y" }""",
            """{ "kind": "cli-zip", "arch": "x64", "name": ["a"], "url": "https://x/y" }""",
            """{ "kind": "cli-zip", "arch": "x64", "name": "n", "url": "https://x/y", "size": "ten", "sha256": 12, "extra": { "deep": [1,2,3] } }""",
            Asset(size: "-5")),
            extra: "\"critical\": \"yes\", \"publishedUtc\": 17, \"releaseNotesUrl\": 5, \"somethingNew\": { \"a\": 1 },");

        var parsed = UpdateManifest.TryParse(json);

        parsed.Should().NotBeNull();
        parsed!.Critical.Should().BeFalse();
        parsed.PublishedUtc.Should().BeNull();
        parsed.ReleaseNotesUrl.Should().BeNull();
        parsed.Assets.Should().HaveCount(2);
    }

    [Fact]
    public void TryParse_LowercasesTheSha_AndAcceptsUnknownSchemaVersions()
    {
        var parsed = UpdateManifest.TryParse(Manifest(asset: Asset(sha: Sha.ToUpperInvariant())).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 7"));

        parsed.Should().NotBeNull();
        parsed!.SchemaVersion.Should().Be(7);
        parsed.Assets.Single().Sha256.Should().Be(Sha);
    }

    // ---- through the checker --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1.7.0")]
    [InlineData("1.6.0")]
    [InlineData("0.0.1")]
    public async Task OlderOrEqualVersions_AreNeverOfferedAsUpdates(string version)
    {
        var result = await Checker(_ => FakeHttpHandler.Json(Manifest(version, Asset()))).CheckAsync(false);

        result.Status.Should().Be(UpdateStatus.UpToDate);
        result.Asset.Should().BeNull("a downgrade must not carry an installable asset");
    }

    [Theory]
    [InlineData("1.8.0-rc.1")]
    [InlineData("2.0.0-beta+build.5")]
    public async Task PrereleaseVersions_AreParsedWithoutIncident(string version)
    {
        var result = await Checker(_ => FakeHttpHandler.Json(Manifest(version, Asset()))).CheckAsync(false);

        result.Status.Should().BeOneOf(UpdateStatus.UpdateAvailable, UpdateStatus.UpToDate);
    }

    [Theory]
    [MemberData(nameof(NotManifests))]
    public async Task Checker_TurnsGarbageIntoAFailedResult(string body)
    {
        var result = await Checker(request => request.RequestUri!.AbsoluteUri == UpdateChecker.DefaultManifestUrl
            ? FakeHttpHandler.Json(body)
            : FakeHttpHandler.Status(HttpStatusCode.NotFound)).CheckAsync(false);

        result.Status.Should().Be(UpdateStatus.Failed);
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Checker_RefusesAnOversizedStreamedBody()
    {
        // A chunked response has no Content-Length, so a size header check alone never fires. The padding sits inside a valid manifest.
        var padding = new string(' ', 6 * 1024 * 1024);
        var body = Manifest("1.8.0", Asset()).Replace("\"assets\"", $"\"padding\": \"{padding}\", \"assets\"");
        var result = await Checker(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(Encoding.UTF8.GetBytes(body)) }).CheckAsync(false);

        result.Status.Should().Be(UpdateStatus.Failed);
        result.Error.Should().Contain("too large");
    }

    [Fact]
    public async Task Checker_RefusesAnOversizedBody_WithAContentLength()
    {
        var body = Manifest("1.8.0", Asset()) + new string(' ', 5 * 1024 * 1024);
        var result = await Checker(_ => FakeHttpHandler.Json(body)).CheckAsync(false);

        result.Status.Should().Be(UpdateStatus.Failed);
    }

    [Fact]
    public async Task Checker_NeverThrows_WhenTheServerMisbehaves()
    {
        foreach (var handler in new Func<HttpRequestMessage, HttpResponseMessage>[]
        {
            _ => throw new HttpRequestException("connection reset"),
            _ => throw new IOException("broken pipe"),
            _ => throw new InvalidOperationException("weird"),
            _ => FakeHttpHandler.Status(HttpStatusCode.InternalServerError),
            _ => FakeHttpHandler.Redirect("http://127.0.0.1/evil"),
            _ => FakeHttpHandler.Redirect("not a uri at all \u0000"),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xFF, 0xFE, 0x00, 0x01]) }
        })
        {
            var result = await Checker(handler).CheckAsync(true);
            result.Status.Should().Be(UpdateStatus.Failed);
        }
    }

    // ---- through the installer: what a checker result may turn into ------------------------------------------------

    private static UpdateInstaller Installer(string root) =>
        new(new FakeHttpHandler((_, _) => throw new InvalidOperationException("no download expected")), new FakeMsixLauncher(), AppChannel.Portable, "x64",
            Path.Combine(root, "updates"), Path.Combine(root, "install"), root);

    [Theory]
    [InlineData("http://github.com/js302/ReStore/releases/download/v1.8.0/x.zip", Sha, "x.zip")]
    [InlineData("ftp://github.com/x.zip", Sha, "x.zip")]
    [InlineData("file:///etc/passwd", Sha, "x.zip")]
    [InlineData("//github.com/x.zip", Sha, "x.zip")]
    [InlineData("not a url", Sha, "x.zip")]
    [InlineData("https://github.com/x.zip", null, "x.zip")]
    [InlineData("https://github.com/x.zip", "abc123", "x.zip")]
    [InlineData("https://github.com/x.zip", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg", "x.zip")]
    [InlineData("https://github.com/x.zip", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef00", "x.zip")]
    [InlineData("https://github.com/x.zip", "", "x.zip")]
    [InlineData("https://github.com/x.zip", Sha, "../x.zip")]
    [InlineData("https://github.com/x.zip", Sha, "..")]
    [InlineData("https://github.com/x.zip", Sha, ".hidden.zip")]
    [InlineData("https://github.com/x.zip", Sha, "a/b.zip")]
    public async Task Installer_RefusesAssetsThatAreNotHttpsWithAFullSha(string url, string? sha, string name)
    {
        var json = Manifest(asset: $$"""{ "kind": "cli-zip", "arch": "x64", "name": "{{name}}", "url": "{{url}}", "sha256": {{(sha == null ? "null" : $"\"{sha}\"")}}, "size": 10 }""");
        var result = await Checker(_ => FakeHttpHandler.Json(json)).CheckAsync(false);
        var root = Path.Combine(Path.GetTempPath(), "ReStoreUpdMeta_" + Guid.NewGuid().ToString("N"));
        try
        {
            var plan = () => Installer(root).PreparePlan(result);

            if (result.Asset == null)
            {
                // Dropped while parsing (for example a null sha is simply not present): nothing installable.
                plan.Should().Throw<UpdateInstallException>();
            }
            else
            {
                plan.Should().Throw<UpdateInstallException>();
            }

            Directory.Exists(root).Should().BeFalse("a refused plan touches nothing");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task Installer_RefusesAVersionThatCouldEscapeTheDownloadFolder()
    {
        foreach (var version in new[] { "1.8.0/../../x", "1.8.0\\..\\x", "1.8.0+../x", "1.8.0-a/b" })
        {
            var result = new UpdateCheckResult
            {
                Status = UpdateStatus.UpdateAvailable, Current = "1.7.0", Latest = version,
                Asset = new UpdateAsset("cli-zip", "x64", "x.zip", "https://github.com/x.zip", Sha, 10)
            };
            var root = Path.Combine(Path.GetTempPath(), "ReStoreUpdMeta_" + Guid.NewGuid().ToString("N"));

            var plan = () => Installer(root).PreparePlan(result);

            plan.Should().Throw<UpdateInstallException>($"version '{version}'");
            await Task.CompletedTask;
        }
    }

    /// <summary>An HttpContent whose length is not known up front (what a chunked response looks like).</summary>
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
