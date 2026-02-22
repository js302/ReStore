using System.IO.Compression;

namespace ReStore.Core.src.utils;

public class CompressionUtil
{
    public static async Task CompressDirectoryAsync(string sourceDirectory, string outputZipFile)
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

    public static async Task DecompressAsync(string zipFile, string outputDirectory)
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

    public static async Task CompressFilesAsync(IEnumerable<string> filesToInclude, string baseDirectory, string destinationArchivePath)
    {
        await Task.Run(() =>
        {
            if (File.Exists(destinationArchivePath))
            {
                File.Delete(destinationArchivePath);
            }

            using var fs = new FileStream(destinationArchivePath, FileMode.Create);
            using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
            foreach (var filePath in filesToInclude)
            {
                if (!File.Exists(filePath))
                {
                    continue;
                }

                // Calculate the relative path within the archive
                var entryName = Path.GetRelativePath(baseDirectory, filePath);
                entryName = entryName.Replace(Path.DirectorySeparatorChar, '/');

                archive.CreateEntryFromFile(filePath, entryName, CompressionLevel.Optimal);
            }
        });
    }

    public static async Task<string> CompressAndEncryptAsync(string sourceZip, string password, string salt, ILogger logger)
    {
        var encryptedPath = sourceZip + ".enc";
        var metadataPath = encryptedPath + ".meta";

        var saltBytes = Convert.FromBase64String(salt);
        var encryptionService = new EncryptionService(logger);
        var metadata = await encryptionService.EncryptFileAsync(sourceZip, encryptedPath, password, saltBytes);
        await EncryptionService.SaveMetadataAsync(metadata, metadataPath);

        File.Delete(sourceZip);

        return encryptedPath;
    }

    public static async Task<string> DecryptAndDecompressAsync(string encryptedZip, string password, string outputDirectory, ILogger logger)
    {
        var decryptedZip = encryptedZip.EndsWith(".enc", StringComparison.OrdinalIgnoreCase)
            ? encryptedZip.Substring(0, encryptedZip.Length - 4)
            : encryptedZip + ".decrypted";
        var metadataPath = encryptedZip + ".meta";

        var encryptionService = new EncryptionService(logger);
        var metadata = await EncryptionService.LoadMetadataAsync(metadataPath);
        await encryptionService.DecryptFileAsync(encryptedZip, decryptedZip, password, metadata);

        try
        {
            await DecompressAsync(decryptedZip, outputDirectory);
        }
        finally
        {
            if (File.Exists(decryptedZip))
            {
                File.Delete(decryptedZip);
            }
        }

        return outputDirectory;
    }
}
