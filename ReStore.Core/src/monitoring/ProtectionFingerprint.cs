using System.Text.Json;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.monitoring;

/// <summary>
/// Summarises the configuration the watcher and scheduler bind to at start (watch directories, intervals,
/// storage, encryption, auto-start). Two equal fingerprints mean a restart would change nothing, so saves
/// of unrelated settings (exclusions, retention, notifications, ...) do not interrupt running backups.
/// </summary>
public static class ProtectionFingerprint
{
    public static string Compute(IConfigManager config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var snapshot = new
        {
            watch = config.WatchDirectories.Select(w => new { w.Path, w.StorageType, w.Schedule }),
            config.BackupInterval,
            config.GlobalStorageType,
            storage = config.StorageSources
                .OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
                .Select(s => new
                {
                    s.Key,
                    s.Value.Path,
                    options = (s.Value.Options ?? []).OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => new { o.Key, o.Value })
                }),
            encryption = new
            {
                config.Encryption.Enabled,
                config.Encryption.Salt,
                config.Encryption.KeyDerivationIterations,
                config.Encryption.VerificationToken
            },
            system = new
            {
                config.SystemBackup.Enabled,
                config.SystemBackup.BackupInterval,
                config.SystemBackup.Schedule,
                config.SystemBackup.IncludePrograms,
                config.SystemBackup.IncludeEnvironmentVariables,
                config.SystemBackup.IncludeWindowsSettings,
                config.SystemBackup.IncludeDevTools,
                config.SystemBackup.IncludeSshConfig
            },
            verification = new { config.Verification.Enabled, config.Verification.VerifyInterval },
            window = config.BackupWindow is { } window ? new { window.Enabled, window.Start, window.End, window.Days } : null,
            autoStart = config.Protection.AutoStart
        };

        return JsonSerializer.Serialize(snapshot);
    }
}
