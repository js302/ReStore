# ReStore System Backup Demo Script
# This script demonstrates the new system backup and restore features

Write-Host "ReStore System Backup and Restore Demo" -ForegroundColor Green
Write-Host "=====================================" -ForegroundColor Green
Write-Host ""

# Build the project first
Write-Host "Building ReStore..." -ForegroundColor Cyan
dotnet build .\ReStore\ReStore.csproj
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

$exePath = ".\ReStore\bin\Debug\net9.0\ReStore.exe"

Write-Host "ReStore executable path: $exePath" -ForegroundColor Yellow
Write-Host ""

# Demo 1: Backup all system components
Write-Host "Demo 1: Backing up all system components (programs + environment variables)" -ForegroundColor Cyan
Write-Host "Command: $exePath system-backup local all" -ForegroundColor Gray
& $exePath system-backup local all
Write-Host ""

# Demo 2: Backup only programs
Write-Host "Demo 2: Backing up only installed programs" -ForegroundColor Cyan
Write-Host "Command: $exePath system-backup local programs" -ForegroundColor Gray
& $exePath system-backup local programs
Write-Host ""

# Demo 3: Backup only environment variables
Write-Host "Demo 3: Backing up only environment variables" -ForegroundColor Cyan
Write-Host "Command: $exePath system-backup local environment" -ForegroundColor Gray
& $exePath system-backup local environment
Write-Host ""

# Show backup location
$backupPath = "$env:USERPROFILE\ReStoreBackups"
Write-Host "Backups are stored in: $backupPath" -ForegroundColor Yellow

if (Test-Path $backupPath) {
    Write-Host "Backup directory contents:" -ForegroundColor Yellow
    Get-ChildItem $backupPath -Recurse | Format-Table Name, Length, LastWriteTime
}

Write-Host ""
Write-Host "Demo completed! Check the backup directory for generated files." -ForegroundColor Green
Write-Host "You can restore using commands like:" -ForegroundColor Yellow
Write-Host "  $exePath system-restore local <backup-path> programs" -ForegroundColor Gray
Write-Host "  $exePath system-restore local <backup-path> environment" -ForegroundColor Gray
