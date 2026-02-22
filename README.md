# QA/QC Device Tool

A Windows desktop utility for QA/QC game testers. Captures device logs, manages test sessions, and takes screenshots from Android & iOS devices.

## Features

- **Auto Log Capture** — Automatically starts logging when a device is connected and stops when it disconnects
- **Session Management** — Organize logs into timestamped session folders per device
- **Live Log Viewer** — View device logs in real-time with auto-scroll
- **Screen Mirroring** — Mirror Android device screens via scrcpy
- **Screenshots** — Capture device screenshots directly from the tool
- **Multi-Device Support** — Android (via ADB) and iOS (via idevicesyslog)

## Requirements

- Windows 10/11
- .NET 8.0 Desktop Runtime
- ADB (Android Debug Bridge) for Android devices
- libimobiledevice for iOS devices (optional)
- scrcpy for screen mirroring (optional)

## Installation

1. Download the latest release zip from [Releases](https://github.com/sundarlohar007/QADeviceTool/releases)
2. Extract to any folder
3. Run `QADeviceTool.App.exe`

## Building from Source

```bash
dotnet publish src/QADeviceTool.App/QADeviceTool.App.csproj -c Release --self-contained -r win-x64 -p:PublishSingleFile=true -o ./publish
```

## Acknowledgments

This tool relies on the following open-source projects:

- **[scrcpy](https://github.com/Genymobile/scrcpy)** — Android screen mirroring and control. Developed by [Genymobile](https://github.com/Genymobile).
- **[libimobiledevice](https://github.com/libimobiledevice/libimobiledevice)** — iOS device communication and log capture. Developed by the [libimobiledevice](https://github.com/libimobiledevice) team.

## Author

**Sundar Lohar**  
---

*Powered by Google AntiGravity*
