param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [string]$IosRuntime = "publish/pymobiledevice3",
    [string]$AdbArchive = "",
    [int]$ManagedRevision = 1
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath $PublishDirectory).Path
$tools = Join-Path $root "tools"
New-Item -ItemType Directory -Path $tools -Force | Out-Null
function Remove-PayloadDirectory([string]$path) {
    $resolved = [IO.Path]::GetFullPath($path)
    if (-not $resolved.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe payload directory" }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
# Replace only this publish payload's iOS folder, preserving the complete runtime metadata.
Remove-PayloadDirectory (Join-Path $tools "pymobiledevice3")
Copy-Item -LiteralPath $IosRuntime -Destination (Join-Path $tools "pymobiledevice3") -Recurse
$scrcpy = Get-ChildItem -LiteralPath $tools -Directory | Where-Object { $_.Name -like "scrcpy-win64-*" } | Select-Object -First 1
if ($scrcpy) {
    $version = $scrcpy.Name -replace '^scrcpy-win64-v', ''
    $destination = Join-Path $tools "scrcpy"
    Remove-PayloadDirectory $destination
    Move-Item -LiteralPath $scrcpy.FullName -Destination $destination
    Set-Content -LiteralPath (Join-Path $destination "tool-version.txt") -Value $version -Encoding utf8NoBOM
}
if (-not $AdbArchive) {
    $AdbArchive = Join-Path $root "platform-tools.zip"
    Invoke-WebRequest -Uri "https://dl.google.com/android/repository/platform-tools-latest-windows.zip" -OutFile $AdbArchive
}
$extract = Join-Path $root "platform-tools-stage"
Remove-PayloadDirectory $extract
Expand-Archive -LiteralPath $AdbArchive -DestinationPath $extract
Remove-PayloadDirectory (Join-Path $tools "adb")
Move-Item -LiteralPath (Join-Path $extract "platform-tools") -Destination (Join-Path $tools "adb")
Remove-PayloadDirectory $extract
$properties = Get-Content -LiteralPath (Join-Path $tools "adb/source.properties") -Raw
$adbVersion = [regex]::Match($properties, 'Pkg.Revision\s*=\s*([\d.]+)').Groups[1].Value
if (-not $adbVersion) { throw "Platform-tools version missing" }
Set-Content -LiteralPath (Join-Path $tools "adb/tool-version.txt") -Value $adbVersion -Encoding utf8NoBOM
foreach ($name in @("adb", "scrcpy", "pymobiledevice3")) {
    $exe = Join-Path $tools "$name/$name.exe"
    if (-not (Test-Path -LiteralPath $exe)) { throw "Required tool missing: $name" }
    $argument = if ($name -eq "scrcpy") { "--version" } else { "version" }
    & $exe $argument
    if ($LASTEXITCODE -ne 0) { throw "Tool health check failed: $name" }
}
$placeholder = Join-Path $tools ".gitkeep"
if (Test-Path -LiteralPath $placeholder) { Remove-Item -LiteralPath $placeholder -Force }
$androidHelper = Join-Path $tools 'adb'
New-Item -ItemType Directory -Force -Path $androidHelper | Out-Null
Copy-Item -LiteralPath 'publish/android-helper/inventory.jar' -Destination $androidHelper
foreach ($name in @('adb', 'scrcpy', 'pymobiledevice3')) {
    $upstream = [Version](Get-Content -LiteralPath (Join-Path $tools "$name/tool-version.txt") -Raw).Trim()
    $packageVersion = "$($upstream.Major).$($upstream.Minor).$([Math]::Max(0,$upstream.Build)).$ManagedRevision"
    Set-Content -LiteralPath (Join-Path $tools "$name/tool-package-version.txt") -Value $packageVersion -Encoding utf8NoBOM
}
$entries = @(Get-ChildItem -LiteralPath $tools -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
    @{ path = $_.FullName.Substring($tools.Length + 1).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
ConvertTo-Json -InputObject $entries -Depth 3 | Set-Content -LiteralPath (Join-Path $root "tools-manifest.json") -Encoding utf8NoBOM
if ($AdbArchive -eq (Join-Path $root "platform-tools.zip")) { Remove-Item -LiteralPath $AdbArchive -Force }
