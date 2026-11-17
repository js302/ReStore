using ReStore.Core.src.utils;
using ReStore.Core.src.storage;
using ReStore.Core.src.core;
using System.Runtime.Versioning;

namespace ReStore.Core.src.backup;

[SupportedOSPlatform("windows")]
public class SystemBackupManager
{
    private readonly ILogger _logger;
    private readonly SystemProgramDiscovery _programDiscovery;
    private readonly EnvironmentVariablesManager _envManager;
    private readonly WindowsSettingsManager _settingsManager;
    private readonly IConfigManager _config;
    private readonly SystemState _systemState;
    private readonly IPasswordProvider? _passwordProvider;
    private readonly RetentionManager _retentionManager;
    private readonly RepositoryCoordinator _coordinator;

    public SystemBackupManager(
        ILogger logger,
        IConfigManager config,
        SystemState systemState,
        IPasswordProvider? passwordProvider = null,
        RepositoryCoordinator? coordinator = null)
    {
        _coordinator = coordinator ?? RepositoryCoordinator.Instance;
        _logger = logger;
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _systemState = systemState;
        _programDiscovery = new SystemProgramDiscovery(logger);
        _envManager = new EnvironmentVariablesManager(logger);
        _settingsManager = new WindowsSettingsManager(logger);
        _passwordProvider = passwordProvider;
        _retentionManager = new RetentionManager(_logger, _config, _systemState, _coordinator);
    }

    /// <summary>
    /// When true (scheduler), a backup that finds the repository busy throws
    /// <see cref="RepositoryBusyException"/> immediately instead of waiting.
    /// </summary>
    public bool FailFastWhenBusy { get; init; }

    /// <summary>Machine access for restore planning, undo points and applying. Null uses the real registry/winget; tests inject fakes.</summary>
    public SystemRestoreServices? RestoreServices { get; init; }

    private SystemRestoreServices EffectiveRestoreServices => RestoreServices ?? (_defaultRestoreServices ??= SystemRestoreServices.CreateDefault(_logger));
    private SystemRestoreServices? _defaultRestoreServices;

    /// <summary>When set (CLI <c>--storage</c>), every component backs up to this storage type instead of the configured one.</summary>
    public string? StorageTypeOverride { get; init; }

    // Every system backup holds the mutation lease for its whole run. The lease is taken here, at the
    // engine layer (like Backup), so the public entry points below are the only places that acquire it;
    // the private *Core methods receive the held lease and must not acquire again.
    private async Task<RepositoryLease> AcquireMutationLeaseAsync(string reason, CancellationToken cancellationToken = default)
    {
        // Resolve (and if needed prompt for) the password before taking the lease: a modal dialog must not hold the repository.
        if (_config.Encryption?.Enabled == true)
        {
            await PasswordPrefetch.WarmAsync(_passwordProvider, PasswordPurpose.Encrypt).ConfigureAwait(false);
        }

        if (!FailFastWhenBusy)
        {
            return await _coordinator.AcquireMutationAsync(reason, cancellationToken, _systemState).ConfigureAwait(false);
        }

        return await _coordinator.TryAcquireMutationAsync(TimeSpan.Zero, reason, cancellationToken, _systemState).ConfigureAwait(false)
            ?? throw new RepositoryBusyException(
                _coordinator.CurrentReason is { } current
                    ? $"Another operation is running: {current}"
                    : "Another ReStore operation is running on this repository.");
    }

    /// <summary>Upper bound for one component backup (discovery, archive, upload).</summary>
    public TimeSpan ComponentTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Backs up every enabled component. A failing component does not stop the others; after all were attempted a
    /// <see cref="SystemBackupException"/> carrying the per-component results is thrown if any failed.
    /// </summary>
    public async Task<SystemBackupResult> BackupSystemAsync(CancellationToken cancellationToken = default)
    {
        // The lease spans every component and the aggregate failure below, and is released on all paths.
        var lease = await AcquireMutationLeaseAsync("System backup", cancellationToken).ConfigureAwait(false);
        await using var leaseScope = lease.ConfigureAwait(false);
        using var activity = ActivityRecorder.Begin(_systemState, ActivityType.SystemBackup, "System backup", cancellationToken);
        try
        {
            var backupResult = await BackupSystemCoreAsync(lease, cancellationToken).ConfigureAwait(false);
            activity.Succeeded($"{backupResult.Components.Count} component(s) backed up");
            return backupResult;
        }
        catch (SystemBackupException ex)
        {
            activity.Failed(ex.Message);
            throw;
        }
    }

    private async Task<SystemBackupResult> BackupSystemCoreAsync(RepositoryLease lease, CancellationToken cancellationToken)
    {
        _logger.Log("Starting full system backup...", LogLevel.Info);

        var result = new SystemBackupResult();

        await RunEnabledComponentAsync(result, "programs", _config.SystemBackup.IncludePrograms,
            "Skipping programs backup (disabled in config)", ct => BackupInstalledProgramsCoreAsync(lease, ct), cancellationToken).ConfigureAwait(false);
        await RunEnabledComponentAsync(result, "environment", _config.SystemBackup.IncludeEnvironmentVariables,
            "Skipping environment variables backup (disabled in config)", ct => BackupEnvironmentVariablesCoreAsync(lease, ct), cancellationToken).ConfigureAwait(false);
        await RunEnabledComponentAsync(result, "settings", _config.SystemBackup.IncludeWindowsSettings,
            "Skipping Windows settings backup (disabled in config)", ct => BackupWindowsSettingsCoreAsync(lease, ct), cancellationToken).ConfigureAwait(false);
        await RunEnabledComponentAsync(result, "devtools", _config.SystemBackup.IncludeDevTools,
            "Skipping developer tools backup (disabled in config)", ct => BackupDevToolsCoreAsync(lease, ct), cancellationToken).ConfigureAwait(false);

        if (result.HasFailures)
        {
            var failure = new SystemBackupException(result);
            _logger.Log(failure.Message, LogLevel.Error);
            throw failure;
        }

        _logger.Log("System backup completed successfully", LogLevel.Info);
        return result;
    }

