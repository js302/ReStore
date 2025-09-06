using ReStore.src.utils;
using ReStore.src.storage;
using ReStore.src.core;
using System.Text.Json;

namespace ReStore.src.backup;

public class SystemBackupManager
{
    private readonly ILogger _logger;
    private readonly SystemProgramDiscovery _programDiscovery;
    private readonly EnvironmentVariablesManager _envManager;
    private readonly IStorage _storage;
    private readonly SystemState _systemState;

    public SystemBackupManager(ILogger logger, IStorage storage, SystemState systemState)
    {
        _logger = logger;
        _storage = storage;
        _systemState = systemState;
        _programDiscovery = new SystemProgramDiscovery(logger);
        _envManager = new EnvironmentVariablesManager(logger);
    }

    public async Task BackupSystemAsync()
    {
        _logger.Log("Starting full system backup...", LogLevel.Info);
        
        try
        {
            await BackupInstalledProgramsAsync();
            await BackupEnvironmentVariablesAsync();
            
            _logger.Log("System backup completed successfully", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _logger.Log($"System backup failed: {ex.Message}", LogLevel.Error);
            throw;
        }
    }

    public async Task BackupInstalledProgramsAsync()
    {
        _logger.Log("Backing up installed programs...", LogLevel.Info);
        
        try
        {
            var programs = await _programDiscovery.GetAllInstalledProgramsAsync();
            
            var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            var tempDir = Path.Combine(Path.GetTempPath(), "ReStore_SystemBackup", timestamp);
            Directory.CreateDirectory(tempDir);

            // Export to JSON
            var jsonPath = Path.Combine(tempDir, "installed_programs.json");
            await _programDiscovery.ExportProgramsToJsonAsync(programs, jsonPath);

            // Create winget restore script
            await CreateWingetRestoreScriptAsync(programs, Path.Combine(tempDir, "restore_winget_programs.ps1"));

            // Create manual install list
            await CreateManualInstallListAsync(programs, Path.Combine(tempDir, "manual_install_list.txt"));

            // Create full restore script
            await CreateFullRestoreScriptAsync(programs, Path.Combine(tempDir, "restore_programs.ps1"));

            // Upload to storage
            var remotePath = $"system_backups/programs/programs_backup_{timestamp}.zip";
            var zipPath = Path.Combine(Path.GetTempPath(), $"programs_backup_{timestamp}.zip");
            
            var compressionUtil = new CompressionUtil();
            var filesToCompress = Directory.GetFiles(tempDir).ToList();
            await compressionUtil.CompressFilesAsync(filesToCompress, tempDir, zipPath);

            await _storage.UploadAsync(zipPath, remotePath);

            // Update system state
            _systemState.AddBackup("system_programs", remotePath, false);

            // Cleanup
            File.Delete(zipPath);
            Directory.Delete(tempDir, true);

            _logger.Log($"Programs backup completed: {programs.Count} programs backed up to {remotePath}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to backup installed programs: {ex.Message}", LogLevel.Error);
            throw;
        }
    }

    public async Task BackupEnvironmentVariablesAsync()
    {
        _logger.Log("Backing up environment variables...", LogLevel.Info);
        
        try
        {
            var variables = await _envManager.GetAllEnvironmentVariablesAsync();
            
            var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            var tempDir = Path.Combine(Path.GetTempPath(), "ReStore_SystemBackup", timestamp);
            Directory.CreateDirectory(tempDir);

            // Export to JSON
            var jsonPath = Path.Combine(tempDir, "environment_variables.json");
            await _envManager.ExportEnvironmentVariablesToJsonAsync(variables, jsonPath);

            // Create restore script
            var scriptPath = Path.Combine(tempDir, "restore_environment_variables.ps1");
            await _envManager.CreateRestoreScriptAsync(variables, scriptPath);

            // Create registry backup script
            await CreateRegistryBackupScriptAsync(Path.Combine(tempDir, "backup_env_registry.ps1"));

            // Upload to storage
            var remotePath = $"system_backups/environment/env_backup_{timestamp}.zip";
            var zipPath = Path.Combine(Path.GetTempPath(), $"env_backup_{timestamp}.zip");
            
            var compressionUtil = new CompressionUtil();
            var filesToCompress = Directory.GetFiles(tempDir).ToList();
            await compressionUtil.CompressFilesAsync(filesToCompress, tempDir, zipPath);

            await _storage.UploadAsync(zipPath, remotePath);

            // Update system state
            _systemState.AddBackup("system_environment", remotePath, false);

            // Cleanup
            File.Delete(zipPath);
            Directory.Delete(tempDir, true);

            _logger.Log($"Environment variables backup completed: {variables.Count} variables backed up to {remotePath}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to backup environment variables: {ex.Message}", LogLevel.Error);
            throw;
        }
    }

    private async Task CreateWingetRestoreScriptAsync(List<InstalledProgram> programs, string outputPath)
    {
        var wingetPrograms = programs.Where(p => p.IsWingetAvailable && !string.IsNullOrEmpty(p.WingetId)).ToList();
        
        var scriptContent = new List<string>
        {
            "# ReStore Winget Programs Restore Script",
            "# Generated on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            "",
            "Write-Host 'Starting winget programs restore...' -ForegroundColor Green",
            "Write-Host 'This will install programs that are available via winget.' -ForegroundColor Yellow",
            "",
            "$installedCount = 0",
            "$failedCount = 0",
            "$skippedCount = 0",
            ""
        };

        foreach (var program in wingetPrograms)
        {
            scriptContent.AddRange(new[]
            {
                $"# Installing: {program.Name}",
                $"Write-Host 'Installing {program.Name}...' -ForegroundColor Cyan",
                "try {",
                $"    winget install --id {program.WingetId} --silent --accept-source-agreements --accept-package-agreements",
                "    if ($LASTEXITCODE -eq 0) {",
                $"        Write-Host 'Successfully installed {program.Name}' -ForegroundColor Green",
                "        $installedCount++",
                "    } else {",
                $"        Write-Host 'Failed to install {program.Name} (Exit code: $LASTEXITCODE)' -ForegroundColor Red",
                "        $failedCount++",
                "    }",
                "} catch {",
                $"    Write-Host 'Error installing {program.Name}: $($_.Exception.Message)' -ForegroundColor Red",
                "    $failedCount++",
                "}",
                ""
            });
        }

        scriptContent.AddRange(new[]
        {
            "Write-Host 'Winget restore completed!' -ForegroundColor Green",
            "Write-Host \"Installed: $installedCount\" -ForegroundColor Green",
            "Write-Host \"Failed: $failedCount\" -ForegroundColor Red",
            "Write-Host \"Total winget-available programs: " + wingetPrograms.Count + "\" -ForegroundColor Yellow"
        });

        await File.WriteAllTextAsync(outputPath, string.Join(Environment.NewLine, scriptContent));
    }

    private async Task CreateManualInstallListAsync(List<InstalledProgram> programs, string outputPath)
    {
        var manualPrograms = programs.Where(p => !p.IsWingetAvailable).ToList();
        
        var content = new List<string>
        {
            "# Programs that need manual installation",
            "# These programs were not found in winget and need to be installed manually",
            "# Generated on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            "",
            $"Total programs requiring manual installation: {manualPrograms.Count}",
            "",
            "Program Name | Version | Publisher | Install Location",
            "-------------|---------|-----------|------------------"
        };

        foreach (var program in manualPrograms.OrderBy(p => p.Name))
        {
            content.Add($"{program.Name} | {program.Version} | {program.Publisher} | {program.InstallLocation}");
        }

        content.AddRange(new[]
        {
            "",
            "Note: Search for these programs online or check if they have newer versions available.",
            "Some programs might now be available in winget - try searching with:",
            "winget search \"<program name>\""
        });

        await File.WriteAllTextAsync(outputPath, string.Join(Environment.NewLine, content));
    }

    private async Task CreateFullRestoreScriptAsync(List<InstalledProgram> programs, string outputPath)
    {
        var scriptContent = new List<string>
        {
            "# ReStore All Programs Restore Script",
            "# Generated on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            "",
            "param(",
            "    [switch]$WingetOnly = $false,",
            "    [switch]$SkipConfirmation = $false",
            ")",
            "",
            "Write-Host 'ReStore Programs Restore' -ForegroundColor Green",
            "Write-Host '=========================' -ForegroundColor Green",
            "",
            "if (-not $SkipConfirmation) {",
            "    $response = Read-Host 'This will attempt to install all backed up programs. Continue? (y/N)'",
            "    if ($response -ne 'y' -and $response -ne 'Y') {",
            "        Write-Host 'Restore cancelled by user.' -ForegroundColor Yellow",
            "        exit 0",
            "    }",
            "}",
            "",
            "# Check if winget is available",
            "try {",
            "    winget --version | Out-Null",
            "    $wingetAvailable = $true",
            "    Write-Host 'Winget is available' -ForegroundColor Green",
            "} catch {",
            "    $wingetAvailable = $false",
            "    Write-Host 'Winget is not available' -ForegroundColor Red",
            "}",
            "",
            "$installedCount = 0",
            "$failedCount = 0",
            "$skippedCount = 0",
            ""
        };

        var wingetPrograms = programs.Where(p => p.IsWingetAvailable && !string.IsNullOrEmpty(p.WingetId)).ToList();
        var manualPrograms = programs.Where(p => !p.IsWingetAvailable).ToList();

        // Add winget section
        scriptContent.AddRange(new[]
        {
            "# Installing programs via winget",
            "if ($wingetAvailable) {",
            $"    Write-Host 'Installing {wingetPrograms.Count} programs via winget...' -ForegroundColor Cyan",
            ""
        });

        foreach (var program in wingetPrograms)
        {
            scriptContent.AddRange(new[]
            {
                $"    Write-Host 'Installing {program.Name}...' -ForegroundColor Yellow",
                "    try {",
                $"        winget install --id {program.WingetId} --silent --accept-source-agreements --accept-package-agreements",
                "        if ($LASTEXITCODE -eq 0) {",
                $"            Write-Host 'Successfully installed {program.Name}' -ForegroundColor Green",
                "            $installedCount++",
                "        } else {",
                $"            Write-Host 'Failed to install {program.Name}' -ForegroundColor Red",
                "            $failedCount++",
                "        }",
                "    } catch {",
                $"        Write-Host 'Error installing {program.Name}: $($_.Exception.Message)' -ForegroundColor Red",
                "        $failedCount++",
                "    }",
                ""
            });
        }

        scriptContent.AddRange(new[]
        {
            "} else {",
            "    Write-Host 'Skipping winget installations (winget not available)' -ForegroundColor Yellow",
            $"    $skippedCount += {wingetPrograms.Count}",
            "}",
            "",
            "# Manual installation required",
            "if (-not $WingetOnly) {",
            $"    Write-Host 'The following {manualPrograms.Count} programs require manual installation:' -ForegroundColor Yellow",
            "    Write-Host '================================================================' -ForegroundColor Yellow"
        });

        foreach (var program in manualPrograms.Take(20)) // Limit output
        {
            scriptContent.Add($"    Write-Host '- {program.Name} (v{program.Version}) by {program.Publisher}' -ForegroundColor White");
        }

        if (manualPrograms.Count > 20)
        {
            scriptContent.Add($"    Write-Host '... and {manualPrograms.Count - 20} more (see manual_install_list.txt)' -ForegroundColor Gray");
        }

        scriptContent.AddRange(new[]
        {
            "    Write-Host 'Check manual_install_list.txt for complete list with details.' -ForegroundColor Yellow",
            "}",
            "",
            "# Summary",
            "Write-Host 'Restore Summary:' -ForegroundColor Green",
            "Write-Host \"Successfully installed: $installedCount\" -ForegroundColor Green",
            "Write-Host \"Failed to install: $failedCount\" -ForegroundColor Red",
            "Write-Host \"Skipped: $skippedCount\" -ForegroundColor Yellow",
            $"Write-Host \"Manual installation required: {manualPrograms.Count}\" -ForegroundColor Yellow"
        });

        await File.WriteAllTextAsync(outputPath, string.Join(Environment.NewLine, scriptContent));
    }

    private async Task CreateRegistryBackupScriptAsync(string outputPath)
    {
        var scriptContent = new List<string>
        {
            "# ReStore Registry Environment Variables Backup Script",
            "# This creates a backup of environment variables stored in the registry",
            "# Generated on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            "",
            "$timestamp = Get-Date -Format 'yyyyMMddHHmmss'",
            "$backupDir = \"$env:TEMP\\ReStore_RegistryBackup_$timestamp\"",
            "New-Item -ItemType Directory -Path $backupDir -Force | Out-Null",
            "",
            "Write-Host 'Backing up environment variables from registry...' -ForegroundColor Green",
            "",
            "# Backup system environment variables",
            "$systemEnvPath = 'HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment'",
            "$systemBackupFile = \"$backupDir\\system_environment.reg\"",
            "reg export \"$systemEnvPath\" \"$systemBackupFile\" /y",
            "",
            "# Backup user environment variables",
            "$userEnvPath = 'HKCU\\Environment'",
            "$userBackupFile = \"$backupDir\\user_environment.reg\"",
            "reg export \"$userEnvPath\" \"$userBackupFile\" /y",
            "",
            "Write-Host 'Registry backup completed:' -ForegroundColor Green",
            "Write-Host \"System variables: $systemBackupFile\" -ForegroundColor White",
            "Write-Host \"User variables: $userBackupFile\" -ForegroundColor White",
            "Write-Host \"Backup directory: $backupDir\" -ForegroundColor White",
            "",
            "# Note: To restore, use: reg import <file.reg>"
        };

        await File.WriteAllTextAsync(outputPath, string.Join(Environment.NewLine, scriptContent));
    }

    public async Task RestoreSystemAsync(string backupType, string backupPath)
    {
        _logger.Log($"Starting system restore of {backupType} from {backupPath}...", LogLevel.Info);
        
        try
        {
            // Download backup
            var tempDir = Path.Combine(Path.GetTempPath(), "ReStore_SystemRestore", DateTime.Now.ToString("yyyyMMddHHmmss"));
            Directory.CreateDirectory(tempDir);
            
            var zipPath = Path.Combine(tempDir, "backup.zip");
            await _storage.DownloadAsync(backupPath, zipPath);
            
            // Extract backup
            var extractDir = Path.Combine(tempDir, "extracted");
            var compressionUtil = new CompressionUtil();
            await compressionUtil.DecompressAsync(zipPath, extractDir);
            
            if (backupType == "system_programs")
            {
                await RestoreProgramsAsync(extractDir);
            }
            else if (backupType == "system_environment")
            {
                await RestoreEnvironmentVariablesAsync(extractDir);
            }
            
            // Cleanup
            Directory.Delete(tempDir, true);
            
            _logger.Log($"System restore of {backupType} completed successfully", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _logger.Log($"System restore failed: {ex.Message}", LogLevel.Error);
            throw;
        }
    }

    private Task RestoreProgramsAsync(string extractDir)
    {
        var scriptPath = Path.Combine(extractDir, "restore_programs.ps1");
        if (File.Exists(scriptPath))
        {
            _logger.Log("Programs restore script found. Please run it manually with appropriate permissions.", LogLevel.Info);
            _logger.Log($"Script location: {scriptPath}", LogLevel.Info);
        }
        
        var jsonPath = Path.Combine(extractDir, "installed_programs.json");
        if (File.Exists(jsonPath))
        {
            _logger.Log($"Programs backup data available at: {jsonPath}", LogLevel.Info);
        }
        
        return Task.CompletedTask;
    }

    private async Task RestoreEnvironmentVariablesAsync(string extractDir)
    {
        var jsonPath = Path.Combine(extractDir, "environment_variables.json");
        if (File.Exists(jsonPath))
        {
            await _envManager.RestoreEnvironmentVariablesAsync(jsonPath);
        }
        
        var scriptPath = Path.Combine(extractDir, "restore_environment_variables.ps1");
        if (File.Exists(scriptPath))
        {
            _logger.Log("Environment variables restore script available for manual execution.", LogLevel.Info);
            _logger.Log($"Script location: {scriptPath}", LogLevel.Info);
        }
    }
}
