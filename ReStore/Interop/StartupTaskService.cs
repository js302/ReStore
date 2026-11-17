using ReStore.Core.src.utils;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;

namespace ReStore.Interop
{
    /// <summary>The MSIX startup task ("ReStoreStartup" in Package.appxmanifest); only usable with package identity.</summary>
    public static class StartupTaskService
    {
        public const string TaskId = "ReStoreStartup";

        public static async Task<StartupTaskStatus> GetStatusAsync()
        {
            var task = await StartupTask.GetAsync(TaskId);
            return Map(task.State);
        }

        /// <summary>Enables or disables the task and returns the resulting state (the user may decline the enable prompt).</summary>
        public static async Task<StartupTaskStatus> SetEnabledAsync(bool enabled)
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (enabled)
            {
                return Map(await task.RequestEnableAsync());
            }

            task.Disable();
            return Map(task.State);
        }

        /// <summary>
        /// True when Windows launched this process for the startup task. Packaged startup tasks carry no
        /// command line, so the activation kind is read from <see cref="AppInstance.GetActivatedEventArgs"/>;
        /// any failure is treated as a normal launch.
        /// </summary>
        public static bool WasActivatedByStartupTask()
        {
            if (!PackageInfo.IsPackaged)
            {
                return false;
            }

            try
            {
                return AppInstance.GetActivatedEventArgs()?.Kind == ActivationKind.StartupTask;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Could not read activation kind: {ex.Message}");
                return false;
            }
        }

        private static StartupTaskStatus Map(StartupTaskState state) => state switch
        {
            StartupTaskState.Enabled => StartupTaskStatus.Enabled,
            StartupTaskState.Disabled => StartupTaskStatus.Disabled,
            StartupTaskState.DisabledByUser => StartupTaskStatus.DisabledByUser,
            StartupTaskState.DisabledByPolicy => StartupTaskStatus.DisabledByPolicy,
            StartupTaskState.EnabledByPolicy => StartupTaskStatus.EnabledByPolicy,
            _ => StartupTaskStatus.DisabledByPolicy
        };
    }
}