    private async Task RunEnabledComponentAsync(
        SystemBackupResult result,
        string component,
        bool enabled,
        string skipMessage,
        Func<CancellationToken, Task> backup,
        CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            _logger.Log(skipMessage, LogLevel.Info);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await backup(cancellationToken).ConfigureAwait(false);
            result.Components.Add(new ComponentBackupResult(component, true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // RunComponentBackupAsync already logged the details; record and carry on with the next component.
            result.Components.Add(new ComponentBackupResult(component, false, ex.Message));
        }
    }

    private string GetStorageTypeForComponent(string component)
    {
        if (!string.IsNullOrWhiteSpace(StorageTypeOverride))
        {
            return StorageTypeOverride;
        }

        return component.ToLowerInvariant() switch
        {
            "programs" => _config.SystemBackup.ProgramsStorageType
                ?? _config.SystemBackup.StorageType
                ?? _config.GlobalStorageType,
            "environment" => _config.SystemBackup.EnvironmentStorageType
                ?? _config.SystemBackup.StorageType
                ?? _config.GlobalStorageType,
            "settings" => _config.SystemBackup.SettingsStorageType
                ?? _config.SystemBackup.StorageType
                ?? _config.GlobalStorageType,
            "devtools" => _config.SystemBackup.DevToolsStorageType
                ?? _config.SystemBackup.StorageType
                ?? _config.GlobalStorageType,
            _ => _config.GlobalStorageType
        };
    }

    public async Task BackupInstalledProgramsAsync(CancellationToken cancellationToken = default)
    {
        var lease = await AcquireMutationLeaseAsync("Installed programs backup", cancellationToken).ConfigureAwait(false);
        await using var leaseScope = lease.ConfigureAwait(false);
        await BackupInstalledProgramsCoreAsync(lease, cancellationToken).ConfigureAwait(false);
    }

    private Task BackupInstalledProgramsCoreAsync(RepositoryLease lease, CancellationToken cancellationToken)
    {
        return RunComponentBackupAsync(
            lease,
            new SystemBackupComponent(
                Component: "programs",
                Group: "system_programs",
                FileNamePrefix: "programs_backup",
                RemoteDirectory: "system_backups/programs",
                StartMessage: "Backing up installed programs...",
                FailureMessage: "Failed to backup installed programs"),
            cancellationToken,
            async (tempDir, ct) =>
            {
                var programs = await _programDiscovery.GetAllInstalledProgramsAsync(ct).ConfigureAwait(false);

                await _programDiscovery.ExportProgramsToJsonAsync(programs, Path.Combine(tempDir, "installed_programs.json")).ConfigureAwait(false);
                await CreateWingetRestoreScriptAsync(programs, Path.Combine(tempDir, "restore_winget_programs.ps1")).ConfigureAwait(false);
                await CreateManualInstallListAsync(programs, Path.Combine(tempDir, "manual_install_list.txt")).ConfigureAwait(false);
                await CreateFullRestoreScriptAsync(programs, Path.Combine(tempDir, "restore_programs.ps1")).ConfigureAwait(false);

                return $"{programs.Count} programs";
            });
    }

    public async Task BackupEnvironmentVariablesAsync(CancellationToken cancellationToken = default)
    {
        var lease = await AcquireMutationLeaseAsync("Environment variables backup", cancellationToken).ConfigureAwait(false);
        await using var leaseScope = lease.ConfigureAwait(false);
        await BackupEnvironmentVariablesCoreAsync(lease, cancellationToken).ConfigureAwait(false);
    }

    private Task BackupEnvironmentVariablesCoreAsync(RepositoryLease lease, CancellationToken cancellationToken)
    {
        return RunComponentBackupAsync(
            lease,
            new SystemBackupComponent(
                Component: "environment",
                Group: "system_environment",
                FileNamePrefix: "env_backup",
                RemoteDirectory: "system_backups/environment",
                StartMessage: "Backing up environment variables...",
                FailureMessage: "Failed to backup environment variables"),
            cancellationToken,
            async (tempDir, ct) =>
            {
                var variables = await _envManager.GetAllEnvironmentVariablesAsync().ConfigureAwait(false);

                await _envManager.ExportEnvironmentVariablesToJsonAsync(variables, Path.Combine(tempDir, "environment_variables.json")).ConfigureAwait(false);
                await _envManager.CreateRestoreScriptAsync(variables, Path.Combine(tempDir, "restore_environment_variables.ps1")).ConfigureAwait(false);
                await CreateRegistryBackupScriptAsync(Path.Combine(tempDir, "backup_env_registry.ps1")).ConfigureAwait(false);

                return $"{variables.Count} variables";
            });
    }

    public async Task BackupWindowsSettingsAsync(CancellationToken cancellationToken = default)
    {
        var lease = await AcquireMutationLeaseAsync("Windows settings backup", cancellationToken).ConfigureAwait(false);
        await using var leaseScope = lease.ConfigureAwait(false);
        await BackupWindowsSettingsCoreAsync(lease, cancellationToken).ConfigureAwait(false);
    }

    private Task BackupWindowsSettingsCoreAsync(RepositoryLease lease, CancellationToken cancellationToken)
    {
        return RunComponentBackupAsync(
            lease,
            new SystemBackupComponent(
                Component: "settings",
                Group: "system_settings",
                FileNamePrefix: "settings_backup",
                RemoteDirectory: "system_backups/settings",
                StartMessage: "Backing up Windows settings...",
                FailureMessage: "Failed to backup Windows settings"),
            cancellationToken,
            async (tempDir, ct) =>
            {
                var export = await _settingsManager.ExportWindowsSettingsAsync(tempDir, null, ct).ConfigureAwait(false);

                var scriptPath = Path.Combine(tempDir, "restore_windows_settings.ps1");
                await _settingsManager.CreateRestoreScriptAsync(export, tempDir, scriptPath).ConfigureAwait(false);

                return $"{export.ExportedCategories.Count} categories";
            });
    }

    public async Task BackupDevToolsAsync(CancellationToken cancellationToken = default)
    {
        var lease = await AcquireMutationLeaseAsync("Developer tools backup", cancellationToken).ConfigureAwait(false);
        await using var leaseScope = lease.ConfigureAwait(false);
        await BackupDevToolsCoreAsync(lease, cancellationToken).ConfigureAwait(false);
    }

    private Task BackupDevToolsCoreAsync(RepositoryLease lease, CancellationToken cancellationToken)
    {
        return RunComponentBackupAsync(
            lease,
            new SystemBackupComponent(
                Component: "devtools",
                Group: "system_devtools",
                FileNamePrefix: "devtools_backup",
                RemoteDirectory: "system_backups/devtools",
                StartMessage: "Backing up developer tools settings...",
                FailureMessage: "Failed to backup developer tools settings"),
            cancellationToken,
            (tempDir, ct) => DevToolsBackup.StageAsync(
                EffectiveRestoreServices.DevTools ?? throw new InvalidOperationException("No developer-tools access is configured."),
                _config.SystemBackup.IncludeSshConfig, tempDir, _logger, ct));
    }

    /// <summary>Naming and storage conventions for one system backup component.</summary>
    private sealed record SystemBackupComponent(
        string Component,
        string Group,
        string FileNamePrefix,
        string RemoteDirectory,
        string StartMessage,
        string FailureMessage);

    /// <summary>
    /// Shared pipeline: resolve storage, stage payload, archive, optionally encrypt, upload,
    /// record history, apply retention, and always clean up temp files.
    /// </summary>
    /// <param name="writePayloadAsync">
    /// Writes files into the staging directory and returns a short summary for the log line.
    /// </param>
    private async Task RunComponentBackupAsync(
        RepositoryLease lease,
        SystemBackupComponent component,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<string>> writePayloadAsync)
    {
        _logger.Log(component.StartMessage, LogLevel.Info);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(ComponentTimeout);
        var token = timeoutSource.Token;

        IStorage? storage = null;
        string? tempDir = null;
        string? zipPath = null;
        string? fileToUpload = null;

        try
        {
            var storageType = GetStorageTypeForComponent(component.Component);
            storage = await _config.CreateStorageAsync(storageType).WaitAsync(token).ConfigureAwait(false);
            _logger.Log($"Using {storageType} storage for {component.Component} backup", LogLevel.Info);

            var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var uniqueId = Guid.NewGuid().ToString("N");
            tempDir = Path.Combine(Path.GetTempPath(), "ReStore_SystemBackup", $"{timestamp}_{uniqueId}");
            Directory.CreateDirectory(tempDir);

            var payloadSummary = await writePayloadAsync(tempDir, token).ConfigureAwait(false);

            var remotePath = $"{component.RemoteDirectory}/{component.FileNamePrefix}_{uniqueId}_{timestamp}.zip";
            zipPath = Path.Combine(Path.GetTempPath(), $"{component.FileNamePrefix}_{uniqueId}_{timestamp}.zip");

            // Recursive so payload writers may use subdirectories.
            var filesToCompress = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories).ToList();
            token.ThrowIfCancellationRequested();
            await CompressionUtil.CompressFilesAsync(filesToCompress, tempDir, zipPath).ConfigureAwait(false);

            fileToUpload = zipPath;
            if (_config.Encryption.Enabled)
            {
                EnsureEncryptionProviderAvailable();
                _logger.Log($"Encrypting {component.Component} backup...", LogLevel.Info);

                var password = await _passwordProvider!.GetPasswordAsync(PasswordPurpose.Encrypt).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(password))
                {
                    throw new InvalidOperationException("Encryption is enabled but no password was provided");
                }

                var encryptedPath = await CompressionUtil.CompressAndEncryptAsync(
                    zipPath,
                    password,
                    _config.Encryption.Salt!,
                    _logger,
                    _config.Encryption.KeyDerivationIterations).ConfigureAwait(false);

                fileToUpload = encryptedPath;
                remotePath = remotePath.Replace(".zip", ".zip.enc");

                var remoteMetadataPath = remotePath + ".meta";
                await storage.UploadAsync(encryptedPath + ".meta", remoteMetadataPath).WaitAsync(token).ConfigureAwait(false);
                _logger.Log($"Uploaded encryption metadata: {remoteMetadataPath}", LogLevel.Debug);
            }

            await storage.UploadAsync(fileToUpload, remotePath).WaitAsync(token).ConfigureAwait(false);

            var backupSize = new FileInfo(fileToUpload).Length;
            _systemState.AddBackup(component.Group, remotePath, false, storageType, backupSize);
            try
            {
                await _retentionManager.ApplyGroupAsync(component.Group, lease).ConfigureAwait(false);
            }
            catch (RepositoryBusyException ex)
            {
                _logger.Log($"Retention deferred: {ex.Message}", LogLevel.Warning);
            }
            await _systemState.SaveStateAsync().ConfigureAwait(false);

            _logger.Log(
                $"{component.Component} backup completed: {payloadSummary} backed up to {remotePath}",
                LogLevel.Info);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var message = $"{component.FailureMessage}: timed out after {ComponentTimeout.TotalMinutes:0.#} minutes";
            _logger.Log(message, LogLevel.Error);
            throw new TimeoutException(message);
        }
        catch (Exception ex)
        {
            _logger.Log($"{component.FailureMessage}: {ex.Message}", LogLevel.Error);
            throw;
        }
        finally
        {
            CleanupBackupTempArtifacts(tempDir, zipPath, fileToUpload);
            storage?.Dispose();
        }
    }

    private async Task CreateWingetRestoreScriptAsync(List<InstalledProgram> programs, string outputPath)
    {
        var wingetPrograms = programs.Where(p => p.IsWingetAvailable && WingetHelper.IsValidId(p.WingetId)).ToList();

        var scriptContent = new List<string>
        {
            "# ReStore Winget Programs Restore Script",
            "# Generated on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
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
            var safeName = ScriptFile.EscapeSingleQuotedLabel(program.Name);
            scriptContent.AddRange(new[]
            {
                $"# Installing: {safeName}",
                $"Write-Host 'Installing {safeName}...' -ForegroundColor Cyan",
                "try {",
                $"    winget install --exact --id {program.WingetId} --silent --accept-source-agreements --accept-package-agreements",
                "    if ($LASTEXITCODE -eq 0) {",
                $"        Write-Host 'Successfully installed {safeName}' -ForegroundColor Green",
                "        $installedCount++",
                "    } else {",
                $"        Write-Host 'Failed to install {safeName} (Exit code: $LASTEXITCODE)' -ForegroundColor Red",
                "        $failedCount++",
                "    }",
                "} catch {",
                $"    Write-Host 'Error installing {safeName}: $($_.Exception.Message)' -ForegroundColor Red",
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

        await ScriptFile.WriteAllTextAsync(outputPath, string.Join(Environment.NewLine, scriptContent)).ConfigureAwait(false);
    }

    private async Task CreateManualInstallListAsync(List<InstalledProgram> programs, string outputPath)
    {
        var manualPrograms = programs.Where(p => !p.IsWingetAvailable).ToList();

        var content = new List<string>
        {
            "# Programs that need manual installation",
            "# These programs were not found in winget and need to be installed manually",
            "# Generated on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
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

        await ScriptFile.WriteAllTextAsync(outputPath, string.Join(Environment.NewLine, content)).ConfigureAwait(false);
    }

    private async Task CreateFullRestoreScriptAsync(List<InstalledProgram> programs, string outputPath)
    {
        var scriptContent = new List<string>
        {
            "# ReStore All Programs Restore Script",
            "# Generated on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
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

        var wingetPrograms = programs.Where(p => p.IsWingetAvailable && WingetHelper.IsValidId(p.WingetId)).ToList();
        var manualPrograms = programs.Where(p => !p.IsWingetAvailable).ToList();

        scriptContent.AddRange(new[]
        {
            "# Installing programs via winget",
            "if ($wingetAvailable) {",
            $"    Write-Host 'Installing {wingetPrograms.Count} programs via winget...' -ForegroundColor Cyan",
            ""
        });

        foreach (var program in wingetPrograms)
        {
            var safeName = ScriptFile.EscapeSingleQuotedLabel(program.Name);
            scriptContent.AddRange(new[]
            {
                $"    Write-Host 'Installing {safeName}...' -ForegroundColor Yellow",
                "    try {",
                $"        winget install --exact --id {program.WingetId} --silent --accept-source-agreements --accept-package-agreements",
                "        if ($LASTEXITCODE -eq 0) {",
                $"            Write-Host 'Successfully installed {safeName}' -ForegroundColor Green",
                "            $installedCount++",
                "        } else {",
                $"            Write-Host 'Failed to install {safeName}' -ForegroundColor Red",
                "            $failedCount++",
                "        }",
                "    } catch {",
                $"        Write-Host 'Error installing {safeName}: $($_.Exception.Message)' -ForegroundColor Red",
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
            scriptContent.Add($"    Write-Host '- {ScriptFile.EscapeSingleQuotedLabel(program.Name)} (v{ScriptFile.EscapeSingleQuotedLabel(program.Version)}) by {ScriptFile.EscapeSingleQuotedLabel(program.Publisher)}' -ForegroundColor White");
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

        await ScriptFile.WriteAllTextAsync(outputPath, string.Join(Environment.NewLine, scriptContent)).ConfigureAwait(false);
    }

    private async Task CreateRegistryBackupScriptAsync(string outputPath)
    {
        var scriptContent = new List<string>
        {
            "# ReStore Registry Environment Variables Backup Script",
            "# This creates a backup of environment variables stored in the registry",
            "# Generated on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
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

        await ScriptFile.WriteAllTextAsync(outputPath, string.Join(Environment.NewLine, scriptContent)).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads, decrypts and extracts a system backup into a private work directory, keeps a durable copy of the
    /// files, then applies the data (never the archived scripts). Storage is resolved from config.
    /// </summary>
    public async Task<SystemRestoreResult> RestoreSystemAsync(
        string backupType,
        string backupPath,
        string? storageTypeOverride = null,
        SystemRestoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (backupPath.EndsWith(".enc", StringComparison.OrdinalIgnoreCase))
        {
            await PasswordPrefetch.WarmAsync(_passwordProvider, PasswordPurpose.Decrypt).ConfigureAwait(false);
        }

        var readLease = await _coordinator.AcquireReadAsync($"System restore of {backupType}", cancellationToken).ConfigureAwait(false);
        await using var readLeaseScope = readLease.ConfigureAwait(false);
        using var activity = ActivityRecorder.Begin(_systemState, ActivityType.SystemRestore, $"{backupType}: {backupPath}", cancellationToken);
        _logger.Log($"Starting system restore of {backupType} from {backupPath}...", LogLevel.Info);

        IStorage? storage = null;
        try
        {
            var component = ResolveRestoreComponent(backupType, backupPath);

            var storageType = storageTypeOverride ?? GetStorageTypeForComponent(component);
            storage = await _config.CreateStorageAsync(storageType).ConfigureAwait(false);
            _logger.Log($"Using {storageType} storage for restore", LogLevel.Info);

            var restored = await RestoreFromStorageAsync(storage, component, backupType, options, backupPath, cancellationToken).ConfigureAwait(false);
            RecordRestoreActivity(activity, restored);
            return restored;
        }
        catch (OperationCanceledException)
        {
            _logger.Log("System restore cancelled", LogLevel.Info);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Log($"System restore failed: {ex.Message}", LogLevel.Error);
            activity.Failed(ex.Message);
            throw;
        }
        finally
        {
            storage?.Dispose();
        }
    }

    /// <summary>Same pipeline with a caller-supplied storage (the GUI); the storage is not disposed.</summary>
    public async Task<SystemRestoreResult> RestoreSystemAsync(
        IStorage storage,
        string backupType,
        string backupPath,
        SystemRestoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (backupPath.EndsWith(".enc", StringComparison.OrdinalIgnoreCase))
        {
            await PasswordPrefetch.WarmAsync(_passwordProvider, PasswordPurpose.Decrypt).ConfigureAwait(false);
        }

        var readLease = await _coordinator.AcquireReadAsync($"System restore of {backupType}", cancellationToken).ConfigureAwait(false);
        await using var readLeaseScope = readLease.ConfigureAwait(false);
        using var activity = ActivityRecorder.Begin(_systemState, ActivityType.SystemRestore, $"{backupType}: {backupPath}", cancellationToken);
        _logger.Log($"Starting system restore of {backupType} from {backupPath}...", LogLevel.Info);

        try
        {
            var component = ResolveRestoreComponent(backupType, backupPath);
            var restored = await RestoreFromStorageAsync(storage, component, backupType, options, backupPath, cancellationToken).ConfigureAwait(false);
            RecordRestoreActivity(activity, restored);
            return restored;
        }
        catch (OperationCanceledException)
        {
            _logger.Log("System restore cancelled", LogLevel.Info);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Log($"System restore failed: {ex.Message}", LogLevel.Error);
            activity.Failed(ex.Message);
            throw;
        }
    }

    private static void RecordRestoreActivity(ActivityScope activity, SystemRestoreResult result)
    {
        var text = $"{result.Succeeded} succeeded, {result.Failed} failed, {result.Skipped} skipped";
        if (result.Failed > 0)
        {
            activity.Warning(text);
        }
        else
        {
            activity.Succeeded(text);
        }
    }

    private async Task<SystemRestoreResult> RestoreFromStorageAsync(
        IStorage storage,
        string component,
        string backupType,
        SystemRestoreOptions? options,
        string backupPath,
        CancellationToken cancellationToken)
    {
        options ??= new SystemRestoreOptions();
        string? workDir = null;
        try
        {
            workDir = CreateRestoreTempDirectory();
            options.Progress?.Report(new SystemRestoreProgress("Restoring backup", "Downloading backup archive..."));

            var extractDir = await DownloadAndExtractRestoreBackupAsync(storage, backupPath, workDir, cancellationToken).ConfigureAwait(false);
            _logger.Log("Extraction complete", LogLevel.Info);

            // The work dir is deleted in finally, so keep the files (and scripts for manual use) somewhere durable.
            // A plan-only run (CLI --plan) leaves nothing behind.
            var durableDir = options.PlanOnly ? null : CopyToDurableRestoreScriptsDirectory(extractDir);
            var result = new SystemRestoreResult(component) { ScriptsFolder = durableDir, SourceBackup = backupPath };

            await RestoreComponentAsync(component, extractDir, durableDir ?? extractDir, options, result, cancellationToken).ConfigureAwait(false);

            _logger.Log($"System restore of {backupType} completed ({result.Succeeded} succeeded, {result.Failed} failed, {result.Skipped} skipped)", LogLevel.Info);
            return result;
        }
        finally
        {
            CleanupRestoreTempDirectory(workDir);
        }
    }

    private static string ResolveRestoreComponent(string backupType, string backupPath)
    {
        var normalizedBackupType = backupType.Trim().ToLowerInvariant();
        var normalizedBackupPath = backupPath.Replace('\\', '/').ToLowerInvariant();
        var component = normalizedBackupType switch
        {
            "system_programs" => "programs",
            "system_environment" => "environment",
            "system_settings" => "settings",
            "system_devtools" => "devtools",
            "all" when normalizedBackupPath.Contains("/programs/") => "programs",
            "all" when normalizedBackupPath.Contains("/environment/") => "environment",
            "all" when normalizedBackupPath.Contains("/settings/") => "settings",
            "all" when normalizedBackupPath.Contains("/devtools/") => "devtools",
            _ => normalizedBackupType
        };

        if (component is not ("programs" or "environment" or "settings" or "devtools"))
        {
            throw new ArgumentException($"Unsupported system restore type: {backupType}. Use programs, environment, settings, or devtools.", nameof(backupType));
        }

        return component;
    }

    private static string CopyToDurableRestoreScriptsDirectory(string extractDir)
    {
        var baseDir = Path.Combine(AppPaths.Root, "restore-scripts");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var target = Path.Combine(baseDir, stamp);
        for (var i = 2; Directory.Exists(target); i++)
        {
            target = Path.Combine(baseDir, $"{stamp}-{i}");
        }

        Directory.CreateDirectory(target);
        foreach (var dir in Directory.GetDirectories(extractDir, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(extractDir, dir)));
        }

        foreach (var file in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(extractDir, file));
            File.Copy(file, destination);
            if (destination.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                ScriptFile.EnsureBom(destination);
            }
        }

        return target;
    }

    private static string CreateRestoreTempDirectory()
    {
        // Private per-restore directory under the user's local app data (never %TEMP%): it holds decrypted archives.
        var workRoot = AppPaths.RestoreWorkDirectory;
        PurgeStaleRestoreWorkDirectories(workRoot);

        var workDir = Path.Combine(workRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        return workDir;
    }

    /// <summary>Removes leftovers of crashed restores so decrypted data does not linger.</summary>
    private static void PurgeStaleRestoreWorkDirectories(string workRoot)
    {
        try
        {
            if (!Directory.Exists(workRoot))
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var dir in Directory.GetDirectories(workRoot))
            {
                if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                {
                    try { Directory.Delete(dir, true); } catch (Exception) { /* in use or locked; try next time */ }
                }
            }
        }
        catch (Exception)
        {
            // Housekeeping only.
        }
    }

    private async Task<string> DownloadAndExtractRestoreBackupAsync(IStorage storage, string backupPath, string tempDir, CancellationToken cancellationToken)
    {
        var isEncrypted = backupPath.EndsWith(".enc", StringComparison.OrdinalIgnoreCase);
        var zipPath = Path.Combine(tempDir, isEncrypted ? "backup.zip.enc" : "backup.zip");
        await storage.DownloadAsync(backupPath, zipPath).WaitAsync(cancellationToken).ConfigureAwait(false);

        var extractDir = Path.Combine(tempDir, "extracted");

        if (isEncrypted)
        {
            await DownloadAndDecryptBackupAsync(storage, backupPath, zipPath, extractDir, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await CompressionUtil.DecompressAsync(zipPath, extractDir).ConfigureAwait(false);
        }

        return extractDir;
    }

    private async Task DownloadAndDecryptBackupAsync(IStorage storage, string backupPath, string zipPath, string extractDir, CancellationToken cancellationToken)
    {
        _logger.Log("Backup is encrypted, decrypting...", LogLevel.Info);
        var metadataPath = backupPath + ".meta";
        var tempMetadataPath = zipPath + ".meta";
        await storage.DownloadAsync(metadataPath, tempMetadataPath).WaitAsync(cancellationToken).ConfigureAwait(false);

        if (_passwordProvider == null)
        {
            throw new InvalidOperationException("Encrypted backup detected but no password provider available");
        }

        var password = await _passwordProvider.GetPasswordAsync(PasswordPurpose.Decrypt).ConfigureAwait(false);
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException("Password required to decrypt backup");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await CompressionUtil.DecryptAndDecompressAsync(zipPath, password, extractDir, _logger).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _passwordProvider.ClearPassword();
            _logger.Log("Decryption failed. Password cleared for retry.", LogLevel.Debug);
            throw new InvalidOperationException($"Failed to decrypt backup: {ex.Message}", ex);
        }
    }

    private async Task RestoreComponentAsync(
        string component,
        string extractDir,
        string scriptsDir,
        SystemRestoreOptions options,
        SystemRestoreResult result,
        CancellationToken cancellationToken)
    {
        if (component == "programs")
        {
            await RestoreProgramsAsync(extractDir, scriptsDir, options, result, cancellationToken).ConfigureAwait(false);
        }
        else if (component == "environment")
        {
            await RestoreEnvironmentVariablesAsync(extractDir, scriptsDir, options, result, cancellationToken).ConfigureAwait(false);
        }
        else if (component == "settings")
        {
            await RestoreWindowsSettingsAsync(extractDir, scriptsDir, options, result, cancellationToken).ConfigureAwait(false);
        }
        else if (component == "devtools")
        {
            await RestoreDevToolsAsync(extractDir, options, result, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> AskAsync(SystemRestoreOptions options, string title, string message, bool isWarning)
        => options.Confirm != null && await options.Confirm(new RestorePrompt(title, message, isWarning)).ConfigureAwait(false);

    private void CleanupRestoreTempDirectory(string? tempDir)
    {
        if (string.IsNullOrEmpty(tempDir) || !Directory.Exists(tempDir))
        {
            return;
        }

        try
        {
            Directory.Delete(tempDir, true);
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to cleanup temporary restore directory {tempDir}: {ex.Message}", LogLevel.Warning);
        }
    }

    private async Task RestoreProgramsAsync(
        string extractDir,
        string scriptsDir,
        SystemRestoreOptions options,
        SystemRestoreResult result,
        CancellationToken cancellationToken)
    {
        options.Progress?.Report(new SystemRestoreProgress("Restoring Programs", "Processing program list..."));

        var scriptPath = Path.Combine(scriptsDir, "restore_programs.ps1");
        if (File.Exists(scriptPath))
        {
            _logger.Log("Programs restore script found. ReStore never runs archived scripts; review it and run it manually if you want to.", LogLevel.Info);
            _logger.Log($"Script location: {scriptPath}", LogLevel.Info);
        }

        var jsonPath = Path.Combine(extractDir, "installed_programs.json");
        if (!File.Exists(jsonPath))
        {
            _logger.Log("Programs JSON not found in backup", LogLevel.Warning);
            result.Messages.Add("Program list not found in the backup.");
            return;
        }

        _logger.Log($"Programs backup data available at: {Path.Combine(scriptsDir, "installed_programs.json")}", LogLevel.Info);

        SystemRestorePlan plan;
        try
        {
            plan = await BuildRestorePlanAsync("programs", extractDir, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
        {
            _logger.Log($"Error restoring programs: {ex.Message}", LogLevel.Error);
            result.Failed++;
            result.Messages.Add($"Could not read the program list: {ex.Message}");
            return;
        }

        var installable = plan.Actions.Count(a => a.Change == PlannedChange.Install);
        var manual = plan.Actions.Count(a => a.Change == PlannedChange.Unavailable);
        _logger.Log($"Found {installable} programs to install via winget, {manual} that need manual installation, {plan.Actions.Count(a => a.Change == PlannedChange.AlreadyInstalled)} already installed", LogLevel.Info);

        await ApplyPlanAsync(plan, options, result,
            () => AskAsync(
                options,
                "Install Programs",
                $"Found {installable} programs available via Winget and {manual} that require manual installation.\n\n" +
                "Would you like to automatically install the Winget programs now?\n\n" +
                "(This may take several minutes and requires an internet connection)",
                isWarning: false),
            "Automatic installation skipped.", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Diffs the extracted backup against this machine (nothing is changed).</summary>
    private async Task<SystemRestorePlan> BuildRestorePlanAsync(string component, string extractDir, CancellationToken cancellationToken)
    {
        var scratch = Path.Combine(AppPaths.RestoreWorkDirectory, "plan-" + Guid.NewGuid().ToString("N"));
        try
        {
            return await new SystemRestorePlanner(EffectiveRestoreServices, _logger, scratch)
                .BuildPlanAsync(component, extractDir, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(scratch, true); } catch (Exception) { /* absent or locked; the work directory is purged later */ }
        }
    }

    /// <summary>
    /// Shared tail of every component restore: records the plan, decides which rows to apply (preview callback, or the
    /// legacy confirm prompt), saves an undo point and applies them. <paramref name="legacyConfirm"/> is only used when the
    /// caller supplied no <see cref="SystemRestoreOptions.SelectActions"/>.
    /// </summary>
    private async Task ApplyPlanAsync(
        SystemRestorePlan plan,
        SystemRestoreOptions options,
        SystemRestoreResult result,
        Func<Task<bool>> legacyConfirm,
        string skippedMessage,
        CancellationToken cancellationToken)
    {
        result.Plan = plan;
        result.Total = plan.Actions.Count + plan.Rejected.Count;
        result.Skipped = plan.UnchangedCount + plan.Rejected.Count;
        foreach (var rejected in plan.Rejected)
        {
            _logger.Log($"Settings entry rejected: {rejected}", LogLevel.Warning);
            result.Messages.Add($"Rejected: {rejected}");
        }

        options.Progress?.Report(new SystemRestoreProgress("Restoring", "Comparing the backup with this machine...", Total: result.Total));
        if (options.PlanOnly)
        {
            return;
        }

        if (plan.ChangeCount == 0)
        {
            if (plan.Actions.Count > 0)
            {
                result.Messages.Add("Nothing to change: this machine already matches the backup.");
            }

            return;
        }

        IReadOnlyList<PlannedAction> selected;
        if (options.SelectActions != null)
        {
            var ids = await options.SelectActions(plan).ConfigureAwait(false);
            if (ids == null)
            {
                _logger.Log("System restore cancelled at the preview", LogLevel.Info);
                result.Messages.Add("Restoration cancelled.");
                return;
            }

            selected = plan.Select(ids);
        }
        else if (await legacyConfirm().ConfigureAwait(false))
        {
            selected = plan.Select(plan.DefaultSelection());
        }
        else
        {
            _logger.Log(skippedMessage, LogLevel.Info);
            result.Messages.Add(skippedMessage);
            return;
        }

        if (selected.Count == 0)
        {
            result.Messages.Add("No items were selected; nothing was changed.");
            return;
        }

        result.Skipped += plan.ChangeCount - selected.Count;
        await new SystemRestoreApplier(EffectiveRestoreServices, _logger)
            .ApplyAsync(plan, selected, result.SourceBackup, result, options.Progress, cancellationToken).ConfigureAwait(false);
        if (result.UndoPointFolder != null)
        {
            result.Messages.Add($"Undo point saved to {result.UndoPointFolder}");
        }

        _logger.Log($"Applied {selected.Count} selected item(s): {result.Succeeded} succeeded, {result.Failed} failed", result.Failed > 0 ? LogLevel.Warning : LogLevel.Info);
    }

    private async Task RestoreEnvironmentVariablesAsync(
        string extractDir,
        string scriptsDir,
        SystemRestoreOptions options,
        SystemRestoreResult result,
        CancellationToken cancellationToken)
    {
        options.Progress?.Report(new SystemRestoreProgress("Restoring Environment Variables", "Processing environment variables..."));

        var jsonPath = Path.Combine(extractDir, "environment_variables.json");
        if (File.Exists(jsonPath))
        {
            var plan = await BuildRestorePlanAsync("environment", extractDir, cancellationToken).ConfigureAwait(false);

            // Without a confirm callback (CLI) variables are restored directly, as before.
            await ApplyPlanAsync(plan, options, result,
                async () => options.Confirm == null || await AskAsync(
                    options,
                    "Restore Environment Variables",
                    $"Found {plan.ChangeCount} environment variables to change ({plan.UnchangedCount} already match).\n\n" +
                    "PATH-like variables are merged with the current value. Machine-wide variables need administrator rights " +
                    "and are reported as failed otherwise.\n\nRestore them now?",
                    isWarning: false).ConfigureAwait(false),
                "Restoration skipped.", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _logger.Log("Environment variables JSON not found in backup", LogLevel.Warning);
            result.Messages.Add("Environment variables not found in the backup.");
        }

        var scriptPath = Path.Combine(scriptsDir, "restore_environment_variables.ps1");
        if (File.Exists(scriptPath))
        {
            _logger.Log("Environment variables restore script available for manual execution.", LogLevel.Info);
            _logger.Log($"Script location: {scriptPath}", LogLevel.Info);
        }
    }

    private void CleanupBackupTempArtifacts(string? tempDir, string? zipPath, string? fileToUpload)
    {
        var tempFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddTempFile(tempFiles, zipPath);
        AddTempFile(tempFiles, fileToUpload);

        if (!string.IsNullOrWhiteSpace(fileToUpload))
        {
            AddTempFile(tempFiles, fileToUpload + ".meta");
        }

        foreach (var tempFile in tempFiles)
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"Failed to delete temporary backup file {tempFile}: {ex.Message}", LogLevel.Warning);
            }
        }

        if (string.IsNullOrWhiteSpace(tempDir) || !Directory.Exists(tempDir))
        {
            return;
        }

        try
        {
            Directory.Delete(tempDir, true);
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to delete temporary backup directory {tempDir}: {ex.Message}", LogLevel.Warning);
        }
    }

    private static void AddTempFile(HashSet<string> tempFiles, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            tempFiles.Add(path);
        }
    }

    private async Task RestoreWindowsSettingsAsync(
        string extractDir,
        string scriptsDir,
        SystemRestoreOptions options,
        SystemRestoreResult result,
        CancellationToken cancellationToken)
    {
        options.Progress?.Report(new SystemRestoreProgress("Restoring Windows Settings", "Processing Windows settings..."));

        var scriptPath = Path.Combine(scriptsDir, "restore_windows_settings.ps1");
        if (File.Exists(scriptPath))
        {
            _logger.Log("Windows settings restore script available for manual execution.", LogLevel.Info);
            _logger.Log($"Script location: {scriptPath}", LogLevel.Info);
            _logger.Log("IMPORTANT: ReStore does not run this script. Review it before running it yourself; some settings may require administrator privileges.", LogLevel.Warning);
        }

        var manifestPath = Path.Combine(extractDir, "settings_manifest.json");
        if (!File.Exists(manifestPath))
        {
            _logger.Log("Windows settings manifest not found in backup", LogLevel.Warning);
            result.Messages.Add("Windows settings manifest not found in the backup.");
            return;
        }

        _logger.Log($"Settings manifest available at: {Path.Combine(scriptsDir, "settings_manifest.json")}", LogLevel.Info);

        // The restore actions are rebuilt from the manifest and the .reg data: only allow-listed keys are ever imported.
        SystemRestorePlan plan;
        try
        {
            plan = await BuildRestorePlanAsync("settings", extractDir, cancellationToken).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.Log($"Failed to read Windows settings manifest: {ex.Message}", LogLevel.Warning);
            result.Failed++;
            result.Messages.Add("The Windows settings manifest is unreadable.");
            return;
        }

        var machineCount = plan.NeedsAdminCount;
        await ApplyPlanAsync(plan, options, result,
            () => AskAsync(
                options,
                "Restore Windows Settings",
                $"Found {plan.ChangeCount} registry exports that differ from the current settings.\n\n" +
                "Only the registry keys ReStore itself exports will be imported." +
                (machineCount > 0 ? $"\n{machineCount} of them are machine-wide and need administrator approval (UAC)." : "") +
                "\n\nImport them now?",
                isWarning: true),
            "Restoration skipped.", cancellationToken).ConfigureAwait(false);
    }

    private async Task RestoreDevToolsAsync(
        string extractDir,
        SystemRestoreOptions options,
        SystemRestoreResult result,
        CancellationToken cancellationToken)
    {
        options.Progress?.Report(new SystemRestoreProgress("Restoring Developer Tools", "Processing developer tools settings..."));

        if (!File.Exists(Path.Combine(extractDir, DevToolsRestorePlanner.ManifestFileName)))
        {
            _logger.Log("Developer tools manifest not found in backup", LogLevel.Warning);
            result.Messages.Add("Developer tools manifest not found in the backup.");
            return;
        }

        SystemRestorePlan plan;
        try
        {
            plan = await BuildRestorePlanAsync("devtools", extractDir, cancellationToken).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.Log($"Failed to read developer tools manifest: {ex.Message}", LogLevel.Warning);
            result.Failed++;
            result.Messages.Add("The developer tools manifest is unreadable.");
            return;
        }

        var files = plan.Actions.Count(a => a.IsChange && a.DevToolsFile != null);
        var extensions = plan.Actions.Count(a => a.IsChange && a.DevToolsExtension != null);
        await ApplyPlanAsync(plan, options, result,
            () => AskAsync(
                options,
                "Restore Developer Tools",
                $"Found {files} file(s) to create or replace and {extensions} VS Code extension(s) to install.\n\n" +
                "The current files are saved to an undo point first. PowerShell profiles and SSH config can run code, so review " +
                "the preview before restoring them.\n\nRestore them now?",
                isWarning: true),
            "Restoration skipped.", cancellationToken).ConfigureAwait(false);
    }

    private void EnsureEncryptionProviderAvailable()
    {
        if (_passwordProvider == null)
        {
            throw new InvalidOperationException("Encryption is enabled but no password provider is available");
        }
    }
}
