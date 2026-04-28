using ReStore.Core.src.utils;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.backup;
using System.Text.Json;

namespace ReStore.Core.src.core;

public class Backup
{
    private readonly ILogger _logger;
    private readonly SystemState _state;
    private readonly SizeAnalyzer _sizeAnalyzer;
    private readonly IConfigManager _config;
    private readonly FileSelectionService _fileSelectionService;
    private readonly FileDiffSyncManager? _diffSyncManager;
    private readonly IPasswordProvider? _passwordProvider;
    private readonly RetentionManager _retentionManager;
    private readonly EncryptionService _encryptionService;

    public Backup(ILogger logger, SystemState state, SizeAnalyzer sizeAnalyzer, IConfigManager config, IPasswordProvider? passwordProvider = null)
    {
        _logger = logger;
        _state = state;
        _sizeAnalyzer = sizeAnalyzer;
        _config = config ?? throw new ArgumentNullException(nameof(config), "Config cannot be null");
        _fileSelectionService = new FileSelectionService(logger, _config);
        _passwordProvider = passwordProvider;
        _encryptionService = new EncryptionService(_logger);

        var backupConfig = new BackupConfigurationManager(logger, _config);
        _diffSyncManager = new FileDiffSyncManager(logger, state, backupConfig);

        _retentionManager = new RetentionManager(_logger, _config, _state);
    }

