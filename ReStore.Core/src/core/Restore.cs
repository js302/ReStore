using ReStore.Core.src.utils;
using ReStore.Core.src.storage;
using System.Security.Cryptography;
using System.Text.Json;

namespace ReStore.Core.src.core;

public class Restore(ILogger logger, IStorage storage, IPasswordProvider? passwordProvider = null, SystemState? systemState = null)
{
    private readonly ILogger _logger = logger;
    private readonly IStorage _storage = storage;
    private readonly IPasswordProvider? _passwordProvider = passwordProvider;
    private readonly SystemState? _systemState = systemState;
    private readonly EncryptionService _encryptionService = new(logger);

    public async Task RestoreFromBackupAsync(string backupPath, string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            throw new ArgumentException("Backup path cannot be null or empty", nameof(backupPath));
        }

        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ArgumentException("Target directory cannot be null or empty", nameof(targetDirectory));
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"restore_snapshot_{Guid.NewGuid():N}");
        var tempManifestPath = Path.Combine(tempDirectory, "snapshot.manifest.json");
        SnapshotManifest? loadedManifest = null;
        string resolvedManifestPath = backupPath;
        string? expectedRootHashFromHead = null;
        var restoreTelemetry = new RestoreTelemetry();

        try
        {
            _logger.Log($"Starting restore from {backupPath} to {targetDirectory}", LogLevel.Info);

            Directory.CreateDirectory(tempDirectory);
            Directory.CreateDirectory(targetDirectory);

            var manifestPath = backupPath;
            if (backupPath.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase))
            {
                var headReference = await ResolveManifestPathFromHeadAsync(backupPath, tempDirectory);
                manifestPath = headReference.ManifestPath;
                expectedRootHashFromHead = headReference.RootHash;
            }

            resolvedManifestPath = manifestPath;

            if (!manifestPath.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"User-file restore requires a snapshot manifest path. Received unsupported artifact: {manifestPath}");
            }

            _logger.Log($"Downloading snapshot manifest: {manifestPath}", LogLevel.Debug);
            await _storage.DownloadAsync(manifestPath, tempManifestPath);

            var manifestJson = await File.ReadAllTextAsync(tempManifestPath);
            var manifest = JsonSerializer.Deserialize<SnapshotManifest>(manifestJson)
                ?? throw new InvalidOperationException($"Failed to deserialize snapshot manifest: {manifestPath}");
            loadedManifest = manifest;

            if (!SnapshotManifestHasher.IsValid(manifest))
            {
                throw new InvalidOperationException($"Manifest integrity check failed for snapshot: {manifestPath}");
            }

            if (!string.IsNullOrWhiteSpace(expectedRootHashFromHead)
                && !expectedRootHashFromHead.Equals(manifest.RootHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Snapshot HEAD root hash mismatch for '{backupPath}'. Expected '{expectedRootHashFromHead}', actual '{manifest.RootHash}'.");
            }

            restoreTelemetry.FileCountExpected = manifest.Files.Count;
            restoreTelemetry.ChunkReferencesExpected = manifest.Files.Sum(file => file.Chunks.Count);

            byte[]? encryptionMasterKey = null;
            if (manifest.EncryptionEnabled)
            {
                if (_passwordProvider == null)
                {
                    throw new InvalidOperationException("Encrypted snapshot detected but no password provider available");
                }

                var password = await _passwordProvider.GetPasswordAsync();
                if (string.IsNullOrWhiteSpace(password))
                {
                    throw new InvalidOperationException("Password required to decrypt snapshot");
                }

                if (string.IsNullOrWhiteSpace(manifest.EncryptionSalt))
                {
                    throw new InvalidOperationException("Snapshot manifest is encrypted but encryption salt is missing");
                }

                var saltBytes = Convert.FromBase64String(manifest.EncryptionSalt);
                encryptionMasterKey = _encryptionService.DeriveKeyFromPassword(
                    password,
                    saltBytes,
                    manifest.KeyDerivationIterations);
            }

            var chunkCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var chunkStorageNamespace = SnapshotStoragePaths.NormalizeChunkStorageNamespace(manifest.ChunkStorageNamespace);
            foreach (var file in manifest.Files.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                var outputPath = ResolveSafeOutputPath(targetDirectory, file.RelativePath);

                var outputDirectory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrWhiteSpace(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                await using (var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
                {
                    foreach (var chunk in file.Chunks)
                    {
                        var chunkBytes = await LoadChunkBytesAsync(
                            chunk,
                            chunkStorageNamespace,
                            manifest.EncryptionEnabled,
                            encryptionMasterKey,
                            tempDirectory,
                            chunkCache,
                            restoreTelemetry);

                        restoreTelemetry.ChunkReferencesProcessed++;

                        await outputStream.WriteAsync(chunkBytes);
                    }

                    await outputStream.FlushAsync();
                }

                var fileHash = FileHasher.ComputeHash(outputPath);
                if (!fileHash.Equals(file.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Restored file hash mismatch for '{file.RelativePath}'. Expected '{file.ContentHash}', actual '{fileHash}'.");
                }

                restoreTelemetry.FileCountCompleted++;
            }

            _logger.Log("Restore completed successfully.", LogLevel.Info);
            LogRestoreTelemetry(resolvedManifestPath, loadedManifest, restoreTelemetry, wasSuccessful: true);
        }
        catch (FileNotFoundException ex)
        {
            restoreTelemetry.FailureCategory = ClassifyRestoreFailure(ex);
            restoreTelemetry.FailureMessage = ex.Message;
            LogRestoreTelemetry(resolvedManifestPath, loadedManifest, restoreTelemetry, wasSuccessful: false);

            _logger.Log($"Restore failed: required snapshot artifact was not found. {ex.Message}", LogLevel.Error);
            throw;
        }
        catch (CryptographicException ex)
        {
            _passwordProvider?.ClearPassword();

            restoreTelemetry.FailureCategory = ClassifyRestoreFailure(ex);
            restoreTelemetry.FailureMessage = ex.Message;
            LogRestoreTelemetry(resolvedManifestPath, loadedManifest, restoreTelemetry, wasSuccessful: false);

            _logger.Log($"Restore failed: snapshot decryption integrity check failed. {ex.Message}", LogLevel.Error);
            throw;
        }
        catch (Exception ex)
        {
            restoreTelemetry.FailureCategory = ClassifyRestoreFailure(ex);
            restoreTelemetry.FailureMessage = ex.Message;
            LogRestoreTelemetry(resolvedManifestPath, loadedManifest, restoreTelemetry, wasSuccessful: false);

            _logger.Log($"Restore failed: {ex.Message}", LogLevel.Error);
            throw;
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                try
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
                catch (Exception cleanupEx)
                {
                    _logger.Log($"Failed to clean up restore temporary directory {tempDirectory}: {cleanupEx.Message}", LogLevel.Warning);
                }
            }
        }
    }

    private async Task<SnapshotHeadReference> ResolveManifestPathFromHeadAsync(string headPath, string tempDirectory)
    {
        var tempHeadPath = Path.Combine(tempDirectory, "snapshot.head");
        await _storage.DownloadAsync(headPath, tempHeadPath);

        var lines = await File.ReadAllLinesAsync(tempHeadPath);
        var nonEmptyLines = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .ToList();

        var manifestPath = nonEmptyLines.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new InvalidOperationException($"Snapshot HEAD did not contain a manifest path: {headPath}");
        }

        var rootHash = nonEmptyLines.Count > 1 ? nonEmptyLines[1] : null;
        return new SnapshotHeadReference(manifestPath, string.IsNullOrWhiteSpace(rootHash) ? null : rootHash);
    }

    private async Task<byte[]> LoadChunkBytesAsync(
        SnapshotChunkManifestEntry chunk,
        string? chunkStorageNamespace,
        bool encrypted,
        byte[]? encryptionMasterKey,
        string tempDirectory,
        Dictionary<string, byte[]> chunkCache,
        RestoreTelemetry telemetry)
    {
        var normalizedChunkId = SnapshotStoragePaths.NormalizeChunkId(chunk.ChunkId);

        if (chunkCache.TryGetValue(normalizedChunkId, out var cachedBytes))
        {
            telemetry.ChunkCacheHits++;
            return cachedBytes;
        }

        var chunkRemotePath = SnapshotStoragePaths.GetChunkPath(normalizedChunkId, chunkStorageNamespace);
        var chunkTempPath = Path.Combine(tempDirectory, $"{normalizedChunkId}.chunk");
        await _storage.DownloadAsync(chunkRemotePath, chunkTempPath);
        telemetry.ChunkDownloads++;

        var storedBytes = await File.ReadAllBytesAsync(chunkTempPath);
        var plaintextBytes = encrypted
            ? EncryptionService.DecryptChunkDeterministic(storedBytes, encryptionMasterKey!, normalizedChunkId)
            : storedBytes;

        if (plaintextBytes.Length != chunk.PlainSizeBytes)
        {
            throw new InvalidOperationException(
                $"Chunk size mismatch for chunk '{normalizedChunkId}'. Expected {chunk.PlainSizeBytes}, actual {plaintextBytes.Length}.");
        }

        var actualChunkHash = Convert.ToHexStringLower(SHA256.HashData(plaintextBytes));
        if (!actualChunkHash.Equals(chunk.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Chunk hash mismatch for chunk '{normalizedChunkId}'. Expected '{chunk.ContentHash}', actual '{actualChunkHash}'.");
        }

        chunkCache[normalizedChunkId] = plaintextBytes;
        return plaintextBytes;
    }

    private static string ResolveSafeOutputPath(string targetDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("Snapshot manifest contains an empty relative file path.");
        }

        var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedRelativePath))
        {
            throw new InvalidOperationException(
                $"Snapshot manifest path '{relativePath}' must be relative to the restore target directory.");
        }

        var normalizedTargetDirectory = Path.GetFullPath(targetDirectory);
        var combinedPath = Path.GetFullPath(Path.Combine(normalizedTargetDirectory, normalizedRelativePath));
        var normalizedTargetPrefix = normalizedTargetDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedTargetDirectory
            : normalizedTargetDirectory + Path.DirectorySeparatorChar;

        if (!combinedPath.StartsWith(normalizedTargetPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Snapshot manifest path '{relativePath}' resolves outside the restore target directory.");
        }

        return combinedPath;
    }

    private static string ClassifyRestoreFailure(Exception exception)
    {
        if (exception is FileNotFoundException)
        {
            return "missing-artifact";
        }

        if (exception is CryptographicException)
        {
            return "decryption-integrity-failure";
        }

        if (exception is InvalidOperationException invalidOperationException)
        {
            if (invalidOperationException.Message.Contains("Manifest integrity check failed", StringComparison.OrdinalIgnoreCase))
            {
                return "manifest-integrity-failure";
            }

            if (invalidOperationException.Message.Contains("HEAD root hash mismatch", StringComparison.OrdinalIgnoreCase))
            {
                return "manifest-integrity-failure";
            }

            if (invalidOperationException.Message.Contains("Chunk hash mismatch", StringComparison.OrdinalIgnoreCase)
                || invalidOperationException.Message.Contains("Chunk size mismatch", StringComparison.OrdinalIgnoreCase))
            {
                return "chunk-validation-failure";
            }

            if (invalidOperationException.Message.Contains("Restored file hash mismatch", StringComparison.OrdinalIgnoreCase))
            {
                return "file-validation-failure";
            }
        }

        return "unexpected-error";
    }

    private void LogRestoreTelemetry(
        string manifestPath,
        SnapshotManifest? manifest,
        RestoreTelemetry telemetry,
        bool wasSuccessful)
    {
        var cacheHitRatio = telemetry.ChunkReferencesProcessed == 0
            ? 0
            : (double)telemetry.ChunkCacheHits / telemetry.ChunkReferencesProcessed;

        var validationFailures = wasSuccessful ? 0 : 1;
        var snapshotId = manifest?.SnapshotId ?? "unknown";

        _logger.Log(
            $"Restore telemetry: manifest='{manifestPath}', snapshot='{snapshotId}', success={wasSuccessful}, filesExpected={telemetry.FileCountExpected}, filesRestored={telemetry.FileCountCompleted}, chunkRefsExpected={telemetry.ChunkReferencesExpected}, chunkRefsProcessed={telemetry.ChunkReferencesProcessed}, chunkDownloads={telemetry.ChunkDownloads}, chunkCacheHits={telemetry.ChunkCacheHits}, chunkCacheHitRatio={cacheHitRatio:P2}, validationFailures={validationFailures}, failureCategory='{telemetry.FailureCategory ?? "none"}'",
            wasSuccessful ? LogLevel.Info : LogLevel.Warning);

        _systemState?.RecordRestoreTelemetry(
            success: wasSuccessful,
            filesExpected: telemetry.FileCountExpected,
            filesRestored: telemetry.FileCountCompleted,
            chunkReferencesExpected: telemetry.ChunkReferencesExpected,
            chunkReferencesProcessed: telemetry.ChunkReferencesProcessed,
            chunkDownloads: telemetry.ChunkDownloads,
            chunkCacheHits: telemetry.ChunkCacheHits,
            failureCategory: telemetry.FailureCategory,
            validationFailures: validationFailures);
    }

    private sealed class RestoreTelemetry
    {
        public int FileCountExpected { get; set; }
        public int FileCountCompleted { get; set; }
        public int ChunkReferencesExpected { get; set; }
        public int ChunkReferencesProcessed { get; set; }
        public int ChunkDownloads { get; set; }
        public int ChunkCacheHits { get; set; }
        public string? FailureCategory { get; set; }
        public string? FailureMessage { get; set; }
    }

    private sealed class SnapshotHeadReference
    {
        public SnapshotHeadReference(string manifestPath, string? rootHash)
        {
            ManifestPath = manifestPath;
            RootHash = rootHash;
        }

        public string ManifestPath { get; }
        public string? RootHash { get; }
    }
}
