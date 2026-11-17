using System.Text.Json;
using ReStore.Core.src.utils;
using System.Runtime.Versioning;

namespace ReStore.Core.src.backup;

[SupportedOSPlatform("windows")]
public class ProgramRestoreManager(ILogger logger)
{
    private readonly ILogger _logger = logger;

    /// <summary>Upper bound for a single <c>winget install</c>.</summary>
    public TimeSpan InstallTimeout { get; set; } = TimeSpan.FromMinutes(30);

    private sealed class InstalledProgramsExportFile
    {
        public List<InstalledProgram>? Programs { get; set; }
    }

    public async Task<ProgramRestoreResult> RestoreProgramsFromJsonAsync(string jsonPath, bool wingetOnly = false, bool dryRun = false, CancellationToken cancellationToken = default)
    {
        _logger.Log($"Starting program restore from {jsonPath} (WingetOnly: {wingetOnly}, DryRun: {dryRun})", LogLevel.Info);
        
        var result = new ProgramRestoreResult();
        
        try
        {
            var json = await File.ReadAllTextAsync(jsonPath).ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<InstalledProgramsExportFile>(json, JsonDefaults.SystemBackup);

            var programs = data?.Programs;
            if (programs == null)
            {
                return result;
            }

            if (programs.Count > 0 &&
                programs.All(p => string.IsNullOrWhiteSpace(p.Name) && string.IsNullOrWhiteSpace(p.WingetId)))
            {
                throw new InvalidDataException(
                    $"'{jsonPath}' contains {programs.Count} program entries but none has a name or winget id; the file is malformed.");
            }

            result = await RestoreProgramsAsync(programs, wingetOnly, dryRun, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Log($"Error restoring programs: {ex.Message}", LogLevel.Error);
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }
        
        return result;
    }

    public async Task<ProgramRestoreResult> RestoreProgramsAsync(List<InstalledProgram> programs, bool wingetOnly = false, bool dryRun = false, CancellationToken cancellationToken = default)
    {
        var result = new ProgramRestoreResult();
        
        var wingetAvailable = await IsWingetAvailableAsync(cancellationToken).ConfigureAwait(false);
        if (!wingetAvailable)
        {
            _logger.Log("Winget is not available on this system", LogLevel.Warning);
            if (wingetOnly)
            {
                result.Success = false;
                result.ErrorMessage = "Winget is required but not available";
                return result;
            }
        }

        // Ids come from an archive that may not be trustworthy: anything that is not a plain winget id is never passed to winget.
        foreach (var program in programs.Where(p => p.IsWingetAvailable && !string.IsNullOrEmpty(p.WingetId) && !WingetHelper.IsValidId(p.WingetId)))
        {
            _logger.Log($"Ignoring invalid winget id for '{program.Name}'; it will need manual installation", LogLevel.Warning);
        }

        var wingetPrograms = programs.Where(p => p.IsWingetAvailable && WingetHelper.IsValidId(p.WingetId)).ToList();
        var manualPrograms = programs.Where(p => !(p.IsWingetAvailable && WingetHelper.IsValidId(p.WingetId))).ToList();

        _logger.Log($"Programs to process: {wingetPrograms.Count} via winget, {manualPrograms.Count} manual", LogLevel.Info);

        if (dryRun)
        {
            result.WingetPrograms = wingetPrograms.Count;
            result.ManualPrograms = manualPrograms.Count;
            result.Success = true;
            _logger.Log("Dry run completed - no programs were actually installed", LogLevel.Info);
            return result;
        }

        if (wingetAvailable && wingetPrograms.Any())
        {
            foreach (var program in wingetPrograms)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var installResult = await InstallProgramViaWingetAsync(program, cancellationToken).ConfigureAwait(false);
                    if (installResult)
                    {
                        result.InstalledPrograms.Add(program);
                        result.SuccessfulInstalls++;
                    }
                    else
                    {
                        result.FailedPrograms.Add(program);
                        result.FailedInstalls++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Log($"Error installing {program.Name}: {ex.Message}", LogLevel.Error);
                    result.FailedPrograms.Add(program);
                    result.FailedInstalls++;
                }
            }
        }

        if (!wingetOnly && manualPrograms.Any())
        {
            result.ManualInstallRequired.AddRange(manualPrograms);
            _logger.Log($"{manualPrograms.Count} programs require manual installation", LogLevel.Info);
        }

        result.WingetPrograms = wingetPrograms.Count;
        result.ManualPrograms = manualPrograms.Count;
        result.Success = result.FailedInstalls == 0 || result.SuccessfulInstalls > 0;

        return result;
    }

