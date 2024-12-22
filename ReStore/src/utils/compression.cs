using System.IO.Compression;

namespace ReStore.Utils;

public class CompressionUtil
{
    public async Task CompressDirectoryAsync(string sourceDirectory, string outputZipFile)
    {
        await Task.Run(() => ZipFile.CreateFromDirectory(
            sourceDirectory,
            outputZipFile,
            CompressionLevel.Optimal,
            includeBaseDirectory: false));
    }

    public async Task DecompressAsync(string zipFile, string outputDirectory)
    {
        await Task.Run(() => ZipFile.ExtractToDirectory(zipFile, outputDirectory, overwriteFiles: true));
    }
}
