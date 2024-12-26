using ReStore.src.core;
using ReStore.src.utils;
using ReStore.src.monitoring;

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
            var storage = await configManager.CreateStorageAsync(remote);

            var systemState = new SystemState();
            var sizeAnalyzer = new SizeAnalyzer();
            var compressionUtil = new CompressionUtil();

            try
            {
                if (isServiceMode)
                {
                    var watcher = new FileWatcher(configManager, logger, systemState, storage, sizeAnalyzer, compressionUtil);
                    await watcher.StartAsync();
                }
                else
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
                            var backup = new Backup(logger, systemState, sizeAnalyzer, storage, compressionUtil);
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
            catch (Exception ex)
            {
                logger.Log($"Error: {ex.Message}");
            }
        }
    }
}