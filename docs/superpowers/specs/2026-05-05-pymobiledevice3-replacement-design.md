# Design Spec: libimobiledevice → pymobiledevice3 Replacement

**Date:** 2026-05-05
**Status:** Approved
**Scope:** Full replacement of all iOS tooling in LogPro v2.8.0

## Motivation

libimobiledevice on Windows is community-compiled (mingw), unmaintained, and has critical bugs:
- 32-bit `stat()` — cannot handle IPA files > 2 GB
- USB disconnections with no auto-reconnect
- No shell access, crash logs, or screen recording
- Dependency on Apple Mobile Device Service (iTunes)

pymobiledevice3 is actively maintained, handles all the above, and provides additional capabilities.

## Architecture

**Before:** 6 separate mingw EXEs + ~200 DLLs in `tools/iMobileDevice/`
**After:** Single PyInstaller .exe in `tools/pymobiledevice3/`

```
tools/pymobiledevice3/
  pymobiledevice3.exe    (~60 MB self-contained, no dependencies)
```

## Files Changed

| File | Nature of Change |
|------|-----------------|
| `Services/IosService.cs` | Rewrite. All 11 public methods. `_pymd3` single field replaces 6 tool fields. Command strings remapped. Output parsers updated. |
| `Helpers/ToolLauncher.cs` | `_toolsDir`: `tools/iMobileDevice` → `tools/pymobiledevice3` |
| `Services/DependencyChecker.cs` | Replace libimobiledevice check with pymobiledevice3 check. Remove iTunes dependency. |
| `build_installer.bat` | Remove iMobileDevice copy step. Add pymobiledevice3 copy. |
| `installer/setup.iss` | Replace iMobileDevice file entries with single pymobiledevice3 entry. |
| `QADeviceTool.App.csproj` | Add `tools/pymobiledevice3/` to publish items. |

## Command Mapping

All use `-u UDID` flag before subcommand:

| Operation | Old Command | New Command |
|-----------|------------|-------------|
| Availability check | `idevice_id -l` | `pymobiledevice3 usbmux list` |
| Connected devices | `idevice_id -l` | `pymobiledevice3 usbmux list` |
| Device details | `ideviceinfo -u UDID` | `pymobiledevice3 lockdown info -u UDID` |
| Log capture | `idevicesyslog -u UDID` | `pymobiledevice3 syslog live -u UDID` |
| Screenshot | `idevicescreenshot -u UDID path` | `pymobiledevice3 screenshot -u UDID path` |
| App list | `ideviceinstaller -u UDID list --all` | `pymobiledevice3 apps list -u UDID --all` |
| App install | `ideviceinstaller install` | `pymobiledevice3 apps install -u UDID` |
| App uninstall | `ideviceinstaller -u UDID uninstall` | `pymobiledevice3 apps uninstall -u UDID` |
| File list | `afcclient ls -l` | `pymobiledevice3 afc -u UDID ls` |
| File pull | `afcclient get` | `pymobiledevice3 afc -u UDID pull` |
| File push | `afcclient put` | `pymobiledevice3 afc -u UDID push` |
| File delete | `afcclient rm -rf` | `pymobiledevice3 afc -u UDID rm` |

## Error Handling

pymobiledevice3 returns exit code 0 on success, non-zero on failure.
Error details in stderr (human-readable).
`result.Success` alone is sufficient — no substring checks on output.

## Output Parsing Changes

- **App list:** CSV → structured text. Parser in `ListInstalledAppsAsync` updated.
- **File list:** `ls` output format differs. Parser in `ListDirectoryAsync` updated.
- **Device info:** Key-value pairs with different keys from `ideviceinfo`.

## Build Pipeline

1. **One-time:** `pip install pymobiledevice3 pyinstaller && pyinstaller --onefile --name pymobiledevice3 -m pymobiledevice3.cli`
2. Commit output to `tools/pymobiledevice3/pymobiledevice3.exe`
3. `build_installer.bat`: copy `tools/pymobiledevice3/` to publish output
4. `installer/setup.iss`: include pymobiledevice3.exe in Files

## New Features Unlocked (Future Phases)

These become possible with pymobiledevice3:

| Feature | Command | QA Tester Value |
|---------|---------|-----------------|
| Shell access | `developer shell` | Run commands on iOS — parity with Android Shell tab |
| Screen recording | `developer dvt --screenrecord` | Record iOS gameplay — parity with Android |
| Crash logs | `crash list/pull` | Auto-fetch iOS crash logs for bug reports |
| Diagnostics | `diagnostics` | Full iOS device diagnostics for bug reports |
| Developer tools | `developer dvt --proclist` | Process list, GPU/CPU counters |
| Notifications | `notification post` | Simulate push notifications on iOS |
| Simulator control | `simulator` | Manage iOS simulators |
| MobileGestalt | `mobilegestalt` | Query device capabilities |

## Distribution Impact

| Metric | Before (libimobiledevice) | After (pymobiledevice3) |
|--------|--------------------------|------------------------|
| Size | 188 MB | ~210-220 MB |
| Tool files | 6 EXEs + ~200 DLLs | 1 EXE |
| USB dependency | iTunes / Apple Mobile Device Service | None (built-in usbmuxd) |
| IPA size limit | 2 GB | Unlimited |
