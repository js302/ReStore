# Features

## Graphical User Interface

- **Modern Dashboard**: Real-time statistics, backup history, and quick actions
- **Settings Management**: Configure watch directories, backup types, storage providers, and exclusions through an intuitive interface
- **Backup Browser**: View and manage your backup history with restore, open-location, and snapshot verification actions
- **Automatic CLI Access**: The `restore` command is automatically available in all terminals after installation
- **Run at Startup**: Option to automatically launch ReStore when Windows starts
- **System Tray Integration**: Minimize to tray and control the file watcher in the background
- **Theme Support**: Light, dark, and system theme options

## File and Directory Backup

- **Multiple Backup Types**: Full, incremental, and chunk snapshot backups
- **Real-time Monitoring**: Automatic backups when files change in watched directories
- **Smart Filtering**: Exclude patterns and paths you don't want to backup
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
- **Multi-destination**: Seamlessly use multiple storage providers simultaneously

## File Sharing

- **Secure Sharing**: Generate temporary, shareable links for your files directly from your storage provider
- **Context Menu Integration**: Enable "Share with ReStore" in Settings to add right-click sharing in Windows Explorer
- **Supported Providers**: Works with Amazon S3, Azure Blob Storage, Google Cloud Storage, Dropbox, and Backblaze B2

## Smart File Handling

- **Change Detection**: SHA256 hashing to detect file modifications accurately
- **Content-Defined Chunking**: Chunk boundaries adapt to file content for high reuse across snapshots
- **Chunk Deduplication**: Content-addressed chunk IDs avoid re-uploading unchanged data
- **Encryption**: AES-256-GCM encryption with password protection for secure backups
- **History Tracking**: Complete backup history with metadata
- **Retention Policies**: Automatic pruning of old backups
