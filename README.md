# LogPro

A robust Windows desktop utility built for QA/QC game testers. It automatically captures device logs, manages test sessions, and captures screenshots from connected Android and iOS devices.

![LogPro](https://img.shields.io/badge/Platform-Windows_10%2F11-blue) ![Version](https://img.shields.io/badge/Version-3.1.0-green) ![License](https://img.shields.io/badge/License-Proprietary-red)

## Core Features

- **Auto Log Capture** — Instantly begins logging the moment a device is connected, and stops when disconnected.
- **Session Management** — Organizes captured logs, traces, and screenshots into meticulously timestamped session folders for each device.
- **Dynamic App-Specific Dual Logging** — Monitor full device logs alongside a fully isolated, automatically generated application-specific log file that tracks target PIDs intelligently (even through crashes) using partial keyword matching (e.g. `youtube`).
- **Live Log Viewer** — View highly readable device logs in real-time natively in the app, with filtering and auto-scroll capabilities.
- **Screen Mirroring (Android)** — Click-to-play Android device mirroring and remote control via bundled `scrcpy`.
- **Instant Snapshots** — Grab and save device screenshots straight from the tool with one click.
- **Bug Reports** — Generates an automated `.zip` archiving active device memory dumps, the last 10,000 log lines, and an instantaneous screenshot.
- **Android + iOS Support** — Full Android device management via bundled ADB, and iOS device information, app management, syslog, crash logs, and diagnostics via bundled `pymobiledevice3`.

## Architecture

- **Dynamic Runtime Path Resolution** — All tool paths are resolved relative to the application's base directory via `AppContext.BaseDirectory`, ensuring the app works from any installation location.
- **Windows Storage Compliance** — Application binaries and native tools reside in the install directory; writable data (logs, preferences, sessions, configs) are stored under `%LOCALAPPDATA%\LogPro\`.
- **Early Startup Diagnostics** — A `startup-debug.log` is written immediately on launch (before framework initialization) capturing executable path, architecture, and environment state for troubleshooting.
- **Serialized ADB Transport** — All ADB commands are serialized via semaphore to prevent concurrent USB transport access that can cause device offline flapping.
- **Per-Binary Working Directories** — Each native tool (adb, scrcpy, pymobiledevice3) launches from its own directory, ensuring correct DLL resolution.

## Installation

### Standard Installation (Recommended)
1. Download the latest `LogPro_v3.0.0.exe` from the [Releases](https://github.com/sundarlohar007/QADeviceTool/releases) page.
2. Run the installer and follow the prompts.
3. Launch LogPro from the Start Menu or Desktop shortcut.

### Silent Installation (Unattended)
For automated deployments or batch scripts:

```batch
LogPro_v3.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

**Silent Install Options:**
| Parameter | Description |
|-----------|-------------|
| `/VERYSILENT` | No UI, suppress all messages |
| `/SILENT` | Minimal UI, show progress only |
| `/SUPPRESSMSGBOXES` | Suppress message boxes |
| `/NORESTART` | Don't restart after install |
| `/DIR="C:\Path"` | Install to custom directory |
| `/LOG="C:\log.txt"` | Create installation log |

### Portable Version
1. Download `LogPro_Portable_v3.0.0.zip` from the releases page.
2. Extract to any folder (e.g., `C:\Tools\LogPro`).
3. Run `LogPro.exe`.

## Usage

### Device Connection
1. Connect your Android device via USB and enable USB Debugging in Developer Options.
2. For iOS devices, plug in via USB and tap "Trust" on the device. No iTunes required.
3. The app auto-detects connected devices. Select from the dropdown in the header.

### Starting a Session
1. Go to **Sessions** tab.
2. Select your device from the dropdown.
3. Optionally name your session.
4. Click **+ New Session**.
5. Toggle **Auto-capture** to start logging immediately.

### Screen Mirroring
1. Go to **Devices** tab.
2. Select your Android device.
3. Click **Mirror Screen**.
4. A window opens showing your device screen in real-time.

### Taking Snapshots
While in a session, click **Snap** to capture a screenshot instantly.

### Exporting Logs
1. Go to **Sessions** tab and select your session.
2. Click **Export CSV** or **Export JSON**.
3. Choose save location.

## System Requirements

- Windows 10/11 (64-bit)
- No additional runtime required (self-contained build)
- USB debugging enabled for Android devices
- For iOS: tap "Trust this computer" on first connect (no iTunes required)
- If the bundled pymobiledevice3 PyInstaller exe is unusable on your machine,
  install Python 3.10+ and run `pip install pymobiledevice3` as a fallback.

## Third-Party Software

This application bundles the following open-source software:

- **scrcpy** - Android screen mirroring (Apache License 2.0)
- **pymobiledevice3** - iOS device communication (GNU GPL v3)

See `licenses` folder for full license texts.

## Troubleshooting

### Startup Issues
Check `%LOCALAPPDATA%\LogPro\startup-debug.log` for early startup diagnostics.

### Device Not Detected
1. Enable USB Debugging on Android (Settings > Developer Options).
2. On first connect, check "Allow USB debugging" prompt on device.
3. Try a different USB cable (some cables are charge-only).
4. Try a different USB port (prefer USB 2.0 ports).

### iOS Issues
1. Tap "Trust this computer" on the iOS device when prompted.
2. For iOS 17+, enable Developer Mode in Settings > Privacy & Security.
3. If iOS shows "pymobiledevice3 not responding" in Settings > Tools, install
   Python 3.10+ and run `pip install pymobiledevice3` (the bundled PyInstaller
   build may be incompatible with your OS / antivirus).

## Version History

See [CHANGELOG.md](CHANGELOG.md) for detailed version history.

## License

Copyright (c) 2026 Sundar Lohar. All rights reserved.
