using System.Text.RegularExpressions;

namespace ReStore.Core.src.utils;

public class FileSelectionService(ILogger logger, IConfigManager configManager)
{
    private readonly ILogger _logger = logger;
    private readonly IConfigManager _configManager = configManager;

    public bool ShouldExcludeFile(string filePath)
    {
        filePath = filePath.Replace('/', '\\');

        // Check exclude paths from config
        if (_configManager.ExcludedPaths.Any(p => IsPathWithinRoot(filePath, NormalizePath(p))))
        {
            return true;
        }

        // Check system or hidden attributes
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Exists)
            {
                if ((fileInfo.Attributes & FileAttributes.System) == FileAttributes.System ||
                    (fileInfo.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
                {
                    return true;
                }

                // Check file size
                long maxFileSizeBytes = _configManager.MaxFileSizeMB * 1024 * 1024L;
                if (fileInfo.Length > maxFileSizeBytes)
                {
                    _logger.Log($"Skipping large file: {filePath} ({fileInfo.Length / (1024 * 1024)}MB)", LogLevel.Debug);
                    return true;
                }
            }
            else
            {
                var dirInfo = new DirectoryInfo(filePath);
                if (dirInfo.Exists)
                {
                    if ((dirInfo.Attributes & FileAttributes.System) == FileAttributes.System ||
                        (dirInfo.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Error accessing file/directory {filePath}: {ex.Message}", LogLevel.Warning);
            return true;
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
                    foreach (var file in EnumerateFilesRecursivelySafe(includePath))
                    {
                        try
                        {
                            if (!ShouldExcludeFile(file))
                            {
                                filesToBackup.Add(file);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Log($"Error evaluating file {file}: {ex.Message}", LogLevel.Warning);
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

    private static IEnumerable<string> EnumerateFilesRecursivelySafe(string rootDirectory)
    {
        var directories = new Stack<string>();
        directories.Push(rootDirectory);

        while (directories.Count > 0)
        {
            var current = directories.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            IEnumerable<string> subDirectories;
            try
            {
                subDirectories = Directory.EnumerateDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (var subDirectory in subDirectories)
            {
                // Skip hidden and system directories
                try
                {
                    var dirInfo = new DirectoryInfo(subDirectory);
                    if ((dirInfo.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden ||
                        (dirInfo.Attributes & FileAttributes.System) == FileAttributes.System)
                    {
                        continue;
                    }
                }
                catch
                {
                    // If we can't read attributes, skip the directory
                    continue;
                }

                directories.Push(subDirectory);
            }
        }
    }

    public static bool IsWildcardMatch(string fileName, string pattern)
    {
        string regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";

        return Regex.IsMatch(fileName, regexPattern, RegexOptions.IgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        var normalizedPath = NormalizePath(path);

        if (normalizedPath.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
}