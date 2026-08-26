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
| **Performance profiler** — FPS/jank/CPU/memory/thermal/battery | ✅ | Phase 6 (macOS) |
| Live HUD + sparklines + HTML reports | ✅ | — |
| **Soak runs** — memory-growth / FPS-decay flags | ✅ | — |
| **Device-tier matrix** — multi-device comparison | ✅ | — |
| **Condition simulation** — network presets, mock location | ✅ | — |
| Headless CLI + loopback control API (CI/Appium) | ✅ | ✅ |
| Plugin system (log parsers) | ✅ | ✅ |

## Download

Every push to `main` builds and publishes a release automatically.
Grab the latest from the **[Releases page](https://github.com/sundarlohar007/QADeviceTool/releases)**:

| Asset | What it is |
|---|---|
| `QADeviceTool_vX.exe` | Windows installer (Inno Setup — install once, run) |
| `LogPro_vX_portable.zip` | Portable build — unzip and run, no installation |
| `logpro-cli_vX_win-x64.zip` | Headless CLI for CI/scripting |
| `LogPro_vX_macos-arm64.zip` | macOS build (Avalonia) |
| `LogPro_vX_linux-x64.tar.gz` | Linux build (Avalonia) |

All tools are **bundled** — no separate installs of adb, scrcpy, Python or drivers required.

## Build from source

```bash
git clone https://github.com/sundarlohar007/QADeviceTool.git
cd QADeviceTool
dotnet restore LogPro.sln
dotnet build LogPro.sln
dotnet test LogPro.sln          # 145+ tests, incl. hardware-free e2e (fake adb)
dotnet publish src/LogPro.App/LogPro.App.csproj -c Release -r win-x64 --self-contained true
```

> **Note:** the bundled `adb.exe`/`scrcpy`/`pymobiledevice3.exe` are stored via **Git LFS**.
> If you clone without LFS you'll get pointer files — run `git lfs pull` after cloning.

## CLI

```bash
logpro-cli devices                                    # list devices
logpro-cli capture --serial S [--seconds N] --out DIR # capture logs
logpro-cli profile --serial S --seconds N --package P # FPS/CPU/mem/thermal sampling
logpro-cli soak    --serial S --seconds N             # endurance run with decay flags
logpro-cli matrix  --serials A,B,C --seconds N        # tier comparison
logpro-cli location route --serial S --app P --waypoints "lat,lon;lat,lon" --speed 5
logpro-cli location reset --serial S --app P          # MANDATORY mock-location reset
logpro-cli network apply --serial S --preset 4g       # tc/netem conditioning (root)
logpro-cli serve --port 8417                         # loopback control API for CI/Appium
logpro-cli issue  --serial S --out DIR                # redacted issue bundle (no network)
logpro-cli plugins --dir DIR                          # plugin discovery
```

## Architecture

```
LogPro.App (WPF, Windows — shipping UI)   LogPro.Avalonia (cross-platform UI)
                 \                              /
                  LogPro.ViewModels (shared, UI-agnostic)
                            |
                       LogPro.Core (engine — adb / pymobiledevice3 orchestration,
                                     profiler, condition sim, plugins, manifest)
```

- Built on **[.NET 10 LTS](https://dotnet.microsoft.com/download/dotnet/10.0)** (supported to Nov 2028).
- MVVM via [CommunityToolkit.Mvvm](https://www.nuget.org/packages/CommunityToolkit.Mvvm); DI via
  [Microsoft.Extensions.DependencyInjection](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection).
- Cross-platform UI: [Avalonia](https://avaloniaui.net/) 12.x.
- Android tooling: [platform-tools (adb)](https://developer.android.com/tools/releases/platform-tools),
  [scrcpy](https://github.com/Genymobile/scrcpy) · iOS: [pymobiledevice3](https://github.com/doronz88/pymobiledevice3)
  (process-isolated, see [GPL_COMPLIANCE.md](GPL_COMPLIANCE.md)).
- Roadmap & status: [.planning/](.planning/) ([consolidated blueprint](.planning/MODERNIZATION-AND-REWORK-BLUEPRINT-CONSOLIDATED.md),
  [remaining work](.planning/REMAINING-WORK-PLAN.md), [KPI results](.planning/KPI-RESULTS.md)).

## Privacy & security

Testing unreleased titles means **data minimization is non-negotiable**:

- **Zero outbound network calls** — no telemetry, no crash upload, no cloud sync (hard gate).
- Redaction **on by default** (`SecureMode`), device serials hashed everywhere.
- Bug-report and issue bundles are minimized and written to disk — you upload them yourself.
- The local control API binds to `127.0.0.1` only.
- Full trust boundary: [SECURITY.md](SECURITY.md).

## Contributing

Pull requests welcome. CI runs build + 145+ tests (including hardware-free end-to-end tests against a
fake `adb`), format verification and a NuGet vulnerability audit on every push; dependabot keeps
dependencies current with auto-merge for patch/minor updates. Please keep the **privacy hard gate**
in mind: nothing that touches the network.

## License

[MIT](LICENSE) · Third-party components: see [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) and
[GPL_COMPLIANCE.md](GPL_COMPLIANCE.md).
