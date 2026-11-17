# SFTP Setup

Backup to any server supporting SFTP (Secure File Transfer Protocol).

## Step 1: Prepare your SFTP Server

Ensure you have the hostname, username, and either a password or private key file.

## Step 2: Configure ReStore

Open `%USERPROFILE%\ReStore\config.json`:

```json
{
  "storageSources": {
    "sftp": {
      "options": {
        "host": "sftp.example.com",
        "port": "22",
        "username": "your_username",
        "password": "your_password",
        "privateKeyPath": "C:\\Users\\Name\\.ssh\\id_rsa",
        "passphrase": "your_key_passphrase",
        "hostKeyFingerprint": "SHA256:AbCdEf...43 characters..."
      }
    }
  }
}
```

**Configuration Parameters:**

- **host**: Server hostname or IP
- **port**: SFTP port (default: `"22"`)
- **username**: Login username
- **password**: Login password (optional if using private key)
- **privateKeyPath**: Path to private key file (optional if using password; takes precedence when both are set)
- **passphrase**: Passphrase for an encrypted private key (optional)
- **hostKeyFingerprint**: SHA256 fingerprint of the server's host key, as printed by OpenSSH (`SHA256:...`; the `SHA256:` prefix is optional). **Strongly recommended.** If it does not match, ReStore refuses to connect.

## Host Key Verification

When `hostKeyFingerprint` is set, ReStore only connects if the server presents a host key with exactly that fingerprint. This protects against man-in-the-middle attacks.

If it is **not** set, ReStore trusts the server on first use: it connects anyway and writes a warning to the log containing the fingerprint and the exact line to add, for example:

```
SFTP host key for sftp.example.com is not pinned; trusting SHA256:... To pin it, add "hostKeyFingerprint": "SHA256:..." to this SFTP source's options in config.json.
```

ReStore does not save the fingerprint automatically and there is no Settings field for it yet; add it to `config.json` by hand. You can also get the fingerprint yourself with:

```
ssh-keyscan -t ed25519 sftp.example.com | ssh-keygen -lf -
```

If the server's key legitimately changes, update `hostKeyFingerprint`.

## Upload Behaviour

Files are uploaded to `<name>.part-<guid>` and renamed into place, so an interrupted upload never leaves a truncated file at the final path. Leftover `.part-*` files are safe to delete.

## Optional: `prefix` option

Add `"prefix": "my-laptop"` to this source's `options` to keep the repository under a sub-folder of the login directory. It is applied to every path (uploads, downloads, listing, sharing) and is empty by default, so existing repositories are unaffected. It must not start with `/` or contain `..`. Changing it later points ReStore at a different (empty) repository; it does not move existing data.

Remote sources do not need a top-level `"path"`. If one is present it is ignored (ReStore logs a warning); use `prefix` instead.
