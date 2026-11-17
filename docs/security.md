# Security & Best Practices

## Security

- **Encryption**: Enable AES-256-GCM encryption to protect sensitive backups with password protection
- **Password Storage**: Your encryption password is never stored - only the salt and a password verification token are saved in `config.json`. Neither reveals the password, but both are used to check it
- **Credentials**: Provider secrets (passwords, keys, tokens) can be stored as `dpapi:` values (Windows DPAPI, current user) or read from the environment with `env:NAME`; see [Secrets in config.json](configuration.md#secrets-in-configjson). Plain values still work but are readable by anyone who can read the file
- **Google Drive tokens**: The sign-in token cache is encrypted with DPAPI on Windows (see [Google Drive](storage/providers/google-drive.md))
- **Environment Variables**: May include sensitive information like API keys - consider enabling encryption
- **Windows Settings**: Backups contain registry exports that can modify system behavior
- **Administrator Privileges**: May be required for some restore operations, particularly for system-level settings
- **Script Review**: Always review restore scripts before executing them, especially when restoring Windows settings
- **Lost Passwords**: There is no password recovery mechanism - encrypted backups are unrecoverable without the password

## Best Practices

- **Config backups hold the same secrets as config.json.** Every `config set`, `import` or migration keeps the previous file under
  `%USERPROFILE%\ReStore\backups\config.pre-*.json` (the newest 20). Literal secret options in them are protected by `restore config protect`
  (or `--protect`) just like in `config.json`, but a plain-text secret you never protected stays plain text in those copies: protect it, then delete old copies you no longer need.

- **Enable Encryption**: Protect sensitive data with AES-256-GCM encryption and a strong password
- **Secure Your Password**: Store your encryption password in a password manager and write it down in a secure location
- **Test Restores**: Verify you can restore and decrypt backups before you need them in an emergency
- **System Backups**: Run before major system changes
- **Multiple Locations**: Keep backups in multiple locations for redundancy (local + cloud)
- **System Restore Points**: Create a Windows system restore point before restoring Windows settings
- **Review Scripts**: Review Windows settings restore scripts before running them
- **Selective Restoration**: When restoring to a different computer, selectively restore settings rather than restoring everything

## Encrypted v3 repositories: operational notes

- **Key file copies.** The repository key file exists twice, always written together: `keys/repository.keys.json` and the mirror
  `keys/repository.keys.1.json`. A read falls back to the mirror when the primary is missing or damaged and repairs it; `repair`
  (not `--dry-run`) restores a missing key file from the mirror. Neither copy holds a plaintext secret (only wrapped slots), so keep
  an offline copy as well: `restore export-key <file>` / `restore import-key <file>` (Settings offers it after a password change).
  If both copies and your offline copy are lost, encrypted v3 data cannot be read.
- **Password change is not atomic with the config.** `change-password` first rewrites the key file (verified by reading it back), then
  updates this computer's configured verification token. If it stops in between, the key file already has the new password and this
  computer's token the old one: running `change-password` again is not possible (the old password no longer opens the key file). Instead
  enter the new password when prompted (or set `RESTORE_ENCRYPTION_PASSWORD`); the new password opens the key file and the token is re-created from it.
  If the key-file write is interrupted before the read-back succeeds, the old password still works; retry.
- **A second computer adopting a new password.** Other computers that back up to the same storage keep their old configured password,
  which no longer opens the key file: their backups stop with "does not unlock this destination's repository key file" and never replace
  it. On each of them, enter the new password in Settings (or via `RESTORE_ENCRYPTION_PASSWORD`) so the verification token matches.
  Two computers creating the key file at the same moment: the loser aborts before uploading anything and the retry uses the stored file.
- **What the storage provider can still see** with encrypted v3: the last name of each backed-up folder (`snapshots/<name>_<hash>/`),
  object sizes (chunk sizes and the number of chunks can fingerprint large known files), and the number and times of snapshots (file
  names and HEAD updates). File paths, names, sizes, dates and content hashes inside manifests are hidden.
- **Manifests are bound to their location**: the clear header carries the snapshot id and group folder key and is authenticated, so a
  manifest copied over another snapshot's file (same or another group) is refused.
- **Key derivation floor**: password slots use at least 100,000 PBKDF2 iterations (lower requests are raised); a key file with fewer is refused.
- **Mixed v2 and v3 snapshots in one group**: file content hashes differ between the schemes (SHA-256 vs keyed HMAC), so a diff or version
  history across a v2 and a v3 snapshot shows every file as changed.
- **Residual: first backup from two computers at once to one cloud destination.** Only local storage creates the key file atomically.
  Other providers upload and read back, and every backup re-reads the key file right before it commits the manifest and HEAD: if it is no
  longer the one the snapshot was encrypted with, the backup stops with nothing committed (chunks already uploaded are left for `repair`),
  and the next run uses the stored key file. A window of a few requests remains in which two computers can both create a key file; the
  loser always detects it before committing, but its chunks are unreadable garbage until `repair` removes them. Provider conditional creates
  (S3 `If-None-Match`, Azure `If-None-Match: *`, GCS `ifGenerationMatch=0`) are not used yet.

## Toast notification actions

Windows toasts (packaged builds) use the `restore-toast:` protocol for their buttons, and any web page can invoke a registered protocol. Every toast URI therefore carries a random 128-bit per-process token (`t=`), generated at startup and kept in memory only. An activation runs its action (Retry now, Back up now, Unlock, Open log, View report) only when the token matches; a missing, wrong or stale token (an external link, or a toast clicked after ReStore restarted) just brings the window forward or opens the target page, with no backup and no password prompt. Ignored activations are logged at Debug.
