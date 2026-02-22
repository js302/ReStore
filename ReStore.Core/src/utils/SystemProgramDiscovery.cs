using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace ReStore.Core.src.utils;

public class InstalledProgram
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string InstallDate { get; set; } = "";
    public string InstallLocation { get; set; } = "";
    public string UninstallString { get; set; } = "";
    public string Source { get; set; } = ""; // "winget", "registry", "wmi"
    public string WingetId { get; set; } = ""; // For winget packages
    public bool IsWingetAvailable { get; set; } = false;
}

[SupportedOSPlatform("windows")]
public class SystemProgramDiscovery(ILogger logger)
{
    private readonly ILogger _logger = logger;

    public async Task<List<InstalledProgram>> GetAllInstalledProgramsAsync()
    {
        _logger.Log("Discovering installed programs from multiple sources...", LogLevel.Info);

        var allPrograms = new Dictionary<string, InstalledProgram>();

        // Get programs from winget
        var wingetPrograms = await GetWingetProgramsAsync();
        foreach (var program in wingetPrograms)
        {
            var key = $"{program.Name}_{program.Publisher}".ToLowerInvariant();
            allPrograms[key] = program;
        }

        // Get programs from registry
        var registryPrograms = GetRegistryPrograms();
        foreach (var program in registryPrograms)
        {
            var key = $"{program.Name}_{program.Publisher}".ToLowerInvariant();
            if (!allPrograms.TryGetValue(key, out InstalledProgram? existing))
            {
                allPrograms[key] = program;
            }
            else
            {
                if (string.IsNullOrEmpty(existing.InstallLocation) && !string.IsNullOrEmpty(program.InstallLocation))
                    existing.InstallLocation = program.InstallLocation;
                if (string.IsNullOrEmpty(existing.UninstallString) && !string.IsNullOrEmpty(program.UninstallString))
                    existing.UninstallString = program.UninstallString;
                if (string.IsNullOrEmpty(existing.InstallDate) && !string.IsNullOrEmpty(program.InstallDate))
                    existing.InstallDate = program.InstallDate;
            }
        }

        // Check which registry programs are available in winget
        await CheckWingetAvailabilityAsync(allPrograms.Values.Where(p => p.Source != "winget").ToList());

        var result = allPrograms.Values.OrderBy(p => p.Name).ToList();
        _logger.Log($"Found {result.Count} installed programs total", LogLevel.Info);

        return result;
    }

