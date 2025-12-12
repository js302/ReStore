using System.IO.Compression;

namespace ReStore.Core.src.utils;

public class CompressionUtil
{
    public async Task CompressDirectoryAsync(string sourceDirectory, string outputZipFile)
    {
        await Task.Run(() =>
        {
            if (File.Exists(outputZipFile))
            {
                File.Delete(outputZipFile);
            }
            ZipFile.CreateFromDirectory(sourceDirectory, outputZipFile, CompressionLevel.Optimal, false);
        });
    }

    public async Task DecompressAsync(string zipFile, string outputDirectory)
    {
        zipFile = Path.GetFullPath(Environment.ExpandEnvironmentVariables(zipFile));
        outputDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(outputDirectory));

        await Task.Run(() =>
        {
            if (File.Exists(zipFile))
            {
                ZipFile.ExtractToDirectory(zipFile, outputDirectory, overwriteFiles: true);
            }
            else
            {
                throw new FileNotFoundException($"Zip file not found: {zipFile}");
            }
        });
    }

    public async Task CompressFilesAsync(IEnumerable<string> filesToInclude, string baseDirectory, string destinationArchivePath)
    {
        await Task.Run(() =>
        {
            if (File.Exists(destinationArchivePath))
            {
                File.Delete(destinationArchivePath);
            }

            using (var fs = new FileStream(destinationArchivePath, FileMode.Create))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var filePath in filesToInclude)
                {
                    // Ensure the file exists before trying to add it
                    if (!File.Exists(filePath))
                    {
                        // Log this?
                        continue;
                    }

                    // Calculate the relative path within the archive
                    var entryName = Path.GetRelativePath(baseDirectory, filePath);
                    // Normalize directory separators for zip standard
                    entryName = entryName.Replace(Path.DirectorySeparatorChar, '/');

                    archive.CreateEntryFromFile(filePath, entryName, CompressionLevel.Optimal);
                }
            }
        });
    }

    public async Task<string> CompressAndEncryptAsync(string sourceZip, string password, string salt, ILogger logger)
    {
        var encryptedPath = sourceZip + ".enc";
        var metadataPath = encryptedPath + ".meta";
        
        var saltBytes = Convert.FromBase64String(salt);
        var encryptionService = new EncryptionService(logger);
        var metadata = await encryptionService.EncryptFileAsync(sourceZip, encryptedPath, password, saltBytes);
        await encryptionService.SaveMetadataAsync(metadata, metadataPath);
        
        File.Delete(sourceZip);
        
        return encryptedPath;
    }

    public async Task<string> DecryptAndDecompressAsync(string encryptedZip, string password, string outputDirectory, ILogger logger)
    {
        var decryptedZip = encryptedZip.Replace(".enc", "");
        var metadataPath = encryptedZip + ".meta";
        
        var encryptionService = new EncryptionService(logger);
        var metadata = await encryptionService.LoadMetadataAsync(metadataPath);
        await encryptionService.DecryptFileAsync(encryptedZip, decryptedZip, password, metadata);
        
        await DecompressAsync(decryptedZip, outputDirectory);
        
        File.Delete(decryptedZip);
        
        return outputDirectory;
    }
}
