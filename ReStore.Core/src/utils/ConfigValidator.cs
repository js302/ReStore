namespace ReStore.Core.src.utils;

public class ConfigValidationResult
{
    public bool IsValid { get; set; } = true;
    public List<string> Errors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<string> Info { get; set; } = [];

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

    private void ValidateWatchDirectories(List<WatchDirectoryConfig> watchDirectories, ConfigValidationResult result)
    {
        if (watchDirectories == null || watchDirectories.Count == 0)
        {
            result.AddError("No watch directories specified. At least one directory must be configured for monitoring.");
            return;
        }

        var validDirectories = 0;
        foreach (var watchConfig in watchDirectories)
        {
            if (string.IsNullOrWhiteSpace(watchConfig.Path))
            {
                result.AddWarning("Empty watch directory entry found and will be ignored.");
                continue;
            }

            try
            {
                var expandedPath = Environment.ExpandEnvironmentVariables(watchConfig.Path);
                if (Directory.Exists(expandedPath))
                {
                    validDirectories++;
                    var storageInfo = watchConfig.StorageType != null ? $" (storage: {watchConfig.StorageType})" : " (using global storage)";
                    result.AddInfo($"Watch directory validated: {expandedPath}{storageInfo}");
                }
                else
                {
                    result.AddWarning($"Watch directory does not exist: {expandedPath}");
                }
            }
            catch (Exception ex)
            {
                result.AddError($"Invalid watch directory path '{watchConfig.Path}': {ex.Message}");
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

        // Validate retention settings
        if (config.Retention.Enabled)
        {
            if (config.Retention.KeepLastPerDirectory < 1)
            {
                result.AddError("Retention is enabled but keepLastPerDirectory is < 1. At least one backup must be kept.");
            }

            if (config.Retention.MaxAgeDays < 0)
            {
                result.AddError("Retention maxAgeDays cannot be negative.");
            }

            if (config.Retention.MaxAgeDays == 0)
            {
                result.AddInfo("Retention is enabled with maxAgeDays=0 (age-based deletion disabled). Only keepLastPerDirectory will apply.");
            }
        }
    }

    private void ValidateStorageSources(Dictionary<string, StorageConfig> storageSources, ConfigValidationResult result)
    {
        if (storageSources == null || storageSources.Count == 0)
        {
            result.AddError("No storage sources configured. At least one storage source is required.");
            return;
        }

        var configuredSourcesCount = 0;

        foreach (var kvp in storageSources)
        {
            var sourceName = kvp.Key;
            var config = kvp.Value;

            if (string.IsNullOrWhiteSpace(sourceName))
            {
                result.AddError("Storage source name cannot be empty.");
                continue;
            }

            var wasConfigured = !IsStorageSourceUnconfigured(sourceName, config);
            ValidateStorageSource(sourceName, config, result);

            if (wasConfigured)
            {
                configuredSourcesCount++;
            }
        }

        if (configuredSourcesCount == 0)
        {
            result.AddError("No storage sources are properly configured. At least one storage source (e.g., 'local') must be configured.");
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

        // Skip validation for storage sources that are not configured (have placeholder values)
        if (IsStorageSourceUnconfigured(sourceName, config))
        {
            result.AddInfo($"Storage source '{sourceName}' is not configured (skipped validation).");
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
            case "azure":
                ValidateAzureStorage(sourceName, config, result);
                break;
            case "gcp":
                ValidateGcpStorage(sourceName, config, result);
                break;
            case "dropbox":
                ValidateDropboxStorage(sourceName, config, result);
                break;
            case "sftp":
                ValidateSftpStorage(sourceName, config, result);
                break;
            case "b2":
                ValidateB2Storage(sourceName, config, result);
                break;
            default:
                result.AddWarning($"Unknown storage type '{sourceName}'. Make sure this storage provider is supported.");
                break;
        }
    }

    private bool IsStorageSourceUnconfigured(string sourceName, StorageConfig config)
    {
        // Check if this is a non-local storage with placeholder values
        if (sourceName.ToLowerInvariant() == "local")
        {
            return false; // Always validate local storage
        }

        // Check if any option contains placeholder text
        return config.Options.Any(opt =>
            string.IsNullOrWhiteSpace(opt.Value) ||
            opt.Value.StartsWith("your_", StringComparison.OrdinalIgnoreCase) ||
            opt.Value.Contains("your_", StringComparison.OrdinalIgnoreCase));
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
            result.AddWarning($"Google Drive storage '{sourceName}' is not configured (missing: {string.Join(", ", missingOptions)})");
            return;
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
            result.AddWarning($"S3 storage '{sourceName}' is not configured (missing: {string.Join(", ", missingOptions)})");
            return;
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
            result.AddWarning($"GitHub storage '{sourceName}' is not configured (missing: {string.Join(", ", missingOptions)})");
            return;
        }
    }

    private void ValidateAzureStorage(string sourceName, StorageConfig config, ConfigValidationResult result)
    {
        var requiredOptions = new[] { "connectionString", "containerName" };
        var missingOptions = requiredOptions.Where(opt =>
            !config.Options.ContainsKey(opt) ||
            string.IsNullOrWhiteSpace(config.Options[opt]) ||
            config.Options[opt].Contains("your_")).ToList();

        if (missingOptions.Any())
        {
            result.AddWarning($"Azure storage '{sourceName}' is not configured (missing: {string.Join(", ", missingOptions)})");
            return;
        }
    }

    private void ValidateGcpStorage(string sourceName, StorageConfig config, ConfigValidationResult result)
    {
        var requiredOptions = new[] { "bucketName" };
        var missingOptions = requiredOptions.Where(opt =>
            !config.Options.ContainsKey(opt) ||
            string.IsNullOrWhiteSpace(config.Options[opt]) ||
            config.Options[opt].Contains("your_")).ToList();

        if (missingOptions.Any())
        {
            result.AddWarning($"GCP storage '{sourceName}' is not configured (missing: {string.Join(", ", missingOptions)})");
            return;
        }

        if (config.Options.ContainsKey("credentialPath"))
        {
            var path = config.Options["credentialPath"];
            if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path))
            {
                result.AddWarning($"GCP credential file not found: {path}");
            }
        }
    }

