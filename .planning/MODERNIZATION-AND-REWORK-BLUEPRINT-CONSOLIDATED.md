# LogPro / QADeviceTool — Modernization, Enhancement & Full-Rework Blueprint (Consolidated)

> **Status:** Consolidated — supersedes *Redising on QA Tool.md* (2026-06, Tauri v2 proposal) and *.planning/MODERNIZATION-AND-REWORK-BLUEPRINT.md* (2026-07-31, rev 3).
> **Stack decision (locked):** **Avalonia UI on .NET 10 LTS**, preserving the existing C# engine (~11k LOC). Tauri/React/Rust was evaluated and **rejected**: it discards the entire C# engine and re-implements all adb/pymobiledevice3 orchestration in a language the team does not yet own (§4).
> **Scope:** Cross-platform migration off WPF, architecture rework, engine-agnostic game-testing platform (OS-level instrumentation only), full iOS/Android potential via `pymobiledevice3` + `adb`, a zero-dependency installer, and a phased roadmap gated by acceptance thresholds (§19).
> **Relationship to other docs:** Tactical, line-by-line defects live in [`AUDIT-FINDINGS.md`](./AUDIT-FINDINGS.md) (~162 items) and are scheduled in [`IMPLEMENTATION-PLAN.md`](./IMPLEMENTATION-PLAN.md). This document is strategic and sequences those items by theme (R1–R11).

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Non-Goals & MVP Guardrails](#2-non-goals--mvp-guardrails)
3. [Current-State Assessment](#3-current-state-assessment)
4. [Stack Decision — Avalonia on .NET 10](#4-stack-decision--avalonia-on-net-10)
5. [Critical Risks & Structural Debt](#5-critical-risks--structural-debt)
6. [Technology Stack](#6-technology-stack)
7. [Zero-Dependency Bundling & Native-Tool Strategy](#7-zero-dependency-bundling--native-tool-strategy)
8. [Architecture Rework (Target State)](#8-architecture-rework-target-state)
9. [Concurrency, Batching & Performance](#9-concurrency-batching--performance)
10. [Security, Privacy & Compliance](#10-security-privacy--compliance)
11. [UX / UI Modernization](#11-ux--ui-modernization)
12. [Engine-Agnostic Game-Testing Suite](#12-engine-agnostic-game-testing-suite)
13. [Full iOS & Android Instrumentation](#13-full-ios--android-instrumentation)
14. [APK/AAB Decompilation (Post-GA)](#14-apkab-decompilation-post-ga)
15. [Enhancements to Existing Features](#15-enhancements-to-existing-features)
16. [New Features (Industry-Aligned)](#16-new-features-industry-aligned)
17. [Migration Plan & Phased Roadmap](#17-migration-plan--phased-roadmap)
18. [Prioritization Matrix](#18-prioritization-matrix)
19. [Success Metrics / KPIs & GA Gate](#19-success-metrics--kpis--ga-gate)
20. [Risks, Assumptions & Open Questions](#20-risks-assumptions--open-questions)

---

## 1. Executive Summary

**LogPro** (repo: `QADeviceTool`, v3.2.0) is a Windows-only WPF desktop utility for QA/QC game testers. It auto-captures device logs, manages timestamped sessions, mirrors Android screens, takes/records evidence, replays touch macros, runs monkey stress tests, and manages apps/files on both Android (bundled `adb` + `scrcpy`) and iOS (bundled `pymobiledevice3`). It already has CI, an installer, a test project, and an unusually thorough self-audit.

Three strategic decisions anchor this program:

1. **Avalonia UI on .NET 10 LTS** — the "current-industry, runs-on-every-machine" answer that preserves ~11k LOC of C# device/session logic, ports most view-models as-is, and — critically — a **macOS build unlocks the iOS DVT testing features Windows tooling cannot provide.** The migration is not mainly a XAML rewrite; it is **decoupling** (retarget to .NET 10 together, extract a UI-agnostic engine) so the WPF app keeps running until parity is proven. ([Avalonia positioning](https://avaloniaui.net/blog/what-is-wpf), [UniGetUI Avalonia migration](https://www.ntcompatible.com/story/unigetui-v202623-releases-nativeaot-cuts-download-size-by/))
2. **Reposition around engine-agnostic game performance testing** — never depend on engine SDKs or in-app hooks; read only OS-level signals that exist for any app (Android SurfaceFlinger/gfxinfo/Perfetto, `/proc`, thermal/battery; iOS `pymobiledevice3` DVT developer services). This is the industry (GameBench) approach, and the only one that works uniformly against proprietary, never-published in-house engines. ([Perfetto FrameTimeline](https://perfetto.dev/docs/data-sources/frametimeline), [pymobiledevice3](https://github.com/doronz88/pymobiledevice3))
3. **Zero-dependency, self-contained installers.** Everything required (adb, scrcpy, pymobiledevice3, optional Perfetto/trimmed JRE) ships inside the installer with sha256 integrity checks — no PATH, no prerequisites, no separate downloads. Users install once and run. The **GA gate requires testing every installer on fresh OS images** (§19).

Time-critical: **.NET 8 and .NET 9 both end support on 2026-11-10**; .NET 10 is the LTS target (support to Nov 2028). The runtime retarget and the Avalonia migration happen together. ([.NET 8/9 EOS](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/), [Announcing .NET 10](https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/))

The plan ships in two tracks: **Track A — Stabilize & De-couple** (months 0–3, low risk, immediate value: DI, decoupling, .NET 10, hardening) and **Track B — Cross-Platform + Game-Testing Platform** (months 3–12). Every new capability is additive and must not block MVP parity.

---

## 2. Non-Goals & MVP Guardrails

**Anti-scope-creep guardrails.** v1 GA is **WPF feature parity, nothing more**. Everything beyond the list below is additive and must not block GA.

**MVP Definition (Minimum Shippable) — WPF parity:**
- ✅ Auto-detect Android/iOS devices
- ✅ Real-time virtualized log capture + viewer (batched events, §9)
- ✅ App-specific filtered logging (PID tracking) + filters (level / tag / keyword / regex)
- ✅ Session management (CRUD, auto-capture, history, search, bug-report .zip export)
- ✅ Screen mirroring + screenshot capture + screen recording
- ✅ Macro record/replay + monkey stress GUI
- ✅ Settings/preferences + legacy migration (§8.5)

**Non-Goals for v1 (deferred, not cancelled):**
- ❌ **AI-assisted log summarization** — post-GA, opt-in, local-first
- ❌ **Plugin marketplace** — ship the plugin *system* first, marketplace later (§16)
- ❌ **Full cloud/team sync** — limited to *linking out* to device farms; shared library is opt-in post-GA
- ❌ **AR/VR sensor logging** — moved to a Future Ideas backlog
- ❌ **Time-scrubbing session replay** — replaced by a static side-by-side session diff
- ❌ **APK/AAB recompile or re-signing** — decompile-only, post-GA (§14)
- ❌ **IPA support** — deferred behind the macOS build + IPA toolchain validation

---

## 3. Current-State Assessment

### 3.1 Tech stack (as built)
| Concern | Current |
|---|---|
| Runtime | .NET 8 (`net8.0-windows`), WPF, `WinExe`, self-contained `win-x64` |
| MVVM | CommunityToolkit.Mvvm 8.4.0 (source generators) |
| Logging | NLog 6.1.0 (static `AppLogger`) |
| Android tooling | Bundled `adb` (platform-tools), `scrcpy` |
| iOS tooling | Bundled `pymobiledevice3` (PyInstaller) + system-Python fallback |
| Packaging | Inno Setup installer + portable zip |
| CI | GitHub Actions (build/test/publish, installer, portable, release) |
| Tests | xUnit + FluentAssertions + Moq (services/models/helpers only) |
| Platform reach | **Windows only** |

### 3.2 Size & shape
- ~10,900 lines of C# in 78 files; ~4,700 lines of XAML in 17 files.
- Largest units: `SessionViewModel.cs` (1,354), `AdbService.cs` (787), `IosService.cs` (651), `SessionService.cs` (604).

### 3.3 Strengths (preserve)
- **Genuinely useful, differentiated feature set** for game QA (dual app-specific logging, PID tracking through crashes, bug-report zips, macro replay, monkey GUI).
- **Robust external-process plumbing** — `ToolLauncher`/`ToolResolver`: stdout/stderr draining (no pipe deadlock), process tracking + kill-on-exit via `ProcessManagerService`, per-binary working directories, dynamic path resolution from `AppContext.BaseDirectory`. **This layer is already UI-agnostic and already resolves bundled binaries from the app directory — the foundation for zero-dependency bundling (§7).**
- **Resilience touches:** device-monitor missed-poll debounce (3 polls before disconnect), parallel Android+iOS polling, atomic preferences save (temp + move), early startup diagnostics log.
- Interface seams already exist for the five core services; `SecureMode` log redaction in places; serial hashing; path allowlists; GPL-compliance documentation for pymobiledevice3.
- Mature process artifacts: working CI, installer, changelog, and an extensive existing self-audit.

### 3.4 Weaknesses (root causes)
- **Windows-only** — structurally caps iOS support (developer-mode DVT services, screen recording, sysmon are far more reliable from macOS).
- **UI and engine entangled** — WPF types leak into services (`DialogService` uses `MessageBox`, `ThemeService` recreates `MainWindow`, VMs capture `Application.Current.Dispatcher`).
- No DI container; hand-wired composition root; static ambient singletons.
- God objects and duplicated device-list/selection logic across nearly every view-model.
- Broken IDisposable lifecycle; phantom ADB serialization semaphore; concurrency docs ≠ implementation.
- Stringly-typed navigation; brand/app-data fragmentation (LogPro / QADeviceTool / QAQCDeviceTool).

---

## 4. Stack Decision — Avalonia on .NET 10

**Avalonia UI 12.x (current: 12.1, shipped Apr 2026) on .NET 10 is the primary and locked recommendation.** ([Avalonia 12 breaking changes](https://docs.avaloniaui.net/docs/avalonia12-breaking-changes) — official guidance: target .NET 10; compiled bindings now default)

1. **Preserves the engine.** ~11k LOC of device/session/instrumentation C# ports directly; the WPF→Avalonia XAML delta is mechanical; CommunityToolkit view-models port near-unchanged.
2. **Cross-platform:** Windows, macOS, Linux from one codebase; renders via SkiaSharp (own UI, not OS WebView) → pixel-consistent results and no WebView2/WKWebView/WebKitGTK fragmentation. **The macOS build unlocks iOS testing.**
3. **Runtime urgency:** .NET 8/9 EOS 2026-11-10; the .NET 10 retarget rides along with the UI migration.
4. **Industry precedent (2026):** production-proven — UniGetUI shipped a full WinUI→Avalonia migration with **NativeAOT enabled by default**: Windows installer dropped 58.0 → 28.3 MB, portable 85.6 → 39.5 MB, Linux ~halved ([UniGetUI v2026.2.3](https://www.ntcompatible.com/story/unigetui-v202623-releases-nativeaot-cuts-download-size-by/)). Caveat: early reports show idle memory ≈ WPF or slightly *higher* — see §19 KPI note.
5. **Risk-controlled:** the UI-agnostic engine stays on WPF during migration (parallel-run) — no big-bang rewrite.

> **Considered and set aside:** Avalonia **XPF** (commercial) runs an *unmodified* WPF app on macOS/Linux — tempting for the macOS-only goal, but per-app commercial licensing and a second UI stack to maintain make a standard Avalonia port the better path.

| Option | Verdict | Reasoning |
|---|---|---|
| **Avalonia .NET 10** | ✅ **Adopt** | WPF-like XAML, Win/macOS/Linux, max C# reuse, macOS unlocks iOS |
| **Tauri v2 (React + Rust)** | ✗ Rejected | **Full rewrite** of the 11k-LOC engine in Rust (no team Rust); discards the C# orchestration for adb/pymobiledevice3; considered and set aside |
| Electron | ✗ Rejected | Largest footprint, no code reuse |
| .NET MAUI | ✗ Not ideal | Mobile-first; weaker desktop story; different XAML dialect |
| Uno Platform | ◐ Consider later | Only if browser/mobile reach ever becomes a requirement |
| Flutter | ✗ Rejected | Dart UI only; discards C# engine |
| Stay WPF | ✗ Rejected | Windows-only; blocks macOS/iOS testing; the reason for this program |

**Main trade-off:** Avalonia is a heavier runtime than Tauri and depends on a GPU for smooth rendering — a software-rendering fallback must exist for GPU-less/older machines. **Cross-platform CI must exist from week 1** to shake out SkiaSharp rendering differences.

### 4.1 What changes vs. what stays
- **Stays (ports directly):** models, all services, `ToolLauncher`/`ToolResolver`/`ProcessManagerService`, adb & pymobiledevice3 orchestration, scrcpy launching, most view-models, converter logic.
- **Rewritten:** XAML views (WPF → Avalonia XAML), theming/resource dictionaries, dialog/clipboard/dispatcher adapters, packaging per OS.
- **New per-OS glue:** file dialogs, notifications, single-instance, deep-link/URL handlers, native tool paths per RID.

---

## 5. Critical Risks & Structural Debt (R1–R11)

> Tactical bug IDs from `AUDIT-FINDINGS.md` are cross-referenced in brackets.

| # | Risk | Impact | Mitigation / refs |
|---|---|---|---|
| R1 | **.NET 8 EOS 2026-11-10** | No security patches; blocked from new tooling | Retarget .NET 10 LTS in Phase 0 — never block it behind the Avalonia migration |
| R2 | **Windows-only limits iOS reach** | Half the mobile market partial; no macOS lab | macOS build unlocks DVT; open question #2 (§20) |
| R3 | **UI/engine entanglement** | Blocks cross-platform + CLI reuse | Decouple the engine (zero UI refs) first — the bulk of the work (§8) |
| R4 | **No DI / God objects** | Untestable VMs, leaks, fragile startup | DI/host; `IDeviceStore` + `IUiDispatcher`; split god classes |
| R5 | **Device-shell command construction gaps** | Injection into device shell (paths/macro/raw passthrough) | Strict allowlists + escape validation; no raw passthrough [TOOL-18/19, FEAT-34] |
| R6 | **Concurrency not consistently implemented as documented** | USB contention, device "offline" flapping | Per-device semaphores + global cap (§9.1); genuinely-async timers [BUG-02, TOOL-01/02] |
| R7 | **O(n²) strings / full-file loads / Reset churn** | UI freezes, OOM on large logs, dropped lines | StringBuilder + throttle + virtualize + batched events (§9) [BUG-07/14, FEAT-25/29/30/31/32] |
| R8 | **Lifecycle leaks (no real Dispose chain)** | Ghost polls, memory growth, theme-switch regressions | Real Dispose + nav lifecycle hooks; in-place theme swap [MISS-05, FEAT-35/36] |
| R9 | **Privacy: serials/app inventory/deep-link secrets leak** | Data-leak risk for unreleased titles | `SecureMode` default; central `SanitizeForLog`; minimized scoped bundles [SEC-01/04/06, FEAT-22/23] |
| R10 | **Brand/app-data fragmentation** | Lost/duplicated user data, support confusion | Single name/app-data; consolidated CI |
| R11 | **GPL-3.0 dep (pymobiledevice3) bundled** | Source-disclosure obligations | Process-isolate; document; wrap CLI behind a capability probe [LEGAL-02] |

---

## 6. Technology Stack

| Concern | From (today) | To (target) | Why |
|---|---|---|---|
| UI framework | WPF | **Avalonia 12.x** | Cross-platform, self-rendered, max reuse; compiled bindings by default |
| Runtime | `net8.0-windows` | **`net10.0`** + platform TFMs | LTS to Nov 2028; C# 14 |
| MVVM | CommunityToolkit.Mvvm 8.4.0 | Same, on 8.4.x line (current 8.4.2) | Portable; zero VM rewrite; Avalonia-docs recommended |
| DI / host | none (manual `new`) | **Microsoft.Extensions.Hosting + DI + Options** | Testability, lifecycle, typed settings |
| Logging | NLog static | **Microsoft.Extensions.Logging** + Serilog/NLog sink | Structured, injectable |
| Dialogs/UI services | WPF `MessageBox` in services | `IDialogService` / `IClipboard` / `IUiDispatcher` | Removes UI-from-engine coupling |
| Charts | none | **LiveChartsCore** / ScottPlot | FPS/CPU/mem/thermal time series |
| Android tooling | adb, scrcpy | adb, scrcpy + **Perfetto** (optional) | Engine-agnostic frame/CPU tracing |
| iOS tooling | pymobiledevice3 (isolated) | Same + expose **DVT services** | Unlock the potential (§13) |
| SQLite | none (per-session) | **Microsoft.Data.Sqlite** + `PRAGMA user_version` | Cross-session DB + search index |
| Packaging | Inno Setup | **NativeAOT publish** (halves app size; UniGetUI 58→28 MB) + MSIX/Inno (Win); .app/notarized DMG (macOS); AppImage/deb (Linux) | Native UX per platform |
| Tests | xUnit + FluentAssertions + Moq | xUnit + Moq/NSubstitute | VM tests unlocked by DI |
| Package mgmt | per-project | **Central Package Management** (`Directory.Packages.props`) | One source of truth |

### 6.1 Dependency hygiene
- CPM, `Deterministic`, `ContinuousIntegrationBuild`, `<NuGetAudit>true</NuGetAudit>`, Dependabot, SBOM on release, Authenticode + notarization signing.
- Keep CommunityToolkit.Mvvm on the 8.4.x line (current stable 8.4.2).
- **NativeAOT is the packaging default** (validated in production by UniGetUI; supported per [Avalonia docs](https://docs.avaloniaui.net/docs/deployment/native-aot/)). Budget for trimming/reflection caveats: `Avalonia.Diagnostics` is removed in v12 (replaced by `AvaloniaUI.DiagnosticsSupport`, subscription-based) — DevTools access is a build-time/diagnostic concern, not a runtime dependency.

*Note: the Tauri plan's React/`rusqlite`/`DashMap`/TanStack choices are dropped along with that stack; in-proc .NET equivalents (Microsoft.Data.Sqlite, `ConcurrentDictionary` of semaphores) replace them.*

---

## 7. Zero-Dependency Bundling & Native-Tool Strategy

**Goal:** users install once and run — no manual installation of ADB, scrcpy, Python, Java, or WebView runtimes; no PATH configuration.

**Strategy (hybrid of the two prior plans): bundle-first + sha256 integrity + auto-update in place.**

| Dependency | Purpose | Bundling method | At |
|---|---|---|---|
| `adb` / platform-tools | Android detection, logcat, screenshots, install | Bundle binary under app dir (sha256-pinned) | MVP |
| `scrcpy` + deps | Screen mirroring / recording | Bundle + native libs | MVP |
| `pymobiledevice3` | iOS support | Bundle PyInstaller binary (isolated process; GPL) + CLI capability probe | MVP |
| **Perfetto** / `traceconv` | System trace capture + parse | Bundle `perfetto`/`traceconv` | post-GA |
| **JRE 17+ (jlink-trimmed)** | Required by JADX/Apktool (decompiler) | Bundle only in the decomp add-on pack — not in the GA core | post-GA |
| App binary + assets | LogPro itself | Bundler output | MVP |

### 7.1 Release manifest & integrity
- Per release, a **signed manifest** (name, version, sha256, source URL) for every bundled tool.
- Tools are **version-pinned per host OS**; on first run and via the dependency doctor (§16) the app verifies hashes and reports/corrects drift.
- **Auto-update in place** when a release ships newer tools (download → replace → verify) — no user reinstallation.
- All bundled executables + installer are **code-signed** (antivirus).

The existing `ToolLauncher`/`ToolResolver` already resolves binaries from `AppContext.BaseDirectory`, so this is additive — the real work is the manifest + `DependencyDoctor`, not new plumbing.

---

## 8. Architecture Rework (Target State)

```
+-----------------------------------------------------------------------+
| Presentation — Avalonia UI (Win/macOS/Linux)    [+ WPF during migration] |
|   Views (Avalonia XAML) + ViewModels (CommunityToolkit.Mvvm)            |
|   Navigation service · WeakReferenceMessenger · IUiDispatcher            |
+-----------------------------------------------------------------------+
| Alternate front-end — Headless CLI (same engine, for CI)               |
+-----------------------------------------------------------------------+
| Application layer (UI-agnostic, testable)                               |
|   Session orchestration · Performance profiling · Condition/Location ·  |
|   Reporting · Export · DeviceStore (single source of truth) ·           |
|   SanitizeForLog                                                          |
+-----------------------------------------------------------------------+
| Domain — Device Abstraction (capabilities over platform branches)       |
|   ILogSource · IScreenCapture · IScreenRecorder · IFileSystem ·         |
|   IAppManager · IInputInjector · IScreenMirror · IPerformanceProfiler  |
|   IConditionInducer · ILocationSimulator                                |
+-----------------------------------------------------------------------+
| Platform backends (plugins) |                                           |
|   Android (adb, scrcpy, Perfetto)    iOS (pymobiledevice3, DVT + tunnel)|
+-----------------------------------------------------------------------+
| Infrastructure                                                         |
|   ToolLauncher/ToolResolver/ProcessManager · Preferences · Logging (MEL)|
|   SQLite storage · per-OS adapters (dialogs/clipboard/tray)             |
|   DependencyDoctor · UpdateService · CrashSender (opt-in)               |
+-----------------------------------------------------------------------+
| Host: Microsoft.Extensions.Hosting + DI + Configuration (.NET 10 LTS)  |
+-----------------------------------------------------------------------+
```

### 8.1 DI (foundational)
`Microsoft.Extensions.DependencyInjection`; **de-static**: `PreferencesService`, `ThemeService`, `DialogService`, `AppLogger`, `ProcessManagerService`. The enabler for testability, CLI, and the platform layer.

### 8.2 Kill the God objects
- Shrink `MainViewModel` to navigation + shell state.
- Split `SessionViewModel` (1,354 LOC) into `LogCaptureViewModel`, `LogViewerViewModel`, `CrashPanelViewModel`, `EvidenceViewModel` + a `SessionExportService`.
- Decompose `AdbService`/`IosService` into focused capability services (DeviceInfo, Logs, Files, Apps, Input, Media, Instrumentation).
- Rule of thumb after split: no VM/service file above ~400 LOC.

### 8.3 Device Abstraction Layer + shared `IDeviceStore`
Capability interfaces so the UI queries capabilities instead of branching on platform. A single `IDeviceStore` (one observable device list + selected device) replaces per-VM duplicated list/selection; `IUiDispatcher` centralizes UI-thread marshalling and is trivially portable to Avalonia. [FEAT-36, R8]

### 8.4 Navigation, messaging, lifecycle
- Typed navigation service + view locator (replaces the stringly-typed switch).
- `WeakReferenceMessenger` for cross-VM events (crash detected, device selected, capture started).
- **Real Dispose chain** + navigation lifecycle hooks (start/stop pollers); in-place `ResourceDictionary` theme swap (kills the window-recreation hack); `TimeProvider` for testable timers.

### 8.5 Legacy data migration
One-time `migrate_legacy_settings` command imports existing WPF configs and sessions (single pass, idempotent, preserves the pre-existing branding/app-data cleanup from R10).

---

## 9. Concurrency, Batching & Performance

### 9.1 Per-device concurrency (answers R6)
Replace the WPF single `SemaphoreSlim(1,1)` global bottleneck with **per-device locks + a global cap** — parallel *across* devices, serialized *per* device:

```csharp
sealed class DeviceCommandQueue {
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _deviceLocks = new();
    private static readonly SemaphoreSlim _global = new(Environment.ProcessorCount); // upper bound on concurrent subprocesses

    IDisposable Enter(string serial) { /* per-device + global acquire */ }
}
```

Policy is centralized in the `ProcessLauncher` (the single choke point the WPF version was missing) — no scattered per-view-model locking.

### 9.2 Log pipeline batching (answers R7)
- **Ring buffer as source of truth** in the engine (bounded, write-behind).
- Throttled **event emission ≈ every 16 ms (1 frame) or every 500 lines, whichever comes first**; the frontend renders the virtualized tail and pulls history via command.
- Heavy fills use in-place `RemoveRange` (no double-`Reset`), batch notifications, incremental filtering; "follow/tail" survives batches without selection loss.
- `StringBuilder` + throttled (100 ms) flush for console/log sinks.

### 9.3 Large-log / export robustness
- Stream via `StreamReader` (no `ReadAllLines` at 50–100 MB+); tail-read preview; `Utf8JsonWriter` streaming export.

### 9.4 Measurement quality
- BenchmarkDotNet micro-benchmarks for hot paths (log parse/filter/export, trace parsing) + a repeatable perf smoke test (1M-line synthetic log) in CI.
- Instrumentation sampling: background sampling threads, ring buffers, adaptive interval; **never sample on the UI thread**; validate against Perfetto / DVT ground truth.

---

## 10. Security, Privacy & Compliance

Testing unreleased games makes data minimization a first-class requirement.

- **Command-injection hardening:** strict allowlists for all path/package/URL args; validate iOS AFC paths; escape/reject macro `input text`; remove raw `ExecuteCommandAsync` passthrough. [TOOL-18/19, FEAT-34, R5]
- **Redaction by default:** `SecureMode` on by default; centralize `SanitizeForLog` (serials, paths, deep-link query params) before *any* sink. [SEC-01, TOOL-13]
- **Bug-report minimization:** hash iOS serials; drop full package inventory; scope to the target package. [FEAT-22/23, SEC-04]
- **Screenshot/video redaction:** blur/region-mask tools before any artifact leaves the machine (critical for pre-release content). [§12.7]
- **Data lifecycle:** retention (auto-delete old sessions/media); export/delete-my-data in Settings; optional DPAPI at-rest + owner-only ACLs. [SEC-02/03/13]
- **Wireless ADB & location spoofing:** warning before `adb tcpip` and before simulated GPS (leaves the device in a modified state); a mandatory "reset location" safety.
- **Supply chain & licensing:** NuGet audit + Dependabot; signed/notarized releases; SBOM; complete `THIRD_PARTY_NOTICES` (scrcpy Apache NOTICE, perfetto, JADX/Apktool licenses); keep pymobiledevice3 process-isolated (GPL-3.0). [LEGAL-01..05]
- Add `SECURITY.md` (trust boundary: local user, USB devices, spawned native tools) + a vuln-reporting process.

---

## 11. UX / UI Modernization

> **North-star:** *a calm, minimalist tool that a first-time tester can operate with zero training — it is always obvious what to press, what will happen, and what to do next.* Explicitly **not** cyberpunk / neon / "hacker terminal". The device screen, logs, and performance data are the heroes; the chrome recedes.

### 11.1 Design principles
1. **Clarity over cleverness** — plain labels ("Start capture", "Record screen"); no jargon-only icons.
2. **One primary action per screen**; everything else secondary/tertiary.
3. **Progressive disclosure** — the 3–4 things a tester needs first; advanced options behind "Advanced ▸" / a settings drawer.
4. **Never leave the user guessing** — every action gives feedback; disabled controls explain *why* via tooltip ("Connect a device to start").
5. **Forgiving by default** — destructive actions confirm; long actions cancellable; nothing silently fails.
6. **Calm, minimal, spacious** — whitespace; color only to carry meaning (status, severity).
7. **Consistency** — one component library, one spacing/type scale.

### 11.2 Visual language
- **Aesthetic:** clean soft neutral surfaces, subtle 1px dividers, 8–12px rounded corners, soft shadows for elevation. **No** neon glows, gradients, glitch, scanlines, dark-with-cyan.
- **Color:** neutrals carry the UI (near-white light mode; deep charcoal, not pure black, dark mode); **one calm accent** (muted indigo/teal) for primary/selection/focus only; semantic green/amber/red/blue reserved for status dots, log levels, pass/fail.
- **Type:** one clean UI sans; a strict type scale (12/14/16/20/28); monospace only for logs/metrics; weight (not color) creates hierarchy.
- **Spacing:** 8px scale (4/8/16/24/32); comfortable log line-height.
- **Motion:** 120–200 ms, short and purposeful; "reduce motion" honors OS. **Light is the default; dark is a first-class equal** — not a hacker mode. Both fully `DynamicResource`-aware for shell + content.

### 11.3 Information architecture
- Persistent left sidebar (icon + word): Dashboard, Sessions, Devices, **Performance**, Apps, Files, Automation, Settings.
- Global top bar with an **always-visible active device selector**, Ctrl/Cmd+K command palette, and health/status.
- Breadcrumbs in drill-down areas; everything reachable in **≤2 clicks**; nothing more than one level deep.

### 11.4 Zero-confusion mechanics
- **First-run guided setup wizard**: connect device → enable USB debugging / trust / developer mode, with a live checklist, inline help, and one-click remediation for each dependency (adb, driver, scrcpy, pymobiledevice3, iOS tunnel).
- **Helpful empty states** (illustration + one-line explanation + the single button to start).
- **Primary/secondary/tertiary button hierarchy** (filled / outlined / text).
- **Contextual affordances** — actions live on the row they act on.
- **State-driven controls**; "● Recording" state during capture; disabled controls explain why.
- Confirmations for destructive/irreversible actions in plain language.
- Inline validation; toasts; global busy indicator with progress + cancel; undo where feasible (e.g. recently deleted session).

### 11.5 Power-user layer
- **Command palette (Ctrl/Cmd+K)** — fuzzy search every action and device; never required.
- Keyboard shortcuts (Ctrl/Cmd+1..n, arrow navigation, `?` shortcuts sheet); adjustable density; resizable/dockable panels (logs side-by-side with the perf HUD).

### 11.6 Component library
One Avalonia component set + design tokens (colors, spacing, radii, type) in one place — so rebrand = editing tokens, not screens.

### 11.7 Accessibility
WCAG-minded contrast in both themes + a high-contrast option; full keyboard operability with visible focus; screen-reader labels; honors OS text scaling and reduced motion.

### 11.8 Acceptance criteria
- A brand-new tester completes connect → capture → screenshot → export bug bundle **without documentation** in a usability test.
- Primary action identifiable in **<3 seconds** on every screen.
- **Zero dead-ends**; the UI passes the "squint test".

---

## 12. Engine-Agnostic Game-Testing Suite

**The core new value proposition.** Because target games run on Unity, Unreal, Godot, Cocos, *and proprietary never-published in-house engines*, every metric must come from **OS-level surfaces that require no engine cooperation, no SDK, no app modification.** This is the GameBench-style approach and the only one that works uniformly on unknown engines.

> **Design guardrail:** if a capability would require an engine SDK or in-app hook, it is explicitly **out of scope.**

### 12.1 Performance HUD & profiler
| Metric | Android (engine-agnostic) | iOS (engine-agnostic) |
|---|---|---|
| FPS / frame time | `dumpsys SurfaceFlinger --latency` · `gfxinfo framestats` · Perfetto FrameTimeline | pymobiledevice3 **DVT core-profile FPS** |
| Jank % | `gfxinfo` jank % · Perfetto frame budgets (16/11/8 ms) | DVT frame timing |
| CPU | `/proc/<pid>/stat` · `dumpsys cpuinfo` | DVT **sysmon** |
| GPU | vendor sysfs / `gfxinfo` | DVT GPU counters |
| Memory | `dumpsys meminfo` · `/proc` | sysmon |
| Power / battery | `dumpsys batterystats` · coulomb counter | DVT **energy** |
| Thermal | `dumpsys thermalservice` · sysfs · throttle events | DVT thermal state |
| Network | `/proc/net` · `dumpsys netstats` | DVT **network** monitor |

**Source caveats (engine-agnostic reality):** `gfxinfo framestats` and Perfetto **FrameTimeline are not available for SurfaceView-based games** (most game engines render into a SurfaceView) — Android 12+ and non-SurfaceView rendering required for FrameTimeline. The universal fallback is `dumpsys SurfaceFlinger --latency <layer>` (per-layer, works for SurfaceView games) and `dumpsys SurfaceFlinger timestats` (present-to-present histograms); `SurfaceFlinger --latency` needs the layer id from `--list` first. FPS validation must therefore use SurfaceFlinger as ground truth, with Perfetto as the deep-dive profiler where supported.

- **Live HUD:** floating always-on-top mini-window (FPS, frame-time graph, CPU/mem/thermal, jank counter) visible while the tester plays.
- **Recorded sessions:** time-series charts; FPS stability %, 1 %/0.1 % lows, jank events, thermal-throttle markers, memory growth, battery drain rate.
- **Slow-session detection** aligned to platform norms (frame > 50 ms ≈ 20 FPS casual; > 34 ms ≈ 30 FPS action).

### 12.2 Device-tier matrix
Device profiles (chipset, RAM, refresh rate, OS); a run-across-tiers view that captures the same session on several attached devices and produces a **comparison report** (FPS / jank / thermal side-by-side).

### 12.3 Condition simulation
- **Network conditioning:** latency, jitter, packet loss, bandwidth caps (Android `tc`/proxy; iOS **condition inducer**). Presets: 3G/4G/5G/Wi-Fi/edge, airplane-mid-match, lossy metro.
- **Thermal conditioning:** induce thermal state and observe throttling.
- **Battery/charging states:** low-battery/charging to test power-saver frame caps.

### 12.4 Location simulation
GPS to a fixed point or a scripted route (Android mock-location provider; iOS **simulated location**). Route editor + speed control + a mandatory "reset location" safety. 

### 12.5 Gameplay automation
- Macro record/replay (kept) upgraded: **multi-touch, gesture library (tap/swipe/pinch/rotate), loop-with-count, drift-corrected timing**.
- **Soak / endurance runs:** replay a macro for N hours while sampling CPU/mem/thermal/FPS; auto-flag memory growth and FPS decay.
- **Image-anchored steps (optional):** template-image trigger, purely visual, so scripts survive minor UI changes.

### 12.6 Crash, ANR & stability
- Live crash/ANR detection (kept) → cluster duplicates, extract stack/tombstone snippets, correlate to the moment on the recorded video/perf timeline.
- **Stability score** per session (crashes + ANRs + jank + thermal throttles).

### 12.7 Evidence capture
- **Marker-synced recording:** timestamped markers auto-dropped on crash/jank/throttle; auto-clip a few seconds around each event.
- **Annotated + redacted screenshots** (blur essential for unreleased assets).
- **One-click evidence bundle:** video + perf report + logs + device metadata + repro steps, minimized/redacted.

### 12.8 Functional QA aids
IAP/store-flow logging; localization (switch locale, per-locale screenshots, missing-string rules); interruption testing (call/notification/background with state capture); deep-link library (kept, extended).

### 12.9 Reporting
HTML/PDF session report (perf charts, stability score, crash snippets, evidence), cross-device comparison reports, trend dashboards across builds, pass/fail vs configurable thresholds.

---

## 13. Full iOS & Android Instrumentation

Today the tool uses a fraction of what these tools expose. This section maps the **full potential** to concrete features.

### 13.1 iOS via pymobiledevice3 — unlock developer (DVT) services
| Capability | Service | State today → target |
|---|---|---|
| List / info | usbmux, lockdown | ✅ have → keep |
| Syslog capture | syslog | ✅ have → add process filter |
| App install/list/uninstall | installation proxy | ✅ have → keep |
| File browse (AFC) | afc | ◑ → fix `ls` dir-detection, add pull, rmdir |
| Crash logs | crash reports | ◑ → pull + list + symbolication guidance |
| Screenshot | DVT | ◑ currently on deprecated path → move to DVT |
| Screen recording | DVT `display start-video-stream` | ✗ returns null today → implement (device-initiated AV path) |
| Performance (CPU/mem/proc) | **sysmon** | ✗ → new: live process monitor |
| FPS / GPU | DVT core-profile | ✗ → new: iOS FPS profiling |
| Energy / power | DVT energy | ✗ → new: power telemetry |
| Network monitor | DVT network | ✗ → new: per-app network |
| Condition inducer | DVT | ✗ → new: condition simulation |
| Simulated location | DVT | ✗ → new: GPS spoofing / routes |
| Dev-mode / DDI | amfi / DDI | ◑ → guided enable + auto-mount |
| **iOS 17+ tunnel (RemoteXPC)** | tunneld / remote | 17.4+: **automatic userspace tunnel, no root, on Win/Linux/macOS**; 17.0–17.3.1 + external tools (lldb): privileged tunneld |

**Notes:** since iOS 17.4, pymobiledevice3 establishes the DVT tunnel automatically **in-process with a no-root userspace stack over USB** (macOS, Linux *and Windows*); privileged `tunneld` is only needed for iOS 17.0–17.3.1, for external consumers (lldb), or persistent/shared tunnels. **macOS remains the most reliable host** for DVT — reinforcing the macOS/Avalonia direction.

### 13.2 Android via adb — beyond logcat
| Capability | ADB / OS source | State → target |
|---|---|---|
| Logcat, install, files, mirror | as-is | ✅ keep (harden paths) |
| Macros | getevent/sendevent/input | ✅ have → multi-touch, text-safe |
| Monkey stress | monkey | ✅ have → richer sampling |
| **FPS / frame timeline** | SurfaceFlinger, gfxinfo, Perfetto | ◑ → full profiler |
| **Jank %** | `gfxinfo` | ✗ → new |
| **Thermal** | `dumpsys thermalservice`, sysfs | ✗ → new |
| **Power / battery** | `batterystats`, coulomb | ✗ → drain rate |
| **Per-uid network** | `netstats`, `/proc/net` | ✗ → new |
| **System trace** | Perfetto + traceconv | ✗ → capture + parse |
| Game mode | `cmd game` | ✗ → read/observe |
| Mock location | mock provider | ✗ → GPS spoofing |
| Network conditioning | `tc` / proxy | ✗ → condition simulation |
| Locale switch | am/settings | ✗ → localization testing |

**Perfetto is the modern, authoritative, engine-agnostic frame timeline; the app can trigger a trace, pull it, and parse FrameTimeline with zero engine involvement.** Caveats: FrameTimeline requires **Android 12+** and does **not cover SurfaceView-rendered games** — fall back to `dumpsys SurfaceFlinger --latency`/`timestats` (see §12.1).

---

## 14. APK/AAB Decompilation (Post-GA)

Extends the tool's sidecar/bundling model to reverse-engineer app packages for QA, security, and debugging.

### 14.1 Scope
- **APK:** full support (JADX + Apktool).
- **IPA and AAB→APK:** deferred until the macOS build and the respective toolchains are validated. **No recompile or re-signing in v1.**

### 14.2 Bundled decompilers (sidecars)
| Sidecar | Role |
|---|---|
| JADX (CLI) | DEX → Java source; service-style one-click use |
| Apktool | Full resource + manifest extraction |
| dex2jar + strings | Optional/quick paths |

Requires Java 17+ (64-bit) — a **jlink-trimmed JRE** is bundled only with this add-on pack (not in the core GA image) and surfaced via the dependency doctor.

### 14.3 Workflow
1. Drop/select an `.apk`.
2. A `DecompileManager` service spawns JADX via the existing process pipeline (`jadx -d <out> <apk>`).
3. Runs Apktool in parallel for resources.
4. Progress streams to UI through the same event-batching pipeline (§9.2).
5. Output opened in an IDE-style file explorer.

### 14.4 Usage/legal
- In-UI consent so testers only decompile packages they are authorized to inspect. ❌ No recompile/re-sign in v1; ❌ no IPA until the toolchain is validated.

---

## 15. Enhancements to Existing Features

- **Live log viewer:** debounced search, incremental filtering, regex/level/tag filters, sticky bookmarks, "follow/tail" without selection loss. [FEAT-01/03]
- **Sessions:** highlight active capture; guard delete while capturing; restart markers; "copy raw file" export. [FEAT-02/04/05, FEAT-27]
- **Bug report:** minimized, structured, correctly-branded, target-package-scoped + redacted. [FEAT-23/24]
- **Macros:** safe text input, multi-touch, drift compensation. [FEAT-18/19/33/34]
- **Stress (monkey):** periodic sampling, time-series report, percentage/clamp validation. [FEAT-12/13/14]
- **Vitals → full profiler (§12.1):** start/stop on navigation, time-series. [FEAT-20, BUG-05]
- **iOS parity:** AFC pull/rmdir, Developer-Mode guidance. [FEAT-10/11, TOOL-09/10/11/17]
- **Screen recording:** free-space/duration checks; store remote path. [FEAT-38, TOOL-20]

---

## 16. New Features (Industry-Aligned)

Beyond the game-testing suite (§12) and instrumentation (§13):

- **Multi-device fleet:** simultaneous capture, side-by-side logs/vitals, fleet health, device groups/labels, saved profiles.
- **Automation & CI:** headless **CLI** (`qadevicetool capture --serial … --package … --out …`, `profile`, `report`) reusing the engine; a local control API (named pipe / localhost HTTP) for Appium/CI harnesses; Appium bridge to correlate logs/video/perf to automated runs.
- **Issue-tracker integrations:** one-click bug filing to Jira / Azure DevOps / GitHub Issues / TestRail with the redacted evidence bundle attached.
- **Plugin system:** manifest-driven plugins (device types, log parsers, custom commands) — the *system* ships, the *marketplace* does not (§2). WASM-sandboxed parsers are post-GA.
- **AI-assisted triage (opt-in, local-first):** crash clustering, "explain this stack/ANR", duplicate grouping — acceptable only if privacy-neutral for unreleased content.
- **Team/cloud (opt-in):** shared session library with retention + access control; polished HTML/PDF stakeholder reports.
- **Reliability & self-service:** **dependency doctor** (adb/scrcpy/pymd3/Perfetto versions, USB driver, Developer Mode, iOS tunnel) with one-click remediation **and live tool health indicator**; app + tool auto-update; opt-in crash reporting.

---

## 17. Migration Plan & Phased Roadmap

### Track A — Stabilize & De-couple (Month 0–3)
| Phase | Scope | Output |
|---|---|---|
| **0 — Retarget** (Wk 1–2) | .NET 10 LTS; unify branding/app-data; consolidate CI (analyzers/format/audit/coverage); code-signing certs + updater keys | Signed installer; single CI |
| **1 — DI + Decouple** (Wk 2–5) | DI/host; de-static core services; real Dispose chain; `IDeviceStore` + `IUiDispatcher`; remove WPF from engine (interfaces). | WPF app compiles against extracted engine |
| **2 — Harden** (Wk 4–8) | Concurrency policy (§9.1), injection hardening, privacy defaults; burn down P0/P1 of `AUDIT`. | Trust boundary documented |
| **3 — Perf + UX** (Wk 6–10) | Perf (streaming/virtualization/batching); split God classes; UX safety + theming; VM/parser/fake-tool tests. | 100k-line capture smooth |

### Track B — Cross-Platform + Game-Testing Platform (Month 3–12)
| Phase | Scope | Output |
|---|---|---|
| **4** (Mo 3–5) | Engine extraction complete; headless **CLI**; begin Avalonia on Windows. | CLI in CI |
| **5** (Mo 4–7) | **Engine-agnostic profiler** (Android FPS/jank/thermal/power; HUD + charts + report). | Live HUD + session report |
| **6** (Mo 5–8) | **macOS build**; light up iOS DVT (tunnel, sysmon, FPS, energy, network, screen recording). | macOS + iOS parity |
| **7** (Mo 6–9) | Condition simulation, location, device-tier matrix + comparison, soak runs. | Full §12 suite |
| **8** (Mo 8–12) | Integrations, Appium bridge, plugin system (§16), AI triage (opt-in), team/cloud; Linux build; **retire WPF**. | GA ready |
| **Post-GA** | Zero-dependency add-ons: decompilers + trimmed JRE (§14), Perfetto path, Plugins marketplace. | Add-on packs |

> **Numbering note:** this is the single authoritative phase plan, superseding all prior numbering schemes in either source document.

> **Clean-machine verification:** every platform installer must be tested on a freshly provisioned OS image (no Java, no deps, no dev tools) before the platform ships — the GA gate below.

---

## 18. Prioritization Matrix

| Initiative | Impact | Effort | Priority |
|---|---|---|---|
| Retarget .NET 10 LTS | Critical (deadline) | Low | **P0 — now** |
| Uncouple engine + DI | Critical (unblocks everything) | Medium–High | **P0** |
| Concurrency + injection + privacy hardening | High | Medium | **P0/P1** |
| Perf: streaming/virtualization/batch | High | Medium | **P1** |
| Headless CLI on extracted engine | High (CI reach) | Medium | **P1** |
| **Engine-agnostic profiler (Android)** | **Very high (core value)** | High | **P1** |
| Zero-dependency bundling + DependencyDoctor | High (installed UX) | Medium | **P1** |
| Avalonia Windows parity | High | High | **P1/P2** |
| macOS + iOS DVT instrumentation | Very high (iOS) | High | **P2** |
| Condition-sim, tier matrix, soak | High (game QA) | Medium–High | **P2** |
| God-split + tests | **Medium** | Medium | **P2** |
| Integrations, Appium, plugin system, AI, cloud | Medium–High | High | **P3** |
| Linux build; decompilers; retire WPF | Medium | Medium | **P3 / post-GA** |

---

## 19. Success Metrics / KPIs & GA Gate

| KPI | WPF (current) | Target (Avalonia) | notes |
|---|---|---|---|
| Cold start | ~3–4 s | **≤ 2 s** (NativeAOT self-contained) | UniGetUI precedent: native boot, half-size installer |
| Idle RAM | ~200 MB | **≤ ~200 MB (parity)** | evidence: UniGetUI reports Avalonia ≈ WPF, not less; RAM reduction is **not** a KPI |
| Installer size | ~150 MB | **≤ 100 MB** core; decompilers in add-on pack | NativeAOT halves app portion (UniGetUI 58→28 MB) |
| Log viewer | laggy ~2 fps | **60 fps @ 100K rows** | virtualized; batched events (§9.2) |
| ADB concurrency | serialized | **per-device parallel** | §9.1 |
| Cross-platform | Windows-only | **Win + macOS + Linux** | |
| User setup steps | install .NET + tools | **single installer, zero prerequisites** | §7 |
| Version support | .NET 8 (EOS 2026-11-10) | **.NET 10 LTS (Nov 2028)** | |

### 19.1 GA acceptance gate
- ✅ Installs **and runs** on a **fresh** OS image with **no** manually installed dependencies (Win/macOS/Linux).
- ✅ adb, scrcpy, iOS support use **bundled** binaries only.
- ✅ Cold start **≤ 2 s** (NativeAOT); idle RAM **≈ WPF parity (≤ ~200 MB)** — measured on a real 100 K-line session.
- ✅ Log viewer sustains **60 fps / 100K rows**.
- ✅ Installer + bundled executables **code-signed**.
- ✅ **100% WPF feature parity** (MVP list, §2) before GA.

---

## 20. Risks, Assumptions & Open Questions

**Assumptions**
- Avalonia + .NET 10 is chosen; the engine is UI-agnostic and WPF keeps working during migration.
- pymobiledevice3 stays process-isolated (GPL-3.0); DVT flags/behavior version-gated behind a capability probe.
- Instrumentation is engine-agnostic only (OS surfaces); engine SDKs are out of scope by design.
- `AUDIT-FINDINGS.md` remains the source of truth for line-level fixes; this blueprint sequences them.

**Risks (merged register)**
| Risk | Likelihood | Mitigation |
|---|---|---|
| Deadline: .NET 8 EOS 2026-11-10 | High (date) | Retarget now; never block behind UI migration |
| SkiaSharp rendering differences (per OS, GPU vs CPU) | Medium | CI matrix across OS/GPU combos; SW rendering fallback |
| Avalonia XAML re-authoring cost | Medium | View-models portable; decouple early; keep WPF parallel |
| iOS 17+ tunnel friction | Low–Medium (17.4+ root-free userspace tunnel; elevation only on 17.0–17.3.1) | Capability probe; graceful prompts; macOS first |
| Avalonia memory ≈ WPF or higher (UniGetUI evidence) | Low–Medium | Target parity, not reduction; measure real workload before GA (§19) |
| Native-tool drift (adb/scrcpy/pymobiledevice3/Perfetto) | Medium | Version pin + sha256 manifest + capability probes + golden tests |
| Installer size growth (JRE) | Medium | JRE only in decomp add-on; jlink trim; signing |
| Bundled-bin licensing (JRE, scrcpy, JADX) | Medium | OpenJDK (GPL+CE); document all licenses (THIRD_PARTY) |
| Antivirus flags bundled tools | Medium | Code-sign everything |
| Instrumentation overhead | Medium | Background sampling, adaptive, ring buffers; validate ground truth |

**Open questions for stakeholders**
1. Confirm **Avalonia** (or Uno if browser reach is later desired).
2. Is a **macOS testing lab** planned (needed for full iOS)? Is **Windows + userspace tunnel** sufficient in the interim (viable for iOS 17.4+, not for 17.0–17.3.1)?
3. Which **engines** must be validated first (Unity/Unreal/Godot/Cocos/in-house)?
4. Which **issue trackers / automation** are in use (drives §16)?
5. Is **AI triage** acceptable under unreleased-content sensitivity (local-only vs cloud)?
6. Are network/thermal/location simulation in v1 of the game suite or fast-follow?

---

## Appendix A — Cross-reference
Defect-level backing: [`AUDIT-FINDINGS.md`](./AUDIT-FINDINGS.md) (BUG/ERR/FEAT/SEC/COMP/LEGAL/UX/MISS/NEW/TOOL). Scheduled: [`IMPLEMENTATION-PLAN.md`](./IMPLEMENTATION-PLAN.md). The blueprint groups ~162 items into R1–R11 and sequences them across the phases above.

## Appendix B — What was consolidated (provenance)
- **From `MODERNIZATION-AND-REWORK-BLUEPRINT.md` (rev 3, base):** current-state, Avalonia/.NET 10 decision, architecture/DI/DeviceStore, game suite (§12), iOS DVT (§13), security/UX spec (§10/11), roadmap, KPIs.
- **From `Redising on QA Tool.md`:** Non-Goals/MVP guardrails (§2), zero-dependency bundling strategy (§7), per-device semaphores + event batching (§9), APK decompilation (§14), GA acceptance gate (§19), sidecar health indicator/plugin system (§16).
- **From industry sources:** engine-agnostic instrumentation, Avalonia decision references (2026, with external links).

---

*Content from external sources was rephrased/summarized for compliance with licensing restrictions.*