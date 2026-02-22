#Requires -Version 5.1
<#
.SYNOPSIS
    Detects availability of required dependencies for QA Device Tool.

.DESCRIPTION
    Checks for:
    - ADB (Android Debug Bridge) from Android platform-tools
    - scrcpy (Android screen mirroring)
    - libimobiledevice (idevice_id, ideviceinfo, idevicesyslog)
    - Apple Mobile Device Support (iTunes)
    
    Returns a JSON report with status of each dependency.

.OUTPUTS
    JSON string with dependency status.
#>

[CmdletBinding()]
param(
    [switch]$OutputJson
)

$ErrorActionPreference = 'SilentlyContinue'

function Test-CommandAvailable {
    param([string]$Command)
    $result = Get-Command $Command -ErrorAction SilentlyContinue
    return ($null -ne $result)
}

function Get-CommandVersion {
    param([string]$Command, [string]$VersionArg = "--version")
    try {
        $output = & $Command $VersionArg 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0 -or $output) {
            return $output.Trim()
        }
    } catch {}
    return $null
}

function Get-CommandPath {
    param([string]$Command)
    $cmd = Get-Command $Command -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }
    return $null
}

# ═══════════════════ CHECK ADB ═══════════════════

$adbStatus = @{
    Name        = "ADB (Android Debug Bridge)"
    Command     = "adb"
    IsInstalled = $false
    Version     = "Not found"
    Path        = ""
    Message     = ""
}

if (Test-CommandAvailable "adb") {
    $adbStatus.IsInstalled = $true
    $versionOutput = Get-CommandVersion "adb" "version"
    if ($versionOutput -match "version ([\d.]+)") {
        $adbStatus.Version = $Matches[1]
    } else {
        $adbStatus.Version = "Installed"
    }
    $adbStatus.Path = Get-CommandPath "adb"
    $adbStatus.Message = "ADB is ready."
} else {
    # Check common install locations
    $commonPaths = @(
        "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe",
        "C:\Program Files\Android\platform-tools\adb.exe",
        "$env:USERPROFILE\AppData\Local\Android\Sdk\platform-tools\adb.exe"
    )
    foreach ($p in $commonPaths) {
        if (Test-Path $p) {
            $adbStatus.IsInstalled = $true
            $adbStatus.Path = $p
            $adbStatus.Message = "ADB found at $p but not in PATH."
            break
        }
    }
    if (-not $adbStatus.IsInstalled) {
        $adbStatus.Message = "ADB not found. Android platform-tools must be installed."
    }
}

# ═══════════════════ CHECK SCRCPY ═══════════════════

$scrcpyStatus = @{
    Name        = "scrcpy (Screen Mirror)"
    Command     = "scrcpy"
    IsInstalled = $false
    Version     = "Not found"
    Path        = ""
    Message     = ""
}

if (Test-CommandAvailable "scrcpy") {
    $scrcpyStatus.IsInstalled = $true
    $versionOutput = Get-CommandVersion "scrcpy" "--version"
    if ($versionOutput -match "(\d+\.\d+(\.\d+)?)") {
        $scrcpyStatus.Version = $Matches[1]
    } else {
        $scrcpyStatus.Version = "Installed"
    }
    $scrcpyStatus.Path = Get-CommandPath "scrcpy"
    $scrcpyStatus.Message = "scrcpy is ready for screen mirroring."
} else {
    $scrcpyStatus.Message = "scrcpy not found. Screen mirroring will be unavailable."
}

# ═══════════════════ CHECK LIBIMOBILEDEVICE ═══════════════════

$iosStatus = @{
    Name        = "libimobiledevice (iOS Tools)"
    Command     = "idevice_id"
    IsInstalled = $false
    Version     = "Not found"
    Path        = ""
    Message     = ""
}

if (Test-CommandAvailable "idevice_id") {
    $iosStatus.IsInstalled = $true
    $iosStatus.Version = "Installed"
    $iosStatus.Path = Get-CommandPath "idevice_id"
    $iosStatus.Message = "iOS tools are ready."
} else {
    $iosStatus.Message = "libimobiledevice not found. iOS features will be unavailable."
}

# ═══════════════════ CHECK ITUNES / APPLE MOBILE DEVICE SUPPORT ═══════════════════

$itunesStatus = @{
    Name        = "Apple Mobile Device Support"
    Command     = "iTunes"
    IsInstalled = $false
    Version     = "Not found"
    Path        = ""
    Message     = ""
}

# Check registry for Apple Mobile Device Support
$appleKeys = @(
    "HKLM:\SOFTWARE\Apple Inc.\Apple Mobile Device Support",
    "HKLM:\SOFTWARE\WOW6432Node\Apple Inc.\Apple Mobile Device Support"
)
foreach ($key in $appleKeys) {
    if (Test-Path $key) {
        $itunesStatus.IsInstalled = $true
        $itunesStatus.Version = (Get-ItemProperty $key -ErrorAction SilentlyContinue).Version
        if (-not $itunesStatus.Version) { $itunesStatus.Version = "Installed" }
        $itunesStatus.Message = "Apple Mobile Device Support is installed."
        break
    }
}
if (-not $itunesStatus.IsInstalled) {
    $itunesStatus.Message = "Apple Mobile Device Support not found. Install iTunes for iOS support."
}

# ═══════════════════ OUTPUT ═══════════════════

$report = @{
    Timestamp    = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
    Dependencies = @($adbStatus, $scrcpyStatus, $iosStatus, $itunesStatus)
    AllInstalled = ($adbStatus.IsInstalled -and $scrcpyStatus.IsInstalled -and $iosStatus.IsInstalled)
    MinimumMet   = $adbStatus.IsInstalled
}

if ($OutputJson) {
    $report | ConvertTo-Json -Depth 3
} else {
    Write-Host ""
    Write-Host "╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
    Write-Host "║           QA Device Tool - Dependency Check                  ║" -ForegroundColor Cyan
    Write-Host "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
    Write-Host ""

    foreach ($dep in $report.Dependencies) {
        $icon = if ($dep.IsInstalled) { "[OK]" } else { "[MISSING]" }
        $color = if ($dep.IsInstalled) { "Green" } else { "Red" }

        Write-Host "  $icon " -NoNewline -ForegroundColor $color
        Write-Host "$($dep.Name)" -ForegroundColor White
        Write-Host "        Version: $($dep.Version)" -ForegroundColor Gray
        Write-Host "        Path:    $($dep.Path)" -ForegroundColor Gray
        Write-Host "        Status:  $($dep.Message)" -ForegroundColor Gray
        Write-Host ""
    }

    if ($report.MinimumMet) {
        Write-Host "  ✓ Minimum requirements met (ADB available)" -ForegroundColor Green
    } else {
        Write-Host "  ✗ Minimum requirements NOT met (ADB missing)" -ForegroundColor Red
    }
    Write-Host ""
}
