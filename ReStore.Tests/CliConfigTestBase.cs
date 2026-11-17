using System.Text;
using System.Text.Json.Nodes;
using ReStore.Core;
using ReStore.Core.src.cli;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>A protector that needs no Windows: reverses the bytes (and can pretend to be unavailable).</summary>
internal sealed class FakeSecretProtector(bool available = true) : ISecretProtector
{
    public bool IsAvailable => available;

    public byte[] Protect(byte[] plaintext) => [.. plaintext.Reverse()];

    public byte[] Unprotect(byte[] protectedData) => [.. protectedData.Reverse()];
}

/// <summary>
/// Shared setup of the in-process CLI tests of the setup/configuration commands: a temp data root (never the real profile), a clean
/// environment, a fake secret protector and no network. Every command runs with <c>--non-interactive</c>.
/// </summary>
[Collection("GlobalState")]
public abstract class CliConfigTestBase : IDisposable
{
    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);

    private static readonly string[] ManagedVariables =
    [
        "RESTORE_ENCRYPTION_PASSWORD", "RESTORE_ENCRYPTION_PASSWORD_FILE", "RESTORE_NEW_ENCRYPTION_PASSWORD", "RESTORE_NON_INTERACTIVE", "CI", "CFGTEST_VALUE", "USERPROFILE"
    ];

    private readonly string _previousRoot = AppPaths.Root;
    private readonly Dictionary<string, string?> _previousVariables = [];

    protected string Root { get; } = Path.Combine(Path.GetTempPath(), "ReStoreCliConfig_" + Guid.NewGuid().ToString("N"));

    protected string StorageDirectory => Path.Combine(Root, "storage");

    protected string WatchedDirectory => Path.Combine(Root, "watched");

    protected CliConfigTestBase()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(WatchedDirectory);
        AppPaths.OverrideRoot(Root);
        foreach (var name in ManagedVariables)
        {
            _previousVariables[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        // The example config's %USERPROFILE% folders (the default local storage) then resolve inside the temp root, never the real profile.
        Environment.SetEnvironmentVariable("USERPROFILE", Root);

        CliServices.Reset();
        CliServices.SecretProtector = new FakeSecretProtector();
        CliServices.HttpHandler = new RefuseAllHandler();
        CliServices.DriveSignIn = (_, _) => throw new InvalidOperationException("A test tried a real Google sign-in.");
    }

    public void Dispose()
    {
        CliServices.Reset();
        AppPaths.OverrideRoot(_previousRoot);
        foreach (var (name, value) in _previousVariables)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try { Directory.Delete(Root, true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Any HTTP request a test did not expect fails the test instead of reaching the network.</summary>
    protected sealed class RefuseAllHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"A test tried a real HTTP request to {request.RequestUri}.");
    }

    /// <summary>Answers every request with the given function and records the requests.</summary>
    protected sealed class FakeHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri? Uri, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri, body));
            return respond(request, body);
        }
    }

    protected static async Task<(int ExitCode, string Stdout, string Stderr)> RunWithStdinAsync(string? stdin, params string[] args)
    {
        await ConsoleLock.WaitAsync();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalIn = Console.In;
        try
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            using var outWriter = new StringWriter(stdout);
            using var errWriter = new StringWriter(stderr);
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            Console.SetIn(new StringReader(stdin ?? string.Empty));

            var exitCode = await Program.Main([.. args, "--non-interactive"]);
            outWriter.Flush();
            errWriter.Flush();
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Console.SetIn(originalIn);
            ConsoleLock.Release();
        }
    }

    protected static Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args) => RunWithStdinAsync(null, args);

    protected static JsonObject Json(string text) => JsonNode.Parse(text)!.AsObject();

    protected JsonObject ReadConfig() => Json(File.ReadAllText(AppPaths.ConfigFile));

    /// <summary>A valid config with local storage in the temp root; <paramref name="customize"/> may change the document.</summary>
    protected void WriteConfig(Action<JsonObject>? customize = null)
    {
        ConfigInitializer.EnsureConfigurationSetup();
        var root = ReadConfig();
        root["watchDirectories"] = new JsonArray(new JsonObject { ["path"] = WatchedDirectory, ["storageType"] = null });
        root["globalStorageType"] = "local";
        root["storageSources"] = new JsonObject { ["local"] = new JsonObject { ["path"] = StorageDirectory, ["options"] = new JsonObject() } };
        root["excludedPaths"] = new JsonArray();
        root["encryption"] = new JsonObject { ["enabled"] = false, ["salt"] = null, ["keyDerivationIterations"] = 1000, ["verificationToken"] = null };
        customize?.Invoke(root);
        File.WriteAllText(AppPaths.ConfigFile, root.ToJsonString());
    }

    /// <summary>The config as <c>init</c> finds it on a fresh machine: copied from the shipped example, untouched.</summary>
    protected static void WritePristineConfig() => ConfigInitializer.EnsureConfigurationSetup();

    protected static string[] BackupFiles() =>
        Directory.Exists(Path.Combine(AppPaths.Root, "backups"))
            ? Directory.GetFiles(Path.Combine(AppPaths.Root, "backups"), "config.pre-change.*.json")
            : [];
}
