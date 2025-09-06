namespace ReStore.src.utils;

public class ConfigValidationResult
{
    public bool IsValid { get; set; } = true;
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Info { get; set; } = new();

    public void AddError(string message)
    {
        IsValid = false;
        Errors.Add(message);
    }

    public void AddWarning(string message)
    {
        Warnings.Add(message);
    }

    public void AddInfo(string message)
    {
        Info.Add(message);
    }

    public bool HasIssues => Errors.Count > 0 || Warnings.Count > 0;
}

public class ConfigValidator
{
    private readonly ILogger _logger;

    public ConfigValidator(ILogger logger)
    {
        _logger = logger;
    }

    public ConfigValidationResult ValidateConfiguration(IConfigManager config)
    {
        var result = new ConfigValidationResult();
        
        _logger.Log("Starting configuration validation...", LogLevel.Info);

        ValidateWatchDirectories(config.WatchDirectories, result);
        ValidateBackupSettings(config, result);
        ValidateStorageSources(config.StorageSources, result);
        ValidateExclusionSettings(config, result);

        if (result.IsValid)
        {
            _logger.Log("Configuration validation passed", LogLevel.Info);
        }
        else
        {
            _logger.Log($"Configuration validation failed with {result.Errors.Count} errors", LogLevel.Error);
        }

        return result;
    }

    private void ValidateWatchDirectories(List<string> watchDirectories, ConfigValidationResult result)
    {
        if (watchDirectories == null || watchDirectories.Count == 0)
        {
            result.AddError("No watch directories specified. At least one directory must be configured for monitoring.");
            return;
        }

        var validDirectories = 0;
        foreach (var directory in watchDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                result.AddWarning("Empty watch directory entry found and will be ignored.");
                continue;
            }

            try
            {
                var expandedPath = Environment.ExpandEnvironmentVariables(directory);
                if (Directory.Exists(expandedPath))
                {
                    validDirectories++;
                    result.AddInfo($"Watch directory validated: {expandedPath}");
                }
                else
                {
                    result.AddWarning($"Watch directory does not exist: {expandedPath}");
                }
            }
            catch (Exception ex)
            {
                result.AddError($"Invalid watch directory path '{directory}': {ex.Message}");
            }
        }

