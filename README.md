# ReStore

A backup and restore tool for Windows systems that captures and stores your data across multiple storage platforms. ReStore handles incremental backups, monitors file changes in real-time, and provides flexible restore options.

## Features

### Backup Operations
- **Full, Incremental, and Differential Backups**: Choose the backup type that fits your needs
- **Real-time File Monitoring**: Automatically detects file changes and triggers backups
- **Smart File Filtering**: Configurable exclusion patterns to skip temporary files, system files, and large files
- **Size Analysis**: Pre-backup directory analysis with configurable size thresholds

### Storage Options
- **Local Storage**: Store backups on local drives or network shares
- **Cloud Storage**: Support for Google Drive, AWS S3, and GitHub repositories
- **Multiple Destinations**: Configure different storage backends for different directories

### File Management
- **Hash-based Change Detection**: Uses SHA256 to accurately detect file modifications
- **Compression**: ZIP compression reduces backup size
- **Metadata Tracking**: Maintains file history and backup relationships

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

### Command Line Interface

#### Service Mode (Continuous Monitoring)
Start ReStore as a background service to monitor file changes:
```bash
ReStore.exe --service local
```

#### Manual Backup
Create a backup of a specific directory:
```bash
ReStore.exe backup local "C:\Users\YourName\Documents"
```

#### Restore from Backup
Restore files from a backup:
```bash
ReStore.exe restore local "backups/Documents/backup_Documents_20250817120000.zip" "C:\Restore\Documents"
```

### Configuration

ReStore uses a JSON configuration file located at `config/default_config.json`. The configuration includes:

#### Watch Directories
Specify which directories to monitor:
```json
"watchDirectories": [
  "%USERPROFILE%\\Desktop",
  "%USERPROFILE%\\Documents",
  "%USERPROFILE%\\Pictures"
]
```

#### Storage Sources
Configure storage backends:
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
Control backup behavior:
```json
"backupInterval": "01:00:00",
"backupType": "Incremental",
"sizeThresholdMB": 500,
"maxFileSizeMB": 100
```

#### File Exclusions
Exclude specific files and directories:
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

## Storage Backends

### Local Storage
Stores backups on the local file system or network drives. Set the `path` option to your desired backup location.

### Google Drive
Requires OAuth2 authentication. Provide your Google API credentials in the configuration.

### AWS S3
Store backups in Amazon S3. Configure your AWS credentials and bucket information.

### GitHub
Uses GitHub repositories for backup storage. Suitable for smaller backups and version-controlled data.

## Architecture

### Core Components
- **SystemState**: Manages file metadata and backup history
- **Backup**: Handles backup creation and file selection
- **Restore**: Manages restore operations and differential reconstruction
- **FileWatcher**: Real-time file system monitoring

### Storage Layer
- **StorageBase**: Abstract base class for all storage implementations
- **IStorage**: Interface defining storage operations
- **StorageFactory**: Creates storage instances based on configuration

### Utilities
- **FileSelectionService**: Handles file filtering and exclusion rules
- **CompressionUtil**: ZIP file operations
- **Logger**: Application logging with file and console output

## Backup Types

### Full Backup
Creates a complete backup of all selected files. Use this for the initial backup or when you want a standalone restore point.

### Incremental Backup
Backs up only files that have changed since the last backup (any type). This is the most efficient option for regular backups.

### Differential Backup
Backs up files that have changed since the last full backup. Provides a balance between storage efficiency and restore speed.

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
│   ├── utils/             # Utility classes
│   └── backup/            # Backup management
```

### Adding Storage Backends
1. Implement the `IStorage` interface
2. Inherit from `StorageBase`
3. Register in `StorageFactory`
4. Add configuration options

## License

This project is licensed under the MIT License. See the LICENSE file for details.

## Contributing

Contributions are welcome. Please feel free to submit issues and pull requests.