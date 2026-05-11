# LogPro Test Plan

## Version 3.0.0

---

## 1. Automated Test Suite

The automated test suite is located in `src/LogPro.Tests/` and includes:

### Unit Tests (36 tests passing)

**Models:**
- `DeviceInfoTests` - DisplayName, DisplayNotes, PlatformIcon, StatusText
- `LogSessionTests` - Default values, DurationText, StatusIcon

**Helpers:**
- `PathHelperTests` - Sessions directory, config path, command resolution
- `ToolResolverTests` - Tool path resolution, bundling detection

**Services:**
- `PreferencesServiceTests` - AppPreferences, DevicePreference defaults

---

## 2. Manual Test Checklist

### Android Device Testing

| Test Case | Steps | Expected Result |
|-----------|-------|------------------|
| **Device Detection** | Connect Android device via USB, enable USB Debugging | Device appears in dropdown within 5 seconds |
| **Device Details** | Select connected device | Model, OS version, battery level displayed |
| **Log Capture Start** | Start session, toggle Auto-capture | Log file created in session folder |
| **Log Capture Stop** | Stop session | Log file finalized, line count shown |
| **Screenshot Capture** | Click "Snap" button | Screenshot saved to session folder |
| **Screen Mirror** | Click "Mirror Screen" (Android) | scrcpy window opens with device screen |
| **App List** | Go to Apps tab, click refresh | List of installed apps displayed |
| **APK Install** | Click Install, select APK | Package installs, appears in app list |
| **File Explorer** | Navigate to /sdcard/ | Files and folders displayed |
| **Bug Report** | Click "Create Bug Report" | ZIP file created with logs + screenshot |
| **Session Export** | Click Export CSV/JSON | File saved to chosen location |
| **Device Disconnect** | Unplug USB device | Device removed from list, session stopped |
| **Wireless ADB** | Enable wireless in settings | Device accessible via IP:port |

### iOS Device Testing

| Test Case | Steps | Expected Result |
|-----------|-------|------------------|
| **Device Detection** | Connect iOS device via USB | Device appears in dropdown (no iTunes required) |
| **Trust Dialog** | Connect new device | Prompt appears on device |
| **Device Details** | Select connected device | Model, iOS version, battery displayed |
| **Syslog Capture** | Start session | iOS syslog captured to session file |
| **App List** | Go to Apps tab | List of installed apps displayed |
| **App Install** | Click Install, select IPA | IPA installs, appears in app list |
| **App Uninstall** | Click Uninstall on selected app | App removed from list |
| **File Explorer** | Navigate to /var/mobile/Media | Files accessible (DCIM, Photos, Library) |
| **Screenshot** | Click Snap | PNG saved to session folder |
| **Crash Log List** | Open bug report | Crash log filenames included |
| **Diagnostics Capture** | Open bug report | `diagnostics info` output included |
| **Multi-device** | Connect 2+ iOS devices | Each device targeted via `--udid <serial>` |

### Installation Testing

| Test Case | Steps | Expected Result |
|-----------|-------|------------------|
| **Fresh Install** | Run installer on clean system | App installs, launches successfully |
| **Silent Install** | Run with `/VERYSILENT` flag | No UI, app installed |
| **Upgrade Install** | Install newer version over existing | Settings preserved, app updated |
| **Downgrade Block** | Install older version | Installer shows error, prevents install |
| **Uninstall** | Run uninstaller | App removed, optional user data cleanup |
| **Portable Run** | Extract ZIP, run exe | App launches from any folder |

### Startup & Core

| Test Case | Steps | Expected Result |
|-----------|-------|------------------|
| **First Launch** | Start app first time | No crashes, startup-debug.log created |
| **Settings Persist** | Change settings, restart | Settings retained |
| **Theme Loads** | Start app | UI theme renders correctly |
| **Tools Detection** | Check Settings > Tools | ADB, scrcpy status shown |

---

## 3. Test Environment

- **OS**: Windows 10/11 (64-bit)
- **.NET**: Self-contained (no runtime needed)
- **Test Devices**: Android 13+, iOS 16+
- **USB**: USB 2.0/3.0 ports

---

## 4. Known Limitations

- iOS screen mirroring not supported
- iOS screen recording not supported (no pymobiledevice3 equivalent)
- iOS deep-link / open-URL not supported via pymobiledevice3
- iOS shell is not pipeable (`developer shell` is an interactive IPython REPL)
- iOS path requires Python 3.10+ with `pip install pymobiledevice3` if the bundled
  PyInstaller exe is unavailable on the target machine
- Wireless ADB requires device and PC on same network
- Some Android OEMs may require additional USB debugging steps