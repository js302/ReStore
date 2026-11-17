using System.Net;
using System.Text;
using FluentAssertions;
using ReStore.Core.src.updates;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

internal sealed class FakeHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];
    public List<string?> UserAgents { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request.RequestUri!.ToString());
            UserAgents.Add(request.Headers.UserAgent.ToString());
        }

        return await respond(request, cancellationToken);
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status);
}

public class UpdateCheckTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    internal static string ManifestJson(string version, bool critical = false, string tag = "") => $$"""
        {
          "schemaVersion": 1, "version": "{{version}}", "tag": "{{(tag.Length > 0 ? tag : "v" + version)}}",
          "publishedUtc": "2026-11-20T10:00:00Z", "releaseNotesUrl": "https://github.com/js302/ReStore/releases/tag/v{{version}}",
          "critical": {{(critical ? "true" : "false")}},
          "assets": [
            { "kind": "msix", "arch": "x64", "name": "ReStore_{{version}}_x64.msix", "url": "https://github.com/js302/ReStore/releases/download/v{{version}}/ReStore_{{version}}_x64.msix", "sha256": "{{Sha}}", "size": 123 },
            { "kind": "msix", "arch": "arm64", "name": "ReStore_{{version}}_arm64.msix", "url": "https://x/arm.msix", "sha256": "{{Sha}}", "size": 124 },
            { "kind": "cli-zip", "arch": "x64", "name": "ReStore-cli_{{version}}_win-x64.zip", "url": "https://x/cli64.zip", "sha256": "{{Sha}}", "size": 456 }
          ]
        }
        """;

    private static UpdateChecker Checker(FakeHttpHandler handler, string current = "1.7.0", string channel = AppChannel.Msix, string arch = "x64",
        TimeSpan? timeout = null, Func<string, string?>? env = null) =>
        new(handler, current, channel, arch, timeout, env ?? (_ => null));

    [Fact]
    public async Task LatestJson_HappyPath_ReportsUpdateAndChannelAsset()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Json(ManifestJson("1.8.0"))));
        var result = await Checker(handler).CheckAsync(false);

        result.Status.Should().Be(UpdateStatus.UpdateAvailable);
        result.Latest.Should().Be("1.8.0");
        result.Current.Should().Be("1.7.0");
        result.Asset!.Name.Should().Be("ReStore_1.8.0_x64.msix");
        result.ReleaseNotesUrl.Should().EndWith("v1.8.0");
        result.VersionOnly.Should().BeFalse();
        handler.Requests.Should().ContainSingle().Which.Should().Be(UpdateChecker.DefaultManifestUrl);
        handler.UserAgents[0].Should().StartWith("ReStore/");
    }

    [Theory]
    [InlineData("1.7.0")]
    [InlineData("1.6.9")]
    public async Task SameOrOlderVersion_IsUpToDate(string latest)
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Json(ManifestJson(latest))));
        var result = await Checker(handler).CheckAsync(false);
        result.Status.Should().Be(UpdateStatus.UpToDate);
        result.Asset.Should().BeNull();
    }

    [Fact]
    public async Task CriticalFlag_IsReported()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Json(ManifestJson("1.8.0", critical: true))));
        (await Checker(handler).CheckAsync(false)).Critical.Should().BeTrue();
    }

    [Theory]
    [InlineData(AppChannel.Msix, "x64", "ReStore_1.8.0_x64.msix")]
    [InlineData(AppChannel.Msix, "arm64", "ReStore_1.8.0_arm64.msix")]
    [InlineData(AppChannel.Portable, "x64", "ReStore-cli_1.8.0_win-x64.zip")]
    [InlineData(AppChannel.Portable, "arm64", null)]
    [InlineData(AppChannel.Dev, "x64", null)]
    public async Task Asset_IsChosenPerChannelAndArchitecture(string channel, string arch, string? expected)
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Json(ManifestJson("1.8.0"))));
        var result = await Checker(handler, channel: channel, arch: arch).CheckAsync(false);
        result.Asset?.Name.Should().Be(expected);
        if (expected == null)
        {
            result.Asset.Should().BeNull();
        }
    }

    [Fact]
    public async Task Manifest404_FallsBackToRedirectTag_VersionOnly()
    {
        var handler = new FakeHttpHandler((request, _) => Task.FromResult(request.RequestUri!.ToString() switch
        {
            UpdateChecker.DefaultManifestUrl => FakeHttpHandler.Status(HttpStatusCode.NotFound),
            UpdateChecker.LatestReleaseUrl => FakeHttpHandler.Redirect("https://github.com/js302/ReStore/releases/tag/v1.9.2"),
            _ => FakeHttpHandler.Status(HttpStatusCode.BadRequest)
        }));
        var result = await Checker(handler).CheckAsync(false);

        result.Status.Should().Be(UpdateStatus.UpdateAvailable);
        result.Latest.Should().Be("1.9.2");
        result.VersionOnly.Should().BeTrue();
        result.Asset.Should().BeNull();
        result.ReleaseNotesUrl.Should().Be("https://github.com/js302/ReStore/releases/tag/v1.9.2");
    }

    [Fact]
    public async Task InvalidManifestJson_FallsBackToRedirect()
    {
        var handler = new FakeHttpHandler((request, _) => Task.FromResult(request.RequestUri!.ToString() == UpdateChecker.DefaultManifestUrl
            ? FakeHttpHandler.Json("{ not json")
            : FakeHttpHandler.Redirect("/js302/ReStore/releases/tag/v1.7.0")));
        var result = await Checker(handler).CheckAsync(false);
        result.Status.Should().Be(UpdateStatus.UpToDate);
        result.Latest.Should().Be("1.7.0");
    }

    [Fact]
    public async Task ManifestWithoutUsableVersion_IsInvalid()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Json("""{"schemaVersion":1,"version":"banana"}""")));
        var result = await Checker(handler).CheckAsync(false);
        result.Status.Should().Be(UpdateStatus.Failed);
    }

    [Fact]
    public async Task EverythingMissing_IsFailedWithError_NeverThrows()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Status(HttpStatusCode.NotFound)));
        var result = await Checker(handler).CheckAsync(false);
        result.Status.Should().Be(UpdateStatus.Failed);
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task NetworkError_IsFailed()
    {
        var handler = new FakeHttpHandler((_, _) => throw new HttpRequestException("no route"));
        var result = await Checker(handler).CheckAsync(false);
        result.Status.Should().Be(UpdateStatus.Failed);
        result.Error.Should().Contain("no route");
    }

    [Fact]
    public async Task Timeout_IsFailed()
    {
        var handler = new FakeHttpHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return FakeHttpHandler.Status(HttpStatusCode.OK);
        });
        var result = await Checker(handler, timeout: TimeSpan.FromMilliseconds(100)).CheckAsync(false);
        result.Status.Should().Be(UpdateStatus.Failed);
        result.Error.Should().Contain("timed out");
    }

    [Fact]
    public async Task ManifestUrlOverride_IsUsed_WithoutGitHubFallbacks()
    {
        var handler = new FakeHttpHandler((request, _) => Task.FromResult(request.RequestUri!.Host == "mirror.example"
            ? FakeHttpHandler.Json(ManifestJson("2.0.0"))
            : FakeHttpHandler.Status(HttpStatusCode.NotFound)));
        var result = await Checker(handler, env: n => n == UpdateChecker.ManifestUrlEnvironmentVariable ? "https://mirror.example/latest.json" : null).CheckAsync(false);
        result.Latest.Should().Be("2.0.0");
        handler.Requests.Should().Equal("https://mirror.example/latest.json");

        var broken = new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Status(HttpStatusCode.NotFound)));
        (await Checker(broken, env: n => n == UpdateChecker.ManifestUrlEnvironmentVariable ? "https://mirror.example/latest.json" : null).CheckAsync(false))
            .Status.Should().Be(UpdateStatus.Failed);
        broken.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData("http://mirror.example/latest.json", false)]
    [InlineData("http://192.168.1.5/latest.json", false)]
    [InlineData("ftp://mirror.example/latest.json", false)]
    [InlineData("not a url", false)]
    [InlineData("https://mirror.example/latest.json", true)]
    [InlineData("http://127.0.0.1:8080/latest.json", true)]
    [InlineData("http://localhost:8080/latest.json", true)]
    public async Task ManifestUrlOverride_MustBeHttps_ExceptForLoopback(string url, bool accepted)
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Json(ManifestJson("2.0.0"))));

        var result = await Checker(handler, env: n => n == UpdateChecker.ManifestUrlEnvironmentVariable ? url : null).CheckAsync(false);

        if (accepted)
        {
            result.Latest.Should().Be("2.0.0");
        }
        else
        {
            result.Status.Should().Be(UpdateStatus.Failed);
            result.Error.Should().Contain("https");
            handler.Requests.Should().BeEmpty("nothing is fetched from an unencrypted, non-local mirror");
        }
    }

    [Fact]
    public async Task ReleaseNotesUrlAndTag_FromTheManifest_LoseControlCharacters()
    {
        var json = ManifestJson("2.0.0").Replace("https://github.com/js302/ReStore/releases/tag/v2.0.0", "https://x/\\u001b[31mnotes\\u0007\\r\\n");
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Json(json)));

        var result = await Checker(handler).CheckAsync(false);

        result.ReleaseNotesUrl.Should().Be("https://x/[31mnotes");
        new UpdateNotice("2.0.0", "1.7.0", false, "https://x/\u001b]0;pwned\u0007y").CliLine.Should().NotContainAny("\u001b", "\u0007");
    }

    private static string ReleasesJson() => """
        [
          { "tag_name": "v1.9.0-beta.1", "prerelease": true, "draft": false, "assets": [ { "name": "latest.json", "browser_download_url": "https://dl/beta1.json" } ] },
          { "tag_name": "v1.9.0-beta.2", "prerelease": true, "draft": false, "assets": [ { "name": "latest.json", "browser_download_url": "https://dl/beta2.json" } ] },
          { "tag_name": "v1.10.0-beta.1", "prerelease": true, "draft": false, "assets": [ { "name": "ReStore.cer", "browser_download_url": "https://dl/cer" } ] },
          { "tag_name": "v2.0.0-draft", "prerelease": true, "draft": true, "assets": [ { "name": "latest.json", "browser_download_url": "https://dl/draft.json" } ] },
          { "tag_name": "v1.8.0", "prerelease": false, "draft": false, "assets": [ { "name": "latest.json", "browser_download_url": "https://dl/stable.json" } ] }
        ]
        """;

    [Fact]
    public async Task Prereleases_PicksHighestVersionWithManifest()
    {
        var handler = new FakeHttpHandler((request, _) => Task.FromResult(request.RequestUri!.ToString() switch
        {
            UpdateChecker.ReleasesApiUrl => FakeHttpHandler.Json(ReleasesJson()),
            "https://dl/beta2.json" => FakeHttpHandler.Json(ManifestJson("1.9.0-beta.2")),
            _ => FakeHttpHandler.Status(HttpStatusCode.NotFound)
        }));
        var result = await Checker(handler).CheckAsync(includePrereleases: true);
        result.Latest.Should().Be("1.9.0-beta.2");
        result.Status.Should().Be(UpdateStatus.UpdateAvailable);
        handler.Requests.Should().Equal(UpdateChecker.ReleasesApiUrl, "https://dl/beta2.json");
    }

    [Fact]
    public async Task Prereleases_RateLimited_FallsBackToStablePath()
    {
        var handler = new FakeHttpHandler((request, _) => Task.FromResult(request.RequestUri!.ToString() switch
        {
            UpdateChecker.ReleasesApiUrl => FakeHttpHandler.Status(HttpStatusCode.Forbidden),
            UpdateChecker.DefaultManifestUrl => FakeHttpHandler.Json(ManifestJson("1.8.0")),
            _ => FakeHttpHandler.Status(HttpStatusCode.NotFound)
        }));
        var result = await Checker(handler).CheckAsync(includePrereleases: true);
        result.Latest.Should().Be("1.8.0");
    }

    [Fact]
    public async Task StableOnly_NeverCallsTheApi()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Json(ManifestJson("1.8.0"))));
        await Checker(handler).CheckAsync(false);
        handler.Requests.Should().NotContain(r => r.Contains("api.github.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReleaseBeatsItsPrereleases_AndPrereleaseIsNotOfferedOverCurrentRelease()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Json(ManifestJson("1.8.0-rc.1"))));
        (await Checker(handler, current: "1.8.0").CheckAsync(false)).Status.Should().Be(UpdateStatus.UpToDate);
        (await Checker(handler, current: "1.7.0").CheckAsync(false)).Status.Should().Be(UpdateStatus.UpdateAvailable);
    }
}

