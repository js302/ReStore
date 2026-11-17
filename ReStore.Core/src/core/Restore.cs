using ReStore.Core.src.backup;
using ReStore.Core.src.utils;
using ReStore.Core.src.storage;
using System.Security.Cryptography;
using System.Text.Json;

namespace ReStore.Core.src.core;

/// <summary>Options controlling which files a restore writes and how conflicts resolve.</summary>
public sealed class RestoreOptions
{
    public RestoreConflictPolicy ConflictPolicy { get; init; } = RestoreConflictPolicy.Skip;

    /// <summary>
    /// Relative paths (manifest form, '/'-separated) to restore. Null restores everything.
    /// Compared case-insensitively.
    /// </summary>
    public IReadOnlySet<string>? RelativePaths { get; init; }

    /// <summary>Produce the preview and return without writing any file.</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Safe default: existing files are left alone. Callers that mean to replace user data
    /// must ask for it explicitly, which matches the CLI's documented <c>--conflict skip</c>
    /// default and <see cref="RestoreConflictPolicy"/>'s stated intent.
    /// </summary>
    public static RestoreOptions Default { get; } = new();

    /// <summary>Replaces differing files, as the pre-conflict-policy behaviour did.</summary>
    public static RestoreOptions Overwrite { get; } = new() { ConflictPolicy = RestoreConflictPolicy.Overwrite };
}

public class Restore(ILogger logger, IStorage storage, IPasswordProvider? passwordProvider = null, SystemState? systemState = null, RepositoryCoordinator? coordinator = null, string? tempRoot = null)
{
    // Restores and previews only read the repository; they share it with each other and with backups,
    // and exclude only chunk deletion (retention / snapshot delete).
    private readonly RepositoryCoordinator _coordinator = coordinator ?? RepositoryCoordinator.Instance;

    private const long MaxChunkCacheBytes = 64L * 1024 * 1024;

    private readonly ILogger _logger = logger;
    private readonly IStorage _storage = storage;
    private readonly IPasswordProvider? _passwordProvider = passwordProvider;
    private readonly SystemState? _systemState = systemState;
    private readonly EncryptionService _encryptionService = new(logger);
    private readonly string _tempRoot = tempRoot ?? Path.GetTempPath();

    /// <summary>
    /// Restores every file, replacing any that already exist. Kept explicit rather than
    /// deferring to <see cref="RestoreOptions.Default"/>, whose safe default is now Skip.
    /// </summary>
    public Task RestoreFromBackupAsync(
        string backupPath,
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        return RestoreFromBackupAsync(backupPath, targetDirectory, RestoreOptions.Overwrite, null, cancellationToken);
    }

