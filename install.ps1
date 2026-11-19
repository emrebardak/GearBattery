# Builds GearBattery, copies it to %LOCALAPPDATA%\GearBattery, adds it to Windows startup and starts it.
# Remove with: .\install.ps1 -Uninstall
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$dir = "$env:LOCALAPPDATA\GearBattery"
$lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'GearBattery.lnk'

Get-Process GearBattery -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if ($Uninstall) {
    Remove-Item $lnk -ErrorAction SilentlyContinue
    Remove-Item $dir -Recurse -ErrorAction SilentlyContinue
    Write-Host 'GearBattery removed.'
    return
}

& "$PSScriptRoot\build.ps1"
New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item "$PSScriptRoot\bin\GearBattery.exe" $dir -Force

$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($lnk)
$s.TargetPath = "$dir\GearBattery.exe"
$s.WorkingDirectory = $dir
$s.Save()

Start-Process "$dir\GearBattery.exe" -WorkingDirectory $dir
Write-Host "Installed to $dir and added to startup."
