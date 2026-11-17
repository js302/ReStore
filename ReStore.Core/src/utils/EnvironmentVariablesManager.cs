using System.Text.Json;

namespace ReStore.Core.src.utils;

public class EnvironmentVariableEntry
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public EnvironmentVariableTarget Target { get; set; }
    public string TargetName => Target.ToString();

    /// <summary>
    /// Registry value kind ("String" or "ExpandString") recorded at backup time. Null in files written by
    /// older versions, which restore as plain strings (the previous behaviour).
    /// </summary>
    public string? Kind { get; set; }
}

public sealed class EnvironmentRestoreResult
{
    public int Total { get; set; }
    public int Restored { get; set; }
    public int Merged { get; set; }
    public int Skipped { get; set; }
    public List<string> Failures { get; } = [];
    public int Failed => Failures.Count;
}

public class EnvironmentVariablesManager(ILogger logger)
{
    private readonly ILogger _logger = logger;

    public Task<List<EnvironmentVariableEntry>> GetAllEnvironmentVariablesAsync()
    {
        _logger.Log("Collecting all environment variables...", LogLevel.Info);

        var variables = new List<EnvironmentVariableEntry>();

        variables.AddRange(GetEnvironmentVariables(EnvironmentVariableTarget.Machine));

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
                    Target = target,
                    Kind = DefaultEnvironmentVariableStore.TryGetKind(key, target)
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

    internal static bool ShouldSkipVariable(string name, EnvironmentVariableTarget target)
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

            var json = JsonSerializer.Serialize(exportData, JsonDefaults.SystemBackup);
            await File.WriteAllTextAsync(outputPath, json).ConfigureAwait(false);

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
                "# Generated on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
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
                var escapedValue = ScriptFile.EscapeSingleQuoted(variable.Value).Replace("`", "``");
                var escapedName = ScriptFile.EscapeSingleQuotedLabel(variable.Name);

                scriptContent.Add($"Set-EnvironmentVariableSafely -Name '{escapedName}' -Value '{escapedValue}' -Target {target}");
            }

            scriptContent.AddRange(new[]
            {
                "",
                "Write-Host 'Environment variables restore completed!' -ForegroundColor Green",
                "Write-Host 'Note: You may need to restart applications or log off/on for changes to take effect.' -ForegroundColor Yellow"
            });

            await ScriptFile.WriteAllTextAsync(outputPath, string.Join(Environment.NewLine, scriptContent)).ConfigureAwait(false);

            _logger.Log($"Created restore script at {outputPath}", LogLevel.Info);
            return outputPath;
        }
        catch (Exception ex)
        {
            _logger.Log($"Error creating restore script: {ex.Message}", LogLevel.Error);
            throw;
        }
    }

    private sealed class EnvironmentVariablesExportFile
    {
        public List<EnvironmentVariableEntry>? Variables { get; set; }
    }

    /// <summary>
    /// Reads the variables from an exported environment_variables.json. A missing list yields no variables. Throws if the file has entries but none has a name (a sign of a mis-read file).
    /// </summary>
    public static async Task<List<EnvironmentVariableEntry>> LoadVariablesFromJsonAsync(string jsonPath)
    {
        var json = await File.ReadAllTextAsync(jsonPath).ConfigureAwait(false);
        var data = JsonSerializer.Deserialize<EnvironmentVariablesExportFile>(json, JsonDefaults.SystemBackup);

        var variables = data?.Variables ?? [];
        if (variables.Count > 0 && variables.All(v => string.IsNullOrWhiteSpace(v.Name)))
        {
            throw new InvalidDataException(
                $"'{jsonPath}' contains {variables.Count} environment variable entries but none has a name; the file is malformed.");
        }

        return variables;
    }

    /// <summary>
    /// Restores variables from environment_variables.json. List variables (PATH, PATHEXT, PSModulePath, any
    /// <c>;</c>-list) are merged with the current value instead of replaced, and expand-type values keep
    /// <c>REG_EXPAND_SZ</c>. Per-variable failures (for example Machine scope without elevation) are collected
    /// in the result rather than thrown, so one failure does not stop the rest.
    /// </summary>
    public async Task<EnvironmentRestoreResult> RestoreEnvironmentVariablesAsync(
        string jsonPath,
        IEnvironmentVariableStore? store = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.Log($"Restoring environment variables from {jsonPath}...", LogLevel.Info);

            var variables = await LoadVariablesFromJsonAsync(jsonPath).ConfigureAwait(false);
            var result = ApplyVariables(variables, store ?? new DefaultEnvironmentVariableStore(), cancellationToken);

            _logger.Log(
                $"Environment variables restored: {result.Restored} written ({result.Merged} merged), {result.Skipped} skipped, {result.Failed} failed",
                result.Failed > 0 ? LogLevel.Warning : LogLevel.Info);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Log($"Error restoring environment variables: {ex.Message}", LogLevel.Error);
            throw;
        }
    }

    public EnvironmentRestoreResult ApplyVariables(
        IReadOnlyList<EnvironmentVariableEntry> variables,
        IEnvironmentVariableStore store,
        CancellationToken cancellationToken = default)
    {
        var result = new EnvironmentRestoreResult { Total = variables.Count };

        foreach (var variable in variables)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsRestorableName(variable.Name))
            {
                result.Skipped++;
                _logger.Log($"Skipped environment variable with unusable name '{variable.Name}'", LogLevel.Warning);
                continue;
            }

            // PATHEXT is merged below, so it is exempt from the system-managed skip list on restore.
            if (!variable.Name.Equals("PATHEXT", StringComparison.OrdinalIgnoreCase)
                && ShouldSkipVariable(variable.Name, variable.Target))
            {
                result.Skipped++;
                _logger.Log($"Skipped system-managed environment variable {variable.Name}", LogLevel.Warning);
                continue;
            }

            try
            {
                var kind = EnvironmentVariableKinds.Normalize(variable.Kind);
                var value = variable.Value;

                if (EnvironmentVariableMerger.IsListVariable(variable.Name, variable.Value))
                {
                    var existing = store.Get(variable.Name, variable.Target);
                    if (existing != null)
                    {
                        value = EnvironmentVariableMerger.Merge(existing.Value, variable.Value);
                        // Keep expand-type if either side was; dropping it would break %VAR% entries.
                        if (existing.Kind == EnvironmentVariableKinds.ExpandString)
                        {
                            kind = EnvironmentVariableKinds.ExpandString;
                        }

                        result.Merged++;
                    }
                    else
                    {
                        value = EnvironmentVariableMerger.Merge(null, variable.Value);
                    }
                }

                store.Set(variable.Name, value, variable.Target, kind);
                result.Restored++;
                _logger.Log($"Restored {variable.TargetName} variable: {variable.Name}", LogLevel.Debug);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var reason = ex is UnauthorizedAccessException or System.Security.SecurityException
                    ? $"{ex.Message} (administrator rights are required for {variable.TargetName} variables)"
                    : ex.Message;
                result.Failures.Add($"{variable.TargetName}:{variable.Name}: {reason}");
                _logger.Log($"Failed to restore {variable.TargetName} variable {variable.Name}: {reason}", LogLevel.Warning);
            }
        }

        if (result.Restored > 0)
        {
            store.NotifyChanged();
        }

        return result;
    }

    internal static bool IsRestorableName(string name)
        => !string.IsNullOrWhiteSpace(name) && !name.Contains('=') && !name.Contains('\0');
}
