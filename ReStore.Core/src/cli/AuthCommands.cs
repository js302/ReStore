using System.Text.Json;
using System.Text.Json.Nodes;
using ReStore.Core.src.storage;
using ReStore.Core.src.storage.dropbox;
using ReStore.Core.src.storage.google;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.cli;

/// <summary><c>auth status|gdrive|dropbox</c>: sign-ins of the OAuth storage providers.</summary>
internal static class AuthCommands
{
    public const string DropboxStateFile = "dropbox-pkce.json";

    private static string DropboxStatePath => Path.Combine(AppPaths.StateDirectory, DropboxStateFile);

    public static IEnumerable<CommandSpec> Specs { get; } =
    [
        new CommandSpec
        {
            Name = "auth status",
            Category = CommandCategory.Configuration,
            Summary = "Show, per OAuth storage provider (gdrive, dropbox), whether it is configured and signed in.",
            Description = "Read-only; opens no browser and calls no server. JSON: providers, a list of {provider, configured, tokenPresent, ...}.",
            Examples = ["auth status --json"],
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            PayloadOnStdout = true,
            Handler = StatusAsync
        },
        new CommandSpec
        {
            Name = "auth gdrive",
            Category = CommandCategory.Configuration,
            Summary = "Sign in to Google Drive once (needs client_id and client_secret in the gdrive storage).",
            Description = "Runs the same installed-app sign-in as the app and stores the token where the storage finds it (DPAPI-protected on Windows). Opens a browser; --no-browser prints the URL instead for a person to open on this computer. "
                + "Without a terminal (non-interactive) it exits 4 unless --no-browser is given, because a person has to approve access. The sign-in waits 5 minutes. JSON: provider, signedIn, tokenFolder.",
            Options = [new OptionSpec("--no-browser", "Print the sign-in URL (on stderr) instead of opening a browser.")],
            Examples = ["auth gdrive", "auth gdrive --no-browser"],
            Mutates = true,
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            PayloadOnStdout = true,
            Handler = GDriveAsync
        },
        new CommandSpec
        {
            Name = "auth dropbox",
            Category = CommandCategory.Configuration,
            Summary = "Authorise Dropbox in two steps (print the URL, then exchange the code for a refresh token).",
            Description = "Step 1, without --code: needs appKey in the dropbox storage; prints an authorize URL (PKCE, offline access, no redirect URI) and remembers the PKCE verifier in state/dropbox-pkce.json. A person opens it, approves, and gives the code to the agent. "
                + "Step 2, --code <code>: exchanges the code for a refresh token and writes it as refreshToken (DPAPI-protected on Windows) next to appKey/appSecret. The code is single-use and short-lived. "
                + "JSON of step 1: authorizeUrl, nextStep; of step 2: refreshTokenStored, protected, warnings.",
            Options = [new OptionSpec("--code", "The code Dropbox showed after approval (step 2).", "code")],
            Examples = ["auth dropbox", "auth dropbox --code <code>"],
            Mutates = true,
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            PayloadOnStdout = true,
            Handler = DropboxAsync
        }
    ];

    private static StorageConfig? Source(CliContext ctx, string type) =>
        ctx.Config.StorageSources.TryGetValue(type, out var source) ? source : null;

    internal static bool HasValue(StorageConfig? source, string option) =>
        source != null && source.Options.TryGetValue(option, out var value) && !CliSupport.IsPlaceholder(value);

    internal static string TokenFolderOf(StorageConfig? source) =>
        ConfigManager.ExpandConfiguredPath(source != null && source.Options.TryGetValue("token_folder", out var folder) && !string.IsNullOrWhiteSpace(folder) ? folder : DriveStorage.DefaultTokenFolder);

