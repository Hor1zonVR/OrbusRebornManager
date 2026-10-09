$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host 'The .NET 8 SDK is required to build this project.' -ForegroundColor Red
    Write-Host 'Download: https://dotnet.microsoft.com/download/dotnet/8.0'
    exit 1
}

$destination = Join-Path $PSScriptRoot 'publish\win-x64'
Write-Host 'Building self-contained OrbusRebornManager.exe for Windows x64...' -ForegroundColor Cyan

dotnet publish '.\OrbusRebornManager.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $destination
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed. See the errors above.' }

$exe = Join-Path $destination 'OrbusRebornManager.exe'
if (-not (Test-Path $exe)) { throw "Build completed but the EXE was not found at $exe" }
Write-Host "SUCCESS: $exe" -ForegroundColor Green
Write-Host 'This EXE can be given to Jordan. It does not require him to install the .NET runtime.' -ForegroundColor Green
