@echo off
echo ============================================
echo  QA/QC Device Tool - MSI Installer Builder
echo ============================================
echo.

REM Step 1: Publish the app
echo [1/3] Publishing application...
dotnet publish src\QADeviceTool.App\QADeviceTool.App.csproj -c Release --self-contained -r win-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Publish failed!
    exit /b 1
)
echo     Done.
echo.

REM Step 2: Build MSI using WiX
echo [2/3] Building MSI installer...
wix build installer\Package.wxs -b publish=publish -b installer=installer -o installer\QAQCDeviceTool-v2.0.0-Setup.msi -arch x64 -ext WixToolset.UI.wixext
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: MSI build failed!
    exit /b 1
)
echo     Done.
echo.

REM Step 3: Create ZIP as well
echo [3/3] Creating ZIP package...
powershell -Command "Compress-Archive -Path '.\publish\*' -DestinationPath '.\QAQCDeviceTool-v2.0.0-win-x64.zip' -Force"
echo     Done.
echo.

echo ============================================
echo  Build complete!
echo  MSI: installer\QAQCDeviceTool-v2.0.0-Setup.msi
echo  ZIP: QAQCDeviceTool-v2.0.0-win-x64.zip
echo ============================================
