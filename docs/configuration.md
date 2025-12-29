# Configuration

You can configure ReStore through the GUI settings page or by editing the configuration file directly.

## Key Settings

**Watch Directories**: Folders to monitor for automatic backups, each with optional storage override

**Global Storage**: Default storage destination for paths without specific configuration

**Backup Type**: Choose between Full, Incremental, or Differential

**Backup Interval**: How often to check for changes (in hours)

**Storage Providers**: Configure Local, S3, Google Drive, GCP, Azure, Dropbox, B2, SFTP, or GitHub storage with per-path and per-component selection

**Exclusions**: File patterns and paths to skip during backup

**Size Limits**: Maximum file size and backup size thresholds

**Encryption**: Password-based AES-256-GCM encryption for secure backups

**Retention**: Automatic pruning of old backups. Always keeps at least one backup per group (the newest), even if it exceeds max age.

## Configuration File Location

Both the GUI and CLI applications use a **unified configuration** located at:

```
%USERPROFILE%\ReStore\config.json
```

This is typically: `C:\Users\YourName\ReStore\config.json`

**First Time Setup:**

When you first launch ReStore (GUI or CLI), it will automatically:

1. Create the `%USERPROFILE%\ReStore` directory
2. Create `config.json` from the template (if not already present)
3. You can then configure all settings through:
   - **GUI**: Settings page with intuitive controls for all options
   - **Manual**: Edit `config.json` directly in a text editor

## Application Data Location

All application data is stored in a centralized user directory:

```
%USERPROFILE%\ReStore\
├── config.json              (Main configuration - shared by GUI and CLI - auto-created on first run)
├── appsettings.json         (GUI-specific settings - auto-generated)
└── state\
    └── system_state.json    (Backup metadata and history - auto-generated)
```

**Settings Available in GUI:**

All configuration options can be managed through the Settings page:

- **General Settings**: Theme, startup options, system tray integration
- **Storage Providers**: Local, Google Drive, AWS S3, GitHub configuration
- **Global Default Storage**: Set the default storage type for all backups
- **Watch Directories**: Add/remove folders to monitor with individual storage selection per path
- **Backup Configuration**: Type (Full/Incremental/Differential), interval, size limits
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

**System Tray Integration**: When minimized, ReStore can run in the system tray, allowing you to keep the file watcher active in the background without cluttering your taskbar.

**CLI Access**: The `restore` command is automatically available system-wide after installation.
