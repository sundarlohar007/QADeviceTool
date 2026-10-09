param([string]$Sdk = $env:ANDROID_HOME)
$ErrorActionPreference = 'Stop'
if (-not $Sdk) { $Sdk = Join-Path $env:LOCALAPPDATA 'Android/Sdk' }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$build = Join-Path $repo 'publish/android-helper-build'
$output = Join-Path $repo 'publish/android-helper'
$androidJar = Join-Path $Sdk 'platforms/android-34/android.jar'
$d8 = Join-Path $Sdk 'build-tools/35.0.0/d8.bat'
if (-not (Test-Path -LiteralPath $androidJar) -or -not (Test-Path -LiteralPath $d8)) {
    throw 'Android SDK platform 34 and build-tools 35.0.0 are required to build the inventory helper.'
}
New-Item -ItemType Directory -Force -Path $build,$output | Out-Null
& javac --release 8 -encoding UTF-8 -cp $androidJar -d $build (Join-Path $PSScriptRoot 'android/AppInventory.java')
if ($LASTEXITCODE -ne 0) { throw 'Android helper compilation failed' }
& $d8 --min-api 23 --lib $androidJar --output (Join-Path $output 'inventory.jar') (Join-Path $build 'com/logpro/AppInventory.class')
if ($LASTEXITCODE -ne 0) { throw 'Android helper dex compilation failed' }
