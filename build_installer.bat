@echo off
echo ============================================
echo  QA/QC Device Tool - Bootstrapper Builder
echo ============================================
echo.

REM Step 1: Publish the app
echo [1/3] Publishing application...
dotnet publish src\QADeviceTool.App\QADeviceTool.App.csproj -c Release --self-contained -r win-x64 -p:PublishSingleFile=true -o .\publish
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Publish failed!
    exit /b 1
)
xcopy /E /I /Y .\licenses .\publish\licenses
echo     Done.
echo.

REM Step 2: Build MSI using WiX
echo [2/3] Building MSI installer...
wix build installer\Package.wxs -b publish=publish -b installer=installer -o publish\QAQCDeviceTool.msi -arch x64 -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: MSI build failed!
    exit /b 1
)
echo     Done.
echo.

REM Step 3: Build setup.exe using WiX Burn (MSBuild v5 SDK)
echo [3/3] Building Setup Bootstrapper...
dotnet build installer\Bootstrapper\Bootstrapper.wixproj -c Release /p:Platform=x64
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Bootstrapper build failed!
    exit /b 1
)

REM Move the setup.exe to the root output folder
copy /Y "installer\Bootstrapper\bin\x64\Release\QAQCDeviceTool-v2.3.0-Setup.exe" ".\QAQCDeviceTool-v2.3.0-Setup.exe"
echo     Done.
echo.

echo ============================================
echo  Build complete!
echo  Installer: QAQCDeviceTool-v2.3.0-Setup.exe
echo ============================================
