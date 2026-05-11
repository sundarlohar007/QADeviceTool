@echo off
echo ============================================
echo  LogPro - Build Script
echo ============================================
echo.

REM Step 1: Clean previous builds
echo [1/7] Cleaning previous builds...
if exist publish rmdir /S /Q publish
if not exist publish mkdir publish
echo     Done.

REM Step 2: Publish the app as self-contained
echo [2/7] Publishing self-contained application...
dotnet publish src\QADeviceTool.App\QADeviceTool.App.csproj -c Release --self-contained -r win-x64 -o .\publish\app
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Publish failed!
    exit /b 1
)
echo     Done.

REM Step 3: Copy additional files (Themes, Assets, Licenses)
echo [3/7] Copying additional files...
xcopy /E /I /Y "src\QADeviceTool.App\Themes" "publish\app\Themes"
xcopy /E /I /Y "src\QADeviceTool.App\Assets" "publish\app\Assets"
xcopy /E /I /Y .\licenses .\publish\app\licenses
echo     Done.

REM Step 4: Copy installer script
echo [4/7] Copying installer script...
copy /Y installer\setup.iss publish\setup.iss
echo     Done.

REM Step 5: Build installer using Inno Setup
echo [5/7] Building installer...
echo Running: C:\InnoSetup\ISCC.exe "publish\setup.iss"
"C:\InnoSetup\ISCC.exe" "publish\setup.iss"
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Installer build failed!
    exit /b 1
)
echo     Done.

REM Step 6: Move installer from nested folder if needed
echo [6/7] Moving installer to correct location...
if exist publish\publish\LogPro_v3.1.0.exe (
    move /Y publish\publish\LogPro_v3.1.0.exe publish\LogPro_v3.1.0.exe
    rmdir /S /Q publish\publish
)
echo     Done.

REM Step 7: Create portable ZIP
echo [7/7] Creating portable ZIP...
if exist publish\LogPro_Portable_v3.1.0.zip del /F publish\LogPro_Portable_v3.1.0.zip
powershell.exe -nologo -noprofile -command "Compress-Archive -Path 'publish\app\*' -DestinationPath 'publish\LogPro_Portable_v3.1.0.zip' -Force"
echo     Done.

echo.
echo ============================================
echo  Build complete!
echo  Installer: publish\LogPro_v3.1.0.exe
echo  Portable:  publish\LogPro_Portable_v3.1.0.zip
echo ============================================