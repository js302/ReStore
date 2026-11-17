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

    private static readonly TimeSpan WingetTimeout = TimeSpan.FromMinutes(2);

    public async Task<List<InstalledProgram>> GetAllInstalledProgramsAsync(CancellationToken cancellationToken = default)
    {
        _logger.Log("Discovering installed programs from multiple sources...", LogLevel.Info);

        var wingetPrograms = await GetWingetProgramsAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var registryPrograms = GetRegistryPrograms();

        var result = ProgramMerger.Merge(wingetPrograms, registryPrograms)
            .OrderBy(p => p.Name)
            .ToList();
        _logger.Log($"Found {result.Count} installed programs total", LogLevel.Info);

        return result;
    }

    /// <summary>
    /// Installed programs known to winget. Ids come from <c>winget list</c>; availability is confirmed against
    /// <c>winget export</c> (JSON) when that works, otherwise the Source column of the list is used.
    /// </summary>
    private async Task<List<InstalledProgram>> GetWingetProgramsAsync(CancellationToken cancellationToken)
    {
        var programs = new List<InstalledProgram>();

        try
        {
            _logger.Log("Querying winget for installed programs...", LogLevel.Debug);

            var list = await ProcessRunner.RunAsync(
                "winget",
                ["list", "--disable-interactivity", "--accept-source-agreements"],
                WingetTimeout,
                cancellationToken,
                System.Text.Encoding.UTF8).ConfigureAwait(false);

            if (list.TimedOut)
            {
                _logger.Log("winget list timed out", LogLevel.Warning);
                return programs;
            }

            if (list.ExitCode != 0)
            {
                _logger.Log($"Winget command failed (exit code {list.ExitCode}): {list.StandardError}", LogLevel.Warning);
                return programs;
            }

            programs = ParseWingetOutput(list.StandardOutput);

            var exported = await TryExportWingetIdsAsync(cancellationToken).ConfigureAwait(false);
            if (exported is { Count: > 0 })
            {
                ApplyExportedIds(programs, exported);
            }

            _logger.Log($"Found {programs.Count} programs via winget", LogLevel.Debug);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Log($"Error running winget: {ex.GetType().Name}: {ex.Message}", LogLevel.Warning);
        }

        return programs;
    }

    private async Task<List<WingetExportEntry>?> TryExportWingetIdsAsync(CancellationToken cancellationToken)
    {
        var exportDir = Path.Combine(AppPaths.RestoreWorkDirectory, "winget-" + Guid.NewGuid().ToString("N"));
        var exportFile = Path.Combine(exportDir, "export.json");
        try
        {
            Directory.CreateDirectory(exportDir);
            var export = await ProcessRunner.RunAsync(
                "winget",
                ["export", "-o", exportFile, "--accept-source-agreements", "--disable-interactivity"],
                WingetTimeout,
                cancellationToken,
                System.Text.Encoding.UTF8).ConfigureAwait(false);

            // winget export returns non-zero when some packages are not in a source but still writes the file.
            if (!export.TimedOut && File.Exists(exportFile))
            {
                return WingetHelper.ParseExportJson(await File.ReadAllTextAsync(exportFile, cancellationToken).ConfigureAwait(false));
            }

            _logger.Log($"winget export produced no usable file (exit code {export.ExitCode}); using winget list sources", LogLevel.Debug);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Log($"winget export unavailable: {ex.GetType().Name}: {ex.Message}", LogLevel.Debug);
        }
        finally
        {
            try { Directory.Delete(exportDir, true); } catch (Exception) { /* best effort */ }
        }

        return null;
    }

    /// <summary>Marks programs whose id appears in the export as installable via winget; exported ids absent from the list are added.</summary>
    internal static void ApplyExportedIds(List<InstalledProgram> programs, IReadOnlyList<WingetExportEntry> exported)
    {
        var exportedIds = exported.ToDictionary(e => e.Id, e => e, StringComparer.OrdinalIgnoreCase);

        foreach (var program in programs)
        {
            if (!string.IsNullOrEmpty(program.WingetId) && exportedIds.Remove(program.WingetId, out var match))
            {
                program.IsWingetAvailable = true;
                if (!string.IsNullOrEmpty(match.Source))
                {
                    program.Source = match.Source;
                }
            }
            else
            {
                // In the list but not exportable (for example installed from outside any source).
                program.IsWingetAvailable = false;
            }
        }

        foreach (var leftover in exportedIds.Values)
        {
            programs.Add(new InstalledProgram
            {
                Name = leftover.Id,
                WingetId = leftover.Id,
                IsWingetAvailable = true,
                Source = string.IsNullOrEmpty(leftover.Source) ? "winget" : leftover.Source
            });
        }
    }

    private List<InstalledProgram> ParseWingetOutput(string output) => WingetHelper.ParseListOutput(output);

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

            programs.AddRange(GetProgramsFromRegistryPath(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"));

            _logger.Log($"Found {programs.Count} programs via registry", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            _logger.Log($"Error reading registry: {ex.GetType().Name}: {ex.Message}", LogLevel.Warning);
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
                        _logger.Log($"Error reading registry subkey {subkeyName}: {ex.GetType().Name}: {ex.Message}", LogLevel.Debug);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Error accessing registry path {path}: {ex.GetType().Name}: {ex.Message}", LogLevel.Debug);
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

            var json = JsonSerializer.Serialize(exportData, JsonDefaults.SystemBackup);
            await File.WriteAllTextAsync(outputPath, json).ConfigureAwait(false);

            _logger.Log($"Exported {programs.Count} programs to {outputPath}", LogLevel.Info);
            return outputPath;
        }
        catch (Exception ex)
        {
            _logger.Log($"Error exporting programs to JSON: {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
            throw;
        }
    }
}
