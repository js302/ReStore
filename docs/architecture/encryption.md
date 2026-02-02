# Encryption Architecture

## Overview

ReStore uses enterprise-grade AES-256-GCM encryption with a hybrid DEK+KEK (Data Encryption Key / Key Encryption Key) pattern for secure backups.

## Hybrid DEK+KEK Pattern

```
Master Password (User Input)
      │
      ↓ PBKDF2-SHA256 (1M iterations)
Master Salt (32 bytes) ────────→ KEK (Key Encryption Key, 32 bytes)
      │                              │
      │                              ↓
      │                         Encrypts DEK
      │                              │
      ↓                              ↓
Verification Token          Encrypted DEK (stored in .enc.meta)
(stored in config)                   │
                                     ↓
                              DEK (Data Encryption Key, 32 bytes)
                                     │
                                     ↓ AES-256-GCM
                              Encrypts File Content
                                     │
                                     ↓
                              .enc file (encrypted backup)
```

## Key Components

| Component          | Size         | Description                                      |
| ------------------ | ------------ | ------------------------------------------------ |
| Master Password    | User-defined | User-provided password (minimum 8 characters)    |
| Master Salt        | 32 bytes     | Random salt stored in `config.json`              |
| Verification Token | Variable     | Encrypted constant for password validation       |
| KEK                | 32 bytes     | Derived from password + salt using PBKDF2-SHA256 |
| DEK                | 32 bytes     | Random key generated per backup                  |
| Encrypted DEK      | Variable     | DEK encrypted with KEK, stored in `.enc.meta`    |
| IV                 | 12 bytes     | Random initialization vector per backup          |
| Tag                | 16 bytes     | Authentication tag for AES-GCM                   |

## Security Properties

| Property                 | Implementation                              |
| ------------------------ | ------------------------------------------- |
| Forward Secrecy          | Each backup has a unique DEK                |
| Password Protection      | KEK derived from password, never stored     |
| Authenticated Encryption | AES-GCM provides encryption + integrity     |
| Key Derivation           | PBKDF2-SHA256 with 1,000,000 iterations     |
| Salt Storage             | Each backup stores its own salt in metadata |
| Password Verification    | Token validates password during backup only |
| No Plaintext             | Original files deleted after encryption     |

## Critical Files

| File                  | Contents                                          | Storage Location         |
| --------------------- | ------------------------------------------------- | ------------------------ |
| `config.json`         | `encryption.salt`, `encryption.verificationToken` | `%USERPROFILE%\ReStore\` |
| `backup.zip.enc`      | Encrypted backup data                             | Remote storage           |
| `backup.zip.enc.meta` | Encryption metadata (salt, IV, encryptedDEK)      | Remote storage           |

## Metadata Structure

The `.enc.meta` file contains JSON with the following structure:

```json
{
  "Salt": "<base64>",
  "IV": "<base64>",
  "EncryptedDEK": "<base64>",
  "Algorithm": "AES-256-GCM",
  "Version": 1,
  "KeyDerivationIterations": 1000000
}
```

## Recovery Requirements

To decrypt a backup, you need:

1. **Password** - User must remember this
2. **`.enc.meta` file** - Contains salt, IV, and encrypted DEK
3. **`.enc` file** - The encrypted backup data

### Recovery Scenarios

| Scenario                   | Outcome                                              |
| -------------------------- | ---------------------------------------------------- |
| Lost master salt in config | Can still decrypt old backups (metadata has salt)    |
| Lost `.enc.meta` file      | Backup permanently lost (no way to decrypt)          |
| Lost password              | All backups permanently lost (no recovery mechanism) |

## Implementation Details

### EncryptionService Methods

```csharp
// Key derivation
byte[] DeriveKeyFromPassword(string password, byte[] salt, int iterations = 1_000_000)

// File encryption (returns metadata)
Task<EncryptionMetadata> EncryptFileAsync(string inputPath, string outputPath,
    string password, byte[]? salt = null)

// File decryption (requires metadata)
Task DecryptFileAsync(string inputPath, string outputPath,
    string password, EncryptionMetadata metadata)

// Password verification token
string CreatePasswordVerificationToken(string password, byte[] salt, int iterations)
bool VerifyPassword(string password, byte[] salt, string verificationToken, int iterations)
```

### Password Provider Interface

```csharp
public interface IPasswordProvider
{
    Task<string?> GetPasswordAsync();
    void SetEncryptionMode(bool isEncrypting);
    void ClearPassword();
}
```

| Implementation           | Usage                               |
| ------------------------ | ----------------------------------- |
| `StaticPasswordProvider` | CLI with hardcoded password         |
| `GuiPasswordProvider`    | WPF with dialog prompts and caching |

## Best Practices

1. **Always upload both files**: `.enc` and `.enc.meta` must be uploaded together
2. **Context awareness**: Call `SetEncryptionMode(true)` before backup, keep default `false` for restore
3. **Password caching**: Password is cached for the session in `App.GlobalPasswordProvider`
4. **Error handling**: Clear cached password on decryption failure to allow retry
5. **Validate on backup only**: Password is validated against token during encryption, not decryption
6. **Use synchronous reads**: Use `ReadExactly()` not `ReadExactlyAsync()` in `Task.Run()` blocks
