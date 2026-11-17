using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ReStore.E2E;

/// <summary>
/// One isolated world for a test: its own RESTORE_HOME (config, state, logs), a local storage folder, a source folder and a
/// restore target. The child's TEMP points inside it too, so the default <c>%TEMP%</c> exclusion rule never swallows the source folder
/// and nothing touches the real user profile.
/// </summary>
internal sealed class Workspace : IDisposable
{
    public const string TestPassword = "e2e correct horse battery staple";

    private Workspace(string root)
    {
        Root = root;
        Home = Path.Combine(root, "home");
        Tmp = Path.Combine(root, "tmp");
        Storage = Path.Combine(root, "storage");
        Src = Path.Combine(root, "src");
        Out = Path.Combine(root, "out");
        foreach (var directory in new[] { Home, Tmp, Storage, Src })
        {
            Directory.CreateDirectory(directory);
        }
    }

    public string Root { get; }

    public string Home { get; }

    public string Tmp { get; }

    public string Storage { get; }

    public string Src { get; }

    public string Out { get; }

    public static Workspace Create([CallerMemberName] string name = "")
    {
        var safe = new string(name.Where(char.IsLetterOrDigit).ToArray());
        var root = Path.Combine(Path.GetTempPath(), "restore-e2e", (safe.Length > 24 ? safe[..24] : safe) + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return new Workspace(root);
    }

    public void Dispose() => TestFiles.TryDelete(Root);

    // ---- process control -----------------------------------------------------------------------------------------------

    public Dictionary<string, string> BuildEnvironment(IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
        }

        // Nothing from the developer's or CI's environment may change behaviour.
        foreach (var name in new[]
        {
            "RESTORE_ENCRYPTION_PASSWORD", "RESTORE_ENCRYPTION_PASSWORD_FILE", "RESTORE_NEW_ENCRYPTION_PASSWORD", "RESTORE_NON_INTERACTIVE",
            "RESTORE_AGENT", "RESTORE_TELEMETRY_ENDPOINT", "RESTORE_TELEMETRY_DEV", "CI", "GITHUB_ACTIONS", "TF_BUILD"
        })
        {
            environment.Remove(name);
        }

        environment["RESTORE_HOME"] = Home;
        environment["TMPDIR"] = Tmp;
        environment["TEMP"] = Tmp;
        environment["TMP"] = Tmp;

        // The default excludedPaths (%TEMP%, %LOCALAPPDATA%\Temp, %USERPROFILE%\Downloads, ...) are expanded by the child from these, so a workspace
        // that lives in the real temp folder is never "excluded" and the real profile is never read.
        var profile = Path.Combine(Root, "profile");
        environment["USERPROFILE"] = profile;
        environment["LOCALAPPDATA"] = Path.Combine(profile, "AppData", "Local");
        environment["APPDATA"] = Path.Combine(profile, "AppData", "Roaming");
        Directory.CreateDirectory(environment["LOCALAPPDATA"]);
        Directory.CreateDirectory(environment["APPDATA"]);
        environment["RESTORE_TELEMETRY_OPTOUT"] = "1";
        environment["DO_NOT_TRACK"] = "1";
        environment["RESTORE_NO_UPDATE_CHECK"] = "1";

        // Nothing here may reach the network: an accidental update check goes to a closed local port.
        environment["RESTORE_UPDATE_MANIFEST_URL"] = "http://127.0.0.1:9/latest.json";
        environment["DOTNET_NOLOGO"] = "1";
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        // A shared extraction folder keeps the single-file binary from unpacking its native libraries for every process.
        environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(CliBinary.RunRoot, "extract");

        if (overrides != null)
        {
            foreach (var (key, value) in overrides)
            {
                if (value == null)
                {
                    environment.Remove(key);
                }
                else
                {
                    environment[key] = value;
                }
            }
        }

        return environment;
    }

