using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReStore.Core.src.telemetry;
using ReStore.Core.src.utils;
using Xunit.Abstractions;

namespace ReStore.Tests;

/// <summary>
/// Secret canary sweep: unmistakable values are planted in every secret-bearing provider option, in env:/%VAR% references, in the
/// encryption password (stdin) and in the process environment; then every offline CLI command runs in-process (plain, --json, and with
/// debug logging) against local and against unreachable storages, and crash reports are built from exceptions that quote the canaries.
/// Afterwards no canary may appear in any output, log, state, queue, crash report or diagnostics text; the config file itself (and its
/// pre-change copies) is the one place a literal secret legitimately lives.
/// </summary>
[Collection("GlobalState")]
public class SecretCanaryTests(ITestOutputHelper output) : CliConfigTestBase, IDisposable
{
    private const string Password = "CANARY-ENCRYPTION-PASSWORD-91x";
    private const string WrongPassword = "CANARY-WRONG-PASSWORD-44q";
    private const string EnvSecretValue = "CANARY-ENV-REFERENCE-VALUE-17";
    private const string PercentSecretValue = "CANARY-PERCENT-REFERENCE-VALUE-23";
    private const string ProcessEnvironmentValue = "CANARY-PROCESS-ENVIRONMENT-VALUE-58";
    private const string AzureKey = "CANARYAZUREKEY12";
    private const string AzureSignature = "CANARYSASSIG77";

    /// <summary>Every canary that must never leak. Literal secrets planted in config.json are listed by their value.</summary>
    private static readonly string[] Canaries =
    [
        Password, WrongPassword, EnvSecretValue, PercentSecretValue, ProcessEnvironmentValue, AzureKey, AzureSignature,
        "CANARY-s3-secretAccessKey-Zq9", "CANARY-s3-sessionToken-Zq9", "CANARY-github-token-Zq9", "CANARY-gdrive-client_secret-Zq9",
        "CANARY-dropbox-accessToken-Zq9", "CANARY-dropbox-refreshToken-Zq9", "CANARY-dropbox-appSecret-Zq9", "CANARY-dropbox-appKey-Zq9",
        "CANARY-sftp-passphrase-Zq9", "CANARY-gcp-token-Zq9", "ghp_CANARYGITHUBTOKEN0123456789abcdefghij"
    ];

    private static readonly string[] ProcessVariables = ["CANARY_ENV_SECRET", "CANARY_PCT_SECRET", "CANARY_PROCESS_VARIABLE"];

    private readonly StringBuilder _allStdout = new();
    private readonly StringBuilder _allStderr = new();
    private readonly Dictionary<string, string?> _previousEnvironment = ProcessVariables.ToDictionary(n => n, Environment.GetEnvironmentVariable);

    private string ConfigCopyPrefix => Path.Combine(AppPaths.Root, "backups") + Path.DirectorySeparatorChar;

