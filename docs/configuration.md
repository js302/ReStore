# Configuration

You can configure ReStore through the GUI settings page or by editing the configuration file directly.

## Key Settings

**Watch Directories**: Folders to monitor for automatic backups, each with optional storage override

**Global Storage**: Default storage destination for paths without specific configuration

**Backup Type**: Choose between Full, Incremental, or ChunkSnapshot. ChunkSnapshot stores point-in-time manifests and deduplicated chunks.

**Backup Interval**: How often to check for changes (in hours)

**Storage Providers**: Configure Local, S3, Google Drive, GCP, Azure, Dropbox, B2, SFTP, or GitHub storage with per-path and per-component selection

**Exclusions**: File patterns and paths to skip during backup

**Size Limits**: Maximum file size and backup size thresholds

**Encryption**: Password-based AES-256-GCM encryption for secure backups

**Retention**: Automatic pruning of old backups. Always keeps at least one backup per group (the newest), even if it exceeds max age.

**Chunk Diffing**: Snapshot manifest version and chunking profile controls (`minChunkSizeKB`, `targetChunkSizeKB`, `maxChunkSizeKB`, rolling hash window, and safety limits).

## Configuration File Location

Both the GUI and CLI applications use a **unified configuration** located at:

```
%USERPROFILE%\ReStore\config.json
```

This is typically: `C:\Users\YourName\ReStore\config.json`

**First Time Setup:**

When you first launch ReStore (GUI or CLI), it will automatically:

1. Create the `%USERPROFILE%\ReStore` directory
2. Copy the latest packaged `config.example.json` into `%USERPROFILE%\ReStore\config.example.json`
3. Create `config.json` from the template (if not already present)
4. You can then configure all settings through:
   - **GUI**: Settings page with intuitive controls for all options
   - **Manual**: Edit `config.json` directly in a text editor

## Schema Versioning and Upgrades

ReStore tracks `configSchemaVersion` in `config.json`.

- On startup, ReStore detects older config schemas and applies non-destructive migrations.
- Before writing migrated config, ReStore creates a backup file under `%USERPROFILE%\ReStore\backups\` named like `config.pre-migration.<timestamp>_<guid>.json`.
- Migrations preserve existing user values and only inject missing defaults or compatibility mappings.

Current compatibility behavior includes:

- Legacy `backupType: Differential` is mapped to `ChunkSnapshot`
- Missing `chunkDiffing` blocks are injected with safe defaults
- Missing/invalid `encryption.keyDerivationIterations` is repaired to `1000000`

You can validate your final config at any time with:

```powershell
dotnet run --project ReStore.Core -- --validate-config
```

## Application Data Location

All application data is stored in a centralized user directory:

```
%USERPROFILE%\ReStore\
├── config.json              (Main configuration - shared by GUI and CLI - auto-created on first run)
├── config.example.json      (Latest packaged reference template)
├── appsettings.json         (GUI-specific settings - auto-generated)
├── backups\                 (Automatic pre-migration config backups)
└── state\
    └── system_state.json    (Backup metadata and history - auto-generated)
```

**Settings Available in GUI:**

All configuration options can be managed through the Settings page:

- **General Settings**: Theme, startup options, system tray integration
- **Storage Providers**: Local, Google Drive, AWS S3, GitHub configuration
- **Global Default Storage**: Set the default storage type for all backups
- **Watch Directories**: Add/remove folders to monitor with individual storage selection per path
- **Backup Configuration**: Type (Full/Incremental/ChunkSnapshot), interval, size limits, chunking profile

Behavior note:
`Full`: backs up all selected files.
`Incremental`: backs up files that changed since the last recorded version of those files.
`ChunkSnapshot`: records a new manifest snapshot and reuses already-uploaded content-addressed chunks.

The repository still contains an experimental `DiffManager` prototype for binary diff generation, but it is not wired into the production backup or restore flow.

- **Encryption**: Enable/disable AES-256-GCM encryption with password protection for all backups
- **System Backup**: Enable/disable system state backups with separate storage selection for programs, environment variables, and settings
- **Retention**: Configure how many backups to keep and max backup age
- **Exclusions**: File patterns and paths to exclude from backups

## Retention Policies

Retention policies automatically prune old backups using the backup history in `%USERPROFILE%\ReStore\state\system_state.json`.

Rules:

- Retention applies to all backup groups (file backups per watched directory, and system backups like programs/environment/settings).
- The newest backup in each group is always kept (so there is always at least one backup), even if it is older than `maxAgeDays`.

Example configuration:

```json
{
  "retention": {
    "enabled": true,
    "keepLastPerDirectory": 10,
    "maxAgeDays": 30
  }
}
```

- **Backup Data**: `%USERPROFILE%\ReStoreBackups` (default, configurable)

## Application Behavior Settings

**Run at Windows Startup**: Configure ReStore to automatically launch when Windows starts. This feature adds an entry to the Windows Registry (`HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`). By default, this is disabled and can be toggled from the Settings page.

**System Tray Integration**: When minimized, ReStore can run in the system tray, allowing you to keep the file watcher active in the background without cluttering your taskbar. By default, this is disabled and can be configured from the Settings page.

**CLI Access**: The `restore` command is automatically available system-wide after installation. This can be disabled from Settings → Apps → Advanced app settings → App execution aliases and toggling off `restore.exe`. Uninstalling ReStore will also remove the CLI access.
