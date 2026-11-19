# Builds bin\GearBattery.exe with the C# compiler that ships with Windows (.NET Framework 4.x). No SDK needed.
$ErrorActionPreference = 'Stop'
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc (.NET Framework 4.x is required)" }
New-Item -ItemType Directory -Force "$PSScriptRoot\bin" | Out-Null
& $csc /nologo /optimize /target:winexe "/out:$PSScriptRoot\bin\GearBattery.exe" "$PSScriptRoot\src\GearBattery.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $PSScriptRoot\bin\GearBattery.exe"
