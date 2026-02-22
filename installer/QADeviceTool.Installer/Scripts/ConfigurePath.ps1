#Requires -Version 5.1
<#
.SYNOPSIS
    Configures the User PATH to include QA Device Tool dependencies.

.DESCRIPTION
    Adds the following paths to the User PATH (if they exist and aren't already in PATH):
    - C:\Program Files\Android\platform-tools  (ADB)
    - C:\Program Files\scrcpy                  (scrcpy)

    Features:
    - Backs up original PATH to registry before modification
    - Only adds paths that actually exist on disk
    - Never modifies System PATH (only User PATH)
    - Supports rollback via -Rollback flag
    - Requires explicit user consent (unless -SkipPrompts)

.PARAMETER SkipPrompts
    Skip user confirmation prompts.

.PARAMETER Rollback
    Restore the previous PATH from backup.

.PARAMETER DryRun
    Show what would change without making modifications.
#>

[CmdletBinding()]
param(
    [switch]$SkipPrompts,
    [switch]$Rollback,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# ═══════════════════ CONFIGURATION ═══════════════════

$PathsToAdd = @(
    "C:\Program Files\Android\platform-tools",
    "C:\Program Files\scrcpy"
)

$BackupRegistryKey = "HKCU:\Software\QADeviceTool"
$BackupRegistryName = "PathBackup"

# ═══════════════════ HELPER FUNCTIONS ═══════════════════

function Get-UserPath {
    return [Environment]::GetEnvironmentVariable("PATH", "User")
}

function Set-UserPath {
    param([string]$NewPath)
    [Environment]::SetEnvironmentVariable("PATH", $NewPath, "User")
}

function Backup-CurrentPath {
    $currentPath = Get-UserPath
    
    if (-not (Test-Path $BackupRegistryKey)) {
        New-Item -Path $BackupRegistryKey -Force | Out-Null
    }
    
    Set-ItemProperty -Path $BackupRegistryKey -Name $BackupRegistryName -Value $currentPath
    Write-Host "  ✓ PATH backed up to registry" -ForegroundColor Green
}

function Restore-PathFromBackup {
    if (-not (Test-Path $BackupRegistryKey)) {
        Write-Host "  ✗ No backup found in registry." -ForegroundColor Red
        return $false
    }
    
    $backup = Get-ItemProperty -Path $BackupRegistryKey -Name $BackupRegistryName -ErrorAction SilentlyContinue
    if (-not $backup) {
        Write-Host "  ✗ No PATH backup found." -ForegroundColor Red
        return $false
    }
    
    Set-UserPath -NewPath $backup.$BackupRegistryName
    Write-Host "  ✓ PATH restored from backup." -ForegroundColor Green
    return $true
}

# ═══════════════════ MAIN LOGIC ═══════════════════

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║          QA Device Tool - PATH Configuration                 ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

# Handle rollback
if ($Rollback) {
    Write-Host "  ── Restoring PATH from backup ──" -ForegroundColor Yellow
    Restore-PathFromBackup
    Write-Host ""
    return
}

# Get current User PATH
$currentPath = Get-UserPath
$pathEntries = $currentPath -split ';' | Where-Object { $_ -ne '' }

Write-Host "  Current User PATH entries: $($pathEntries.Count)" -ForegroundColor Gray
Write-Host ""

# Determine what needs to be added
$toAdd = @()
foreach ($p in $PathsToAdd) {
    if (-not (Test-Path $p)) {
        Write-Host "  ⊘ $p — directory does not exist, skipping" -ForegroundColor Gray
        continue
    }
    
    $alreadyInPath = $pathEntries | Where-Object { $_.TrimEnd('\') -eq $p.TrimEnd('\') }
    if ($alreadyInPath) {
        Write-Host "  ✓ $p — already in PATH" -ForegroundColor Green
    }
    else {
        Write-Host "  + $p — will be added" -ForegroundColor Yellow
        $toAdd += $p
    }
}

Write-Host ""

if ($toAdd.Count -eq 0) {
    Write-Host "  No changes needed. All paths are already configured." -ForegroundColor Green
    Write-Host ""
    return
}

# Show summary
Write-Host "  ── Changes to be made ──" -ForegroundColor Yellow
Write-Host ""
foreach ($p in $toAdd) {
    Write-Host "    ADD: $p" -ForegroundColor Cyan
}
Write-Host ""

if ($DryRun) {
    Write-Host "  [DRY RUN] No changes were made." -ForegroundColor Yellow
    return
}

# Confirm with user
if (-not $SkipPrompts) {
    $response = Read-Host "  Add these paths to your User PATH? (Y/N)"
    if ($response -ne 'Y' -and $response -ne 'y') {
        Write-Host "  Cancelled. No changes made." -ForegroundColor Gray
        return
    }
}

# Backup current PATH
Backup-CurrentPath

# Add new entries
$newPath = $currentPath
foreach ($p in $toAdd) {
    $newPath = "$newPath;$p"
}

# Remove any trailing/leading semicolons and duplicates
$newPath = ($newPath -split ';' | Where-Object { $_ -ne '' } | Select-Object -Unique) -join ';'

# Apply
Set-UserPath -NewPath $newPath
Write-Host ""
Write-Host "  ✓ PATH updated successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "  NOTE: You may need to restart your terminal or sign out/in" -ForegroundColor Yellow
Write-Host "        for PATH changes to take effect." -ForegroundColor Yellow
Write-Host ""

# Verify the tools are now accessible
Write-Host "  ── Verification ──" -ForegroundColor Yellow
foreach ($p in $toAdd) {
    $exes = Get-ChildItem -Path $p -Filter "*.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($exes) {
        Write-Host "  ✓ Found executables in $p" -ForegroundColor Green
    }
}
Write-Host ""
