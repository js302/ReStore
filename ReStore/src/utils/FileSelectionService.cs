using ReStore.src.utils;
using System.Text.RegularExpressions;

namespace ReStore.src.utils;

public class FileSelectionService
{
    private readonly ILogger _logger;
    private readonly IConfigManager _configManager;

    public FileSelectionService(ILogger logger, IConfigManager configManager)
    {
        _logger = logger;
        _configManager = configManager;
    }

    public bool ShouldExcludeFile(string filePath)
    {
        // Normalize path for comparison
        filePath = filePath.Replace('/', '\\');

        // Check if file exists
        if (!File.Exists(filePath) && !Directory.Exists(filePath))
        {
            return true;
        }

        // Check exclude paths from config
        if (_configManager.ExcludedPaths.Any(p => filePath.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Check system or hidden attributes
        if (File.Exists(filePath))
        {
            try
            {
                var attr = File.GetAttributes(filePath);
                if ((attr & FileAttributes.System) == FileAttributes.System ||
                    (attr & FileAttributes.Hidden) == FileAttributes.Hidden)
                {
                    return true;
                }
                
                // Check file size
                var fileInfo = new FileInfo(filePath);
                long maxFileSizeBytes = _configManager.MaxFileSizeMB * 1024 * 1024L;
                if (fileInfo.Length > maxFileSizeBytes)
                {
                    _logger.Log($"Skipping large file: {filePath} ({fileInfo.Length / (1024 * 1024)}MB)", LogLevel.Debug);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"Error accessing file {filePath}: {ex.Message}", LogLevel.Warning);
                return true;
            }
        }

        // Check exclude patterns
        string fileName = Path.GetFileName(filePath);
        foreach (var pattern in _configManager.ExcludedPatterns)
        {
            if (IsWildcardMatch(fileName, pattern))
            {
                return true;
            }
        }

        return false;
    }

    public List<string> GetFilesToBackup(List<string> includePaths)
    {
        var filesToBackup = new List<string>();

        foreach (var includePath in includePaths)
        {
            try
            {
                if (File.Exists(includePath))
                {
                    // If it's a single file
                    if (!ShouldExcludeFile(includePath))
                    {
                        filesToBackup.Add(includePath);
                    }
                }
                else if (Directory.Exists(includePath))
                {
                    // If it's a directory
                    var files = Directory.GetFiles(includePath, "*", SearchOption.AllDirectories);
                    foreach (var file in files)
                    {
                        if (!ShouldExcludeFile(file))
                        {
                            filesToBackup.Add(file);
                        }
                    }
                }
                else
                {
                    _logger.Log($"Path not found: {includePath}", LogLevel.Warning);
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"Error accessing path {includePath}: {ex.Message}", LogLevel.Error);
            }
        }

        return filesToBackup;
    }

    // Static utility method that can be used by other classes
    public static bool IsWildcardMatch(string fileName, string pattern)
    {
        string regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";

        return Regex.IsMatch(fileName, regexPattern, RegexOptions.IgnoreCase);
    }
}