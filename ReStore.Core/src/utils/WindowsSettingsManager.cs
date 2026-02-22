using System.Diagnostics;
using System.Text.Json;
using System.Runtime.Versioning;

namespace ReStore.Core.src.utils;

public class RegistryKeyInfo
{
    public string Path { get; set; } = "";
    public string Category { get; set; } = "";
    public bool RequiresAdmin { get; set; }
}

public class WindowsSettingsExport
{
    public DateTime ExportDate { get; set; }
    public string ComputerName { get; set; } = "";
    public string UserName { get; set; } = "";
    public List<string> ExportedCategories { get; set; } = [];
    public Dictionary<string, string> ExportedFiles { get; set; } = [];
}

[SupportedOSPlatform("windows")]
public class WindowsSettingsManager(ILogger logger)
{
    private readonly ILogger _logger = logger;
    private readonly Dictionary<string, List<RegistryKeyInfo>> _settingsCategories = InitializeSettingsCategories();

    private static Dictionary<string, List<RegistryKeyInfo>> InitializeSettingsCategories()
    {
        return new Dictionary<string, List<RegistryKeyInfo>>
        {
            ["Personalization"] = new List<RegistryKeyInfo>
            {
                new() { Path = @"HKEY_CURRENT_USER\Control Panel\Desktop", Category = "Personalization", RequiresAdmin = false },
                new() { Path = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes", Category = "Personalization", RequiresAdmin = false },
                new() { Path = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", Category = "Personalization", RequiresAdmin = false },
                new() { Path = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", Category = "Personalization", RequiresAdmin = false }
            },
            ["Explorer"] = new List<RegistryKeyInfo>
            {
                new() { Path = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", Category = "Explorer", RequiresAdmin = false },
                new() { Path = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\CabinetState", Category = "Explorer", RequiresAdmin = false }
            },
            ["Regional"] = new List<RegistryKeyInfo>
            {
                new() { Path = @"HKEY_CURRENT_USER\Control Panel\International", Category = "Regional", RequiresAdmin = false }
            },
            ["Taskbar"] = new List<RegistryKeyInfo>
            {
                new() { Path = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3", Category = "Taskbar", RequiresAdmin = false },
                new() { Path = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", Category = "Taskbar", RequiresAdmin = false }
            },
            ["Mouse"] = new List<RegistryKeyInfo>
            {
                new() { Path = @"HKEY_CURRENT_USER\Control Panel\Mouse", Category = "Mouse", RequiresAdmin = false },
                new() { Path = @"HKEY_CURRENT_USER\Control Panel\Cursors", Category = "Mouse", RequiresAdmin = false }
            },
            ["Keyboard"] = new List<RegistryKeyInfo>
            {
                new() { Path = @"HKEY_CURRENT_USER\Control Panel\Keyboard", Category = "Keyboard", RequiresAdmin = false }
            },
            ["Accessibility"] = new List<RegistryKeyInfo>
            {
                new() { Path = @"HKEY_CURRENT_USER\Control Panel\Accessibility", Category = "Accessibility", RequiresAdmin = false }
            },
            ["System"] = new List<RegistryKeyInfo>
            {
                new() { Path = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\TimeZoneInformation", Category = "System", RequiresAdmin = true },
                new() { Path = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power", Category = "System", RequiresAdmin = true }
            }
        };
    }

    public async Task<WindowsSettingsExport> ExportWindowsSettingsAsync(string outputDirectory, List<string>? categories = null)
    {
        _logger.Log("Exporting Windows settings...", LogLevel.Info);
        
        Directory.CreateDirectory(outputDirectory);
        
        var export = new WindowsSettingsExport
        {
            ExportDate = DateTime.UtcNow,
            ComputerName = Environment.MachineName,
            UserName = Environment.UserName
        };

        var categoriesToExport = categories ?? _settingsCategories.Keys.ToList();

        foreach (var category in categoriesToExport)
        {
            if (!_settingsCategories.ContainsKey(category))
            {
                _logger.Log($"Unknown category: {category}", LogLevel.Warning);
                continue;
            }

            _logger.Log($"Exporting {category} settings...", LogLevel.Debug);
            
            foreach (var keyInfo in _settingsCategories[category])
            {
                try
                {
                    var fileName = $"{category}_{Path.GetFileName(keyInfo.Path)}.reg";
                    var outputPath = Path.Combine(outputDirectory, fileName);
                    
                    var success = await ExportRegistryKeyAsync(keyInfo.Path, outputPath);
                    
                    if (success)
                    {
                        export.ExportedFiles[keyInfo.Path] = fileName;
                        if (!export.ExportedCategories.Contains(category))
                        {
                            export.ExportedCategories.Add(category);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log($"Error exporting {keyInfo.Path}: {ex.Message}", LogLevel.Warning);
                }
            }
        }

        var manifestPath = Path.Combine(outputDirectory, "settings_manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true }));

        _logger.Log($"Exported {export.ExportedCategories.Count} categories with {export.ExportedFiles.Count} registry keys", LogLevel.Info);
        
        return export;
    }

    private async Task<bool> ExportRegistryKeyAsync(string registryPath, string outputPath)
    {
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = $"export \"{registryPath}\" \"{outputPath}\" /y",
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
                    _logger.Log($"Exported registry key: {registryPath}", LogLevel.Debug);
                    return true;
                }
                else
                {
                    _logger.Log($"Failed to export {registryPath}: {error}", LogLevel.Warning);
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Error running reg export for {registryPath}: {ex.Message}", LogLevel.Warning);
        }
        
        return false;
    }

    public async Task<string> CreateRestoreScriptAsync(WindowsSettingsExport export, string exportDirectory, string outputPath)
    {
        try
        {
            var scriptContent = new List<string>
            {
                "# ReStore Windows Settings Restore Script",
                "# Generated on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                "",
                "Write-Host 'ReStore Windows Settings Restore' -ForegroundColor Green",
                "Write-Host '===================================' -ForegroundColor Green",
                "",
                "Write-Host 'WARNING: This will modify Windows Registry settings.' -ForegroundColor Yellow",
                "Write-Host 'It is recommended to create a system restore point before proceeding.' -ForegroundColor Yellow",
                "",
                "$response = Read-Host 'Do you want to continue? (y/N)'",
                "if ($response -ne 'y' -and $response -ne 'Y') {",
                "    Write-Host 'Restore cancelled by user.' -ForegroundColor Yellow",
                "    exit 0",
                "}",
                "",
                "$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path",
                "$successCount = 0",
                "$failedCount = 0",
                "",
                "function Import-RegistryFile {",
                "    param([string]$FilePath, [string]$Description)",
                "    ",
                "    if (Test-Path $FilePath) {",
                "        Write-Host \"Importing $Description...\" -ForegroundColor Cyan",
                "        try {",
                "            $result = reg import \"$FilePath\" 2>&1",
                "            if ($LASTEXITCODE -eq 0) {",
                "                Write-Host \"Successfully imported $Description\" -ForegroundColor Green",
                "                return $true",
                "            } else {",
                "                Write-Host \"Failed to import $Description\" -ForegroundColor Red",
                "                return $false",
                "            }",
                "        } catch {",
                "            Write-Host \"Error importing $Description: $($_.Exception.Message)\" -ForegroundColor Red",
                "            return $false",
                "        }",
                "    } else {",
                "        Write-Host \"File not found: $FilePath\" -ForegroundColor Yellow",
                "        return $false",
                "    }",
                "}",
                ""
            };

            var adminRequired = false;
            var userFiles = new List<(string path, string desc)>();
            var systemFiles = new List<(string path, string desc)>();

            foreach (var kvp in export.ExportedFiles)
            {
                var registryPath = kvp.Key;
                var fileName = kvp.Value;
                var filePath = Path.Combine("$scriptDir", fileName);
                var description = $"{Path.GetFileNameWithoutExtension(fileName)}";

                var isSystemKey = registryPath.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase);
                
                if (isSystemKey)
                {
                    adminRequired = true;
                    systemFiles.Add((filePath, description));
                }
                else
                {
                    userFiles.Add((filePath, description));
                }
            }

            if (adminRequired)
            {
                scriptContent.AddRange(
                [
                    "$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())",
                    "$isAdmin = $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)",
                    "",
                    "if (-not $isAdmin) {",
                    "    Write-Host 'Some settings require administrator privileges.' -ForegroundColor Yellow",
                    "    Write-Host 'Please run this script as Administrator to restore all settings.' -ForegroundColor Yellow",
                    "    Write-Host 'Continuing with user-level settings only...' -ForegroundColor Yellow",
                    "    Start-Sleep -Seconds 3",
                    "}",
                    ""
                ]);
            }

            scriptContent.Add("# Importing user-level settings");
            foreach (var (filePath, description) in userFiles)
            {
                scriptContent.AddRange(new[]
                {
                    $"if (Import-RegistryFile \"{filePath}\" \"{description}\") {{",
                    "    $successCount++",
                    "} else {",
                    "    $failedCount++",
                    "}",
                    ""
                });
            }

            if (systemFiles.Count > 0)
            {
                scriptContent.AddRange(
                [
                    "# Importing system-level settings (requires admin)",
                    "if ($isAdmin) {"
                ]);

                foreach (var (filePath, description) in systemFiles)
                {
                    scriptContent.AddRange(
                    [
                        $"    if (Import-RegistryFile \"{filePath}\" \"{description}\") {{",
                        "        $successCount++",
                        "    } else {",
                        "        $failedCount++",
                        "    }",
                        ""
                    ]);
                }

                scriptContent.AddRange(
                [
                    "} else {",
                    $"    Write-Host 'Skipped {systemFiles.Count} system-level settings (requires admin)' -ForegroundColor Yellow",
                    $"    $failedCount += {systemFiles.Count}",
                    "}",
                    ""
                ]);
            }

            scriptContent.AddRange(
            [
                "Write-Host ''",
                "Write-Host 'Restore Summary:' -ForegroundColor Green",
                "Write-Host \"Successfully imported: $successCount\" -ForegroundColor Green",
                "Write-Host \"Failed to import: $failedCount\" -ForegroundColor Red",
                "",
                "Write-Host ''",
                "Write-Host 'NOTE: Some changes may require logging off or restarting Windows to take effect.' -ForegroundColor Yellow"
            ]);

            await File.WriteAllTextAsync(outputPath, string.Join(Environment.NewLine, scriptContent));
            
            _logger.Log($"Created restore script at {outputPath}", LogLevel.Info);
            return outputPath;
        }
        catch (Exception ex)
        {
            _logger.Log($"Error creating restore script: {ex.Message}", LogLevel.Error);
            throw;
        }
    }

    public List<string> GetAvailableCategories()
    {
        return [.. _settingsCategories.Keys];
    }

    public Dictionary<string, List<RegistryKeyInfo>> GetAllCategories()
    {
        return _settingsCategories;
    }
}
