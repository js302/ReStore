using ReStore.Core.src.core;
using ReStore.Core.src.utils;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.backup;

namespace ReStore
{
    public class Program
    {
        private const string USAGE_MESSAGE = @"Usage:
  restore.exe --service <remote-source>
  restore.exe backup <remote-source> <sourceDir>
  restore.exe restore <remote-source> <backupPath> <targetDir>
  restore.exe system-backup <remote-source> [programs|environment|all]
  restore.exe system-restore <remote-source> <backupPath> [programs|environment]
  restore.exe --validate-config

Examples:
  restore.exe --service gdrive
  restore.exe backup gdrive %USERPROFILE%\Desktop
  restore.exe system-backup local all
  restore.exe system-backup gdrive programs
  restore.exe system-restore local system_backups/programs/... programs
  restore.exe --validate-config";

        public static async Task Main(string[] args)
        {
            var logger = new Logger();
            
            ConfigInitializer.EnsureConfigurationSetup(logger);
            
            var configManager = new ConfigManager(logger);
            await configManager.LoadAsync();

            if (args.Length == 0)
            {
                Console.WriteLine(USAGE_MESSAGE);
                return;
            }

            // Handle configuration validation
            if (args.Length == 1 && args[0] == "--validate-config")
            {
                ValidateConfiguration(configManager, logger);
                return;
            }

            var isServiceMode = args.Length == 2 && args[0] == "--service";
            var commandMode = args.Length >= 2 && (args[0] == "backup" || args[0] == "restore" || args[0] == "system-backup" || args[0] == "system-restore");

            if (!isServiceMode && !commandMode)
            {
                Console.WriteLine(USAGE_MESSAGE);
                return;
            }

            // Auto-validate configuration for all operations
            var validationResult = configManager.ValidateConfiguration();
            if (!validationResult.IsValid)
            {
                logger.Log("Configuration validation failed. Please fix the errors before proceeding.", LogLevel.Error);
                PrintValidationResults(validationResult, logger);
                Environment.Exit(1);
                return;
            }
            else if (validationResult.HasIssues)
            {
                logger.Log("Configuration validation completed with warnings.", LogLevel.Warning);
                PrintValidationResults(validationResult, logger);
            }

            var remote = args[1];
            // Use a 'using' statement to ensure storage is disposed
            using IStorage storage = await configManager.CreateStorageAsync(remote);

            var systemState = new SystemState(logger);
            await systemState.LoadStateAsync();

            var sizeAnalyzer = new SizeAnalyzer();
            var compressionUtil = new CompressionUtil();

            // Use a CancellationTokenSource for graceful shutdown in service mode
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                logger.Log("Cancellation requested. Shutting down...", LogLevel.Info);
                e.Cancel = true; // Prevent the process from terminating immediately
                cts.Cancel();
            };

