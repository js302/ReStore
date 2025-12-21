# Determine the path to ReStore.exe
# Priority 1: Installed App Execution Alias (for end users)
$localAppData = [Environment]::GetFolderPath("LocalApplicationData")
$installedPath = Join-Path $localAppData "Microsoft\WindowsApps\restore.exe"

# Priority 2: Development Build (for developers)
$devPath = Join-Path $PSScriptRoot "ReStore\bin\Debug\net9.0-windows\win-x64\ReStore.exe"

if (Test-Path $installedPath) {
    $exePath = $installedPath
    Write-Host "Found installed ReStore.exe (Execution Alias). Using: $exePath"
}
elseif (Test-Path $devPath) {
    $exePath = Resolve-Path $devPath
    Write-Host "Found development ReStore.exe. Using: $exePath"
}
else {
    Write-Error "Could not find ReStore.exe. Please install the app or build the project first."
    exit 1
}

$command = "`"$exePath`" --share `"%1`""

New-Item -Path "HKCU:\Software\Classes\*\shell\ReStoreShare" -Force
Set-ItemProperty -Path "HKCU:\Software\Classes\*\shell\ReStoreShare" -Name "(default)" -Value "Share with ReStore"
Set-ItemProperty -Path "HKCU:\Software\Classes\*\shell\ReStoreShare" -Name "Icon" -Value $exePath

New-Item -Path "HKCU:\Software\Classes\*\shell\ReStoreShare\command" -Force
Set-ItemProperty -Path "HKCU:\Software\Classes\*\shell\ReStoreShare\command" -Name "(default)" -Value $command

Write-Host "Context menu registered for $exePath"
