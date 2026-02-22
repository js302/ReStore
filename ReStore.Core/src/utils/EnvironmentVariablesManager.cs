using System.Text.Json;
using System.Diagnostics;

namespace ReStore.Core.src.utils;

public class EnvironmentVariableEntry
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public EnvironmentVariableTarget Target { get; set; }
    public string TargetName => Target.ToString();
}

public class EnvironmentVariablesManager(ILogger logger)
{
    private readonly ILogger _logger = logger;

    public Task<List<EnvironmentVariableEntry>> GetAllEnvironmentVariablesAsync()
    {
        _logger.Log("Collecting all environment variables...", LogLevel.Info);

        var variables = new List<EnvironmentVariableEntry>();

        // Get system environment variables
        variables.AddRange(GetEnvironmentVariables(EnvironmentVariableTarget.Machine));

        // Get user environment variables  
        variables.AddRange(GetEnvironmentVariables(EnvironmentVariableTarget.User));

        _logger.Log($"Found {variables.Count} environment variables", LogLevel.Info);
        return Task.FromResult(variables);
    }

    private List<EnvironmentVariableEntry> GetEnvironmentVariables(EnvironmentVariableTarget target)
    {
        var variables = new List<EnvironmentVariableEntry>();

        try
        {
            _logger.Log($"Reading {target} environment variables...", LogLevel.Debug);

            var envVars = Environment.GetEnvironmentVariables(target);

            foreach (string key in envVars.Keys)
            {
                var value = envVars[key]?.ToString() ?? "";

                // Skip some system variables that shouldn't be backed up
                if (ShouldSkipVariable(key, target))
                    continue;

                variables.Add(new EnvironmentVariableEntry
                {
                    Name = key,
                    Value = value,
                    Target = target
                });
            }

            _logger.Log($"Found {variables.Count} {target} environment variables", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            _logger.Log($"Error reading {target} environment variables: {ex.Message}", LogLevel.Warning);
        }

        return variables;
    }

    private static bool ShouldSkipVariable(string name, EnvironmentVariableTarget target)
    {
        // Skip system-managed variables that shouldn't be restored
        var systemManagedVars = new[]
        {
            "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "PROCESSOR_LEVEL", "PROCESSOR_REVISION",
            "NUMBER_OF_PROCESSORS", "OS", "COMPUTERNAME", "USERNAME", "USERDOMAIN",
            "SYSTEMROOT", "WINDIR", "SYSTEMDRIVE", "HOMEDRIVE", "HOMEPATH",
            "LOGONSERVER", "SESSIONNAME", "CLIENTNAME", "APPDATA", "LOCALAPPDATA",
            "PROGRAMDATA", "PROGRAMFILES", "PROGRAMFILES(X86)", "COMMONPROGRAMFILES",
            "COMMONPROGRAMFILES(X86)", "PUBLIC", "ALLUSERSPROFILE", "USERPROFILE",
            "TEMP", "TMP", "PATHEXT", "COMSPEC"
        };

        return systemManagedVars.Contains(name.ToUpperInvariant());
    }

    public async Task<string> ExportEnvironmentVariablesToJsonAsync(List<EnvironmentVariableEntry> variables, string outputPath)
    {
        try
        {
            var exportData = new
            {
                ExportDate = DateTime.UtcNow,
                ComputerName = Environment.MachineName,
                UserName = Environment.UserName,
                TotalVariables = variables.Count,
                SystemVariables = variables.Count(v => v.Target == EnvironmentVariableTarget.Machine),
                UserVariables = variables.Count(v => v.Target == EnvironmentVariableTarget.User),
                Variables = variables
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var json = JsonSerializer.Serialize(exportData, options);
            await File.WriteAllTextAsync(outputPath, json);

            _logger.Log($"Exported {variables.Count} environment variables to {outputPath}", LogLevel.Info);
            return outputPath;
        }
        catch (Exception ex)
        {
            _logger.Log($"Error exporting environment variables to JSON: {ex.Message}", LogLevel.Error);
            throw;
        }
    }

    public async Task<string> CreateRestoreScriptAsync(List<EnvironmentVariableEntry> variables, string outputPath)
    {
        try
        {
            var scriptContent = new List<string>
            {
                "# ReStore Environment Variables Restore Script",
                "# Generated on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                "",
                "Write-Host 'Starting environment variables restore...'",
                "",
                "# Function to set environment variable safely",
                "function Set-EnvironmentVariableSafely {",
                "    param(",
                "        [string]$Name,",
                "        [string]$Value,",
                "        [System.EnvironmentVariableTarget]$Target",
                "    )",
                "    ",
                "    try {",
                "        $currentValue = [System.Environment]::GetEnvironmentVariable($Name, $Target)",
                "        if ($currentValue -ne $Value) {",
                "            [System.Environment]::SetEnvironmentVariable($Name, $Value, $Target)",
                "            Write-Host \"Set $Target variable '$Name' = '$Value'\" -ForegroundColor Green",
                "        } else {",
                "            Write-Host \"Variable '$Name' already has correct value\" -ForegroundColor Yellow",
                "        }",
                "    } catch {",
                "        Write-Error \"Failed to set variable '$Name': $($_.Exception.Message)\"",
                "    }",
                "}",
                "",
                "# Restore environment variables"
            };

            foreach (var variable in variables.OrderBy(v => v.Target).ThenBy(v => v.Name))
            {
                var target = variable.Target == EnvironmentVariableTarget.Machine ? "Machine" : "User";
                var escapedValue = variable.Value.Replace("'", "''").Replace("`", "``");

                scriptContent.Add($"Set-EnvironmentVariableSafely -Name '{variable.Name}' -Value '{escapedValue}' -Target {target}");
            }

            scriptContent.AddRange(new[]
            {
                "",
                "Write-Host 'Environment variables restore completed!' -ForegroundColor Green",
                "Write-Host 'Note: You may need to restart applications or log off/on for changes to take effect.' -ForegroundColor Yellow"
            });

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

    public async Task RestoreEnvironmentVariablesAsync(string jsonPath)
    {
        try
        {
            _logger.Log($"Restoring environment variables from {jsonPath}...", LogLevel.Info);

            var json = await File.ReadAllTextAsync(jsonPath);
            var data = JsonSerializer.Deserialize<JsonElement>(json);

            if (data.TryGetProperty("variables", out var variablesElement))
            {
                var variables = JsonSerializer.Deserialize<List<EnvironmentVariableEntry>>(variablesElement.GetRawText());

                if (variables != null)
                {
                    foreach (var variable in variables)
                    {
                        try
                        {
                            Environment.SetEnvironmentVariable(variable.Name, variable.Value, variable.Target);
                            _logger.Log($"Restored {variable.TargetName} variable: {variable.Name}", LogLevel.Debug);
                        }
                        catch (Exception ex)
                        {
                            _logger.Log($"Failed to restore variable {variable.Name}: {ex.Message}", LogLevel.Warning);
                        }
                    }

                    _logger.Log($"Successfully restored {variables.Count} environment variables", LogLevel.Info);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Error restoring environment variables: {ex.Message}", LogLevel.Error);
            throw;
        }
    }

    public async Task<bool> RestoreEnvironmentVariablesWithPowerShellAsync(string scriptPath)
    {
        try
        {
            _logger.Log($"Executing PowerShell restore script: {scriptPath}", LogLevel.Info);

            var processStartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\"",
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

                if (!string.IsNullOrEmpty(output))
                {
                    _logger.Log($"PowerShell output: {output}", LogLevel.Debug);
                }

                if (process.ExitCode == 0)
                {
                    _logger.Log("Environment variables restored successfully via PowerShell", LogLevel.Info);
                    return true;
                }
                else
                {
                    _logger.Log($"PowerShell script failed with exit code {process.ExitCode}: {error}", LogLevel.Error);
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Error executing PowerShell restore script: {ex.Message}", LogLevel.Error);
        }

        return false;
    }
}
