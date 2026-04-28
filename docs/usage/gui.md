# GUI Application Usage

Launch the application for a visual interface:

The GUI provides:

- **Dashboard**: Real-time statistics, backup history, and quick actions
- **Backup Browser**: View and manage backup history with restore, delete, and snapshot verification actions
- **Settings**: Configure watch directories, storage providers, and backup options
- **System Tray**: Minimize to tray and control the file watcher in the background

## Snapshot Verification in Backup Browser

From **Backup Browser**, each snapshot backup row includes a **Verify Snapshot** action.

- Verification is available for snapshot manifest and HEAD artifacts only
- The check validates manifest integrity, chunk presence/hash/size, and reconstructed file hashes
- Verification results are shown in the UI and telemetry counters are persisted to `system_state.json`

## Post-Installation Configuration

After installation, visit the Settings page to:

- Enable "Run at Windows Startup" to launch ReStore automatically when your computer boots
- Configure your storage providers
- Add directories to watch
