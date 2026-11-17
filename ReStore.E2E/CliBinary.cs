using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ReStore.E2E;

/// <summary>
/// The CLI under test, located (or built) once per test run.
/// Windows: the published single-file <c>restore.exe</c> with the <c>ReStore.channel</c> marker (channel = portable), exactly as
/// <c>build-msix.ps1</c> (Publish-PortableCli) produces it. Elsewhere: <c>dotnet ReStore.Core.dll</c> from the normal build output.
/// <c>RESTORE_E2E_CLI</c> (path to a restore.exe or ReStore.Core.dll) skips the build/publish step.
/// </summary>
internal sealed record CliBinary(string FileName, string[] PrefixArguments, string Description)
{
    public const string OverrideVariable = "RESTORE_E2E_CLI";

    private static readonly Lazy<CliBinary> Shared = new(Locate, LazyThreadSafetyMode.ExecutionAndPublication);

    public static CliBinary Instance => Shared.Value;

    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>The temp folder of this run (published binary, big data pool); removed at process exit.</summary>
    public static string RunRoot { get; } = CreateRunRoot();

    private static string CreateRunRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "restore-e2e-run-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TestFiles.TryDelete(root);
        return root;
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ReStore.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("ReStore.sln was not found above " + AppContext.BaseDirectory);
    }

    private static CliBinary Locate()
    {
        var overridePath = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return FromPath(overridePath, "from " + OverrideVariable);
        }

        return OperatingSystem.IsWindows() ? PublishPortable() : LocateManagedDll();
    }

    private static CliBinary FromPath(string path, string how) =>
        path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? new CliBinary("dotnet", [path], $"dotnet {path} ({how})")
            : new CliBinary(path, [], $"{path} ({how})");

    /// <summary>The Core project is built before this project (ProjectReference), into the same configuration folder name.</summary>
    private static CliBinary LocateManagedDll()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var dll = Path.Combine(RepositoryRoot, "ReStore.Core", "bin", configuration, "net10.0", "ReStore.Core.dll");
        if (!File.Exists(dll))
        {
            RunDotnet(["build", Path.Combine(RepositoryRoot, "ReStore.Core", "ReStore.Core.csproj"), "-c", configuration, "--nologo", "-v", "q"]);
        }

        return FromPath(dll, "build output");
    }

    private static CliBinary PublishPortable()
    {
        var rid = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        var output = Path.Combine(RunRoot, "cli");

        // The same switches as Publish-PortableCli in build-msix.ps1.
        RunDotnet(
        [
            "publish", Path.Combine(RepositoryRoot, "ReStore.Core", "ReStore.Core.csproj"),
            "-c", "Release", "-r", rid, "--self-contained", "true",
            "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:DebugType=embedded",
            "-o", output, "--nologo", "-v", "q"
        ]);

        var coreExe = Path.Combine(output, "ReStore.Core.exe");
        var restoreExe = Path.Combine(output, "restore.exe");
        File.Move(coreExe, restoreExe, overwrite: true);
        File.WriteAllText(Path.Combine(output, "ReStore.channel"), "portable", new System.Text.UTF8Encoding(false));
        return new CliBinary(restoreExe, [], $"{restoreExe} (published {rid}, channel marker 'portable')");
    }

    private static void RunDotnet(IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet {string.Join(' ', arguments)} failed ({process.ExitCode}):\n{stdout.Result}\n{stderr.Result}");
        }
    }
}