    private void ValidateDropboxStorage(string sourceName, StorageConfig config, ConfigValidationResult result)
    {
        // Dropbox needs either accessToken OR (appKey + appSecret + refreshToken)
        var hasAccessToken = config.Options.ContainsKey("accessToken") && !string.IsNullOrWhiteSpace(config.Options["accessToken"]);
        var hasRefreshToken = config.Options.ContainsKey("refreshToken") && !string.IsNullOrWhiteSpace(config.Options["refreshToken"]);
        var hasAppKey = config.Options.ContainsKey("appKey") && !string.IsNullOrWhiteSpace(config.Options["appKey"]);
        var hasAppSecret = config.Options.ContainsKey("appSecret") && !string.IsNullOrWhiteSpace(config.Options["appSecret"]);

        if (!hasAccessToken && !(hasRefreshToken && hasAppKey && hasAppSecret))
        {
            result.AddWarning($"Dropbox storage '{sourceName}' is not configured correctly. Requires either 'accessToken' OR ('refreshToken', 'appKey', and 'appSecret').");
        }
    }

    private void ValidateSftpStorage(string sourceName, StorageConfig config, ConfigValidationResult result)
    {
        var requiredOptions = new[] { "host", "username" };
        var missingOptions = requiredOptions.Where(opt =>
            !config.Options.ContainsKey(opt) ||
            string.IsNullOrWhiteSpace(config.Options[opt]) ||
            config.Options[opt].Contains("your_")).ToList();

        if (missingOptions.Any())
        {
            result.AddWarning($"SFTP storage '{sourceName}' is not configured (missing: {string.Join(", ", missingOptions)})");
            return;
        }

        // Needs password OR privateKeyPath
        var hasPassword = config.Options.ContainsKey("password") && !string.IsNullOrWhiteSpace(config.Options["password"]);
        var hasKey = config.Options.ContainsKey("privateKeyPath") && !string.IsNullOrWhiteSpace(config.Options["privateKeyPath"]);

        if (!hasPassword && !hasKey)
        {
            result.AddWarning($"SFTP storage '{sourceName}' requires either 'password' or 'privateKeyPath'.");
        }

        if (hasKey && !File.Exists(config.Options["privateKeyPath"]))
        {
            result.AddWarning($"SFTP private key file not found: {config.Options["privateKeyPath"]}");
        }
    }

    private void ValidateB2Storage(string sourceName, StorageConfig config, ConfigValidationResult result)
    {
        var requiredOptions = new[] { "keyId", "applicationKey", "bucketName" };
        var missingOptions = requiredOptions.Where(opt =>
            !config.Options.ContainsKey(opt) ||
            string.IsNullOrWhiteSpace(config.Options[opt]) ||
            config.Options[opt].Contains("your_")).ToList();

        if (missingOptions.Any())
        {
            result.AddWarning($"Backblaze B2 storage '{sourceName}' is not configured (missing: {string.Join(", ", missingOptions)})");
            return;
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
                    string regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                        .Replace("\\*", ".*")
                        .Replace("\\?", ".") + "$";
                    _ = new System.Text.RegularExpressions.Regex(regexPattern);
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
        if (string.IsNullOrWhiteSpace(region)) return false;

        // Full, up-to-date list of known AWS commercial regions
        var validRegions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // US
            "us-east-1", "us-east-2", "us-west-1", "us-west-2",

            // Africa
            "af-south-1",

            // Asia Pacific
            "ap-east-1", "ap-east-2",
            "ap-south-1", "ap-south-2",
            "ap-southeast-1", "ap-southeast-2", "ap-southeast-3", "ap-southeast-4", "ap-southeast-5", "ap-southeast-6", "ap-southeast-7",
            "ap-northeast-1", "ap-northeast-2", "ap-northeast-3",

            // Canada
            "ca-central-1", "ca-west-1",

            // Europe
            "eu-central-1", "eu-central-2",
            "eu-west-1", "eu-west-2", "eu-west-3",
            "eu-south-1", "eu-south-2",
            "eu-north-1",

            // Israel
            "il-central-1",

            // Mexico
            "mx-central-1",

            // Middle East
            "me-south-1", "me-central-1",

            // South America
            "sa-east-1"
        };

        // Prefer explicit known list, but keep a conservative prefix fallback for future regions
        return validRegions.Contains(region.Trim())
               || region.StartsWith("us-", StringComparison.OrdinalIgnoreCase)
               || region.StartsWith("eu-", StringComparison.OrdinalIgnoreCase)
               || region.StartsWith("ap-", StringComparison.OrdinalIgnoreCase)
               || region.StartsWith("sa-", StringComparison.OrdinalIgnoreCase)
               || region.StartsWith("ca-", StringComparison.OrdinalIgnoreCase)
               || region.StartsWith("af-", StringComparison.OrdinalIgnoreCase)
               || region.StartsWith("il-", StringComparison.OrdinalIgnoreCase)
               || region.StartsWith("mx-", StringComparison.OrdinalIgnoreCase)
               || region.StartsWith("me-", StringComparison.OrdinalIgnoreCase);
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
