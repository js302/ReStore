# Backup Encryption

ReStore supports enterprise-grade AES-256-GCM encryption to secure your backups with password protection. This feature is available for both file backups and system backups.

## Encryption Features

- **Algorithm**: AES-256-GCM (Galois/Counter Mode) - provides both confidentiality and authentication
- **Key Derivation**: PBKDF2-SHA256 with 1,000,000 iterations for secure password-based key generation
- **Hybrid Architecture**: Each backup uses a unique Data Encryption Key (DEK) that is encrypted with a Key Encryption Key (KEK) derived from your password
- **Authenticated Encryption**: Built-in authentication prevents tampering with encrypted backups
- **Artifact Format**:
  - User-file backups: encrypted deterministic chunk objects referenced by snapshot manifests
  - System backups: encrypted `.enc` archives with `.enc.meta` metadata files

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

**Note**: Disabling encryption only affects new backups. Existing encrypted backups will still require the password to restore.

## Security Best Practices

1. **Use a strong, unique password** - Don't reuse passwords from other services
2. **Store your password securely** - Use a password manager like Bitwarden or KeePass
3. **Backup your password** - Write it down and store it in a secure physical location
4. **Test restoration** - Verify you can decrypt and restore backups before you need them
5. **Multiple storage locations** - Keep encrypted backups in multiple places (local + cloud)
6. **Regular password updates** - Consider changing your encryption password periodically
7. **Secure your config** - The salt is stored in `config.json` - keep this file secure

## Encryption Performance

- **Overhead**: Minimal impact on backup speed due to efficient AES-256-GCM implementation
- **Compression First**: Files are compressed before encryption for optimal storage efficiency
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
