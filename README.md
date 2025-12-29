# ReStore

[![Download Latest Release](https://img.shields.io/github/v/release/js302/ReStore?label=Download&style=for-the-badge)](https://github.com/js302/ReStore/releases/latest)
[![License](https://img.shields.io/github/license/js302/ReStore?style=for-the-badge)](LICENSE)

A backup and restore solution for Windows that protects your files, programs, and system configuration. ReStore includes both a command-line interface and a modern WPF GUI for managing your backups.

Basically OneDrive but open-source, privacy-focused, highly customizable and with more features.

## Documentation

- **[Installation](docs/installation.md)**: Installation instructions and building from source.
- **[Features](docs/features.md)**: Detailed overview of ReStore's capabilities.
- **[Configuration](docs/configuration.md)**: Settings, config files, and retention policies.
- **[Storage Providers](docs/storage/README.md)**: Setup guides for all supported storage backends.
  - [Local](docs/storage/providers/local.md), [Google Drive](docs/storage/providers/google-drive.md), [AWS S3](docs/storage/providers/aws-s3.md), [GitHub](docs/storage/providers/github.md), [Azure](docs/storage/providers/azure.md), [GCP](docs/storage/providers/google-cloud-storage.md), [Dropbox](docs/storage/providers/dropbox.md), [SFTP](docs/storage/providers/sftp.md), [Backblaze B2](docs/storage/providers/backblaze-b2.md)
- **[Usage](docs/usage/gui.md)**: How to use the GUI and CLI.
  - [GUI Guide](docs/usage/gui.md)
  - [CLI Guide](docs/usage/cli.md)
  - [File Sharing](docs/usage/file-sharing.md)
- **[System Backup](docs/system-backup.md)**: Backup programs, environment variables, and Windows settings.
- **[Encryption](docs/encryption.md)**: Security features, password protection, and technical details.
- **[Security & Best Practices](docs/security.md)**: Important security notes and recommendations.
- **[Development](docs/development.md)**: Project structure and testing.
- **[Limitations](docs/limitations.md)**: Known limitations.

## Quick Start

### Installation

1. **[Download the latest MSIX installer](https://github.com/js302/ReStore/releases/latest)**
2. **Double-click the MSIX file** to install ReStore.
3. **Optional**: To enable file sharing via the context menu in Windows Explorer, download and run the `register-context-menu.ps1` script from the release. See [File Sharing Docs](docs/usage/file-sharing.md) for details.

For building from source, see [Installation Docs](docs/installation.md).

### Key Features

- **Modern GUI**: Dashboard, backup browser, and settings management.
- **Multi-Cloud Support**: Backup to S3, Google Drive, Azure, Dropbox, and more.
- **System Backup**: Save installed programs, environment variables, and Windows settings.
- **Smart Backup**: Incremental/Differential backups, compression, and deduplication.
- **Encryption**: AES-256-GCM encryption for secure backups.
- **File Sharing**: Right-click to share files via your cloud storage.

## License

AGPL-3.0 License - see [LICENSE](LICENSE) file for details.

## Contributing

Issues and pull requests are welcome.
