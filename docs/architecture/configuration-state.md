# Configuration & State Files

## Overview

ReStore uses several JSON files for configuration and state persistence. All user data is stored under `%USERPROFILE%\ReStore\`.

## File Locations

| File                | Location                       | Purpose                             |
| ------------------- | ------------------------------ | ----------------------------------- |
| `config.json`       | `%USERPROFILE%\ReStore\`       | Application configuration           |
| `system_state.json` | `%USERPROFILE%\ReStore\state\` | Backup history and file metadata    |
| `appsettings.json`  | Application directory          | GUI-specific settings               |
| `theme.json`        | Application directory          | Theme preferences                   |
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
    "manifestVersion": 2,
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
      "path": "./backups",
      "options": {
        "accessKeyId": "...",
        "secretAccessKey": "...",
        "region": "...",
        "bucketName": "..."
      }
    },
    "azure": {
      "path": "backups",
      "options": {
        "connectionString": "...",
        "containerName": "..."
      }
    },
    "gcp": {
      "path": "backups",
      "options": {
        "bucketName": "...",
        "credentialPath": "..."
      }
    },
    "gdrive": {
      "path": "backups",
      "options": {
        "credentialPath": "..."
      }
    },
    "dropbox": {
      "path": "backups",
      "options": {
        "accessToken": "..."
      }
    },
    "b2": {
      "path": "backups",
      "options": {
        "keyId": "...",
        "applicationKey": "...",
        "bucketName": "..."
      }
    },
    "sftp": {
      "path": "/backups",
      "options": {
        "host": "...",
        "port": "22",
        "username": "...",
        "password": "..."
      }
    },
    "github": {
      "path": "backups",
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
| `backupInterval`   | How often automatic backups run (TimeSpan format)           |
| `backupType`       | `Full`, `Incremental`, or `ChunkSnapshot`                   |
| `chunkDiffing`     | Manifest version, chunk profile, and chunking safety limits |
| `excludedPatterns` | Glob patterns to exclude (e.g., `*.tmp`)                    |
| `excludedPaths`    | Absolute paths to exclude                                   |
| `encryption`       | Encryption settings and master salt                         |
| `retention`        | Automatic backup pruning settings                           |
| `storageSources`   | Storage provider configurations                             |
| `systemBackup`     | System backup settings (programs, env vars, registry)       |

## system_state.json (Runtime State)

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
    "s3|<chunk-id>": 2
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
| `chunkReferenceCounts` | Map of storage+chunk key → reference count         |
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
  "minimizeToTray": true
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
3. Creates `state\` subdirectory for `system_state.json`

## Best Practices

1. **Never hardcode paths**: Always use `ConfigInitializer.GetUserConfigDirectory()`
2. **Expand environment variables**: Use `Environment.ExpandEnvironmentVariables()` when reading paths
3. **Validate after changes**: Call `configManager.ValidateConfiguration()` after config updates
4. **Save state after operations**: Always call `await systemState.SaveStateAsync()` after backup/restore
