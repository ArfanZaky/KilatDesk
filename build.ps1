# Build KilatDesk (butuh .NET SDK 8 atau lebih baru). Hasil: dist\KilatDesk.exe
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (-not (Test-Path app.ico)) { & "$PSScriptRoot\tools\make-icon.ps1" -Out "$PSScriptRoot\app.ico" }
dotnet build -c Release -nologo
if ($LASTEXITCODE -ne 0) { throw "Build gagal" }
New-Item -ItemType Directory -Force dist | Out-Null
Copy-Item bin\Release\KilatDesk.exe dist\KilatDesk.exe -Force
Write-Host "Selesai: $PSScriptRoot\dist\KilatDesk.exe"