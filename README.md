# ReStore

A backup and restore solution for Windows systems that protects your files, programs, and system configuration. ReStore offers intelligent file monitoring, multi-platform storage support, and complete system state backup capabilities.

## Features

### File and Directory Backup

- **Multiple Backup Types**: Full, incremental, and differential backups to optimize storage and time
- **Real-time Monitoring**: Continuous file system watching with automatic backup triggering
- **Smart Filtering**: Exclude temporary files, system directories, and large files with pattern matching
- **Size Management**: Pre-backup analysis with configurable thresholds and warnings

### System State Backup

- **Installed Programs**: Complete inventory of all installed software with restoration options
- **Environment Variables**: Backup and restore user and system environment variables
- **Winget Integration**: Automatic program reinstallation for winget-compatible software
- **Registry Backup**: Export environment variable registry entries for manual restoration

### Storage Flexibility

- **Local Storage**: Local drives, external storage, and network shares
- **Cloud Platforms**: Google Drive, Amazon S3, and GitHub repository storage
- **Multi-destination**: Different storage backends for different backup types

### Intelligent File Handling

- **Change Detection**: SHA256 hashing for accurate file modification detection
- **Compression**: ZIP compression to minimize backup size
- **Metadata Tracking**: Complete backup history and file relationship management

## Installation

### Prerequisites

- .NET 9.0 or later
- Windows operating system

### Build from Source

1. Clone the repository:

   ```bash
   git clone https://github.com/js302/ReStore.git
   cd ReStore
   ```

2. Build the project:

   ```bash
   dotnet build
   ```

3. Run the application:
   ```bash
   dotnet run --project ReStore
   ```

## Usage

### File Backup Commands

#### Service Mode (Continuous Monitoring)

Start ReStore as a background service to monitor file changes:

```bash
ReStore.exe --service local
```

#### Manual File Backup

Create a backup of a specific directory:

```bash
ReStore.exe backup local "C:\Users\YourName\Documents"
```

#### File Restore

Restore files from a backup:

```bash
ReStore.exe restore local "backups/Documents/backup_Documents_20250817120000.zip" "C:\Restore\Documents"
```

### System Backup Commands

#### Complete System Backup

Backup both programs and environment variables:

```bash
ReStore.exe system-backup local all
```

#### Program-only Backup

Backup installed programs information:

```bash
ReStore.exe system-backup local programs
```

#### Environment Variables Backup

Backup user and system environment variables:

```bash
ReStore.exe system-backup local environment
```

#### System Restore

Restore system components from backup:

```bash
ReStore.exe system-restore local "system_backups/programs/programs_backup_20250906143022.zip" programs
ReStore.exe system-restore local "system_backups/environment/env_backup_20250906143022.zip" environment
```

### Configuration

ReStore uses a JSON configuration file at `config/config.json`. Key configuration sections:

#### Watch Directories

Define directories for continuous monitoring:

```json
"watchDirectories": [
  "%USERPROFILE%\\Desktop",
  "%USERPROFILE%\\Documents",
  "%USERPROFILE%\\Pictures"
]
```

#### System Backup Configuration

Control system backup behavior:

```json
"systemBackup": {
  "enabled": true,
  "includePrograms": true,
  "includeEnvironmentVariables": true,
  "backupInterval": "24:00:00",
  "excludeSystemPrograms": [
    "Microsoft Visual C++",
    "Microsoft .NET",
    "Windows SDK"
  ]
}
```

#### Storage Configuration

Set up multiple storage backends:

```json
"storageSources": {
  "local": {
    "path": "%USERPROFILE%\\ReStoreBackups",
    "options": {}
  },
  "gdrive": {
    "path": "./backups",
    "options": {
      "client_id": "your_client_id",
      "client_secret": "your_client_secret"
    }
  }
}
```

#### Backup Settings

Configure backup timing and behavior:

```json
"backupInterval": "01:00:00",
"backupType": "Incremental",
"sizeThresholdMB": 500,
"maxFileSizeMB": 100
```

#### File Exclusions

Exclude files and directories from backups:

```json
"excludedPatterns": [
  "*.tmp",
  "*.temp",
  "Thumbs.db",
  "*.log"
],
"excludedPaths": [
  "%TEMP%",
  "%WINDIR%"
]
```

