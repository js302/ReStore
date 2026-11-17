using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ReStore.E2E;

/// <summary>
/// A tiny HTTP/1.1 server on 127.0.0.1 (an ephemeral port) that serves fixed bodies and records every request.
/// A raw TcpListener rather than HttpListener, so no URL reservation or administrator rights are needed on Windows.
/// </summary>
internal sealed class LocalHttpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentDictionary<string, (string ContentType, byte[] Body)> _routes = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Request> _requests = new();
    private readonly CancellationTokenSource _stop = new();

    public sealed record Request(string Method, string Path, IReadOnlyDictionary<string, string> Headers);

    public LocalHttpServer()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public IReadOnlyCollection<Request> Requests => [.. _requests];

    public void Serve(string path, byte[] body, string contentType = "application/octet-stream") => _routes[path] = (contentType, body);

    public void ServeText(string path, string text, string contentType = "application/json") => Serve(path, Encoding.UTF8.GetBytes(text), contentType);

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(requestLine))
                {
                    return;
                }

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().ConfigureAwait(false)))
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                    }
                }

                var parts = requestLine.Split(' ');
                var path = parts.Length > 1 ? parts[1] : "/";
                _requests.Enqueue(new Request(parts[0], path, headers));

                var found = _routes.TryGetValue(path, out var route);
                var body = found ? route.Body : "not found"u8.ToArray();
                var head = $"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\nContent-Type: {(found ? route.ContentType : "text/plain")}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
                if (parts[0] != "HEAD")
                {
                    await stream.WriteAsync(body).ConfigureAwait(false);
                }
            }
            catch (IOException)
            {
                // The client went away.
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
