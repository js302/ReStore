using System.Net.Http.Headers;
using System.Text;

namespace ReStore.Core.src.telemetry;

/// <summary>Where telemetry goes. No endpoint is decided yet, so none is built in.</summary>
public static class TelemetryEndpoint
{
    public const string EnvironmentVariable = "RESTORE_TELEMETRY_ENDPOINT";

    /// <summary>
    /// TODO: the collection endpoint has not been decided. While this is null events are only queued locally
    /// (bounded, inspectable with <c>restore telemetry preview</c>). Setting it is the only change needed to start sending.
    /// </summary>
    public const string? Default = null;

    /// <summary>The environment override (for tests and self-hosting) if it is an http(s) URL, else <see cref="Default"/>.</summary>
    public static Uri? Resolve(Func<string, string?> getEnv)
    {
        foreach (var candidate in new[] { getEnv(EnvironmentVariable), Default })
        {
            if (!string.IsNullOrWhiteSpace(candidate)
                && Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https")
            {
                return uri;
            }
        }

        return null;
    }
}

/// <summary>Delivers a batch of queued events. Returns true only when the receiver accepted them (events are then removed).</summary>
public interface ITelemetrySender
{
    Task<bool> SendAsync(IReadOnlyList<string> eventJsonLines, CancellationToken cancellationToken);
}

/// <summary>POSTs <c>{ "events": [...] }</c> as JSON; any 2xx is success. Never throws for network problems.</summary>
public sealed class HttpTelemetrySender : ITelemetrySender, IDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly Uri _endpoint;
    private readonly HttpClient _client;

    public HttpTelemetrySender(Uri endpoint, HttpMessageHandler? handler = null, string? userAgent = null)
    {
        _endpoint = endpoint;
        _client = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _client.Timeout = RequestTimeout;
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ReStore-Telemetry", userAgent ?? "1"));
    }

    public async Task<bool> SendAsync(IReadOnlyList<string> eventJsonLines, CancellationToken cancellationToken)
    {
        if (eventJsonLines.Count == 0)
        {
            return true;
        }

        try
        {
            var body = "{\"events\":[" + string.Join(",", eventJsonLines) + "]}";
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _client.PostAsync(_endpoint, content, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose() => _client.Dispose();
}
