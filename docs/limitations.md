# Limitations

- System backup is Windows-only
- Automatic program restoration requires Winget
- Some programs may install newer versions than what was backed up
- Windows settings backup captures registry-based settings only; some settings stored in other locations may not be included
- WiFi passwords and network credentials are not included in the settings backup
- User-file backups use snapshot manifests and deduplicated chunk objects; restoring from the older archive format is not supported by this flow
- Verification downloads one chunk at a time. Restore caches up to 64 MB of decrypted chunks and writes each file as it goes. A single file larger than `maxFileSizeMB` is skipped at backup time rather than streamed
- User-file chunks are not compressed before encryption; only system backup archives are zipped
- Share links are created with a fixed 7-day expiry (Dropbox links do not expire) and the shared file is uploaded unencrypted
- Restore conflict handling "abort" (`--conflict fail`) exists only in the CLI
- GitHub storage buffers each upload in memory as base64 (peak ≈ 2.3× file size) and is capped at GitHub's 100 MB per-file API limit; prefer object storage for large backups
- Only one operation that changes a repository (backup, restore of system state, label, delete, repair) runs at a time per machine. The watcher and scheduler skip a cycle and retry later when it is busy; the GUI and CLI wait for their turn or report which operation holds the lock
- `repair` cannot fully close the race with a long-running backup on another machine sharing the same bucket/prefix; run it when other machines are idle (see the CLI guide)
- MSIX startup can be turned off by the user in Windows Settings > Apps > Startup; ReStore shows that state but cannot override it
