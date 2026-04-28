# Data Flows

## High-Level Data Flow

```
User Action (GUI)
      │
      ↓
┌─────────────┐
│   Page      │ (DashboardPage/BackupsPage/SettingsPage)
└──────┬──────┘
       │
       ↓
┌─────────────┐
│ ConfigMgr   │ ← Loads config.json
└──────┬──────┘
       │
       ↓
┌─────────────┐
│ SystemState │ ← Loads system_state.json
└──────┬──────┘
       │
       ↓
┌─────────────┐
│  Storage    │ ← Creates storage provider instance
└──────┬──────┘
       │
       ├──→ LocalStorage ──→ File System
       ├──→ S3Storage ──→ AWS S3
       ├──→ AzureStorage ──→ Azure Blob Storage
       ├──→ DriveStorage ──→ Google Drive
       ├──→ GcpStorage ──→ Google Cloud Storage
       ├──→ DropboxStorage ──→ Dropbox
       ├──→ B2Storage ──→ Backblaze B2
       ├──→ SftpStorage ──→ SFTP Server
       └──→ GitHubStorage ──→ GitHub
       │
       ↓
┌─────────────┐
│ Backup/     │ ← Executes operation
│ Restore     │
└──────┬──────┘
       │
       ↓
┌─────────────┐
│ Update      │ ← Updates state
│ State       │
└──────┬──────┘
       │
       ↓
┌─────────────┐
│ Save State  │ ← Persists to disk
└─────────────┘
```

## File Watcher Flow

```
User clicks "Start Watcher" in GUI
  ↓
DashboardPage creates FileWatcher instance
  ↓
FileWatcher monitors directories from ConfigManager
  ↓
File change detected
  ↓
FileWatcher buffers changes (10-second delay)
  ↓
FileWatcher triggers Backup.BackupFilesAsync()
  ↓
Backup uploads missing chunks and snapshot manifest via Storage provider
  ↓
SystemState updated with snapshot metadata
  ↓
GUI refreshes backup history display
```

## Backup Flow

```
User clicks "Manual Backup" in GUI
  ↓
DashboardPage shows folder selection dialog
  ↓
Backup.BackupDirectoryAsync() called
  ↓
SizeAnalyzer checks directory size
  ↓
FileSelectionService filters files (exclusions)
  ↓
SystemState.GetChangedFiles() determines changed files
  ↓
ChunkingService builds content-defined chunks for changed files
  ↓
Backup uploads only missing chunks (ExistsAsync checks)
  ↓
Backup uploads snapshot manifest
  ↓
Backup updates HEAD pointer (commit point)
  ↓
RetentionManager applies manifest retention and chunk GC
  ↓
SystemState.AddSnapshotBackup() records snapshot and chunk references
  ↓
SystemState.SaveStateAsync() persists state
  ↓
GUI updates statistics and history
```

Current implementation note:

- User-file backups are chunk snapshots.
- Snapshot manifests, HEAD pointers, and deduplicated chunks are the canonical artifact format.
- `DiffManager` is a standalone binary diff prototype and is not part of the production backup or restore flow.

## Restore Flow

```
User clicks "Restore" in GUI
  ↓
BackupsPage shows folder selection dialog
  ↓
Restore.RestoreFromBackupAsync() called
  ↓
Restore resolves HEAD to a manifest path (if needed)
  ↓
Storage.DownloadAsync() downloads snapshot manifest
  ↓
Manifest root hash is validated
  ↓
Restore downloads referenced chunks and validates chunk hashes
  ↓
Restore reconstructs files and validates final file hashes
  ↓
Files restored to target directory
  ↓
GUI shows success message
```

## Verification Flow

```
User runs "restore verify <manifest-or-head-path>"
  ↓
SnapshotIntegrityVerifier resolves HEAD to manifest (if needed)
  ↓
Manifest is downloaded and root hash validated
  ↓
Each unique chunk is downloaded and validated
  ↓
Each file hash is recomputed from verified chunks
  ↓
Verification telemetry is logged (reuse, missing/invalid chunks, file validation failures)
  ↓
CLI exits with status 0 on success, non-zero on validation failure
```

