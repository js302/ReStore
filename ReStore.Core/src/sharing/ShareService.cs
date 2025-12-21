namespace ReStore.Core.src.sharing;

using ReStore.Core.src.storage;
using ReStore.Core.src.utils;
using System;
using System.IO;
using System.Threading.Tasks;

public class ShareService(IConfigManager configManager, ILogger logger)
{
    private readonly IConfigManager _configManager = configManager;
    private readonly ILogger _logger = logger;

    public async Task<string> ShareFileAsync(string localFilePath, string storageType, TimeSpan expiration)
    {
        if (!File.Exists(localFilePath))
        {
            throw new FileNotFoundException($"File not found: {localFilePath}");
        }

        // Create storage instance
        using var storage = await _configManager.CreateStorageAsync(storageType);
        
        // Define remote path in a "shared" folder
        string fileName = Path.GetFileName(localFilePath);
        string remotePath = $"shared/{Guid.NewGuid()}/{fileName}"; 

        _logger.Log($"Uploading {fileName} to {storageType} for sharing...");
        
        // Upload unencrypted
        await storage.UploadAsync(localFilePath, remotePath);

        _logger.Log($"Generating share link for {fileName}...");
        string link = await storage.GenerateShareLinkAsync(remotePath, expiration);
        
        return link;
    }
}
