#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Downloads and installs required dependencies for QA Device Tool.

.DESCRIPTION
    Downloads and installs:
    - Android platform-tools (ADB) → C:\Program Files\Android\platform-tools\
    - scrcpy → C:\Program Files\scrcpy\
    
    Optionally (with prompts):
    - Adds tool paths to User PATH

.PARAMETER InstallAdb
    Install Android platform-tools if missing.

.PARAMETER InstallScrcpy
    Install scrcpy if missing.

.PARAMETER SkipPrompts
    Skip confirmation prompts (for silent install).

.NOTES
    Version-pinned sources:
    - platform-tools: https://dl.google.com/android/repository/platform-tools-latest-windows.zip
    - scrcpy: https://github.com/Genymobile/scrcpy/releases/download/v3.1/scrcpy-win64-v3.1.zip
#>

[CmdletBinding()]
param(
    [switch]$InstallAdb,
    [switch]$InstallScrcpy,
    [switch]$SkipPrompts,
    [switch]$All
)

$ErrorActionPreference = 'Stop'

# ═══════════════════ CONFIGURATION ═══════════════════

$Config = @{
    AdbUrl           = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip"
    AdbInstallDir    = "C:\Program Files\Android\platform-tools"
    ScrcpyUrl        = "https://github.com/Genymobile/scrcpy/releases/download/v3.1/scrcpy-win64-v3.1.zip"
    ScrcpyInstallDir = "C:\Program Files\scrcpy"
    TempDir          = Join-Path $env:TEMP "QADeviceTool_Install"
}

# ═══════════════════ HELPER FUNCTIONS ═══════════════════

function Write-Step {
    param([string]$Message)
    Write-Host "  → " -NoNewline -ForegroundColor Cyan
    Write-Host $Message -ForegroundColor White
}

function Write-Success {
    param([string]$Message)
    Write-Host "  ✓ " -NoNewline -ForegroundColor Green
    Write-Host $Message -ForegroundColor White
}

function Write-Failure {
    param([string]$Message)
    Write-Host "  ✗ " -NoNewline -ForegroundColor Red
    Write-Host $Message -ForegroundColor White
}

function Confirm-Action {
    param([string]$Message)
    if ($SkipPrompts) { return $true }
    $response = Read-Host "$Message (Y/N)"
    return ($response -eq 'Y' -or $response -eq 'y')
}

function Download-File {
    param(
        [string]$Url,
        [string]$OutputPath
    )
    Write-Step "Downloading from $Url..."
    
    # Use System.Net.WebClient for compatibility
    $webClient = New-Object System.Net.WebClient
    try {
        $webClient.DownloadFile($Url, $OutputPath)
        Write-Success "Downloaded to $OutputPath"
        return $true
    }
    catch {
        Write-Failure "Download failed: $_"
        return $false
    }
    finally {
        $webClient.Dispose()
    }
}

function Extract-Zip {
    param(
        [string]$ZipPath,
        [string]$DestPath
    )
    Write-Step "Extracting to $DestPath..."
    
    if (Test-Path $DestPath) {
        Remove-Item $DestPath -Recurse -Force
    }
    
    Expand-Archive -Path $ZipPath -DestinationPath $DestPath -Force
    Write-Success "Extracted successfully."
}

# ═══════════════════ INSTALL ADB ═══════════════════

