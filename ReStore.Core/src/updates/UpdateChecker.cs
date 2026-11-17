using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.updates;

/// <summary>
/// Asks GitHub whether a newer ReStore exists. The normal path downloads <c>latest.json</c> from the latest release (a plain
/// file download, not the rate-limited API). Never throws: every failure is a <see cref="UpdateStatus.Failed"/> result.
/// Only GitHub (or the host named by <c>RESTORE_UPDATE_MANIFEST_URL</c>) is contacted; the only thing sent is the User-Agent.
/// </summary>
public sealed class UpdateChecker
{
    public const string Repository = "js302/ReStore";
    public const string ManifestUrlEnvironmentVariable = "RESTORE_UPDATE_MANIFEST_URL";
    public const string DefaultManifestUrl = "https://github.com/" + Repository + "/releases/latest/download/latest.json";
    public const string LatestReleaseUrl = "https://github.com/" + Repository + "/releases/latest";
    public const string ReleasesApiUrl = "https://api.github.com/repos/" + Repository + "/releases?per_page=10";
    public const string ManifestAssetName = "latest.json";

    private static readonly Lazy<HttpMessageHandler> SharedFollowHandler = new(() => new SocketsHttpHandler { AllowAutoRedirect = true });
    private static readonly Lazy<HttpMessageHandler> SharedNoRedirectHandler = new(() => new SocketsHttpHandler { AllowAutoRedirect = false });

    private readonly HttpClient _follow;
    private readonly HttpClient _noRedirect;
    private readonly string _current;
    private readonly string _channel;
    private readonly string _arch;
    private readonly Func<string, string?> _environment;
    private readonly Func<DateTime> _now;

    /// <param name="handler">Tests inject a fake; it must itself answer 3xx for the redirect fallback. Null uses real network handlers.</param>
    public UpdateChecker(
        HttpMessageHandler? handler = null,
        string? currentVersion = null,
        string? channel = null,
        string? architecture = null,
        TimeSpan? timeout = null,
        Func<string, string?>? environment = null,
        Func<DateTime>? now = null)
    {
        _follow = new HttpClient(handler ?? SharedFollowHandler.Value, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        _noRedirect = new HttpClient(handler ?? SharedNoRedirectHandler.Value, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        _current = currentVersion ?? CurrentVersion;
        _channel = channel ?? AppChannel.Current;
        _arch = architecture ?? AppChannel.Architecture;
        RequestTimeout = timeout ?? TimeSpan.FromSeconds(10);
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public TimeSpan RequestTimeout { get; }

    /// <summary>The running version without build metadata.</summary>
    public static string CurrentVersion => AboutInfo.VersionOf(typeof(UpdateChecker).Assembly);

    public static string UserAgent => $"ReStore/{CurrentVersion}";

    public async Task<UpdateCheckResult> CheckAsync(bool includePrereleases, CancellationToken cancellationToken = default)
    {
        try
        {
            return await CheckCoreAsync(includePrereleases, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failed("The update check was cancelled.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or JsonException or OperationCanceledException or UriFormatException)
        {
            return Failed(Describe(ex));
        }
    }

    private async Task<UpdateCheckResult> CheckCoreAsync(bool includePrereleases, CancellationToken cancellationToken)
    {
        var overrideUrl = _environment(ManifestUrlEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideUrl))
        {
            // A mirror: its manifest or nothing (no GitHub fallbacks). The manifest carries the hash and URL of the download, so a
            // plain-http mirror would let anyone on the network substitute both: https only (http for loopback, which tests use).
            if (!IsAcceptableMirrorUrl(overrideUrl.Trim()))
            {
                return Failed($"{ManifestUrlEnvironmentVariable} must be an https:// URL (http:// is accepted only for 127.0.0.1 or localhost).");
            }

            var mirror = await TryGetManifestAsync(overrideUrl.Trim(), cancellationToken).ConfigureAwait(false);
            return mirror.Manifest != null ? FromManifest(mirror.Manifest) : Failed(mirror.Error ?? "The update manifest is not valid.");
        }

        if (includePrereleases)
        {
            var pre = await TryPrereleaseManifestAsync(cancellationToken).ConfigureAwait(false);
            if (pre != null)
            {
                return FromManifest(pre);
            }
        }

        var primary = await TryGetManifestAsync(DefaultManifestUrl, cancellationToken).ConfigureAwait(false);
        if (primary.Manifest != null)
        {
            return FromManifest(primary.Manifest);
        }

        // latest.json missing or invalid: the latest release redirect still tells us the version number.
        var tag = await TryGetLatestTagAsync(cancellationToken).ConfigureAwait(false);
        if (tag is { } found)
        {
            return Build(found.Version.ToString(), critical: false, "https://github.com/" + Repository + "/releases/tag/" + found.Tag, asset: null, versionOnly: true);
        }

        return Failed(primary.Error ?? "Could not determine the latest version.");
    }

    /// <summary>https, or http to the local machine only.</summary>
    internal static bool IsAcceptableMirrorUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))));

    private async Task<(UpdateManifest? Manifest, string? Error)> TryGetManifestAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(_follow, url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (null, $"HTTP {(int)response.StatusCode} while fetching the update manifest.");
        }

        var body = await ReadLimitedAsync(response, cancellationToken).ConfigureAwait(false);
        var manifest = UpdateManifest.TryParse(body);
        return manifest != null ? (manifest, null) : (null, "The update manifest is not valid.");
    }