    private async Task<bool> IsWingetAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("winget", ["--version"], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return !result.TimedOut && result.ExitCode == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Log($"Error checking winget availability: {ex.Message}", LogLevel.Debug);
        }

        return false;
    }

    private async Task<bool> InstallProgramViaWingetAsync(InstalledProgram program, CancellationToken cancellationToken)
    {
        _logger.Log($"Installing {program.Name} via winget (ID: {program.WingetId})", LogLevel.Info);

        try
        {
            var result = await ProcessRunner.RunAsync(
                "winget",
                WingetHelper.BuildInstallArguments(program.WingetId),
                InstallTimeout,
                cancellationToken).ConfigureAwait(false);

            if (result.TimedOut)
            {
                _logger.Log($"Installing {program.Name} timed out after {InstallTimeout.TotalMinutes:0} minutes", LogLevel.Warning);
                return false;
            }

            var status = WingetHelper.DescribeInstallExitCode(result.ExitCode);
            if (status.IsSuccess)
            {
                _logger.Log($"{program.Name}: {status.Description}", LogLevel.Info);
                return true;
            }

            _logger.Log($"Failed to install {program.Name}: {status.Description}", LogLevel.Warning);
            if (!string.IsNullOrEmpty(result.StandardError))
            {
                _logger.Log($"Error details: {result.StandardError}", LogLevel.Debug);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Log($"Exception installing {program.Name}: {ex.Message}", LogLevel.Error);
        }

        return false;
    }

    public async Task<List<InstalledProgram>> CheckProgramStatusAsync(List<InstalledProgram> programs)
    {
        _logger.Log("Checking current installation status of programs...", LogLevel.Info);
        
        var currentlyInstalled = new List<InstalledProgram>();
        var discovery = new SystemProgramDiscovery(_logger);
        var currentPrograms = await discovery.GetAllInstalledProgramsAsync().ConfigureAwait(false);
        
        foreach (var program in programs)
        {
            var isInstalled = currentPrograms.Any(cp => 
                cp.Name.Equals(program.Name, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(program.WingetId) && cp.WingetId.Equals(program.WingetId, StringComparison.OrdinalIgnoreCase)));
                
            if (isInstalled)
            {
                currentlyInstalled.Add(program);
            }
        }
        
        _logger.Log($"Found {currentlyInstalled.Count} out of {programs.Count} programs already installed", LogLevel.Info);
        return currentlyInstalled;
    }

    public async Task GenerateInstallationReportAsync(ProgramRestoreResult result, string outputPath)
    {
        var report = new
        {
            RestoreDate = DateTime.UtcNow,
            Summary = new
            {
                TotalPrograms = result.WingetPrograms + result.ManualPrograms,
                WingetPrograms = result.WingetPrograms,
                ManualPrograms = result.ManualPrograms,
                SuccessfulInstalls = result.SuccessfulInstalls,
                FailedInstalls = result.FailedInstalls,
                Success = result.Success
            },
            InstalledPrograms = result.InstalledPrograms,
            FailedPrograms = result.FailedPrograms,
            ManualInstallRequired = result.ManualInstallRequired,
            ErrorMessage = result.ErrorMessage
        };

        var json = JsonSerializer.Serialize(report, JsonDefaults.SystemBackup);
        await File.WriteAllTextAsync(outputPath, json).ConfigureAwait(false);

        _logger.Log($"Installation report saved to {outputPath}", LogLevel.Info);
    }
}

public class ProgramRestoreResult
{
    public bool Success { get; set; } = true;
    public string ErrorMessage { get; set; } = "";
    public int WingetPrograms { get; set; }
    public int ManualPrograms { get; set; }
    public int SuccessfulInstalls { get; set; }
    public int FailedInstalls { get; set; }
    public List<InstalledProgram> InstalledPrograms { get; set; } = [];
    public List<InstalledProgram> FailedPrograms { get; set; } = [];
    public List<InstalledProgram> ManualInstallRequired { get; set; } = [];
}
