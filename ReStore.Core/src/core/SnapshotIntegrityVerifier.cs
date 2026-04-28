using ReStore.Core.src.storage;
using ReStore.Core.src.utils;
using System.Security.Cryptography;
using System.Text.Json;

namespace ReStore.Core.src.core;

public class SnapshotVerificationResult
{
    public string RequestedBackupPath { get; set; } = string.Empty;
    public string ResolvedManifestPath { get; set; } = string.Empty;
    public string SnapshotId { get; set; } = string.Empty;
    public bool EncryptionEnabled { get; set; }
    public bool ManifestHashValid { get; set; }
    public int FileCount { get; set; }
    public int ChunkReferences { get; set; }
    public int UniqueChunks { get; set; }
    public int ChunksDownloaded { get; set; }
    public int MissingChunks { get; set; }
    public int InvalidChunks { get; set; }
    public int InvalidFiles { get; set; }
    public List<string> Errors { get; } = [];

    public bool IsValid => Errors.Count == 0;
}

public class SnapshotIntegrityVerifier(ILogger logger, IStorage storage, IPasswordProvider? passwordProvider = null, SystemState? systemState = null)
{
    private readonly ILogger _logger = logger;
    private readonly IStorage _storage = storage;
    private readonly IPasswordProvider? _passwordProvider = passwordProvider;
    private readonly SystemState? _systemState = systemState;
    private readonly EncryptionService _encryptionService = new(logger);

    public async Task<SnapshotVerificationResult> VerifyAsync(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            throw new ArgumentException("Backup path cannot be null or empty", nameof(backupPath));
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"restore_verify_{Guid.NewGuid():N}");
        var result = new SnapshotVerificationResult
        {
            RequestedBackupPath = backupPath
        };

        try
        {
            Directory.CreateDirectory(tempDirectory);

            var manifestPath = backupPath;
            string? expectedRootHashFromHead = null;
            if (backupPath.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase))
            {
                var headReference = await ResolveManifestPathFromHeadAsync(backupPath, tempDirectory);
                manifestPath = headReference.ManifestPath;
                expectedRootHashFromHead = headReference.RootHash;
            }

            if (!manifestPath.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Snapshot verification requires a manifest path or HEAD reference. Received unsupported artifact: {manifestPath}");
            }

            result.ResolvedManifestPath = manifestPath;

            var manifest = await DownloadManifestAsync(manifestPath, tempDirectory);
            result.SnapshotId = manifest.SnapshotId;
            result.EncryptionEnabled = manifest.EncryptionEnabled;
            result.FileCount = manifest.Files.Count;
            result.ChunkReferences = manifest.Files.Sum(file => file.Chunks.Count);

            result.ManifestHashValid = SnapshotManifestHasher.IsValid(manifest);
            if (!result.ManifestHashValid)
            {
                result.Errors.Add($"Manifest integrity check failed for snapshot: {manifestPath}");
            }

            if (!string.IsNullOrWhiteSpace(expectedRootHashFromHead)
                && !expectedRootHashFromHead.Equals(manifest.RootHash, StringComparison.OrdinalIgnoreCase))
            {
                result.Errors.Add(
                    $"Snapshot HEAD root hash mismatch for '{backupPath}'. Expected '{expectedRootHashFromHead}', actual '{manifest.RootHash}'.");
            }

            string? chunkStorageNamespace;
            try
            {
                chunkStorageNamespace = SnapshotStoragePaths.NormalizeChunkStorageNamespace(manifest.ChunkStorageNamespace);
            }
            catch (ArgumentException ex)
            {
                result.InvalidChunks++;
                result.Errors.Add($"Invalid chunk storage namespace '{manifest.ChunkStorageNamespace}': {ex.Message}");
                LogVerificationTelemetry(result);
                return result;
            }

            var encryptionMasterKey = await TryResolveEncryptionMasterKeyAsync(manifest);
            var uniqueChunkDescriptors = BuildUniqueChunkDescriptors(manifest, result);
            result.UniqueChunks = uniqueChunkDescriptors.Count;

            var verifiedChunks = await VerifyChunksAsync(
                uniqueChunkDescriptors,
                chunkStorageNamespace,
                manifest.EncryptionEnabled,
                encryptionMasterKey,
                tempDirectory,
                result);

            VerifyFiles(manifest, verifiedChunks, result);
            LogVerificationTelemetry(result);