function Install-Adb {
    Write-Host ""
    Write-Host "  ── Installing Android Platform-Tools (ADB) ──" -ForegroundColor Yellow
    Write-Host ""

    # Check if already installed
    if (Get-Command "adb" -ErrorAction SilentlyContinue) {
        Write-Success "ADB is already installed and in PATH."
        return $true
    }

    if (Test-Path (Join-Path $Config.AdbInstallDir "adb.exe")) {
        Write-Success "ADB found at $($Config.AdbInstallDir) but not in PATH."
        return $true
    }

    if (-not $SkipPrompts) {
        if (-not (Confirm-Action "Install Android platform-tools to $($Config.AdbInstallDir)?")) {
            Write-Host "  Skipped ADB installation." -ForegroundColor Gray
            return $false
        }
    }

    # Create temp directory
    if (-not (Test-Path $Config.TempDir)) {
        New-Item -ItemType Directory -Path $Config.TempDir -Force | Out-Null
    }

    $zipPath = Join-Path $Config.TempDir "platform-tools.zip"

    # Download
    $downloaded = Download-File -Url $Config.AdbUrl -OutputPath $zipPath
    if (-not $downloaded) { return $false }

    # Extract to temp first, then move
    $extractTemp = Join-Path $Config.TempDir "platform-tools-extract"
    Extract-Zip -ZipPath $zipPath -DestPath $extractTemp

    # The zip contains a "platform-tools" subfolder
    $sourceDir = Join-Path $extractTemp "platform-tools"
    if (-not (Test-Path $sourceDir)) {
        $sourceDir = $extractTemp
    }

    # Create target directory
    $parentDir = Split-Path $Config.AdbInstallDir -Parent
    if (-not (Test-Path $parentDir)) {
        New-Item -ItemType Directory -Path $parentDir -Force | Out-Null
    }

    # Copy to final location
    if (Test-Path $Config.AdbInstallDir) {
        Remove-Item $Config.AdbInstallDir -Recurse -Force
    }
    Copy-Item -Path $sourceDir -Destination $Config.AdbInstallDir -Recurse -Force

    # Verify
    $adbExe = Join-Path $Config.AdbInstallDir "adb.exe"
    if (Test-Path $adbExe) {
        Write-Success "ADB installed to $($Config.AdbInstallDir)"
        
        # Verify it runs
        $version = & $adbExe version 2>&1 | Out-String
        if ($version -match "version") {
            Write-Success "ADB verification passed: $($version.Trim().Split("`n")[0])"
        }
        return $true
    }
    else {
        Write-Failure "ADB installation failed — executable not found."
        return $false
    }
}

# ═══════════════════ INSTALL SCRCPY ═══════════════════

function Install-Scrcpy {
    Write-Host ""
    Write-Host "  ── Installing scrcpy (Screen Mirroring) ──" -ForegroundColor Yellow
    Write-Host ""

    # Check if already installed
    if (Get-Command "scrcpy" -ErrorAction SilentlyContinue) {
        Write-Success "scrcpy is already installed and in PATH."
        return $true
    }

    if (Test-Path (Join-Path $Config.ScrcpyInstallDir "scrcpy.exe")) {
        Write-Success "scrcpy found at $($Config.ScrcpyInstallDir) but not in PATH."
        return $true
    }

    if (-not $SkipPrompts) {
        if (-not (Confirm-Action "Install scrcpy to $($Config.ScrcpyInstallDir)?")) {
            Write-Host "  Skipped scrcpy installation." -ForegroundColor Gray
            return $false
        }
    }

    # Create temp directory
    if (-not (Test-Path $Config.TempDir)) {
        New-Item -ItemType Directory -Path $Config.TempDir -Force | Out-Null
    }

    $zipPath = Join-Path $Config.TempDir "scrcpy.zip"

    # Download
    $downloaded = Download-File -Url $Config.ScrcpyUrl -OutputPath $zipPath
    if (-not $downloaded) { return $false }

    # Extract
    $extractTemp = Join-Path $Config.TempDir "scrcpy-extract"
    Extract-Zip -ZipPath $zipPath -DestPath $extractTemp

    # Find the scrcpy executable in extracted contents
    $scrcpyExe = Get-ChildItem -Path $extractTemp -Filter "scrcpy.exe" -Recurse | Select-Object -First 1
    if ($scrcpyExe) {
        $sourceDir = $scrcpyExe.DirectoryName
    }
    else {
        $sourceDir = $extractTemp
    }

    # Create target directory
    if (Test-Path $Config.ScrcpyInstallDir) {
        Remove-Item $Config.ScrcpyInstallDir -Recurse -Force
    }
    Copy-Item -Path $sourceDir -Destination $Config.ScrcpyInstallDir -Recurse -Force

    # Verify
    $scrcpyExePath = Join-Path $Config.ScrcpyInstallDir "scrcpy.exe"
    if (Test-Path $scrcpyExePath) {
        Write-Success "scrcpy installed to $($Config.ScrcpyInstallDir)"
        
        $version = & $scrcpyExePath --version 2>&1 | Out-String
        if ($version) {
            Write-Success "scrcpy verification passed: $($version.Trim().Split("`n")[0])"
        }
        return $true
    }
    else {
        Write-Failure "scrcpy installation failed — executable not found."
        return $false
    }
}

# ═══════════════════ MAIN ═══════════════════

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║         QA Device Tool - Dependency Installer                ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan

$results = @{
    AdbInstalled    = $false
    ScrcpyInstalled = $false
}

if ($All -or $InstallAdb) {
    $results.AdbInstalled = Install-Adb
}

if ($All -or $InstallScrcpy) {
    $results.ScrcpyInstalled = Install-Scrcpy
}

if (-not $All -and -not $InstallAdb -and -not $InstallScrcpy) {
    Write-Host ""
    Write-Host "  No install flags specified. Use:" -ForegroundColor Yellow
    Write-Host "    -All             Install all dependencies" -ForegroundColor Gray
    Write-Host "    -InstallAdb      Install ADB only" -ForegroundColor Gray
    Write-Host "    -InstallScrcpy   Install scrcpy only" -ForegroundColor Gray
    Write-Host "    -SkipPrompts     Skip confirmation prompts" -ForegroundColor Gray
}

# Clean up temp files
if (Test-Path $Config.TempDir) {
    Remove-Item $Config.TempDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "  ── Installation Summary ──" -ForegroundColor Yellow
Write-Host "  ADB:    $(if ($results.AdbInstalled) { '✓ Installed' } else { '✗ Not installed' })" -ForegroundColor $(if ($results.AdbInstalled) { 'Green' } else { 'Red' })
Write-Host "  scrcpy: $(if ($results.ScrcpyInstalled) { '✓ Installed' } else { '✗ Not installed' })" -ForegroundColor $(if ($results.ScrcpyInstalled) { 'Green' } else { 'Red' })
Write-Host ""
Write-Host "  NOTE: Run ConfigurePath.ps1 to add installed tools to your PATH." -ForegroundColor Gray
Write-Host ""
