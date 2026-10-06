param(
    [string]$Python = "python",
    [string]$Destination = "publish/pymobiledevice3"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$entrypoint = Join-Path $PSScriptRoot "pymobiledevice3_entrypoint.py"
$target = [IO.Path]::GetFullPath((Join-Path $repoRoot $Destination))
$buildRoot = Join-Path $env:TEMP ("logpro-pymd3-build-" + [guid]::NewGuid().ToString("N"))

& $Python -c "import win32security, win32api, pywintypes"
if ($LASTEXITCODE -ne 0) { throw "Windows iOS runtime dependencies are missing. Install scripts/requirements-pymobiledevice3-build.txt with the selected Python interpreter." }
$version = ((& $Python -m pymobiledevice3 version) -join "`n").Trim()
if ($LASTEXITCODE -ne 0 -or $version -ne "9.12.0") {
    throw "Expected pymobiledevice3 9.12.0; found '$version'."
}

New-Item -ItemType Directory -Path $buildRoot | Out-Null
$previousPyInstallerConfig = $env:PYINSTALLER_CONFIG_DIR
$env:PYINSTALLER_CONFIG_DIR = Join-Path $buildRoot "config"
try {
    & $Python -m PyInstaller --onedir --clean --noconfirm `
        --name pymobiledevice3 `
        --collect-all pymobiledevice3 `
        --collect-all rich `
        --collect-all pytun_pmd3 `
        --recursive-copy-metadata pymobiledevice3 `
        --exclude-module numpy `
        --exclude-module pandas `
        --exclude-module scipy `
        --exclude-module matplotlib `
        --exclude-module torch `
        --exclude-module torchvision `
        --exclude-module tensorflow `
        --exclude-module onnxruntime `
        --exclude-module cv2 `
        --exclude-module PyQt5 `
        --exclude-module PyQt6 `
        --exclude-module PySide6 `
        --exclude-module pygame `
        --exclude-module sqlalchemy `
        --exclude-module openpyxl `
        --exclude-module numba `
        --exclude-module llvmlite `
        --distpath (Join-Path $buildRoot "dist") `
        --workpath (Join-Path $buildRoot "work") `
        --specpath $buildRoot `
        $entrypoint
    if ($LASTEXITCODE -ne 0) { throw "PyInstaller failed." }

    $builtExe = Join-Path $buildRoot "dist\pymobiledevice3\pymobiledevice3.exe"
    & (Join-Path $PSScriptRoot "test-pymobiledevice3.ps1") -Executable $builtExe
    if ($LASTEXITCODE -ne 0) { throw "Bundled CLI smoke test failed." }

    $publishRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "publish")) + [IO.Path]::DirectorySeparatorChar
    if (-not $target.StartsWith($publishRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Runtime destination must be a subdirectory of publish." }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -Path (Join-Path $buildRoot "dist\pymobiledevice3\*") -Destination $target -Recurse -Force
    Set-Content -LiteralPath (Join-Path $target "tool-version.txt") -Value $version -Encoding utf8NoBOM
}
finally {
    $env:PYINSTALLER_CONFIG_DIR = $previousPyInstallerConfig
    $resolvedTemp = [IO.Path]::GetFullPath($env:TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolvedBuild = [IO.Path]::GetFullPath($buildRoot)
    if ($resolvedBuild.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedBuild)) {
        Remove-Item -LiteralPath $resolvedBuild -Recurse -Force
    }
}
