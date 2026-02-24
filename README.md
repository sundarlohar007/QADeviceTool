# QA/QC Device Tool

A robust Windows desktop utility built for QA/QC game testers. It automatically captures device logs, manages test sessions, and captures screenshots from connected Android and iOS devices.

![QA QC Device Tool](https://img.shields.io/badge/Platform-Windows_10%2F11-blue) ![License](https://img.shields.io/badge/License-Proprietary-red)

## Core Features

- **Auto Log Capture** — Instantly begins logging the moment a device is connected, and stops when disconnected.
- **Session Management** — Organizes captured logs, traces, and screenshots into meticulously timestamped session folders for each device.
- **Live Log Viewer** — View highly readable device logs in real-time natively in the app, with filtering and auto-scroll capabilities.
- **Screen Mirroring (Android)** — Click-to-play Android device mirroring and remote control via bundled `scrcpy`.
- **Instant Snapshots** — Grab and save device screenshots straight from the tool with one click.
- **Multi-Platform Support** — Fully compatible with Android (via ADB) and iOS (via libimobiledevice).
- **Dark Mode UI** — Clean, modern WPF interface perfectly suited for dark environments.

## Installation

### Standard Installation (Recommended)
1. Download the latest `.msi` package from the [Releases](https://github.com/sundarlohar007/QADeviceTool/releases) page.
2. Run the installer and follow the prompts. The application will be installed to `C:\Program Files\QAQCDeviceTool` and a shortcut will be added to your Start Menu and Desktop.

### Portable Version
1. Download the latest `-win-x64.zip` release from the [Releases](https://github.com/sundarlohar007/QADeviceTool/releases) page.
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

To compile the standalone desktop application:
```bash
dotnet publish src/QADeviceTool.App/QADeviceTool.App.csproj -c Release --self-contained -r win-x64 -p:PublishSingleFile=true -o ./publish
```

To compile the MSI Installer setup package (requires WiX v4):
```bash
wix build installer\Package.wxs -b publish=publish -b installer=installer -o installer\QAQCDeviceTool-v2.2.0-Setup.msi -arch x64 -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext
```
Or simply run the provided `build_installer.bat` at the repository root.

## Author

**Sundar Lohar**  
sundar.lohar@ubisoft.com

---

*Powered by Google AntiGravity*
