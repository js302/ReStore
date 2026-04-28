using System.Reflection;

namespace ReStore.Core.src.utils;

public sealed class ConfigSetupResult
{
    public bool ConfigCreated { get; set; }
    public bool ExampleConfigUpdated { get; set; }
    public string? ConfigSourcePath { get; set; }
}

public static class ConfigInitializer
{
    private static readonly string USER_CONFIG_DIR = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "ReStore");

    private static readonly string USER_STATE_DIR = Path.Combine(USER_CONFIG_DIR, "state");
    private static readonly string USER_CONFIG_PATH = Path.Combine(USER_CONFIG_DIR, "config.json");
    private static readonly string USER_EXAMPLE_CONFIG_PATH = Path.Combine(USER_CONFIG_DIR, "config.example.json");

    public static ConfigSetupResult EnsureConfigurationSetup(ILogger? logger = null)
    {
        var setupResult = new ConfigSetupResult();

        try
        {
            Directory.CreateDirectory(USER_CONFIG_DIR);
            Directory.CreateDirectory(USER_STATE_DIR);
            logger?.Log($"Ensured ReStore directories exist at: {USER_CONFIG_DIR}", LogLevel.Debug);

            var appExamplePath = ResolveApplicationExampleConfigPath();
            if (appExamplePath != null && File.Exists(appExamplePath))
            {
                if (ShouldRefreshUserExampleConfig(appExamplePath))
                {
                    File.Copy(appExamplePath, USER_EXAMPLE_CONFIG_PATH, overwrite: true);
                    setupResult.ExampleConfigUpdated = true;
                    logger?.Log($"Updated user example configuration at: {USER_EXAMPLE_CONFIG_PATH}", LogLevel.Debug);
                }
            }

            var configExists = File.Exists(USER_CONFIG_PATH);

            if (!configExists)
            {
                if (appExamplePath != null && File.Exists(appExamplePath))
                {
                    File.Copy(appExamplePath, USER_CONFIG_PATH, overwrite: false);
                    setupResult.ConfigCreated = true;
                    setupResult.ConfigSourcePath = appExamplePath;
                    logger?.Log($"Created initial configuration at: {USER_CONFIG_PATH}", LogLevel.Info);
                    logger?.Log("You can modify the configuration through the GUI settings or by editing the config.json file.", LogLevel.Info);
                }
                else if (File.Exists(USER_EXAMPLE_CONFIG_PATH))
                {
                    File.Copy(USER_EXAMPLE_CONFIG_PATH, USER_CONFIG_PATH, overwrite: false);
                    setupResult.ConfigCreated = true;
                    setupResult.ConfigSourcePath = USER_EXAMPLE_CONFIG_PATH;
                    logger?.Log($"Created initial configuration from user example at: {USER_CONFIG_PATH}", LogLevel.Info);
                }
                else
                {
                    logger?.Log($"Configuration directory created at: {USER_CONFIG_DIR}", LogLevel.Warning);
                    logger?.Log("Warning: Could not find example configuration file to initialize config.json.", LogLevel.Warning);
                    logger?.Log("Please ensure the application is installed correctly.", LogLevel.Warning);
                }
            }
            else
            {
                logger?.Log($"Configuration loaded from: {USER_CONFIG_PATH}", LogLevel.Debug);
            }
        }
        catch (Exception ex)
        {
            logger?.Log($"Error during configuration setup: {ex.Message}", LogLevel.Error);
        }

        return setupResult;
    }

    internal static string? ResolveApplicationExampleConfigPath(string? assemblyLocation = null)
    {
        assemblyLocation ??= Assembly.GetExecutingAssembly().Location;
        var assemblyDirectory = Path.GetDirectoryName(assemblyLocation);

        if (string.IsNullOrEmpty(assemblyDirectory))
        {
            return null;
        }

        var inAppConfig = Path.Combine(assemblyDirectory, "config", "config.example.json");
        if (File.Exists(inAppConfig))
        {
            return inAppConfig;
        }

        var currentDir = new DirectoryInfo(assemblyDirectory);
        DirectoryInfo? projectRoot = null;

        while (currentDir?.Parent != null)
        {
            if ((currentDir.Name == "net9.0" || currentDir.Name == "net9.0-windows")
                && currentDir.Parent?.Name == "Debug" && currentDir.Parent.Parent?.Name == "bin")
            {
                projectRoot = currentDir.Parent.Parent.Parent;
                break;
            }
            else if ((currentDir.Name == "net9.0" || currentDir.Name == "net9.0-windows")
                && currentDir.Parent?.Name == "Release" && currentDir.Parent.Parent?.Name == "bin")
            {
                projectRoot = currentDir.Parent.Parent.Parent;
                break;
            }
            else if (currentDir.GetFiles("*.csproj").Length > 0)
            {
                projectRoot = currentDir;
                break;
            }

            currentDir = currentDir.Parent;
        }

        if (projectRoot != null && projectRoot.Exists)
        {
            var probe = projectRoot;
            for (int i = 0; i < 3 && probe != null; i++)
            {
                var candidate = Path.Combine(probe.FullName, "config", "config.example.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                probe = probe.Parent;
            }
        }

        return null;
    }

    public static string GetUserConfigDirectory() => USER_CONFIG_DIR;
    public static string GetUserStateDirectory() => USER_STATE_DIR;
    public static string GetUserConfigPath() => USER_CONFIG_PATH;
    public static string GetUserExampleConfigPath() => USER_EXAMPLE_CONFIG_PATH;

    private static bool ShouldRefreshUserExampleConfig(string sourceExamplePath)
    {
        if (!File.Exists(USER_EXAMPLE_CONFIG_PATH))
        {
            return true;
        }

        var sourceBytes = File.ReadAllBytes(sourceExamplePath);
        var targetBytes = File.ReadAllBytes(USER_EXAMPLE_CONFIG_PATH);

        if (sourceBytes.Length != targetBytes.Length)
        {
            return true;
        }

        return !sourceBytes.AsSpan().SequenceEqual(targetBytes);
    }
}
