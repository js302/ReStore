namespace ReStore.Core.src.sharing;

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

        try
        {
            _logger.Log($"Generating share link for {fileName}...");
            string link = await storage.GenerateShareLinkAsync(remotePath, expiration);
            return link;
        }
        catch (Exception ex)
        {
            // Clean up the uploaded file if link generation fails
            _logger.Log($"Share link generation failed, cleaning up uploaded file: {ex.Message}", LogLevel.Warning);
            try
            {
                await storage.DeleteAsync(remotePath);
            }
            catch (Exception cleanupEx)
            {
                _logger.Log($"Failed to cleanup uploaded file after share link failure: {cleanupEx.Message}", LogLevel.Warning);
            }
            throw;
        }
    }
}
