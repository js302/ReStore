using ReStore.Core.src.utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ReStore.Core.src.backup
{
    public class BackupConfiguration
    {
        public List<string> IncludePaths { get; set; } = [];
        public List<string> ExcludePaths { get; set; } = [];
        public List<string> ExcludePatterns { get; set; } = [];
        public int MaxFileSize { get; set; } = 100 * 1024 * 1024; // 100MB by default
        public BackupType Type { get; set; } = BackupType.Incremental;
    }

    public class BackupConfigurationManager
    {
        private readonly ILogger _logger;
        private readonly IConfigManager _configManager;
        private readonly FileSelectionService _fileSelectionService;
        private BackupConfiguration _configuration;

        public BackupConfigurationManager(ILogger logger, IConfigManager configManager)
        {
            _logger = logger;
            _configManager = configManager;
            _fileSelectionService = new FileSelectionService(logger, configManager);

            // Initialize from ConfigManager instead of creating new defaults
            _configuration = new BackupConfiguration
            {
                IncludePaths = _configManager.WatchDirectories.Select(w => w.Path).ToList(),
                ExcludePaths = [.. _configManager.ExcludedPaths],
                ExcludePatterns = [.. _configManager.ExcludedPatterns],
                MaxFileSize = _configManager.MaxFileSizeMB * 1024 * 1024,
                Type = _configManager.BackupType
            };
        }

        public BackupConfiguration Configuration => _configuration;

        public void LoadConfiguration(string configPath)
        {
            try
            {
                if (File.Exists(configPath))
                {
                    var configText = File.ReadAllText(configPath);
                    _configuration = System.Text.Json.JsonSerializer.Deserialize<BackupConfiguration>(configText)
                        ?? new BackupConfiguration
                        {
                            IncludePaths = _configManager.WatchDirectories.Select(w => w.Path).ToList(),
                            ExcludePaths = [.. _configManager.ExcludedPaths],
                            ExcludePatterns = [.. _configManager.ExcludedPatterns],
                            MaxFileSize = _configManager.MaxFileSizeMB * 1024 * 1024,
                            Type = _configManager.BackupType
                        };
                    _logger.Log("Backup configuration loaded successfully", LogLevel.Info);
                }
                else
                {
                    SaveConfiguration(configPath);
                    _logger.Log("Created backup configuration from system settings", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"Error loading backup configuration: {ex.Message}", LogLevel.Error);
            }
        }

        public void SaveConfiguration(string configPath)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                var configText = System.Text.Json.JsonSerializer.Serialize(_configuration, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(configPath, configText);
                _logger.Log("Backup configuration saved successfully", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _logger.Log($"Error saving backup configuration: {ex.Message}", LogLevel.Error);
            }
        }

        public List<string> GetFilesToBackup()
        {
            return _fileSelectionService.GetFilesToBackup(_configuration.IncludePaths);
        }
    }
}