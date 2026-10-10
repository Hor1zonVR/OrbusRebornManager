param(
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET 8 SDK before building: https://dotnet.microsoft.com/download/dotnet/8.0'
}

[xml]$project = Get-Content -Raw '.\OrbusRebornManager.csproj'
$version = [string]$project.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw 'Project version is missing.' }

$destination = Join-Path $PSScriptRoot 'publish\win-x64'
Write-Host "Publishing RebornManager v$version..." -ForegroundColor Cyan
dotnet publish '.\OrbusRebornManager.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $destination
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

if ($SkipInstaller) {
    Write-Host "Published executable: $destination\OrbusRebornManager.exe"
    Write-Host 'This standalone build does not support automatic installation updates.'
    exit 0
}

$toolsPath = Join-Path $PSScriptRoot '.tools'
$vpk = Join-Path $toolsPath 'vpk.exe'
if (-not (Test-Path $vpk)) {
    dotnet tool install --tool-path $toolsPath vpk --version 1.2.161
    if ($LASTEXITCODE -ne 0) { throw 'Could not install the Velopack packaging tool.' }
}

& $vpk pack --packId 'Hor1zonVR.OrbusRebornManager' --packVersion $version --packDir $destination --mainExe 'OrbusRebornManager.exe' --packTitle 'Orbus Reborn Manager' --icon 'Assets/OrbusRebornManager.ico' --channel win --runtime win-x64
if ($LASTEXITCODE -ne 0) { throw 'Velopack packaging failed.' }
Write-Host 'Installer and update packages are in the Releases directory.' -ForegroundColor Green
Write-Host 'Only properly tagged GitHub Releases are offered through the in-app updater.'
