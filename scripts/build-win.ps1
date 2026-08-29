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

# Clean any stale publish output to avoid Electron.NET migration warnings and leftover package.json files
if (Test-Path $DotnetPublishDir) {
    Write-Host "  Cleaning $DotnetPublishDir..."
    Remove-Item -Path $DotnetPublishDir -Recurse -Force -ErrorAction SilentlyContinue
}

# Use dotnet publish win-x64.xml
dotnet publish -c $Configuration -p:PublishProfile=win-x64 -p:ElectronSkipExecCommands=true

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

# Verify main .NET executable exists (AssemblyName in csproj should match ProductName)
$DotNetExe = Join-Path $DotnetPublishDir "bin\$ProductName.exe"
if (-not (Test-Path $DotNetExe)) {
    Write-Host "  Warning: $ProductName.exe not found at $DotNetExe"
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

# Verify the Electron host app and its runtime dependencies are present.
# dotnet publish (with ElectronSkipExecCommands=true) creates the app/ folder and runs
# npm install --omit=dev inside it to populate node_modules.
$AppDir = Join-Path $ElectronAppDir "app"
if (-not (Test-Path $AppDir) -or -not (Test-Path (Join-Path $AppDir "main.js")) -or -not (Test-Path (Join-Path $AppDir "node_modules"))) {
    Write-Host "  Error: Electron host app not found or its node_modules are missing in $AppDir"
    exit 1
}
Write-Host "  Verified Electron host app in $AppDir"

# Install npm dependencies
Write-Host "  Installing npm dependencies..."
Set-Location $ElectronAppDir
# npm install --no-bin-links
npm install electron-builder@$ElectronBuilderVersion --save-dev
if ($LASTEXITCODE -ne 0) {
    Set-Location $ProjectRoot
    Write-Host "  electron-builder installation failed!"
    exit 1
}

# Run electron-builder in standard mode (NOT --prepackaged)
# Use the app/ subdirectory as the Electron app directory (produced by dotnet publish).
Write-Host "  Running electron-builder..."
npx electron-builder --config=$BuilderJsonPath --config.electronVersion=$ElectronVersion --win --x64
if ($LASTEXITCODE -ne 0) {
    Set-Location $ProjectRoot
    Write-Host "  electron-builder failed!"
    exit 1
}
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
else {
    Write-Host "  Electron installer output not found: $InstallerDir"
    exit 1
}

Write-Host "  Done."
Write-Host ""
Write-Host "========================================"
Write-Host "  Build Completed Successfully!"
Write-Host "========================================"
Write-Host "DotNet-First Output: $ReleaseDir"

Set-Location $ProjectRoot
