using ReStore.Core.src.backup;
﻿using ReStore.Core.src.storage;
using ReStore.Core.src.utils;
using System.Security.Cryptography;
using System.Text.Json;

namespace ReStore.Core.src.core;

/// <summary>How thoroughly a snapshot is verified.</summary>
public enum VerificationMode
{
    /// <summary>Every chunk is downloaded, decrypted and hashed and every file is rebuilt and hashed.</summary>
    Full,

    /// <summary>Manifest, existence and stored size of every chunk; no chunk is downloaded.</summary>
    Quick,

    /// <summary>Quick checks for every chunk, plus a full check of a random percentage of them.</summary>
    Sample
}

public static class VerificationModes
{
    public const int DefaultSamplePercent = 5;

    public static bool TryParse(string? value, out VerificationMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "full":
            case null:
            case "":
                mode = VerificationMode.Full;
                return true;
            case "quick":
                mode = VerificationMode.Quick;
                return true;
            case "sample":
                mode = VerificationMode.Sample;
                return true;
            default:
                mode = VerificationMode.Full;
                return false;
        }
    }

    public static string Name(VerificationMode mode) => mode.ToString().ToLowerInvariant();
}

/// <summary>Options for <see cref="SnapshotIntegrityVerifier.VerifyAsync"/>. The default is a full verification.</summary>
/// <param name="Mode">How thoroughly to verify.</param>
/// <param name="SamplePercent">Percentage (1 to 100) of unique chunks fully verified in sample mode.</param>
/// <param name="Seed">Seed of the random chunk selection in sample mode; null picks one (it is reported in the result).</param>
public sealed record VerificationOptions(
    VerificationMode Mode = VerificationMode.Full,
    int SamplePercent = VerificationModes.DefaultSamplePercent,
    int? Seed = null)
{
    public static VerificationOptions Default { get; } = new();
}

public class SnapshotVerificationResult
{
    public VerificationMode Mode { get; set; } = VerificationMode.Full;

    /// <summary>Sample mode: the seed that selected the chunks, so the same sample can be re-checked.</summary>
    public int? SampleSeed { get; set; }

    /// <summary>Sample mode: how many unique chunks were fully verified.</summary>
    public int ChunksSampled { get; set; }

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

public class SnapshotIntegrityVerifier(ILogger logger, IStorage storage, IPasswordProvider? passwordProvider = null, SystemState? systemState = null, string? tempRoot = null, RepositoryCoordinator? coordinator = null)
{
    private readonly RepositoryCoordinator _coordinator = coordinator ?? RepositoryCoordinator.Instance;

    // Matches the bound Restore uses, so both paths have the same footprint.
    private const long MaxChunkCacheBytes = 64L * 1024 * 1024;

    private readonly ILogger _logger = logger;
    private readonly IStorage _storage = storage;
    private readonly IPasswordProvider? _passwordProvider = passwordProvider;
    private readonly SystemState? _systemState = systemState;
    private readonly string _tempRoot = tempRoot ?? Path.GetTempPath();
    private readonly EncryptionService _encryptionService = new(logger);

    public async Task<SnapshotVerificationResult> VerifyAsync(
        string backupPath,
        IProgress<VerificationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        VerificationOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            throw new ArgumentException("Backup path cannot be null or empty", nameof(backupPath));
        }

        options ??= VerificationOptions.Default;
        if (options.Mode == VerificationMode.Sample && options.SamplePercent is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SamplePercent must be between 1 and 100.");
        }

        // Any password prompt happens before the lease, not while it blocks other operations.
        await PasswordPrefetch.WarmForSnapshotAsync(_logger, _storage, _passwordProvider, backupPath, _tempRoot, cancellationToken).ConfigureAwait(false);
        var readLease = await _coordinator.AcquireReadAsync($"Verifying {backupPath}", cancellationToken).ConfigureAwait(false);
        await using var readLeaseScope = readLease.ConfigureAwait(false);
        using var activity = ActivityRecorder.Begin(_systemState, ActivityType.Verify, backupPath, cancellationToken);

