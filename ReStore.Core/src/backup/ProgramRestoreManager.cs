using System.Diagnostics;
using System.Text.Json;
using ReStore.Core.src.utils;
using System.Runtime.Versioning;

namespace ReStore.Core.src.backup;

[SupportedOSPlatform("windows")]
public class ProgramRestoreManager(ILogger logger)
{
    private readonly ILogger _logger = logger;

    public async Task<ProgramRestoreResult> RestoreProgramsFromJsonAsync(string jsonPath, bool wingetOnly = false, bool dryRun = false)
    {
        _logger.Log($"Starting program restore from {jsonPath} (WingetOnly: {wingetOnly}, DryRun: {dryRun})", LogLevel.Info);
        
        var result = new ProgramRestoreResult();
        
        try
        {
            var json = await File.ReadAllTextAsync(jsonPath);
            var data = JsonSerializer.Deserialize<JsonElement>(json);
            
            if (data.TryGetProperty("programs", out var programsElement))
            {
                var programs = JsonSerializer.Deserialize<List<InstalledProgram>>(programsElement.GetRawText());
                
                if (programs != null)
                {
                    result = await RestoreProgramsAsync(programs, wingetOnly, dryRun);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Error restoring programs: {ex.Message}", LogLevel.Error);
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }
        
        return result;
    }

    public async Task<ProgramRestoreResult> RestoreProgramsAsync(List<InstalledProgram> programs, bool wingetOnly = false, bool dryRun = false)
    {
        var result = new ProgramRestoreResult();
        
        // Check if winget is available
        var wingetAvailable = await IsWingetAvailableAsync();
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

        var wingetPrograms = programs.Where(p => p.IsWingetAvailable && !string.IsNullOrEmpty(p.WingetId)).ToList();
        var manualPrograms = programs.Where(p => !p.IsWingetAvailable || string.IsNullOrEmpty(p.WingetId)).ToList();

        _logger.Log($"Programs to process: {wingetPrograms.Count} via winget, {manualPrograms.Count} manual", LogLevel.Info);

        if (dryRun)
        {
            result.WingetPrograms = wingetPrograms.Count;
            result.ManualPrograms = manualPrograms.Count;
            result.Success = true;
            _logger.Log("Dry run completed - no programs were actually installed", LogLevel.Info);
            return result;
        }

        // Install winget programs
        if (wingetAvailable && wingetPrograms.Any())
        {
            foreach (var program in wingetPrograms)
            {
                try
                {
                    var installResult = await InstallProgramViaWingetAsync(program);
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
                catch (Exception ex)
                {
                    _logger.Log($"Error installing {program.Name}: {ex.Message}", LogLevel.Error);
                    result.FailedPrograms.Add(program);
                    result.FailedInstalls++;
                }
            }
        }

        // Report manual programs
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

    private async Task<bool> IsWingetAvailableAsync()
    {
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processStartInfo);
            if (process != null)
            {
                await process.WaitForExitAsync();
                return process.ExitCode == 0;
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Error checking winget availability: {ex.Message}", LogLevel.Debug);
        }
        
        return false;
    }

    private async Task<bool> InstallProgramViaWingetAsync(InstalledProgram program)
    {
        _logger.Log($"Installing {program.Name} via winget (ID: {program.WingetId})", LogLevel.Info);
        
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = $"install --id {program.WingetId} --silent --accept-source-agreements --accept-package-agreements",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processStartInfo);
            if (process != null)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode == 0)
                {
                    _logger.Log($"Successfully installed {program.Name}", LogLevel.Info);
                    return true;
                }
                else
                {
                    _logger.Log($"Failed to install {program.Name}: Exit code {process.ExitCode}", LogLevel.Warning);
                    if (!string.IsNullOrEmpty(error))
                    {
                        _logger.Log($"Error details: {error}", LogLevel.Debug);
                    }
                    return false;
                }
            }
        }
        catch (Exception ex)
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
        var currentPrograms = await discovery.GetAllInstalledProgramsAsync();
        
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

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var json = JsonSerializer.Serialize(report, options);
        await File.WriteAllTextAsync(outputPath, json);

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
