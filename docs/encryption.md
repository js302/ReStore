# Backup Encryption

ReStore supports enterprise-grade AES-256-GCM encryption to secure your backups with password protection. This feature is available for both file backups and system backups.

## Encryption Features

- **Algorithm**: AES-256-GCM (Galois/Counter Mode) - provides both confidentiality and authentication
- **Key Derivation**: PBKDF2-SHA256 with 1,000,000 iterations for secure password-based key generation
- **Hybrid Architecture**: Each backup uses a unique Data Encryption Key (DEK) that is encrypted with a Key Encryption Key (KEK) derived from your password
- **Authenticated Encryption**: Built-in authentication prevents tampering with encrypted backups
- **File Format**: Encrypted backups are stored with `.enc` extension and include a `.enc.meta` metadata file

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
   - A unique DEK is generated for each backup
   - Files are compressed into a ZIP archive
   - The ZIP is encrypted with the DEK using AES-256-GCM
   - The DEK is encrypted with the KEK (derived from your password + salt)
   - Encrypted backup (`.enc`) and metadata (`.enc.meta`) files are stored
3. **During Restore**:
   - You're prompted for your password
   - The KEK is derived from your password + stored salt
   - The DEK is decrypted using the KEK
   - The backup is decrypted and decompressed to restore your files

## Password Requirements

- Minimum 8 characters
- Recommended: Mix of uppercase, lowercase, numbers, and special characters
- The GUI provides real-time strength feedback with color indicators:
  - **Red (Weak)**: Basic password, easy to crack
  - **Orange (Medium)**: Decent password, consider adding more complexity
  - **Green (Strong)**: Excellent password with good complexity

## Encrypted Backup Behavior

**What Gets Encrypted:**

- **All file and directory backups**: Full, Incremental, and Differential backups
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

**Scenario 1**: Config file lost, but .enc.meta files intact

- Can restore all encrypted backups (metadata has the salt)
- Cannot create new encrypted backups (no verification token or master salt)
- Solution: User can disable and then re-enable encryption with the same password (generates new master salt + token)

**Scenario 2**: .enc.meta file lost, but .enc file intact

- Data is PERMANENTLY LOST - no way to decrypt without salt, IV, and EncryptedDEK
- Solution: No recovery possible

**Scenario 3**: Config salt modified accidentally

- Can still restore old backups (they have their own salt in metadata)
- New backups will use wrong salt and won't decrypt with original password
- This is a data corruption scenario
