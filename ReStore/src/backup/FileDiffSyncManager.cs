using ReStore.src.core;
using ReStore.src.utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace ReStore.src.backup
{
    public class FileDiffSyncManager
    {
        private readonly ILogger _logger;
        private readonly SystemState _systemState;
        private readonly BackupConfigurationManager _backupConfig;
        
        private const int MAX_RETRY_ATTEMPTS = 3;
        
        public FileDiffSyncManager(ILogger logger, SystemState systemState, BackupConfigurationManager backupConfig)
        {
            _logger = logger;
            _systemState = systemState;
            _backupConfig = backupConfig;
        }
        
        public async Task UpdateFileMetadataAsync(List<string> backedUpFiles)
        {
            foreach (var file in backedUpFiles)
            {
                await _systemState.AddOrUpdateFileMetadataAsync(file);
            }
            
            await _systemState.SaveMetadataAsync();
            _logger.Log($"Updated metadata for {backedUpFiles.Count} files", LogLevel.Info);
        }

        public List<string> GetFilesToBackup(List<string> candidateFiles)
        {
            var backupType = _backupConfig.Configuration.Type;
            var maxFileSize = _backupConfig.Configuration.MaxFileSize;
            var excludePatterns = _backupConfig.Configuration.ExcludePatterns;
            
            // First filter by configuration rules
            var filteredFiles = FilterFilesByConfiguration(candidateFiles, maxFileSize, excludePatterns);
            
            // Then use SystemState to determine which files have changed
            var changedFiles = _systemState.GetChangedFiles(filteredFiles, backupType);
            
            _logger.Log($"Identified {changedFiles.Count} files to backup based on {backupType} strategy", LogLevel.Info);
            return changedFiles;
        }
        
        private List<string> FilterFilesByConfiguration(List<string> files, int maxFileSize, List<string> excludePatterns)
        {
            var result = new List<string>();
            
            foreach (var file in files)
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    if (!fileInfo.Exists) continue;
                    
                    // Skip files that exceed max size
                    if (fileInfo.Length > maxFileSize)
                    {
                        _logger.Log($"Skipping large file: {file} ({fileInfo.Length / (1024 * 1024)}MB)", LogLevel.Debug);
                        continue;
                    }
                    
                    // Apply backup-specific exclusion patterns
                    if (IsExcludedByPattern(file, excludePatterns)) continue;
                    
                    result.Add(file);
                }
                catch (Exception ex)
                {
                    _logger.Log($"Error filtering file {file}: {ex.Message}", LogLevel.Warning);
                }
            }
            
            return result;
        }
        
        private bool IsExcludedByPattern(string filePath, List<string> patterns)
        {
            string fileName = Path.GetFileName(filePath);
            
            foreach (var pattern in patterns)
            {
                if (FileSelectionService.IsWildcardMatch(fileName, pattern))
                {
                    return true;
                }
            }
            
            return false;
        }
    }
}