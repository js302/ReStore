# File Sharing & Context Menu

ReStore integrates with Windows Explorer to allow quick file sharing.

## Enable Context Menu

1. **Download the `register-context-menu.ps1` script** (available alongside the installer in the release).
2. Right-click the script and select **"Run with PowerShell"**.

The script automatically detects your ReStore installation. If you installed via MSIX, it uses the system-wide alias. If you are running from source, it uses your local build.

## Usage

Once enabled, you can **right-click any file** in Explorer and select **"Share with ReStore"**. This opens a dialog to upload the file to your chosen cloud provider (S3, Azure, GCP, Dropbox, B2) and generates a shareable link.

**NOTE**: You must have at least one cloud storage provider configured in ReStore for sharing to work. The files will be **unencrypted** when shared. If you get any error, ensure that the storage provider is correctly set up.

## Disable Context Menu

To remove the context menu option, open Registry Editor (`regedit.exe`) and navigate to:

```
HKEY_CLASSES_ROOT\*\shell\ReStoreShare
```

Then, right-click the **"ReStoreShare"** key and select **Delete**.
