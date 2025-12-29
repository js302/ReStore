# Dropbox Setup

Backup to your Dropbox account.

## Step 1: Create a Dropbox App

1. Go to the [Dropbox App Console](https://www.dropbox.com/developers/apps)
2. Click **Create app**
3. Choose **Scoped access**
4. Choose **App folder** (safer) or **Full Dropbox**
5. Name your app (e.g., "ReStore Backup")
6. Go to the **Permissions** tab and enable `files.content.write` and `files.content.read`
7. Click **Submit** at the bottom
8. Go to the **Settings** tab
9. Copy **App key** and **App secret**

## Step 2: Configure ReStore

Open `%USERPROFILE%\ReStore\config.json`:

```json
{
  "storageSources": {
    "dropbox": {
      "path": "/backups",
      "options": {
        "appKey": "your_app_key",
        "appSecret": "your_app_secret",
        "refreshToken": "your_refresh_token"
      }
    }
  }
}
```

**To get a Refresh Token:**

1. Go to this URL in your browser (replace `APP_KEY` with your App Key):
   `https://www.dropbox.com/oauth2/authorize?client_id=APP_KEY&response_type=code&token_access_type=offline`
2. Click **Continue** and **Allow**
3. Copy the access code
4. Use a tool like Postman or curl to exchange the code for a refresh token:
   ```bash
   curl https://api.dropbox.com/oauth2/token \
       -d code=YOUR_ACCESS_CODE \
       -d grant_type=authorization_code \
       -d client_id=YOUR_APP_KEY \
       -d client_secret=YOUR_APP_SECRET
   ```
5. Copy the `refresh_token` from the response.

**Configuration Parameters:**

- **path**: Path within Dropbox (default: `"/backups"`)
- **appKey**: Your Dropbox App Key
- **appSecret**: Your Dropbox App Secret
- **refreshToken**: The long-lived refresh token
