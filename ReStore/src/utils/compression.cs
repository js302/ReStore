using System.IO.Compression;

namespace ReStore.src.utils;

public class CompressionUtil
{
    public async Task CompressDirectoryAsync(string sourceDirectory, string outputZipFile)
    {
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputZipFile)!);
            using var archive = ZipFile.Open(outputZipFile, ZipArchiveMode.Create);
            
            var files = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase));
                
            foreach (var file in files)
            {
                var relativePath = Path.GetRelativePath(sourceDirectory, file);
                archive.CreateEntryFromFile(file, relativePath, CompressionLevel.Optimal);
            }
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
}
