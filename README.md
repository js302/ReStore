# ReStore

[![Download Latest Release](https://img.shields.io/github/v/release/js302/ReStore?label=Download&style=for-the-badge)](https://github.com/js302/ReStore/releases/latest)
[![License](https://img.shields.io/github/license/js302/ReStore?style=for-the-badge)](LICENSE)

A backup and restore solution for Windows that protects your files, programs, and system configuration. ReStore includes both a command-line interface and a modern WPF GUI for managing your backups.

Basically OneDrive but open-source, privacy-focused, highly customizable and with more features.

## Quick Start

### Installation

1. **[Download the latest MSIX installer](https://github.com/js302/ReStore/releases/latest)**
2. **Double-click the MSIX file** to install ReStore.

For building from source, see [Installation Docs](docs/installation.md).

### Key Features

- **Modern GUI**: Dashboard, backup browser, and settings management.
- **Multi-Cloud Support**: Backup to S3, Google Drive, Azure, Dropbox, and more.
- **CLI Access**: Full command-line interface for scripting and automation.
- **System Backup**: Save installed programs, environment variables, and Windows settings.
- **Smart Backup**: File backups are chunk snapshots — a point-in-time manifest plus deduplicated chunk objects, so unchanged data is never re-uploaded.
- **Disaster Recovery**: Reconnect to backups already in your storage from a new PC, adopt them into this PC's history, and repair leftover data.
- **Restore Safety**: Every restore previews what it would touch first, and never replaces an existing file unless you ask it to.
- **Encryption**: AES-256-GCM encryption for secure backups.
- **File Sharing**: Right-click in file explorer to share files via your cloud storage.

### Use ReStore from an AI agent

An agent can install and drive ReStore from the command line: `irm https://github.com/js302/ReStore/releases/latest/download/install.ps1 | iex`
installs the portable CLI (no admin rights). Then `restore agent-guide` prints the guide (rules, exit codes, recipes; also in
[docs/agents.md](docs/agents.md)), `restore doctor --json` checks the setup and `restore commands --json` lists every command.
Everything supports `--json` and `--non-interactive`. See also [llms.txt](llms.txt).

## Documentation

**[Installation](docs/installation.md)**: Installation instructions and building from source.

**[Features](docs/features.md)**: Detailed overview of ReStore's capabilities.

**[Configuration](docs/configuration.md)**: Settings, config files, and retention policies.

**[AI agents](docs/agents.md)**: A short guide for agents that operate ReStore on a user's behalf.

**[Updates](docs/updates.md)**: How update checks work, what is contacted, and how to install or turn them off.

**[Storage Providers](docs/storage/README.md)**: Setup guides for all supported storage backends.

- [Local](docs/storage/providers/local.md)
- [Google Drive](docs/storage/providers/google-drive.md)
- [AWS S3](docs/storage/providers/aws-s3.md)
- [GitHub](docs/storage/providers/github.md)
- [Azure](docs/storage/providers/azure.md)
- [GCP](docs/storage/providers/google-cloud-storage.md)
- [Dropbox](docs/storage/providers/dropbox.md)
- [SFTP](docs/storage/providers/sftp.md)
- [Backblaze B2](docs/storage/providers/backblaze-b2.md)

**[Usage](docs/usage/gui.md)**: How to use the GUI and CLI.

- [GUI Guide](docs/usage/gui.md)
- [CLI Guide](docs/usage/cli.md)
- [File Sharing](docs/usage/file-sharing.md)

**[System Backup](docs/system-backup.md)**: Backup programs, environment variables, and Windows settings.

**[Encryption](docs/encryption.md)**: Security features, password protection, and technical details.

**[Security & Best Practices](docs/security.md)**: Important security notes and recommendations.

**[Telemetry](docs/telemetry.md)**: What anonymous usage statistics are collected, and how to turn them off.

**[Release notes](docs/release-notes/README.md)**: every release; [1.8.0](docs/release-notes/1.8.0.md) lists what is new and what behaves differently.

**[Architecture](docs/architecture/README.md)**: Technical architecture and component diagrams.

- [Components](docs/architecture/components.md)
- [Data Flows](docs/architecture/data-flows.md)
- [Chunk & Manifest Model](docs/architecture/chunk-manifest.md)
- [Encryption Architecture](docs/architecture/encryption.md)
- [Configuration & State](docs/architecture/configuration-state.md)
- [Technology Stack](docs/architecture/technology-stack.md)

**[Development](docs/development.md)**: Project structure and testing.

**[Limitations](docs/limitations.md)**: Known limitations.

**Upgrading from 1.6:** version 1.7.0 changed chunk boundaries, so the first 1.7 backup of each folder re-chunks and re-uploads everything once. See the [release note in the chunk model](docs/architecture/chunk-manifest.md).

**Upgrading from 1.7:** protection auto-starts for configs with watch directories (`protection.autoStart`, set `false` to opt out), the backup format stays v2 until you choose "Upgrade backup format", and the first run re-hashes every file once. See [Upgrading from 1.7](docs/configuration.md#upgrading-from-17).

## License

AGPL-3.0 License - see [LICENSE](LICENSE) file for details.

## Privacy Policy

Privacy policy: ReStore can send anonymous usage statistics; what is collected and how to turn it off is described in
[docs/telemetry.md](docs/telemetry.md). Update checks contact GitHub only ([docs/updates.md](docs/updates.md)). Apart from that, this
program will not transfer any information to other networked systems unless specifically requested by the user or the person
installing or operating it (for example the backup storage they configure).

## Contributing

Issues and pull requests are welcome.