            return result;
        }
        finally
        {
            TryDeleteTemporaryDirectory(tempDirectory);
        }
    }

    private async Task<SnapshotManifest> DownloadManifestAsync(string manifestPath, string tempDirectory)
    {
        var tempManifestPath = Path.Combine(tempDirectory, "snapshot.manifest.json");
        await _storage.DownloadAsync(manifestPath, tempManifestPath);

        var manifestJson = await File.ReadAllTextAsync(tempManifestPath);
        return JsonSerializer.Deserialize<SnapshotManifest>(manifestJson)
            ?? throw new InvalidOperationException($"Failed to deserialize snapshot manifest: {manifestPath}");
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

    private async Task<byte[]?> TryResolveEncryptionMasterKeyAsync(SnapshotManifest manifest)
    {
        if (!manifest.EncryptionEnabled)
        {
            return null;
        }

        if (_passwordProvider == null)
        {
            throw new InvalidOperationException("Encrypted snapshot verification requires a password provider");
        }

        var password = await _passwordProvider.GetPasswordAsync();
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Password required to verify encrypted snapshot");
        }

        if (string.IsNullOrWhiteSpace(manifest.EncryptionSalt))
        {
            throw new InvalidOperationException("Snapshot manifest is encrypted but encryption salt is missing");
        }

        var saltBytes = Convert.FromBase64String(manifest.EncryptionSalt);
        return _encryptionService.DeriveKeyFromPassword(password, saltBytes, manifest.KeyDerivationIterations);
    }

    private static Dictionary<string, ChunkDescriptor> BuildUniqueChunkDescriptors(
        SnapshotManifest manifest,
        SnapshotVerificationResult result)
    {
        var descriptors = new Dictionary<string, ChunkDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in manifest.Files.SelectMany(file => file.Chunks))
        {
            string normalizedChunkId;
            try
            {
                normalizedChunkId = SnapshotStoragePaths.NormalizeChunkId(chunk.ChunkId);
            }
            catch (ArgumentException ex)
            {
                result.InvalidChunks++;
                result.Errors.Add($"Invalid chunk id '{chunk.ChunkId}': {ex.Message}");
                continue;
            }

            var descriptor = new ChunkDescriptor(normalizedChunkId, chunk.ContentHash, chunk.PlainSizeBytes);
            if (!descriptors.TryAdd(descriptor.ChunkId, descriptor))
            {
                var existing = descriptors[descriptor.ChunkId];
                if (!existing.ContentHash.Equals(descriptor.ContentHash, StringComparison.OrdinalIgnoreCase)
                    || existing.PlainSizeBytes != descriptor.PlainSizeBytes)
                {
                    result.InvalidChunks++;
                    result.Errors.Add($"Inconsistent manifest metadata for chunk '{descriptor.ChunkId}'.");
                }
            }
        }

        return descriptors;
    }

    private async Task<Dictionary<string, byte[]>> VerifyChunksAsync(
        IReadOnlyDictionary<string, ChunkDescriptor> chunkDescriptors,
        string? chunkStorageNamespace,
        bool encrypted,
        byte[]? encryptionMasterKey,
        string tempDirectory,
        SnapshotVerificationResult result)
    {
        var verifiedChunks = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var descriptor in chunkDescriptors.Values.OrderBy(value => value.ChunkId, StringComparer.OrdinalIgnoreCase))
        {
            var remoteChunkPath = SnapshotStoragePaths.GetChunkPath(descriptor.ChunkId, chunkStorageNamespace);
            var tempChunkPath = Path.Combine(tempDirectory, $"{descriptor.ChunkId}.chunk");

            try
            {
                await _storage.DownloadAsync(remoteChunkPath, tempChunkPath);
                result.ChunksDownloaded++;
            }
            catch (FileNotFoundException)
            {
                result.MissingChunks++;
                result.Errors.Add($"Missing chunk object: {remoteChunkPath}");
                continue;
            }
            catch (Exception ex)
            {
                result.InvalidChunks++;
                result.Errors.Add($"Failed to download chunk '{descriptor.ChunkId}': {ex.Message}");
                continue;
            }

            try
            {
                var storedBytes = await File.ReadAllBytesAsync(tempChunkPath);
                var plainBytes = encrypted
                    ? EncryptionService.DecryptChunkDeterministic(storedBytes, encryptionMasterKey!, descriptor.ChunkId)
                    : storedBytes;

                if (plainBytes.Length != descriptor.PlainSizeBytes)
                {
                    result.InvalidChunks++;
                    result.Errors.Add(
                        $"Chunk size mismatch for '{descriptor.ChunkId}'. Expected {descriptor.PlainSizeBytes}, actual {plainBytes.Length}.");
                    continue;
                }

                var chunkHash = Convert.ToHexStringLower(SHA256.HashData(plainBytes));
                if (!chunkHash.Equals(descriptor.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    result.InvalidChunks++;
                    result.Errors.Add(
                        $"Chunk hash mismatch for '{descriptor.ChunkId}'. Expected '{descriptor.ContentHash}', actual '{chunkHash}'.");
                    continue;
                }

                verifiedChunks[descriptor.ChunkId] = plainBytes;
            }
            catch (CryptographicException ex)
            {
                result.InvalidChunks++;
                result.Errors.Add($"Failed to decrypt chunk '{descriptor.ChunkId}': {ex.Message}");
            }
            catch (Exception ex)
            {
                result.InvalidChunks++;
                result.Errors.Add($"Failed to validate chunk '{descriptor.ChunkId}': {ex.Message}");
            }
            finally
            {
                TryDeleteTemporaryFile(tempChunkPath);
            }
        }

        return verifiedChunks;
    }

    private static void VerifyFiles(
        SnapshotManifest manifest,
        IReadOnlyDictionary<string, byte[]> verifiedChunks,
        SnapshotVerificationResult result)
    {
        foreach (var file in manifest.Files.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            using var fileHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long reconstructedSize = 0;
            bool hasMissingChunk = false;

            foreach (var chunk in file.Chunks)
            {
                string normalizedChunkId;
                try
                {
                    normalizedChunkId = SnapshotStoragePaths.NormalizeChunkId(chunk.ChunkId);
                }
                catch (ArgumentException ex)
                {
                    hasMissingChunk = true;
                    result.Errors.Add($"File '{file.RelativePath}' references invalid chunk id '{chunk.ChunkId}': {ex.Message}");
                    continue;
                }

                if (!verifiedChunks.TryGetValue(normalizedChunkId, out var chunkBytes))
                {
                    hasMissingChunk = true;
                    continue;
                }

                fileHasher.AppendData(chunkBytes);
                reconstructedSize += chunkBytes.Length;
            }

            if (hasMissingChunk)
            {
                result.InvalidFiles++;
                result.Errors.Add(
                    $"File '{file.RelativePath}' could not be reconstructed because one or more chunks are missing or invalid.");
                continue;
            }

            var fileHash = Convert.ToHexStringLower(fileHasher.GetHashAndReset());
            if (!fileHash.Equals(file.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                result.InvalidFiles++;
                result.Errors.Add(
                    $"File hash mismatch for '{file.RelativePath}'. Expected '{file.ContentHash}', actual '{fileHash}'.");
            }

            if (reconstructedSize != file.SizeBytes)
            {
                result.InvalidFiles++;
                result.Errors.Add(
                    $"File size mismatch for '{file.RelativePath}'. Expected {file.SizeBytes}, actual {reconstructedSize}.");
            }
        }
    }

    private void LogVerificationTelemetry(SnapshotVerificationResult result)
    {
        var verifiedChunks = Math.Max(0, result.UniqueChunks - result.MissingChunks - result.InvalidChunks);
        var chunkVerificationRatio = result.UniqueChunks == 0 ? 0 : (double)verifiedChunks / result.UniqueChunks;
        var failureCount = result.Errors.Count;

        _logger.Log(
            $"Verification telemetry: path='{result.ResolvedManifestPath}', snapshot='{result.SnapshotId}', success={result.IsValid}, files={result.FileCount}, chunkRefs={result.ChunkReferences}, uniqueChunks={result.UniqueChunks}, downloadedChunks={result.ChunksDownloaded}, missingChunks={result.MissingChunks}, invalidChunks={result.InvalidChunks}, invalidFiles={result.InvalidFiles}, verifiedChunkRatio={chunkVerificationRatio:P2}, validationFailures={failureCount}",
            result.IsValid ? LogLevel.Info : LogLevel.Warning);

        _systemState?.RecordVerificationTelemetry(
            success: result.IsValid,
            fileCount: result.FileCount,
            chunkReferences: result.ChunkReferences,
            uniqueChunks: result.UniqueChunks,
            downloadedChunks: result.ChunksDownloaded,
            missingChunks: result.MissingChunks,
            invalidChunks: result.InvalidChunks,
            invalidFiles: result.InvalidFiles,
            validationFailures: failureCount);
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void TryDeleteTemporaryDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed record ChunkDescriptor(string ChunkId, string ContentHash, int PlainSizeBytes);

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
