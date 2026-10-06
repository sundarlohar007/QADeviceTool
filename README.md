# LogPro — QA Device Tool

> **Privacy-first QA tooling for game testers.** LogPro captures device logs, mirrors screens,
> profiles performance, replays touch macros, runs monkey stress tests and simulates network/
> location conditions — for Android and iOS — while testing **unannounced, unreleased games**.
> **It never calls home.** Zero telemetry, zero analytics, zero outbound network traffic
> (see [SECURITY.md](SECURITY.md)).

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Release](https://img.shields.io/badge/Release-Latest-brightgreen)](https://github.com/sundarlohar007/QADeviceTool/releases)

---

## What it does

| Feature | Android | iOS |
|---|---|---|
| Auto device detection | ✅ `adb` | ✅ `pymobiledevice3` |
| Real-time virtualized log capture + viewer | ✅ | ✅ |
| App-specific filtered logging (PID tracking) | ✅ | ◐ syslog |
| Session management + bug-report bundles | ✅ | ✅ |
| Screen mirroring ([scrcpy](https://github.com/Genymobile/scrcpy)) | ✅ | — |
| Screenshots + screen recording | ✅ | ◐ |
| Macro record/replay | ✅ | — |
| Monkey stress testing | ✅ | — |
| **Performance profiler** — live FPS, estimated frame gaps, CPU, memory, thermal, battery | ✅ | — |
| Performance run charts, HUD, markers, baseline alerts, tier comparison, JSON/CSV/HTML reports | ✅ | — |
| **Soak runs** — memory-growth / FPS-decay flags | ✅ | — |
| **Device-tier matrix** — multi-device comparison | ✅ | — |
| **Condition simulation** — network presets, mock location | ✅ | — |
| Headless CLI + loopback control API (CI/Appium) | ✅ | ✅ |
| Plugin system (log parsers) | ✅ | ✅ |

The **Apps** tab lists user and system apps on both platforms. Android also offers a running-app
filter, Force Stop, Clear Data, and optional test-APK installation (`-t`); iOS offers a hidden-app
filter and IPA installation. Android-only actions are disabled for iOS. Use the device picker and
search to narrow the list, drag APK/IPA files into the tab for a cancellable install queue, or save
an inventory snapshot to compare after testing. Inventory can be exported as CSV. Package archives
are checked before installation; Android displays a version code when the device supplies one.

## Download

Every push to `main` builds and publishes a release automatically.
Grab the latest from the **[Releases page](https://github.com/sundarlohar007/QADeviceTool/releases)**:

| Asset | What it is |
|---|---|
| `LogPro_vX.exe` | Windows installer (Inno Setup `.exe` — install once, run) |
| `LogPro_vX_portable.zip` | Portable build — unzip and run, no installation |
| `logpro-cli_vX_win-x64.zip` | Headless CLI for CI/scripting |

The Windows installer, portable ZIP, and Windows CLI ZIP contain the tested `adb`, `scrcpy`,
and `pymobiledevice3` tools. Android USB drivers are supplied by Windows/OEMs and are **not**
redistributed by LogPro. Windows is the only supported host platform; Android and iOS USB devices remain supported.
For iOS USB devices on Windows, install the classic iTunes package to provide Apple Mobile
Device Service, then trust the computer on the device. The bundled pymobiledevice3 executable
does not replace that Windows service.

To rebuild the Windows iOS executable, install the packages in
`scripts/requirements-pymobiledevice3-build.txt` with Python 3.13, then run
`pwsh -File scripts/build-pymobiledevice3.ps1`. Run
`pwsh -File scripts/test-pymobiledevice3.ps1 -Executable publish/pymobiledevice3/pymobiledevice3.exe` to check the complete runtime.
Run `scripts/prepare-windows-tools.ps1 -PublishDirectory publish/app` after publishing; it installs the complete iOS runtime and Google platform-tools into the payload and generates its exact integrity manifest.

## Windows installation and updates

The installer includes the self-contained .NET application, ADB, scrcpy, and a complete Python/iOS runtime. It checks for newer compatible LogPro packages after copying the bundled baseline. Offline installs retain the bundled tools. `/SKIPUPDATECHECK` disables the installer network check for managed/offline deployments.

The application checks and downloads verified updates at startup and every six hours by default (configurable in Settings). Checks begin only after the local-data notice is accepted. Existing update preferences are preserved. Install Selected defers while device operations are active; tool installation into Program Files opens an elevated Windows updater and closes LogPro gracefully. Start LogPro again after the updater finishes. Rollback is available for each managed tool. Application and embedded .NET updates use the full Windows installer.

Tool updates are complete `LogPro-tool-NAME-VERSION-win-x64.zip` assets from this repository's releases, validated before replacing the previous version. A newer upstream release is not installed until its commands and runtime are validated and published as a compatible package. No system Python installation is required.

USB readiness distinguishes transport discovery, device authorization/trust, service availability, and tool integrity. For Android, enable USB debugging and accept the RSA prompt. For iOS, unlock the device, accept Trust This Computer, and ensure Apple Mobile Device Service is running. Driver/service installation must use the appropriate manufacturer package.

## Build from source

```bash
git clone https://github.com/sundarlohar007/QADeviceTool.git
cd QADeviceTool
dotnet restore LogPro.sln
dotnet build LogPro.sln
dotnet test LogPro.sln          # full suite, incl. hardware-free e2e (fake adb)
dotnet publish src/LogPro.App/LogPro.App.csproj -c Release -r win-x64 --self-contained true
```

> **Note:** the bundled `adb.exe`/`scrcpy`/`pymobiledevice3.exe` are stored via **Git LFS**.
> If you clone without LFS you'll get pointer files — run `git lfs pull` after cloning.

## CLI

```bash
logpro-cli devices                                    # list devices
logpro-cli capture --serial S [--seconds N] --out DIR # capture logs
logpro-cli profile --serial S --seconds N --package P # FPS/CPU/mem/thermal sampling
logpro-cli soak    --serial S --seconds N --package P # endurance run with decay flags
logpro-cli matrix  --serials A,B,C --seconds N --package P # tier comparison
logpro-cli location route --serial S --app P --waypoints "lat,lon;lat,lon" --speed 5
logpro-cli location reset --serial S --app P          # MANDATORY mock-location reset
logpro-cli network apply --serial S --preset 4g       # tc/netem conditioning (root)
logpro-cli serve --port 8417                           # loopback API; prints a per-run API key
logpro-cli issue  --serial S --out DIR                # redacted issue bundle (no network)
logpro-cli plugins --dir DIR                          # plugin discovery
```

Performance runs use an online Android device and a selected app for app FPS, CPU, and memory. The first SurfaceFlinger poll establishes a baseline; missing FPS is reported as insufficient data rather than a healthy run. Frame-gap counts are estimates from presentation timestamps, not Android FrameTimeline jank classifications. Soak loads run for the entire requested duration, and CLI soak exits nonzero for missing data, an early or failed load, or threshold flags.

## Architecture

```
LogPro.App (WPF, Windows)              LogPro.Cli (Windows)
                 \                              /
                  LogPro.ViewModels (shared, UI-agnostic)
                            |
                       LogPro.Core (engine — adb / pymobiledevice3 orchestration,
                                     profiler, condition sim, plugins, manifest)
```

- Built on **[.NET 10 LTS](https://dotnet.microsoft.com/download/dotnet/10.0)** (supported to Nov 2028).
- MVVM via [CommunityToolkit.Mvvm](https://www.nuget.org/packages/CommunityToolkit.Mvvm); DI via
  [Microsoft.Extensions.DependencyInjection](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection).
- Android tooling: [platform-tools (adb)](https://developer.android.com/tools/releases/platform-tools),
  [scrcpy](https://github.com/Genymobile/scrcpy) · iOS: [pymobiledevice3](https://github.com/doronz88/pymobiledevice3)
  (process-isolated, see [GPL_COMPLIANCE.md](GPL_COMPLIANCE.md)).
- Roadmap & status: [.planning/](.planning/) ([consolidated blueprint](.planning/MODERNIZATION-AND-REWORK-BLUEPRINT-CONSOLIDATED.md),
  [remaining work](.planning/REMAINING-WORK-PLAN.md), [KPI results](.planning/KPI-RESULTS.md)).

## Privacy & security

Testing unreleased titles means **data minimization is non-negotiable**:

- **No telemetry, crash upload, or cloud sync** — update checks and verified binary downloads contact GitHub (and the release build downloads Android platform-tools from Google)
  (hard gate). Wireless ADB, network device discovery, URL/file schemes, port forwarding,
  and arbitrary shell composition are blocked by the engine.
- Exported text and issue bundles are always redacted; device identifiers in evidence are hashed.
- Bug-report and issue bundles are minimized and written to disk — you upload them yourself.
- The local control API binds to `127.0.0.1` only and requires a per-process API key.
- Full trust boundary: [SECURITY.md](SECURITY.md).

## Contributing

Pull requests welcome. CI runs the full test suite (including hardware-free end-to-end tests against a
fake `adb`), format verification and a NuGet vulnerability audit on every push; Dependabot keeps
dependencies current with auto-merge for patch/minor updates. Please keep the **privacy hard gate**
in mind: nothing that touches the network.

## License

[MIT](LICENSE) · Third-party components: see [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt),
[GPL_COMPLIANCE.md](GPL_COMPLIANCE.md), and the packaged [source offer](licenses/SOURCE-OFFER-pymobiledevice3.txt).