    private async Task<List<InstalledProgram>> GetWingetProgramsAsync()
    {
        var programs = new List<InstalledProgram>();

        try
        {
            _logger.Log("Querying winget for installed programs...", LogLevel.Debug);

            var processStartInfo = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = "list --disable-interactivity",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            using var process = Process.Start(processStartInfo);
            if (process != null)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode == 0)
                {
                    programs = ParseWingetOutput(output);
                    _logger.Log($"Found {programs.Count} programs via winget", LogLevel.Debug);
                }
                else
                {
                    _logger.Log($"Winget command failed: {error}", LogLevel.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Error running winget: {ex.Message}", LogLevel.Warning);
        }

        return programs;
    }

    private List<InstalledProgram> ParseWingetOutput(string output)
    {
        var programs = new List<InstalledProgram>();
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var headerLine = lines.FirstOrDefault(l => l.StartsWith("Name") && l.Contains("Id") && l.Contains("Version"));
        if (headerLine == null) return programs;

        int idIndex = headerLine.IndexOf("Id");
        int versionIndex = headerLine.IndexOf("Version");
        int availableIndex = headerLine.IndexOf("Available");
        int sourceIndex = headerLine.IndexOf("Source");

        var dataLines = lines.SkipWhile(l => !l.StartsWith("---")).Skip(1).Where(line => !string.IsNullOrWhiteSpace(line)).ToList();

        foreach (var line in dataLines)
        {
            try
            {
                if (line.Length < versionIndex) continue;

                var name = line[..idIndex].Trim();
                var id = line[idIndex..versionIndex].Trim();

                string version = "";
                if (availableIndex > 0 && line.Length >= availableIndex)
                {
                    version = line[versionIndex..availableIndex].Trim();
                }
                else if (line.Length > versionIndex)
                {
                    version = line[versionIndex..].Trim();
                }

                string source = "";
                if (sourceIndex > 0 && line.Length > sourceIndex)
                {
                    source = line[sourceIndex..].Trim();
                }

                if (!string.IsNullOrEmpty(name) && !name.StartsWith("-"))
                {
                    programs.Add(new InstalledProgram
                    {
                        Name = name,
                        Version = version,
                        WingetId = id,
                        Source = string.IsNullOrEmpty(source) ? "registry" : source,
                        IsWingetAvailable = !string.IsNullOrEmpty(source) && source.Equals("winget", StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"Error parsing winget line '{line}': {ex.Message}", LogLevel.Debug);
            }
        }

        return programs;
    }

    private List<InstalledProgram> GetRegistryPrograms()
    {
        var programs = new List<InstalledProgram>();

        try
        {
            _logger.Log("Querying Windows Registry for installed programs...", LogLevel.Debug);

            // Check both 32-bit and 64-bit registry locations
            var registryPaths = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            foreach (var registryPath in registryPaths)
            {
                programs.AddRange(GetProgramsFromRegistryPath(Registry.LocalMachine, registryPath));
            }

            // Also check current user installs
            programs.AddRange(GetProgramsFromRegistryPath(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"));

            _logger.Log($"Found {programs.Count} programs via registry", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            _logger.Log($"Error reading registry: {ex.Message}", LogLevel.Warning);
        }

        return programs;
    }

    private List<InstalledProgram> GetProgramsFromRegistryPath(RegistryKey rootKey, string path)
    {
        var programs = new List<InstalledProgram>();

        try
        {
            using var key = rootKey.OpenSubKey(path);
            if (key != null)
            {
                foreach (var subkeyName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var subkey = key.OpenSubKey(subkeyName);
                        if (subkey != null)
                        {
                            var displayName = subkey.GetValue("DisplayName")?.ToString();
                            if (!string.IsNullOrEmpty(displayName))
                            {
                                // Skip system components and updates
                                if (ShouldSkipProgram(displayName, subkey))
                                    continue;

                                var program = new InstalledProgram
                                {
                                    Name = displayName,
                                    Version = subkey.GetValue("DisplayVersion")?.ToString() ?? "",
                                    Publisher = subkey.GetValue("Publisher")?.ToString() ?? "",
                                    InstallDate = FormatInstallDate(subkey.GetValue("InstallDate")?.ToString()),
                                    InstallLocation = subkey.GetValue("InstallLocation")?.ToString() ?? "",
                                    UninstallString = subkey.GetValue("UninstallString")?.ToString() ?? "",
                                    Source = "registry"
                                };

                                programs.Add(program);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Log($"Error reading registry subkey {subkeyName}: {ex.Message}", LogLevel.Debug);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Error accessing registry path {path}: {ex.Message}", LogLevel.Debug);
        }

        return programs;
    }

    private bool ShouldSkipProgram(string displayName, RegistryKey subkey)
    {
        // Skip Windows updates, hotfixes, and system components
        if (displayName.Contains("Update for") ||
            displayName.Contains("Hotfix for") ||
            displayName.StartsWith("KB") ||
            displayName.Contains("Security Update") ||
            displayName.Contains("Microsoft Visual C++") && displayName.Contains("Redistributable"))
        {
            return true;
        }

        // Skip if it's a system component
        var systemComponent = subkey.GetValue("SystemComponent");
        if (systemComponent != null && systemComponent.ToString() == "1")
        {
            return true;
        }

        // Skip if it has no uninstall string and is likely a component
        var uninstallString = subkey.GetValue("UninstallString")?.ToString();
        if (string.IsNullOrEmpty(uninstallString))
        {
            return true;
        }

        return false;
    }

    private static string FormatInstallDate(string? installDate)
    {
        if (string.IsNullOrEmpty(installDate) || installDate.Length != 8)
            return "";

        try
        {
            var year = installDate[..4];
            var month = installDate.Substring(4, 2);
            var day = installDate.Substring(6, 2);
            return $"{year}-{month}-{day}";
        }
        catch
        {
            return installDate;
        }
    }

    private async Task CheckWingetAvailabilityAsync(List<InstalledProgram> programs)
    {
        _logger.Log("Checking winget availability for registry programs...", LogLevel.Debug);

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

                if (process.ExitCode == 0)
                {
                    // Winget is available, check each program
                    foreach (var program in programs.Take(10)) // Limit to avoid too many API calls
                    {
                        program.IsWingetAvailable = await CheckSingleProgramWingetAvailability(program.Name);
                        if (program.IsWingetAvailable)
                        {
                            program.WingetId = await GetWingetIdForProgram(program.Name);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Error checking winget availability: {ex.Message}", LogLevel.Debug);
        }
    }

    private async Task<bool> CheckSingleProgramWingetAvailability(string programName)
    {
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = $"search \"{programName}\" --disable-interactivity --count 1",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processStartInfo);
            if (process != null)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                return process.ExitCode == 0 && !output.Contains("No package found");
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Error checking winget for {programName}: {ex.Message}", LogLevel.Debug);
        }

        return false;
    }

    private async Task<string> GetWingetIdForProgram(string programName)
    {
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = $"search \"{programName}\" --disable-interactivity --count 1",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processStartInfo);
            if (process != null)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode == 0)
                {
                    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    var dataLine = lines.Skip(2).FirstOrDefault();
                    if (dataLine != null)
                    {
                        var parts = dataLine.Split('|');
                        if (parts.Length >= 2)
                        {
                            return parts[1].Trim();
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Error getting winget ID for {programName}: {ex.Message}", LogLevel.Debug);
        }

        return "";
    }

    public async Task<string> ExportProgramsToJsonAsync(List<InstalledProgram> programs, string outputPath)
    {
        try
        {
            var exportData = new
            {
                ExportDate = DateTime.UtcNow,
                TotalPrograms = programs.Count,
                WingetAvailable = programs.Count(p => p.IsWingetAvailable),
                Programs = programs
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var json = JsonSerializer.Serialize(exportData, options);
            await File.WriteAllTextAsync(outputPath, json);

            _logger.Log($"Exported {programs.Count} programs to {outputPath}", LogLevel.Info);
            return outputPath;
        }
        catch (Exception ex)
        {
            _logger.Log($"Error exporting programs to JSON: {ex.Message}", LogLevel.Error);
            throw;
        }
    }
}
