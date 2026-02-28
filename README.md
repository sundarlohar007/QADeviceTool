# QA/QC Device Tool

A robust Windows desktop utility built for QA/QC game testers. It automatically captures device logs, manages test sessions, and captures screenshots from connected Android and iOS devices.

![QA QC Device Tool](https://img.shields.io/badge/Platform-Windows_10%2F11-blue) ![Version](https://img.shields.io/badge/Version-2.4.0-green) ![License](https://img.shields.io/badge/License-Proprietary-red)

## Core Features

- **Auto Log Capture** — Instantly begins logging the moment a device is connected, and stops when disconnected.
- **Session Management** — Organizes captured logs, traces, and screenshots into meticulously timestamped session folders for each device.
- **Dynamic App-Specific Dual Logging** — Monitor full device logs alongside a fully isolated, automatically generated application-specific log file that tracks target PIDs intelligently (even through crashes) using partial keyword matching (e.g. `youtube`).
- **Live Log Viewer** — View highly readable device logs in real-time natively in the app, with filtering and auto-scroll capabilities.
- **Screen Mirroring (Android)** — Click-to-play Android device mirroring and remote control via bundled `scrcpy`.
- **Instant Snapshots** — Grab and save device screenshots straight from the tool with one click.
- **Bug Reports** — Generates an automated `.zip` archiving active device memory dumps, the last 10,000 log lines, and an instantaneous screenshot.
- **Android + iOS Support** — Full Android device management via bundled ADB, and iOS device information, app management, and syslog capture via bundled `libimobiledevice`.

## Architecture

- **Dynamic Runtime Path Resolution** — All tool paths are resolved relative to the application's base directory via `AppContext.BaseDirectory`, ensuring the app works from any installation location.
- **Windows Storage Compliance** — Application binaries and native tools reside in the install directory; writable data (logs, preferences, sessions, configs) are stored under `%LOCALAPPDATA%\QAQCDeviceTool\`.
- **Early Startup Diagnostics** — A `startup-debug.log` is written immediately on launch (before framework initialization) capturing executable path, architecture, and environment state for troubleshooting.
- **Serialized ADB Transport** — All ADB commands are serialized via semaphore to prevent concurrent USB transport access that can cause device offline flapping.
- **Per-Binary Working Directories** — Each native tool (adb, scrcpy, iMobileDevice) launches from its own directory, ensuring correct DLL resolution.

## Installation

### Standard Installation (Recommended)
1. Download the latest `QAQCDeviceTool-v2.4.0-Setup.exe` from the [Releases](https://github.com/sundarlohar007/QADeviceTool/releases) page.
2. Run the installer and follow the prompts. The bootstrapper will install .NET 8.0 Desktop Runtime and iTunes drivers if needed.
3. A shortcut will be added to your Start Menu and Desktop.

### Portable Version
1. Download the latest `QAQCDeviceTool-v2.4.0-win-x64.zip` from the [Releases](https://github.com/sundarlohar007/QADeviceTool/releases) page.
2. Extract the archive to any folder.
3. Launch `QADeviceTool.exe`.

*Note: For iOS support on Windows, ensure you have the Apple Mobile Device Service installed (typically bundled with iTunes).*

## Licensing and Third-Party Software

This application is proprietary software.  
However, it bundles third-party open-source software to facilitate device interactions:
- **scrcpy** (Apache License 2.0)
- **libimobiledevice** (GNU LGPL v2.1)

Please see the `licenses` directory included with the application distribution for the full license texts, ownership details, and copyright notices.

## Building from Source

### Portable Build
```bash
dotnet publish src/QADeviceTool.App/QADeviceTool.App.csproj -c Release --self-contained -r win-x64 -p:PublishSingleFile=true -o ./publish
```

### Full Installer Build
Run the provided `build_installer.bat` at the repository root, which will:
1. Publish the application (self-contained, single-file, win-x64)
2. Build the MSI installer package via WiX v5
3. Build the Setup Bootstrapper (bundles .NET runtime + iTunes drivers + MSI)

Requires: .NET 8.0 SDK, WiX Toolset v5

## Author

**Sundar Lohar**

---

*Powered by Google AntiGravity*
