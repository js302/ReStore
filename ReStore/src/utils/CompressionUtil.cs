using System.IO.Compression;

namespace ReStore.src.utils;

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

            using var archive = ZipFile.Open(destinationArchivePath, ZipArchiveMode.Create);
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
        });
    }
}
