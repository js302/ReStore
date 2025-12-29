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
      "path": "/home/user/backups",
      "options": {
        "host": "sftp.example.com",
        "port": "22",
        "username": "your_username",
        "password": "your_password",
        "privateKeyPath": "C:\\Users\\Name\\.ssh\\id_rsa"
      }
    }
  }
}
```

**Configuration Parameters:**

- **path**: Remote directory path
- **host**: Server hostname or IP
- **port**: SFTP port (default: `"22"`)
- **username**: Login username
- **password**: Login password (optional if using private key)
- **privateKeyPath**: Path to private key file (optional if using password)