    public RunningCli Start(RunOptions? options, params string[] arguments)
    {
        options ??= new RunOptions();
        var binary = CliBinary.Instance;
        var all = binary.PrefixArguments.Concat(arguments).ToArray();
        var environment = BuildEnvironment(options.Environment);
        var commandLine = binary.FileName + " " + string.Join(' ', arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        ChildProcess child;
        if (options.NewProcessGroup && OperatingSystem.IsWindows())
        {
            if (options.Stdin != null || options.KeepStdinOpen)
            {
                throw new NotSupportedException("A child in its own process group gets an empty closed stdin.");
            }

            child = NativeChild.Start(binary.FileName, all, environment, Root);
        }
        else
        {
            child = ManagedChild.Start(binary.FileName, all, environment, Root, options.Stdin, options.KeepStdinOpen);
        }

        return new RunningCli(child, commandLine, options.Timeout);
    }

    public RunningCli Start(params string[] arguments) => Start(null, arguments);

    public CliResult Run(RunOptions? options, params string[] arguments)
    {
        using var running = Start(options, arguments);
        return running.WaitForExit();
    }

    /// <summary>Runs with stdin closed and no flags added.</summary>
    public CliResult Run(params string[] arguments) => Run(null, arguments);

    /// <summary>Runs the way an agent should: <c>--json --non-interactive</c> appended.</summary>
    public CliResult RunJson(params string[] arguments) => Run(null, [.. arguments, "--json", "--non-interactive"]);

    public CliResult RunJson(RunOptions options, params string[] arguments) => Run(options, [.. arguments, "--json", "--non-interactive"]);

    /// <summary>Runs with the encryption password on stdin.</summary>
    public CliResult RunJsonWithPassword(params string[] arguments) =>
        Run(new RunOptions { Stdin = TestPassword + "\n" }, [.. arguments, "--password-stdin", "--json", "--non-interactive"]);

    // ---- common set-up and queries -------------------------------------------------------------------------------------

    /// <summary>init with local storage in <see cref="Storage"/> watching <see cref="Src"/>.</summary>
    public JsonElement Init(string? watch = null)
    {
        Directory.CreateDirectory(Src);
        return RunJson("init", "--storage", "local", "--path", Storage, "--watch", watch ?? Src, "--probe").Ok();
    }

    public JsonElement Backup(string? source = null, bool encrypted = false)
    {
        var result = encrypted ? RunJsonWithPassword("backup", source ?? Src) : RunJson("backup", source ?? Src);
        return result.Ok();
    }

    public void EnableEncryption() =>
        Run(new RunOptions { Stdin = TestPassword + "\n" }, "encryption", "enable", "--password-stdin", "--json", "--non-interactive").Ok();

    /// <summary>The <c>snapshots/&lt;group&gt;</c> folder name of the backed-up folder.</summary>
    public string GroupKey(string? source = null, bool encrypted = false)
    {
        var json = (encrypted ? RunJsonWithPassword("snapshots") : RunJson("snapshots")).Ok();
        var wanted = Normalize(source ?? Src);
        foreach (var group in json.GetProperty("groups").EnumerateArray())
        {
            if (Normalize(group.GetProperty("originalPath").GetString() ?? string.Empty) == wanted)
            {
                return group.GetProperty("key").GetString()!;
            }
        }

        throw new Xunit.Sdk.XunitException($"No snapshot group for '{wanted}' in: {json}");
    }

    public string HeadPath(string? source = null, bool encrypted = false) => $"snapshots/{GroupKey(source, encrypted)}/HEAD";

    /// <summary>The newest snapshot's manifest path as <c>snapshots --json</c> lists it.</summary>
    public string LatestManifestPath(string? source = null, bool encrypted = false)
    {
        var json = (encrypted ? RunJsonWithPassword("snapshots") : RunJson("snapshots")).Ok();
        var wanted = Normalize(source ?? Src);
        foreach (var group in json.GetProperty("groups").EnumerateArray())
        {
            if (Normalize(group.GetProperty("originalPath").GetString() ?? string.Empty) == wanted)
            {
                return group.GetProperty("snapshots").EnumerateArray().First(s => s.GetProperty("head").GetBoolean()).GetProperty("manifestPath").GetString()!;
            }
        }

        throw new Xunit.Sdk.XunitException("No snapshot group for " + wanted);
    }

    public string StoragePath(string relative) => Path.Combine(Storage, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string Normalize(string path) => path.TrimEnd('/', '\\').Replace('\\', '/');

    /// <summary>The raw bytes of the HEAD pointer of a group, as stored in the local storage.</summary>
    public string ReadHeadFile(string groupKey) => File.ReadAllText(StoragePath($"snapshots/{groupKey}/HEAD"));

    public static string LockFile(Workspace workspace) => Path.Combine(workspace.Home, "state", "repository.lock");

    /// <summary>Holds the cross-process repository lock exactly as another ReStore process does (exclusive, FileShare.None).</summary>
    public FileStream HoldRepositoryLock()
    {
        Directory.CreateDirectory(Path.Combine(Home, "state"));
        return new FileStream(LockFile(this), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
    }

    public static Stopwatch StartClock() => Stopwatch.StartNew();
}
