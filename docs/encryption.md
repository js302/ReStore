# Backup Encryption

ReStore encrypts backups with AES-256-GCM under a password you choose. This covers both file
backups and system backups.

## Encryption Features

- **Algorithm**: AES-256-GCM (Galois/Counter Mode) - provides both confidentiality and authentication
- **Key Derivation**: PBKDF2-SHA256 with 1,000,000 iterations for secure password-based key generation
- **Authenticated Encryption**: Built-in authentication prevents tampering with encrypted backups
- **Artifact Format**:
  - User-file backups: encrypted deterministic chunk objects referenced by snapshot manifests
  - System backups: encrypted `.enc` archives with `.enc.meta` metadata files

The two artifact types protect their payloads differently:

- **Chunk objects** derive a per-chunk key and IV from the master key and the chunk's own
  content hash (HMAC-SHA256). Because identical plaintext always yields identical ciphertext,
  deduplication keeps working on encrypted backups — which a random IV per chunk would break.
- **System archives** use a per-archive random Data Encryption Key, itself wrapped with a Key
  Encryption Key derived from your password. The wrapped key and IV live in the `.enc.meta`
  file beside the archive.

## Enabling Encryption

**Via GUI (Recommended):**

1. Open the **Settings** page
2. Expand the **Encryption** section
3. Click **Enable Encryption**
4. Enter a strong password (minimum 8 characters)
5. Confirm the password
6. View the password strength indicator (Weak/Medium/Strong with color coding)
7. Click **Enable Encryption** to save

**Important**: Store your encryption password securely - it **CANNOT be recovered** if lost!

**Via the command line (scripts and AI agents):**

```bash
echo %BACKUP_PASSWORD% | restore encryption enable --password-stdin     # or RESTORE_ENCRYPTION_PASSWORD / ..._FILE
restore encryption status --json
restore unlock --remember --password-stdin                              # Windows: let scheduled runs use the password
restore init --storage local --path D:\Backups --watch D:\Data --encrypt --password-stdin   # first-time setup in one command
```

`encryption enable` follows the same rules as the Settings page (at least 8 characters, a weak password is accepted with a warning)
and refuses when encryption is already on; without a terminal and without a password source it exits `4`. After the first backup keep an
offline copy of the key file (`restore export-key <file>`).

