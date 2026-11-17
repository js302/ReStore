# System Backup

ReStore can backup your Windows system configuration, including installed programs, environment variables, and Windows settings.

## Program Backup

ReStore scans for installed programs using Winget and Windows Registry. It creates:

- A complete list of installed software
- Automated restoration scripts for Winget-compatible programs
- Manual installation lists for programs not available through Winget

## Environment Variables

Backup both user and system environment variables. The restore process generates PowerShell scripts you can run to restore your environment configuration.

## Windows Settings Backup

ReStore backs up Windows registry settings across multiple categories:

- **Personalization**: Themes, wallpaper, colors, accent colors, transparency effects, and Desktop Window Manager settings
- **File Explorer**: View settings, folder options, navigation pane configuration, and recently used items preferences
- **Regional Settings**: Date and time formats, number formats, currency settings, and language preferences
- **Taskbar**: Taskbar position, size, search box configuration, and system tray settings
- **Mouse and Keyboard**: Pointer speed, double-click speed, cursor schemes, and keyboard repeat settings
- **Accessibility**: High contrast themes, screen reader settings, and other accessibility features
- **System Settings**: Time zone configuration and power management settings (requires administrator privileges)

The backup process exports registry keys to .reg files and generates a PowerShell restore script. Some settings require administrator privileges to restore, and the restore script will automatically detect permission levels and restore what it can.

## Developer Tools Backup (opt-in)

Off by default. Enable **Include Developer Tools** in Settings (`systemBackup.includeDevTools`, CLI: `restore.exe system-backup devtools`). It backs up, when present:

- Windows Terminal `settings.json` (Store, Preview and unpackaged installs)
- PowerShell profiles for Windows PowerShell and PowerShell 7 (`profile.ps1`, `Microsoft.PowerShell_profile.ps1`, `Microsoft.VSCode_profile.ps1`, and the ISE profile for Windows PowerShell)
- VS Code and VS Code Insiders `settings.json`, `keybindings.json` and the `snippets` folder (`.json` / `.code-snippets` files)
- The VS Code / Insiders extension list (`code --list-extensions`; skipped quietly when `code` is not on PATH)
- The global git config `%USERPROFILE%\.gitconfig`
- Optionally `%USERPROFILE%\.ssh\config` (**Include SSH Config**, `systemBackup.includeSshConfig`, off by default)

**Private keys are never backed up.** Nothing else in `.ssh` is read (no `id_*`, `known_hosts`, `authorized_keys`); only the single `config` file, and only when you opt in. Files over 10 MB are skipped. Settings may still contain things you consider private (tokens in a git config or profile), so enable encryption for the repository if the storage is not trusted.

The archive (`system_backups/devtools/devtools_backup_*.zip`, encrypted like the other components when encryption is on) holds `devtools_manifest.json` (every item with its source path, size and SHA-256) and the files under `files/`. The storage type can be overridden with `systemBackup.devToolsStorageType`.

**Restore** goes through the same plan, preview and undo flow as the other components (`restore.exe system-restore <backup> devtools --plan` prints the plan):

- Each file is shown as create, replace (with a size and changed-line count) or unchanged.
- Each extension is shown as install, already installed or manual (when `code` is missing).
- Targets are resolved from the item id against this machine's folders; the source path in the manifest is informational only, and a file whose checksum does not match is rejected.
- Before anything is written, the current files are saved to an undo point (`restore.exe system-undo`). Undo puts the previous content back and removes files the restore created. Extensions are not uninstalled by undo; they are listed.
- Extensions are installed only when selected, with `code --install-extension <id>` after the id is validated (`publisher.name`, letters, digits, `-`, `_`).
- PowerShell profiles and SSH config can execute commands, so review those rows in the preview before restoring them.

## Generated Files

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

**Developer Tools Backup:**

- `devtools_manifest.json` - Items (id, source path, size, SHA-256) and extension ids
- `files/NNN.dat` - The backed-up files
