# Component Overview

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         ReStore (WPF Application)                           │
│                              net9.0-windows                                 │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌───────────────┐  ┌───────────────┐  ┌───────────────┐  ┌──────────────┐  │
│  │ DashboardPage │  │  BackupsPage  │  │ SettingsPage  │  │SystemRestore │  │
│  │               │  │               │  │               │  │     Page     │  │
│  │ • Statistics  │  │ • History     │  │ • Config Edit │  │ • Sys Backups│  │
│  │ • Start/Stop  │  │ • Filter/Sort │  │ • Providers   │  │ • Programs   │  │
│  │ • Manual Ops  │  │ • Restore     │  │ • Exclusions  │  │ • Environment│  │
│  │ • System Bkp  │  │ • Delete      │  │ • Validation  │  │ • Settings   │  │
│  │               │  │               │  │ • Encryption  │  │ • Restore Ops│  │
│  └───────┬───────┘  └───────┬───────┘  └───────┬───────┘  └───────┬──────┘  │
│          │                  │                  │                  │         │
│          └──────────────────┴──────────────────┴──────────────────┘         │
│                             │                                               │
│  ┌──────────────────────────┴───────────────────────────┐                   │
│  │                    GUI Services                      │                   │
│  ├──────────────────────────────────────────────────────┤                   │
│  │ • WatcherService (Singleton Bridge)                  │                   │
│  │ • SystemTrayManager (Tray Icon)                      │                   │
│  │ • AppSettings (GUI Settings)                         │                   │
│  │ • ThemeSettings (Theme Management)                   │                   │
│  │ • GuiPasswordProvider (Password Management)          │                   │
│  │ • FileContextMenuService (Explorer Context Menu)     │                   │
│  └──────────────────────────────────────────────────────┘                   │
│                             │                                               │
│  ┌──────────────────────────┴───────────────────────────┐                   │
│  │                    GUI Windows                       │                   │
│  ├──────────────────────────────────────────────────────┤                   │
│  │ • PasswordPromptWindow (Password Entry)              │                   │
│  │ • EncryptionSetupWindow (Encryption Config)          │                   │
│  │ • RestoreProgressWindow (System Restore Progress)    │                   │
│  │ • ShareWindow (File Sharing Dialog)                  │                   │
│  └──────────────────────────────────────────────────────┘                   │
│                             │                                               │
└─────────────────────────────┼───────────────────────────────────────────────┘
                              │
                              │ Project Reference
                              ↓
┌─────────────────────────────────────────────────────────────────────────────┐
│                         ReStore.Core (Library)                              │
│                              net9.0                                         │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌────────────────────────────────────────────────────────────────┐         │
│  │                     src/utils (Utilities)                      │         │
│  ├────────────────────────────────────────────────────────────────┤         │
│  │ • ConfigManager ← Load/Save/Validate Config                    │         │
│  │ • ConfigInitializer ← Config Setup & Initialization            │         │
│  │ • Logger / ILogger ← Logging Infrastructure                    │         │
│  │ • CompressionUtil ← Zip/Unzip + Encryption Integration         │         │
│  │ • FileHasher ← File Hash Calculation (SHA256)                  │         │
│  │ • FileSelectionService ← File Filtering Logic                  │         │
│  │ • ConfigValidator ← Configuration Validation                   │         │
│  │ • EncryptionService ← AES-256-GCM Encryption/Decryption        │         │
│  │ • IPasswordProvider ← Password Provider Interface              │         │
│  │ • EnvironmentVariablesManager ← Env Var Backup/Restore         │         │
│  │ • SystemProgramDiscovery ← Installed Programs Detection        │         │
│  │ • WindowsSettingsManager ← Registry Settings Backup/Restore    │         │
│  └────────────────────────────────────────────────────────────────┘         │
│                                                                             │
│  ┌────────────────────────────────────────────────────────────────┐         │
│  │                   src/core (Core Logic)                        │         │
│  ├────────────────────────────────────────────────────────────────┤         │
│  │ • SystemState ← State Management & Persistence                 │         │
│  │ • Backup ← Backup Operations (Full/Incremental/Differential)   │         │
│  │ • Restore ← Restore Operations                                 │         │
│  │ • DiffManager ← Differential Backup Logic                      │         │
│  └────────────────────────────────────────────────────────────────┘         │
│                                                                             │
│  ┌────────────────────────────────────────────────────────────────┐         │
│  │                 src/monitoring (File Watching)                 │         │
│  ├────────────────────────────────────────────────────────────────┤         │
│  │ • FileWatcher ← Real-time File Monitoring                      │         │
│  │ • SizeAnalyzer ← Directory Size Analysis                       │         │
│  └────────────────────────────────────────────────────────────────┘         │
│                                                                             │
│  ┌────────────────────────────────────────────────────────────────┐         │
│  │                 src/sharing (File Sharing)                     │         │
│  ├────────────────────────────────────────────────────────────────┤         │
│  │ • ShareService ← File Upload & Link Generation                 │         │
│  └────────────────────────────────────────────────────────────────┘         │
│                                                                             │
│  ┌────────────────────────────────────────────────────────────────┐         │
│  │                 src/storage (Storage Providers)                │         │
│  ├────────────────────────────────────────────────────────────────┤         │
│  │ • IStorage ← Storage Interface                                 │         │
│  │ • StorageBase ← Base Implementation                            │         │
│  │ • StorageFactory ← Provider Factory                            │         │
│  │   ├── local/LocalStorage ← Local File System                   │         │
│  │   ├── aws/S3Storage ← Amazon S3                                │         │
│  │   ├── azure/AzureStorage ← Azure Blob Storage                  │         │
│  │   ├── google/DriveStorage ← Google Drive                       │         │
│  │   ├── google/GcpStorage ← Google Cloud Storage                 │         │
│  │   ├── dropbox/DropboxStorage ← Dropbox                         │         │
│  │   ├── backblaze/B2Storage ← Backblaze B2                       │         │
│  │   ├── sftp/SftpStorage ← SFTP                                  │         │
│  │   └── github/GitHubStorage ← GitHub Releases                   │         │
│  └────────────────────────────────────────────────────────────────┘         │
│                                                                             │
│  ┌────────────────────────────────────────────────────────────────┐         │
│  │                 src/backup (Backup Management)                 │         │
│  ├────────────────────────────────────────────────────────────────┤         │
│  │ • SystemBackupManager ← System Backup (Programs/Env/Settings)  │         │
│  │   - BackupInstalledProgramsAsync() ← Program List Backup       │         │
│  │   - BackupEnvironmentVariablesAsync() ← Env Variables Backup   │         │
│  │   - BackupWindowsSettingsAsync() ← Registry Settings Backup    │         │
│  │   - RestoreSystemAsync() ← System Restore with Decryption      │         │
│  │ • BackupConfigurationManager ← Backup Config                   │         │
│  │ • FileDiffSyncManager ← Diff Sync Logic                        │         │
│  │ • ProgramRestoreManager ← Program Restore                      │         │
│  │ • RetentionManager ← Backup Pruning & Retention Policies       │         │
│  └────────────────────────────────────────────────────────────────┘         │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
                              │
                              │ External Dependencies
                              ↓