        if (validDirectories == 0)
        {
            result.AddError("No valid watch directories found. At least one existing directory is required.");
        }
    }

    private void ValidateBackupSettings(IConfigManager config, ConfigValidationResult result)
    {
        // Validate backup interval
        if (config.BackupInterval <= TimeSpan.Zero)
        {
            result.AddError("Backup interval must be greater than zero.");
        }
        else if (config.BackupInterval < TimeSpan.FromMinutes(1))
        {
            result.AddWarning("Backup interval is less than 1 minute. This may cause excessive system load.");
        }
        else if (config.BackupInterval > TimeSpan.FromDays(7))
        {
            result.AddWarning("Backup interval is greater than 7 days. Consider more frequent backups for data protection.");
        }

        // Validate size thresholds
        if (config.SizeThresholdMB <= 0)
        {
            result.AddError("Size threshold must be greater than zero.");
        }
        else if (config.SizeThresholdMB < 10)
        {
            result.AddWarning("Size threshold is very low (< 10MB). This may trigger warnings for small directories.");
        }

        if (config.MaxFileSizeMB <= 0)
        {
            result.AddError("Maximum file size must be greater than zero.");
        }
        else if (config.MaxFileSizeMB > 1000)
        {
            result.AddWarning("Maximum file size is very large (> 1GB). Large files may impact backup performance.");
        }

        // Check if max file size is larger than size threshold
        if (config.MaxFileSizeMB > config.SizeThresholdMB)
        {
            result.AddWarning("Maximum file size is larger than size threshold. This configuration may be inconsistent.");
        }

        // Validate backup type
        if (!Enum.IsDefined(typeof(BackupType), config.BackupType))
        {
            result.AddError($"Invalid backup type: {config.BackupType}");
        }
    }

    private void ValidateStorageSources(Dictionary<string, StorageConfig> storageSources, ConfigValidationResult result)
    {
        if (storageSources == null || storageSources.Count == 0)
        {
            result.AddError("No storage sources configured. At least one storage source is required.");
            return;
        }

        foreach (var kvp in storageSources)
        {
            var sourceName = kvp.Key;
            var config = kvp.Value;

            if (string.IsNullOrWhiteSpace(sourceName))
            {
                result.AddError("Storage source name cannot be empty.");
                continue;
            }

            ValidateStorageSource(sourceName, config, result);
        }
    }

    private void ValidateStorageSource(string sourceName, StorageConfig config, ConfigValidationResult result)
    {
        if (config == null)
        {
            result.AddError($"Storage source '{sourceName}' configuration is null.");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.Path))
        {
            result.AddError($"Storage source '{sourceName}' must have a path specified.");
            return;
        }

        switch (sourceName.ToLowerInvariant())
        {
            case "local":
                ValidateLocalStorage(sourceName, config, result);
                break;
            case "gdrive":
                ValidateGoogleDriveStorage(sourceName, config, result);
                break;
            case "s3":
                ValidateS3Storage(sourceName, config, result);
                break;
            case "github":
                ValidateGitHubStorage(sourceName, config, result);
                break;
            default:
                result.AddWarning($"Unknown storage type '{sourceName}'. Make sure this storage provider is supported.");
                break;
        }
    }

    private void ValidateLocalStorage(string sourceName, StorageConfig config, ConfigValidationResult result)
    {
        try
        {
            var expandedPath = Environment.ExpandEnvironmentVariables(config.Path);
            var directory = Path.GetDirectoryName(expandedPath);
            
            if (string.IsNullOrEmpty(directory))
            {
                result.AddError($"Local storage '{sourceName}' has invalid path: {config.Path}");
                return;
            }

            // Check if we can create the directory
            if (!Directory.Exists(directory))
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    result.AddInfo($"Local storage directory created: {directory}");
                }
                catch (Exception ex)
                {
                    result.AddError($"Cannot create local storage directory '{directory}': {ex.Message}");
                }
            }
            else
            {
                result.AddInfo($"Local storage directory validated: {directory}");
            }
        }
        catch (Exception ex)
        {
            result.AddError($"Local storage '{sourceName}' path validation failed: {ex.Message}");
        }
    }

    private void ValidateGoogleDriveStorage(string sourceName, StorageConfig config, ConfigValidationResult result)
    {
        var requiredOptions = new[] { "client_id", "client_secret" };
        var missingOptions = requiredOptions.Where(opt => 
            !config.Options.ContainsKey(opt) || 
            string.IsNullOrWhiteSpace(config.Options[opt]) ||
            config.Options[opt].Contains("your_")).ToList();

        if (missingOptions.Any())
        {
            result.AddError($"Google Drive storage '{sourceName}' missing required options: {string.Join(", ", missingOptions)}");
        }

        if (config.Options.ContainsKey("token_folder"))
        {
            try
            {
                var tokenFolder = Environment.ExpandEnvironmentVariables(config.Options["token_folder"]);
                var tokenDir = Path.GetDirectoryName(tokenFolder);
                if (!string.IsNullOrEmpty(tokenDir) && !Directory.Exists(tokenDir))
                {
                    Directory.CreateDirectory(tokenDir);
                    result.AddInfo($"Google Drive token directory created: {tokenDir}");
                }
            }
            catch (Exception ex)
            {
                result.AddWarning($"Cannot create Google Drive token directory: {ex.Message}");
            }
        }
    }

    private void ValidateS3Storage(string sourceName, StorageConfig config, ConfigValidationResult result)
    {
        var requiredOptions = new[] { "accessKeyId", "secretAccessKey", "region", "bucketName" };
        var missingOptions = requiredOptions.Where(opt => 
            !config.Options.ContainsKey(opt) || 
            string.IsNullOrWhiteSpace(config.Options[opt]) ||
            config.Options[opt].Contains("your_")).ToList();

        if (missingOptions.Any())
        {
            result.AddError($"S3 storage '{sourceName}' missing required options: {string.Join(", ", missingOptions)}");
        }

        // Validate region format
        if (config.Options.ContainsKey("region") && !config.Options["region"].Contains("your_"))
        {
            var region = config.Options["region"];
            if (!IsValidAwsRegion(region))
            {
                result.AddWarning($"S3 storage '{sourceName}' has potentially invalid AWS region: {region}");
            }
        }

        // Validate bucket name format
        if (config.Options.ContainsKey("bucketName") && !config.Options["bucketName"].Contains("your_"))
        {
            var bucketName = config.Options["bucketName"];
            if (!IsValidS3BucketName(bucketName))
            {
                result.AddError($"S3 storage '{sourceName}' has invalid bucket name: {bucketName}");
            }
        }
    }

    private void ValidateGitHubStorage(string sourceName, StorageConfig config, ConfigValidationResult result)
    {
        var requiredOptions = new[] { "token", "repo", "owner" };
        var missingOptions = requiredOptions.Where(opt => 
            !config.Options.ContainsKey(opt) || 
            string.IsNullOrWhiteSpace(config.Options[opt]) ||
            config.Options[opt].Contains("your_")).ToList();

        if (missingOptions.Any())
        {
            result.AddError($"GitHub storage '{sourceName}' missing required options: {string.Join(", ", missingOptions)}");
        }
    }

    private void ValidateExclusionSettings(IConfigManager config, ConfigValidationResult result)
    {
        // Validate excluded patterns
        if (config.ExcludedPatterns != null)
        {
            foreach (var pattern in config.ExcludedPatterns)
            {
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    result.AddWarning("Empty exclusion pattern found and will be ignored.");
                    continue;
                }

                try
                {
                    // Test if the pattern is a valid glob pattern by attempting to use it
                    _ = System.IO.Directory.EnumerateFiles(".", pattern).Any();
                    // If we get here without exception, the pattern is valid
                }
                catch (Exception)
                {
                    result.AddWarning($"Potentially invalid exclusion pattern: {pattern}");
                }
            }
        }

        // Validate excluded paths
        if (config.ExcludedPaths != null)
        {
            foreach (var path in config.ExcludedPaths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    result.AddWarning("Empty exclusion path found and will be ignored.");
                    continue;
                }

                try
                {
                    var expandedPath = Environment.ExpandEnvironmentVariables(path);
                    if (Directory.Exists(expandedPath))
                    {
                        result.AddInfo($"Exclusion path validated: {expandedPath}");
                    }
                    else
                    {
                        result.AddInfo($"Exclusion path does not exist (will be ignored): {expandedPath}");
                    }
                }
                catch (Exception ex)
                {
                    result.AddWarning($"Invalid exclusion path '{path}': {ex.Message}");
                }
            }
        }
    }

    private bool IsValidAwsRegion(string region)
    {
        var validRegions = new[]
        {
            "us-east-1", "us-east-2", "us-west-1", "us-west-2",
            "eu-west-1", "eu-west-2", "eu-west-3", "eu-central-1",
            "ap-southeast-1", "ap-southeast-2", "ap-northeast-1", "ap-northeast-2",
            "ap-south-1", "sa-east-1", "ca-central-1"
        };
        
        return validRegions.Contains(region) || region.StartsWith("us-") || region.StartsWith("eu-") || 
               region.StartsWith("ap-") || region.StartsWith("sa-") || region.StartsWith("ca-");
    }

    private bool IsValidS3BucketName(string bucketName)
    {
        if (string.IsNullOrWhiteSpace(bucketName) || bucketName.Length < 3 || bucketName.Length > 63)
            return false;

        // S3 bucket naming rules
        if (bucketName.StartsWith('-') || bucketName.EndsWith('-') || bucketName.StartsWith('.') || bucketName.EndsWith('.'))
            return false;

        if (bucketName.Contains("..") || bucketName.Contains(".-") || bucketName.Contains("-."))
            return false;

        return bucketName.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '.');
    }
}
