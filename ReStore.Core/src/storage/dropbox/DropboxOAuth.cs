using System.Text.Json;
using Dropbox.Api;

namespace ReStore.Core.src.storage.dropbox;

/// <summary>The code exchange was refused (wrong or expired code, wrong app key) or its answer was unusable.</summary>
internal sealed class DropboxAuthException(string message) : Exception(message);

/// <summary>
/// The two steps of Dropbox's authorization-code flow with PKCE and no redirect URI, used by <c>auth dropbox</c>: build the URL a
/// person opens (the code is then shown to them), and exchange that code for a long-lived refresh token. No secret is needed
/// for the exchange (the code verifier proves it is the same client). The HTTP client is injected so tests need no network.
/// </summary>
internal static class DropboxOAuth
{
    public const string TokenEndpoint = "https://api.dropboxapi.com/oauth2/token";

    /// <summary>A fresh code verifier and its S256 challenge (the Dropbox SDK's PKCE helpers).</summary>
    public static (string Verifier, string Challenge) NewPkcePair()
    {
        var verifier = DropboxOAuth2Helper.GeneratePKCECodeVerifier();
        return (verifier, DropboxOAuth2Helper.GeneratePKCECodeChallenge(verifier));
    }

    /// <summary>The authorize URL: response_type=code, token_access_type=offline (so a refresh token is issued), the PKCE challenge, no redirect URI.</summary>
    public static string BuildAuthorizeUrl(string appKey, string codeChallenge) =>
        DropboxOAuth2Helper.GetAuthorizeUri(
            OAuthResponseType.Code,
            appKey,
            (string?)null,
            tokenAccessType: TokenAccessType.Offline,
            codeChallenge: codeChallenge).AbsoluteUri;

    /// <summary>Exchanges the code the person copied for tokens; returns the refresh token.</summary>
    public static async Task<string> ExchangeCodeForRefreshTokenAsync(
        HttpClient http, string appKey, string code, string codeVerifier, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["client_id"] = appKey,
            ["code_verifier"] = codeVerifier
        });

        using var response = await http.PostAsync(TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new DropboxAuthException(DescribeError(body, (int)response.StatusCode));
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("refresh_token", out var token) && token.GetString() is { Length: > 0 } refreshToken)
            {
                return refreshToken;
            }
        }
        catch (JsonException)
        {
            // Falls through to the message below.
        }

        throw new DropboxAuthException("Dropbox did not return a refresh token. Make sure the authorize URL came from 'restore auth dropbox' (it asks for offline access).");
    }

    private static string DescribeError(string body, int statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var description = root.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (description != null || error != null)
            {
                return $"Dropbox refused the code (HTTP {statusCode}): {description ?? error}";
            }
        }
        catch (JsonException)
        {
            // Not JSON: report the status only (never echo a body that might contain a token).
        }

        return $"Dropbox refused the code (HTTP {statusCode}).";
    }
}
