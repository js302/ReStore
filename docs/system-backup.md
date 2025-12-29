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