        var tempDirectory = Path.Combine(_tempRoot, $"restore_verify_{Guid.NewGuid():N}");
        var result = new SnapshotVerificationResult
        {
            RequestedBackupPath = backupPath,
            Mode = options.Mode
        };
        RepositoryKeys? keysToZero = null;

        try
        {
            Directory.CreateDirectory(tempDirectory);

            var manifestPath = backupPath;
            string? expectedRootHashFromHead = null;
            if (backupPath.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase))
            {
                var headReference = await ResolveManifestPathFromHeadAsync(backupPath, tempDirectory, cancellationToken).ConfigureAwait(false);
                manifestPath = headReference.ManifestPath;
                expectedRootHashFromHead = headReference.RootHash;
            }

            if (!manifestPath.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Snapshot verification requires a manifest path or HEAD reference. Received unsupported artifact: {manifestPath}");
            }

            result.ResolvedManifestPath = manifestPath;

            SnapshotManifest manifest;
            try
            {
                manifest = await DownloadManifestAsync(manifestPath, tempDirectory, cancellationToken).ConfigureAwait(false);
            }
            catch (WrongRepositoryPasswordException)
            {
                // The repository key file did not open: stop before any chunk is downloaded.
                _passwordProvider?.ClearPassword();
                result.Errors.Add("Wrong password: it does not unlock the repository key file. Verification stopped.");
                return result;
            }

            keysToZero = manifest.Keys;
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

            // Quick mode never decrypts a chunk, so it needs no key beyond what opening the manifest already took.
            var encryptionMasterKey = options.Mode == VerificationMode.Quick
                ? null
                : await TryResolveEncryptionMasterKeyAsync(manifest).ConfigureAwait(false);
            var uniqueChunkDescriptors = BuildUniqueChunkDescriptors(manifest, result);
            result.UniqueChunks = uniqueChunkDescriptors.Count;

            if (options.Mode == VerificationMode.Full)
            {
                await VerifyContentAsync(
                    manifest,
                    uniqueChunkDescriptors,
                    chunkStorageNamespace,
                    encryptionMasterKey,
                    tempDirectory,
                    result,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await VerifyChunkPresenceAsync(
                    manifest,
                    uniqueChunkDescriptors,
                    chunkStorageNamespace,
                    encryptionMasterKey,
                    tempDirectory,
                    options,
                    result,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }

            LogVerificationTelemetry(result);
            if (result.IsValid)
            {
                activity.Succeeded(options.Mode switch
                {
                    VerificationMode.Quick => $"Quick: {result.FileCount} file(s), {result.UniqueChunks} chunk(s) present with the expected size",
                    VerificationMode.Sample => $"Sample ({options.SamplePercent}%, seed {result.SampleSeed}): {result.ChunksSampled} of {result.UniqueChunks} chunk(s) fully verified, all present",
                    _ => $"{result.FileCount} file(s), {result.UniqueChunks} chunk(s) verified"
                });
            }
            else
            {
                activity.Failed(result.Errors[0]);
            }

            return result;
        }
        finally
        {
            keysToZero?.Dispose();
            TryDeleteTemporaryDirectory(tempDirectory);
        }
    }

