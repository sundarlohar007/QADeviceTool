param(
    [string]$installDir
)

try {
    $adbZip = Join-Path $installDir "assets\platform-tools-latest-windows.zip"
    $driverZip = Join-Path $installDir "assets\usb_driver_r13-windows.zip"
    
    $adbDest = Join-Path $installDir "platform-tools"
    $driverDest = Join-Path $installDir "usb_driver"
    
    # 1. Check if driver is already installed before extracting/installing
    $isDriverInstalled = $false
    try {
        if (pnputil /enum-drivers | Select-String "android_winusb.inf" -Quiet) {
            $isDriverInstalled = $true
        }
    }
    catch { }

    if (-not $isDriverInstalled -and (Test-Path $driverZip)) {
        if (-not (Test-Path $driverDest)) {
            New-Item -ItemType Directory -Force -Path $driverDest | Out-Null
        }
        Expand-Archive -Path $driverZip -DestinationPath $driverDest -Force
        
        $infPath = Join-Path $driverDest "usb_driver\android_winusb.inf"
        if (Test-Path $infPath) {
            pnputil /add-driver $infPath /install
        }
    }
    
    # 2. Extract ADB platform-tools if not already present
    $adbExePath = Join-Path $adbDest "adb.exe"
    if ((Test-Path $adbZip) -and (-not (Test-Path $adbExePath))) {
        # Expand-Archive extracts exactly what's in the zip. 
        # platform-tools-latest-windows.zip contains a 'platform-tools' folder.
        # So we extract it directly to INSTALLFOLDER, which will extract the platform-tools folder.
        Expand-Archive -Path $adbZip -DestinationPath $installDir -Force
    }
    
    # 3. Add ADB directory to SYSTEM PATH
    if (Test-Path $adbDest) {
        $path = [Environment]::GetEnvironmentVariable("PATH", "Machine")
        if ($path -notmatch [regex]::Escape($adbDest)) {
            $newPath = $path + ";" + $adbDest
            [Environment]::SetEnvironmentVariable("PATH", $newPath, "Machine")
        }
    }
}
catch {
    # Fail silently to not break installer
    exit 0
}
