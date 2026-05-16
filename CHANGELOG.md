# Changelog

## v3.1.0 — 2026-05-08

### Changed
- **iOS Backend** — Replaced libimobiledevice tool suite with pymobiledevice3.
  All 11 iOS service methods rewritten to use pymobiledevice3 subcommands.
- **No iTunes Dependency** — Removed Apple Mobile Device Service check; iOS
  communication runs purely through pymobiledevice3.

### Added
- iOS crash log retrieval (`crash ls`, `crash pull`)
- iOS diagnostics capture (`diagnostics info`)
- iOS Darwin notification posting (`notification post`)
- iOS network device discovery (`usbmux list --network`)
- IosService static parsers (`ParseLockdownInfo`, `ParseAppsList`, `ParseAfcLs`)
  with unit-test coverage (LogPro.Tests/Services/IosServiceParserTests.cs)

### Fixed
- All per-device pymobiledevice3 calls now pass `--udid <serial>` so multi-device
  setups target the correct iOS device.
- ToolLauncher working directory no longer leaks pymobiledevice3 pairing files
  into the user's Python install directory.
- ToolResolver no longer prepends pymobiledevice3 PyInstaller `_internal/` dirs
  to PATH (those would shadow the system Python's runtime DLLs).
- IosService falls back to `python -m pymobiledevice3` when the bundled
  PyInstaller exe is missing or fails its self-test.
- Build pipeline excludes pymobiledevice3 PyInstaller intermediates, source
  files, and unused numpy site-packages from publish output.

### Bundle
- Rebuilt the PyInstaller bundle (`tools/pymobiledevice3/pymobiledevice3.exe`)
  as a single-file exe excluding torch/numpy/pandas/matplotlib/scipy/parso/jedi.
  Final size: ~52 MB. Verified `version` and `usbmux list` against a real
  iPad16,3 / iOS 18.5.

### Known Limitations
- iOS screen recording, deep-link/open-URL, and pipeable iOS shell are not
  available via pymobiledevice3 — UI surfaces a clear "not supported" message.
- If the bundled PyInstaller exe is blocked by antivirus on the target machine,
  IosService falls back to `python -m pymobiledevice3` from the system Python
  install (Python 3.10+ with `pip install pymobiledevice3`).

## v3.0.0 — 2026-05-05

### Added
- **Complete Rebranding** - Application renamed from "QA/QC Device Tool" to "LogPro"
- **Self-Contained Build** - Includes .NET 8 runtime (no downloads needed)
- **ReadyToRun Optimization** - Faster cold startup performance
- **Advanced Installer** - Custom version handling with Pascal code:
  - Fresh install detection with welcome message
  - Same version reinstall with confirmation dialog
  - Upgrade detection with changelog popup
  - User data preservation on upgrade
  - Optional user data cleanup on uninstall
- **Per-User Installation** - Uses lowest privileges (no admin required)

### Fixed
- **Startup Crash** - Fixed missing icon resource reference (QAQCDeviceIcon.ico → LogProIcon.ico)
- **Missing Themes Folder** - DarkTheme.xaml now properly included in build output
- **Missing Assets Folder** - Application assets now copied to publish directory
- **Duplicate PATH Injection** - Fixed duplicate tool paths being added to system PATH
- **Installer Missing Files** - Added runtimes folder to installer package

### Improved
- **Build Script** - Updated build_installer.bat with correct paths and clean build process
- **Installer Reliability** - All version scenarios handled gracefully
- **Startup Logging** - Enhanced early startup diagnostics
- **Code Quality** - Removed debug symbols from release builds

### Changed
- **Portable Size** - Reduced from ~90MB to ~87MB (debug symbols removed)
- **Installer Size** - ~61MB (self-contained with .NET runtime)

## v2.4.0 — 2026-02-28

### Added
- Full installer packaging support with WiX v5 bootstrapper (bundles .NET 8 Desktop Runtime + iTunes drivers)
- Early startup diagnostics logging (`startup-debug.log`) capturing environment state before framework initialization
- Dynamic runtime path resolution — all tool paths resolve relative to `AppContext.BaseDirectory`
- Proper Windows storage architecture separation — writable data (logs, preferences, sessions, configs) stored under `%LOCALAPPDATA%\QAQCDeviceTool\`
- Serialized ADB transport via `SemaphoreSlim(1,1)` preventing concurrent USB access
- Per-binary WorkingDirectory in `ToolLauncher` — each native tool launches from its own directory

### Fixed
- **Installer launch failure** — 5 native WPF DLLs (`D3DCompiler_47_cor3.dll`, `PenImc_cor3.dll`, `PresentationNative_cor3.dll`, `vcruntime140_cor3.dll`, `wpfgfx_cor3.dll`) were missing from MSI package, preventing application startup
- **iMobileDevice tools missing from installer** — WiX `<Files>` glob path resolved incorrectly; fixed to include all 324 bundled files
- Hardcoded development paths replaced with dynamic resolution
- Permission issues when writing logs/settings under `Program Files`
- `ToolLauncher` WorkingDirectory forcing all processes to run from `iMobileDevice` directory

### Improved
- Android device polling interval increased from 5s to 10s to reduce USB transport pressure
- Tool resolution system (`ToolResolver`) with pattern-matched bundled tool discovery
- Runtime stability through global exception handlers and forensic logging
- Packaging reliability — MSI now includes complete publish output

## v2.3.0

- Initial release with Android + iOS device support
- Bundled scrcpy for screen mirroring
- Bundled libimobiledevice for iOS device management
- Session management and log capture
- WPF Fluent Design UI
