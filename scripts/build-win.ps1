# Electron.NET DotNet-First Windows Build Script
# Usage: .\scripts\build-dotnet-first-win.ps1
#
# This script creates a DotNet-First packaged application:
# 1. Build frontend (Vite)
# 2. Build .NET application
# 3. Prepare electron-builder working directory
# 4. Run electron-builder (standard mode with extraFiles)
# 5. Post-process: Reorganize directory structure for DotNet-First

param(
    [switch]$SkipFrontend,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

# Project paths
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$ElectronNetDir = Join-Path $ProjectRoot "ElectronNet\ElectronNet"
$CsprojPath = Join-Path $ElectronNetDir "ElectronNet.csproj"
$BuilderJsonPath = Join-Path $ElectronNetDir "Properties\electron-builder.json"

# Read configuration from csproj (ElectronVersion, Title, Version, ElectronPackageId)
[xml]$Csproj = Get-Content $CsprojPath
$ElectronNetCommon = $Csproj.Project.PropertyGroup | Where-Object { $_.Label -eq "ElectronNetCommon" }
$ElectronVersion = $ElectronNetCommon.ElectronVersion
$ElectronBuilderVersion = $ElectronNetCommon.ElectronBuilderVersion
$ProductName = $ElectronNetCommon.Title
$Version = $ElectronNetCommon.Version

# Read configuration from electron-builder.json
$BuilderConfig = Get-Content $BuilderJsonPath -Raw | ConvertFrom-Json
$AppId = $BuilderConfig.appId

# Directories
$DotnetPublishDir = Join-Path $ElectronNetDir "Publish\$Configuration\net10.0\win-x64"
$ReleaseDir = Join-Path $ProjectRoot "release"

Write-Host "========================================"
Write-Host "  DotNet-First Windows Build Script"
Write-Host "========================================"
Write-Host "Project Root: $ProjectRoot"
Write-Host "ElectronNet Dir: $ElectronNetDir"
Write-Host "Dotnet Publish Dir: $DotnetPublishDir"
Write-Host "Release Dir: $ReleaseDir"
Write-Host ""
Write-Host "Product Name: $ProductName"
Write-Host "Version: $Version"
Write-Host "Electron Version: $ElectronVersion"
Write-Host "App ID: $AppId"
Write-Host ""

# Step 1: Clean
Write-Host "[1/5] Cleaning previous builds..."
Set-Location $ElectronNetDir

# Clean dotnet
dotnet clean -c $Configuration 2>$null | Out-Null

# Remove previous output directories
if (Test-Path $DotnetPublishDir) {
    Remove-Item -Path $DotnetPublishDir -Recurse -Force -ErrorAction SilentlyContinue
}
if (Test-Path $ReleaseDir) {
    Remove-Item -Path $ReleaseDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "  Done."

# Step 2: Build frontend
if (-not $SkipFrontend) {
    Write-Host "[2/5] Building frontend with Vite..."
    Set-Location $ProjectRoot
    pnpm run build
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  Frontend build failed!"
        exit 1
    }
    Write-Host "  Done."
}
else {
    Write-Host "[2/5] Skipping frontend build..."
}

# Step 3: Build .NET application
Write-Host "[3/5] Building .NET application..."
Set-Location $ElectronNetDir

# Use dotnet publish win-x64.xml
dotnet publish -c $Configuration -p:PublishProfile=win-x64

if ($LASTEXITCODE -ne 0) {
    Write-Host "  .NET build failed!"
    exit 1
}

# Delete useless folders from publish output
if (Test-Path "$DotnetPublishDir\.vscode") {
    Remove-Item -Path "$DotnetPublishDir\.vscode" -Recurse -Force -ErrorAction SilentlyContinue
}
if (Test-Path "$DotnetPublishDir\bin\.electron") {
    Remove-Item -Path "$DotnetPublishDir\bin\.electron" -Recurse -Force -ErrorAction SilentlyContinue
}

# Rename main .NET executable to match product name
$OldDotNetExe = Join-Path $DotnetPublishDir "bin\ElectronNet.exe"
if (Test-Path $OldDotNetExe) {
    Rename-Item -Path $OldDotNetExe -NewName "$ProductName.exe" -Force
}

Write-Host "  Done."

# Step 4: Run electron-builder (standard mode)
Write-Host "[4/5] Running electron-builder (standard mode)..."

# Electron app directory (where main.js and package.json are)
$ElectronAppDir = $DotnetPublishDir

# Copy installer.nsh to build directory for NSIS customization
$BuildDir = Join-Path $ElectronAppDir "build"
New-Item -ItemType Directory -Path $BuildDir -Force | Out-Null
$InstallerNshSource = Join-Path $ElectronNetDir "Properties\installer.nsh"
Copy-Item -Path $InstallerNshSource -Destination $BuildDir -Force
Write-Host "  Copied installer.nsh to build directory"

# Install npm dependencies
Write-Host "  Installing npm dependencies..."
Set-Location $ElectronAppDir
# npm install --no-bin-links
npm install electron-builder@$ElectronBuilderVersion --save-dev

# Run electron-builder in standard mode (NOT --prepackaged)
Write-Host "  Running electron-builder..."
npx electron-builder --config=$BuilderJsonPath --win --x64
Set-Location $ProjectRoot

Write-Host "  Done."

# Step 5: Post-process - Copy output to release directory
Write-Host "[5/5] Copying output to release directory..."

$InstallerDir = Join-Path $DotnetPublishDir "installer"

# Create release directory
New-Item -ItemType Directory -Path $ReleaseDir -Force | Out-Null

# Copy all files and subdirectories from installer directory to release
if (Test-Path $InstallerDir) {
    Copy-Item -Path "$InstallerDir\*" -Destination $ReleaseDir -Recurse -Force
    Write-Host "  Copied all installer contents to release"
}

Write-Host "  Done."
Write-Host ""
Write-Host "========================================"
Write-Host "  Build Completed Successfully!"
Write-Host "========================================"
Write-Host "DotNet-First Output: $ReleaseDir"

Set-Location $ProjectRoot
