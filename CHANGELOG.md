# Changelog

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
