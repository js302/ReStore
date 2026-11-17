using ReStore.Core.src.utils;

namespace ReStore.Core.src.cli;

/// <summary>What <c>auth gdrive</c> hands to the sign-in routine.</summary>
internal sealed record DriveSignInRequest(string ClientId, string ClientSecret, string TokenFolder, bool NoBrowser, Action<string> ShowMessage);

/// <summary>
/// The few things CLI commands reach outside the process for, as replaceable seams so tests never need DPAPI, a network or a
/// browser. Production code never changes them.
/// </summary>
internal static class CliServices
{
    /// <summary>Protects the remembered password and <c>--protect</c>ed secrets (Windows DPAPI).</summary>
    public static ISecretProtector SecretProtector { get; set; } = DpapiSecretProtector.Instance;

    /// <summary>Handler of the HTTP client used for OAuth token exchanges; null means the default one.</summary>
    public static HttpMessageHandler? HttpHandler { get; set; }

    /// <summary>The interactive Google sign-in; the default opens the browser (or prints the URL) and stores the token.</summary>
    public static Func<DriveSignInRequest, CancellationToken, Task> DriveSignIn { get; set; } = ReStore.Core.src.storage.google.DriveSignIn.RunAsync;

    public static HttpClient CreateHttpClient() =>
        HttpHandler != null ? new HttpClient(HttpHandler, disposeHandler: false) : new HttpClient();

    public static void Reset()
    {
        SecretProtector = DpapiSecretProtector.Instance;
        HttpHandler = null;
        DriveSignIn = ReStore.Core.src.storage.google.DriveSignIn.RunAsync;
    }
}
