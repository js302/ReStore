# CLI Usage

The `restore` command is automatically available system-wide after installing ReStore through the MSIX package.

**To disable the CLI** (if needed):

1. Open Windows **Settings** → **Apps** → **Advanced app settings** → **App execution aliases**
2. Find **restore.exe** in the list
3. Toggle it **Off**

## Start File Watcher

Monitor directories for changes and backup automatically:

```bash
restore --service
```

## Manual Backup

Backup a specific directory (uses configured storage from config.json):

```bash
restore backup "C:\Users\YourName\Documents"

# Or override storage type
restore backup "C:\Users\YourName\Documents" --storage gdrive
```

## Restore Files

Restore from a backup:

```bash
restore restore "backups/Documents/backup_Documents_20250817120000.zip" "C:\Restore\Documents"
```

## System Backup

Backup installed programs, environment variables, and Windows settings:

```bash
# Backup all components (uses configured storage per component)
restore system-backup all

# Backup specific components
restore system-backup programs
restore system-backup environment
restore system-backup settings

# Override storage for specific backup
restore system-backup programs --storage github
```

## System Restore

Restore system components:

```bash
restore system-restore "system_backups/programs/programs_backup_<timestamp>.zip" programs
restore system-restore "system_backups/environment/env_backup_<timestamp>.zip" environment
restore system-restore "system_backups/settings/settings_backup_<timestamp>.zip" settings
```

## CLI Usage with Encryption

When encryption is enabled, the CLI will prompt for your password during restore operations:

```bash
# Restore encrypted file backup (will prompt for password)
restore restore "backups/Documents/backup_Documents_20250817120000.zip.enc" "C:\Restore\Documents"

# Restore encrypted system backups (will prompt for password)
restore system-restore "system_backups/programs/programs_backup_20250817.zip.enc" programs
restore system-restore "system_backups/environment/env_backup_20250817.zip.enc" environment
restore system-restore "system_backups/settings/settings_backup_20250817.zip.enc" settings
```
