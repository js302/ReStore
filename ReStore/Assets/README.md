# ReStore Assets Guide

This folder should contain all the icon and image assets required for the MSIX package.

## Required Files

Create the following image files for your MSIX package:

### Application Icon

- **icon.ico** - 256x256 pixels (ICO format)
  - Main application icon shown in taskbar and title bar

### Store and Tile Logos

- **StoreLogo.png** - 50x50 pixels

  - Used in Microsoft Store and Windows Settings

- **Square44x44Logo.png** - 44x44 pixels

  - App list icon in Start menu

- **Square71x71Logo.png** - 71x71 pixels (SmallTile)

  - Small tile in Start menu

- **Square150x150Logo.png** - 150x150 pixels

  - Medium tile in Start menu (default)

- **Square310x310Logo.png** - 310x310 pixels (LargeTile)

  - Large tile in Start menu

- **Wide310x150Logo.png** - 310x150 pixels
  - Wide tile in Start menu

### Splash Screen

- **SplashScreen.png** - 620x300 pixels
  - Shown while app is launching

## Design Guidelines

### Colors

- Use transparent backgrounds for logos
- Ensure icons work on both light and dark backgrounds
- Follow Microsoft Fluent Design guidelines

### Content

- Keep icons simple and recognizable at small sizes
- Use your app's primary branding colors
- Center important content (50% of the tile area)

## Quick Creation Options

### Option 1: Online Tools

- [App Icon Generator](https://www.appicon.co/) - Upload one icon, get all sizes
- [MakeAppIcon](https://makeappicon.com/) - Free icon generator

### Option 2: Design Tools

- Figma (free, web-based)
- Adobe Photoshop
- GIMP (free)
- Inkscape (free, vector)

### Option 3: AI Tools

- Use AI image generators (DALL-E, Midjourney, etc.)
- Generate a backup/restore themed icon
- Resize using online tools

## Placeholder for Testing

For quick testing, you can use simple colored squares:

1. Create a 300x300 blue square
2. Add text "ReStore" in white
3. Resize to each required size

Or use PowerShell to create simple placeholders:

```powershell
# This creates basic colored placeholder images (requires ImageMagick or similar)
# For real deployment, use proper icons
```

## Validation

After creating your icons:

1. Check all files are in PNG format (except icon.ico)
2. Verify dimensions match exactly
3. Test on both light and dark backgrounds
4. View at actual size to ensure clarity

## Resources

- [Microsoft Design Toolkit](https://docs.microsoft.com/windows/apps/design/downloads/)
- [Windows App Icon Guidelines](https://docs.microsoft.com/windows/apps/design/style/iconography/app-icon-design)
- [Fluent Design System](https://www.microsoft.com/design/fluent/)

---

**Note:** Without these assets, the MSIX package may build but won't display properly in Windows.