    private void ConfigureCanaries(bool encryption = false, string logLevel = "Debug")
    {
        Environment.SetEnvironmentVariable("CANARY_ENV_SECRET", EnvSecretValue);
        Environment.SetEnvironmentVariable("CANARY_PCT_SECRET", PercentSecretValue);
        Environment.SetEnvironmentVariable("CANARY_PROCESS_VARIABLE", ProcessEnvironmentValue);
        WriteConfig(root =>
        {
            root["logging"] = new JsonObject { ["level"] = logLevel };
            root["telemetry"] = new JsonObject { ["enabled"] = true, ["crashReports"] = "always" };
            if (encryption)
            {
                root["encryption"] = new JsonObject
                {
                    ["enabled"] = true, ["salt"] = Convert.ToBase64String(EncryptionService.GenerateSalt()), ["keyDerivationIterations"] = 1000, ["verificationToken"] = null
                };
            }

            var sources = root["storageSources"]!.AsObject();
            sources["s3"] = Source(("accessKeyId", "AKIACANARY0ACCESSKEY"), ("secretAccessKey", "CANARY-s3-secretAccessKey-Zq9"), ("sessionToken", "CANARY-s3-sessionToken-Zq9"), ("region", "us-east-1"), ("bucketName", "canary-bucket-name"));
            sources["github"] = Source(("token", "ghp_CANARYGITHUBTOKEN0123456789abcdefghij"), ("repo", "canary-repo"), ("owner", "canary-owner"));
            sources["gdrive"] = Source(("client_id", "canary-client-id"), ("client_secret", "CANARY-gdrive-client_secret-Zq9"));
            sources["dropbox"] = Source(("accessToken", "CANARY-dropbox-accessToken-Zq9"), ("refreshToken", "CANARY-dropbox-refreshToken-Zq9"), ("appKey", "CANARY-dropbox-appKey-Zq9"), ("appSecret", "CANARY-dropbox-appSecret-Zq9"));
            sources["sftp"] = Source(("host", "127.0.0.1"), ("port", "1"), ("username", "canary-user"), ("password", "env:CANARY_ENV_SECRET"), ("passphrase", "CANARY-sftp-passphrase-Zq9"));
            sources["b2"] = Source(("keyId", "CANARYKEYID"), ("applicationKey", "%CANARY_PCT_SECRET%"), ("serviceUrl", "http://127.0.0.1:1"), ("bucketName", "canary-b2-bucket"));
            sources["azure"] = Source(("connectionString", $"DefaultEndpointsProtocol=http;AccountName=canaryacct;AccountKey={AzureKey};BlobEndpoint=http://127.0.0.1:1/canaryacct;SharedAccessSignature=sv=2020&sig={AzureSignature}"), ("containerName", "canary-container"));
            sources["gcp"] = Source(("bucketName", "canary-gcp-bucket"), ("credentialPath", Path.Combine(Root, "missing-credentials.json")), ("token", "CANARY-gcp-token-Zq9"));
        });
    }

    private static JsonObject Source(params (string Name, string Value)[] options)
    {
        var bag = new JsonObject();
        foreach (var (name, value) in options)
        {
            bag[name] = value;
        }

        return new JsonObject { ["path"] = null, ["options"] = bag };
    }

    private async Task<(int Exit, string Stdout, string Stderr)> RunCliAsync(string? stdin, params string[] args)
    {
        var result = await RunWithStdinAsync(stdin, args);
        _allStdout.AppendLine(result.Stdout);
        _allStderr.AppendLine(result.Stderr);
        return result;
    }

