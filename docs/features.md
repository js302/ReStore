# Features

## Graphical User Interface

- **First-Run Wizard**: Three-step setup on first launch — folders to protect, destination with a live connection test, and optional encryption. No hand-editing of JSON required.
- **Modern Dashboard**: Real-time statistics, backup history, and quick actions
- **Backup Health Panel**: Dedup savings, integrity-check pass rate, restore reliability, per-folder staleness, and a "needs attention" list
- **Progress and Cancellation**: Backups, restores, and verifications report live per-file and per-byte progress and can be cancelled mid-operation
- **Restore Preview**: Before writing anything, see file count, total size, and exactly which existing files would be affected, with per-file selection
- **Settings Management**: Configure watch directories, backup types, storage providers, and exclusions from the Settings page
- **Backup Browser**: View and manage your backup history with restore, labelling, open-location, and snapshot verification actions; filter by provider, encryption, or age
- **Automatic CLI Access**: The `restore` command is automatically available in all terminals after installation
- **Run at Startup**: Option to automatically launch ReStore when Windows starts. The MSIX package registers a Windows startup task; unpackaged installs use the per-user Run key. A startup launch opens hidden in the tray
- **Protection Status**: A status chip on the Dashboard shows whether background protection is Active, Needs password (encrypted backups paused until you unlock), Paused, or Stopped, with the next scheduled run (CLI: `restore pause`, `restore resume`, `restore schedule install` for a Task Scheduler task)
- **Background Protection**: The file watcher and interval scheduler are owned by an app-level service, not a page, so navigating between pages never restarts or duplicates them. `protection.autoStart` (default on) starts them at launch; changing unrelated settings does not restart them
- **Backup Run Reports**: The Dashboard lists the last runs per folder with files added, changed, removed or skipped, duration and bytes uploaded
- **Staleness Alerts**: A banner and a once-a-day notification flag folders that have not been backed up successfully for too long
- **Connect to Existing Backups**: From the first-run wizard or Settings, find backups already in a storage provider (for example from another PC) and adopt them; Settings also offers a repair with a preview
- **One Operation at a Time**: Backups, restores, verification, label/delete and repair coordinate through a repository lock; the GUI disables the conflicting buttons while another operation runs and the CLI waits or reports that another process is busy
- **System Tray Integration**: Minimize to tray and control the file watcher in the background
- **Failure Notifications**: Tray alerts for failed backups and integrity checks, and for recovery after a run of failures
- **Theme Support**: Light, dark, and system theme options

## File and Directory Backup

- **Multiple Backup Types**: Full, incremental, and chunk snapshot backups
- **Real-time Monitoring**: Automatic backups when files change in watched directories
- **Scheduled Backups**: Each watched directory is also swept on its configured `backupInterval`, so changes made while the machine was asleep or the app was closed still get captured
- **Clock Schedules**: Give a folder (or the system backup) its own schedule instead of the global interval: `daily 02:00`, `weekdays 18:30`, `mon,wed,fri 07:15`, `weekly sun 03:00`, `monthly last 23:30`, `every 4h`, a five-field `cron` expression, or `off`. A PC that was off at the scheduled time backs up once when it returns; daylight-saving changes never run a job twice. Edit them in Settings (with a live "Next runs" preview) or with `restore config set-schedule`; check an expression with `restore schedule preview`
- **Backup Window**: Restrict automatic work (scheduled and watcher-triggered backups, system backup, verification, retention) to a window such as 22:00 to 06:00 on chosen days. What is due outside waits for it to open; manual backups always run. `restore schedule next` explains what is waiting and why
- **Scheduled Verification**: Optional periodic integrity checks, off by default and local-only unless you opt out. Each cycle checks the newest snapshot per folder plus the least-recently-verified older ones, so old restore points are eventually covered too
- **Overwrite Protection**: Restores default to never replacing an existing file; choose skip, keep-both, or overwrite in the GUI; the CLI also has `--conflict fail`, which aborts before writing anything
- **Selective Restore**: Restore a chosen subset of a snapshot rather than all of it
- **Restore Point Labels**: Tag a snapshot ("before Windows reinstall") to find it again later
- **Smart Filtering**: Exclude patterns and paths you don't want to backup
- **Locked Files**: A file that cannot be read (for example held open by another program) is skipped and reported, and its previous version stays in the snapshot; the run completes instead of failing
- **Size Management**: Configurable thresholds and file size limits

ChunkSnapshot mode creates point-in-time manifests and reuses previously uploaded chunks to reduce transfer and storage costs. The browser can also verify a snapshot manifest and its chunk store without restoring files.

## System State Backup

- **Installed Programs**: Backup your installed software list with automatic Winget restoration scripts
- **Environment Variables**: Save and restore user and system environment variables
- **Windows Settings**: Backup registry-based settings including personalization, themes, taskbar, File Explorer preferences, regional settings, mouse and keyboard configurations, and accessibility options

## Storage Flexibility

- **Local Storage**: Backup to local drives, external drives, or network shares
- **Cloud Platforms**: Support for Google Drive, Google Cloud Storage, Amazon S3, Azure Blob Storage, Dropbox, Backblaze B2, SFTP, and GitHub storage
- **Per-Path Storage**: Configure different storage destinations for each watched directory
- **Per-Component Storage**: Use different storage backends for system backups (programs, environment, settings)
- **Global Fallback**: Set a default storage type that applies when no specific storage is configured
- **Multi-destination**: Use several storage providers at once

## File Sharing

- **Secure Sharing**: Generate shareable links (7-day expiry; Dropbox links do not expire) for your files directly from your storage provider (CLI: `restore share <file>`)
- **Context Menu Integration**: Enable "Share with ReStore" in Settings to add right-click sharing in Windows Explorer
- **Supported Providers**: Works with Amazon S3, Azure Blob Storage, Google Cloud Storage, Dropbox, and Backblaze B2

## Smart File Handling

- **Change Detection**: SHA256 hashing to detect file modifications accurately. Unchanged files are recognised by size and timestamps without being read, and a periodic deep scan (`backup.deepScanIntervalDays`, default 7) re-hashes everything
- **Parallel Transfers**: Chunk uploads and restore downloads run in parallel, bounded by each provider's own limit
- **Logging Level**: `logging.level` controls how much is written to the console and `restore.log`
- **Content-Defined Chunking**: Chunk boundaries adapt to file content for high reuse across snapshots
- **Chunk Deduplication**: Content-addressed chunk IDs avoid re-uploading unchanged data
- **Encryption**: AES-256-GCM encryption with password protection for secure backups
- **History Tracking**: Complete backup history with metadata
- **Retention Policies**: Automatic pruning of old backups (CLI: `restore retention plan` previews, `restore retention apply` deletes now)
