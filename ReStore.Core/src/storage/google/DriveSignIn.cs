using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using ReStore.Core.src.cli;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.storage.google;

/// <summary>
/// The one-off interactive Google Drive sign-in behind <c>auth gdrive</c>: the same installed-app flow, scope, user key and token
/// store as <see cref="DriveStorage"/>, so the token it stores is the one the storage later finds.
/// </summary>
internal static class DriveSignIn
{
    /// <summary>How long the sign-in waits for the browser callback.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public static async Task RunAsync(DriveSignInRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        ICodeReceiver receiver = request.NoBrowser ? new PrintUrlCodeReceiver(request.ShowMessage) : new LocalServerCodeReceiver();
        try
        {
            // Full Drive scope, exactly as DriveStorage requests it: tokens and uploaded data depend on it.
            await GoogleWebAuthorizationBroker.AuthorizeAsync(
                new ClientSecrets { ClientId = request.ClientId, ClientSecret = request.ClientSecret },
                [DriveService.Scope.Drive],
                DriveStorage.TokenUserKey,
                timeout.Token,
                DriveStorage.CreateTokenStore(request.TokenFolder, isUnattended: () => false),
                receiver).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Google Drive sign-in was not completed within {Timeout.TotalMinutes:0} minutes.");
        }
    }

    /// <summary>The loopback receiver, except that it prints the sign-in URL instead of opening a browser (for a person on this machine to open).</summary>
    private sealed class PrintUrlCodeReceiver(Action<string> show) : LocalServerCodeReceiver
    {
        protected override bool OpenBrowser(string url)
        {
            show($"Open this URL in a browser on this computer and approve access (waiting up to {Timeout.TotalMinutes:0} minutes):{Environment.NewLine}{url}");
            return true;
        }
    }
}
