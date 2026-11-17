using System.Windows;
using ReStore.Core.src.core;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;
using ReStore.Views.Windows;

namespace ReStore.Services;

/// <summary>One snapshot of the same backup group, offered as a comparison target.</summary>
public sealed record SnapshotChoice(string ManifestPath, DateTime Timestamp, string? Label);

/// <summary>The snapshot the browser was opened on.</summary>
/// <param name="Group">The backed-up folder as this PC's history records it (an adopted, remapped snapshot differs from the manifest's).</param>
public sealed record SnapshotBrowserSource(
    string Directory,
    string Group,
    string ManifestPath,
    string StorageType,
    DateTime Timestamp,
    IReadOnlyList<SnapshotChoice> OtherSnapshots);

/// <summary>
/// What the snapshot browser windows need from the page that opened them: storage access and the one shared
/// restore flow (preview, conflict-policy dialog, progress window), so browsing a snapshot restores exactly as
/// the Backups page does.
/// </summary>
public sealed class SnapshotBrowserHost(
    ILogger logger,
    Func<string?, Task<IStorage>> createStorage,
    SystemState? state,
    Func<Task> saveState)
{
    public ILogger Logger { get; } = logger;

    public Task<IStorage> CreateStorageAsync(string? storageType) => createStorage(storageType);

    /// <summary>
    /// Restores <paramref name="relativePaths"/> (manifest form) of one snapshot into <paramref name="targetDirectory"/>.
    /// Returns true when the restore ran to completion; false when the user cancelled or it failed (already reported).
    /// </summary>
    public async Task<bool> RestoreAsync(
        Window owner,
        string title,
        string manifestPath,
        string storageType,
        string targetDirectory,
        IReadOnlySet<string> relativePaths)
    {
        if (relativePaths.Count == 0)
        {
            System.Windows.MessageBox.Show(owner, "Nothing to restore: the selection contains no files.", "Restore", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var passwordProvider = App.GlobalPasswordProvider ?? new GuiPasswordProvider();

        try
        {
            // Preview first so the user sees which existing files are at risk before anything is written.
            RestorePreview preview;
            using (var previewStorage = await CreateStorageAsync(storageType))
            {
                preview = await new Restore(Logger, previewStorage, passwordProvider, state)
                    .PreviewRestoreAsync(manifestPath, targetDirectory, relativePaths);
            }

            if (preview.FileCount == 0)
            {
                System.Windows.MessageBox.Show(owner, "None of the selected files are in this snapshot.", "Restore", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            var confirm = new RestoreConfirmWindow(preview) { Owner = owner };
            if (confirm.ShowDialog() != true || !confirm.Confirmed)
            {
                return false;
            }

            // The preview was already limited to the requested files, so "everything selected" (null) means
            // those files, never the whole snapshot.
            var options = new RestoreOptions
            {
                ConflictPolicy = confirm.ConflictPolicy,
                RelativePaths = confirm.SelectedRelativePaths ?? relativePaths
            };

            var progressWindow = new OperationProgressWindow(title, Logger);
            RestoreOutcome? outcome = null;
            progressWindow.RunModal(owner, async token =>
            {
                using var storage = await CreateStorageAsync(storageType);
                var restore = new Restore(progressWindow, storage, passwordProvider, state);
                outcome = await restore.RestoreFromBackupAsync(manifestPath, targetDirectory, options, progressWindow.RestoreProgress, token);
                progressWindow.Log(
                    $"Restored {outcome.FilesRestored} file(s); skipped {outcome.FilesSkipped}, kept both {outcome.FilesKeptBoth}, overwrote {outcome.FilesOverwritten}.",
                    LogLevel.Info);
            });

            await saveState();

            if (progressWindow.Succeeded && outcome != null)
            {
                System.Windows.MessageBox.Show(
                    owner,
                    $"Restored {outcome.FilesRestored} file(s) to {targetDirectory}.\n"
                    + $"Skipped {outcome.FilesSkipped}, kept both {outcome.FilesKeptBoth}, overwrote {outcome.FilesOverwritten}.",
                    "Restore completed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return true;
            }

            if (progressWindow.Failure != null)
            {
                System.Windows.MessageBox.Show(owner, $"Restore failed: {progressWindow.Failure.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await saveState();
            System.Windows.MessageBox.Show(owner, $"Restore failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }
}