    private async Task<SnapshotManifest> DownloadManifestAsync(
        string manifestPath,
        string tempDirectory,
        CancellationToken cancellationToken = default)
    {
        var tempManifestPath = Path.Combine(tempDirectory, "snapshot.manifest.json");
        await _storage.DownloadAsync(manifestPath, tempManifestPath).ConfigureAwait(false);

        // Encrypted v3: unlocks the repository key file with the password (a wrong password fails here, before any chunk download).
        using var reader = new RepositoryManifestReader(_logger, _storage, _passwordProvider, zeroKeysOnDispose: false);
        return await reader.ReadFileAsync(tempManifestPath, manifestPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SnapshotHeadReference> ResolveManifestPathFromHeadAsync(
        string headPath,
        string tempDirectory,
        CancellationToken cancellationToken = default)
    {
        var tempHeadPath = Path.Combine(tempDirectory, "snapshot.head");
        await _storage.DownloadAsync(headPath, tempHeadPath).ConfigureAwait(false);

        var lines = await File.ReadAllLinesAsync(tempHeadPath, cancellationToken).ConfigureAwait(false);
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

        if (manifest.Keys != null)
        {
            return manifest.Keys.ChunkKey;
        }

        if (_passwordProvider == null)
        {
            throw new InvalidOperationException("Encrypted snapshot verification requires a password provider");
        }

        var password = await _passwordProvider.GetPasswordAsync(PasswordPurpose.Decrypt).ConfigureAwait(false);
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

            var descriptor = new ChunkDescriptor(normalizedChunkId, chunk.ContentHash, chunk.PlainSizeBytes, SnapshotChunkCodecs.Normalize(chunk.Codec), chunk.StoredSizeBytes);
            if (!descriptors.TryAdd(descriptor.ChunkId, descriptor))
            {
                var existing = descriptors[descriptor.ChunkId];
                if (!existing.ContentHash.Equals(descriptor.ContentHash, StringComparison.OrdinalIgnoreCase)
                    || existing.PlainSizeBytes != descriptor.PlainSizeBytes
                    || existing.Codec != descriptor.Codec)
                {
                    result.InvalidChunks++;
                    result.Errors.Add($"Inconsistent manifest metadata for chunk '{descriptor.ChunkId}'.");
                }
            }
        }

        return descriptors;
    }

    private enum ChunkState { Verified, Failed }

    /// <summary>Thrown to unwind verification on the first AES-GCM authentication failure.</summary>
    private sealed class WrongPasswordAbort : Exception;

    /// <summary>
    /// Single pass: files are visited in manifest order and each chunk is downloaded, decrypted and
    /// validated on first use, then fed straight into the file's incremental hash. Plaintext is kept
    /// only in a size-bounded LRU (a chunk evicted and referenced again is fetched again, which is
    /// rare); temp use stays at one chunk.
    /// </summary>
    private async Task VerifyContentAsync(
        SnapshotManifest manifest,
        IReadOnlyDictionary<string, ChunkDescriptor> chunkDescriptors,
        string? chunkStorageNamespace,
        byte[]? encryptionMasterKey,
        string tempDirectory,
        SnapshotVerificationResult result,
        IProgress<VerificationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var hashScheme = manifest.GetHashScheme();
        var chunkCache = new ChunkByteCache(MaxChunkCacheBytes);
        var chunkStates = new Dictionary<string, ChunkState>(StringComparer.OrdinalIgnoreCase);
        var chunksHandled = 0;
        var filesHandled = 0;
        var decryptedAny = new System.Runtime.CompilerServices.StrongBox<bool>(false);

        try
        {
            foreach (var file in manifest.Files.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                progress?.Report(new VerificationProgress(
                    file.RelativePath,
                    filesHandled,
                    manifest.Files.Count,
                    "files"));

                using var fileHasher = hashScheme.NewFileHasher();
                long reconstructedSize = 0;
                bool hasMissingChunk = false;

                foreach (var chunk in file.Chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();

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

                    // Already reported when first seen; don't re-download a known-bad object.
                    if (chunkStates.TryGetValue(normalizedChunkId, out var knownState) && knownState == ChunkState.Failed)
                    {
                        hasMissingChunk = true;
                        continue;
                    }

                    if (!chunkCache.TryGetValue(normalizedChunkId, out var chunkBytes))
                    {
                        var firstUse = !chunkStates.ContainsKey(normalizedChunkId);
                        var descriptor = chunkDescriptors[normalizedChunkId];

                        var loaded = await LoadAndValidateChunkAsync(
                            descriptor,
                            chunkStorageNamespace,
                            manifest.EncryptionEnabled,
                            encryptionMasterKey,
                            hashScheme,
                            tempDirectory,
                            result,
                            decryptedAny,
                            cancellationToken).ConfigureAwait(false);

                        chunkStates[normalizedChunkId] = loaded == null ? ChunkState.Failed : ChunkState.Verified;
                        if (firstUse)
                        {
                            chunksHandled++;
                            progress?.Report(new VerificationProgress(
                                normalizedChunkId,
                                chunksHandled,
                                chunkDescriptors.Count,
                                "chunks"));
                        }

                        if (loaded == null)
                        {
                            hasMissingChunk = true;
                            continue;
                        }

                        chunkCache.Set(normalizedChunkId, loaded);
                        chunkBytes = loaded;
                    }

                    fileHasher.AppendData(chunkBytes);
                    reconstructedSize += chunkBytes.Length;
                }

                filesHandled++;

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
        catch (WrongPasswordAbort)
        {
            // One clear error instead of one per chunk; the cached password is known bad.
            _passwordProvider?.ClearPassword();
            result.InvalidChunks++;
            result.Errors.Add("Wrong password (or corrupted data): chunk authentication failed. Verification stopped.");
        }
    }

    /// <summary>
    /// Quick and sample modes. Every unique chunk is checked for existence and stored size without downloading it (the
    /// stored size is recorded in the manifest; 0 means unknown, as in older snapshots, and is not compared). Sample mode
    /// then fully verifies (download, decrypt, decode, hash) a seeded random percentage of the chunks that are present.
    /// File hashes are not rebuilt: only a full verification can prove a file restores.
    /// </summary>
    private async Task VerifyChunkPresenceAsync(
        SnapshotManifest manifest,
        IReadOnlyDictionary<string, ChunkDescriptor> chunkDescriptors,
        string? chunkStorageNamespace,
        byte[]? encryptionMasterKey,
        string tempDirectory,
        VerificationOptions options,
        SnapshotVerificationResult result,
        IProgress<VerificationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var existenceIndex = new ChunkExistenceIndex(
            _storage,
            string.IsNullOrWhiteSpace(chunkStorageNamespace) ? "chunks/" : $"chunks/{chunkStorageNamespace}/",
            ChunkExistenceIndex.DefaultListingThreshold,
            _logger);

        // Ordinal order so a seed always selects the same chunks.
        var ordered = chunkDescriptors.Values.OrderBy(descriptor => descriptor.ChunkId, StringComparer.Ordinal).ToList();
        var badChunks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var handled = 0;

        foreach (var descriptor in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remoteChunkPath = SnapshotStoragePaths.GetChunkPath(descriptor.ChunkId, chunkStorageNamespace);
            try
            {
                var storedSize = await existenceIndex.GetStoredSizeAsync(remoteChunkPath, cancellationToken).ConfigureAwait(false);
                if (storedSize == null)
                {
                    result.MissingChunks++;
                    result.Errors.Add($"Missing chunk object: {remoteChunkPath}");
                    badChunks.Add(descriptor.ChunkId);
                }
                else if (descriptor.StoredSizeBytes > 0 && storedSize.Value != descriptor.StoredSizeBytes)
                {
                    result.InvalidChunks++;
                    result.Errors.Add(
                        $"Chunk size mismatch for '{descriptor.ChunkId}'. Stored object is {storedSize.Value} bytes, manifest expects {descriptor.StoredSizeBytes}.");
                    badChunks.Add(descriptor.ChunkId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.InvalidChunks++;
                result.Errors.Add($"Could not check chunk '{descriptor.ChunkId}': {ex.Message}");
                badChunks.Add(descriptor.ChunkId);
            }

            handled++;
            progress?.Report(new VerificationProgress(descriptor.ChunkId, handled, ordered.Count, "chunks"));
        }

        if (badChunks.Count > 0)
        {
            result.InvalidFiles = manifest.Files.Count(file => file.Chunks.Any(chunk => IsBad(chunk.ChunkId)));
        }

        bool IsBad(string chunkId)
        {
            try
            {
                return badChunks.Contains(SnapshotStoragePaths.NormalizeChunkId(chunkId));
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        if (options.Mode != VerificationMode.Sample)
        {
            return;
        }

        var seed = options.Seed ?? Random.Shared.Next();
        result.SampleSeed = seed;

        var candidates = ordered.Where(descriptor => !badChunks.Contains(descriptor.ChunkId)).ToList();
        var sampleSize = candidates.Count == 0
            ? 0
            : Math.Min(candidates.Count, Math.Max(1, (int)Math.Ceiling(candidates.Count * (options.SamplePercent / 100.0))));

        // Partial Fisher-Yates with the seeded generator.
        var random = new Random(seed);
        for (var i = 0; i < sampleSize; i++)
        {
            var j = random.Next(i, candidates.Count);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        _logger.Log($"Sample verification: {sampleSize} of {candidates.Count} chunk(s) ({options.SamplePercent}%), seed {seed}.", LogLevel.Info);

        var hashScheme = manifest.GetHashScheme();
        var decryptedAny = new System.Runtime.CompilerServices.StrongBox<bool>(false);
        try
        {
            for (var i = 0; i < sampleSize; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await LoadAndValidateChunkAsync(
                    candidates[i],
                    chunkStorageNamespace,
                    manifest.EncryptionEnabled,
                    encryptionMasterKey,
                    hashScheme,
                    tempDirectory,
                    result,
                    decryptedAny,
                    cancellationToken).ConfigureAwait(false);

                result.ChunksSampled++;
                progress?.Report(new VerificationProgress(candidates[i].ChunkId, i + 1, sampleSize, "sampled chunks"));
            }
        }
        catch (WrongPasswordAbort)
        {
            _passwordProvider?.ClearPassword();
            result.InvalidChunks++;
            result.Errors.Add("Wrong password (or corrupted data): chunk authentication failed. Verification stopped.");
        }
    }

    /// <summary>Downloads (once), decrypts and validates a chunk; null when it is missing or invalid (already reported).</summary>
    private async Task<byte[]?> LoadAndValidateChunkAsync(
        ChunkDescriptor descriptor,
        string? chunkStorageNamespace,
        bool encrypted,
        byte[]? encryptionMasterKey,
        ChunkHashScheme hashScheme,
        string tempDirectory,
        SnapshotVerificationResult result,
        System.Runtime.CompilerServices.StrongBox<bool> decryptedAny,
        CancellationToken cancellationToken)
    {
        var remoteChunkPath = SnapshotStoragePaths.GetChunkPath(descriptor.ChunkId, chunkStorageNamespace);
        var tempChunkPath = Path.Combine(tempDirectory, $"{descriptor.ChunkId}.chunk");

        try
        {
            try
            {
                await _storage.DownloadAsync(remoteChunkPath, tempChunkPath, cancellationToken).ConfigureAwait(false);
                result.ChunksDownloaded++;
            }
            catch (Exception ex) when (ex is FileNotFoundException or StorageNotFoundException)
            {
                result.MissingChunks++;
                result.Errors.Add($"Missing chunk object: {remoteChunkPath}");
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.InvalidChunks++;
                result.Errors.Add($"Failed to download chunk '{descriptor.ChunkId}': {ex.Message}");
                return null;
            }

            try
            {
                var storedBytes = await File.ReadAllBytesAsync(tempChunkPath, cancellationToken).ConfigureAwait(false);
                var plainBytes = encrypted
                    ? EncryptionService.DecryptChunkDeterministic(storedBytes, encryptionMasterKey!, descriptor.ChunkId, hashScheme.IsKeyed ? descriptor.Codec : null)
                    : storedBytes;
                decryptedAny.Value = true;

                try
                {
                    plainBytes = ChunkCompression.Decode(descriptor.Codec, plainBytes, descriptor.PlainSizeBytes);
                }
                catch (InvalidDataException ex)
                {
                    result.InvalidChunks++;
                    result.Errors.Add($"Corrupted chunk '{descriptor.ChunkId}': {ex.Message}.");
                    return null;
                }

                if (plainBytes.Length != descriptor.PlainSizeBytes)
                {
                    result.InvalidChunks++;
                    result.Errors.Add(
                        $"Chunk size mismatch for '{descriptor.ChunkId}'. Expected {descriptor.PlainSizeBytes}, actual {plainBytes.Length}.");
                    return null;
                }

                var chunkHash = hashScheme.ChunkId(plainBytes);
                if (!chunkHash.Equals(descriptor.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    result.InvalidChunks++;
                    result.Errors.Add(
                        $"Chunk hash mismatch for '{descriptor.ChunkId}'. Expected '{descriptor.ContentHash}', actual '{chunkHash}'.");
                    return null;
                }

                return plainBytes;
            }
            catch (CryptographicException) when (!decryptedAny.Value)
            {
                // Nothing has decrypted yet: most likely the wrong password.
                throw new WrongPasswordAbort();
            }
            catch (CryptographicException ex)
            {
                // The password demonstrably works, so this chunk is corrupted.
                result.InvalidChunks++;
                result.Errors.Add($"Corrupted chunk '{descriptor.ChunkId}': authentication failed. {ex.Message}");
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.InvalidChunks++;
                result.Errors.Add($"Failed to validate chunk '{descriptor.ChunkId}': {ex.Message}");
                return null;
            }
        }
        finally
        {
            TryDeleteTemporaryFile(tempChunkPath);
        }
    }

    /// <summary>Size-bounded LRU over plaintext chunk bytes.</summary>
    private sealed class ChunkByteCache(long maxBytes)
    {
        private readonly long _maxBytes = Math.Max(0, maxBytes);
        private readonly Dictionary<string, LinkedListNode<CacheEntry>> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<CacheEntry> _lru = [];
        private long _currentBytes;

        public bool TryGetValue(string chunkId, out byte[] bytes)
        {
            if (_entries.TryGetValue(chunkId, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                bytes = node.Value.Bytes;
                return true;
            }

            bytes = [];
            return false;
        }

        public void Set(string chunkId, byte[] bytes)
        {
            if (_maxBytes == 0 || bytes.LongLength > _maxBytes)
            {
                return;
            }

            if (_entries.TryGetValue(chunkId, out var existing))
            {
                _currentBytes -= existing.Value.SizeBytes;
                _lru.Remove(existing);
                _entries.Remove(chunkId);
            }

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(chunkId, bytes));
            _lru.AddFirst(node);
            _entries[chunkId] = node;
            _currentBytes += node.Value.SizeBytes;

            while (_currentBytes > _maxBytes && _lru.Last != null)
            {
                var evicted = _lru.Last;
                _lru.RemoveLast();
                _entries.Remove(evicted.Value.ChunkId);
                _currentBytes -= evicted.Value.SizeBytes;
            }
        }

        private sealed class CacheEntry(string chunkId, byte[] bytes)
        {
            public string ChunkId { get; } = chunkId;
            public byte[] Bytes { get; } = bytes;
            public long SizeBytes { get; } = bytes.LongLength;
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
        // Runs in a finally on the per-chunk path. A transient lock (indexer, AV scanner) must
        // not turn a passing verification into a thrown exception.
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
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

    private sealed record ChunkDescriptor(string ChunkId, string ContentHash, int PlainSizeBytes, string Codec, int StoredSizeBytes = 0);

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