    private async Task<UpdateManifest?> TryPrereleaseManifestAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(_follow, ReleasesApiUrl, cancellationToken, "application/vnd.github+json").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 403/429 rate limit (or anything else): use the stable path.
                return null;
            }

            using var document = JsonDocument.Parse(await ReadLimitedAsync(response, cancellationToken).ConfigureAwait(false));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            (SemVer Version, string Url)? best = null;
            foreach (var release in document.RootElement.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object))
            {
                if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                if (!release.TryGetProperty("tag_name", out var tagElement) || SemVer.ParseOrNull(tagElement.GetString()) is not { } version
                    || !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var manifestAsset = assets.EnumerateArray().FirstOrDefault(a =>
                    a.ValueKind == JsonValueKind.Object && a.TryGetProperty("name", out var n) && n.GetString() == ManifestAssetName && a.TryGetProperty("browser_download_url", out _));
                if (manifestAsset.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (best == null || version > best.Value.Version)
                {
                    best = (version, manifestAsset.GetProperty("browser_download_url").GetString() ?? string.Empty);
                }
            }

            if (best == null || best.Value.Url.Length == 0)
            {
                return null;
            }

            return (await TryGetManifestAsync(best.Value.Url, cancellationToken).ConfigureAwait(false)).Manifest;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<(SemVer Version, string Tag)?> TryGetLatestTagAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(_noRedirect, LatestReleaseUrl, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is not { } location)
            {
                return null;
            }

            var path = location.OriginalString;
            const string marker = "/releases/tag/";
            var index = path.LastIndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                return null;
            }

            var tag = Uri.UnescapeDataString(path[(index + marker.Length)..].Split('?', '#')[0].Trim('/'));
            return SemVer.ParseOrNull(tag) is { } version ? (version, tag) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpClient client, string url, CancellationToken cancellationToken, string? accept = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ReStore", CurrentVersion));
        if (accept != null)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        }

        try
        {
            // Headers only: the caller streams the body under a size cap and its own timeout, so an oversized body is never buffered.
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException($"The request timed out after {RequestTimeout.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>Largest manifest or release list accepted.</summary>
    internal const int MaxResponseBytes = 4 * 1024 * 1024;

    private async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Manifests and release lists are small; refuse anything absurd, as soon as the declared or the streamed size exceeds the cap.
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new InvalidOperationException("The response is too large.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var buffer = new MemoryStream();
            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var chunk = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxResponseBytes)
                    {
                        throw new InvalidOperationException("The response is too large.");
                    }

                    buffer.Write(chunk, 0, read);
                }
            }

            return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException($"The request timed out after {RequestTimeout.TotalSeconds:0} seconds.");
        }
    }

    private UpdateCheckResult FromManifest(UpdateManifest manifest) =>
        Build(manifest.Version, manifest.Critical, manifest.ReleaseNotesUrl, UpdateInstaller.SelectAsset(manifest.Assets, _channel, _arch), versionOnly: false);

    private UpdateCheckResult Build(string latest, bool critical, string? notes, UpdateAsset? asset, bool versionOnly)
    {
        var available = SemVer.ParseOrNull(latest) is { } latestVersion && (SemVer.ParseOrNull(_current) is not { } current || latestVersion > current);
        return new UpdateCheckResult
        {
            Status = available ? UpdateStatus.UpdateAvailable : UpdateStatus.UpToDate,
            Current = _current,
            Latest = latest,
            Critical = critical && available,
            ReleaseNotesUrl = notes,
            Asset = available ? asset : null,
            CheckedUtc = _now(),
            VersionOnly = versionOnly
        };
    }

    private UpdateCheckResult Failed(string error) => new()
    {
        Status = UpdateStatus.Failed,
        Current = _current,
        CheckedUtc = _now(),
        Error = error
    };

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } code } => $"HTTP {(int)code}",
        HttpRequestException http when http.InnerException is { } inner => $"Network error: {inner.Message}",
        HttpRequestException http => http.Message,
        JsonException => "The server answered with something that is not valid JSON.",
        _ => ex.Message
    };
}
