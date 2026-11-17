using System.Runtime.Versioning;

namespace ReStore.Core.src.utils;

/// <summary>Reads the programs installed on this machine (winget + registry).</summary>
public interface IInstalledProgramSource
{
    Task<IReadOnlyList<InstalledProgram>> GetInstalledProgramsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Exports a registry key (with subkeys) to a <c>.reg</c> file. Returns false ONLY when the key does not exist; any other
/// problem (timeout, access denied, reg.exe failure) throws, so a restore never proceeds on an incomplete undo point.
/// </summary>
public interface IRegistryKeyExporter
{
    Task<bool> ExportKeyAsync(string registryKey, string destinationFile, CancellationToken cancellationToken);
}

/// <summary>Imports validated <c>.reg</c> files.</summary>
public interface IRegistryImporter
{
    Task<RegistryImportResult> ImportAsync(RegistryImportPlan plan, CancellationToken cancellationToken);
}

/// <summary>Installs programs (winget). Programs are never uninstalled by ReStore.</summary>
public interface IProgramInstaller
{
    Task<ProgramInstallSummary> InstallAsync(IReadOnlyList<InstalledProgram> programs, CancellationToken cancellationToken);
}

public sealed record ProgramInstallSummary(int Succeeded, int Failed, string? Error = null);

/// <summary>
/// Everything the system restore planner, undo snapshot and applier need from the machine. The defaults talk to the
/// real registry/winget/reg.exe; tests substitute fakes so nothing real is touched.
/// </summary>
public sealed class SystemRestoreServices
{
    public required IEnvironmentVariableStore Environment { get; init; }
    public required IInstalledProgramSource Programs { get; init; }
    public required IRegistryKeyExporter RegistryExporter { get; init; }
    public required IRegistryImporter RegistryImporter { get; init; }
    public required IProgramInstaller Installer { get; init; }

    /// <summary>Developer-tool files and the VS Code CLI. Optional so existing fakes keep compiling; the devtools component needs it.</summary>
    public IDevToolsMachine? DevTools { get; init; }

    [SupportedOSPlatform("windows")]
    public static SystemRestoreServices CreateDefault(ILogger logger) => new()
    {
        Environment = new DefaultEnvironmentVariableStore(),
        Programs = new DefaultInstalledProgramSource(logger),
        RegistryExporter = new DefaultRegistryKeyExporter(logger),
        RegistryImporter = new DefaultRegistryImporter(logger),
        Installer = new DefaultProgramInstaller(logger),
        DevTools = new DefaultDevToolsMachine(logger)
    };
}

[SupportedOSPlatform("windows")]
internal sealed class DefaultInstalledProgramSource(ILogger logger) : IInstalledProgramSource
{
    public async Task<IReadOnlyList<InstalledProgram>> GetInstalledProgramsAsync(CancellationToken cancellationToken)
        => await new SystemProgramDiscovery(logger).GetAllInstalledProgramsAsync(cancellationToken).ConfigureAwait(false);
}

[SupportedOSPlatform("windows")]
internal sealed class DefaultRegistryKeyExporter(ILogger logger) : IRegistryKeyExporter
{
    public async Task<bool> ExportKeyAsync(string registryKey, string destinationFile, CancellationToken cancellationToken)
    {
        // Existence is checked through the registry API, not by parsing (localised) reg.exe error text, so "absent"
        // and "export failed" cannot be confused.
        if (!KeyExists(registryKey))
        {
            return false;
        }

        var run = await ProcessRunner.RunAsync(
            "reg.exe", ["export", registryKey, destinationFile, "/y"], TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        if (run.TimedOut)
        {
            throw new IOException($"Exporting registry key {registryKey} timed out.");
        }

        if (run.ExitCode != 0 || !File.Exists(destinationFile))
        {
            logger.Log($"reg export of {registryKey} failed: {run.StandardError}", LogLevel.Warning);
            throw new IOException($"Exporting registry key {registryKey} failed (reg.exe exit code {run.ExitCode}).");
        }

        return true;
    }

    private static bool KeyExists(string registryKey)
    {
        var slash = registryKey.IndexOf('\\');
        var hiveName = slash < 0 ? registryKey : registryKey[..slash];
        var subKey = slash < 0 ? "" : registryKey[(slash + 1)..];
        var hive = hiveName.ToUpperInvariant() switch
        {
            "HKEY_CURRENT_USER" => Microsoft.Win32.Registry.CurrentUser,
            "HKEY_LOCAL_MACHINE" => Microsoft.Win32.Registry.LocalMachine,
            _ => throw new InvalidOperationException($"Unsupported registry hive in '{registryKey}'.")
        };

        using var key = hive.OpenSubKey(subKey, writable: false);
        return key != null;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class DefaultRegistryImporter(ILogger logger) : IRegistryImporter
{
    public Task<RegistryImportResult> ImportAsync(RegistryImportPlan plan, CancellationToken cancellationToken)
        => new WindowsSettingsImporter(logger).ImportAsync(plan, cancellationToken);
}

[SupportedOSPlatform("windows")]
internal sealed class DefaultProgramInstaller(ILogger logger) : IProgramInstaller
{
    public async Task<ProgramInstallSummary> InstallAsync(IReadOnlyList<InstalledProgram> programs, CancellationToken cancellationToken)
    {
        var result = await new backup.ProgramRestoreManager(logger)
            .RestoreProgramsAsync([.. programs], wingetOnly: false, dryRun: false, cancellationToken).ConfigureAwait(false);
        return new ProgramInstallSummary(result.SuccessfulInstalls, result.FailedInstalls, string.IsNullOrEmpty(result.ErrorMessage) ? null : result.ErrorMessage);
    }
}