    /// <summary>The set of files a sweep reads: everything under the data root except the config file and its pre-change copies.</summary>
    private IEnumerable<string> SweepFiles() =>
        Directory.EnumerateFiles(AppPaths.Root, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, AppPaths.ConfigFile, StringComparison.Ordinal))
            .Where(path => !(path.StartsWith(ConfigCopyPrefix, StringComparison.Ordinal) && Path.GetFileName(path).StartsWith("config.", StringComparison.Ordinal)));

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    private static bool Contains(string haystack, string canary) =>
        haystack.Contains(canary, StringComparison.Ordinal)
        || haystack.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(canary)), StringComparison.Ordinal)
        || haystack.Contains(Uri.EscapeDataString(canary), StringComparison.Ordinal)
        || haystack.Contains(Convert.ToHexString(Encoding.UTF8.GetBytes(canary)), StringComparison.OrdinalIgnoreCase);

    private void AssertNoCanaryAnywhere(params string[] extraTexts)
    {
        var leaks = new List<string>();
        void Check(string where, string text)
        {
            foreach (var canary in Canaries.Where(c => Contains(text, c)))
            {
                leaks.Add($"{canary} in {where}");
            }
        }

        Check("stdout", _allStdout.ToString());
        Check("stderr", _allStderr.ToString());
        foreach (var path in SweepFiles())
        {
            Check(Path.GetRelativePath(AppPaths.Root, path), ReadShared(path));
            // Binary state (DPAPI-style blobs) is searched as text above; also search the raw bytes for the UTF-16 form.
            Check(Path.GetRelativePath(AppPaths.Root, path) + " (utf-16)", Encoding.Unicode.GetString(File.ReadAllBytes(path)));
        }

        foreach (var (text, index) in extraTexts.Select((t, i) => (t, i)))
        {
            Check($"extra text #{index}", text);
        }

        leaks.Should().BeEmpty("a secret must never appear in output, logs, state, queues, crash reports or diagnostics");
    }

    // ---- the sweeps ----------------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> OfflineCommands() =>
    [
        [new[] { "config", "show" }], [new[] { "config", "show", "--json" }],
        [new[] { "config", "get" }], [new[] { "config", "get", "--json" }],
        [new[] { "config", "get", "storageSources" }], [new[] { "config", "get", "storageSources.s3.options", "--json" }],
        [new[] { "config", "get", "storageSources.sftp.options.passphrase" }],
        [new[] { "config", "path" }], [new[] { "config", "validate" }], [new[] { "config", "validate", "--json" }],
        [new[] { "config", "schema", "--json" }], [new[] { "config", "list-watch", "--json" }],
        [new[] { "diagnostics" }], [new[] { "diagnostics", "--json" }], [new[] { "logs" }], [new[] { "logs", "--json" }], [new[] { "logs", "--tail", "500" }],
        [new[] { "doctor" }], [new[] { "doctor", "--json" }], [new[] { "status" }], [new[] { "status", "--json" }],
        [new[] { "usage", "--json" }], [new[] { "activity", "--json" }], [new[] { "encryption", "status", "--json" }], [new[] { "auth", "status", "--json" }],
        [new[] { "telemetry", "status", "--json" }], [new[] { "telemetry", "preview", "--json" }], [new[] { "telemetry", "crash-reports", "--json" }],
        [new[] { "schedule", "status", "--json" }], [new[] { "update", "check", "--json" }], [new[] { "version", "--json" }], [new[] { "commands", "--json" }],
        [new[] { "snapshots", "--json" }], [new[] { "retention", "--dry-run", "--json" }], [new[] { "repair", "--dry-run", "--json" }],
        [new[] { "system-undo", "--list", "--json" }], [new[] { "pause", "--json" }], [new[] { "resume", "--json" }]
    ];

    [Theory]
    [MemberData(nameof(OfflineCommands))]
    public async Task OfflineCommand_NeverPrintsOrStoresASecret(string[] command)
    {
        ConfigureCanaries();

        // Twice: the second run sees the log, state and queue files the first one created.
        var first = await RunCliAsync(null, command);
        await RunCliAsync(null, command);

        first.Exit.Should().NotBe(2, "the command line must be valid, or this sweep proves nothing: " + first.Stdout + first.Stderr);
        AssertNoCanaryAnywhere();
    }

    [Theory]
    [InlineData("logs/restore.log")]
    [InlineData("state/anything.json")]
    [InlineData("client-state/telemetry-queue.jsonl")]
    public void TheSweepItself_DetectsALeakInAnyFileUnderTheDataRoot(string relative)
    {
        ConfigureCanaries();
        var path = Path.Combine(AppPaths.Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "line\nsecretAccessKey=" + Canaries[8] + "\n");

        var act = () => AssertNoCanaryAnywhere();

        act.Should().Throw<Exception>().WithMessage("*" + Canaries[8] + "*");
    }

    [Fact]
    public void TheSweepItself_DetectsEncodedLeaksInOutput()
    {
        _allStdout.AppendLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(Password)));
        var act = () => AssertNoCanaryAnywhere();
        act.Should().Throw<Exception>().WithMessage("*" + Password + "*");
    }

    [Fact]
    public async Task ConfigExport_RemovesEveryLiteralSecret()
    {
        ConfigureCanaries();
        var target = Path.Combine(Root, "exported.json");

        var (exit, _, _) = await RunCliAsync(null, "config", "export", target);

        exit.Should().Be(0);
        var exported = File.ReadAllText(target);
        Canaries.Where(c => Contains(exported, c)).Should().BeEmpty("config export writes a shareable copy without secrets");
        exported.Should().Contain("env:CANARY_ENV_SECRET", "env: references are kept");
    }

    [Fact]
    public async Task ConfigShowAndGet_MaskLiteralSecrets_AndKeepReferences()
    {
        ConfigureCanaries();

        var (_, shown, _) = await RunCliAsync(null, "config", "show", "--json");
        var (_, secrets, _) = await RunCliAsync(null, "config", "get", "storageSources", "--json");

        shown.Should().Contain("***");
        secrets.Should().Contain("env:CANARY_ENV_SECRET").And.Contain("%CANARY_PCT_SECRET%").And.NotContain("CANARY-s3-secretAccessKey");
        AssertNoCanaryAnywhere();
    }

    public static IEnumerable<object[]> FailingStorages() => [["sftp"], ["b2"], ["azure"], ["gcp"]];

    [Theory]
    [MemberData(nameof(FailingStorages))]
    public async Task FailingStorage_ErrorsAndLogs_DoNotQuoteSecrets(string storage)
    {
        ConfigureCanaries();
        var stopwatch = Stopwatch.StartNew();
        string[][] commands =
        [
            ["probe", "--storage", storage, "--json"],
            ["probe", "--storage", storage],
            ["snapshots", "--storage", storage, "--json"],
            ["snapshots", "--storage", storage],
            ["verify", "snapshots/x/HEAD", "--storage", storage, "--json"],
            ["restore", "snapshots/x/HEAD", Path.Combine(Root, "restored"), "--storage", storage, "--json"],
            ["ls", "snapshots/x/HEAD", "--storage", storage, "--json"],
            ["adopt", "--storage", storage, "--json"]
        ];
        foreach (var command in commands)
        {
            var (exit, _, _) = await RunCliAsync(null, command);
            exit.Should().NotBe(0, $"{string.Join(' ', command)} cannot succeed against an unreachable {storage}");
        }

        output.WriteLine($"{storage}: {stopwatch.ElapsedMilliseconds} ms");
        AssertNoCanaryAnywhere();
    }

    [Fact]
    public async Task BackupRestoreVerify_WithAnEncryptedRepository_NeverLeakThePassword()
    {
        ConfigureCanaries(encryption: true);
        await File.WriteAllTextAsync(Path.Combine(WatchedDirectory, "a.txt"), "hello canary");

        var (backupExit, backupOut, _) = await RunCliAsync(Password + "\n", "backup", WatchedDirectory, "--password-stdin", "--json");
        backupExit.Should().Be(0, backupOut);
        var (listExit, listOut, _) = await RunCliAsync(Password + "\n", "snapshots", "--password-stdin", "--json");
        listExit.Should().Be(0, listOut);
        var head = JsonNode.Parse(listOut)!["groups"]!.AsArray().SelectMany(g => g!["snapshots"]!.AsArray()).Select(s => s!["manifestPath"]!.GetValue<string>()).First();

        string[][] withPassword =
        [
            ["verify", head, "--password-stdin", "--json"],
            ["restore", head, Path.Combine(Root, "restored-1"), "--password-stdin", "--json"],
            ["restore", head, Path.Combine(Root, "restored-1"), "--password-stdin", "--conflict", "overwrite", "--yes", "--json"],
            ["ls", head, "--password-stdin", "--json"],
            ["test-restore", head, "--password-stdin", "--json"],
            ["unlock", "--remember", "--password-stdin", "--json"]
        ];
        foreach (var command in withPassword)
        {
            var (exit, stdout, _) = await RunCliAsync(Password + "\n", command);
            exit.Should().Be(0, $"{string.Join(' ', command)}: {stdout}");
        }

        File.ReadAllText(Path.Combine(Root, "restored-1", "a.txt")).Should().Be("hello canary");
        SweepFiles().Should().Contain(path => path.EndsWith("restore.log", StringComparison.Ordinal), "the log file is part of the sweep");

        // A wrong password is an error path that must not echo what was typed.
        foreach (var command in new[] { new[] { "verify", head, "--password-stdin", "--json" }, new[] { "restore", head, Path.Combine(Root, "restored-2"), "--password-stdin", "--json" }, new[] { "unlock", "--password-stdin", "--json" } })
        {
            var (exit, _, _) = await RunCliAsync(WrongPassword + "\n", command);
            exit.Should().NotBe(0);
        }

        await RunCliAsync(null, "config", "show", "--json");
        await RunCliAsync(null, "diagnostics");
        AssertNoCanaryAnywhere();
    }

    [Fact]
    public async Task EnvironmentPassword_NeverLeaks()
    {
        ConfigureCanaries(encryption: true);
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", Password);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(WatchedDirectory, "a.txt"), "hello canary");
            var (exit, stdout, _) = await RunCliAsync(null, "backup", WatchedDirectory, "--json");
            exit.Should().Be(0, stdout);
            await RunCliAsync(null, "doctor", "--json");
            await RunCliAsync(null, "diagnostics");
        }
        finally
        {
            Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", null);
        }

        AssertNoCanaryAnywhere();
    }

    [Fact]
    public async Task SystemBackup_DoesNotCaptureTheProcessEnvironmentIntoOutputs()
    {
        ConfigureCanaries();

        // Windows-only features report "not supported" elsewhere; either way nothing may echo the process environment.
        await RunCliAsync(null, "system-backup", "--json");
        await RunCliAsync(null, "system-backup", "--dry-run", "--json");
        await RunCliAsync(null, "system-restore", "--plan", "--json");

        AssertNoCanaryAnywhere();
    }

    // ---- crash reports and telemetry --------------------------------------------------------------------------------

    private TelemetryClient NewClient(Func<string, string?>? env = null) => new(new TelemetryClientOptions
    {
        Surface = "cli",
        Channel = AppChannel.Msix,
        GetEnv = env ?? Environment.GetEnvironmentVariable,
        GetEnvNames = () => ProcessVariables,
        IsTestRootOverride = () => false,
        StateDirectory = () => Path.Combine(AppPaths.Root, "client-state"),
        ConfigFile = () => AppPaths.ConfigFile,
        ConfigEnabled = () => true,
        ConfigCrashReports = () => CrashReportMode.Always,
        UtcNow = () => new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
        AppVersion = "9.9.9",
        Interactive = false
    });

    private static Exception Thrown(string message, Exception? inner = null)
    {
        try
        {
            throw inner == null ? new InvalidOperationException(message) : new InvalidOperationException(message, inner);
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public void CrashReports_FromExceptionsQuotingSecrets_ContainNoCanary()
    {
        ConfigureCanaries();
        var messages = new[]
        {
            $"Authentication failed: secretAccessKey=CANARY-s3-secretAccessKey-Zq9 sessionToken=CANARY-s3-sessionToken-Zq9",
            $"HTTP 401 for https://api.github.com/repos/canary-owner/canary-repo with token ghp_CANARYGITHUBTOKEN0123456789abcdefghij",
            $"The connection string DefaultEndpointsProtocol=https;AccountName=canaryacct;AccountKey={AzureKey}; is invalid (sig={AzureSignature})",
            $"Bearer CANARY-dropbox-accessToken-Zq9 was rejected; refreshToken=CANARY-dropbox-refreshToken-Zq9",
            $"sftp password for canary-user@127.0.0.1 rejected: {EnvSecretValue} / {PercentSecretValue}",
            $"Decryption failed for password '{Password}' and '{WrongPassword}'",
            $"Environment CANARY_PROCESS_VARIABLE={ProcessEnvironmentValue} was captured",
            $"Passphrase \"CANARY-sftp-passphrase-Zq9\" for key C:\\Users\\canary-user\\.ssh\\id_rsa"
        };
        var reports = new List<string>();
        foreach (var message in messages)
        {
            var client = NewClient();
            var outcome = client.RecordCrash(Thrown(message, Thrown(message)));
            outcome.Id.Should().NotBeNull();
            reports.Add(client.CrashStore.Read(outcome.Id!) ?? "");
        }

        var builtDirectly = CrashReportBuilder.Build(Thrown(string.Join(" ", messages)), new CrashReportContext { SensitiveTerms = CrashScrubber.CollectConfigTerms(AppPaths.ConfigFile) }).ToJson();

        AssertNoCanaryAnywhere([.. reports, builtDirectly]);
        // The sweep above also covers the queue and the store under the data root.
        Directory.EnumerateFiles(Path.Combine(AppPaths.Root, "client-state"), "*", SearchOption.AllDirectories).Should().NotBeEmpty();
    }

    [Fact]
    public async Task ARealUnhandledFailure_NeverLeaksThroughTheCrashPathOrTheLog()
    {
        ConfigureCanaries();
        // A config the loader rejects, carrying canaries in values that end up in exception messages.
        File.WriteAllText(AppPaths.ConfigFile, "{ \"storageSources\": { \"sftp\": { \"options\": { \"password\": \"" + Password + "\" } } }, \"backupInterval\": \"daily\" }");

        var (exit, _, _) = await RunCliAsync(null, "status", "--json");
        await RunCliAsync(null, "backup", WatchedDirectory, "--json");
        await RunCliAsync(null, "config", "validate", "--json");
        await RunCliAsync(null, "diagnostics");

        exit.Should().NotBe(0);
        AssertNoCanaryAnywhere();
    }

    // Re-implemented so the planted environment variables are removed before the base class cleans up the data root.
    void IDisposable.Dispose()
    {
        foreach (var (name, value) in _previousEnvironment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", null);
        base.Dispose();
    }
}
