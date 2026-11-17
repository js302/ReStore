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
    private readonly IPasswordProvider? _passwordProvider;
    private readonly RetentionManager _retentionManager;
    private readonly EncryptionService _encryptionService;
    private readonly RepositoryCoordinator _coordinator;

    /// <summary>Extra attempts for a file that changes while it is being read, before it is skipped.</summary>
    private const int MaxChangedFileRetries = 2;

    public Backup(
        ILogger logger,
        SystemState state,
        SizeAnalyzer sizeAnalyzer,
        IConfigManager config,
        IPasswordProvider? passwordProvider = null,
        RepositoryCoordinator? coordinator = null)
    {
        _coordinator = coordinator ?? RepositoryCoordinator.Instance;
        _logger = logger;
        _state = state;
        _sizeAnalyzer = sizeAnalyzer;
        _config = config ?? throw new ArgumentNullException(nameof(config), "Config cannot be null");
        _fileSelectionService = new FileSelectionService(logger, _config);
        _passwordProvider = passwordProvider;
        _encryptionService = new EncryptionService(_logger);

        _retentionManager = new RetentionManager(_logger, _config, _state, _coordinator, passwordProvider);
    }

    /// <summary>
    /// When true (watcher, scheduler), a backup that finds the repository busy throws
    /// <see cref="RepositoryBusyException"/> immediately instead of waiting, so the caller can skip
    /// and retry later. Interactive callers (GUI, CLI) leave this false and wait their turn.
    /// </summary>
    public bool FailFastWhenBusy { get; init; }

    /// <summary>
    /// The folder passed to <see cref="BackupDirectoryAsync"/> was named explicitly (CLI <c>--allow-excluded</c>): an
    /// <c>excludedPaths</c> entry that contains it does not exclude its files.
    /// </summary>
    public bool IgnoreExcludedPathsContainingSource { get; init; }

    // Every backup holds the mutation lease for its whole run (and, inside it, the delete lease for the
    // retention phase). The lease is taken here, at the engine layer, so every caller gets it.
    private async Task<RepositoryLease> AcquireMutationLeaseAsync(string reason, CancellationToken cancellationToken)
    {
        // Resolve (and if needed prompt for) the password before taking the lease: a modal dialog must not hold the repository.
        if (_config.Encryption?.Enabled == true)
        {
            await PasswordPrefetch.WarmAsync(_passwordProvider, PasswordPurpose.Encrypt).ConfigureAwait(false);
        }

        if (!FailFastWhenBusy)
        {
            return await _coordinator.AcquireMutationAsync(reason, cancellationToken, _state).ConfigureAwait(false);
        }

        return await _coordinator.TryAcquireMutationAsync(TimeSpan.Zero, reason, cancellationToken, _state).ConfigureAwait(false)
            ?? throw new RepositoryBusyException(
                _coordinator.CurrentReason is { } current
                    ? $"Another operation is running: {current}"
                    : "Another ReStore operation is running on this repository.");
    }

    /// <summary>
    /// Files to back up for (group, storage type). With <paramref name="forceFull"/> every file counts as changed:
    /// the destination has no usable previous snapshot, so the recorded metadata says nothing about what it holds.
    /// </summary>
    private List<string> GetFilesToBackup(IReadOnlyList<ScannedFile> allFiles, string group, string storageType, bool forceFull, ChangeScanContext scan)
    {
        if (forceFull)
        {
            _logger.Log("Destination has no usable previous snapshot; treating every file as changed.", LogLevel.Info);
            scan.FilesScanned += allFiles.Count;
            scan.FilesChanged += allFiles.Count;
            return [.. allFiles.Select(file => file.Path)];
        }

        var backupType = _config.BackupType;
        _logger.Log($"Determining files to backup based on type: {backupType}", LogLevel.Debug);

        var filesToBackup = _state.GetChangedFiles(allFiles, backupType, group, storageType, scan) ?? [];

        _logger.Log($"Identified {filesToBackup.Count} files requiring backup.", LogLevel.Info);
        return filesToBackup;
    }

    internal const string CloudOnlySkipReason = "cloud-only (not downloaded)";

    internal FileSelectionService FileSelection => _fileSelectionService;

    /// <summary>Test seam: number of full tree walks this instance has done.</summary>
    internal int TreeScanCount { get; private set; }

    /// <summary>Test seam: replaces how source files are opened for chunking.</summary>
    internal Func<string, Stream>? SourceFileOpener { get; set; }

    public async Task<BackupResult> BackupDirectoryAsync(
        string sourceDirectory,
        string? storageTypeOverride = null,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            throw new ArgumentException("Source directory cannot be null or empty", nameof(sourceDirectory));
        }

        var lease = await AcquireMutationLeaseAsync($"Backing up {sourceDirectory}", cancellationToken).ConfigureAwait(false);
        await using var leaseScope = lease.ConfigureAwait(false);
        var activityStartUtc = DateTime.UtcNow;

        IStorage? storage = null;
        try
        {
            sourceDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(sourceDirectory));

            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException($"Source directory not found: {sourceDirectory}");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var storageType = storageTypeOverride ?? GetStorageTypeForDirectory(sourceDirectory);
            storage = await _config.CreateStorageAsync(storageType).ConfigureAwait(false);

            _logger.Log($"Starting backup of {sourceDirectory} using {storageType} storage");

            progress?.Report(new BackupProgress(sourceDirectory, 0, 0, 0, 0, BackupPhase.Enumerating));

            cancellationToken.ThrowIfCancellationRequested();

            // Taken before anything is read: it anchors the racy-timestamp rule for what this run records.
            var scanStartUtc = DateTime.UtcNow;
            var runTimer = System.Diagnostics.Stopwatch.StartNew();

            // The tree is walked once; selection, change detection and progress totals all use these records.
            var unreadableDirectories = new List<string>();
            var cloudOnlyFiles = new List<ScannedFile>();
            var scannedFiles = GetFilesInDirectory(sourceDirectory, unreadableDirectories, cloudOnlyFiles);
            var allFiles = scannedFiles.Select(file => file.Path).ToList();

            _sizeAnalyzer.SizeThreshold = _config.SizeThresholdMB * 1024 * 1024;
            var size = scannedFiles.Sum(file => file.Length);
            if (size > _sizeAnalyzer.SizeThreshold)
            {
                _logger.Log($"Warning: Directory size ({size} bytes) exceeds threshold");
            }

            // The destination decides what "changed" means: a folder whose recorded metadata says "unchanged" must
            // still be uploaded in full to a destination that holds no usable snapshot of it.
            var forceFullReason = await GetForceFullPassReasonAsync(sourceDirectory, storageType, storage, cancellationToken).ConfigureAwait(false);
            if (forceFullReason != null)
            {
                _logger.Log($"Backing up every file of {sourceDirectory} to {storageType}: {forceFullReason}.", LogLevel.Info);
            }

            // A deep scan reads and hashes every file; between deep scans unchanged files are recognised by their
            // size and timestamps. A forced full pass reads everything anyway, so it counts as one.
            var deepScan = forceFullReason != null
                || _state.IsDeepScanDue(sourceDirectory, storageType, DeepScanIntervalDays, scanStartUtc);
            // Encrypted runs need the key material for the scan too: encrypted v3 repositories record keyed content hashes.
            var encryptionContext = _config.Encryption.Enabled
                ? await BuildEncryptionContextAsync(storage, cancellationToken).ConfigureAwait(false)
                : null;
            var scan = new ChangeScanContext
            {
                ScanStartUtc = scanStartUtc,
                DeepScan = deepScan,
                ContentHasher = encryptionContext?.Keys is { } scanKeys ? scanKeys.HashScheme.HashFile : null
            };

            var filesToBackup = GetFilesToBackup(scannedFiles, sourceDirectory, storageType, forceFullReason != null, scan);

            _logger.Log(
                $"Scan of {sourceDirectory} ({(deepScan ? "deep" : "stat-cache")}): {scannedFiles.Count} files scanned, {scan.FilesHashed} hashed, {scan.FilesReused} reused unread, {filesToBackup.Count} to back up.",
                LogLevel.Info);

            // Metadata of files that no longer exist in this directory; dropped only once the snapshot is committed,
            // otherwise a failed backup would forget the removal and never retry it.
            var trackedFiles = _state.GetTrackedFiles(sourceDirectory, storageType) ?? [];
            // Files under a directory that could not be read are unknown, not removed: keep their metadata.
            var filesToRemove = trackedFiles
                .Except(allFiles, StringComparer.OrdinalIgnoreCase)
                .Except(cloudOnlyFiles.Select(file => file.Path), StringComparer.OrdinalIgnoreCase)
                .Where(file => !IsUnderAnyDirectory(file, unreadableDirectories))
                .ToList();

            // Same encryption setting but a different key (password or salt changed): the stored chunks live in another
            // namespace, so nothing at the destination can be reused. Needs the key, so it is only derived here when
            // the run would otherwise do nothing.
            if (filesToBackup.Count == 0 && filesToRemove.Count == 0 && forceFullReason == null && encryptionContext != null)
            {
                if (!LastSnapshotMatchesNamespace(sourceDirectory, storageType, IntendedChunkNamespace(encryptionContext, sourceDirectory, storageType)))
                {
                    _logger.Log($"Backing up every file of {sourceDirectory} to {storageType}: the encryption key changed since the last snapshot.", LogLevel.Info);
                    filesToBackup = allFiles;
                    deepScan = true;
                }
            }

            if (filesToBackup.Count == 0 && filesToRemove.Count == 0)
            {
                _logger.Log("No files need to be backed up based on the current state and backup type.", LogLevel.Info);
                if (deepScan)
                {
                    _state.RecordDeepScan(sourceDirectory, storageType, scanStartUtc);
                }

                _state.RecordGroupRunSucceeded(sourceDirectory, DateTime.UtcNow);
                await _state.SaveStateAsync().ConfigureAwait(false);

                // Nothing new to snapshot and HEAD is untouched, so whatever the previous snapshot
                // holds under an unreadable directory is still there.
                return BuildResult([
                    .. unreadableDirectories.Select(dir =>
                        new SkippedFile(
                            dir,
                            "Directory could not be read",
                            trackedFiles.Any(file => IsUnderAnyDirectory(file, [dir])))),
                    .. cloudOnlyFiles.Select(file => new SkippedFile(
                        file.Path,
                        CloudOnlySkipReason,
                        trackedFiles.Contains(file.Path, StringComparer.OrdinalIgnoreCase)))], scannedFiles.Count);
            }

            _logger.Log($"Preparing to build snapshot manifest for {sourceDirectory}. Changed files: {filesToBackup.Count}, removed files: {filesToRemove.Count}", LogLevel.Info);
            var snapshot = (await CreateSnapshotBackupAsync(sourceDirectory, scannedFiles, unreadableDirectories, filesToBackup, storage, storageType, lease, progress, cancellationToken, encryptionContext, cloudOnlyFiles: cloudOnlyFiles).ConfigureAwait(false))!;

            // Metadata comes from the entries the snapshot holds, not from re-reading the files, so a file that
            // changed after it was chunked is still seen as changed. Skipped files get none and are retried next run.
            _state.RecordFileMetadata(sourceDirectory, storageType, snapshot.ChunkedEntries, scanStartUtc, scannedFiles);
            _state.RemoveFileMetadata(sourceDirectory, storageType, filesToRemove);
            // Only a deep scan that verified every file counts: unreadable or skipped files leave it due.
            if (deepScan && scan.FilesUnverified == 0 && snapshot.Skipped.Count == 0)
            {
                _state.RecordDeepScan(sourceDirectory, storageType, scanStartUtc);
            }

            _state.LastBackupTime = DateTime.UtcNow;
            _state.RecordBackupRun(BackupRunReport.Create(
                sourceDirectory, storageType, filesToBackup, trackedFiles, filesToRemove.Count,
                snapshot.Skipped, runTimer.Elapsed, snapshot.UploadedBytes, _state.LastBackupTime, snapshot.ReplacedChunks));

            await _state.SaveStateAsync().ConfigureAwait(false);
            return BuildResult(snapshot.Skipped, scannedFiles.Count);
        }
        catch (OperationCanceledException)
        {
            // Not a backup failure: the three-phase commit means HEAD has not advanced, so
            // the snapshot simply did not happen. Must not be logged as an error or swallowed.
            _logger.Log($"Backup of {sourceDirectory} cancelled by request.", LogLevel.Warning);
            ActivityRecorder.RecordFailure(_state, ActivityType.Backup, sourceDirectory, activityStartUtc, null);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to backup directory: {ex.Message}", LogLevel.Error);
            ActivityRecorder.RecordFailure(_state, ActivityType.Backup, sourceDirectory, activityStartUtc, ex);
            throw;
        }
        finally
        {
            storage?.Dispose();
        }
    }

    public async Task<BackupResult> BackupFilesAsync(
        IEnumerable<string> filesToBackup,
        string baseDirectory,
        string? storageTypeOverride = null,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? previousPaths = null)
    {
        if (filesToBackup == null)
        {
            throw new ArgumentNullException(nameof(filesToBackup));
        }

        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("Base directory cannot be null or empty", nameof(baseDirectory));
        }

        // Restore's in-progress temp files are never backed up, even if a watcher reports them.
        var fileList = filesToBackup.Where(file => !FileSelectionService.IsRestorePartialFile(file)).ToList();
        if (fileList.Count == 0)
        {
            _logger.Log("No files provided for backup.", LogLevel.Info);
            return BackupResult.Empty;
        }

        var lease = await AcquireMutationLeaseAsync($"Backing up changes in {baseDirectory}", cancellationToken).ConfigureAwait(false);
        await using var leaseScope = lease.ConfigureAwait(false);
        var activityStartUtc = DateTime.UtcNow;

        IStorage? storage = null;
        try
        {
            var storageType = storageTypeOverride ?? GetStorageTypeForDirectory(baseDirectory);
            storage = await _config.CreateStorageAsync(storageType).ConfigureAwait(false);

            _logger.Log($"Starting backup of {fileList.Count} specific files from base directory {baseDirectory} using {storageType} storage", LogLevel.Info);

            var existingFilesToBackup = fileList.Where(File.Exists).ToList();
            var deletedFiles = fileList.Except(existingFilesToBackup, StringComparer.OrdinalIgnoreCase).ToList();

            if (existingFilesToBackup.Count == 0 && deletedFiles.Count == 0)
            {
                _logger.Log("No valid file changes detected for snapshot creation.", LogLevel.Info);
                await _state.SaveStateAsync().ConfigureAwait(false);
                return BackupResult.Empty;
            }

            var scanStartUtc = DateTime.UtcNow;
            var runTimer = System.Diagnostics.Stopwatch.StartNew();
            var trackedBefore = _state.GetTrackedFiles(baseDirectory, storageType) ?? [];

            // Preferred: extend the previous manifest with just this change set (no tree walk). Any doubt about the
            // previous snapshot falls back to the full pass below; the scheduler's interval/deep-scan passes
            // (BackupDirectoryAsync) are unaffected and still verify the whole tree.
            IReadOnlyList<ScannedFile> scannedFiles = [];
            SnapshotBackupResult? snapshot = null;
            var incremental = await TryPlanIncrementalAsync(baseDirectory, storageType, storage, existingFilesToBackup, deletedFiles, previousPaths, cancellationToken).ConfigureAwait(false);
            if (incremental != null)
            {
                scannedFiles = incremental.Changed;
                snapshot = await CreateSnapshotBackupAsync(incremental.Group, scannedFiles, [], [.. incremental.Changed.Select(file => file.Path)], storage, storageType, lease, progress, cancellationToken, null, incremental, incremental.CloudOnly).ConfigureAwait(false);
            }

            if (snapshot == null)
            {
                var unreadableDirectories = new List<string>();
                var cloudOnlyFiles = new List<ScannedFile>();
                scannedFiles = GetFilesInDirectory(baseDirectory, unreadableDirectories, cloudOnlyFiles);
                snapshot = (await CreateSnapshotBackupAsync(baseDirectory, scannedFiles, unreadableDirectories, existingFilesToBackup, storage, storageType, lease, progress, cancellationToken, cloudOnlyFiles: cloudOnlyFiles).ConfigureAwait(false))!;
            }

            // Applied once the snapshot is committed, on state reloaded under the lease: deleted files and the
            // paths files were renamed from are dropped, and the metadata of what was chunked is recorded.
            var group = Path.GetFullPath(Environment.ExpandEnvironmentVariables(baseDirectory));
            _state.RecordFileMetadata(group, storageType, snapshot.ChunkedEntries, scanStartUtc, scannedFiles);
            _state.RemoveFileMetadata(group, storageType, deletedFiles.Concat(previousPaths ?? []));
            _state.RecordBackupRun(BackupRunReport.Create(
                group, storageType, existingFilesToBackup, trackedBefore, deletedFiles.Count,
                snapshot.Skipped, runTimer.Elapsed, snapshot.UploadedBytes, DateTime.UtcNow, snapshot.ReplacedChunks));

            _logger.Log("Specific file snapshot backup completed.", LogLevel.Info);

            await _state.SaveStateAsync().ConfigureAwait(false);
            return BuildResult(snapshot.Skipped);
        }
        catch (OperationCanceledException)
        {
            _logger.Log($"Backup of specific files from {baseDirectory} cancelled by request.", LogLevel.Warning);
            ActivityRecorder.RecordFailure(_state, ActivityType.Backup, baseDirectory, activityStartUtc, null);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to backup specific files from {baseDirectory}: {ex.Message}", LogLevel.Error);
            ActivityRecorder.RecordFailure(_state, ActivityType.Backup, baseDirectory, activityStartUtc, ex);
            throw;
        }
        finally
        {
            storage?.Dispose();
        }
    }

    private BackupResult BuildResult(IReadOnlyList<SkippedFile> skipped, int? filesScanned = null)
    {
        var result = skipped.Count == 0 && filesScanned == null ? BackupResult.Empty : new BackupResult(skipped) { FilesScanned = filesScanned };
        if (result.HasSkipped)
        {
            _logger.Log($"Backup {result.SkippedSummary}", LogLevel.Warning);
            foreach (var file in skipped)
            {
                _logger.Log($"Skipped '{file.Path}': {file.Reason}{(file.CarriedForward ? " (previous version kept)" : string.Empty)}", LogLevel.Warning);
            }
        }

        return result;
    }

    private static bool IsUnderAnyDirectory(string path, List<string> directories)
    {
        foreach (var directory in directories)
        {
            var prefix = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private int DeepScanIntervalDays
        => _config.BackupSettings?.DeepScanIntervalDays ?? BackupSettingsConfig.DefaultDeepScanIntervalDays;

    private List<ScannedFile> GetFilesInDirectory(string directory, List<string> unreadableDirectories, List<ScannedFile>? cloudOnlyFiles = null)
    {
        try
        {
            var directoryList = new List<string> { directory };
            TreeScanCount++;
            _fileSelectionService.LiftExcludedPathsContaining = IgnoreExcludedPathsContainingSource ? directory : null;
            var files = _fileSelectionService.ScanFiles(directoryList, unreadableDirectories, cloudOnlyFiles);

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
            Path.GetFullPath(w.ExpandedPath)
                .Equals(normalizedDir, StringComparison.OrdinalIgnoreCase));

        return watchConfig?.StorageType ?? _config.GlobalStorageType;
    }

    /// <summary>What a committed snapshot holds: skipped files, and the manifest entries of the files it chunked.</summary>
    private sealed record SnapshotBackupResult(List<SkippedFile> Skipped, List<SnapshotFileManifestEntry> ChunkedEntries, long UploadedBytes = 0, int ReplacedChunks = 0);

    /// <summary>
    /// Key material for the current encryption settings; all null when encryption is off. <see cref="MasterKey"/> and
    /// <see cref="ChunkStorageNamespace"/> are the v2 scheme (password-derived). <see cref="Keys"/> is the unlocked v3 repository key
    /// set when the destination has a key file; <see cref="KeyFileError"/> says why an existing key file could not be opened.
    /// </summary>
    private sealed record EncryptionContext(
        Lazy<(byte[] MasterKey, string Namespace)>? V2Keys,
        string? Salt,
        string? Password = null,
        RepositoryKeys? Keys = null,
        bool KeyFileExists = false,
        string? KeyFileError = null)
    {
        // The password-derived v2 key (1M PBKDF2 iterations) is only computed when a v2 snapshot is actually written or compared.
        public byte[]? MasterKey => V2Keys?.Value.MasterKey;
        public string? ChunkStorageNamespace => V2Keys?.Value.Namespace;
    }

    private async Task<EncryptionContext> BuildEncryptionContextAsync(IStorage storage, CancellationToken cancellationToken)
    {
        if (!_config.Encryption.Enabled)
        {
            return new EncryptionContext(null, null);
        }

        EnsureEncryptionProviderAvailable();

        var password = await _passwordProvider!.GetPasswordAsync(PasswordPurpose.Encrypt).ConfigureAwait(false);
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException("Encryption is enabled but no password was provided");
        }

        if (string.IsNullOrWhiteSpace(_config.Encryption.Salt))
        {
            throw new InvalidOperationException("Encryption is enabled but encryption salt is missing from configuration");
        }

        var saltBytes = Convert.FromBase64String(_config.Encryption.Salt);
        var iterations = _config.Encryption.KeyDerivationIterations;
        var v2Keys = new Lazy<(byte[], string)>(() =>
        {
            var masterKey = _encryptionService.DeriveKeyFromPassword(password, saltBytes, iterations);
            return (masterKey, SnapshotStoragePaths.BuildEncryptedChunkNamespace(masterKey));
        });

        // v3 repositories keep their data key in a key file at the destination; opening it with the password is the key check
        // (a wrong password stops here, before any chunk is touched).
        RepositoryKeys? keys = null;
        string? keyFileError = null;
        var keyFile = await RepositoryKeyStore.ReadAsync(storage, cancellationToken).ConfigureAwait(false);
        if (keyFile != null)
        {
            try
            {
                keys = RepositoryKeyFiles.Unlock(keyFile, password);
            }
            catch (WrongRepositoryPasswordException)
            {
                keyFileError = "The configured password does not unlock this destination's repository key file "
                    + $"({RepositoryKeyFiles.StoragePath}); the password may have been changed from another computer.";
            }
        }

        return new EncryptionContext(
            v2Keys,
            _config.Encryption.Salt,
            password,
            keys,
            keyFile != null,
            keyFileError);
    }

    /// <summary>
    /// The repository keys an encrypted v3 snapshot needs: the unlocked ones, or a new key file (first v3 snapshot of this
    /// destination). An existing key file that cannot be opened is an error, never replaced: replacing it would orphan every
    /// encrypted v3 snapshot already stored.
    /// </summary>
    private async Task<RepositoryKeys> EnsureRepositoryKeysAsync(EncryptionContext context, IStorage storage, CancellationToken cancellationToken)
    {
        if (context.Keys != null)
        {
            return context.Keys;
        }

        if (context.KeyFileExists)
        {
            throw new InvalidOperationException(context.KeyFileError ?? "The repository key file could not be opened.");
        }

        var (file, keys) = RepositoryKeyFiles.Create(context.Password!, _config.Encryption.KeyDerivationIterations);
        var stored = await RepositoryKeyStore.CreateIfAbsentAsync(storage, file, cancellationToken).ConfigureAwait(false);
        if (stored.KeyFileId != file.KeyFileId)
        {
            // Another computer created the key file at the same moment and won. Nothing was uploaded yet; the next run uses theirs.
            keys.Dispose();
            throw new InvalidOperationException(
                "Another computer created this destination's repository key file at the same time. Nothing was uploaded; run the backup again to use it.");
        }

        _logger.Log($"Created the repository key file {RepositoryKeyFiles.StoragePath}.", LogLevel.Info);
        return keys;
    }

    /// <summary>
    /// The chunk namespace the next snapshot of this group would use (without loading the previous manifest), for the
    /// "nothing changed" check: v3 when configured or when the last snapshot already is one; null when v3 is wanted but the
    /// destination has no key file yet (so the format upgrade is detected).
    /// </summary>
    private string? IntendedChunkNamespace(EncryptionContext context, string group, string storageType)
    {
        var configured = (_config.ChunkDiffing ?? new ChunkDiffingConfig()).ManifestVersion;
        var last = _state.GetLatestSnapshotForGroup(group, storageType, _config.GlobalStorageType);
        var wantsV3 = configured >= SnapshotManifest.VersionV3
            || (configured == 0 && last?.ChunkStorageNamespace?.StartsWith(RepositoryKeys.NamespacePrefix, StringComparison.OrdinalIgnoreCase) == true);
        return wantsV3 ? context.Keys?.ChunkNamespace : context.ChunkStorageNamespace;
    }

    /// <summary>
    /// Why every file must be backed up regardless of the recorded metadata, or null when the metadata can be
    /// trusted: the destination has no snapshot of this group, the last one was made with a different encryption
    /// setting, or its manifest is gone from the destination.
    /// </summary>
    private async Task<string?> GetForceFullPassReasonAsync(string group, string storageType, IStorage storage, CancellationToken cancellationToken)
    {
        var last = _state.GetLatestSnapshotForGroup(group, storageType, _config.GlobalStorageType);
        if (last == null)
        {
            return "no previous snapshot at this destination";
        }

        if (last.Encrypted != _config.Encryption.Enabled)
        {
            return "encryption was turned " + (_config.Encryption.Enabled ? "on" : "off") + " since the last snapshot";
        }

        var manifestPath = string.IsNullOrWhiteSpace(last.ManifestPath) ? last.Path : last.ManifestPath;
        try
        {
            if (!await storage.ExistsAsync(manifestPath, cancellationToken).ConfigureAwait(false))
            {
                return $"the last snapshot manifest '{manifestPath}' is missing from the destination";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unreachable storage fails the upload anyway when there is something to upload; an idle run stays idle.
            _logger.Log($"Could not check for the last snapshot manifest '{manifestPath}': {ex.Message}", LogLevel.Warning);
        }

        return null;
    }

    private bool LastSnapshotMatchesNamespace(string group, string storageType, string? currentNamespace)
    {
        var last = _state.GetLatestSnapshotForGroup(group, storageType, _config.GlobalStorageType);
        if (last == null)
        {
            return false;
        }

        try
        {
            return string.Equals(
                SnapshotStoragePaths.NormalizeChunkStorageNamespace(last.ChunkStorageNamespace),
                SnapshotStoragePaths.NormalizeChunkStorageNamespace(currentNamespace),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Previous manifest entries by relative path. Paths that differ only by case (possible on case-sensitive
    /// directories) keep the first entry in ordinal order and are logged; <see cref="TryGetPreviousEntry"/> only
    /// returns an entry whose path matches exactly, so the surviving duplicate is never reused for its twin.
    /// </summary>
    private Dictionary<string, SnapshotFileManifestEntry> IndexPreviousFiles(SnapshotManifest? manifest)
    {
        var index = new Dictionary<string, SnapshotFileManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (manifest?.Files ?? []).OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            if (!index.TryAdd(entry.RelativePath, entry))
            {
                _logger.Log(
                    $"Previous snapshot lists '{index[entry.RelativePath].RelativePath}' and '{entry.RelativePath}', which differ only by case; the second will be backed up again.",
                    LogLevel.Warning);
            }
        }

        return index;
    }

    private static bool TryGetPreviousEntry(
        Dictionary<string, SnapshotFileManifestEntry> previousFilesByPath,
        string relativePath,
        out SnapshotFileManifestEntry entry)
    {
        return previousFilesByPath.TryGetValue(relativePath, out entry!)
            && string.Equals(entry.RelativePath, relativePath, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file judged unchanged reuses the previous manifest entry; that is only sound when the entry is what the
    /// recorded metadata says was backed up. State restored from an older copy, or a snapshot made by another
    /// process, can disagree; the file is then re-chunked. No record at all is not a disagreement.
    /// </summary>
    private bool MatchesRecordedMetadata(string group, string storageType, string absolutePath, SnapshotFileManifestEntry entry)
    {
        if (!_state.TryGetFileMetadata(group, storageType, absolutePath, out var metadata) || metadata == null)
        {
            return true;
        }

        if (metadata.Size == entry.SizeBytes
            && string.Equals(metadata.Hash, entry.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        _logger.Log($"Recorded metadata of '{absolutePath}' disagrees with the previous snapshot entry; backing it up again.", LogLevel.Info);
        return false;
    }

    /// <summary>
    /// Returns null only when <paramref name="incremental"/> was requested but the previous snapshot cannot be extended
    /// safely (nothing has been written then); the caller falls back to a full pass.
    /// </summary>
    private async Task<SnapshotBackupResult?> CreateSnapshotBackupAsync(
        string sourceDirectory,
        IReadOnlyList<ScannedFile> scannedFiles,
        List<string> unreadableDirectories,
        List<string> changedFiles,
        IStorage storage,
        string storageType,
        RepositoryLease mutationLease,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default,
        EncryptionContext? encryptionContext = null,
        IncrementalChange? incremental = null,
        IReadOnlyList<ScannedFile>? cloudOnlyFiles = null)
    {
        var chunkConfig = _config.ChunkDiffing ?? new ChunkDiffingConfig();

        if (incremental == null && scannedFiles.Count > chunkConfig.MaxFilesPerSnapshot)
        {
            throw new InvalidOperationException(
                $"Snapshot exceeds maxFilesPerSnapshot safety limit ({chunkConfig.MaxFilesPerSnapshot}) for directory '{sourceDirectory}'.");
        }

        // Pins every chunk this snapshot references or reuses, from the moment it is decided until the
        // snapshot is registered (or the backup fails), so nothing can delete it in between.
        var pinToken = new object();

        try
        {
            var normalizedSourceDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(sourceDirectory));
            var normalizedChangedFiles = changedFiles
                .Select(path => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            encryptionContext ??= await BuildEncryptionContextAsync(storage, cancellationToken).ConfigureAwait(false);
            byte[]? encryptionMasterKey = null;
            var encryptionSalt = encryptionContext.Salt;
            string? chunkStorageNamespace = null;

            var previousManifest = await TryLoadLatestManifestAsync(normalizedSourceDirectory, storageType, storage, encryptionContext).ConfigureAwait(false);
            var manifestVersion = ResolveManifestVersion(chunkConfig, previousManifest, normalizedSourceDirectory, storageType);
            var writeV3 = manifestVersion >= SnapshotManifest.VersionV3;

            // Encrypted v3: keyed chunk ids in their own namespace, encrypted manifest. The password-derived v2 key is not used.
            RepositoryKeys? repositoryKeys = null;
            if (writeV3 && _config.Encryption.Enabled)
            {
                repositoryKeys = await EnsureRepositoryKeysAsync(encryptionContext, storage, cancellationToken).ConfigureAwait(false);
                encryptionMasterKey = repositoryKeys.ChunkKey;
                encryptionSalt = null;
                chunkStorageNamespace = repositoryKeys.ChunkNamespace;
            }
            else if (_config.Encryption.Enabled)
            {
                encryptionMasterKey = encryptionContext.MasterKey;
                chunkStorageNamespace = encryptionContext.ChunkStorageNamespace;
            }

            if (!writeV3 && previousManifest?.ChunkIdScheme != null)
            {
                throw new InvalidOperationException(
                    "chunkDiffing.manifestVersion is 2 but the previous snapshot of this group is an encrypted v3 snapshot, which a v2 manifest cannot describe. "
                    + "Set manifestVersion to 3 (or 0 to choose automatically) to continue this group.");
            }

            if (!writeV3 && ManifestUsesCompressedChunks(previousManifest))
            {
                throw new InvalidOperationException(
                    "chunkDiffing.manifestVersion is 2 but the previous snapshot of this group stores compressed (v3) chunks, which a v2 manifest cannot describe. "
                    + "Set manifestVersion to 3 (or 0 to choose automatically) to continue this group.");
            }

            var algorithmVersion = ChunkingProfile.AlgorithmForManifestVersion(manifestVersion);
            var compress = writeV3 && !string.Equals(chunkConfig.Compression, "off", StringComparison.OrdinalIgnoreCase);
            var chunkingService = new ChunkingService(
                _logger,
                chunkConfig,
                _encryptionService,
                _config.Encryption.Enabled,
                encryptionMasterKey,
                SourceFileOpener,
                recordCodecs: writeV3,
                compress: compress,
                hashScheme: repositoryKeys?.HashScheme,
                algorithmVersion: algorithmVersion);
            var previousFilesByPath = IndexPreviousFiles(previousManifest);

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

            // Chunk boundaries differ between algorithm versions, so reusing/carrying previous entries would
            // mix algorithms in one manifest. Chunk dedup by id still avoids re-uploading unchanged chunks.
            // Files that cannot be read in this case are omitted from the snapshot and reported as skipped.
            if (previousManifest != null
                && previousManifest.GetChunkingAlgorithmVersion() != algorithmVersion)
            {
                canReusePreviousManifestEntries = false;
                _logger.Log(
                    $"Chunking algorithm changed ({previousManifest.GetChunkingAlgorithmVersion()} -> {algorithmVersion}); re-chunking all files once.",
                    LogLevel.Info);
            }

            if (incremental != null)
            {
                var unsafeReason = previousManifest == null ? "there is no readable previous snapshot manifest"
                    : !canReusePreviousManifestEntries ? "the chunk namespace or chunking algorithm changed"
                    : previousManifest.Version != manifestVersion ? "the manifest format changes"
                    : previousManifest.EncryptionEnabled != _config.Encryption.Enabled ? "encryption settings changed"
                    : null;
                if (unsafeReason != null)
                {
                    _logger.Log($"Incremental snapshot not possible ({unsafeReason}); doing a full pass.", LogLevel.Info);
                    return null;
                }
            }

            if (incremental != null && ChangesPathKind(previousFilesByPath, normalizedSourceDirectory, incremental))
            {
                _logger.Log("A path changed between file and directory; doing a full pass.", LogLevel.Info);
                return null;
            }

            var manifestFiles = new List<SnapshotFileManifestEntry>();

            // Payloads upload as they are produced and are not retained, so peak memory does
            // not scale with the size of the change set. Only the handled ids are kept, to
            // skip chunks this snapshot already stored.
            var uploadedChunkIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var uploadTelemetry = new ChunkUploadTelemetry();

            // What is actually stored under each chunk id (codec and stored size), recorded by the upload workers. An object
            // that already exists wins over the codec this run chose, so manifest entries are corrected from it afterwards.
            var chunkOutcomes = new System.Collections.Concurrent.ConcurrentDictionary<string, StoredChunkForm>(StringComparer.OrdinalIgnoreCase);

            // Existence of chunks: per chunk for small backups, one listing of the chunk directory for large ones.
            var normalizedChunkNamespace = SnapshotStoragePaths.NormalizeChunkStorageNamespace(chunkStorageNamespace);
            var existenceIndex = new ChunkExistenceIndex(
                storage,
                string.IsNullOrWhiteSpace(normalizedChunkNamespace) ? "chunks/" : $"chunks/{normalizedChunkNamespace}/",
                ChunkExistenceIndex.DefaultListingThreshold,
                _logger);

            var skipped = new List<SkippedFile>();
            var chunkedEntries = new List<SnapshotFileManifestEntry>();

            // Not filtered by File.Exists: a file that vanished after enumeration is handled per file below
            // (carried forward if the previous snapshot has it).
            // Incremental: the previous manifest's files (minus removed paths) plus the changed ones, without a tree walk.
            var orderedFiles = incremental != null
                ? BuildIncrementalFileList(normalizedSourceDirectory, previousManifest!, normalizedChangedFiles, incremental.RemovedPaths)
                : scannedFiles
                    .Select(scanned => Path.GetFullPath(Environment.ExpandEnvironmentVariables(scanned.Path)))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (incremental != null && orderedFiles.Count > chunkConfig.MaxFilesPerSnapshot)
            {
                _logger.Log("Incremental snapshot would exceed maxFilesPerSnapshot; doing a full pass.", LogLevel.Info);
                return null;
            }

            // Progress counts what will actually be chunked: carried entries are not work in an incremental run.
            var progressTotalFiles = incremental != null ? incremental.Changed.Count : orderedFiles.Count;
            var chunkingBytesTotal = scannedFiles.Sum(scanned => scanned.Length);

            var filesProcessed = 0;
            var bytesProcessed = 0L;

            // Uploads run on up to MaxConcurrency workers while chunking continues, behind a small bounded queue.
            // A worker failure cancels the rest and is rethrown to the chunking loop (first error wins).
            var chunksQueued = 0;

            var uploadQueue = new ParallelWorkQueue<ChunkBuildPayload>(
                storage.MaxConcurrency,
                async (chunkPayload, uploadToken) =>
                {
                    await UploadChunkIfMissingAsync(
                        storage,
                        chunkPayload,
                        chunkStorageNamespace,
                        uploadTelemetry,
                        existenceIndex,
                        chunkOutcomes,
                        writeV3,
                        uploadToken).ConfigureAwait(false);

                    // Uploads interleave with chunking, so the byte totals are the running
                    // chunking totals, not a separate upload-only denominator.
                    progress?.Report(new BackupProgress(
                        $"chunk {Volatile.Read(ref chunksQueued)}",
                        filesProcessed,
                        progressTotalFiles,
                        bytesProcessed,
                        chunkingBytesTotal,
                        BackupPhase.Uploading));
                },
                cancellationToken);
            await using var uploadQueueScope = uploadQueue.ConfigureAwait(false);

            // Called by the chunker on this one thread, so the dedup set needs no lock. The pin is taken
            // here, before anything awaits, so no delete can slip in between deciding on and uploading a chunk.
            async Task UploadChunkAsync(ChunkBuildPayload chunkPayload, CancellationToken uploadToken)
            {
                if (!uploadedChunkIds.Add(chunkPayload.ChunkId))
                {
                    return;
                }

                _state.PinChunks(pinToken, [chunkPayload.ChunkId]);
                Interlocked.Increment(ref chunksQueued);
                await uploadQueue.EnqueueAsync(chunkPayload).ConfigureAwait(false);
            }

            // Keeps the previous snapshot's entry for a file that cannot be read now (pinned like any reused
            // chunk) so one locked file does not drop it from the snapshot; records it either way.
            void SkipFile(string absolutePath, string relativePath, string reason, bool vanished = false)
            {
                // A file that is really gone must not be resurrected from the previous snapshot.
                if (!vanished && canReusePreviousManifestEntries && TryGetPreviousEntry(previousFilesByPath, relativePath, out var previousEntry))
                {
                    _state.PinChunks(pinToken, previousEntry.Chunks.Select(chunk => chunk.ChunkId));
                    manifestFiles.Add(previousEntry);
                    bytesProcessed += previousEntry.SizeBytes;
                    skipped.Add(new SkippedFile(absolutePath, reason, true));
                }
                else
                {
                    skipped.Add(new SkippedFile(absolutePath, reason, false));
                }

                filesProcessed++;
            }

            // Handled after the loop: never opened, so never downloaded.
            var cloudOnlyPaths = (cloudOnlyFiles ?? []).Select(file => Path.GetFullPath(file.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var absolutePath in orderedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (cloudOnlyPaths.Contains(absolutePath))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(normalizedSourceDirectory, absolutePath)
                    .Replace(Path.DirectorySeparatorChar, '/');

                progress?.Report(new BackupProgress(
                    relativePath,
                    filesProcessed,
                    progressTotalFiles,
                    bytesProcessed,
                    chunkingBytesTotal,
                    BackupPhase.Chunking));

                if (canReusePreviousManifestEntries
                    && !normalizedChangedFiles.Contains(absolutePath)
                    && TryGetPreviousEntry(previousFilesByPath, relativePath, out var existingEntry)
                    && MatchesRecordedMetadata(normalizedSourceDirectory, storageType, absolutePath, existingEntry))
                {
                    _state.PinChunks(pinToken, existingEntry.Chunks.Select(chunk => chunk.ChunkId));
                    // Incremental runs carry the entry as recorded: refreshing attributes would stat every file.
                    manifestFiles.Add(writeV3 && incremental == null ? WithCurrentFileMetadata(existingEntry, absolutePath) : existingEntry);
                    if (incremental == null)
                    {
                        filesProcessed++;
                        bytesProcessed += existingEntry.SizeBytes;
                    }

                    continue;
                }

                // Only failures reading the source file are isolated; storage/upload errors still fail the backup.
                ChunkedFileBuildResult? chunkBuild = null;
                for (var attempt = 1; chunkBuild == null; attempt++)
                {
                    try
                    {
                        chunkBuild = await chunkingService.BuildFileManifestEntryAsync(
                            absolutePath,
                            normalizedSourceDirectory,
                            UploadChunkAsync,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (FileChangedDuringBackupException) when (attempt <= MaxChangedFileRetries)
                    {
                        _logger.Log($"'{absolutePath}' changed while being read; retrying ({attempt}/{MaxChangedFileRetries})", LogLevel.Debug);
                    }
                    catch (SourceFileUnreadableException ex)
                    {
                        var reason = ex is FileChangedDuringBackupException
                            ? "Changed during backup"
                            : (ex.InnerException ?? ex).Message;
                        var vanished = ex.InnerException is FileNotFoundException or DirectoryNotFoundException
                            && !File.Exists(absolutePath);
                        SkipFile(absolutePath, relativePath, vanished ? "Deleted during backup" : reason, vanished);
                        break;
                    }
                }

                if (chunkBuild == null)
                {
                    continue;
                }

                if (writeV3)
                {
                    StampFileMetadata(chunkBuild.FileEntry, absolutePath);
                }

                manifestFiles.Add(chunkBuild.FileEntry);
                chunkedEntries.Add(chunkBuild.FileEntry);
                filesProcessed++;
                bytesProcessed += chunkBuild.FileEntry.SizeBytes;
            }

            // Cloud-only files are not downloaded: keep what the previous snapshot held, and report the rest.
            foreach (var cloudFile in cloudOnlyFiles ?? [])
            {
                var cloudPath = Path.GetFullPath(cloudFile.Path);
                var cloudRelative = Path.GetRelativePath(normalizedSourceDirectory, cloudPath).Replace(Path.DirectorySeparatorChar, '/');
                if (canReusePreviousManifestEntries
                    && TryGetPreviousEntry(previousFilesByPath, cloudRelative, out var cloudEntry))
                {
                    if (!manifestFiles.Any(file => string.Equals(file.RelativePath, cloudEntry.RelativePath, StringComparison.OrdinalIgnoreCase)))
                    {
                        _state.PinChunks(pinToken, cloudEntry.Chunks.Select(chunk => chunk.ChunkId));
                        manifestFiles.Add(cloudEntry);
                    }

                    skipped.Add(new SkippedFile(cloudPath, CloudOnlySkipReason, true));
                }
                else
                {
                    skipped.Add(new SkippedFile(cloudPath, CloudOnlySkipReason, false));
                }
            }

            // Directories that could not be listed: keep whatever the previous snapshot held under them.
            foreach (var directory in unreadableDirectories)
            {
                var directoryRelative = Path.GetRelativePath(normalizedSourceDirectory, Path.GetFullPath(directory))
                    .Replace(Path.DirectorySeparatorChar, '/');
                var prefix = directoryRelative == "." ? string.Empty : directoryRelative.TrimEnd('/') + "/";
                var present = manifestFiles.Select(file => file.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var carried = 0;

                if (canReusePreviousManifestEntries)
                {
                    foreach (var previousEntry in previousFilesByPath.Values)
                    {
                        if (!previousEntry.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            || present.Contains(previousEntry.RelativePath))
                        {
                            continue;
                        }

                        _state.PinChunks(pinToken, previousEntry.Chunks.Select(chunk => chunk.ChunkId));
                        manifestFiles.Add(previousEntry);
                        skipped.Add(new SkippedFile(
                            Path.Combine(normalizedSourceDirectory, previousEntry.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                            "Directory could not be read",
                            true));
                        carried++;
                    }
                }

                if (carried == 0)
                {
                    skipped.Add(new SkippedFile(directory, "Directory could not be read", false));
                }
            }

            progress?.Report(new BackupProgress(
                string.Empty,
                filesProcessed,
                progressTotalFiles,
                bytesProcessed,
                chunkingBytesTotal,
                BackupPhase.Chunking));

            // Phase one of the commit: every chunk must be stored before the manifest names it. Waiting here (before the
            // manifest is built) also means every entry can be corrected to what is really stored.
            await uploadQueue.CompleteAsync().ConfigureAwait(false);
            ApplyStoredChunkForms(chunkedEntries, chunkOutcomes, writeV3);

            var snapshotId = SnapshotStoragePaths.BuildSnapshotId();
            var manifest = new SnapshotManifest
            {
                Version = manifestVersion,
                SnapshotId = snapshotId,
                Group = normalizedSourceDirectory,
                CreatedUtc = DateTime.UtcNow,
                BackupMode = _config.BackupType.ToString(),
                EncryptionEnabled = _config.Encryption.Enabled,
                EncryptionSalt = encryptionSalt,
                KeyDerivationIterations = _config.Encryption.KeyDerivationIterations,
                ChunkStorageNamespace = chunkStorageNamespace,
                ChunkIdScheme = repositoryKeys != null ? ChunkHashScheme.HmacSha256Name : null,
                Profile = ChunkingProfile.FromConfig(chunkConfig, algorithmVersion),
                Files = manifestFiles,
                Directories = !writeV3 ? null
                    : incremental != null ? BuildIncrementalDirectoryEntries(normalizedSourceDirectory, previousManifest!, incremental)
                    : BuildDirectoryEntries(normalizedSourceDirectory)
            };

            manifest.RootHash = SnapshotManifestHasher.ComputeRootHash(manifest);

            uploadTelemetry.CandidateChunks = uploadedChunkIds.Count;

            // Every chunk is stored by this point. The snapshot commits in three phases —
            // chunks, then the manifest, then HEAD — so cancelling before HEAD leaves HEAD on
            // the previous snapshot and an interrupted backup is inert.
            progress?.Report(new BackupProgress(
                "manifest",
                progressTotalFiles,
                progressTotalFiles,
                chunkingBytesTotal,
                chunkingBytesTotal,
                BackupPhase.Finalising));

            var manifestPath = SnapshotStoragePaths.GetManifestPath(normalizedSourceDirectory, snapshotId);
            if (repositoryKeys != null)
            {
                await AssertKeyFileUnchangedAsync(storage, repositoryKeys, cancellationToken).ConfigureAwait(false);
            }

            await UploadManifestAsync(storage, manifestPath, manifest, repositoryKeys).ConfigureAwait(false);
            await EnsureGroupIndexAsync(storage, normalizedSourceDirectory, chunkStorageNamespace, repositoryKeys, cancellationToken).ConfigureAwait(false);

            var headPath = SnapshotStoragePaths.GetHeadPath(normalizedSourceDirectory);
            await UploadSnapshotHeadAsync(storage, headPath, manifestPath, manifest.RootHash).ConfigureAwait(false);

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
                chunkStorageNamespace,
                SnapshotChunkUsage.FromManifest(manifest));

            // HEAD already points at this snapshot. Persist it now so a crash or a failure in
            // retention or the metadata pass cannot orphan the snapshot. A failed save fails the backup.
            await _state.SaveStateAsync().ConfigureAwait(false);

            try
            {
                await _retentionManager.ApplyGroupAsync(normalizedSourceDirectory, mutationLease, cancellationToken).ConfigureAwait(false);
            }
            catch (RepositoryBusyException ex)
            {
                // The snapshot is committed and saved; only the cleanup waits (a restore in another process).
                _logger.Log($"Retention deferred: {ex.Message}", LogLevel.Warning);
            }
            _logger.Log($"Snapshot backup completed: {manifestPath}", LogLevel.Info);
            return new SnapshotBackupResult(skipped, chunkedEntries, uploadTelemetry.UploadedStoredBytes, uploadTelemetry.ReplacedChunks);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to create snapshot backup: {ex.Message}", LogLevel.Error);
            throw;
        }
        finally
        {
            _state.UnpinChunks(pinToken);
        }
    }

    /// <summary>
    /// Uploads one chunk unless the provider already has it. Called as each chunk is produced,
    /// so the payload can be released immediately afterwards.
    /// </summary>
    private async Task UploadChunkIfMissingAsync(
        IStorage storage,
        ChunkBuildPayload chunk,
        string? chunkStorageNamespace,
        ChunkUploadTelemetry telemetry,
        ChunkExistenceIndex existenceIndex,
        System.Collections.Concurrent.ConcurrentDictionary<string, StoredChunkForm> outcomes,
        bool writeV3,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var chunkPath = SnapshotStoragePaths.GetChunkPath(chunk.ChunkId, chunkStorageNamespace);
        var existingSize = await existenceIndex.GetStoredSizeAsync(chunkPath, cancellationToken).ConfigureAwait(false);
        if (existingSize is long size)
        {
            // The same id can already be stored raw (v2, an earlier run with compression off, an already-compressed file type)
            // or compressed. Reuse it as it is and record the codec it really has; never replace it with the other form,
            // because older snapshots reference it with that codec.
            string? existingCodec = null;
            if (size == chunk.StoredPayload.Length)
            {
                existingCodec = chunk.Codec;
            }
            else if (size == chunk.RawStoredSize)
            {
                existingCodec = SnapshotChunkCodecs.Raw;
            }
            else if (chunk.ComputeCompressedStoredSize?.Invoke() == size)
            {
                existingCodec = SnapshotChunkCodecs.Brotli;
            }

            if (existingCodec != null)
            {
                if (!writeV3 && SnapshotChunkCodecs.IsBrotli(existingCodec))
                {
                    throw new InvalidOperationException(
                        $"Chunk {chunk.ChunkId} is stored compressed by a v3 snapshot, which a v2 manifest cannot reference. "
                        + "Set chunkDiffing.manifestVersion to 3 (or 0 to choose automatically) to continue this group.");
                }

                outcomes[chunk.ChunkId] = new StoredChunkForm(existingCodec, (int)size);
                telemetry.RecordReused();
                return;
            }

            // (A failure to read the object is not caught: the backup fails and the object is left alone.)
            // The size matches no form we can produce (a different Brotli build, or damage). Overwriting could break older
            // snapshots that recorded the other codec, so decode the object first and only replace it when it is unusable.
            existingCodec = await IdentifyExistingChunkAsync(storage, chunk, chunkPath, cancellationToken).ConfigureAwait(false);
            if (existingCodec != null)
            {
                if (!writeV3 && SnapshotChunkCodecs.IsBrotli(existingCodec))
                {
                    throw new InvalidOperationException(
                        $"Chunk {chunk.ChunkId} is stored compressed by a v3 snapshot, which a v2 manifest cannot reference. "
                        + "Set chunkDiffing.manifestVersion to 3 (or 0 to choose automatically) to continue this group.");
                }

                outcomes[chunk.ChunkId] = new StoredChunkForm(existingCodec, (int)size);
                telemetry.RecordReused();
                return;
            }

            _logger.Log($"Stored chunk '{chunkPath}' is {size} bytes and does not decode to its content; replacing it.", LogLevel.Warning);
            telemetry.RecordReplaced();
        }

        // Streamed from memory: no temp file per chunk.
        using (var payloadStream = new MemoryStream(chunk.StoredPayload, writable: false))
        {
            await storage.UploadAsync(payloadStream, chunkPath, cancellationToken).ConfigureAwait(false);
        }

        outcomes[chunk.ChunkId] = new StoredChunkForm(chunk.Codec, chunk.StoredPayload.Length);
        telemetry.RecordUploaded(chunk.StoredPayload.Length);
        _logger.Log($"Uploaded chunk {chunk.ChunkId} to {chunkPath}", LogLevel.Debug);
    }

    private async Task<string?> IdentifyExistingChunkAsync(IStorage storage, ChunkBuildPayload chunk, string chunkPath, CancellationToken cancellationToken)
    {
        if (chunk.IdentifyStoredForm == null)
        {
            return null;
        }

        var temp = Path.Combine(Path.GetTempPath(), "restore-chunk-" + Guid.NewGuid().ToString("N"));
        try
        {
            await storage.DownloadAsync(chunkPath, temp, cancellationToken).ConfigureAwait(false);
            var stored = await File.ReadAllBytesAsync(temp, cancellationToken).ConfigureAwait(false);
            return chunk.IdentifyStoredForm(stored);
        }
        catch (StorageNotFoundException)
        {
            // Vanished since it was listed: nothing to protect, store ours.
            return null;
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record StoredChunkForm(string Codec, int StoredSize);

    /// <summary>
    /// Makes each chunk entry describe the object that is really stored (a chunk id found already stored keeps its codec).
    /// v2 manifests carry no codec, and their stored sizes can only differ from the plain form by encryption overhead.
    /// </summary>
    private static void ApplyStoredChunkForms(
        IEnumerable<SnapshotFileManifestEntry> entries,
        IReadOnlyDictionary<string, StoredChunkForm> outcomes,
        bool writeV3)
    {
        foreach (var chunk in entries.SelectMany(file => file.Chunks))
        {
            if (!outcomes.TryGetValue(chunk.ChunkId, out var form))
            {
                continue;
            }

            chunk.StoredSizeBytes = form.StoredSize;
            if (writeV3)
            {
                chunk.Codec = SnapshotChunkCodecs.Normalize(form.Codec);
            }
        }
    }

    private static bool ManifestUsesCompressedChunks(SnapshotManifest? manifest)
    {
        return manifest != null
            && manifest.Files.Any(file => file.Chunks.Any(chunk => SnapshotChunkCodecs.IsBrotli(chunk.Codec)));
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

        // Stored size of each unique chunk this snapshot references, taken from the manifest
        // so the figure is exact rather than an average-size estimate.
        var referencedStoredBytes = manifest.Files
            .SelectMany(file => file.Chunks)
            .GroupBy(chunk => chunk.ChunkId, StringComparer.OrdinalIgnoreCase)
            .Sum(group => (long)group.First().StoredSizeBytes);

        var dedupSavedBytes = Math.Max(0, referencedStoredBytes - uploadTelemetry.UploadedStoredBytes);

        _logger.Log(
            $"Chunk telemetry: group='{sourceDirectory}', snapshot='{manifest.SnapshotId}', fileCount={manifest.Files.Count}, chunkRefs={totalChunkReferences}, uniqueChunks={totalUniqueChunks}, uploadedChunks={uploadTelemetry.UploadedChunks}, reusedChunks={uniqueReusedChunks}, manifestReuseRatio={manifestReuseRatio:P2}, candidateChunks={uploadTelemetry.CandidateChunks}, storageHitChunks={uploadTelemetry.ReusedChunks}, uploadBypassRatio={uploadBypassRatio:P2}, referencedStoredBytes={referencedStoredBytes}, uploadedStoredBytes={uploadTelemetry.UploadedStoredBytes}, dedupSavedBytes={dedupSavedBytes}",
            LogLevel.Info);

        _state.RecordSnapshotBackupTelemetry(
            fileCount: manifest.Files.Count,
            chunkReferences: totalChunkReferences,
            uniqueChunks: totalUniqueChunks,
            uploadedChunks: uploadTelemetry.UploadedChunks,
            uniqueReusedChunks: uniqueReusedChunks,
            storageHitChunks: uploadTelemetry.ReusedChunks,
            candidateChunks: uploadTelemetry.CandidateChunks,
            referencedStoredBytes: referencedStoredBytes,
            uploadedStoredBytes: uploadTelemetry.UploadedStoredBytes);
    }

    /// <summary>
    /// The manifest format this snapshot is written in. An explicit <c>chunkDiffing.manifestVersion</c> wins (2 = always v2,
    /// 3 = always v3, also via the Settings "Upgrade backup format" button). When the key is absent (0) the rule is automatic:
    /// v3 for new groups and groups already on v3, v2 for existing v2 groups until the user opts in, because older app
    /// versions cannot read v3. Config migration writes 2 for existing configs, so they stay v2.
    /// </summary>
    internal int ResolveManifestVersion(ChunkDiffingConfig chunkConfig, SnapshotManifest? previousManifest, string group, string storageType)
    {
        if (chunkConfig.ManifestVersion >= SnapshotManifest.VersionV3)
        {
            return SnapshotManifest.VersionV3;
        }

        if (chunkConfig.ManifestVersion == SnapshotManifest.VersionV2)
        {
            return SnapshotManifest.VersionV2;
        }

        if (previousManifest != null)
        {
            return previousManifest.Version >= SnapshotManifest.VersionV3 ? SnapshotManifest.VersionV3 : SnapshotManifest.VersionV2;
        }

        // No readable previous manifest: still an existing repository when the state knows of an earlier snapshot.
        // A group whose last snapshot used the encrypted v3 namespace stays v3 even when its manifest cannot be loaded now
        // (damaged, or the key file did not open): writing v2 would silently downgrade it.
        var latest = _state.GetLatestSnapshotForGroup(group, storageType, _config.GlobalStorageType);
        if (latest?.ChunkStorageNamespace?.StartsWith(RepositoryKeys.NamespacePrefix, StringComparison.OrdinalIgnoreCase) == true)
        {
            return SnapshotManifest.VersionV3;
        }

        return latest != null ? SnapshotManifest.VersionV2 : SnapshotManifest.VersionV3;
    }

    private const FileAttributes RestorableAttributes =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive
        | FileAttributes.NotContentIndexed | FileAttributes.Temporary;

    private void StampFileMetadata(SnapshotFileManifestEntry entry, string absolutePath)
    {
        try
        {
            var info = new FileInfo(absolutePath);
            entry.Attributes = (int)(info.Attributes & RestorableAttributes);
            entry.CreatedUtc = info.CreationTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Log($"Could not read attributes of '{absolutePath}': {ex.Message}", LogLevel.Debug);
        }
    }

    /// <summary>A reused entry (shares its chunk list) carrying the file's current attributes and creation time.</summary>
    private SnapshotFileManifestEntry WithCurrentFileMetadata(SnapshotFileManifestEntry entry, string absolutePath)
    {
        var copy = new SnapshotFileManifestEntry
        {
            RelativePath = entry.RelativePath,
            SizeBytes = entry.SizeBytes,
            LastModifiedUtc = entry.LastModifiedUtc,
            ContentHash = entry.ContentHash,
            Chunks = entry.Chunks,
            Attributes = entry.Attributes,
            CreatedUtc = entry.CreatedUtc
        };
        StampFileMetadata(copy, absolutePath);
        return copy;
    }

    /// <summary>
    /// What a watcher flush changes relative to the previous manifest: the existing, eligible changed files (with
    /// their stat), and the absolute paths whose previous entries (and everything beneath them) must go.
    /// </summary>
    private sealed record IncrementalChange(
        string Group,
        List<ScannedFile> Changed,
        HashSet<string> RemovedPaths,
        List<string> DirectoryCandidates,
        List<ScannedFile> CloudOnly);

    /// <summary>
    /// Plans an incremental snapshot for a watcher flush, or returns null when the full pass must run: any reason the
    /// destination's snapshot cannot be trusted (no snapshot, encryption change, missing manifest), a missing directory,
    /// or a changed file that cannot be examined. Manifest-level doubts (namespace, algorithm, format) are checked
    /// by <see cref="CreateSnapshotBackupAsync"/> once the previous manifest is loaded.
    /// </summary>
    private async Task<IncrementalChange?> TryPlanIncrementalAsync(
        string baseDirectory,
        string storageType,
        IStorage storage,
        List<string> existingFiles,
        List<string> deletedPaths,
        IReadOnlyCollection<string>? previousPaths,
        CancellationToken cancellationToken)
    {
        string group;
        try
        {
            group = Path.GetFullPath(Environment.ExpandEnvironmentVariables(baseDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!Directory.Exists(group))
        {
            return null;
        }

        var reason = await GetForceFullPassReasonAsync(group, storageType, storage, cancellationToken).ConfigureAwait(false);
        if (reason != null)
        {
            _logger.Log($"Incremental snapshot not possible ({reason}); doing a full pass.", LogLevel.Info);
            return null;
        }

        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryCandidates = new List<string>();

        void Remove(string path)
        {
            try
            {
                var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
                removed.Add(full);
                if (Path.GetDirectoryName(full) is { } parent)
                {
                    directoryCandidates.Add(parent);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Cannot be a path in the manifest.
            }
        }

        foreach (var path in deletedPaths)
        {
            if (Directory.Exists(path))
            {
                // A created/changed directory: only its empty-directory record can change.
                directoryCandidates.Add(Path.GetFullPath(path));
            }
            else
            {
                Remove(path);
            }
        }

        foreach (var path in previousPaths ?? [])
        {
            Remove(path);
        }

        var changed = new List<ScannedFile>();
        var cloudOnly = new List<ScannedFile>();
        foreach (var path in existingFiles)
        {
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            if (!IsUnderRoot(group, full))
            {
                continue;
            }

            if (IsExcludedForIncremental(group, full))
            {
                // The full pass would leave it out of the snapshot, so a previous entry must go.
                Remove(full);
                continue;
            }

            try
            {
                var info = new FileInfo(full);
                var scanned = _fileSelectionService.Observe(new ScannedFile(full, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc, info.Attributes));
                if (!_fileSelectionService.HydratesCloudFiles && FileSelectionService.IsCloudOnly(scanned.Attributes))
                {
                    // Not downloaded: the previous entry (if any) is carried as it is, never removed.
                    cloudOnly.Add(scanned);
                    continue;
                }

                changed.Add(scanned);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Log($"Cannot examine '{full}' ({ex.Message}); doing a full pass.", LogLevel.Debug);
                return null;
            }
        }

        return new IncrementalChange(group, changed, removed, directoryCandidates, cloudOnly);
    }

    /// <summary>
    /// True when a reported path switched kind since the previous snapshot: a directory where the manifest has a file,
    /// or a changed file where the manifest has entries beneath that path. Only a full pass sorts that out.
    /// </summary>
    private static bool ChangesPathKind(Dictionary<string, SnapshotFileManifestEntry> previousFiles, string root, IncrementalChange change)
    {
        string Relative(string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

        foreach (var directory in change.DirectoryCandidates)
        {
            if (Directory.Exists(directory) && previousFiles.ContainsKey(Relative(directory)))
            {
                return true;
            }
        }

        var prefixes = change.Changed.Select(file => Relative(file.Path) + "/").ToList();
        return prefixes.Count > 0 && previousFiles.Keys.Any(key => prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsUnderRoot(string root, string full)
    {
        var relative = Path.GetRelativePath(root, full);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative) && relative != ".";
    }

    /// <summary>The full walk's rules for one file: its own exclusions and hidden/system directories on the way down.</summary>
    private bool IsExcludedForIncremental(string root, string full)
    {
        if (_fileSelectionService.ShouldExcludeFile(full, root))
        {
            return true;
        }

        try
        {
            for (var directory = Path.GetDirectoryName(full);
                 directory != null && IsUnderRoot(root, directory);
                 directory = Path.GetDirectoryName(directory))
            {
                if ((new DirectoryInfo(directory).Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    /// <summary>Absolute paths of the previous manifest's files that survive the change set, plus the changed files, sorted as a full pass sorts them.</summary>
    private static List<string> BuildIncrementalFileList(string root, SnapshotManifest previous, HashSet<string> changed, HashSet<string> removed)
    {
        var all = new HashSet<string>(changed, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in previous.Files)
        {
            var absolute = Path.GetFullPath(Path.Combine(root, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (changed.Contains(absolute) || !IsRemoved(root, absolute, removed))
            {
                all.Add(absolute);
            }
        }

        return [.. all.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool IsRemoved(string root, string absolute, HashSet<string> removed)
    {
        if (removed.Count == 0)
        {
            return false;
        }

        for (var path = absolute; path != null && path.Length > root.Length; path = Path.GetDirectoryName(path))
        {
            if (removed.Contains(path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Empty-directory list for an incremental snapshot: the previous list minus directories that are gone or no longer
    /// empty, plus directories the change set emptied or created. Best effort; the next full pass corrects any drift.
    /// </summary>
    private List<SnapshotDirectoryManifestEntry> BuildIncrementalDirectoryEntries(string root, SnapshotManifest previous, IncrementalChange change)
    {
        var result = new Dictionary<string, SnapshotDirectoryManifestEntry>(StringComparer.OrdinalIgnoreCase);

        bool IsEmptyDirectory(string absolute)
        {
            try
            {
                return Directory.Exists(absolute) && !Directory.EnumerateFileSystemEntries(absolute).Any();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        foreach (var directory in previous.Directories ?? [])
        {
            var absolute = Path.GetFullPath(Path.Combine(root, directory.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (IsEmptyDirectory(absolute))
            {
                result[directory.RelativePath] = directory;
            }
        }

        foreach (var candidate in change.DirectoryCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsUnderRoot(root, candidate) || !IsEmptyDirectory(candidate) || _fileSelectionService.ShouldExcludeFile(candidate))
            {
                continue;
            }

            try
            {
                var info = new DirectoryInfo(candidate);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, candidate).Replace(Path.DirectorySeparatorChar, '/');
                result[relative] = new SnapshotDirectoryManifestEntry
                {
                    RelativePath = relative,
                    Attributes = (int)(info.Attributes & RestorableAttributes),
                    LastWriteUtc = info.LastWriteTimeUtc
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort.
            }
        }

        return [.. result.Values.OrderBy(directory => directory.RelativePath, StringComparer.Ordinal)];
    }

    private List<SnapshotDirectoryManifestEntry> BuildDirectoryEntries(string sourceDirectory)
    {
        return _fileSelectionService.FindEmptyDirectories(sourceDirectory)
            .Select(directory => new SnapshotDirectoryManifestEntry
            {
                RelativePath = Path.GetRelativePath(sourceDirectory, directory.Path).Replace(Path.DirectorySeparatorChar, '/'),
                Attributes = (int)(directory.Attributes & RestorableAttributes),
                LastWriteUtc = directory.LastWriteUtc
            })
            .OrderBy(directory => directory.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Last check before anything is committed: the key file in storage must still be the one this snapshot's chunks and manifest
    /// are encrypted with. Closes (not removes) the race of two computers creating the key file on a provider without an atomic create.
    /// Stray chunks of an aborted backup are collected by repair.
    /// </summary>
    private async Task AssertKeyFileUnchangedAsync(IStorage storage, RepositoryKeys keys, CancellationToken cancellationToken)
    {
        var current = await RepositoryKeyStore.ReadAsync(storage, cancellationToken, repair: false).ConfigureAwait(false);
        if (current == null || current.KeyFileId != keys.KeyFileId)
        {
            throw new InvalidOperationException(
                "The repository key file at the destination changed while this backup was running (another computer created or replaced it). " +
                "Nothing was committed; uploaded chunks are left for 'repair'. Run the backup again.");
        }
    }

    private async Task UploadManifestAsync(IStorage storage, string manifestPath, SnapshotManifest manifest, RepositoryKeys? keys = null)
    {
        var tempManifestPath = Path.Combine(Path.GetTempPath(), $"restore_manifest_{Guid.NewGuid():N}.json");

        try
        {
            await SnapshotManifestSerializer.WriteFileAsync(tempManifestPath, manifest, keys).ConfigureAwait(false);
            await storage.UploadAsync(tempManifestPath, manifestPath).ConfigureAwait(false);
            _logger.Log($"Uploaded snapshot manifest: {manifestPath}", LogLevel.Debug);
        }
        finally
        {
            TryDeleteTemporaryFile(tempManifestPath);
        }
    }

    /// <summary>
    /// Writes the group's repository index (group.json) if missing or changed, before HEAD moves. Best effort: the
    /// index only helps a future disaster recovery, so a failure here never fails the backup.
    /// </summary>
    private async Task EnsureGroupIndexAsync(IStorage storage, string group, string? chunkStorageNamespace, RepositoryKeys? keys, CancellationToken cancellationToken)
    {
        try
        {
            var encrypted = _config.Encryption.Enabled;
            string? password = null;
            byte[]? salt = null;
            if (encrypted)
            {
                password = _passwordProvider == null ? null : await _passwordProvider.GetPasswordAsync(PasswordPurpose.Encrypt).ConfigureAwait(false);
                salt = string.IsNullOrWhiteSpace(_config.Encryption.Salt) ? null : Convert.FromBase64String(_config.Encryption.Salt);
            }

            var index = new GroupIndex
            {
                GroupPath = group,
                MachineName = Environment.MachineName,
                InstallId = _state.GetOrCreateInstallId(),
                Encrypted = encrypted,
                ChunkStorageNamespace = chunkStorageNamespace
            };

            await new GroupIndexStore(_logger, _encryptionService)
                .EnsureAsync(storage, index, password, salt, _config.Encryption.KeyDerivationIterations, cancellationToken, keys).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Log($"Could not write the repository index for '{group}': {ex.Message}", LogLevel.Warning);
        }
    }

    private async Task UploadSnapshotHeadAsync(IStorage storage, string headPath, string manifestPath, string rootHash)
    {
        var tempHeadPath = Path.Combine(Path.GetTempPath(), $"restore_head_{Guid.NewGuid():N}.txt");

        try
        {
            var content = $"{manifestPath}\n{rootHash}\n";
            await File.WriteAllTextAsync(tempHeadPath, content).ConfigureAwait(false);
            await storage.UploadAsync(tempHeadPath, headPath).ConfigureAwait(false);
            _logger.Log($"Updated snapshot head: {headPath}", LogLevel.Debug);
        }
        finally
        {
            TryDeleteTemporaryFile(tempHeadPath);
        }
    }

    private async Task<SnapshotManifest?> TryLoadLatestManifestAsync(string sourceDirectory, string storageType, IStorage storage, EncryptionContext encryptionContext)
    {
        // The newest snapshot at this destination, not the newest anywhere: another destination's manifest
        // says nothing about what this one holds.
        var previousBackupPath = _state.GetLatestSnapshotForGroup(sourceDirectory, storageType, _config.GlobalStorageType)?.Path;
        if (string.IsNullOrWhiteSpace(previousBackupPath))
        {
            return null;
        }

        var previousPath = previousBackupPath;
        string? expectedRootHashFromHead = null;
        if (previousPath.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase))
        {
            var headReference = await ResolveManifestPathFromHeadAsync(storage, previousPath).ConfigureAwait(false);
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
            await storage.DownloadAsync(previousPath, tempManifestPath).ConfigureAwait(false);
            var previousManifest = await SnapshotManifestSerializer.ReadFileAsync(tempManifestPath, encryptionContext.Keys, previousPath).ConfigureAwait(false);

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
        catch (ManifestKeyRequiredException)
        {
            // Not "unreadable": the repository key file did not open. Rebuilding would hide it (and write a v2 snapshot).
            throw new InvalidOperationException(
                encryptionContext.KeyFileError ?? "The previous snapshot is encrypted but the repository key file is not available.");
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
            await storage.DownloadAsync(headPath, tempHeadPath).ConfigureAwait(false);
            var lines = await File.ReadAllLinesAsync(tempHeadPath).ConfigureAwait(false);
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
        // Runs in finally blocks around chunk, manifest and HEAD uploads: a transient lock
        // must not mask the upload's own outcome, and a leftover temp file is inert.
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

    /// <summary>Counters updated concurrently by the upload workers (Interlocked); read after they have all finished.</summary>
    private sealed class ChunkUploadTelemetry
    {
        private int _uploadedChunks;
        private int _reusedChunks;
        private int _replacedChunks;
        private long _uploadedStoredBytes;

        public int CandidateChunks { get; set; }
        public int UploadedChunks => Volatile.Read(ref _uploadedChunks);
        public int ReusedChunks => Volatile.Read(ref _reusedChunks);

        /// <summary>Stored bytes actually transferred to the provider.</summary>
        public long UploadedStoredBytes => Interlocked.Read(ref _uploadedStoredBytes);

        /// <summary>Stored objects found damaged (truncated or undecodable) and uploaded again.</summary>
        public int ReplacedChunks => Volatile.Read(ref _replacedChunks);

        public void RecordReused() => Interlocked.Increment(ref _reusedChunks);

        public void RecordReplaced() => Interlocked.Increment(ref _replacedChunks);

        public void RecordUploaded(long storedBytes)
        {
            Interlocked.Increment(ref _uploadedChunks);
            Interlocked.Add(ref _uploadedStoredBytes, storedBytes);
        }
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
