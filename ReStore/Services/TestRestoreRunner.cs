using System.Windows;
using ReStore.Core.src.core;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;
using ReStore.Views.Windows;

namespace ReStore.Services;

/// <summary>
/// Runs a test restore (a few random files restored to a temporary folder and compared with the backup) behind the
/// shared progress window, then reports the result. Shared by the Backups and Dashboard pages.
/// </summary>
public static class TestRestoreRunner
{
    public static async Task RunAsync(
        Window? owner,
        ILogger logger,
        string description,
        string snapshotPath,
        Func<Task<IStorage>> createStorage,
        SystemState? state,
        int fileCount = SnapshotTestRestore.DefaultFileCount)
    {
        var passwordProvider = App.GlobalPasswordProvider ?? new GuiPasswordProvider();
        TestRestoreResult? result = null;

        var progressWindow = new OperationProgressWindow($"Test restore: {description}", logger);
        progressWindow.RunModal(owner, async token =>
        {
            using var storage = await createStorage();
            result = await new SnapshotTestRestore(progressWindow, storage, passwordProvider, state)
                .RunAsync(snapshotPath, fileCount, null, progressWindow.VerificationProgress, token);
        });

        if (state != null)
        {
            try
            {
                await RepositoryCoordinator.Instance.MutateStateAsync(state, "Saving test restore results", () => true);
            }
            catch (Exception ex)
            {
                logger.Log($"Could not save test restore results: {ex.Message}", LogLevel.Warning);
            }
        }

        if (progressWindow.WasCancelled)
        {
            return;
        }

        if (progressWindow.Failure != null)
        {
            MessageBox.Show($"Test restore failed: {progressWindow.Failure.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (result == null)
        {
            return;
        }

        if (result.Passed)
        {
            MessageBox.Show(
                $"Test restore passed.\n\n{result.FilesPassed} random file(s) were restored to a temporary folder and matched the backup. The temporary files have been deleted.",
                "Test Restore Passed",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var details = result.Files.Where(file => !file.Passed).Take(5)
            .Select((file, index) => $"{index + 1}. {file.RelativePath}: {file.Error}");
        var message = $"Test restore failed: {result.FilesPassed} of {result.FilesTested} file(s) passed.\n\n"
            + string.Join("\n", details)
            + (result.Error != null ? $"\n\n{result.Error}" : string.Empty);
        MessageBox.Show(message, "Test Restore Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
