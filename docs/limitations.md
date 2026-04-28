# Limitations

- System backup is Windows-only
- Automatic program restoration requires Winget
- Some programs may install newer versions than what was backed up
- Windows settings backup captures registry-based settings only; some settings stored in other locations may not be included
- WiFi passwords and network credentials are not included in the settings backup
- User-file backups now use snapshot manifests and deduplicated chunk objects; archive compatibility for user-file restores is not part of this flow
- `ReStore.Core/src/core/DiffManager.cs` remains an experimental prototype and is not wired into production backup/restore flow
