# Configuration & State Files

## Overview

ReStore uses several JSON files for configuration and state persistence. All user data is stored under `%USERPROFILE%\ReStore\`.

## File Locations

| File                | Location                       | Purpose                             |
| ------------------- | ------------------------------ | ----------------------------------- |
| `config.json`       | `%USERPROFILE%\ReStore\`       | Application configuration           |
| `system_state.json` | `%USERPROFILE%\ReStore\state\` | Backup history and file metadata    |
| `appsettings.json`  | `%USERPROFILE%\ReStore\`       | GUI-specific settings               |
| `theme.json`        | `%LOCALAPPDATA%\ReStore\`      | Theme preferences                   |
| `*.manifest.json`   | Remote storage                 | User-file snapshot manifest         |
| `HEAD`              | Remote storage                 | Pointer to latest snapshot manifest |
| `chunks/*/*.chunk`  | Remote storage                 | Deduplicated chunk objects          |
| `*.enc.meta`        | Remote storage                 | System archive encryption metadata  |

## config.json (Application Config)

Main configuration file for backup settings, storage providers, and encryption.

```json
{
  "watchDirectories": [
    {
      "path": "C:\\Users\\...",
      "storageType": "gdrive"
    }
  ],
  "globalStorageType": "local",
  "backupInterval": "01:00:00",
  "sizeThresholdMB": 500,
  "maxFileSizeMB": 100,
  "backupType": "ChunkSnapshot",
  "chunkDiffing": {
    "manifestVersion": 3,
    "compression": "auto",
    "minChunkSizeKB": 32,
    "targetChunkSizeKB": 128,
    "maxChunkSizeKB": 512,
    "rollingHashWindowSize": 64,
    "maxChunksPerFile": 200000,
    "maxFilesPerSnapshot": 200000
  },
  "excludedPatterns": ["*.tmp", "*.log"],
  "excludedPaths": ["C:\\Temp"],
  "encryption": {
    "enabled": false,
    "salt": null,
    "verificationToken": null,
    "keyDerivationIterations": 1000000
  },
  "retention": {
    "enabled": false,
    "keepLastPerDirectory": 10,
    "maxAgeDays": 30
  },
  "storageSources": {
    "local": {
      "path": "./backups",
      "options": {}
    },
    "s3": {
      "options": {
        "accessKeyId": "...",
        "secretAccessKey": "...",
        "region": "...",
        "bucketName": "..."
      }
    },
    "azure": {
      "options": {
        "connectionString": "...",
        "containerName": "..."
      }
    },
    "gcp": {
      "options": {
        "bucketName": "...",
        "credentialPath": "..."
      }
    },
    "gdrive": {
      "options": {
        "client_id": "...",
        "client_secret": "...",
        "token_folder": "...",
        "backup_folder_name": "ReStoreBackups"
      }
    },
    "dropbox": {
      "options": {
        "accessToken": "..."
      }
    },
    "b2": {
      "options": {
        "keyId": "...",
        "applicationKey": "...",
        "serviceUrl": "https://s3.<region>.backblazeb2.com",
        "bucketName": "..."
      }
    },
    "sftp": {
      "options": {
        "host": "...",
        "port": "22",
        "username": "...",
        "password": "..."
      }
    },
    "github": {
      "options": {
        "token": "...",
        "owner": "...",
        "repo": "..."
      }
    }
  },
  "systemBackup": {
    "enabled": true,
    "includePrograms": true,
    "includeEnvironmentVariables": true,
    "includeWindowsSettings": true,
    "backupInterval": "24:00:00",
    "excludeSystemPrograms": [],
    "storageType": null,
    "programsStorageType": null,
    "environmentStorageType": null,
    "settingsStorageType": null
  }
}
```

### Configuration Sections

| Section            | Description                                                 |
| ------------------ | ----------------------------------------------------------- |
| `watchDirectories` | Directories to monitor for file changes                     |
| `backupInterval`   | How often each watched directory is backed up (TimeSpan format) |
| `backupType`       | `Full`, `Incremental`, or `ChunkSnapshot`                   |
| `chunkDiffing`     | Manifest version, chunk profile, and chunking safety limits |
| `excludedPatterns` | Glob patterns to exclude (e.g., `*.tmp`)                    |
| `excludedPaths`    | Absolute paths to exclude                                   |
| `encryption`       | Encryption settings and master salt                         |
| `retention`        | Automatic backup pruning settings                           |
| `storageSources`   | Storage provider configurations                             |
| `systemBackup`     | System backup settings (programs, env vars, registry)       |
| `protection`       | `autoStart` (default `true`): start the file watcher and scheduler at launch when at least one watch directory exists |
| `logging`          | `level` (`Debug`/`Info`/`Warning`/`Error`, default `Info`): minimum log level (see [Configuration](../configuration.md#logging)) |
| `verification`     | Scheduled integrity checks (see [Configuration](../configuration.md#scheduled-verification)) |
| `notifications`    | Tray notification toggles (see [Configuration](../configuration.md#notifications)) |
| `watcher`          | `minSnapshotInterval` (`d.hh:mm:ss`, default 15 min): minimum time between watcher snapshots of a folder; changes inside it are kept and flushed later. Zero disables the minimum. `maxDebounceWait` (default 5 min): the 10 s change debounce restarts on every event, so this caps how long continuous changes can postpone a backup. Zero disables the cap |

### Scheduling

`backupInterval` and `systemBackup.backupInterval` are driven by `BackupScheduler`, which
runs whenever the file watcher is running (the GUI's **Start Watcher**, or
`ReStore.Core.exe --service`).

The scheduler complements the watcher rather than duplicating it:

- The **watcher** is event-driven and reacts to changes as they happen, debounced by a
  10-second buffer (capped by `watcher.maxDebounceWait`). Failed or busy batches are re-queued
  with a backoff, and a watcher buffer overflow/error triggers a full backup of that folder.
- The **scheduler** sweeps each watched directory on its interval, so changes made while
  the machine was asleep or the app was closed are still captured.

Due times are computed from the **later of the last recorded backup and the last attempted sweep** of that group (so a failing group does not re-run every tick), not from when the
scheduler started. Restarting does not reset the clock, and a machine that was off past
its due time backs up shortly after the next launch rather than waiting another full
interval. The scheduler re-evaluates once a minute, and intervals below one minute are
clamped to one minute; a `backupInterval` of zero or less disables only the interval *file*
backups. System backup and verification have their own settings and keep running; the
scheduler only stays stopped when all three are disabled.

After a failed scheduled backup (user files or system) the retry comes after an exponential
backoff (5 min, 10 min, 20 min, ... never beyond the interval) rather than a full interval.
Scheduled verification records its last attempt per group in `system_state.json`
(`SnapshotVerificationTimes`), so restarting the app does not trigger a verify; a verification
that errors (e.g. unreachable provider) is retried after one hour instead of a full verify interval.

Sweeps never overlap — if a backup is still running when the next tick fires, that tick is
skipped. A failure on one directory is logged and does not stop the others.

`systemBackup.backupInterval` schedules the system backup (programs, environment
variables, Windows settings) on Windows only, and only when `systemBackup.enabled` is true
and at least one component is included.

## system_state.json (Runtime State)

Snapshot chunk-id lists are not stored in `system_state.json`. Each snapshot's list lives in a binary sidecar `state\snapshots\<snapshotId>.chunks` (concatenated 32-byte SHA-256 ids; non-SHA-256 ids use `<snapshotId>.chunklist`, length-prefixed), written once when the snapshot is first saved and deleted after the save that removes the snapshot. If a sidecar is missing, retention and delete re-read the manifest from storage. The state file is compact (not indented); older files with inline `ChunkIds` are migrated on load.

File metadata for change detection is stored per destination, in `FileMetadataByGroup`: group directory, then storage type, then path relative to the group (forward slashes) to `{Size, LastModified, Hash}`. The values are the size, modification time and SHA-256 of the bytes the snapshot actually holds (taken from the manifest entries, never re-read afterwards), so a file that changed while it was being backed up is picked up on the next run. Metadata is only changed after the snapshot commits. Before diffing, a backup treats every file as changed when the destination has no snapshot of the group, the last snapshot was made with a different encryption setting or key, or its manifest is missing from the destination.

- **Migration:** state files from v1.6/v1.7 keep one flat `FileMetadata` dictionary keyed by absolute path. On load those entries are moved into `FileMetadataByGroup` under the group that contains them and the storage type of that group's newest snapshot (the destination the old record described). Entries under a folder with no snapshot are dropped (its first backup is a full pass anyway). The flat `FileMetadata` property is never written again.
- **Downgrading to v1.7:** v1.7 ignores `FileMetadataByGroup` and loads no file metadata, so its next run backs up every file once. Nothing fails and nothing is lost.

Notes:

- **Downgrading to v1.7** after this change is safe but wasteful: the old version ignores the sidecars and sees snapshots with no chunk list, so retention there will not delete those snapshots' chunks (they leak); nothing is lost.
- **Restoring an older `.bak` state** may reference snapshots whose sidecars were already deleted. They are read from the manifest instead (retention does this automatically).
- If the reference counts have to be rebuilt and a sidecar is missing, the counts are flagged incomplete (persisted as `ChunkReferenceCountsIncomplete`, logged as an error). While flagged, retention deletes only manifests and history, never chunks; the next retention pass repairs the counts from the manifests when storage is reachable. Sidecars with no matching history entry (and older than an hour) are removed during a rebuild.

Tracks backup history and file metadata for change detection.

```json
{
    "lastBackupTime": "2026-01-25T10:30:00Z",
    "backupHistory": {
        "C:\\Users\\...\\Documents": [
            {
        "path": "snapshots/documents_abcd1234ef567890/snapshot_20260125103000_abcdef.manifest.json",
                "timestamp": "2026-01-25T10:30:00Z",
                "isDiff": false,
        "storageType": "s3",
        "sizeBytes": 4096,
        "artifactType": "SnapshotManifest",
        "snapshotId": "snapshot_20260125103000_abcdef",
        "manifestPath": "snapshots/documents_abcd1234ef567890/snapshot_20260125103000_abcdef.manifest.json",
        "chunkIds": ["<sha256>", "<sha256>"],
        "rootHash": "<sha256>",
        "encrypted": true
            }
        ],
        "system_programs": [...],
        "system_environment": [...],
        "system_settings": [...]
    },
  "chunkReferenceCounts": {
    "s3|legacy|<chunk-id>": 2
  },
  "telemetry": {
    "backup": {
      "snapshotCount": 12,
      "fileCount": 482,
      "chunkReferences": 1900,
      "uniqueChunks": 1302,
      "uploadedChunks": 274,
      "uniqueReusedChunks": 1028,
      "storageHitChunks": 950,
      "candidateChunks": 1224
    },
    "restore": {
      "attemptCount": 8,
      "successCount": 7,
      "validationFailureCount": 1,
      "failureCategoryCounts": {
        "chunk-validation-failure": 1
      }
    },
    "verification": {
      "runCount": 5,
      "successCount": 4,
      "validationFailureCount": 1
    }
  },
    "fileMetadata": {
        "C:\\Users\\...\\Documents\\file.txt": {
            "size": 1024,
            "lastModified": "2026-01-20T08:00:00Z",
            "hash": "abc123..."
        }
    }
}
```

### State Sections

| Section                | Description                                        |
| ---------------------- | -------------------------------------------------- |
| `lastBackupTime`       | Timestamp of last backup operation                 |
| `backupHistory`        | Map of directory → list of backups                 |
| `chunkReferenceCounts` | Map of `storage\|namespace\|chunk-id` → reference count (missing namespace is stored as `legacy`, missing storage type as `default`) |
| `telemetry`            | Aggregated backup/restore/verify counters          |
| `fileMetadata`         | Map of file path → metadata (size, hash, modified) |

### Backup Groups

The `backupHistory` contains both file backup groups and system backup groups:

- **File backups**: Keyed by absolute directory path (e.g., `C:\Users\...\Documents`)
- **System backups**: Special keys `system_programs`, `system_environment`, `system_settings`

## appsettings.json (GUI Settings)

GUI-specific settings that don't affect core backup functionality.

```json
{
  "defaultStorage": "local",
  "showOnlyConfiguredProviders": true,
  "minimizeToTray": true,
  "contextMenuEnabled": false
}
```

## theme.json (Theme Preferences)

User's theme preference.

```json
{
  "preference": "System"
}
```

Valid values: `System`, `Light`, `Dark`

## Snapshot Manifest Artifacts

User-file backups rely on two artifact types:

- Snapshot manifest files (`*.manifest.json`) containing file/chunk mapping and root hash
- Chunk object files (`chunks/*/*.chunk`) containing deduplicated payload data

The latest snapshot in a group is discovered through the group's `HEAD` pointer.

## Encryption Metadata (.enc.meta)

Per-backup encryption metadata stored alongside encrypted system archive backups.

```json
{
  "Salt": "<base64-encoded-32-bytes>",
  "IV": "<base64-encoded-12-bytes>",
  "EncryptedDEK": "<base64-encoded>",
  "Algorithm": "AES-256-GCM",
  "Version": 1,
  "KeyDerivationIterations": 1000000
}
```

## Initialization Flow

On first launch, `ConfigInitializer.EnsureConfigurationSetup()` performs:

1. Creates `%USERPROFILE%\ReStore` directory
2. Copies `config\config.example.json` to `%USERPROFILE%\ReStore\config.json`
3. Copies the template to `%USERPROFILE%\ReStore\config.example.json` as a reference copy
4. Creates `state\` subdirectory for `system_state.json` (and `backups\` when a migration needs to save a pre-migration copy)

## Best Practices

1. **Never hardcode paths**: Always use `ConfigInitializer.GetUserConfigDirectory()`
2. **Expand environment variables**: Use `Environment.ExpandEnvironmentVariables()` when reading paths
3. **Validate after changes**: Call `configManager.ValidateConfiguration()` after config updates
4. **Save state after operations**: Always call `await systemState.SaveStateAsync()` after backup/restore