            try
            {
                if (isServiceMode)
                {
                    // Dispose FileWatcher using 'using'
                    using var watcher = new FileWatcher(configManager, logger, systemState, storage, sizeAnalyzer, compressionUtil);
                    await watcher.StartAsync();
                    logger.Log("File watcher service running. Press Ctrl+C to stop.", LogLevel.Info);
                    // Wait for cancellation instead of infinite delay
                    await Task.Delay(Timeout.Infinite, cts.Token);
                    logger.Log("File watcher service stopped.", LogLevel.Info);
                }
                else // Command mode
                {
                    var command = args[0];
                    switch (command)
                    {
                        case "backup":
                            if (args.Length < 3)
                            {
                                Console.WriteLine(USAGE_MESSAGE);
                                break;
                            }
                            var backup = new Backup(logger, systemState, sizeAnalyzer, storage, configManager);
                            await backup.BackupDirectoryAsync(args[2]);
                            break;

                        case "restore":
                            if (args.Length < 4)
                            {
                                Console.WriteLine(USAGE_MESSAGE);
                                break;
                            }
                            var restore = new Restore(logger, systemState, storage);
                            await restore.RestoreFromBackupAsync(args[2], args[3]);
                            break;

                        case "system-backup":
                            if (args.Length < 2)
                            {
                                Console.WriteLine(USAGE_MESSAGE);
                                break;
                            }
                            if (!OperatingSystem.IsWindows())
                            {
                                logger.Log("The 'system-backup' command is only supported on Windows.", LogLevel.Error);
                                break;
                            }
                            var backupType = args.Length >= 3 ? args[2] : "all";
                            var systemBackupManager = new SystemBackupManager(logger, storage, systemState);
                            
                            switch (backupType.ToLowerInvariant())
                            {
                                case "programs":
                                    await systemBackupManager.BackupInstalledProgramsAsync();
                                    break;
                                case "environment":
                                    await systemBackupManager.BackupEnvironmentVariablesAsync();
                                    break;
                                case "all":
                                default:
                                    await systemBackupManager.BackupSystemAsync();
                                    break;
                            }
                            break;

                        case "system-restore":
                            if (args.Length < 4)
                            {
                                Console.WriteLine(USAGE_MESSAGE);
                                break;
                            }
                            if (!OperatingSystem.IsWindows())
                            {
                                logger.Log("The 'system-restore' command is only supported on Windows.", LogLevel.Error);
                                break;
                            }
                            var restoreType = args.Length >= 4 ? args[3] : "all";
                            var systemRestoreManager = new SystemBackupManager(logger, storage, systemState);
                            await systemRestoreManager.RestoreSystemAsync(restoreType, args[2]);
                            break;
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // Expected when Ctrl+C is pressed in service mode
                logger.Log("Shutdown initiated due to cancellation request.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                logger.Log($"An unexpected error occurred: {ex.Message}", LogLevel.Error);
                logger.Log($"Stack Trace: {ex.StackTrace}", LogLevel.Debug);
            }
            finally
            {
                // Ensure state is saved on exit, especially in command mode or graceful shutdown
                if (!cts.IsCancellationRequested || !isServiceMode) // Avoid saving if service cancelled abruptly? Consider needs.
                {
                    await systemState.SaveStateAsync();
                }
                logger.Log("Application finished.", LogLevel.Info);
            }
        }

        private static void ValidateConfiguration(IConfigManager configManager, ILogger logger)
        {
            logger.Log($"Configuration file location: {configManager.GetConfigFilePath()}", LogLevel.Info);
            logger.Log("Running comprehensive configuration validation...", LogLevel.Info);
            
            var result = configManager.ValidateConfiguration();
            
            PrintValidationResults(result, logger);
            
            if (result.IsValid)
            {
                Console.WriteLine("\nConfiguration is valid and ready to use!");
                if (result.Warnings.Count > 0)
                {
                    Console.WriteLine($"Found {result.Warnings.Count} warning(s) that should be reviewed.");
                }
            }
            else
            {
                Console.WriteLine($"\nConfiguration validation failed with {result.Errors.Count} error(s).");
                Console.WriteLine("Please fix the errors above before using ReStore.");
                Environment.Exit(1);
            }
        }

        private static void PrintValidationResults(ConfigValidationResult result, ILogger logger)
        {
            // Print errors
            if (result.Errors.Count > 0)
            {
                Console.WriteLine("\nERRORS:");
                foreach (var error in result.Errors)
                {
                    Console.WriteLine($"{error}");
                    logger.Log($"Config Error: {error}", LogLevel.Error);
                }
            }

            // Print warnings
            if (result.Warnings.Count > 0)
            {
                Console.WriteLine("\nWARNINGS:");
                foreach (var warning in result.Warnings)
                {
                    Console.WriteLine($"{warning}");
                    logger.Log($"Config Warning: {warning}", LogLevel.Warning);
                }
            }

            // Print info messages (only in validation mode, not during startup)
            if (result.Info.Count > 0)
            {
                Console.WriteLine("\nINFO:");
                foreach (var info in result.Info)
                {
                    Console.WriteLine($"{info}");
                    logger.Log($"Config Info: {info}", LogLevel.Info);
                }
            }
        }
    }
}