    private static async Task<int> StatusAsync(CliContext ctx)
    {
        var drive = Source(ctx, "gdrive");
        var dropbox = Source(ctx, "dropbox");
        var tokenFolder = TokenFolderOf(drive);
        var pending = File.Exists(DropboxStatePath);
        var providers = new JsonArray(
            new JsonObject
            {
                ["provider"] = "gdrive",
                ["configured"] = HasValue(drive, "client_id") && HasValue(drive, "client_secret"),
                ["tokenPresent"] = DriveStorage.HasStoredToken(tokenFolder),
                ["tokenFolder"] = tokenFolder
            },
            new JsonObject
            {
                ["provider"] = "dropbox",
                ["configured"] = HasValue(dropbox, "appKey") && HasValue(dropbox, "appSecret"),
                ["tokenPresent"] = HasValue(dropbox, "refreshToken") || HasValue(dropbox, "accessToken"),
                ["authorizationPending"] = pending
            });

        var text = string.Join(Environment.NewLine, providers.OfType<JsonObject>().Select(p =>
            $"{p["provider"]}: {((bool)p["configured"]! ? "configured" : "not configured")}, {((bool)p["tokenPresent"]! ? "signed in" : "not signed in")}"
            + (p["authorizationPending"] is { } waiting && (bool)waiting ? " (authorization pending: run auth dropbox --code <code>)" : string.Empty)));
        await CliSupport.EmitAsync(ctx, new { providers }, text).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> GDriveAsync(CliContext ctx)
    {
        var source = Source(ctx, "gdrive")
            ?? throw CliSupport.ConfigProblem("The gdrive storage is not configured.", "configure it: restore.exe config set-storage gdrive --option client_id=... --option-stdin client_secret");
        if (!HasValue(source, "client_id") || !HasValue(source, "client_secret"))
        {
            throw CliSupport.ConfigProblem("The gdrive storage needs client_id and client_secret first.", "restore.exe config set-storage gdrive --option client_id=... --option-stdin client_secret");
        }

        var noBrowser = ctx.Parsed.HasOption("--no-browser");
        if (CliRuntime.NonInteractive && !noBrowser)
        {
            throw CliCommandException.NeedsUser(
                "Google Drive sign-in needs a person to approve access in a browser.",
                "run 'restore.exe auth gdrive' in a terminal, or 'restore.exe auth gdrive --no-browser' and give the printed URL to a person on this computer");
        }

        var tokenFolder = TokenFolderOf(source);
        try
        {
            await CliServices.DriveSignIn(
                new DriveSignInRequest(ResolveOption(source, "client_id"), ResolveOption(source, "client_secret"), tokenFolder, noBrowser, message => Console.Error.WriteLine(message)),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw CliCommandException.NeedsUser(ex.Message, "run 'restore.exe auth gdrive' again and approve access in the browser within the time limit");
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or CliCommandException))
        {
            throw new CliCommandException(Program.ExitCodes.Failure, CliErrorCodes.Failure, $"Google Drive sign-in failed: {ex.Message}");
        }

        await CliSupport.EmitAsync(
            ctx,
            new { provider = "gdrive", signedIn = true, tokenFolder, nextSteps = new[] { "restore.exe probe --storage gdrive" } },
            $"Signed in to Google Drive.{Environment.NewLine}Next: restore.exe probe --storage gdrive").ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    /// <summary>One option with %VAR%, <c>env:</c> and <c>dpapi:</c> references resolved (only this one, so unrelated secrets are never decrypted).</summary>
    private static string ResolveOption(StorageConfig source, string option)
    {
        try
        {
            return SecretReferences.Resolve(option, ConfigManager.ExpandConfiguredPath(source.Options[option]), CliServices.SecretProtector);
        }
        catch (StorageAuthRequiredException ex)
        {
            throw CliSupport.ConfigProblem(ex.Message);
        }
    }

    private static string ResolvedAppKey(StorageConfig source)
    {
        if (HasValue(source, "appKey") && ResolveOption(source, "appKey") is { } appKey && !CliSupport.IsPlaceholder(appKey))
        {
            return appKey.Trim();
        }

        throw CliSupport.ConfigProblem(
            "The dropbox storage needs appKey (and appSecret) first.",
            "restore.exe config set-storage dropbox --option appKey=... --option-stdin appSecret");
    }

    private static async Task<int> DropboxAsync(CliContext ctx)
    {
        var source = Source(ctx, "dropbox")
            ?? throw CliSupport.ConfigProblem("The dropbox storage is not configured.", "restore.exe config set-storage dropbox --option appKey=... --option-stdin appSecret");
        var appKey = ResolvedAppKey(source);
        var code = ctx.Parsed.GetOption("--code")?.Trim();

        if (string.IsNullOrEmpty(code))
        {
            var (verifier, challenge) = DropboxOAuth.NewPkcePair();
            Directory.CreateDirectory(AppPaths.StateDirectory);
            await File.WriteAllTextAsync(
                DropboxStatePath,
                new JsonObject { ["appKey"] = appKey, ["codeVerifier"] = verifier, ["createdUtc"] = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture) }.ToJsonString()).ConfigureAwait(false);

            var url = DropboxOAuth.BuildAuthorizeUrl(appKey, challenge);
            const string next = "restore.exe auth dropbox --code <code>";
            await CliSupport.EmitAsync(
                ctx,
                new { authorizeUrl = url, nextStep = next },
                $"Open this URL in a browser, approve access, and copy the code Dropbox shows:{Environment.NewLine}{url}{Environment.NewLine}Then run: {next}").ConfigureAwait(false);
            return Program.ExitCodes.Success;
        }

        if (!File.Exists(DropboxStatePath))
        {
            throw CliSupport.Usage("No Dropbox authorization is pending.", "run 'restore.exe auth dropbox' first to get the URL, then use the code it leads to");
        }

        JsonObject state;
        try
        {
            state = JsonNode.Parse(await File.ReadAllTextAsync(DropboxStatePath).ConfigureAwait(false)) as JsonObject ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw CliSupport.Usage("The pending Dropbox authorization is unreadable.", "start over with 'restore.exe auth dropbox'");
        }

        var verifierText = state["codeVerifier"]?.GetValue<string>();
        if (string.IsNullOrEmpty(verifierText) || !string.Equals(state["appKey"]?.GetValue<string>(), appKey, StringComparison.Ordinal))
        {
            throw CliSupport.Usage("The pending Dropbox authorization belongs to a different app key.", "start over with 'restore.exe auth dropbox'");
        }

        string refreshToken;
        try
        {
            using var http = CliServices.CreateHttpClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            refreshToken = await DropboxOAuth.ExchangeCodeForRefreshTokenAsync(http, appKey, code, verifierText, CancellationToken.None).ConfigureAwait(false);
        }
        catch (DropboxAuthException ex)
        {
            throw new CliCommandException(Program.ExitCodes.Failure, CliErrorCodes.Failure, ex.Message, "start over with 'restore.exe auth dropbox' to get a fresh URL and code");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new CliCommandException(Program.ExitCodes.Failure, CliErrorCodes.Failure, $"Could not reach Dropbox: {ex.Message}");
        }

        var protectSecret = CliServices.SecretProtector.IsAvailable;
        var warnings = new List<string>();
        var stored = protectSecret ? SecretReferences.Protect(refreshToken, CliServices.SecretProtector) : refreshToken;
        if (!protectSecret)
        {
            warnings.Add("The refresh token is stored in plain text in config.json (Windows DPAPI is not available here); keep the file private.");
        }

        var result = await CliSupport.EditConfigAsync(ctx, root =>
            ConfigEditor.Set(root, ["storageSources", "dropbox", "options", "refreshToken"], JsonValue.Create(stored))).ConfigureAwait(false);
        if (!result.Applied)
        {
            return await CliSupport.RejectAsync(ctx, result).ConfigureAwait(false);
        }

        File.Delete(DropboxStatePath);
        warnings.AddRange(result.Warnings);
        if (!HasValue(Source(ctx, "dropbox"), "appSecret"))
        {
            warnings.Add("The dropbox storage also needs appSecret: restore.exe config set-storage dropbox --option-stdin appSecret");
        }

        await CliSupport.EmitAsync(
            ctx,
            new { provider = "dropbox", refreshTokenStored = true, @protected = protectSecret, backup = result.BackupPath, warnings, nextSteps = new[] { "restore.exe probe --storage dropbox" } },
            $"Dropbox is authorised; the refresh token was saved{(protectSecret ? " (DPAPI-protected)" : string.Empty)}."
                + string.Concat(warnings.Select(w => $"{Environment.NewLine}Warning: {w}"))
                + $"{Environment.NewLine}Next: restore.exe probe --storage dropbox").ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }
}
