using ReStore.src.core;
using ReStore.src.utils;
using ReStore.src.monitoring;
using ReStore.src.storage; // Added for IStorage

namespace ReStore
{
    public class Program
    {
        private const string USAGE_MESSAGE = "Usage:\n  ReStore.exe --service <remote-source>\n  ReStore.exe backup <remote-source> <sourceDir>\n  ReStore.exe restore <remote-source> <backupPath> <targetDir>\n\nExample:\n  ReStore.exe --service gdrive\n  ReStore.exe backup gdrive %USERPROFILE%\\Desktop\n";

        public static async Task Main(string[] args)
        {
            var logger = new Logger();
            var configManager = new ConfigManager(logger);
            await configManager.LoadAsync();

            if (args.Length == 0)
            {
                Console.WriteLine(USAGE_MESSAGE);
                return;
            }

            var isServiceMode = args.Length == 2 && args[0] == "--service";
            var commandMode = args.Length >= 2 && (args[0] == "backup" || args[0] == "restore");

            if (!isServiceMode && !commandMode)
            {
                Console.WriteLine(USAGE_MESSAGE);
                return;
            }

            var remote = args[1];
            // Use a 'using' statement to ensure storage is disposed
            using IStorage storage = await configManager.CreateStorageAsync(remote);

            var systemState = new SystemState(logger); // Pass logger to SystemState
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
                logger.Log($"Stack Trace: {ex.StackTrace}", LogLevel.Debug); // Log stack trace for debugging
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
    }
}