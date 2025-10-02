# ReStore

A backup and restore solution for Windows that protects your files, programs, and system configuration. ReStore includes both a command-line interface and a modern WPF GUI for managing your backups.

## Features

### Graphical User Interface

- **Modern Dashboard**: Real-time statistics, backup history, and quick actions
- **Settings Management**: Configure watch directories, backup types, storage providers, and exclusions through an intuitive interface
- **Backup Browser**: View and manage your backup history with one-click restore
- **CLI Integration**: Enable/disable command-line access from the settings menu
- **Run at Startup**: Option to automatically launch ReStore when Windows starts
- **System Tray Integration**: Minimize to tray and control the file watcher in the background
- **Theme Support**: Light, dark, and system theme options

### File and Directory Backup

- **Multiple Backup Types**: Full, incremental, and differential backups
- **Real-time Monitoring**: Automatic backups when files change in watched directories
- **Smart Filtering**: Exclude patterns and paths you don't want to backup
- **Size Management**: Configurable thresholds and file size limits

### System State Backup

- **Installed Programs**: Backup your installed software list with automatic Winget restoration scripts
- **Environment Variables**: Save and restore user and system environment variables
- **Registry Backup**: Export environment variable registry entries for manual restoration

### Storage Flexibility

- **Local Storage**: Backup to local drives, external drives, or network shares
- **Cloud Platforms**: Support for Google Drive, Amazon S3, and GitHub storage
- **Multi-destination**: Use different storage backends for different backup types

### Smart File Handling

- **Change Detection**: SHA256 hashing to detect file modifications accurately
- **Compression**: ZIP compression to save storage space
- **History Tracking**: Complete backup history with metadata

## Installation

### Prerequisites

- .NET 9.0 or later
- Windows OS (Windows 10 or later recommended)

### Build from Source

1. Clone the repository:

   ```bash
   git clone https://github.com/js302/ReStore.git
   cd ReStore
   ```

2. Build the solution:

   ```bash
   dotnet build
   ```

3. Run the GUI application:

   ```bash
   dotnet run --project ReStore
   ```

   Or run the CLI application:

   ```bash
   dotnet run --project ReStore
   ```

## Usage

### GUI Application

Launch the WPF application for a visual interface:

```bash
ReStore.exe
```

The GUI provides:

- Dashboard with backup statistics and quick actions
- Backup history browser with one-click restore
- Settings page for configuring watch directories, storage providers, and backup options
- System tray support for background operation

**Post-Installation Configuration**:

After installation, visit the Settings page to:

- Enable "Run at Windows Startup" to launch ReStore automatically when your computer boots
- Enable "CLI Access" to add the `restore` command to your system PATH for terminal access

### Command Line Interface

The CLI is still available for scripting and automation.

#### Start File Watcher

Monitor directories for changes and backup automatically:

```bash
ReStore.exe --service local
```

#### Manual Backup

Backup a specific directory:

```bash
ReStore.exe backup local "C:\Users\YourName\Documents"
```

#### Restore Files

Restore from a backup:

```bash
ReStore.exe restore local "backups/Documents/backup_Documents_20250817120000.zip" "C:\Restore\Documents"
```

#### System Backup

Backup installed programs and environment variables:

```bash
ReStore.exe system-backup local all
ReStore.exe system-backup local programs
ReStore.exe system-backup local environment
```

#### System Restore

Restore system components:

```bash
ReStore.exe system-restore local "system_backups/programs/programs_backup_20250906143022.zip" programs
ReStore.exe system-restore local "system_backups/environment/env_backup_20250906143022.zip" environment
```

## Configuration

You can configure ReStore through the GUI settings page or by editing `config/config.json` directly.

### Key Settings

**Watch Directories**: Folders to monitor for automatic backups

**Backup Type**: Choose between Full, Incremental, or Differential

**Backup Interval**: How often to check for changes (in hours)

**Storage Providers**: Configure Local, S3, Google Drive, or GitHub storage

**Exclusions**: File patterns and paths to skip during backup

**Size Limits**: Maximum file size and backup size thresholds

### Configuration File Location

- GUI: `ReStore/config/config.json`
- CLI: `ReStore/config/config.json`

A sample configuration file is provided as `config.example.json`.

### Application Behavior Settings

**Run at Windows Startup**: Configure ReStore to automatically launch when Windows starts. This feature adds an entry to the Windows Registry (`HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`). By default, this is disabled and can be toggled from the Settings page.

**CLI Access Management**: Enable or disable command-line access to ReStore from any terminal:

- **Enable**: Adds the ReStore CLI folder to your user PATH environment variable, allowing you to run `restore` commands from anywhere
- **Disable**: Removes the CLI folder from your PATH
- **Location**: The CLI executable is bundled with the GUI installation in the `cli\` subdirectory

Example usage after enabling CLI access:

```powershell
# Create a manual backup
restore backup

# List all backups
restore list-backups

# Restore from a specific backup
restore restore --id abc123

# View help
restore --help
```

## System Backup

ReStore can backup your Windows system configuration, including installed programs and environment variables.

### Program Backup

ReStore scans for installed programs using Winget and Windows Registry. It creates:

- A complete list of installed software
- Automated restoration scripts for Winget-compatible programs
- Manual installation lists for programs not available through Winget

### Environment Variables

Backup both user and system environment variables. The restore process generates PowerShell scripts you can run to restore your environment configuration.

### Generated Files

When you perform a system backup, ReStore creates:

- `restore_winget_programs.ps1` - Automated program installation
- `manual_install_list.txt` - Programs needing manual installation
- `restore_environment_variables.ps1` - Environment variable restoration

## Storage Options

**Local**: Backup to local drives, external drives, or network shares

**Google Drive**: Cloud storage with OAuth2 authentication (requires API credentials)

**AWS S3**: Amazon S3 bucket storage (requires AWS credentials and bucket configuration)

**GitHub**: Repository-based storage for version-controlled backups

## Backup Types

**Full**: Complete backup of all files in selected directories

**Incremental**: Only backs up files changed since the last backup (most efficient)

**Differential**: Backs up files changed since the last full backup

## Project Structure

```
ReStore/
├── ReStore/                    # Core CLI application
│   ├── Program.cs             # CLI entry point
│   ├── src/
│   │   ├── core/             # Backup, restore, and state management
│   │   ├── storage/          # Storage provider implementations
│   │   ├── monitoring/       # File watching and change detection
│   │   ├── utils/            # Configuration, logging, and utilities
│   │   └── backup/           # System backup functionality
│   └── config/               # Configuration files
│
└── ReStore/           # WPF GUI application
    ├── App.xaml              # Application entry point
    ├── Views/                # UI pages (Dashboard, Backups, Settings)
    ├── Services/             # GUI services (theme, tray, settings)
    └── config/               # GUI configuration files
```

## Development

The project is built with .NET 9.0 and uses:

- WPF for the GUI with the WPF-UI library
- JSON for configuration
- SHA256 hashing for change detection
- ZIP compression for backups

To extend storage support, implement the `IStorage` interface and register your provider in the configuration.

## Notes

### Security

- Store configuration files securely, especially if they contain cloud storage credentials
- Environment variables may include sensitive information like API keys
- Administrator privileges may be required for some restore operations

### Limitations

- System backup is Windows-only
- Automatic program restoration requires Winget
- Some programs may install newer versions than what was backed up

### Best Practices

- Test restore procedures before relying on them
- Run system backups before major system changes
- Keep backups in multiple locations for redundancy

## License

MIT License - see LICENSE file for details.

## Contributing

Issues and pull requests are welcome.