## File Sharing Flow

```
User enables context menu in Settings → Application Behavior
  ↓
FileContextMenuService.RegisterContextMenu() adds registry entry
  ↓
User right-clicks file in Explorer → "Share with ReStore"
  ↓
ReStore.exe --share "path/to/file" launches
  ↓
ShareWindow opens with provider selection
  ↓
User selects storage provider and expiration
  ↓
ShareService.ShareFileAsync() called
  ↓
File uploaded unencrypted to shared/ folder
  ↓
Storage.GenerateShareLinkAsync() creates presigned URL
  ↓
Share link displayed and copied to clipboard
  ↓
App exits (shutdownOnClose=true)
```

## Encryption Flow (Backup)

```
User enables encryption in Settings
  ↓
EncryptionSetupWindow prompts for password
  ↓
Password strength validated (min 8 characters)
  ↓
EncryptionService.GenerateSalt() creates master salt
  ↓
EncryptionService.CreatePasswordVerificationToken() creates token
  ↓
Salt + VerificationToken saved to config.json
  ↓
Password cached in App.GlobalPasswordProvider for session
  ↓
User creates backup
  ↓
GuiPasswordProvider.SetEncryptionMode(true) sets backup context
  ↓
If password cached: Use directly
If not cached: Prompt with "Encryption Password Required"
  ↓
EncryptionService.VerifyPassword() validates against token
  ↓
CompressionUtil.CompressAndEncryptAsync() called:
  ├─ Compress files to .zip
  ├─ EncryptionService.EncryptFileAsync(zip, password, masterSalt)
  │   ├─ Derive KEK from password + masterSalt (PBKDF2-SHA256, 1M iterations)
  │   ├─ Generate random DEK (32 bytes)
  │   ├─ Encrypt DEK with KEK (AES-256-GCM)
  │   ├─ Encrypt file content with DEK
  │   └─ Return metadata (salt, IV, encryptedDEK)
  ├─ Save metadata to .enc.meta file
  └─ Delete unencrypted .zip
  ↓
Storage.UploadAsync() uploads both .enc and .enc.meta files
  ↓
SystemState updated with backup info
  ↓
GUI shows success message
```

## Decryption Flow (Restore)

```
User restores encrypted backup
  ↓
GuiPasswordProvider.SetEncryptionMode(false) sets restore context
  ↓
PasswordPromptWindow shows "Decrypt Backup" message
  ↓
User enters password (no validation against token)
  ↓
Storage.DownloadAsync() downloads .enc and .enc.meta
  ↓
CompressionUtil.DecryptAndDecompressAsync() called:
  ├─ EncryptionService.LoadMetadataAsync() loads metadata
  ├─ EncryptionService.DecryptFileAsync(enc, password, metadata)
  │   ├─ Derive KEK from password + metadata.Salt
  │   ├─ Decrypt DEK using KEK (if wrong password: AES-GCM fails here)
  │   ├─ Decrypt file content using DEK
  │   └─ Write decrypted .zip file
  ├─ On CryptographicException:
  │   ├─ GuiPasswordProvider.ClearPassword() clears cache
  │   └─ Throw "Invalid password or corrupted data"
  └─ Decompress .zip to target directory
  ↓
Files restored
  ↓
GUI shows success message
```

## Retention Flow

```
Backup completes successfully
  ↓
RetentionManager.ApplyRetentionPolicyAsync() called
  ↓
SystemState.GetBackupGroups() returns all backup groups
  ↓
For each group:
  ├─ Get backups sorted by timestamp (newest first)
  ├─ SelectBackupsToDelete() applies retention rules:
  │   ├─ Keep newest snapshot (always)
  │   ├─ Keep last N snapshots (KeepLastPerDirectory)
  │   └─ Remove snapshots older than MaxAgeDays
  ├─ Delete selected manifests from storage
  ├─ Unregister deleted manifests from chunk reference counts
  └─ Delete only chunks that become unreferenced
  └─ Remove from SystemState.BackupHistory
  ↓
SystemState.SaveStateAsync() persists updated state
```