public class SemVerTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.1", "1.0.0", 1)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    [InlineData("1.0.0", "1.0.0-rc.1", 1)]
    [InlineData("1.0.0-rc.1", "1.0.0-beta.9", 1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-1", "1.0.0-alpha", -1)]
    [InlineData("v1.2.3", "1.2.3", 0)]
    [InlineData("1.2.3+abc", "1.2.3+def", 0)]
    [InlineData("1.2.3-rc.1+build", "1.2.3-rc.1", 0)]
    public void Compare(string a, string b, int expected)
    {
        SemVer.TryParse(a, out var left).Should().BeTrue();
        SemVer.TryParse(b, out var right).Should().BeTrue();
        Math.Sign(left.CompareTo(right)).Should().Be(expected);
        Math.Sign(right.CompareTo(left)).Should().Be(-expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("a.b.c")]
    [InlineData("1.2.-3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-a..b")]
    [InlineData(null)]
    public void Invalid_IsRejected(string? text) => SemVer.TryParse(text, out _).Should().BeFalse();

    [Fact]
    public void ToString_DropsMetadataAndPrefix() => SemVer.ParseOrNull("v1.2.3-rc.1+sha.5")!.ToString().Should().Be("1.2.3-rc.1");
}

[Collection("GlobalState")]
public class UpdateServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ReStoreUpd_" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private int _calls;
    private string _latest = "1.8.0";
    private bool _fail;

    public UpdateServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private UpdateService Service(Func<string, string?>? env = null, bool disableUnderTest = false, string current = "1.7.0")
    {
        var handler = new FakeHttpHandler((_, _) =>
        {
            _calls++;
            return Task.FromResult(_fail ? FakeHttpHandler.Status(HttpStatusCode.InternalServerError) : FakeHttpHandler.Json(UpdateCheckTests.ManifestJson(_latest)));
        });
        env ??= _ => null;
        var checker = new UpdateChecker(handler, current, AppChannel.Msix, "x64", null, env, () => _now);
        return new UpdateService(checker, new UpdateCache(Path.Combine(_dir, "update-check.json")), () => _now, env, disableUnderTest, current);
    }

    [Fact]
    public async Task AutomaticCheck_RunsOncePer24Hours_ForceBypasses()
    {
        var service = Service();
        var settings = new UpdatesConfig();

        (await service.CheckAutomaticallyAsync(settings)).Should().NotBeNull();
        (await service.CheckAutomaticallyAsync(settings)).Should().BeNull();
        _calls.Should().Be(1);

        _now += TimeSpan.FromHours(23);
        (await service.CheckAutomaticallyAsync(settings)).Should().BeNull();
        _now += TimeSpan.FromHours(2);
        (await service.CheckAutomaticallyAsync(settings)).Should().NotBeNull();
        _calls.Should().Be(2);

        (await service.CheckAutomaticallyAsync(settings, force: true)).Should().NotBeNull();
        _calls.Should().Be(3);
    }

    [Fact]
    public async Task ExplicitCheck_AlwaysWorks_EvenWhenAutomaticIsOff()
    {
        var service = Service(env: n => n == "RESTORE_NO_UPDATE_CHECK" ? "1" : null);
        var settings = new UpdatesConfig { CheckAutomatically = false };
        (await service.CheckNowAsync(settings)).Status.Should().Be(UpdateStatus.UpdateAvailable);
        _calls.Should().Be(1);
    }

    [Theory]
    [InlineData("config")]
    [InlineData("env")]
    [InlineData("ci")]
    public async Task AutomaticChecks_AreOff_WhenOptedOut(string how)
    {
        var service = Service(env: n => how switch
        {
            "env" when n == "RESTORE_NO_UPDATE_CHECK" => "1",
            "ci" when n == "CI" => "true",
            _ => null
        });
        var settings = new UpdatesConfig { CheckAutomatically = how != "config" };
        (await service.CheckAutomaticallyAsync(settings)).Should().BeNull();
        (await service.CheckAutomaticallyAsync(settings, force: true)).Should().BeNull();
        _calls.Should().Be(0);
    }

    [Fact]
    public async Task AutomaticChecks_AreOff_UnderTheAppPathsTestOverride()
    {
        // The test suite runs with AppPaths.OverrideRoot set, so the default service must never go to the network.
        var service = Service(disableUnderTest: true);
        (await service.CheckAutomaticallyAsync(new UpdatesConfig())).Should().BeNull();
        _calls.Should().Be(0);
    }

    [Fact]
    public async Task FailedCheck_IsRetriedAfterAnHour_NotImmediately()
    {
        _fail = true;
        var service = Service();
        var settings = new UpdatesConfig();
        (await service.CheckAutomaticallyAsync(settings))!.Status.Should().Be(UpdateStatus.Failed);
        (await service.CheckAutomaticallyAsync(settings)).Should().BeNull();
        _now += TimeSpan.FromMinutes(61);
        _fail = false;
        (await service.CheckAutomaticallyAsync(settings))!.Status.Should().Be(UpdateStatus.UpdateAvailable);
    }

    [Fact]
    public async Task Cache_PersistsResult_AndCorruptFileIsEmptyCache()
    {
        var service = Service();
        await service.CheckNowAsync(new UpdatesConfig());
        var data = new UpdateCache(Path.Combine(_dir, "update-check.json")).Load();
        data.LastCheckUtc.Should().Be(_now);
        data.Result!.Latest.Should().Be("1.8.0");
        data.Result.Asset!.Name.Should().Be("ReStore_1.8.0_x64.msix");

        File.WriteAllText(Path.Combine(_dir, "update-check.json"), "{ nope");
        new UpdateCache(Path.Combine(_dir, "update-check.json")).Load().Result.Should().BeNull();
    }

    [Fact]
    public async Task Skip_HidesThatVersionOnly_AndCriticalIgnoresSkip()
    {
        var service = Service();
        await service.CheckNowAsync(new UpdatesConfig());
        service.GetPendingUpdate().Should().NotBeNull();

        service.Skip("v1.8.0");
        service.GetSkippedVersion().Should().Be("1.8.0");
        service.GetPendingUpdate().Should().BeNull();
        service.GetNotice().Should().BeNull();

        _latest = "1.8.1";
        await service.CheckNowAsync(new UpdatesConfig());
        service.GetPendingUpdate().Should().NotBeNull("a newer version than the skipped one is offered");

        service.Skip("1.8.1");
        service.ClearSkip();
        service.GetPendingUpdate().Should().NotBeNull();
    }

    [Fact]
    public async Task Notice_IsThrottledTo24Hours_AndRepeatsForNewVersions()
    {
        var service = Service();
        await service.CheckNowAsync(new UpdatesConfig());

        var notice = service.GetNotice();
        notice.Should().NotBeNull();
        notice!.CliLine.Should().Be("ReStore 1.8.0 is available (you have 1.7.0). Update: restore update install - notes: https://github.com/js302/ReStore/releases/tag/v1.8.0");
        service.MarkNotified(notice);

        service.GetNotice().Should().BeNull();
        _now += TimeSpan.FromHours(23);
        service.GetNotice().Should().BeNull();
        _now += TimeSpan.FromHours(2);
        service.GetNotice().Should().NotBeNull();

        service.MarkNotified(service.GetNotice()!);
        _latest = "1.9.0";
        await service.CheckNowAsync(new UpdatesConfig());
        service.GetNotice().Should().NotBeNull("a new version is announced at once");
    }

    [Fact]
    public async Task CachedUpdate_StopsBeingOffered_OnceTheRunningVersionCatchesUp()
    {
        await Service().CheckNowAsync(new UpdatesConfig());
        var upgraded = Service(current: "1.8.0");
        upgraded.GetPendingUpdate().Should().BeNull();
        upgraded.GetNotice().Should().BeNull();
        upgraded.GetCachedResult()!.Status.Should().Be(UpdateStatus.UpToDate);
    }
}
