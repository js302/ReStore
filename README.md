# ReStore

[![Download Latest Release](https://img.shields.io/github/v/release/js302/ReStore?label=Download&style=for-the-badge)](https://github.com/js302/ReStore/releases/latest)
[![License](https://img.shields.io/github/license/js302/ReStore?style=for-the-badge)](LICENSE)

A backup and restore solution for Windows that protects your files, programs, and system configuration. ReStore includes both a command-line interface and a modern WPF GUI for managing your backups.

Basically OneDrive but open-source, privacy-focused, highly customizable and with more features.

## Installation

### Download Pre-built Installer (Recommended)

1. **[Download the latest MSIX installer](https://github.com/js302/ReStore/releases/latest)**
2. **Double-click the MSIX file** to install ReStore

### Build from Source

See the [Build from Source](#build-from-source-1) section below.

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
- **Windows Settings**: Backup registry-based settings including personalization, themes, taskbar, File Explorer preferences, regional settings, mouse and keyboard configurations, and accessibility options

### Storage Flexibility

- **Local Storage**: Backup to local drives, external drives, or network shares
- **Cloud Platforms**: Support for Google Drive, Amazon S3, and GitHub storage
- **Multi-destination**: Use different storage backends for different backup types

### Smart File Handling

- **Change Detection**: SHA256 hashing to detect file modifications accurately
- **Compression**: ZIP compression to save storage space
- **History Tracking**: Complete backup history with metadata

### Prerequisites

- Windows 10 version 1809 or later
- ~250MB free disk space

### Build from Source

**Prerequisites:**

- .NET 9.0 SDK or later
- Windows OS (Windows 10 or later recommended)

1. Clone the repository:

   ```bash
   git clone https://github.com/js302/ReStore.git
   cd ReStore
   ```

2. Build the solution:

   ```bash
   dotnet build ReStore.sln
   ```

3. Run the GUI application:

   ```bash
   dotnet run --project ReStore
   ```

## Usage

### GUI Application

Launch the application for a visual interface:

The GUI provides:

- Dashboard with backup statistics and quick actions
- Backup history browser with one-click restore
- Settings page for configuring watch directories, storage providers, and backup options
- System tray support for background operation

**Post-Installation Configuration**:

After installation, visit the Settings page to:

- Enable "Run at Windows Startup" to launch ReStore automatically when your computer boots
- Enable "CLI Access" to add the `restore` command to your system PATH for terminal access

## Configuration

You can configure ReStore through the GUI settings page or by editing the configuration file directly.

### Key Settings

**Watch Directories**: Folders to monitor for automatic backups

**Backup Type**: Choose between Full, Incremental, or Differential

**Backup Interval**: How often to check for changes (in hours)

**Storage Providers**: Configure Local, S3, Google Drive, or GitHub storage

**Exclusions**: File patterns and paths to skip during backup

**Size Limits**: Maximum file size and backup size thresholds

### Configuration File Location

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

### Application Data Location

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

- **General Settings**: Theme, startup options, system tray, CLI access
- **Storage Providers**: Local, Google Drive, AWS S3, GitHub configuration
- **Watch Directories**: Add/remove folders to monitor for automatic backups
- **Backup Configuration**: Type (Full/Incremental/Differential), interval, size limits
- **System Backup**: Enable/disable system state backups, configure program and environment variable backups
- **Exclusions**: File patterns and paths to exclude from backups

- **Backup Data**: `%USERPROFILE%\ReStoreBackups` (default, configurable)

## Storage Provider Setup

ReStore supports multiple storage backends. Below are detailed instructions for setting up each provider.

### Local Storage

Local storage is the simplest option and requires no additional setup beyond specifying the backup directory path.

**Configuration:**

In `%USERPROFILE%\ReStore\config.json`, configure the local storage source:

```json
{
  "storageSources": {
    "local": {
      "path": "%USERPROFILE%\\ReStoreBackups",
      "options": {}
    }
  }
}
```

**Configuration Parameters:**

- **path**: The directory where backups will be stored
- **options**: Empty object (no additional options needed for local storage)

**Notes:**

- The path can be on a local drive, external drive, or network share
- You can use environment variables like `%USERPROFILE%`, `%APPDATA%`, etc.
- Ensure the directory has sufficient free space for your backups
- For network shares, use UNC paths like `\\server\share\backups`

### Google Drive Setup

To use Google Drive as your backup destination, you'll need to create a Google Cloud project and obtain OAuth 2.0 credentials.

#### Step 1: Create a Google Cloud Project

1. Go to the [Google Cloud Console](https://console.cloud.google.com/)
2. Click **"New Project"** (or select an existing project)
3. Name your project (e.g., "ReStore Backups") and click **Create**

#### Step 2: Enable the Google Drive API

1. In your project, navigate to **APIs & Services** > **Library**
2. Search for **"Google Drive API"**
3. Click on it and press **Enable**

#### Step 3: Create OAuth 2.0 Credentials

1. Go to **APIs & Services** > **Credentials**
2. Click **Create Credentials** > **OAuth client ID**
3. If prompted, configure the OAuth consent screen:
   - User Type: **External** (unless you have a Google Workspace)
   - App name: **ReStore** (or your preferred name)
   - User support email: Your email
   - Developer contact: Your email
   - Add scopes: `https://www.googleapis.com/auth/drive.file`
   - Add your email as a test user
4. Back in Credentials, click **Create Credentials** > **OAuth client ID**
5. Application type: **Desktop app**
6. Name: **ReStore Client**
7. Click **Create**
8. Download the JSON file containing your credentials

#### Step 4: Configure ReStore

Open `%USERPROFILE%\ReStore\config.json` and configure the Google Drive section:

```json
{
  "storageSources": {
    "gdrive": {
      "path": "./backups",
      "options": {
        "client_id": "client_id",
        "client_secret": "client_secret",
        "token_folder": "token_folder",
        "backup_folder_name": "ReStore Backups"
      }
    }
  }
}
```

**Configuration Parameters:**

- **path**: Relative path for organizing backups (default: `"./backups"`)
- **client_id**: From the downloaded JSON file (`client_id` field)
- **client_secret**: From the downloaded JSON file (`client_secret` field)
- **token_folder**: Where ReStore stores authentication tokens (recommended: `C:\Users\YourName\ReStore\tokens`)
- **backup_folder_name**: The name of the folder in Google Drive where backups will be stored (default: `"ReStore Backups"`)

#### Step 5: First Authentication

When you run your first backup, ReStore will:

1. Open a browser window for Google authentication
2. Ask you to sign in to your Google account
3. Request permission to access files it creates in Google Drive
4. Save the authentication token for future use

**Notes:**

- The app will only access files it creates, not your existing Drive files
- Tokens are stored locally and can be revoked from your [Google Account settings](https://myaccount.google.com/permissions)
- If the app is in "Testing" mode, you may need to re-authenticate periodically

**Storage Limits (Free Plan):**

- **15 GB** of free storage shared across Google Drive, Gmail, and Google Photos
- Files can be up to **5 TB** in size (but will count against your quota)
- To get more storage, consider [Google One](https://one.google.com/) plans starting at 100 GB

### AWS S3 Setup

To backup to Amazon S3, you need an AWS account and programmatic access credentials.

#### Step 1: Create an AWS Account

1. Go to [AWS Console](https://aws.amazon.com/)
2. Sign up for an AWS account if you don't have one
3. Note: S3 is not free but very affordable for personal backups

#### Step 2: Create an S3 Bucket

1. Sign in to the [AWS Management Console](https://console.aws.amazon.com/)
2. Navigate to **S3** service
3. Click **Create bucket**
4. Configure your bucket:
   - **Bucket name**: Choose a unique name (e.g., `restore-backups-yourname`)
   - **Region**: Choose a region close to you
   - **Block Public Access**: Keep all blocks enabled (recommended)
   - **Versioning**: Enable if you want S3-level version history
   - **Encryption**: Enable default encryption (recommended)
5. Click **Create bucket**

#### Step 3: Create IAM User with S3 Access

1. Navigate to **IAM** service in AWS Console
2. Click **Users** > **Create user**
3. User name: `restore-backup-user`
4. Click **Next**
5. Select **Attach policies directly**
6. Search and select: **AmazonS3FullAccess** (or create a custom policy for specific bucket access)
7. Click **Next** and **Create user**

#### Step 4: Generate Access Keys

1. Click on the newly created user
2. Go to **Security credentials** tab
3. Scroll to **Access keys**
4. Click **Create access key**
5. Use case: **Application running outside AWS**
6. Click **Next** and **Create access key**
7. **Important**: Copy both the **Access Key ID** and **Secret Access Key** immediately (you can't view the secret key again)

#### Step 5: Configure ReStore

Open `%USERPROFILE%\ReStore\config.json` and configure the S3 section:

```json
{
  "storageSources": {
    "s3": {
      "path": "./backups",
      "options": {
        "accessKeyId": "your_access_key_id",
        "secretAccessKey": "your_secret_access_key",
        "region": "your_aws_region",
        "bucketName": "your_bucket_name"
      }
    }
  }
}
```

**Configuration Parameters:**

- **path**: Relative path prefix for organizing backups in the bucket (default: `"./backups"`)
- **accessKeyId**: The Access Key ID from Step 4
- **secretAccessKey**: The Secret Access Key from Step 4
- **region**: The AWS region where your bucket is located (e.g., `us-east-1`, `eu-west-1`, `ap-southeast-2`)
- **bucketName**: The name of your S3 bucket from Step 2

#### Optional: Create a More Restrictive IAM Policy

For better security, create a custom policy that only grants access to your specific bucket:

1. In IAM, go to **Policies** > **Create policy**
2. Select **JSON** and paste:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "s3:PutObject",
        "s3:GetObject",
        "s3:DeleteObject",
        "s3:ListBucket"
      ],
      "Resource": [
        "arn:aws:s3:::restore-backups-yourname",
        "arn:aws:s3:::restore-backups-yourname/*"
      ]
    }
  ]
}
```

3. Replace `restore-backups-yourname` with your bucket name
4. Name the policy (e.g., `ReStoreBackupPolicy`)
5. Attach this policy to your IAM user instead of `AmazonS3FullAccess`

**Notes:**

- Store your access keys securely - they provide full access to your S3 bucket
- Consider enabling S3 bucket versioning for additional protection
- Monitor your S3 usage in the AWS Console to track costs
- Set up lifecycle policies to automatically delete old backups and reduce costs

**Storage Limits & Pricing:**

- **AWS Free Tier** (first 12 months): 5 GB of S3 Standard storage, 20,000 GET requests, 2,000 PUT requests per month
- **After Free Tier**: Pay-as-you-go pricing (typically $0.023 per GB/month for Standard storage in US East)
- **No file size limit**: Individual objects can be up to 5 TB
- **Cost Example**: 100 GB of backups ≈ $2.30/month
- Consider using **S3 Glacier** for long-term archival at ~$0.004 per GB/month (much cheaper but slower retrieval)

### GitHub Storage Setup

Use GitHub as a storage backend to leverage version control for your backups. This is best suited for configuration files and smaller backups.

#### Step 1: Create a GitHub Account

1. Go to [GitHub](https://github.com/)
2. Sign up for a free account if you don't have one

#### Step 2: Create a Private Repository

1. Click the **+** icon in the top-right corner
2. Select **New repository**
3. Configure your repository:
   - **Repository name**: `restore-backups` (or your preferred name)
   - **Visibility**: **Private** (recommended for backups)
   - **Initialize**: You can add a README if desired
4. Click **Create repository**

#### Step 3: Generate a Personal Access Token (Classic)

1. Click your profile picture > **Settings**
2. Scroll down to **Developer settings** (in the left sidebar)
3. Click **Personal access tokens** > **Tokens (classic)**
4. Click **Generate new token** > **Generate new token (classic)**
5. Configure the token:
   - **Note**: `ReStore Backup Access`
   - **Expiration**: Choose an expiration period (or No expiration)
   - **Scopes**: Select **repo** (Full control of private repositories)
6. Click **Generate token**
7. **Important**: Copy the token immediately (you won't be able to see it again)

#### Alternative: Fine-grained Personal Access Token

For better security, use a fine-grained token:

1. Go to **Personal access tokens** > **Fine-grained tokens**
2. Click **Generate new token**
3. Configure:
   - **Token name**: `ReStore Backup`
   - **Expiration**: Choose duration
   - **Repository access**: Only select repositories > Choose your backup repository
   - **Permissions** > **Repository permissions**:
     - **Contents**: Read and write
     - **Metadata**: Read-only (automatically granted)
4. Click **Generate token** and copy it

#### Step 4: Configure ReStore

Open `%USERPROFILE%\ReStore\config.json` and configure the GitHub section:

```json
{
  "storageSources": {
    "github": {
      "path": "./backups",
      "options": {
        "token": "your_token",
        "repo": "your_repo",
        "owner": "your_github_username"
      }
    }
  }
}
```

**Configuration Parameters:**

- **path**: Relative path for organizing backups in the repository (default: `"./backups"`)
- **token**: The personal access token you generated in Step 3
- **repo**: The name of your backup repository
- **owner**: Your GitHub username

**Notes:**

- GitHub has file size limits
- This storage option is best for configuration files, scripts, and smaller backups
- For large file backups, consider using Git LFS or another storage provider
- Keep your token secure - it provides write access to your repository
- You can revoke tokens at any time from GitHub Settings > Developer settings
- Private repositories are free on GitHub for unlimited users

**Storage Limits (Free Plan):**

- **Repository size**: No hard limit, but recommended to keep under 1 GB
- **File size**: 100 MB maximum per file (warning at 50 MB)
- **Free tier**: 2 GB of Git LFS storage and 1 GB of bandwidth per month
- **Best practice**: Use GitHub for configuration backups and smaller files; use Google Drive or S3 for large file backups

### Using Multiple Storage Providers

You can configure multiple storage sources in the same `config.json` file. ReStore supports all four storage providers simultaneously:

```json
{
  "storageSources": {
    "gdrive": {
      "path": "./backups",
      "options": {
        "client_id": "your_client_id",
        "client_secret": "your_client_secret",
        "token_folder": "your_token_folder",
        "backup_folder_name": "ReStore Backups"
      }
    },
    "s3": {
      "path": "./backups",
      "options": {
        "accessKeyId": "your_access_key_id",
        "secretAccessKey": "your_secret_access_key",
        "region": "your_aws_region",
        "bucketName": "your_bucket_name"
      }
    },
    "github": {
      "path": "./backups",
      "options": {
        "token": "your_token",
        "repo": "your_repo",
        "owner": "your_github_username"
      }
    },
    "local": {
      "path": "%USERPROFILE%\\ReStoreBackups",
      "options": {}
    }
  }
}
```

**Usage:**
When using the CLI, specify which storage provider to use with commands:

```bash
# Backup to local storage
restore backup local "C:\Users\YourName\Documents"

# Backup to Google Drive
restore backup gdrive "C:\Users\YourName\Documents"

# Backup to AWS S3
restore backup s3 "C:\Users\YourName\Documents"

# Backup to GitHub
restore backup github "C:\Users\YourName\Documents"
```

In the GUI, you can select the storage provider from the Settings page.

### Application Behavior Settings

**Run at Windows Startup**: Configure ReStore to automatically launch when Windows starts. This feature adds an entry to the Windows Registry (`HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`). By default, this is disabled and can be toggled from the Settings page.

**System Tray Integration**: When minimized, ReStore can run in the system tray, allowing you to keep the file watcher active in the background without cluttering your taskbar.

**CLI Access Management**: Enable or disable command-line access to ReStore from any terminal:

- **Enable**: Adds the ReStore CLI folder to your user PATH environment variable, allowing you to run `restore` commands from anywhere
- **Disable**: Removes the CLI folder from your PATH
- **Location**: The CLI executable is bundled with the GUI installation

### Example usage after enabling CLI access:

#### Start File Watcher

Monitor directories for changes and backup automatically:

```bash
restore --service local
```

#### Manual Backup

Backup a specific directory:

```bash
restore backup local "C:\Users\YourName\Documents"
```

#### Restore Files

Restore from a backup:

```bash
restore restore local "backups/Documents/backup_Documents_20250817120000.zip" "C:\Restore\Documents"
```

#### System Backup

Backup installed programs, environment variables, and Windows settings:

```bash
restore system-backup local all
restore system-backup local programs
restore system-backup local environment
restore system-backup local settings
```

#### System Restore

Restore system components:

```bash
restore system-restore local "system_backups/programs/programs_backup_<timestamp>.zip" programs
restore system-restore local "system_backups/environment/env_backup_<timestamp>.zip" environment
restore system-restore local "system_backups/settings/settings_backup_<timestamp>.zip" settings
```

## System Backup

ReStore can backup your Windows system configuration, including installed programs, environment variables, and Windows settings.

### Program Backup

ReStore scans for installed programs using Winget and Windows Registry. It creates:

- A complete list of installed software
- Automated restoration scripts for Winget-compatible programs
- Manual installation lists for programs not available through Winget

### Environment Variables

Backup both user and system environment variables. The restore process generates PowerShell scripts you can run to restore your environment configuration.

### Windows Settings Backup

ReStore backs up Windows registry settings across multiple categories:

- **Personalization**: Themes, wallpaper, colors, accent colors, transparency effects, and Desktop Window Manager settings
- **File Explorer**: View settings, folder options, navigation pane configuration, and recently used items preferences
- **Regional Settings**: Date and time formats, number formats, currency settings, and language preferences
- **Taskbar**: Taskbar position, size, search box configuration, and system tray settings
- **Mouse and Keyboard**: Pointer speed, double-click speed, cursor schemes, and keyboard repeat settings
- **Accessibility**: High contrast themes, screen reader settings, and other accessibility features
- **System Settings**: Time zone configuration and power management settings (requires administrator privileges)

The backup process exports registry keys to .reg files and generates a PowerShell restore script. Some settings require administrator privileges to restore, and the restore script will automatically detect permission levels and restore what it can.

### Generated Files

When you perform a system backup, ReStore creates:

**Programs Backup:**

- `restore_winget_programs.ps1` - Automated program installation
- `manual_install_list.txt` - Programs needing manual installation
- `installed_programs.json` - Complete program inventory

**Environment Variables Backup:**

- `restore_environment_variables.ps1` - Environment variable restoration
- `environment_variables.json` - Environment variables data
- `backup_env_registry.ps1` - Registry backup script

**Windows Settings Backup:**

- `restore_windows_settings.ps1` - Settings restoration script
- `settings_manifest.json` - Metadata about exported settings
- Multiple `.reg` files - Registry exports for each setting category

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
├── ReStore.Core/             # Core CLI application
│   ├── Program.cs            # CLI entry point
│   ├── src/
│   │   ├── core/             # Backup, restore, and state management
│   │   ├── storage/          # Storage provider implementations
│   │   ├── monitoring/       # File watching and change detection
│   │   ├── utils/            # Configuration, logging, and utilities
│   │   └── backup/           # System backup functionality
│   └── config/               # Configuration files
│
└── ReStore/                  # WPF GUI application
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
- Windows settings backups contain registry exports that can modify system behavior
- Administrator privileges may be required for some restore operations, particularly for system-level settings
- Always review restore scripts before executing them, especially when restoring Windows settings

### Limitations

- System backup is Windows-only
- Automatic program restoration requires Winget
- Some programs may install newer versions than what was backed up
- Windows settings backup captures registry-based settings only; some settings stored in other locations may not be included
- Hardware-specific settings are backed up but may not be appropriate to restore on different hardware
- WiFi passwords and network credentials are not included in the settings backup

### Best Practices

- Test restore procedures before relying on them
- Run system backups before major system changes
- Create a Windows system restore point before restoring Windows settings
- Keep backups in multiple locations for redundancy
- Review Windows settings restore scripts before running them
- When restoring to a different computer, selectively restore settings rather than restoring everything

## License

AGPL-3.0 License - see LICENSE file for details.

## Contributing

Issues and pull requests are welcome.