    public async Task<RestoreOutcome> RestoreFromBackupAsync(
        string backupPath,
        string targetDirectory,
        RestoreOptions options,
        IProgress<RestoreProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(backupPath))
        {
            throw new ArgumentException("Backup path cannot be null or empty", nameof(backupPath));
        }

        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ArgumentException("Target directory cannot be null or empty", nameof(targetDirectory));
        }

        // Any password prompt happens before the lease, not while it blocks other operations.
        await PasswordPrefetch.WarmForSnapshotAsync(_logger, _storage, _passwordProvider, backupPath, _tempRoot, cancellationToken).ConfigureAwait(false);
        var readLease = await _coordinator.AcquireReadAsync($"Restoring {backupPath}", cancellationToken).ConfigureAwait(false);
        await using var readLeaseScope = readLease.ConfigureAwait(false);
        using var activity = ActivityRecorder.Begin(_systemState, ActivityType.Restore, backupPath, cancellationToken);

        var runStartedUtc = DateTime.UtcNow;
        var tempDirectory = Path.Combine(_tempRoot, $"restore_snapshot_{Guid.NewGuid():N}");
        SnapshotManifest? loadedManifest = null;
        string resolvedManifestPath = backupPath;
        var restoreTelemetry = new RestoreTelemetry();
        var outcome = new RestoreOutcome();

        try
        {
            _logger.Log($"Starting restore from {backupPath} to {targetDirectory}", LogLevel.Info);

            Directory.CreateDirectory(tempDirectory);

            var loaded = await LoadManifestAsync(backupPath, tempDirectory, cancellationToken).ConfigureAwait(false);
            var manifest = loaded.Manifest;
            loadedManifest = manifest;
            resolvedManifestPath = loaded.ManifestPath;

            var selectedFiles = SelectFiles(manifest, options.RelativePaths);

            // Telemetry must reflect the selected subset, or a partial restore reports a
            // success ratio against files it was never asked to write.
            restoreTelemetry.FileCountExpected = selectedFiles.Count;
            restoreTelemetry.ChunkReferencesExpected = selectedFiles.Sum(file => file.Chunks.Count);

            var totalBytes = selectedFiles.Sum(file => file.SizeBytes);

            progress?.Report(new RestoreProgress(
                string.Empty, 0, selectedFiles.Count, 0, totalBytes, RestorePhase.Previewing));

            var preview = BuildPreview(
                backupPath,
                resolvedManifestPath,
                manifest,
                targetDirectory,
                selectedFiles,
                manifest.Files.Count - selectedFiles.Count);

            if (options.ConflictPolicy == RestoreConflictPolicy.Fail && preview.HasConflicts)
            {
                throw new InvalidOperationException(
                    $"Restore would replace {preview.DifferingFileCount} existing file(s) at '{targetDirectory}'. " +
                    "Re-run with a conflict policy of Skip, Overwrite or KeepBoth.");
            }

            if (options.DryRun)
            {
                _logger.Log(
                    $"Dry-run restore preview: files={preview.FileCount}, bytes={preview.TotalBytes}, existing={preview.ExistingFileCount}, identical={preview.IdenticalFileCount}, differing={preview.DifferingFileCount}",
                    LogLevel.Info);

                LogRestoreTelemetry(resolvedManifestPath, loadedManifest, restoreTelemetry, wasSuccessful: true);
                activity.Succeeded($"Dry run: {preview.FileCount} file(s) would be restored");
                return outcome;
            }

            byte[]? encryptionMasterKey = await ResolveEncryptionMasterKeyAsync(manifest).ConfigureAwait(false);
            var hashScheme = manifest.GetHashScheme();

            // Only now that the restore will actually write: a dry run must leave the
            // filesystem exactly as it found it.
            Directory.CreateDirectory(targetDirectory);
            SweepStalePartialFiles(targetDirectory, runStartedUtc);

            var verifiedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var chunkCache = new ChunkByteCache(MaxChunkCacheBytes);
            var chunkStorageNamespace = SnapshotStoragePaths.NormalizeChunkStorageNamespace(manifest.ChunkStorageNamespace);
            var conflictsByRelativePath = preview.Entries.ToDictionary(
                entry => entry.RelativePath,
                entry => entry.Conflict,
                StringComparer.OrdinalIgnoreCase);

            bool IsSkippedByConflict(SnapshotFileManifestEntry file) =>
                options.ConflictPolicy == RestoreConflictPolicy.Skip
                && conflictsByRelativePath.GetValueOrDefault(file.RelativePath, RestoreConflictKind.None) != RestoreConflictKind.None;

            // Chunks are downloaded up to MaxConcurrency ahead of the file being written, in exactly the order the
            // loop below consumes them; decryption and verification stay in that order too (see ChunkPrefetcher).
            var prefetcher = new ChunkPrefetcher(
                selectedFiles.Where(file => !IsSkippedByConflict(file)).SelectMany(file => file.Chunks),
                _storage.MaxConcurrency,
                (chunk, token) => DownloadStoredChunkAsync(chunk, chunkStorageNamespace, tempDirectory, restoreTelemetry, token),
                chunkId => chunkCache.TryGetValue(chunkId, out var cached) ? cached : null,
                () => restoreTelemetry.ChunkCacheHits++,
                cancellationToken);
            await using var prefetcherScope = prefetcher.ConfigureAwait(false);

            foreach (var file in selectedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var outputPath = ResolveSafeOutputPath(targetDirectory, file.RelativePath, verifiedDirectories);
                var conflict = conflictsByRelativePath.GetValueOrDefault(file.RelativePath, RestoreConflictKind.None);

                if (conflict != RestoreConflictKind.None && options.ConflictPolicy == RestoreConflictPolicy.Skip)
                {
                    outcome.FilesSkipped++;
                    outcome.SkippedRelativePaths.Add(file.RelativePath);
                    restoreTelemetry.ChunkReferencesProcessed += file.Chunks.Count;
                    restoreTelemetry.FileCountCompleted++;

                    progress?.Report(new RestoreProgress(
                        file.RelativePath,
                        restoreTelemetry.FileCountCompleted,
                        selectedFiles.Count,
                        outcome.BytesRestored,
                        totalBytes,
                        RestorePhase.Restoring));
                    continue;
                }

                if (conflict != RestoreConflictKind.None && options.ConflictPolicy == RestoreConflictPolicy.KeepBoth)
                {
                    outputPath = BuildKeepBothPath(outputPath);
                    outcome.FilesKeptBoth++;
                }
                else if (conflict != RestoreConflictKind.None)
                {
                    outcome.FilesOverwritten++;
                }

                var outputDirectory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrWhiteSpace(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                progress?.Report(new RestoreProgress(
                    file.RelativePath,
                    restoreTelemetry.FileCountCompleted,
                    selectedFiles.Count,
                    outcome.BytesRestored,
                    totalBytes,
                    RestorePhase.Restoring));

                // Written to a sibling temp file and moved into place on success, so a
                // cancellation or failure mid-file cannot leave a truncated file where the
                // user's real one was.
                var partialPath = outputPath + FileSelectionService.RestorePartialExtension;

                try
                {
                    var outputStream = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
                    await using (outputStream.ConfigureAwait(false))
                    {
                        foreach (var chunk in file.Chunks)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var fetched = await prefetcher.NextAsync(chunk).ConfigureAwait(false);
                            var chunkBytes = fetched.IsPlaintext
                                ? fetched.Bytes
                                : DecodeChunk(chunk, fetched.Bytes, manifest.EncryptionEnabled, encryptionMasterKey, hashScheme, chunkCache, restoreTelemetry);

                            restoreTelemetry.ChunkReferencesProcessed++;

                            await outputStream.WriteAsync(chunkBytes, cancellationToken).ConfigureAwait(false);
                        }

                        await outputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }

                    var fileHash = hashScheme.IsKeyed ? hashScheme.HashFile(partialPath) : FileHasher.ComputeHash(partialPath);
                    if (!fileHash.Equals(file.ContentHash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Restored file hash mismatch for '{file.RelativePath}'. Expected '{file.ContentHash}', actual '{fileHash}'.");
                    }

                    ClearReadOnly(outputPath);
                    File.Move(partialPath, outputPath, overwrite: true);
                }
                catch
                {
                    TryDeleteTemporaryFile(partialPath);
                    throw;
                }

                if (manifest.Version >= SnapshotManifest.VersionV3)
                {
                    ApplyFileMetadata(outputPath, file);
                }

                outcome.FilesRestored++;
                outcome.BytesRestored += file.SizeBytes;
                restoreTelemetry.FileCountCompleted++;

                progress?.Report(new RestoreProgress(
                    file.RelativePath,
                    restoreTelemetry.FileCountCompleted,
                    selectedFiles.Count,
                    outcome.BytesRestored,
                    totalBytes,
                    RestorePhase.Restoring));
            }

            RestoreEmptyDirectories(manifest, targetDirectory, options.RelativePaths, outcome);

            progress?.Report(new RestoreProgress(
                string.Empty,
                restoreTelemetry.FileCountCompleted,
                selectedFiles.Count,
                outcome.BytesRestored,
                totalBytes,
                RestorePhase.Finalising));

            _logger.Log("Restore completed successfully.", LogLevel.Info);
            LogRestoreTelemetry(resolvedManifestPath, loadedManifest, restoreTelemetry, wasSuccessful: true);
            activity.Succeeded($"{outcome.FilesRestored} file(s) restored to {targetDirectory}");

            return outcome;
        }
        catch (OperationCanceledException)
        {
            // A cancellation is not a restore failure; recording it as one would poison the
            // failure-category telemetry the health panel reports on.
            _logger.Log("Restore cancelled by request.", LogLevel.Warning);
            throw;
        }
        catch (FileNotFoundException ex)
        {
            restoreTelemetry.FailureCategory = ClassifyRestoreFailure(ex);
            restoreTelemetry.FailureMessage = ex.Message;
            LogRestoreTelemetry(resolvedManifestPath, loadedManifest, restoreTelemetry, wasSuccessful: false);

            _logger.Log($"Restore failed: required snapshot artifact was not found. {ex.Message}", LogLevel.Error);
            activity.Failed(ex.Message);
            throw;
        }
        catch (CryptographicException ex)
        {
            _passwordProvider?.ClearPassword();

            restoreTelemetry.FailureCategory = ClassifyRestoreFailure(ex);
            restoreTelemetry.FailureMessage = ex.Message;
            LogRestoreTelemetry(resolvedManifestPath, loadedManifest, restoreTelemetry, wasSuccessful: false);

            _logger.Log($"Restore failed: snapshot decryption integrity check failed. {ex.Message}", LogLevel.Error);
            activity.Failed(ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            restoreTelemetry.FailureCategory = ClassifyRestoreFailure(ex);
            restoreTelemetry.FailureMessage = ex.Message;
            LogRestoreTelemetry(resolvedManifestPath, loadedManifest, restoreTelemetry, wasSuccessful: false);

            _logger.Log($"Restore failed: {ex.Message}", LogLevel.Error);
            activity.Failed(ex.Message);
            throw;
        }
        finally
        {
            loadedManifest?.Keys?.Dispose();
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

    /// <summary>
    /// Reads the manifest and reports what a restore would write, without downloading any
    /// chunk objects or touching the target directory.
    /// </summary>
    public async Task<RestorePreview> PreviewRestoreAsync(
        string backupPath,
        string targetDirectory,
        IReadOnlySet<string>? relativePaths = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            throw new ArgumentException("Backup path cannot be null or empty", nameof(backupPath));
        }

        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ArgumentException("Target directory cannot be null or empty", nameof(targetDirectory));
        }

        // Any password prompt happens before the lease, not while it blocks other operations.
        await PasswordPrefetch.WarmForSnapshotAsync(_logger, _storage, _passwordProvider, backupPath, _tempRoot, cancellationToken).ConfigureAwait(false);
        var readLease = await _coordinator.AcquireReadAsync($"Previewing {backupPath}", cancellationToken).ConfigureAwait(false);
        await using var readLeaseScope = readLease.ConfigureAwait(false);

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"restore_preview_{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(tempDirectory);

            var loaded = await LoadManifestAsync(backupPath, tempDirectory, cancellationToken).ConfigureAwait(false);
            var selectedFiles = SelectFiles(loaded.Manifest, relativePaths);

            // Conflict detection hashes every existing target file: keep that off the caller's (UI) thread.
            var preview = await Task.Run(() => BuildPreview(
                backupPath,
                loaded.ManifestPath,
                loaded.Manifest,
                targetDirectory,
                selectedFiles,
                loaded.Manifest.Files.Count - selectedFiles.Count), cancellationToken).ConfigureAwait(false);
            loaded.Manifest.Keys?.Dispose();
            return preview;
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
                    _logger.Log($"Failed to clean up preview temporary directory {tempDirectory}: {cleanupEx.Message}", LogLevel.Warning);
                }
            }
        }
    }

    private async Task<LoadedManifest> LoadManifestAsync(
        string backupPath,
        string tempDirectory,
        CancellationToken cancellationToken)
    {
        var tempManifestPath = Path.Combine(tempDirectory, "snapshot.manifest.json");
        string? expectedRootHashFromHead = null;

        var manifestPath = backupPath;
        if (backupPath.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase))
        {
            var headReference = await ResolveManifestPathFromHeadAsync(backupPath, tempDirectory, cancellationToken).ConfigureAwait(false);
            manifestPath = headReference.ManifestPath;
            expectedRootHashFromHead = headReference.RootHash;
        }

        if (!manifestPath.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"User-file restore requires a snapshot manifest path. Received unsupported artifact: {manifestPath}");
        }

        _logger.Log($"Downloading snapshot manifest: {manifestPath}", LogLevel.Debug);
        await _storage.DownloadAsync(manifestPath, tempManifestPath).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        // An encrypted v3 manifest unlocks the repository key file here (wrong password stops before any chunk is read);
        // the unlocked keys stay on the manifest for chunk and file hash checks.
        // The keys stay with the manifest (and are zeroed by the caller when the operation ends), so the reader must not zero them.
        using var reader = new RepositoryManifestReader(_logger, _storage, _passwordProvider, zeroKeysOnDispose: false);
        var manifest = await reader.ReadFileAsync(tempManifestPath, manifestPath, cancellationToken).ConfigureAwait(false);

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

        return new LoadedManifest(manifest, manifestPath);
    }

    private async Task<byte[]?> ResolveEncryptionMasterKeyAsync(SnapshotManifest manifest)
    {
        if (!manifest.EncryptionEnabled)
        {
            return null;
        }

        if (manifest.Keys != null)
        {
            // Encrypted v3: the key file was unlocked when the manifest was read.
            return manifest.Keys.ChunkKey;
        }

        if (_passwordProvider == null)
        {
            throw new InvalidOperationException("Encrypted snapshot detected but no password provider available");
        }

        var password = await _passwordProvider.GetPasswordAsync(PasswordPurpose.Decrypt).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Password required to decrypt snapshot");
        }

        if (string.IsNullOrWhiteSpace(manifest.EncryptionSalt))
        {
            throw new InvalidOperationException("Snapshot manifest is encrypted but encryption salt is missing");
        }

        var saltBytes = Convert.FromBase64String(manifest.EncryptionSalt);
        return _encryptionService.DeriveKeyFromPassword(
            password,
            saltBytes,
            manifest.KeyDerivationIterations);
    }

    /// <summary>
    /// Sets the times and attributes a v3 manifest recorded. Times first, attributes (read-only in particular) last, so a
    /// read-only file can still have its times set. Best effort: a file whose content restored correctly is not failed
    /// because the filesystem refused a timestamp.
    /// </summary>
    /// <summary>Windows refuses to replace a read-only file; clear the flag on the existing destination (attributes are re-applied last).</summary>
    internal static void ClearReadOnly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The move reports the real failure.
        }
    }

    private void ApplyFileMetadata(string path, SnapshotFileManifestEntry file)
    {
        try
        {
            if (file.CreatedUtc is { } created && created.Year > 1601)
            {
                File.SetCreationTimeUtc(path, DateTime.SpecifyKind(created, DateTimeKind.Utc));
            }

            if (file.LastModifiedUtc.Year > 1601)
            {
                File.SetLastWriteTimeUtc(path, DateTime.SpecifyKind(file.LastModifiedUtc, DateTimeKind.Utc));
            }

            if (file.Attributes is { } attributes)
            {
                File.SetAttributes(path, attributes == 0 ? FileAttributes.Normal : (FileAttributes)attributes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.Log($"Restored '{path}' but could not set its timestamps/attributes: {ex.Message}", LogLevel.Warning);
        }
    }

    /// <summary>
    /// Recreates the empty directories a v3 manifest recorded. A selective restore only creates those it named. An
    /// existing directory is left untouched (its times and attributes are the user's).
    /// </summary>
    private void RestoreEmptyDirectories(
        SnapshotManifest manifest,
        string targetDirectory,
        IReadOnlySet<string>? selectedRelativePaths,
        RestoreOutcome outcome)
    {
        if (manifest.Directories is not { Count: > 0 } directories)
        {
            return;
        }

        HashSet<string>? selection = selectedRelativePaths?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeManifestRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var verifiedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories.OrderBy(d => d.RelativePath, StringComparer.Ordinal))
        {
            if (selection != null && !selection.Contains(NormalizeManifestRelativePath(directory.RelativePath)))
            {
                continue;
            }

            var outputPath = ResolveSafeOutputPath(targetDirectory, directory.RelativePath, verifiedDirectories);
            if (Directory.Exists(outputPath))
            {
                continue;
            }

            Directory.CreateDirectory(outputPath);
            outcome.DirectoriesRestored++;

            try
            {
                if (directory.LastWriteUtc.Year > 1601)
                {
                    Directory.SetLastWriteTimeUtc(outputPath, DateTime.SpecifyKind(directory.LastWriteUtc, DateTimeKind.Utc));
                }

                if (directory.Attributes != 0)
                {
                    File.SetAttributes(outputPath, (FileAttributes)directory.Attributes);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.Log($"Created directory '{outputPath}' but could not set its time/attributes: {ex.Message}", LogLevel.Warning);
            }
        }
    }

    private static List<SnapshotFileManifestEntry> SelectFiles(
        SnapshotManifest manifest,
        IReadOnlySet<string>? relativePaths)
    {
        var ordered = manifest.Files.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);

        if (relativePaths == null)
        {
            return [.. ordered];
        }

        var normalizedSelection = relativePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeManifestRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. ordered.Where(entry =>
            normalizedSelection.Contains(NormalizeManifestRelativePath(entry.RelativePath)))];
    }

    private static string NormalizeManifestRelativePath(string relativePath)
    {
        return relativePath
            .Replace('\\', '/')
            .TrimStart('/');
    }

    private RestorePreview BuildPreview(
        string requestedBackupPath,
        string resolvedManifestPath,
        SnapshotManifest manifest,
        string targetDirectory,
        List<SnapshotFileManifestEntry> selectedFiles,
        int filesFilteredOut)
    {
        var entries = new List<RestorePreviewEntry>(selectedFiles.Count);
        var verifiedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in selectedFiles)
        {
            // Rejects traversal before anything is reported, so a hostile manifest cannot
            // have its escaping paths shown as if they were legitimate targets.
            var targetPath = ResolveSafeOutputPath(targetDirectory, file.RelativePath, verifiedDirectories);

            var conflict = RestoreConflictKind.None;
            long? existingSize = null;
            DateTime? existingLastModified = null;

            var existing = new FileInfo(targetPath);
            if (existing.Exists)
            {
                existingSize = existing.Length;
                existingLastModified = existing.LastWriteTimeUtc;

                conflict = IsSameContent(existing, file, manifest.GetHashScheme())
                    ? RestoreConflictKind.Identical
                    : RestoreConflictKind.Differs;
            }

            entries.Add(new RestorePreviewEntry
            {
                RelativePath = file.RelativePath,
                TargetPath = targetPath,
                SizeBytes = file.SizeBytes,
                LastModifiedUtc = file.LastModifiedUtc,
                ContentHash = file.ContentHash,
                Conflict = conflict,
                ExistingSizeBytes = existingSize,
                ExistingLastModifiedUtc = existingLastModified
            });
        }

        return new RestorePreview
        {
            RequestedBackupPath = requestedBackupPath,
            ResolvedManifestPath = resolvedManifestPath,
            SnapshotId = manifest.SnapshotId,
            SnapshotCreatedUtc = manifest.CreatedUtc,
            TargetDirectory = Path.GetFullPath(targetDirectory),
            EncryptionEnabled = manifest.EncryptionEnabled,
            Entries = entries,
            FilesFilteredOut = Math.Max(0, filesFilteredOut)
        };
    }

    private static bool IsSameContent(FileInfo existing, SnapshotFileManifestEntry file, ChunkHashScheme scheme)
    {
        if (existing.Length != file.SizeBytes)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(file.ContentHash))
        {
            return false;
        }

        try
        {
            return (scheme.IsKeyed ? scheme.HashFile(existing.FullName) : FileHasher.ComputeHash(existing.FullName))
                .Equals(file.ContentHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string BuildKeepBothPath(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(outputPath);
        var extension = Path.GetExtension(outputPath);

        var candidate = Path.Combine(directory, $"{fileName} (restored){extension}");
        var suffix = 2;

        // Repeat runs must not collide with the copy an earlier run already wrote.
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{fileName} (restored {suffix}){extension}");
            suffix++;
        }

        return candidate;
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

    /// <summary>
    /// Downloads one chunk object and returns its stored (possibly encrypted) bytes. Safe to run concurrently with other
    /// downloads: each uses its own temp file, deleted as soon as it has been read so %TEMP% holds roughly one chunk per
    /// in-flight download rather than the whole unique data set.
    /// </summary>
    private async Task<byte[]> DownloadStoredChunkAsync(
        SnapshotChunkManifestEntry chunk,
        string? chunkStorageNamespace,
        string tempDirectory,
        RestoreTelemetry telemetry,
        CancellationToken cancellationToken)
    {
        var normalizedChunkId = SnapshotStoragePaths.NormalizeChunkId(chunk.ChunkId);
        var chunkRemotePath = SnapshotStoragePaths.GetChunkPath(normalizedChunkId, chunkStorageNamespace);
        var chunkTempPath = Path.Combine(tempDirectory, $"{normalizedChunkId}.{Guid.NewGuid():N}.chunk");

        try
        {
            await _storage.DownloadAsync(chunkRemotePath, chunkTempPath, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref telemetry.ChunkDownloads);
            return await File.ReadAllBytesAsync(chunkTempPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteTemporaryFile(chunkTempPath);
        }
    }

    /// <summary>
    /// Decrypts (when encrypted) and verifies one downloaded chunk, then caches it. Always called by the single consumer
    /// in manifest order, never from a download task. That keeps the wrong-password rule well defined under parallel
    /// downloads: the first authentication failure before any chunk has decrypted is reported as a wrong password, a
    /// later one as a corrupted chunk, exactly as in a sequential restore.
    /// </summary>
    private static byte[] DecodeChunk(
        SnapshotChunkManifestEntry chunk,
        byte[] storedBytes,
        bool encrypted,
        byte[]? encryptionMasterKey,
        ChunkHashScheme hashScheme,
        ChunkByteCache chunkCache,
        RestoreTelemetry telemetry)
    {
        var normalizedChunkId = SnapshotStoragePaths.NormalizeChunkId(chunk.ChunkId);

        byte[] plaintextBytes;
        try
        {
            plaintextBytes = encrypted
                ? EncryptionService.DecryptChunkDeterministic(storedBytes, encryptionMasterKey!, normalizedChunkId, hashScheme.IsKeyed ? SnapshotChunkCodecs.Normalize(chunk.Codec) : null)
                : storedBytes;
            telemetry.ChunksDecrypted++;
        }
        catch (CryptographicException ex) when (telemetry.ChunksDecrypted == 0)
        {
            // Nothing has decrypted yet, so the password is the likely culprit: stop with one
            // clear error (the caller clears the cached password).
            throw new CryptographicException(
                "Wrong password (or corrupted data): chunk authentication failed. Restore stopped.", ex);
        }
        catch (CryptographicException ex)
        {
            // Earlier chunks decrypted, so the password is right and this chunk is damaged.
            throw new InvalidOperationException($"Corrupted chunk '{normalizedChunkId}': authentication failed. {ex.Message}", ex);
        }

        // Decompression runs after decryption, so a wrong password is still reported by the authentication step above.
        try
        {
            plaintextBytes = ChunkCompression.Decode(chunk.Codec, plaintextBytes, chunk.PlainSizeBytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Corrupted chunk '{normalizedChunkId}': {ex.Message}", ex);
        }

        if (plaintextBytes.Length != chunk.PlainSizeBytes)
        {
            throw new InvalidOperationException(
                $"Chunk size mismatch for chunk '{normalizedChunkId}'. Expected {chunk.PlainSizeBytes}, actual {plaintextBytes.Length}.");
        }

        var actualChunkHash = hashScheme.ChunkId(plaintextBytes);
        if (!actualChunkHash.Equals(chunk.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Chunk hash mismatch for chunk '{normalizedChunkId}'. Expected '{chunk.ContentHash}', actual '{actualChunkHash}'.");
        }

        chunkCache.Set(normalizedChunkId, plaintextBytes);
        return plaintextBytes;
    }

    /// <summary>
    /// Keeps up to <c>window</c> chunk downloads running ahead of the consumer and hands results back strictly in the
    /// order the chunks were supplied. Scheduling and cache access happen only on the consumer's thread (inside
    /// <see cref="NextAsync"/>), so the cache needs no lock. A chunk that is already cached, or already being downloaded
    /// for an earlier slot, is not downloaded again. Disposing cancels and awaits whatever is still running.
    /// </summary>
    private sealed class ChunkPrefetcher : IAsyncDisposable
    {
        private readonly IEnumerator<SnapshotChunkManifestEntry> _source;
        private readonly int _window;
        private readonly Func<SnapshotChunkManifestEntry, CancellationToken, Task<byte[]>> _download;
        private readonly Func<string, byte[]?> _tryCache;
        private readonly Action _onCacheHit;
        private readonly CancellationTokenSource _cts;
        private readonly Queue<Slot> _pending = new();
        private readonly Dictionary<string, Task<byte[]>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
        private bool _exhausted;

        public ChunkPrefetcher(
            IEnumerable<SnapshotChunkManifestEntry> chunks,
            int maxConcurrency,
            Func<SnapshotChunkManifestEntry, CancellationToken, Task<byte[]>> download,
            Func<string, byte[]?> tryCache,
            Action onCacheHit,
            CancellationToken cancellationToken)
        {
            _source = chunks.GetEnumerator();
            _window = Math.Max(1, maxConcurrency);
            _download = download;
            _tryCache = tryCache;
            _onCacheHit = onCacheHit;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        /// <summary>Returns the next chunk's bytes (plaintext when it came from the cache, otherwise stored bytes).</summary>
        public async Task<FetchedChunk> NextAsync(SnapshotChunkManifestEntry expected)
        {
            Fill(_window);

            if (_pending.Count == 0)
            {
                throw new InvalidOperationException("Chunk prefetcher ran out of chunks before the restore finished.");
            }

            var slot = _pending.Dequeue();
            if (!ReferenceEquals(slot.Chunk, expected))
            {
                throw new InvalidOperationException("Chunk prefetcher is out of step with the restore loop.");
            }

            // The chunk handed to the caller counts against the window, so with a window of one nothing is
            // started ahead and the restore stays strictly sequential.
            if (slot.CachedPlaintext != null)
            {
                Fill(_window - 1);
                return new FetchedChunk(slot.CachedPlaintext, IsPlaintext: true);
            }

            byte[] stored;
            try
            {
                stored = await slot.Download!.ConfigureAwait(false);
            }
            finally
            {
                if (_inFlight.TryGetValue(slot.ChunkId, out var current) && ReferenceEquals(current, slot.Download))
                {
                    _inFlight.Remove(slot.ChunkId);
                }
            }

            Fill(_window - 1);
            return new FetchedChunk(stored, IsPlaintext: false);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _source.Dispose();

            foreach (var slot in _pending)
            {
                if (slot.Download != null)
                {
                    try { await slot.Download.ConfigureAwait(false); } catch { /* the restore already failed or was cancelled */ }
                }
            }

            _pending.Clear();
            _cts.Dispose();
        }

        private void Fill(int limit)
        {
            while (!_exhausted && _pending.Count < limit)
            {
                if (!_source.MoveNext())
                {
                    _exhausted = true;
                    return;
                }

                var chunk = _source.Current;
                var chunkId = SnapshotStoragePaths.NormalizeChunkId(chunk.ChunkId);

                var cached = _tryCache(chunkId);
                if (cached != null)
                {
                    _onCacheHit();
                    _pending.Enqueue(new Slot(chunk, chunkId, cached, null));
                    continue;
                }

                if (_inFlight.TryGetValue(chunkId, out var shared))
                {
                    _onCacheHit();
                    _pending.Enqueue(new Slot(chunk, chunkId, null, shared));
                    continue;
                }

                var token = _cts.Token;
                var download = Task.Run(() => _download(chunk, token), CancellationToken.None);
                _inFlight[chunkId] = download;
                _pending.Enqueue(new Slot(chunk, chunkId, null, download));
            }
        }

        private sealed record Slot(SnapshotChunkManifestEntry Chunk, string ChunkId, byte[]? CachedPlaintext, Task<byte[]>? Download);
    }

    private readonly record struct FetchedChunk(byte[] Bytes, bool IsPlaintext);

    /// <summary>
    /// Deletes <c>*.restorepartial</c> files left in the target tree by a restore that crashed or could not clean up.
    /// Only the suffix Restore itself appends, and only files last written before this run started, so a partial
    /// being written by another restore is not touched (it is also open exclusively, so deleting it would fail).
    /// Junctions and symlinks are not followed.
    /// </summary>
    private void SweepStalePartialFiles(string targetDirectory, DateTime runStartedUtc)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            var removed = 0;
            foreach (var path in Directory.EnumerateFiles(targetDirectory, "*" + FileSelectionService.RestorePartialExtension, options))
            {
                try
                {
                    if (!FileSelectionService.IsRestorePartialFile(path) || File.GetLastWriteTimeUtc(path) >= runStartedUtc)
                    {
                        continue;
                    }

                    File.Delete(path);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.Log($"Could not remove leftover '{path}': {ex.Message}", LogLevel.Warning);
                }
            }

            if (removed > 0)
            {
                _logger.Log($"Removed {removed} leftover {FileSelectionService.RestorePartialExtension} file(s) from {targetDirectory}.", LogLevel.Info);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Log($"Could not sweep leftover {FileSelectionService.RestorePartialExtension} files in {targetDirectory}: {ex.Message}", LogLevel.Warning);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort; a leftover .restorepartial is inert.
        }
    }

    private sealed record LoadedManifest(SnapshotManifest Manifest, string ManifestPath);

    private sealed class ChunkByteCache(long maxBytes)
    {
        private readonly long _maxBytes = Math.Max(0, maxBytes);
        private readonly Dictionary<string, LinkedListNode<ChunkCacheEntry>> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<ChunkCacheEntry> _lru = [];
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

            var entry = new ChunkCacheEntry(chunkId, bytes);
            var node = new LinkedListNode<ChunkCacheEntry>(entry);
            _lru.AddFirst(node);
            _entries[chunkId] = node;
            _currentBytes += entry.SizeBytes;

            Trim();
        }

        private void Trim()
        {
            while (_currentBytes > _maxBytes && _lru.Last != null)
            {
                var node = _lru.Last;
                _lru.RemoveLast();
                _entries.Remove(node.Value.ChunkId);
                _currentBytes -= node.Value.SizeBytes;
            }
        }
    }

    private sealed class ChunkCacheEntry(string chunkId, byte[] bytes)
    {
        public string ChunkId { get; } = chunkId;
        public byte[] Bytes { get; } = bytes;
        public long SizeBytes { get; } = bytes.LongLength;
    }

    private static string ResolveSafeOutputPath(string targetDirectory, string relativePath, HashSet<string>? verifiedDirectories = null)
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

        RejectLinkedDirectories(normalizedTargetDirectory, combinedPath, relativePath, verifiedDirectories);
        return combinedPath;
    }

    /// <summary>
    /// A symbolic link or junction planted inside the restore target would carry a write out of it, past the textual containment
    /// check above. Directories between the target and the output path must not be links (the target folder itself may be one).
    /// <paramref name="verifiedDirectories"/> remembers directories already checked so a large restore does not re-check each one per file.
    /// </summary>
    private static void RejectLinkedDirectories(string targetDirectory, string outputPath, string relativePath, HashSet<string>? verifiedDirectories)
    {
        var root = Path.TrimEndingDirectorySeparator(targetDirectory);
        var current = Path.GetDirectoryName(outputPath);
        var pending = new List<string>();
        while (current != null
            && !string.Equals(Path.TrimEndingDirectorySeparator(current), root, StringComparison.OrdinalIgnoreCase)
            && verifiedDirectories?.Contains(current) != true)
        {
            var directory = new DirectoryInfo(current);
            if (directory.Exists && directory.LinkTarget != null)
            {
                throw new InvalidOperationException(
                    $"Snapshot manifest path '{relativePath}' passes through a link ('{current}') inside the restore target directory; refusing to write through it.");
            }

            pending.Add(current);
            current = Path.GetDirectoryName(current);
        }

        foreach (var directory in pending)
        {
            verifiedDirectories?.Add(directory);
        }
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
                || invalidOperationException.Message.Contains("Corrupted chunk", StringComparison.OrdinalIgnoreCase)
                || invalidOperationException.Message.Contains("Chunk size mismatch", StringComparison.OrdinalIgnoreCase))
            {
                return "chunk-validation-failure";
            }

            if (invalidOperationException.Message.Contains("Restored file hash mismatch", StringComparison.OrdinalIgnoreCase))
            {
                return "file-validation-failure";
            }

            if (invalidOperationException.Message.Contains("would replace", StringComparison.OrdinalIgnoreCase))
            {
                return "restore-conflict";
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

        /// <summary>Updated with Interlocked from concurrent download tasks.</summary>
        public int ChunkDownloads;

        public int ChunkCacheHits { get; set; }
        public int ChunksDecrypted { get; set; }
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
