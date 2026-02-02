# File Sharing & Context Menu

ReStore integrates with Windows Explorer to allow quick file sharing.

## Enable Context Menu

The context menu integration is now built into ReStore. To enable it:

1. Open ReStore
2. Go to **Settings** → **Application Behavior**
3. Check **"Enable 'Share with ReStore' context menu"**

That's it! The setting is applied immediately.

## Usage

Once enabled, you can **right-click any file** in Explorer and select **"Share with ReStore"**. This opens a dialog to upload the file to your chosen cloud provider (S3, Azure, GCP, Dropbox, B2) and generates a shareable link.

**NOTE**: You must have at least one cloud storage provider configured in ReStore for sharing to work. The files will be **unencrypted** when shared. If you get any error, ensure that the storage provider is correctly set up.

## Disable Context Menu

To disable the context menu:

1. Open ReStore
2. Go to **Settings** → **Application Behavior**
3. Uncheck **"Enable 'Share with ReStore' context menu"**

The context menu entry will be removed immediately.