┌─────────────────────────────────────────────────────────────────────────────┐
│                         External Services & APIs                            │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  • Local File System ← LocalStorage                                         │
│  • Amazon S3 API ← S3Storage (AWSSDK.S3)                                    │
│  • Azure Blob Storage API ← AzureStorage (Azure.Storage.Blobs)              │
│  • Google Drive API ← DriveStorage (Google.Apis.Drive.v3)                   │
│  • Google Cloud Storage API ← GcpStorage (Google.Cloud.Storage.V1)          │
│  • Dropbox API ← DropboxStorage (Dropbox.Api)                               │
│  • Backblaze B2 API ← B2Storage (via AWSSDK.S3)                             │
│  • SFTP ← SftpStorage (SSH.NET)                                             │
│  • GitHub API ← GitHubStorage (Octokit)                                     │
│  • Windows Registry ← SystemBackupManager                                   │
│  • Winget CLI ← ProgramRestoreManager                                       │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

## IStorage Interface

All storage backends implement the `IStorage` interface:

```csharp
public interface IStorage : IDisposable
{
    Task InitializeAsync(Dictionary<string, string> options);
    Task UploadAsync(string localPath, string remotePath);
    Task DownloadAsync(string remotePath, string localPath);
    Task<bool> ExistsAsync(string remotePath);
    Task DeleteAsync(string remotePath);
    Task<string> GenerateShareLinkAsync(string remotePath, TimeSpan expiration);
    bool SupportsSharing { get; }
}
```

### Storage Provider Support Matrix

| Provider             | Backup/Restore | File Sharing |
| -------------------- | -------------- | ------------ |
| Local                | ✓              | ✗            |
| S3                   | ✓              | ✓            |
| Azure                | ✓              | ✓            |
| Google Drive         | ✓              | ✗            |
| Google Cloud Storage | ✓              | ✓            |
| Dropbox              | ✓              | ✓            |
| Backblaze B2         | ✓              | ✓            |
| SFTP                 | ✓              | ✗            |
| GitHub               | ✓              | ✗            |

## GUI-Core Bridge

The `WatcherService` singleton bridges the GUI and Core library:

- GUI creates `FileWatcher` instances and registers them with `WatcherService`
- Enables shared watcher state across GUI pages
- **Pattern**: Use singleton for cross-page state, not for services tied to view lifecycle

## Service Layer

### GUI Services

| Service                  | Responsibility                           |
| ------------------------ | ---------------------------------------- |
| `WatcherService`         | File watcher lifecycle management        |
| `SystemTrayManager`      | System tray icon and notifications       |
| `AppSettings`            | GUI-specific settings persistence        |
| `ThemeSettings`          | Light/Dark/System theme management       |
| `GuiPasswordProvider`    | Password caching and prompt handling     |
| `FileContextMenuService` | Windows Explorer context menu management |

### Core Utilities

| Utility                       | Responsibility                              |
| ----------------------------- | ------------------------------------------- |
| `ConfigManager`               | Configuration loading, saving, validation   |
| `ConfigInitializer`           | First-run setup and directory creation      |
| `EncryptionService`           | AES-256-GCM encryption/decryption           |
| `CompressionUtil`             | Zip compression with encryption integration |
| `FileHasher`                  | SHA256 file hashing for change detection    |
| `FileSelectionService`        | File filtering based on exclusion rules     |
| `SystemProgramDiscovery`      | Winget and registry program detection       |
| `WindowsSettingsManager`      | Registry settings export/import             |
| `EnvironmentVariablesManager` | Environment variable backup/restore         |
