using ReStore.Core.src.storage;
using ReStore.Core.src.utils;
using System.Security.Cryptography;

namespace ReStore.Core.src.core;

public sealed class TestRestoreFileResult
{
    public string RelativePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public bool Passed { get; set; }
    public string? Error { get; set; }
}

public sealed class TestRestoreResult
{
    public string RequestedBackupPath { get; set; } = string.Empty;
    public string ResolvedManifestPath { get; set; } = string.Empty;
    public string SnapshotId { get; set; } = string.Empty;

    /// <summary>Seed of the random file selection; passing it again selects the same files.</summary>
    public int Seed { get; set; }

    public int FilesInSnapshot { get; set; }
    public List<TestRestoreFileResult> Files { get; } = [];
    public long BytesRestored { get; set; }

    /// <summary>Set when the test could not run at all (for example a wrong password stopped it).</summary>
    public string? Error { get; set; }

    /// <summary>The storage's "not found" when the snapshot (manifest or HEAD) itself does not exist; <see cref="Error"/> is set too.</summary>
    public StorageNotFoundException? Missing { get; set; }

    public int FilesTested => Files.Count;
    public int FilesPassed => Files.Count(file => file.Passed);
    public int FilesFailed => Files.Count(file => !file.Passed);

    /// <summary>True when at least one file was tested and every tested file restored and matched its manifest hash.</summary>
    public bool Passed => Error == null && Files.Count > 0 && FilesFailed == 0;
}

/// <summary>
/// Proves a snapshot can really be restored: restores a seeded random sample of its files into a temporary directory
/// (each through the normal <see cref="Restore"/> path, which checks the file's hash against the manifest), checks the
/// result is present with the manifest size, and deletes the temporary directory. The user's folders are never written.
/// </summary>
public sealed class SnapshotTestRestore(
    ILogger logger,
    IStorage storage,
    IPasswordProvider? passwordProvider = null,
    SystemState? systemState = null,
    RepositoryCoordinator? coordinator = null,
    string? tempRoot = null)
{
    public const int DefaultFileCount = 5;

    private readonly string _tempRoot = tempRoot ?? Path.GetTempPath();

    /// <param name="backupPath">Snapshot manifest path or a <c>HEAD</c> reference.</param>
    /// <param name="fileCount">How many files to restore (clamped to the number the snapshot holds).</param>
    /// <param name="seed">Seed of the random file selection; null picks one (reported in the result).</param>
    public async Task<TestRestoreResult> RunAsync(
        string backupPath,
        int fileCount = DefaultFileCount,
        int? seed = null,
        IProgress<VerificationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            throw new ArgumentException("Backup path cannot be null or empty", nameof(backupPath));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(fileCount, 1);

        using var activity = ActivityRecorder.Begin(systemState, ActivityType.Verify, $"Test restore: {backupPath}", cancellationToken);
        var result = new TestRestoreResult { RequestedBackupPath = backupPath, Seed = seed ?? Random.Shared.Next() };
        var tempDirectory = Path.Combine(_tempRoot, $"restore_testrestore_{Guid.NewGuid():N}");

        try
        {
            // No SystemState: the individual restores must not add restore telemetry or activity entries of their own.
            var restore = new Restore(logger, storage, passwordProvider, systemState: null, coordinator, _tempRoot);

            RestorePreview preview;
            try
            {
                preview = await restore.PreviewRestoreAsync(backupPath, tempDirectory, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Error = $"Could not read the snapshot: {ex.Message}";
                result.Missing = ex as StorageNotFoundException ?? ex.InnerException as StorageNotFoundException;
                activity.Failed(result.Error);
                return result;
            }

            result.ResolvedManifestPath = preview.ResolvedManifestPath;
            result.SnapshotId = preview.SnapshotId;
            result.FilesInSnapshot = preview.Entries.Count;

            // Files with content first: an empty file proves nothing about chunk storage.
            var pool = preview.Entries.Where(entry => entry.SizeBytes > 0).ToList();
            if (pool.Count == 0)
            {
                pool = [.. preview.Entries];
            }

            if (pool.Count == 0)
            {
                result.Error = "The snapshot contains no files to test.";
                activity.Warning(result.Error);
                return result;
            }

            pool = [.. pool.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)];
            var selected = Math.Min(fileCount, pool.Count);
            var random = new Random(result.Seed);
            for (var i = 0; i < selected; i++)
            {
                var j = random.Next(i, pool.Count);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }

            Directory.CreateDirectory(tempDirectory);

            for (var i = 0; i < selected; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = pool[i];
                progress?.Report(new VerificationProgress(entry.RelativePath, i, selected, "files"));

                var fileResult = new TestRestoreFileResult { RelativePath = entry.RelativePath, SizeBytes = entry.SizeBytes };
                result.Files.Add(fileResult);

                var fileTarget = Path.Combine(tempDirectory, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                try
                {
                    var outcome = await restore.RestoreFromBackupAsync(
                        backupPath,
                        fileTarget,
                        new RestoreOptions
                        {
                            ConflictPolicy = RestoreConflictPolicy.Overwrite,
                            RelativePaths = new HashSet<string>([entry.RelativePath], StringComparer.OrdinalIgnoreCase)
                        },
                        null,
                        cancellationToken).ConfigureAwait(false);

                    // Restore compares the rebuilt file's hash with the manifest and throws on a mismatch; the size check
                    // below catches a file that was not written at all.
                    var restoredPath = Path.Combine(fileTarget, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (outcome.FilesRestored != 1 || !File.Exists(restoredPath))
                    {
                        fileResult.Error = "The file was not restored.";
                    }
                    else if (new FileInfo(restoredPath).Length != entry.SizeBytes)
                    {
                        fileResult.Error = $"Restored size {new FileInfo(restoredPath).Length} does not match the manifest size {entry.SizeBytes}.";
                    }
                    else
                    {
                        fileResult.Passed = true;
                        result.BytesRestored += entry.SizeBytes;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (CryptographicException ex)
                {
                    // Wrong password or an authentication failure: further files would only prompt again.
                    fileResult.Error = $"Decryption failed (wrong password or corrupted data): {ex.Message}";
                    result.Error = "Stopped after a decryption failure.";
                    break;
                }
                catch (Exception ex)
                {
                    fileResult.Error = ex.Message;
                }
                finally
                {
                    TryDelete(fileTarget);
                }
            }

            progress?.Report(new VerificationProgress(string.Empty, result.FilesTested, selected, "files"));

            var summary = $"{result.FilesPassed} of {result.FilesTested} sampled file(s) restored and matched (seed {result.Seed})";
            logger.Log($"Test restore of '{backupPath}': {summary}.", result.Passed ? LogLevel.Info : LogLevel.Warning);
            if (result.Passed)
            {
                activity.Succeeded($"Test restore: {summary}");
            }
            else
            {
                activity.Failed($"Test restore: {summary}. {result.Files.FirstOrDefault(f => !f.Passed)?.Error ?? result.Error}");
            }

            return result;
        }
        finally
        {
            TryDelete(tempDirectory);
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort: it is a temp directory, and a transient lock must not change the test's verdict.
        }
    }
}
