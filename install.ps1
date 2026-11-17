<#
.SYNOPSIS
    Installs, updates or removes the portable ReStore command-line tool (restore.exe) for the current user.

.DESCRIPTION
    Downloads the portable CLI for this PC's architecture from the GitHub release, checks its SHA-256 against the
    release's latest.json, unpacks it to %LOCALAPPDATA%\Programs\ReStore\cli and adds that folder to the user PATH.
    No administrator rights, certificate or MSIX install are needed. Your configuration, state and logs live in
    %USERPROFILE%\ReStore and are never touched by this script.

    One-line install (latest version):
        irm https://github.com/js302/ReStore/releases/latest/download/install.ps1 | iex

    With options:
        & ([scriptblock]::Create((irm https://github.com/js302/ReStore/releases/latest/download/install.ps1))) -Version 1.8.0

    Environment variables (useful with the one-liner, which cannot take parameters):
        RESTORE_INSTALL_VERSION, RESTORE_INSTALL_DIR, RESTORE_INSTALL_NO_PATH=1, RESTORE_INSTALL_NO_TELEMETRY=1

.PARAMETER Version
    Install this version (for example 1.8.0) instead of the latest release.

.PARAMETER InstallDir
    Where to put restore.exe. Default: %LOCALAPPDATA%\Programs\ReStore\cli

.PARAMETER NoPathUpdate
    Don't add the install folder to the user PATH.

.PARAMETER DisableTelemetry
    Turn off ReStore's anonymous usage statistics right away (the same as running 'restore telemetry disable' later).
    What is collected and why: https://github.com/js302/ReStore/blob/main/docs/telemetry.md

.PARAMETER Uninstall
    Remove the files this installer put in the install folder (restore.exe, ReStore.channel, LICENSE.txt, config\config.example.json) and the
    PATH entry. Refuses when the folder has no ReStore.channel file; other files in the folder are left alone.

.PARAMETER ManifestUrl
    Read the release description (latest.json) from this URL instead of GitHub (mirrors and testing).
#>
[CmdletBinding()]
param(
    [string]$Version = $env:RESTORE_INSTALL_VERSION,
    [string]$InstallDir = $env:RESTORE_INSTALL_DIR,
    [switch]$NoPathUpdate,
    [switch]$DisableTelemetry,
    [switch]$Uninstall,
    [string]$ManifestUrl
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"   # Windows PowerShell's progress bar makes downloads many times slower
$Repository = "js302/ReStore"

if ($env:RESTORE_INSTALL_NO_PATH -eq "1") {
    $NoPathUpdate = $true
}

if ($env:RESTORE_INSTALL_NO_TELEMETRY -eq "1") {
    $DisableTelemetry = $true
}

if (-not $InstallDir) {
    $InstallDir = Join-Path $env:LOCALAPPDATA "Programs\ReStore\cli"
}
$InstallDir = [System.IO.Path]::GetFullPath($InstallDir)

function Write-Step([string]$Message) {
    Write-Host "==> $Message"
}

function Get-UserPathEntries {
    $current = [Environment]::GetEnvironmentVariable("Path", "User")
    if (-not $current) {
        return @()
    }

    return @($current.Split(";") | Where-Object { $_ })
}

function Test-SamePath([string]$A, [string]$B) {
    return $A.TrimEnd("\") -ieq $B.TrimEnd("\")
}

function Add-ToUserPath([string]$Directory) {
    $entries = Get-UserPathEntries
    if ($entries | Where-Object { Test-SamePath $_ $Directory }) {
        return $false
    }

    # First, so it wins over the MSIX app alias (%LOCALAPPDATA%\Microsoft\WindowsApps\restore.exe) if both are installed.
    [Environment]::SetEnvironmentVariable("Path", (@($Directory) + $entries) -join ";", "User")
    return $true
}

function Remove-FromUserPath([string]$Directory) {
    $entries = Get-UserPathEntries
    $kept = @($entries | Where-Object { -not (Test-SamePath $_ $Directory) })
    if ($kept.Count -ne $entries.Count) {
        [Environment]::SetEnvironmentVariable("Path", $kept -join ";", "User")
        return $true
    }

    return $false
}

function Get-Architecture {
    $arch = $env:PROCESSOR_ARCHITEW6432
    if (-not $arch) {
        $arch = $env:PROCESSOR_ARCHITECTURE
    }

    switch ($arch) {
        "AMD64" { return "x64" }
        "ARM64" { return "arm64" }
        default { throw "Unsupported processor architecture '$arch'. ReStore supports x64 and arm64 Windows." }
    }
}

# The files the CLI zip installs (relative to the install folder). -Uninstall removes exactly these and nothing else, so an
# -InstallDir that is a shared folder (C:\Tools) never loses anything that is not ReStore's.
$InstalledFiles = @("restore.exe", "restore.exe.old", "ReStore.channel", "LICENSE.txt", "config\config.example.json")

if ($Uninstall) {
    Write-Step "Removing the ReStore CLI from $InstallDir"
    if (Test-Path $InstallDir) {
        if (-not (Test-Path (Join-Path $InstallDir "ReStore.channel"))) {
            throw "Refusing to uninstall: '$InstallDir' has no ReStore.channel file, so it does not look like a ReStore CLI install. Nothing was removed."
        }

        foreach ($relative in $InstalledFiles) {
            Remove-Item (Join-Path $InstallDir $relative) -Force -ErrorAction SilentlyContinue
        }

        # Only folders that are empty now; anything else in them is not ours.
        foreach ($folder in @((Join-Path $InstallDir "config"), $InstallDir)) {
            if ((Test-Path $folder -PathType Container) -and -not (Get-ChildItem -Path $folder -Force)) {
                Remove-Item $folder -Force
            }
        }
    }

    if (Remove-FromUserPath $InstallDir) {
        Write-Step "Removed $InstallDir from the user PATH (open a new terminal for it to take effect)"
    }

    Write-Step "Done. Your configuration and backups in %USERPROFILE%\ReStore were left untouched."
    return
}

# Windows PowerShell 5.1 may default to TLS 1.0/1.1, which GitHub refuses.
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
} catch {
    # PowerShell 7 does not need this.
}

$architecture = Get-Architecture

if (-not $ManifestUrl) {
    if ($Version) {
        $ManifestUrl = "https://github.com/$Repository/releases/download/v$($Version.TrimStart('v'))/latest.json"
    } else {
        $ManifestUrl = "https://github.com/$Repository/releases/latest/download/latest.json"
    }
}

Write-Step "Reading release information from $ManifestUrl"
$release = Invoke-RestMethod -Uri $ManifestUrl -UseBasicParsing
if (-not $release -or -not $release.version) {
    throw "The release description at $ManifestUrl is not valid."
}

$asset = @($release.assets | Where-Object { $_.kind -eq "cli-zip" -and $_.arch -eq $architecture }) | Select-Object -First 1
if (-not $asset) {
    throw "Release $($release.version) has no portable CLI for $architecture."
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("restore-install-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work -Force | Out-Null

try {
    $zip = Join-Path $work $asset.name
    Write-Step "Downloading ReStore CLI $($release.version) ($architecture)"
    Invoke-WebRequest -Uri $asset.url -OutFile $zip -UseBasicParsing

    $actual = (Get-FileHash -Algorithm SHA256 -Path $zip).Hash
    if (-not $asset.sha256 -or $actual -ine $asset.sha256) {
        throw "Checksum mismatch for $($asset.name): expected $($asset.sha256), got $actual. Nothing was installed."
    }
    Write-Step "Checksum verified"

    $unpacked = Join-Path $work "unpacked"
    Expand-Archive -Path $zip -DestinationPath $unpacked -Force
    if (-not (Test-Path (Join-Path $unpacked "restore.exe"))) {
        throw "The downloaded archive does not contain restore.exe. Nothing was installed."
    }

    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

    # A running restore.exe cannot be overwritten but can be renamed, so move the old one aside first.
    $existing = Join-Path $InstallDir "restore.exe"
    $aside = Join-Path $InstallDir "restore.exe.old"
    if (Test-Path $aside) {
        Remove-Item $aside -Force -ErrorAction SilentlyContinue
    }
    $movedAside = $false
    if (Test-Path $existing) {
        Move-Item $existing $aside -Force
        $movedAside = $true
    }

    try {
        if ($env:RESTORE_INSTALL_SIMULATE_COPY_FAILURE -eq "1") {
            # Test hook (ReStore.E2E/packaging/Test-Packaging.ps1): exercises the rollback below.
            throw "Simulated copy failure (RESTORE_INSTALL_SIMULATE_COPY_FAILURE=1)."
        }
        Copy-Item -Path (Join-Path $unpacked "*") -Destination $InstallDir -Recurse -Force
    } catch {
        # Never leave the user without a restore.exe: put the previous one back.
        if ($movedAside) {
            Remove-Item $existing -Force -ErrorAction SilentlyContinue
            Move-Item $aside $existing -Force
            Write-Step "Copying the new files failed; the previous restore.exe was put back"
        }
        throw
    }

    Remove-Item $aside -Force -ErrorAction SilentlyContinue
    Write-Step "Installed to $InstallDir"
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

if (-not $NoPathUpdate) {
    if (Add-ToUserPath $InstallDir) {
        Write-Step "Added $InstallDir to the user PATH (new terminals pick it up)"
    }
}

# Make it usable in this session straight away.
if (-not (($env:Path.Split(";")) | Where-Object { $_ -and (Test-SamePath $_ $InstallDir) })) {
    $env:Path = "$InstallDir;$env:Path"
}

$restoreExe = Join-Path $InstallDir "restore.exe"
Write-Step "Installed version:"
& $restoreExe --version
Write-Host ""

# Privacy notice, shown at every install (and the opt-out offered here, as part of installing).
Write-Host "Privacy: ReStore collects anonymous usage statistics (which features are used, whether they succeed, how long they take)."
Write-Host "It never collects file names, paths or file contents. Details: https://github.com/js302/ReStore/blob/main/docs/telemetry.md"
if ($DisableTelemetry) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = "Continue"   # Windows PowerShell 5.1 treats any stderr line of a native command as an error under "Stop"
    try {
        & $restoreExe telemetry disable --quiet 2>$null | Out-Null
        $telemetryExit = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $saved
    }
    if ($telemetryExit -eq 0) {
        Write-Step "Anonymous usage statistics are turned off"
    } else {
        Write-Warning "Could not turn off usage statistics (exit $telemetryExit); run 'restore telemetry disable' yourself."
    }
} else {
    Write-Host "Turn it off with 'restore telemetry disable' (or install with -DisableTelemetry / RESTORE_INSTALL_NO_TELEMETRY=1)."
}
Write-Host ""
Write-Host "Run 'restore --help' to get started, or 'restore agent-guide' if you are an AI agent setting ReStore up."