## System Backup Details

### Program Backup and Restore

ReStore discovers installed programs through multiple methods to ensure full coverage:

**Discovery Methods:**

- **Winget**: Queries Windows Package Manager for managed installations
- **Registry Scanning**: Examines Windows Registry for all installed programs
- **Cross-referencing**: Identifies which programs can be automatically reinstalled

**Backup Contents:**

- Complete program inventory in JSON format
- Automated winget installation scripts
- Manual installation reference lists
- Restore scripts with error handling

**Restore Process:**
Programs available through winget can be automatically reinstalled. Programs not in winget are documented with installation details for manual reinstallation.

### Environment Variables

**Backup Coverage:**

- User-specific environment variables
- System-wide environment variables
- Selective exclusion of system-managed variables

**Restore Options:**

- Direct programmatic restoration
- PowerShell script generation for manual execution
- Registry export files for advanced users

### Generated Restore Files

**Program Restoration:**

- `restore_winget_programs.ps1` - Automatic installation via winget
- `manual_install_list.txt` - Programs requiring manual installation
- `restore_programs.ps1` - Combined automatic and manual restore workflow

**Environment Variables:**

- `restore_environment_variables.ps1` - Variable restoration script
- `backup_env_registry.ps1` - Registry backup script for advanced scenarios

## Storage Backends

### Local Storage

File system or network drive storage. Configure the path to your preferred backup location.

### Google Drive

Cloud storage with OAuth2 authentication. Requires Google API credentials.

### AWS S3

Amazon S3 bucket storage. Configure AWS credentials and bucket details.

### GitHub

Repository-based storage for smaller backups and version-controlled data.

## Backup Types

### Full Backup

Complete backup of all selected files. Best for initial backups or creating standalone restore points.

### Incremental Backup

Backs up only files changed since the last backup of any type. Most efficient for regular scheduled backups.

### Differential Backup

Backs up files changed since the last full backup. Good balance between storage efficiency and restore speed.

## Architecture

### Core Components

- **SystemState**: File metadata and backup history management
- **Backup**: Backup creation and intelligent file selection
- **Restore**: Restore operations and differential file reconstruction
- **FileWatcher**: Real-time file system monitoring
- **SystemBackupManager**: System state backup and restore coordination

### Storage Layer

- **StorageBase**: Foundation for all storage implementations
- **IStorage**: Storage operation interface
- **Multiple Backends**: Local, cloud, and repository storage options

### Utilities

- **SystemProgramDiscovery**: Multi-method program detection and cataloging
- **EnvironmentVariablesManager**: Environment variable backup and restoration
- **FileSelectionService**: Advanced file filtering and exclusion management
- **CompressionUtil**: ZIP archive operations
- **Logger**: Logging with multiple output targets

## Development

### Project Structure

```
ReStore/
├── Program.cs              # Application entry point
├── config/                 # Configuration files
├── src/
│   ├── core/              # Core backup/restore logic
│   ├── storage/           # Storage implementations
│   ├── monitoring/        # File watching and analysis
│   ├── utils/             # Utility classes and system discovery
│   └── backup/            # Backup management and system state
```

### Extending Storage Support

1. Implement the `IStorage` interface
2. Inherit from `StorageBase` for common functionality
3. Register the new storage type in configuration
4. Add any required authentication or connection logic

### System Backup Extensibility

The system backup framework can be extended to support additional system components such as:

- Windows Registry sections
- Installed Windows services
- Browser bookmarks and extensions
- Application-specific settings

## Important Notes

### Security Considerations

- Environment variables may contain sensitive data like API keys
- System backups should be stored in secure locations
- Some restore operations require administrator privileges
- Always verify restore scripts before execution in production environments

### Limitations

- System backup features are Windows-specific
- Automatic program restoration requires Windows Package Manager (winget)
- Some programs may install newer versions than originally backed up
- Registry modifications during restoration may require system restart

### Best Practices

- Run regular system backups before major system changes
- Test restore procedures in safe environments
- Keep secure backups of sensitive environment variables
- Document any custom software that cannot be automatically restored

## License

This project is licensed under the MIT License. See the LICENSE file for details.

## Contributing

Contributions welcome. Please submit issues and pull requests through the project repository.