`restore unlock --remember` verifies the password and stores it protected with your Windows user's DPAPI key
(`%USERPROFILE%\ReStore\state\remembered-password.bin`). Later CLI commands, `run-due` from Task Scheduler and `--service` then work
without `RESTORE_ENCRYPTION_PASSWORD`. Anyone running as your Windows user can use it (the same trust model as `dpapi:` secrets in
`config.json`); `restore lock` removes it, `change-password` updates it, and one that no longer matches the configured password is
ignored with a warning. See [Remembering the password](usage/cli.md#remembering-the-password).

**Via Configuration File:**

Edit `%USERPROFILE%\ReStore\config.json`:

```json
{
  "encryption": {
    "enabled": true,
    "salt": "base64_encoded_salt_will_be_generated",
    "keyDerivationIterations": 1000000,
    "verificationToken": "base64_encoded_verification_token_will_be_generated"
  }
}
```

When you first enable encryption, the application will generate a random salt and a verification token automatically.
Note: Do NOT change the salt after enabling encryption, as it is required for key derivation.

## How It Works

1. **First Time Setup**: When you enable encryption, you set a master password and a random salt is generated
2. **During Backup**:

- A KEK is derived from your password + salt
- User-file backups are chunked and chunks are encrypted deterministically using AES-256-GCM-derived per-chunk material
- Snapshot manifests reference encrypted chunk objects
- System backups continue to use encrypted archives (`.enc`) with metadata (`.enc.meta`)

3. **During Restore**:

- You're prompted for your password
- The KEK is derived from your password + stored salt
- User-file restore decrypts and validates chunks before reconstructing files
- System backup restore decrypts archive payloads and then restores content

## Password Requirements

- Minimum 8 characters
- Recommended: Mix of uppercase, lowercase, numbers, and special characters
- The GUI provides real-time strength feedback with color indicators:
  - **Red (Weak)**: Basic password, easy to crack
  - **Orange (Medium)**: Decent password, consider adding more complexity
  - **Green (Strong)**: Excellent password with good complexity

## Encrypted Backup Behavior

**What Gets Encrypted:**

- **All user-file snapshots**: Full, Incremental, and ChunkSnapshot backup modes
- **System component backups**:
  - Installed programs list and Winget restore scripts
  - Environment variables (user and system)
  - Windows registry settings (personalization, taskbar, File Explorer, etc.)
- **Everything**: Once encryption is enabled, all new backups (file and system) are automatically encrypted

**Restoration:**

- Encrypted backups (`.enc` files) automatically trigger password prompts
- Both GUI and CLI support decryption during restore
- Password is only required when restoring encrypted backups

## Disabling Encryption

To disable encryption:

1. Open **Settings** > **Encryption**
2. Click **Disable Encryption**
3. Confirm the action

From the command line: `restore encryption disable --yes` (`--yes` is required without a terminal).

**Note**: Disabling encryption only affects new backups. Existing encrypted backups will still require the password to restore.

## Security Best Practices

1. **Use a strong, unique password** - Don't reuse passwords from other services
2. **Store your password securely** - Use a password manager like Bitwarden or KeePass
3. **Backup your password** - Write it down and store it in a secure physical location
4. **Test restoration** - Verify you can decrypt and restore backups before you need them
5. **Multiple storage locations** - Keep encrypted backups in multiple places (local + cloud)
6. **Regular password updates** - Consider changing your encryption password periodically
7. **Secure your config** - The salt and the password verification token are stored in `config.json` - keep this file secure

## Encryption Performance

- **Overhead**: Minimal impact on backup speed due to efficient AES-256-GCM implementation
- **Compression**: System backup archives are zipped before encryption. User-file chunks are encrypted without compression, so compressible data is not shrunk on the remote
- **Large Files**: Encryption uses streaming with small chunks, ensuring low memory usage even for very large files

## Risk Scenarios

**Scenario 1**: Config file lost, but snapshot manifests and system `.enc.meta` files intact

- Existing encrypted snapshots and system backups remain recoverable (salt/metadata is present in artifacts)
- New encrypted backups require re-establishing encryption settings in config

**Scenario 2**: Required metadata or chunk artifact is missing

- Missing system `.enc.meta` files make corresponding encrypted system archives unrecoverable
- Missing snapshot chunk objects make corresponding files unrecoverable

**Scenario 3**: Config salt modified accidentally

- Existing encrypted backups remain restorable if artifact metadata is intact
- New backups may be created with incompatible key material
- This is a data corruption scenario

## Encrypted v3 repositories (key file, hidden metadata, password change)

New encrypted backup sets, and existing ones after "Upgrade backup format" (`chunkDiffing.manifestVersion: 3`), use the v3 scheme:

- **Key file** `keys/repository.keys.json` (one per storage): a random 256-bit data key wrapped (AES-256-GCM) by a key-encryption
  key derived with PBKDF2-SHA256 from your password, in one or more **key slots**; a recovery-key slot is optional. The file
  holds no secret. **Keep a copy of it: without it, encrypted v3 data cannot be read.**
- **Sub-keys** (HKDF-SHA256 from the data key): manifest key, chunk-id key, content-hash key, chunk key.
- **Manifests** are `RESTORE-EM1` envelopes: a clear header (version, snapshot id, key file id, key check) and an
  AES-256-GCM body (gzip JSON) authenticated together with the header. File paths, sizes, times and hashes are not visible.
  `group.json` is encrypted under the manifest key too. Object names still carry the backed-up folder's last name
  (`snapshots/<name>_<hash>/`) and chunk/object sizes; that is a known residual.
- **Chunk ids and file content hashes** are HMAC-SHA256 under the repository key, in a separate chunk namespace
  (`chunks/e3_<id>/`), so the provider cannot confirm that it holds a known file. Upgrading therefore uploads the data once
  more; v2 snapshots stay readable (with the password they were written with).
- **Wrong password** is detected when the key file is opened, before any chunk is downloaded.
- **Change password** (Settings > Encryption > "Change password...", or `restore change-password`) re-wraps the data key: no chunk or
  manifest is rewritten. The old password stops working. `restore recovery-key` adds a one-time recovery key.
  Other computers that back up to the same storage must be given the new password; a backup whose configured password does not
  open the key file fails and never replaces it.
- Browsing, versions, diff, adopt and repair of an encrypted v3 repository need the password (repair stops without deleting
  anything if a manifest cannot be decrypted).