    public async Task BackupDirectoryAsync(string sourceDirectory, string? storageTypeOverride = null)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            throw new ArgumentException("Source directory cannot be null or empty", nameof(sourceDirectory));
        }

        IStorage? storage = null;
        try
        {
            sourceDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(sourceDirectory));

            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException($"Source directory not found: {sourceDirectory}");
            }

            var storageType = storageTypeOverride ?? GetStorageTypeForDirectory(sourceDirectory);
            storage = await _config.CreateStorageAsync(storageType);

            _logger.Log($"Starting backup of {sourceDirectory} using {storageType} storage");

            _sizeAnalyzer.SizeThreshold = _config.SizeThresholdMB * 1024 * 1024;
            var (size, exceedsThreshold) = await _sizeAnalyzer.AnalyzeDirectoryAsync(sourceDirectory);

            if (exceedsThreshold)
            {
                _logger.Log($"Warning: Directory size ({size} bytes) exceeds threshold");
            }

            var allFiles = GetFilesInDirectory(sourceDirectory);

            var filesToBackup = _diffSyncManager != null
                ? _diffSyncManager.GetFilesToBackup(allFiles, sourceDirectory)
                : allFiles;

            // Clean up metadata for files that no longer exist in this directory
            var trackedFiles = _state.GetTrackedFilesInDirectory(sourceDirectory) ?? [];
            var filesToRemove = trackedFiles.Except(allFiles, StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var file in filesToRemove)
            {
                await _state.AddOrUpdateFileMetadataAsync(file);
            }

            if (filesToBackup.Count == 0 && filesToRemove.Count == 0)
            {
                _logger.Log("No files need to be backed up based on the current state and backup type.", LogLevel.Info);
                await _state.SaveStateAsync();
                return;
            }

            _logger.Log($"Preparing to build snapshot manifest for {sourceDirectory}. Changed files: {filesToBackup.Count}, removed files: {filesToRemove.Count}", LogLevel.Info);
            await CreateSnapshotBackupAsync(sourceDirectory, allFiles, filesToBackup, storage, storageType);

            if (_diffSyncManager != null)
            {
                await _diffSyncManager.UpdateFileMetadataAsync(filesToBackup);
            }
            else
            {
                foreach (var file in filesToBackup)
                {
                    await _state.AddOrUpdateFileMetadataAsync(file);
                }
            }

            _state.LastBackupTime = DateTime.UtcNow;

            await _state.SaveStateAsync();
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to backup directory: {ex.Message}", LogLevel.Error);
            throw;
        }
        finally
        {
            storage?.Dispose();
        }
    }

    public async Task BackupFilesAsync(IEnumerable<string> filesToBackup, string baseDirectory, string? storageTypeOverride = null)
    {
        if (filesToBackup == null)
        {
            throw new ArgumentNullException(nameof(filesToBackup));
        }

        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("Base directory cannot be null or empty", nameof(baseDirectory));
        }

        var fileList = filesToBackup.ToList();
        if (fileList.Count == 0)
        {
            _logger.Log("No files provided for backup.", LogLevel.Info);
            return;
        }

        IStorage? storage = null;
        try
        {
            var storageType = storageTypeOverride ?? GetStorageTypeForDirectory(baseDirectory);
            storage = await _config.CreateStorageAsync(storageType);

            _logger.Log($"Starting backup of {fileList.Count} specific files from base directory {baseDirectory} using {storageType} storage", LogLevel.Info);

            var existingFilesToBackup = fileList.Where(File.Exists).ToList();
            var deletedFiles = fileList.Except(existingFilesToBackup, StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var file in deletedFiles)
            {
                await _state.AddOrUpdateFileMetadataAsync(file);
            }

            if (existingFilesToBackup.Count == 0 && deletedFiles.Count == 0)
            {
                _logger.Log("No valid file changes detected for snapshot creation.", LogLevel.Info);
                await _state.SaveStateAsync();
                return;
            }

            var allFiles = GetFilesInDirectory(baseDirectory);
            await CreateSnapshotBackupAsync(baseDirectory, allFiles, existingFilesToBackup, storage, storageType);

            foreach (var file in existingFilesToBackup)
            {
                await _state.AddOrUpdateFileMetadataAsync(file);
            }

            _logger.Log("Specific file snapshot backup completed.", LogLevel.Info);

            await _state.SaveStateAsync();
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to backup specific files from {baseDirectory}: {ex.Message}", LogLevel.Error);
            throw;
        }
        finally
        {
            storage?.Dispose();
        }
    }

    private List<string> GetFilesInDirectory(string directory)
    {
        try
        {
            var directoryList = new List<string> { directory };
            var files = _fileSelectionService.GetFilesToBackup(directoryList);

            _logger.Log($"Found {files.Count} files to backup in {directory}", LogLevel.Info);
            return files;
        }
        catch (Exception ex)
        {
            _logger.Log($"Error collecting files: {ex.Message}", LogLevel.Error);
            throw new InvalidOperationException(
                $"Failed to enumerate files for backup in '{directory}'.",
                ex);
        }
    }

    private string GetStorageTypeForDirectory(string directory)
    {
        var normalizedDir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory));

        var watchConfig = _config.WatchDirectories.FirstOrDefault(w =>
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(w.Path))
                .Equals(normalizedDir, StringComparison.OrdinalIgnoreCase));

        return watchConfig?.StorageType ?? _config.GlobalStorageType;
    }

    private async Task CreateSnapshotBackupAsync(string sourceDirectory, List<string> allFiles, List<string> changedFiles, IStorage storage, string storageType)
    {
        var chunkConfig = _config.ChunkDiffing ?? new ChunkDiffingConfig();

        if (allFiles.Count > chunkConfig.MaxFilesPerSnapshot)
        {
            throw new InvalidOperationException(
                $"Snapshot exceeds maxFilesPerSnapshot safety limit ({chunkConfig.MaxFilesPerSnapshot}) for directory '{sourceDirectory}'.");
        }

        try
        {
            var normalizedSourceDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(sourceDirectory));
            var normalizedChangedFiles = changedFiles
                .Select(path => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            byte[]? encryptionMasterKey = null;
            string? encryptionSalt = null;
            string? chunkStorageNamespace = null;

            if (_config.Encryption.Enabled)
            {
                EnsureEncryptionProviderAvailable();

                var password = await _passwordProvider!.GetPasswordAsync();
                if (string.IsNullOrEmpty(password))
                {
                    throw new InvalidOperationException("Encryption is enabled but no password was provided");
                }

                if (string.IsNullOrWhiteSpace(_config.Encryption.Salt))
                {
                    throw new InvalidOperationException("Encryption is enabled but encryption salt is missing from configuration");
                }

                var saltBytes = Convert.FromBase64String(_config.Encryption.Salt);
                encryptionMasterKey = _encryptionService.DeriveKeyFromPassword(password, saltBytes, _config.Encryption.KeyDerivationIterations);
                encryptionSalt = _config.Encryption.Salt;
                chunkStorageNamespace = SnapshotStoragePaths.BuildEncryptedChunkNamespace(encryptionMasterKey);
            }

            var chunkingService = new ChunkingService(
                _logger,
                chunkConfig,
                _encryptionService,
                _config.Encryption.Enabled,
                encryptionMasterKey);

            var previousManifest = await TryLoadLatestManifestAsync(normalizedSourceDirectory, storage);
            var previousFilesByPath = previousManifest?.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, SnapshotFileManifestEntry>(StringComparer.OrdinalIgnoreCase);

            string? previousChunkStorageNamespace;
            try
            {
                previousChunkStorageNamespace = SnapshotStoragePaths.NormalizeChunkStorageNamespace(previousManifest?.ChunkStorageNamespace);
            }
            catch (ArgumentException ex)
            {
                _logger.Log(
                    $"Previous snapshot namespace is invalid and will not be reused: {ex.Message}",
                    LogLevel.Warning);
                previousChunkStorageNamespace = null;
            }

            var canReusePreviousManifestEntries = string.Equals(
                previousChunkStorageNamespace,
                chunkStorageNamespace,
                StringComparison.OrdinalIgnoreCase);

            if (previousManifest != null && !canReusePreviousManifestEntries)
            {
                _logger.Log(
                    "Previous snapshot chunk namespace differs from current backup context; rebuilding all file manifests for this snapshot.",
                    LogLevel.Info);
            }

            var manifestFiles = new List<SnapshotFileManifestEntry>();
            var pendingChunkPayloads = new Dictionary<string, ChunkBuildPayload>(StringComparer.OrdinalIgnoreCase);

            foreach (var absolutePath in allFiles
                .Select(path => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)))
                .Where(File.Exists)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var relativePath = Path.GetRelativePath(normalizedSourceDirectory, absolutePath)
                    .Replace(Path.DirectorySeparatorChar, '/');

                if (canReusePreviousManifestEntries
                    && !normalizedChangedFiles.Contains(absolutePath)
                    && previousFilesByPath.TryGetValue(relativePath, out var existingEntry))
                {
                    manifestFiles.Add(existingEntry);
                    continue;
                }

                var chunkBuild = await chunkingService.BuildFileManifestEntryAsync(absolutePath, normalizedSourceDirectory);
                manifestFiles.Add(chunkBuild.FileEntry);

                foreach (var chunkPayload in chunkBuild.ChunkPayloads)
                {
                    if (!pendingChunkPayloads.ContainsKey(chunkPayload.ChunkId))
                    {
                        pendingChunkPayloads[chunkPayload.ChunkId] = chunkPayload;
                    }
                }
            }

            var snapshotId = SnapshotStoragePaths.BuildSnapshotId();
            var manifest = new SnapshotManifest
            {
                Version = chunkConfig.ManifestVersion,
                SnapshotId = snapshotId,
                Group = normalizedSourceDirectory,
                CreatedUtc = DateTime.UtcNow,
                BackupMode = _config.BackupType.ToString(),
                EncryptionEnabled = _config.Encryption.Enabled,
                EncryptionSalt = encryptionSalt,
                KeyDerivationIterations = _config.Encryption.KeyDerivationIterations,
                ChunkStorageNamespace = chunkStorageNamespace,
                Profile = ChunkingProfile.FromConfig(chunkConfig),
                Files = manifestFiles
            };

            manifest.RootHash = SnapshotManifestHasher.ComputeRootHash(manifest);

            var uploadTelemetry = await UploadMissingChunksAsync(storage, pendingChunkPayloads.Values, chunkStorageNamespace);

            var manifestPath = SnapshotStoragePaths.GetManifestPath(normalizedSourceDirectory, snapshotId);
            await UploadManifestAsync(storage, manifestPath, manifest);

            var headPath = SnapshotStoragePaths.GetHeadPath(normalizedSourceDirectory);
            await UploadSnapshotHeadAsync(storage, headPath, manifestPath, manifest.RootHash);

            var referencedChunkIds = manifest.Files
                .SelectMany(file => file.Chunks)
                .Select(chunk => chunk.ChunkId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            LogChunkReuseTelemetry(normalizedSourceDirectory, manifest, referencedChunkIds, uploadTelemetry);

            var logicalSize = manifest.Files.Sum(file => file.SizeBytes);
            _state.AddSnapshotBackup(
                normalizedSourceDirectory,
                snapshotId,
                manifestPath,
                storageType,
                referencedChunkIds,
                logicalSize,
                manifest.RootHash,
                _config.Encryption.Enabled,
                chunkStorageNamespace);

            await _retentionManager.ApplyGroupAsync(normalizedSourceDirectory);
            _logger.Log($"Snapshot backup completed: {manifestPath}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to create snapshot backup: {ex.Message}", LogLevel.Error);
            throw;
        }
    }

    private async Task<ChunkUploadTelemetry> UploadMissingChunksAsync(
        IStorage storage,
        IEnumerable<ChunkBuildPayload> chunkPayloads,
        string? chunkStorageNamespace)
    {
        var payloadList = chunkPayloads.ToList();
        var telemetry = new ChunkUploadTelemetry
        {
            CandidateChunks = payloadList.Count
        };

        foreach (var chunk in payloadList)
        {
            var chunkPath = SnapshotStoragePaths.GetChunkPath(chunk.ChunkId, chunkStorageNamespace);
            if (await storage.ExistsAsync(chunkPath))
            {
                telemetry.ReusedChunks++;
                continue;
            }

            var tempChunkPath = Path.Combine(Path.GetTempPath(), $"restore_chunk_{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(tempChunkPath, chunk.StoredPayload);

            try
            {
                await storage.UploadAsync(tempChunkPath, chunkPath);
                telemetry.UploadedChunks++;
                _logger.Log($"Uploaded chunk {chunk.ChunkId} to {chunkPath}", LogLevel.Debug);
            }
            finally
            {
                TryDeleteTemporaryFile(tempChunkPath);
            }
        }

        return telemetry;
    }

    private void LogChunkReuseTelemetry(
        string sourceDirectory,
        SnapshotManifest manifest,
        IReadOnlyCollection<string> referencedChunkIds,
        ChunkUploadTelemetry uploadTelemetry)
    {
        var totalChunkReferences = manifest.Files.Sum(file => file.Chunks.Count);
        var totalUniqueChunks = referencedChunkIds.Count;
        var uniqueReusedChunks = Math.Max(0, totalUniqueChunks - uploadTelemetry.UploadedChunks);
        var manifestReuseRatio = totalUniqueChunks == 0 ? 0 : (double)uniqueReusedChunks / totalUniqueChunks;
        var uploadBypassRatio = uploadTelemetry.CandidateChunks == 0
            ? 0
            : (double)uploadTelemetry.ReusedChunks / uploadTelemetry.CandidateChunks;

        _logger.Log(
            $"Chunk telemetry: group='{sourceDirectory}', snapshot='{manifest.SnapshotId}', fileCount={manifest.Files.Count}, chunkRefs={totalChunkReferences}, uniqueChunks={totalUniqueChunks}, uploadedChunks={uploadTelemetry.UploadedChunks}, reusedChunks={uniqueReusedChunks}, manifestReuseRatio={manifestReuseRatio:P2}, candidateChunks={uploadTelemetry.CandidateChunks}, storageHitChunks={uploadTelemetry.ReusedChunks}, uploadBypassRatio={uploadBypassRatio:P2}",
            LogLevel.Info);

        _state.RecordSnapshotBackupTelemetry(
            fileCount: manifest.Files.Count,
            chunkReferences: totalChunkReferences,
            uniqueChunks: totalUniqueChunks,
            uploadedChunks: uploadTelemetry.UploadedChunks,
            uniqueReusedChunks: uniqueReusedChunks,
            storageHitChunks: uploadTelemetry.ReusedChunks,
            candidateChunks: uploadTelemetry.CandidateChunks);
    }

    private async Task UploadManifestAsync(IStorage storage, string manifestPath, SnapshotManifest manifest)
    {
        var tempManifestPath = Path.Combine(Path.GetTempPath(), $"restore_manifest_{Guid.NewGuid():N}.json");

        try
        {
            var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(tempManifestPath, manifestJson);
            await storage.UploadAsync(tempManifestPath, manifestPath);
            _logger.Log($"Uploaded snapshot manifest: {manifestPath}", LogLevel.Debug);
        }
        finally
        {
            TryDeleteTemporaryFile(tempManifestPath);
        }
    }

    private async Task UploadSnapshotHeadAsync(IStorage storage, string headPath, string manifestPath, string rootHash)
    {
        var tempHeadPath = Path.Combine(Path.GetTempPath(), $"restore_head_{Guid.NewGuid():N}.txt");

        try
        {
            var content = $"{manifestPath}\n{rootHash}\n";
            await File.WriteAllTextAsync(tempHeadPath, content);
            await storage.UploadAsync(tempHeadPath, headPath);
            _logger.Log($"Updated snapshot head: {headPath}", LogLevel.Debug);
        }
        finally
        {
            TryDeleteTemporaryFile(tempHeadPath);
        }
    }

    private async Task<SnapshotManifest?> TryLoadLatestManifestAsync(string sourceDirectory, IStorage storage)
    {
        var previousBackupPath = _state.GetPreviousBackupPath(sourceDirectory);
        if (string.IsNullOrWhiteSpace(previousBackupPath))
        {
            return null;
        }

        var previousPath = previousBackupPath;
        string? expectedRootHashFromHead = null;
        if (previousPath.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase))
        {
            var headReference = await ResolveManifestPathFromHeadAsync(storage, previousPath);
            previousPath = headReference.ManifestPath;
            expectedRootHashFromHead = headReference.RootHash;
        }

        if (!previousPath.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Log($"Ignoring non-manifest previous backup in snapshot flow: {previousPath}", LogLevel.Warning);
            return null;
        }

        var tempManifestPath = Path.Combine(Path.GetTempPath(), $"restore_prev_manifest_{Guid.NewGuid():N}.json");

        try
        {
            await storage.DownloadAsync(previousPath, tempManifestPath);
            var json = await File.ReadAllTextAsync(tempManifestPath);
            var previousManifest = JsonSerializer.Deserialize<SnapshotManifest>(json);

            if (previousManifest == null)
            {
                throw new InvalidOperationException($"Failed to deserialize previous snapshot manifest: {previousPath}");
            }

            if (!SnapshotManifestHasher.IsValid(previousManifest))
            {
                throw new InvalidOperationException($"Previous snapshot manifest hash validation failed: {previousPath}");
            }

            if (!string.IsNullOrWhiteSpace(expectedRootHashFromHead)
                && !expectedRootHashFromHead.Equals(previousManifest.RootHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Previous snapshot HEAD root hash mismatch. Expected '{expectedRootHashFromHead}', actual '{previousManifest.RootHash}'.");
            }

            try
            {
                var normalizedManifestGroup = Path.GetFullPath(Environment.ExpandEnvironmentVariables(previousManifest.Group));
                if (!normalizedManifestGroup.Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Log(
                        $"Ignoring previous snapshot manifest from different group. Expected '{sourceDirectory}', found '{normalizedManifestGroup}'.",
                        LogLevel.Warning);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.Log(
                    $"Ignoring previous snapshot manifest with invalid group path '{previousManifest.Group}': {ex.Message}",
                    LogLevel.Warning);
                return null;
            }

            return previousManifest;
        }
        catch (FileNotFoundException)
        {
            _logger.Log($"Previous snapshot manifest not found in storage: {previousPath}", LogLevel.Warning);
            return null;
        }
        catch (Exception ex)
        {
            _logger.Log(
                $"Unable to load previous snapshot manifest '{previousPath}'. Falling back to full snapshot rebuild: {ex.Message}",
                LogLevel.Warning);
            return null;
        }
        finally
        {
            TryDeleteTemporaryFile(tempManifestPath);
        }
    }

    private async Task<SnapshotHeadReference> ResolveManifestPathFromHeadAsync(IStorage storage, string headPath)
    {
        var tempHeadPath = Path.Combine(Path.GetTempPath(), $"restore_head_download_{Guid.NewGuid():N}.txt");
        try
        {
            await storage.DownloadAsync(headPath, tempHeadPath);
            var lines = await File.ReadAllLinesAsync(tempHeadPath);
            var nonEmptyLines = lines
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim())
                .ToList();

            var manifestPath = nonEmptyLines.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(manifestPath))
            {
                throw new InvalidOperationException($"Snapshot head file does not contain a manifest path: {headPath}");
            }

            var rootHash = nonEmptyLines.Count > 1 ? nonEmptyLines[1] : null;
            return new SnapshotHeadReference(manifestPath, string.IsNullOrWhiteSpace(rootHash) ? null : rootHash);
        }
        finally
        {
            TryDeleteTemporaryFile(tempHeadPath);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed class ChunkUploadTelemetry
    {
        public int CandidateChunks { get; init; }
        public int UploadedChunks { get; set; }
        public int ReusedChunks { get; set; }
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

    private void EnsureEncryptionProviderAvailable()
    {
        if (_passwordProvider == null)
        {
            throw new InvalidOperationException("Encryption is enabled but no password provider is available");
        }
    }
}
