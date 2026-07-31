# LogPro / QADeviceTool — Modernization, Enhancement & Full-Rework Blueprint

> **Author:** Engineering audit (Kiro)
> **Date:** 2026-07-31 (rev. 3 — adds minimalist UI design language & zero-confusion UX spec; rev. 2 added UI-framework migration + engine-agnostic game-testing suite)
> **Scope:** Forward-looking transformation plan — migrating off Windows-only WPF to a cross-platform stack, architecture rework, technology updates, performance & security hardening, UX modernization, and a game-tester-focused feature program (engine-agnostic performance profiling, full iOS/Android instrumentation via `pymobiledevice3` + `adb`), with a phased roadmap aligned with the current (2026) device-QA industry.
> **Relationship to existing docs:** This document is *strategic and forward-looking*. It complements — and does not repeat — the tactical, line-by-line defect registry in [`AUDIT-FINDINGS.md`](./AUDIT-FINDINGS.md) (~162 catalogued bugs across 12 categories) and the wave-based fix schedule in [`IMPLEMENTATION-PLAN.md`](./IMPLEMENTATION-PLAN.md). Where a theme here maps to specific bug IDs, they are cross-referenced.

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [What the Tool Is Today](#2-what-the-tool-is-today)
3. [Current-State Assessment](#3-current-state-assessment)
4. [Critical Risks & Structural Debt](#4-critical-risks--structural-debt)
5. [Technology Update & Modernization](#5-technology-update--modernization)
6. [UI Framework Decision — Migrating Off WPF](#6-ui-framework-decision--migrating-off-wpf)
7. [Architecture Rework (Target State)](#7-architecture-rework-target-state)
8. [Performance & Optimization](#8-performance--optimization)
9. [Security, Privacy & Compliance](#9-security-privacy--compliance)
10. [Testing, Quality & CI/CD](#10-testing-quality--cicd)
11. [UX / UI Modernization](#11-ux--ui-modernization)
12. [Engine-Agnostic Game-Testing Suite](#12-engine-agnostic-game-testing-suite)
13. [Full iOS & Android Instrumentation (pymobiledevice3 + ADB at Full Potential)](#13-full-ios--android-instrumentation-pymobiledevice3--adb-at-full-potential)
14. [Enhancements to Existing Features](#14-enhancements-to-existing-features)
15. [New Features (Industry-Aligned)](#15-new-features-industry-aligned)
16. [Cross-Platform Rollout & Migration Plan](#16-cross-platform-rollout--migration-plan)
17. [Full-Rework Target Architecture](#17-full-rework-target-architecture)
18. [Phased Roadmap](#18-phased-roadmap)
19. [Prioritization Matrix](#19-prioritization-matrix)
20. [Success Metrics / KPIs](#20-success-metrics--kpis)
21. [Risks, Assumptions & Open Questions](#21-risks-assumptions--open-questions)

---

## 1. Executive Summary

**LogPro** (repo: `QADeviceTool`) is a Windows-only WPF desktop utility for QA/QC game testers. It auto-captures device logs, manages timestamped test sessions, mirrors Android screens, takes snapshots, records screens, replays touch macros, runs monkey stress tests, and provides file/app management for both **Android** (via bundled `adb` + `scrcpy`) and **iOS** (via bundled `pymobiledevice3`).

The product is feature-rich and clearly the result of significant iteration (currently v3.2.0). It has a working CI pipeline, an installer + portable distribution, a test project, and — notably — an unusually thorough self-audit already exists in `.planning/`.

Two strategic decisions now anchor this revision of the blueprint:

- **Migrate off Windows-only WPF to Avalonia UI on .NET 10 LTS.** This is the "current-industry, runs-on-every-machine" answer: Avalonia is a WPF-inspired, XAML-based cross-platform UI framework that renders its own UI (SkiaSharp) and runs on **Windows, macOS, and Linux** (with experimental mobile/browser targets). It preserves the large existing C# codebase, and — critically for this product — a **macOS build unlocks the iOS testing features that Windows tooling cannot provide.** ([Avalonia positioning](https://avaloniaui.net/blog/what-is-wpf), [WPF modernization 2026](https://platform.uno/articles/wpf-modernization-in-2026-a-source-backed-decision-guide/))
- **Reposition the product around engine-agnostic game performance testing.** Mobile games are built on many engines — Unity, Unreal, Godot, Cocos, and **proprietary in-house engines that are never published**. The tool therefore must **never depend on engine SDKs, in-app hooks, or profiler integrations.** Instead it reads **OS-level signals** that exist for *any* running app: Android `SurfaceFlinger`/`gfxinfo`/Perfetto frame timelines, `/proc`, thermal and battery services; iOS `pymobiledevice3` developer (DVT) services — `sysmon`, core-profile/FPS, energy, network, condition inducer, simulated location. This is the same approach industry profilers such as GameBench use, and it is the only approach that works uniformly across secret, unavailable engines. ([Android jank/FPS](https://developer.android.com/topic/performance/vitals/tracking_jank), [Perfetto FrameTimeline](https://perfetto.dev/docs/data-sources/frametimeline), [GameBench FPS](https://docs.gamebench.net/docs/web-dashboard/the-performance-pane/), [pymobiledevice3 developer services](https://github.com/doronz88/pymobiledevice3))

Alongside these, the earlier findings still hold and are time-critical:

- **.NET 8 (current target) and .NET 9 both reach end of support on 2026-11-10** — ~3 months out. **.NET 10 is the LTS target** (supported to Nov 2028). The Avalonia migration and the runtime retarget happen together. ([.NET 8/9 EOS](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/), [Announcing .NET 10](https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/))
- **No DI container; God objects** (`SessionViewModel` 1,354 LOC, `MainViewModel`, `AdbService` 787, `IosService` 651); **broken IDisposable lifecycle**; **phantom ADB serialization semaphore**; **command-construction injection gaps**; **brand/app-data fragmentation** (LogPro / QADeviceTool / QAQCDeviceTool).

**The plan is a UI-agnostic engine first, then a cross-platform UI on top of it.** By extracting all device/session/instrumentation logic into pure .NET libraries (zero WPF references), the same engine powers (a) the new Avalonia desktop app on Windows/macOS/Linux and (b) a headless CLI for CI. The WPF app keeps running during the migration to avoid a big-bang rewrite.

---

## 2. What the Tool Is Today

### 2.1 Purpose
A desktop companion for manual + exploratory QA of mobile games: connect a device, auto-capture logs, take evidence (screenshots, recordings, bug-report zips), and drive light automation (macros, monkey).

### 2.2 Tech stack (as built)
| Concern | Current |
|---|---|
| Runtime | .NET 8 (`net8.0-windows`), WPF, `WinExe`, self-contained `win-x64` |
| MVVM | CommunityToolkit.Mvvm 8.4.0 (source generators) |
| Logging | NLog 6.1.0 |
| Android tooling | Bundled `adb` (platform-tools), `scrcpy` |
| iOS tooling | Bundled `pymobiledevice3` (PyInstaller bundle) + system-Python fallback |
| Packaging | Inno Setup installer + portable zip |
| CI | GitHub Actions (build/test/publish, installer, portable, release) |
| Tests | xUnit + FluentAssertions + Moq (services/models/helpers only) |
| Platform reach | **Windows only** |

### 2.3 Feature surface (12 views / view-models)
Dashboard, Sessions (log capture), Devices (info + mirroring), App Management (install/uninstall/control), File Explorer, Shell (adb/pymd3 terminal), Deep Link, Vitals (live memory/CPU), Macros (record/replay), Stress Test (monkey), Settings, plus a Command Palette (Ctrl+K).

### 2.4 Size & shape
- ~10,900 lines of C# across 78 files; ~4,700 lines of XAML across 17 files.
- Largest units: `SessionViewModel.cs` (1,354), `AdbService.cs` (787), `IosService.cs` (651), `SessionService.cs` (604).

---

## 3. Current-State Assessment

### 3.1 Strengths (what to preserve)
- **Genuinely useful, differentiated feature set** for game QA (dual app-specific logging, PID tracking through crashes, bug-report zips, macro replay, monkey GUI).
- **Robust external-process plumbing** in `ToolLauncher`/`ToolResolver`: stdout/stderr draining to avoid pipe deadlock, process tracking + kill-on-exit via `ProcessManagerService`, per-binary working directories, dynamic path resolution from `AppContext.BaseDirectory`. **This layer is UI-agnostic already and ports directly to Avalonia/CLI.**
- **Resilience touches**: device-monitor missed-poll debounce (3 polls before disconnect), parallel Android+iOS polling, atomic preferences save (temp + move), early startup diagnostics log.
- **Interface seams already exist** for the five core services — a strong foundation for DI and for the capability abstraction.
- **Security-aware in places**: `SecureMode` log redaction, serial hashing, path allowlists, GPL-compliance documentation for pymobiledevice3.
- **Mature process artifacts**: working CI, installer, changelog, and an extensive existing self-audit.

### 3.2 Weaknesses (what to fix)
- **Windows-only** — structurally caps iOS support (developer-mode DVT services, screen recording, sysmon are far more reliable from macOS).
- **UI and engine are entangled** — WPF types leak into services (`DialogService` uses `MessageBox`, `ThemeService` recreates `MainWindow`, VMs capture `Application.Current.Dispatcher`). Must be untangled before any UI migration.
- **No DI container**; hand-wired composition root; static ambient singletons.
- **God objects** and **duplicated device-list/selection logic** across nearly every view-model.
- **Broken lifecycle**, **heavyweight theme switch**, **concurrency docs ≠ implementation**, **stringly-typed navigation**, **identity fragmentation**.

---

## 4. Critical Risks & Structural Debt

Ranked by business/technical risk. (Tactical bug IDs from `AUDIT-FINDINGS.md` referenced in brackets.)

| # | Risk | Impact | Evidence / refs |
|---|---|---|---|
| R1 | **.NET 8 EOS on 2026-11-10** | No security patches; blocked from new tooling | Runtime = `net8.0-windows`; [Microsoft EOS notice](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/) |
| R2 | **Windows-only limits iOS reach & market** | Half the mobile market only partially supported; no macOS lab | `.continue-here.md` constraints |
| R3 | **UI/engine entanglement** | Blocks cross-platform migration and CLI reuse | WPF refs in services, static UI singletons |
| R4 | **No DI / God composition root** | Untestable VMs, leaks, fragile startup | `MainViewModel` ctor wires everything |
| R5 | **Device-shell command construction** relies on per-call allowlists with bypass gaps | Injection into device shell (paths / macro text / raw passthrough) | [TOOL-18/19, FEAT-34] |
| R6 | **Concurrency: documented ADB/iOS serialization not consistently implemented** | USB contention, "offline" flapping, stalls | [BUG-02, TOOL-01/02] |
| R7 | **Memory/perf: O(n²) string building, full-file loads, Reset churn** | UI freezes, OOM on large logs, dropped lines | [BUG-07/14, FEAT-25/29/30/31/32] |
| R8 | **Lifecycle leaks (no real Dispose chain)** | Ghost polls, memory growth, theme-switch regressions | [MISS-05, FEAT-35/36] |
| R9 | **Privacy: logs/bug-reports leak serials, app inventory, deep-link secrets** | Data-leak risk for unreleased titles | [SEC-01/04/06, FEAT-22/23] |
| R10 | **Identity fragmentation** (namespaces, app-data folders, branding) | Lost/duplicated user data, support confusion | `QAQCDeviceTool` vs `LogPro` folders |
| R11 | **GPL-3.0 dependency (pymobiledevice3) bundled** | Source-disclosure obligations if mis-linked | [LEGAL-02]; keep process-isolated |

---

## 5. Technology Update & Modernization

### 5.1 Runtime: retarget to .NET 10 LTS (do this with the UI migration)
- **Change** `net8.0-windows` → **`net10.0`** (engine libraries, platform-neutral) and platform TFMs only where OS APIs are needed. Publish self-contained for `win-x64`, `win-arm64`, `osx-x64`, `osx-arm64`, `linux-x64`.
- **Why:** .NET 8/9 hit EOS 2026-11-10; .NET 10 is LTS to Nov 2028 with runtime/GC/JIT gains and C# 14. ([Announcing .NET 10](https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/))

### 5.2 Target tech stack (what to change / update)

| Concern | From (today) | To (target) | Why |
|---|---|---|---|
| **UI framework** | WPF (`net8.0-windows`, Windows-only) | **Avalonia UI 11.x on .NET 10** | Cross-platform (Win/macOS/Linux), WPF-like XAML, self-rendered UI; macOS unlocks iOS features ([Avalonia](https://avaloniaui.net/blog/what-is-wpf)) |
| **Runtime** | .NET 8 | **.NET 10 LTS** | Support deadline + perf |
| **MVVM** | CommunityToolkit.Mvvm 8.4.0 | Same (works on Avalonia) | Portable; no rewrite of VM patterns |
| **DI / host** | none (manual `new`) | **`Microsoft.Extensions.Hosting` + DI + Options + Configuration** | Testability, lifecycle, typed settings |
| **Logging** | NLog (static `AppLogger`) | **`Microsoft.Extensions.Logging`** abstraction + Serilog/NLog sink (structured) | Injectable, structured, queryable |
| **Dialogs/UI services** | WPF `MessageBox` in service layer | Avalonia dialog service behind `IDialogService`/`IUiDispatcher` | Removes UI-from-engine coupling |
| **Charts/visualization** | none | **LiveChartsCore** or **ScottPlot** (Avalonia-compatible) | FPS/CPU/mem/thermal time-series graphs |
| **Screen mirroring** | scrcpy (Android) | scrcpy (cross-platform already: Win/macOS/Linux) | No change needed; works on all hosts |
| **iOS tooling** | pymobiledevice3 (bundled, isolated) | pymobiledevice3 (same, isolated) + expose DVT services | Cross-platform Python tool; unlock full potential (§13) |
| **Android tooling** | adb, scrcpy | adb, scrcpy + **Perfetto** trace processor (optional) | Engine-agnostic frame/CPU tracing |
| **Packaging** | Inno Setup + portable | Per-OS: MSIX/Inno (Win), `.app`/notarized DMG (macOS), AppImage/deb (Linux) | Native install UX per platform |
| **Tests** | xUnit + FluentAssertions + Moq | xUnit + Moq/NSubstitute (+ evaluate Shouldly for licensing) | VM tests unlocked by DI |
| **Package mgmt** | per-project versions | **Central Package Management** (`Directory.Packages.props`) | One source of truth |

### 5.3 Dependency hygiene
- Central Package Management; enable `Deterministic`, `ContinuousIntegrationBuild`, `<NuGetAudit>true</NuGetAudit>`, Dependabot, SBOM on release, Authenticode/notarization signing.
- Keep CommunityToolkit.Mvvm on the current stable 8.4.x line ([CommunityToolkit/dotnet](https://github.com/CommunityToolkit/dotnet)).

### 5.4 Bundled native-tool strategy
- **Version-pin + integrity-check + auto-update** `adb`/`platform-tools`, `scrcpy`, and (optionally) `perfetto`/`traceconv` per host OS. Ship a manifest (name, version, sha256, source URL) and probe on first run.
- **pymobiledevice3** stays **process-isolated** (GPL-3.0 compliance, [LEGAL-02]); wrap CLI behind a version/capability probe because DVT flags evolve across iOS versions.

---

## 6. UI Framework Decision — Migrating Off WPF

The direct answer to "change WPF to something current that works on every machine."

### 6.1 Decision: Avalonia UI on .NET 10 (primary recommendation)
**Recommended primary target: Avalonia UI.** It is the pragmatic, current-industry choice that (a) runs on Windows, macOS, and Linux from one codebase, (b) uses XAML + MVVM very close to WPF so the existing view-models and CommunityToolkit patterns port with minimal churn, and (c) preserves the ~11k lines of C# device/session logic. Avalonia renders its own UI with SkiaSharp for pixel-consistent results and is production-proven (e.g., UniGetUI shipped an Avalonia + NativeAOT migration, roughly halving installer size and reducing memory/GPU overhead). ([UniGetUI Avalonia migration](https://www.ntcompatible.com/story/unigetui-v202623-releases-nativeaot-cuts-download-size-by/))

**Why not the others (for this specific tool):**

| Option | Verdict | Reasoning |
|---|---|---|
| **Avalonia** | ✅ **Recommended** | Desktop-first, WPF-like XAML, Win/macOS/Linux, biggest code reuse, unlocks macOS for iOS |
| **Uno Platform** | ◑ Consider later | Adds browser/mobile reach; higher complexity than needed for a desktop tester tool ([5 frameworks for WPF modernization](https://platform.uno/articles/5-best-frameworks-for-wpf-modernization-in-2026/)) |
| **.NET MAUI** | ✗ Not ideal | Mobile-first; weaker desktop story; XAML dialect differs from WPF (more rewrite) ([MAUI vs Avalonia vs Uno](https://startdebugging.net/2026/05/maui-vs-avalonia-vs-uno-in-2026/)) |
| **Electron / Tauri / Flutter** | ✗ Reject | Throws away the entire C# engine; re-implement all adb/pymobiledevice3 orchestration in JS/Rust/Dart |
| **Stay WPF** | ✗ Rejected | Windows-only; blocks macOS iOS testing; the core reason for this program |

### 6.2 Precondition: extract a UI-agnostic engine (this is 80% of the work)
The migration is *not* mainly a XAML rewrite — it's **decoupling**. Before/while adopting Avalonia:
1. Move all services, models, capability logic, and orchestration into pure-`net10.0` libraries with **zero UI references** (`LogPro.Core`, `LogPro.Devices.Android`, `LogPro.Devices.Ios`, `LogPro.Instrumentation`).
2. Remove WPF from the engine: replace `MessageBox`/`Clipboard`/`Dispatcher`/`Application.Current` usages with interfaces (`IDialogService`, `IClipboard`, `IUiDispatcher`) implemented per-UI.
3. Keep the WPF app compiling against the extracted engine during migration (parallel-run), then stand up the Avalonia app against the same engine.
4. View-models are largely portable as-is (CommunityToolkit works on Avalonia); XAML views are re-authored in Avalonia XAML (syntax is close but not identical — bindings, styles, and control names differ).

### 6.3 What concretely changes vs. stays
- **Stays (ports directly):** models, all services, `ToolLauncher`/`ToolResolver`/`ProcessManagerService`, adb & pymobiledevice3 orchestration, scrcpy launching (scrcpy is itself cross-platform), most view-models, converters' logic.
- **Rewritten:** XAML views (WPF → Avalonia XAML), theming/resource dictionaries, dialog/clipboard/dispatcher adapters, tray/hotkey/window integration, packaging per OS.
- **New per-OS glue:** file dialogs, notifications, single-instance handling, deep-link/URL handlers, and native tool paths per RID.

---

## 7. Architecture Rework (Target State)

### 7.1 DI container (foundational)
Adopt `Microsoft.Extensions.DependencyInjection`; register all services + VMs; **de-static** `PreferencesService`, `ThemeService`, `DialogService`, `AppLogger`, `ProcessManagerService` behind interfaces; add interfaces for `MacroService`, `CrashDetector`, `LogAnalyzerService`, `DependencyChecker`. This is the enabler for testability, cross-platform, and the CLI.

### 7.2 Kill the God objects
- Shrink `MainViewModel` to navigation + shell state.
- Split `SessionViewModel` (1,354) → `LogCaptureViewModel`, `LogViewerViewModel`, `CrashPanelViewModel`, `EvidenceViewModel`, plus a `SessionExportService`.
- Decompose `AdbService`/`IosService` into focused capability services (DeviceInfo, Logs, Files, Apps, Input, Media, **Instrumentation**).

### 7.3 Device Abstraction Layer + shared Device Store (also the game-testing foundation)
Define capability interfaces so the UI queries capabilities instead of branching on platform:
`ILogSource`, `IScreenCapture`, `IScreenRecorder`, `IFileSystem`, `IAppManager`, `IInputInjector`, `IScreenMirror`, and — new — **`IPerformanceProfiler`**, **`IConditionInducer`** (thermal/network), **`ILocationSimulator`**. Android and iOS backends implement the subset they support.

A single **`IDeviceStore`** (one observable device list + selected device) replaces per-VM `DevicesChanged` subscriptions and the manual fan-out; an **`IUiDispatcher`** centralizes UI-thread marshalling (and is trivially portable to Avalonia). [FEAT-36, R8]

### 7.4 Navigation, messaging, concurrency, lifecycle
- Typed navigation service + view locator (replace stringly-typed switch + duplicated `navMap`).
- `WeakReferenceMessenger` for cross-VM events (crash detected, device selected, capture started).
- Explicit transport concurrency policy (serialize state-changing adb ops; bounded concurrency for reads); make async truly async; `TimeProvider` for testable timers.
- Real Dispose chain + navigation lifecycle hooks (start/stop pollers); in-place `ResourceDictionary` theme swap (kills the window-recreation hack).

---

## 8. Performance & Optimization

| Area | Problem | Fix |
|---|---|---|
| Console/log text | `string +=` in loops → O(n²), UI jank | `StringBuilder` + throttled (100 ms) flush; virtualized list. [BUG-07/14, FEAT-31/32/42] |
| Large log files | `File.ReadAllLines/Text` → OOM on 50–100 MB+ | Stream via `StreamReader`; tail-read preview; `Utf8JsonWriter` streaming export. [BUG-17, FEAT-29/30] |
| Live log collection | `Clear()`+`AddRange()` double `Reset` → freeze + selection loss | In-place `RemoveRange`; batch notifications; incremental filtering. [FEAT-25, NEW-10] |
| Device detail fetch | Sequential getprop/dumpsys | Batch to a single shell round-trip; parallelize safely. [TOOL-16] |
| Polling | Sequential Android→iOS | `Task.WhenAll`; emit property-change (battery/auth), not just connect/disconnect. [TOOL-14/15] |
| Rendering | Ensure `VirtualizingStackPanel` recycling on all lists; freeze brushes | 100k+ row lists stay smooth |
| Startup | Self-contained + R2R | Add startup timing telemetry; lazy-init non-critical services via DI |
| Instrumentation sampling | High-frequency FPS/CPU sampling can itself cause overhead | Ring buffers, background sampling threads, adaptive interval; never sample on UI thread |

Add **BenchmarkDotNet** micro-benchmarks for hot paths (log parse/filter/export, trace parsing) and a repeatable perf smoke test (1M-line synthetic log) in CI.

---

## 9. Security, Privacy & Compliance

Testing **unreleased games** makes data minimization first-class.

- **Command-injection hardening:** strict allowlists for all path/package/URL args; validate iOS AFC paths; escape/reject macro `input text`; stop raw `ExecuteCommandAsync` passthrough. [TOOL-18/19, FEAT-34, R5]
- **Redaction by default:** `SecureMode` on by default; centralize `SanitizeForLog` (serials, paths, deep-link query params) before *any* sink. [SEC-01, TOOL-13]
- **Bug-report minimization:** hash iOS serials, drop full package inventory, scope to target package. [FEAT-22/23, SEC-04]
- **Screenshot/video redaction:** blur/region-mask tools before any artifact leaves the machine (critical for pre-release content).
- **Data lifecycle:** retention (auto-delete old sessions/media), real export/delete-my-data in Settings, optional DPAPI at-rest encryption + owner-only ACLs. [SEC-02/03/13, COMP-01/02/04]
- **Wireless ADB & location spoofing safety:** confirm/warn before `adb tcpip` and before enabling simulated GPS (leaves device in a modified state).
- **Supply chain & licensing:** NuGet audit + Dependabot; signed/notarized releases; SBOM; keep pymobiledevice3 process-isolated (GPL-3.0); complete `THIRD_PARTY_NOTICES` (scrcpy Apache NOTICE, perfetto). [LEGAL-01..05]
- Add `SECURITY.md` (trust boundary: local user, USB devices, spawned native tools) + vuln-reporting process.

---

## 10. Testing, Quality & CI/CD

- **Unlock VM tests** via DI + interfaces (mock services). Cover navigation, device selection, capture start/stop, export, error branches, and instrumentation parsers.
- **Golden-file parser tests:** log parsers, `getevent` macro parser, `gfxinfo`/`SurfaceFlinger`/Perfetto output, pymobiledevice3 `sysmon`/DVT output — all fragile, all fixture-driven.
- **Fake-tool integration tests:** stub `adb`/`pymobiledevice3` executables to exercise `ToolLauncher` timeouts, non-zero exits, stderr deterministically.
- **Cross-platform CI matrix:** build/test on Windows, macOS, Linux runners once on Avalonia; publish coverage with a ratcheting gate.
- **Static analysis:** `.editorconfig`, `Microsoft.CodeAnalysis.NetAnalyzers`, `dotnet format` check, incremental `TreatWarningsAsErrors`.
- **CI cleanup:** consolidate the two overlapping workflows; cache NuGet; attach SBOM + checksums to releases.

---

## 11. UX / UI Modernization

> **North-star:** *A beautiful, calm, minimalist tool that a first-time tester can operate with zero training — it is always obvious what to press, what will happen, and what to do next.* Explicitly **not** a cyberpunk / neon / "hacker terminal" aesthetic. The visual language is quiet and professional so the **device screen, logs, and performance data are the heroes** — the chrome recedes.

### 11.1 Design principles (the rules every screen follows)
1. **Clarity over cleverness.** Plain labels ("Start capture", "Record screen"), never jargon-only icons. Every icon pairs with a text label or a tooltip.
2. **One primary action per screen.** Exactly one visually dominant button (the thing you most likely came to do); everything else is secondary/tertiary. No wall of equally-loud buttons.
3. **Progressive disclosure.** Show the 3–4 things a tester needs first; tuck advanced options behind "Advanced ▸" or a settings drawer. The default path is short.
4. **Never leave the user guessing.** Every action gives immediate feedback (state change, toast, progress). Disabled controls explain *why* they're disabled via tooltip ("Connect a device to start").
5. **Forgiving by default.** Destructive actions confirm; long actions can be cancelled; nothing silently fails.
6. **Calm, minimal, spacious.** Generous whitespace, few colors, restrained motion. Color is used sparingly and only to carry meaning (status, severity), never decoration.
7. **Consistency.** The same control looks and behaves the same everywhere; one component library, one spacing scale, one type scale.

### 11.2 Visual language (minimalist — the opposite of cyberpunk)
- **Aesthetic:** clean, soft, "quiet productivity tool" — think a modern, uncluttered desktop app: lots of neutral surface, subtle 1px dividers, gentle rounded corners (8–12px), soft shadows for elevation. **No** neon glows, gradients-on-everything, glitch effects, matrix greens, scanlines, or dark-with-cyan-accents.
- **Color system — neutral base + one calm accent:**
  - **Neutrals carry the UI:** near-white / very-light-gray surfaces in light mode; deep-charcoal (not pure black) surfaces in dark mode. Text uses layered neutrals (primary / secondary / tertiary) rather than pure black-on-white for softer contrast.
  - **A single, calm brand accent** (e.g., a muted indigo/teal — chosen with the team) used only for the primary action, selection, and focus. Not spread across the whole screen.
  - **Semantic colors reserved for meaning only:** success (green), warning (amber), error (red), info (blue). These appear in status dots, log-level tags, and pass/fail chips — nowhere decorative.
- **Typography:** one clean, highly-legible UI sans-serif (e.g., Inter, already bundled) with a small, strict **type scale** (e.g., 12 / 14 / 16 / 20 / 28). Monospace only where it earns its place: log/console text and metric readouts. Weight (not color) creates hierarchy.
- **Spacing & grid:** an **8px spacing scale** (4/8/16/24/32) applied consistently; comfortable line-height for logs; content max-widths so text never sprawls edge-to-edge.
- **Elevation:** flat by default; subtle shadow/tint only to separate overlays (dialogs, popovers, the perf HUD) from content.
- **Iconography:** one consistent, thin-stroke icon set (Fluent/Lucide-style), always with labels. Replaces today's broken/cyber glyphs. [UX-07]
- **Motion:** short, subtle, purposeful (120–200ms ease) for state changes and panel transitions; a "reduce motion" setting honors OS preference. No flashy animation.
- **Light & dark themes**, both fully theme-aware for **shell *and* content** via `DynamicResource` (today the shell stays dark). Light is the default; dark is a first-class equal, not a "hacker mode". [NEW-05/06]

### 11.3 Information architecture & navigation (so nothing is hidden)
- **Persistent left sidebar** with labeled sections (icon **+** word): Dashboard, Sessions, Devices, Performance, Apps, Files, Automation, Settings. The current section is clearly highlighted.
- **Global top bar:** the **active device selector** (always visible, so the tester always knows which device they're acting on), a global search/command palette (Ctrl/Cmd+K), and connection/health status.
- **Breadcrumbs** in drill-down areas (Files, Sessions) so users always know where they are and can step back.
- **Everything reachable in ≤2 clicks** from the sidebar; no feature buried more than one level deep.

### 11.4 Guided, self-explanatory UX (zero-confusion mechanics)
- **First-run guided setup wizard:** step-by-step "connect your device" — enable USB debugging / trust computer / developer mode, with a live checklist, inline help, and **one-click remediation** for each dependency (adb, driver, scrcpy, pymobiledevice3, iOS tunnel). [UX-17]
- **Helpful empty states:** every screen with no data shows a friendly illustration, a one-line explanation, and the single button to get started ("No devices yet — connect a device over USB").
- **Primary/secondary button hierarchy:** the main action is a filled accent button; secondary actions are outlined; tertiary are text-only. Users can tell at a glance what the "main" thing is.
- **Contextual affordances:** actions live next to the thing they act on (a session row's Stop/Export buttons appear on that row), not in a distant toolbar.
- **State-driven controls** ([UX-01/02/04/10/11/12]): Start/Stop, Mirror, Record, Uninstall, Delete, Push/Pull, Play/Stop are enabled only when valid; when disabled they show a tooltip explaining the precondition. A capture in progress shows a clear "● Recording" state.
- **Confirmations for destructive/irreversible actions** (delete session, uninstall app, wipe data, enable wireless ADB, spoof GPS) — with a plain-language summary of what will happen and a clear cancel.
- **Inline validation:** forms (deep links, macro params, thresholds) validate as you type with human-readable messages, not error codes.
- **Consistent feedback system:** a lightweight, non-blocking **toast** confirms success ("Screenshot saved"), a **global busy indicator** covers long operations with progress + cancel, and **live status dots** reflect real device/connection state. [UX-03/09/14]
- **Undo where possible** (e.g., recently deleted session) instead of only confirm dialogs.

### 11.5 Power-user layer (kept out of the beginner's way)
- **Command palette (Ctrl/Cmd+K):** fuzzy-search every action and device; the fastest path for pros — but never required for beginners.
- **Keyboard shortcuts:** Ctrl/Cmd+1..n for sections, arrow-key list navigation, documented in a discoverable "Shortcuts" sheet (press `?`). [UX-07/08]
- **Adjustable density** (comfortable / compact) for small laptops; **resizable/dockable panels** (e.g., logs side-by-side with the perf HUD) with sensible defaults that non-power-users never need to touch.

### 11.6 Component library (one source of truth)
A single Avalonia component set + design tokens (colors, spacing, radii, type) so the whole app is consistent and theme-swaps cleanly: buttons (primary/secondary/tertiary/danger), inputs (fixing today's broken dark-theme combo box), cards, tabs, tables/virtualized lists, status chips, toasts, dialogs, the perf HUD, and empty states. Tokens live in one place so rebranding = editing tokens, not screens.

### 11.7 Accessibility (part of "no one is confused")
- WCAG-minded contrast in **both** themes; a **high-contrast** theme option.
- Full **keyboard-only** operability with visible focus rings; logical tab order.
- **Screen-reader** support via automation peers/labels on every control.
- Respects OS settings for **reduced motion**, text scaling, and theme.

### 11.8 Acceptance criteria for "so good no one is confused"
- A brand-new tester completes "connect a device → start a capture → take a screenshot → export a bug bundle" **without documentation** in a quick usability test.
- On every screen, the intended primary action is identifiable in **under 3 seconds**.
- **Zero dead-ends:** no disabled control without an explanation; no empty screen without a next step; no destructive action without confirmation + (where feasible) undo.
- The UI passes a "squint test": with eyes half-closed the single most important action still stands out.

---

## 12. Engine-Agnostic Game-Testing Suite

**The core new value proposition.** Because target games run on many engines — including proprietary in-house engines that are never published — the tool must obtain everything from **OS-level instrumentation that requires no engine cooperation, no SDK, and no app modification.** Every capability below is engine-blind: it reads the same OS surfaces whether the game is Unity, Unreal, Godot, Cocos, or a studio's secret engine.

### 12.1 Real-time performance HUD & profiler (engine-agnostic)
A GameBench-style live overlay + recorded session covering the metrics testers actually judge games on ([GameBench performance pane](https://docs.gamebench.net/docs/web-dashboard/the-performance-pane/)):

| Metric | Android source (engine-agnostic) | iOS source (engine-agnostic) |
|---|---|---|
| **FPS / frame time** | `dumpsys SurfaceFlinger --latency <layer>`, `dumpsys gfxinfo <pkg> framestats`, Perfetto **FrameTimeline** | pymobiledevice3 DVT **core-profile / opengl** FPS session |
| **Jank / dropped frames** | `gfxinfo` janky-frame %, Perfetto frame classification (16ms=60fps, 11ms=90fps, 8ms=120fps budgets) | DVT frame timing |
| **CPU (per-process & total)** | `/proc/<pid>/stat`, `dumpsys cpuinfo`, `top` | pymobiledevice3 **sysmon** (process monitor) |
| **GPU utilization** | vendor sysfs / `gfxinfo` where exposed | DVT GPU counters where available |
| **Memory (PSS/RSS/graphics)** | `dumpsys meminfo <pkg>`, `/proc` | sysmon memory |
| **Power / energy drain** | `dumpsys batterystats`, `batteryproperties`, coulomb counter | DVT **energy** / power telemetry |
| **Thermal state** | `dumpsys thermalservice`, thermal sysfs, throttle events | DVT thermal state |
| **Network I/O** | `/proc/net`, `dumpsys netstats` per-uid | DVT **network** monitor |

- **Live HUD:** floating, always-on-top mini-window with FPS, frame-time graph, CPU/mem/thermal, and a jank counter — visible while the tester plays. ([Android jank targets](https://developer.android.com/topic/performance/vitals/tracking_jank), [Perfetto FrameTimeline](https://perfetto.dev/docs/data-sources/frametimeline))
- **Recorded sessions:** time-series charts (LiveCharts/ScottPlot) with FPS stability %, 1%/0.1% low FPS, jank events, thermal-throttle markers, memory-growth trend, and battery drain rate.
- **Slow-session detection** aligned with platform norms (frame > 50 ms ≈ 20 FPS, and 34 ms ≈ 30 FPS) for casual vs. action titles. ([Android slow sessions for games](https://developer.android.com/topic/performance/vitals/slow-session))

### 12.2 Device-tier matrix testing
Testers must validate low/mid/high-end tiers. Provide **device profiles** (chipset, RAM, refresh rate, OS) and a **run-across-tiers** view that captures the same session on several attached devices and produces a **comparison report** (FPS/jank/thermal side-by-side). ([device-tier testing](https://yrkan.com/blog/mobile-game-testing/))

### 12.3 Condition simulation (real-world reproduction)
- **Network conditioning:** latency, jitter, packet loss, bandwidth caps for online/multiplayer games — Android via `tc`/proxy or emulated network profiles; iOS via pymobiledevice3 **condition inducer**. Presets: 3G/4G/5G/Wi-Fi/edge, "airplane mid-match", "lossy metro".
- **Thermal conditioning:** induce thermal pressure states (iOS condition inducer) and observe throttling behavior.
- **Battery/charging states:** simulate low-battery/charging to test power-saver frame-rate caps.

### 12.4 Location simulation (location-based games)
GPS spoofing to a fixed point or a scripted route/path for geolocation games — Android mock-location provider; iOS via pymobiledevice3 **simulated location** (DVT). Includes a route editor + speed control, with a mandatory "reset location" safety action. ([pymobiledevice3 simulated location](https://github.com/doronz88/pymobiledevice3), [iOS location spoofing example](https://gist.github.com/lucasrod/52b8375d0b8a8212092c2440f0400fa3))

### 12.5 Gameplay automation for QA (engine-agnostic)
- **Macro record/replay** (already present) upgraded: multi-touch, gesture library (tap/swipe/pinch/rotate), loop-with-count for grinding/soak tests, and drift-corrected timing.
- **Soak / endurance runs:** replay a macro for N hours while sampling memory/thermal/FPS to catch leaks and long-session degradation; auto-flag memory growth and FPS decay.
- **Image-anchored steps (optional):** trigger next action when a template image appears on screen (engine-agnostic, purely visual) so scripts survive minor UI changes without engine hooks.

### 12.6 Crash, ANR & stability
- **Live crash/ANR detection** from logcat/syslog (already present) → cluster duplicates, extract stack/tombstone snippets, and correlate to the moment on the recorded video/perf timeline.
- **iOS crash-log pull + symbolication guidance**; Android tombstone/ANR trace pull.
- **Stability score per session** (crashes + ANRs + jank + thermal throttles).

### 12.7 Evidence capture (game-appropriate)
- **Marker-synced screen recording:** record gameplay with timestamped markers auto-dropped on crashes, jank spikes, and thermal throttles; auto-clip a few seconds around each event.
- **Annotated + redacted screenshots** (draw/blur) — blur is essential for unreleased assets.
- **One-click evidence bundle:** video + perf report + logs + device metadata + repro steps, minimized/redacted, ready to attach to a bug.

### 12.8 Functional game-QA aids
- **IAP / store-flow logging** (capture purchase/receipt log lines; sandbox account notes).
- **Localization testing:** switch device locale/language, capture per-locale screenshots for layout/overflow review, and flag missing-string patterns via log rules.
- **Save/resume & interruption testing:** scripted interruptions (call/notification/backgrounding/lock) with state-capture before/after.
- **Deep-link testing** (already present) extended with a link library and batch runner.

### 12.9 Reporting
- **Session report (HTML/PDF):** performance charts, stability score, crashes with snippets, evidence thumbnails, device/tier metadata, and pass/fail vs. configurable thresholds (e.g., "≥30 FPS avg, ≤5% jank, no ANRs").
- **Cross-device comparison report** for tier matrices.
- **Trend dashboards** across builds (regressions in FPS/jank/crashes over time).

> **Design guardrail:** none of §12 requires the game engine's cooperation. If a capability *would* require an engine SDK or in-app hook, it is explicitly out of scope, because in-house engines are unavailable. Everything is sourced from `adb`/OS surfaces or `pymobiledevice3` DVT services.

---

## 13. Full iOS & Android Instrumentation (pymobiledevice3 + ADB at Full Potential)

Today the tool uses a fraction of what these tools expose. This section maps the **full potential** to concrete features.

### 13.1 iOS via pymobiledevice3 — unlock developer (DVT) services
pymobiledevice3 is a pure-Python implementation that ships a CLI + Python API and runs on Windows/Linux/macOS; it exposes lockdown, AFC, crash logs, DDI/developer disk image, tunnels, and **developer (DVT) services**. ([pymobiledevice3 repo](https://github.com/doronz88/pymobiledevice3), [device-operator skill list](https://github.com/doronz88/pymobiledevice3/blob/master/.codex/skills/pymobiledevice3-device-operator/SKILL.md))

| Capability | pymobiledevice3 service | Status today → target |
|---|---|---|
| Device list / info | usbmux, lockdown | ✅ have → keep |
| Syslog capture | syslog | ✅ have → add filtering by process |
| App install/list/uninstall | installation proxy | ✅ have → keep |
| File browse (AFC) | afc | ◑ partial → fix ls dir-detection, add pull, rmdir [TOOL-09/10/11] |
| Crash logs | crash reports | ◑ → pull + list + symbolication guidance |
| **Screenshot** | DVT screenshot | ◑ deprecated path → move to DVT |
| **Screen recording** | DVT / QuickTime stream | ✗ returns null today → implement |
| **Performance (CPU/mem per process)** | **sysmon** | ✗ → **new: live process monitor** |
| **FPS / GPU** | DVT core-profile / opengl | ✗ → **new: iOS FPS profiling** |
| **Energy / power** | DVT energy | ✗ → **new: power telemetry** |
| **Network monitor** | DVT network | ✗ → **new: per-app network** |
| **Condition inducer** (thermal/network) | DVT condition inducer | ✗ → **new: condition simulation** |
| **Simulated location** | DVT simulated location | ✗ → **new: GPS spoofing / routes** |
| Developer mode / DDI mount | amfi / DDI | ◑ → guided enable + auto-mount |
| iOS 17+ tunnel (RemoteXPC) | tunneld / remote | ✗ → **required** for DVT on iOS 17+; run the tunnel daemon (needs elevated privileges) |

**Notes:** iOS 17+ requires establishing a **tunnel (RemoteXPC)** before most DVT services work; the app must manage the tunnel daemon lifecycle (and elevated-privilege prompt) transparently. Developer Mode must be enabled on the device. macOS is the most reliable host for these services, reinforcing the Avalonia+macOS direction.

### 13.2 Android via ADB — go beyond logcat
| Capability | ADB / OS source | Status → target |
|---|---|---|
| Device list/info/props | `adb devices`, getprop | ✅ have |
| Logcat capture (app-filtered by PID) | `adb logcat` | ✅ have → keep |
| Install/uninstall/control | `pm`, `am` | ✅ have |
| File browse/push/pull | `adb`/`content` | ✅ have → harden paths |
| Screenshot / screen record | `screencap`, `screenrecord` | ✅ have → add free-space checks [FEAT-38] |
| Screen mirror | scrcpy | ✅ have |
| Macros | `getevent`/`sendevent`/`input` | ✅ have → multi-touch, safe text [FEAT-33/34] |
| Monkey stress | `monkey` | ✅ have → richer sampling [MISS-01/02/03] |
| **FPS / frame timeline** | `dumpsys SurfaceFlinger --latency`, `gfxinfo framestats`, **Perfetto** | ◑ vitals → **new: full FPS/jank profiler** |
| **Jank %** | `gfxinfo` | ✗ → **new** ([gfxinfo janky frames](https://stackoverflow.com/questions/45236131/total-frames-and-janky-frames-in-dumpsys-gfxinfo-report)) |
| **Thermal** | `dumpsys thermalservice`, sysfs | ✗ → **new** |
| **Power / battery** | `dumpsys batterystats`, coulomb counter | ◑ → **new: drain rate over session** |
| **Per-uid network** | `dumpsys netstats`, `/proc/net` | ✗ → **new** |
| **System trace** | **Perfetto** (`perfetto`/`traceconv`) | ✗ → **new: capture + parse frame/CPU trace** |
| **GameManager / game mode** | `cmd game` | ✗ → **new: read/observe game mode & interventions** |
| Mock location | mock-location provider | ✗ → **new: GPS spoofing** |
| Network conditioning | `tc` / proxy | ✗ → **new: condition simulation** |
| Locale switch | `am`/settings | ✗ → **new: localization testing** |

**Perfetto** is the modern, engine-agnostic way to get authoritative frame timelines and CPU scheduling on Android; the app can trigger a trace, pull it, and parse the FrameTimeline for jank without any engine involvement. ([Perfetto FrameTimeline](https://perfetto.dev/docs/data-sources/frametimeline))

---

## 14. Enhancements to Existing Features

- **Live log viewer:** debounced search, incremental filtering, regex + level + tag filters, sticky bookmarks, "follow/tail" that survives batches without selection loss. [FEAT-01/03]
- **Sessions:** highlight the active capture; guard delete while capturing; restart separators; "copy raw file" export. [FEAT-02/04/05, FEAT-27]
- **Bug report:** minimized, structured, correctly branded, target-package scoped. [FEAT-23/24]
- **Macros:** auto-detect touchscreen node, safe text input, multi-touch, precise drift compensation. [FEAT-18/19/33/34]
- **Stress test (monkey):** periodic metric sampling, time-series report, defensive flags, percentage/clamp validation, iOS "not supported" guard. [FEAT-12/13/14, MISS-01/02/03]
- **Vitals → full profiler:** GPU/jank via `gfxinfo`, start/stop on navigation, time-series charts. [FEAT-20, BUG-05]
- **iOS parity:** AFC pull, `afc ls` dir detection, `rmdir`, actionable Developer-Mode guidance. [FEAT-10/11, TOOL-09/10/11/17]
- **Screen recording:** free-space + duration/size checks; store remote path instead of glob-searching. [FEAT-38, TOOL-20]

---

## 15. New Features (Industry-Aligned)

Beyond the game-testing suite (§12) and instrumentation (§13):

- **Multi-device fleet:** simultaneous capture, side-by-side logs/vitals, fleet health, device groups/labels, saved profiles.
- **Automation & CI:** **headless CLI** (`qadevicetool capture --serial … --package … --out …`, `profile`, `report`) reusing the engine; a local control API (named pipe/localhost HTTP) so Appium/CI harnesses can start/stop capture and pull artifacts; **Appium bridge** to correlate logs/video/perf to automated test runs.
- **Issue-tracker integrations:** one-click bug filing to **Jira / Azure DevOps / GitHub Issues / TestRail** with redacted evidence bundle attached.
- **AI-assisted triage (opt-in, privacy-respecting):** crash clustering, "explain this stack/ANR", duplicate grouping, likely-root-cause hints — local-first given unreleased-content sensitivity.
- **Team/cloud (opt-in):** shared session library with retention + access control; signed-link artifact upload; polished HTML/PDF stakeholder reports.
- **Reliability & self-service:** dependency doctor (adb/scrcpy/pymd3/Perfetto versions, USB driver, Developer Mode, iOS tunnel) with one-click remediation; app + tool auto-update; the tool's own crash reporting (opt-in). [ERR-03]

---

## 16. Cross-Platform Rollout & Migration Plan

The migration off WPF is staged to avoid a big-bang rewrite and to keep shipping.

1. **Engine extraction (parallel-run):** carve out `LogPro.Core` + platform/instrumentation libraries with zero UI refs; keep the WPF app building against them. (Delivers value immediately: enables the CLI and unit tests even before Avalonia.)
2. **CLI/headless host:** ship `qadevicetool` CLI on the extracted engine — usable in CI on Windows/macOS/Linux runners.
3. **Avalonia app (Windows first):** re-author views in Avalonia XAML against the same engine + view-models; reach parity with the WPF app on Windows.
4. **macOS build:** validate the Avalonia app on macOS; light up the iOS DVT features (tunnel, sysmon, FPS, energy, condition inducer, simulated location, screen recording) that are most reliable there.
5. **Linux build:** validate for Android-only labs.
6. **Retire WPF** once Avalonia reaches parity and is stable across the matrix.

**Effort reality check:** the bulk of effort is decoupling (§6.2) + re-authoring XAML views + per-OS packaging/glue. View-models and the entire engine port with minimal change.

---

## 17. Full-Rework Target Architecture

```
+-----------------------------------------------------------------------+
|  Presentation — Avalonia UI (Win/macOS/Linux)   [+ WPF during migration]|
|   Avalonia XAML Views + ViewModels (CommunityToolkit.Mvvm)              |
|   Navigation service · WeakReferenceMessenger · IUiDispatcher          |
+-----------------------------------------------------------------------+
|  Alternate front-end: Headless CLI (same engine, for CI)               |
+-----------------------------------------------------------------------+
|  Application layer (UI-agnostic, testable)                             |
|   Session orchestration · Performance profiling · Condition/Location    |
|   Reporting · AI triage · Export · DeviceStore (single source of truth) |
+-----------------------------------------------------------------------+
|  Domain / Device Abstraction (capabilities)                            |
|   ILogSource · IScreenCapture · IScreenRecorder · IFileSystem ·         |
|   IAppManager · IInputInjector · IScreenMirror ·                        |
|   IPerformanceProfiler · IConditionInducer · ILocationSimulator        |
+-----------------------------------------------------------------------+
|  Platform backends (plugins)                                           |
|   Android (adb, scrcpy, Perfetto)   iOS (pymobiledevice3, isolated,     |
|                                        DVT + tunnel)                    |
|   + future: cloud device providers, Appium bridge                      |
+-----------------------------------------------------------------------+
|  Infrastructure                                                        |
|   ToolLauncher/ToolResolver · ProcessManager · Preferences(Options) ·   |
|   Logging(MEL + sink) · Storage · per-OS adapters (dialogs/clipboard)   |
+-----------------------------------------------------------------------+
|  Host: Microsoft.Extensions.Hosting + DI + Configuration  (.NET 10 LTS) |
+-----------------------------------------------------------------------+
```

**Principles:** UI-agnostic engine (zero WPF/Avalonia refs below Presentation) · capabilities over platform branches · single device store · everything injectable · plugin backends · engine-agnostic instrumentation only.

---

## 18. Phased Roadmap

### Track A — Stabilize & De-couple (Month 0–3)
- **Phase 0 (Wk 1–2):** Retarget to **.NET 10 LTS**; unify branding/app-data; consolidate CI (+analyzers/format/audit/coverage). [R1, R10]
- **Phase 1 (Wk 2–5):** Introduce DI/host; de-static core services; real Dispose chain; `IDeviceStore` + `IUiDispatcher`; **remove WPF from the engine** (dialog/clipboard/dispatcher interfaces). [R3, R4, R8]
- **Phase 2 (Wk 4–8):** Concurrency policy + injection hardening + privacy defaults; burn down P0/P1 from `AUDIT-FINDINGS.md`. [R5, R6, R9]
- **Phase 3 (Wk 6–10):** Perf (streaming/virtualization/StringBuilder); split God classes; UX safety + theming; VM + parser + fake-tool tests. [R7]

### Track B — Cross-Platform + Game-Testing Platform (Month 3–12)
- **Phase 4 (Mo 3–5):** Engine extraction complete; **headless CLI** shipped; begin **Avalonia (Windows)**.
- **Phase 5 (Mo 4–7):** **Engine-agnostic performance profiler** (Android FPS/jank/thermal/power via SurfaceFlinger/gfxinfo/Perfetto; live HUD + charts + session report). [§12.1, §13.2]
- **Phase 6 (Mo 5–8):** **macOS Avalonia build**; light up **iOS DVT** (tunnel, sysmon, FPS, energy, network, screen recording). [§13.1]
- **Phase 7 (Mo 6–9):** Condition simulation (network/thermal), location spoofing, device-tier matrix + comparison reports, soak/endurance runs. [§12.2/12.3/12.4/12.5]
- **Phase 8 (Mo 8–12):** Integrations (Jira/ADO/TestRail/GitHub), Appium bridge, AI triage, team/cloud, trend dashboards; Linux build; retire WPF. [§15, §16]

---

## 19. Prioritization Matrix

| Initiative | Impact | Effort | Priority |
|---|---|---|---|
| Retarget to .NET 10 LTS | Critical (deadline) | Low | **P0 — now** |
| Remove WPF from engine (decouple) + DI | Critical (unblocks everything) | Medium-High | **P0** |
| Concurrency + injection + privacy hardening | High | Medium | **P0/P1** |
| Perf: streaming/virtualization/StringBuilder | High | Medium | **P1** |
| Headless CLI on extracted engine | High (CI reach) | Medium | **P1** |
| Engine-agnostic performance profiler (Android) | **Very High (core value)** | High | **P1** |
| Avalonia app (Windows parity) | High (migration) | High | **P1/P2** |
| macOS build + full iOS DVT instrumentation | **Very High (iOS unlock)** | High | **P2** |
| Condition/location simulation, tier matrix, soak | High (game QA) | Medium-High | **P2** |
| Split God classes; VM/parser/fake-tool tests | High (maintainability) | Medium | **P2** |
| Integrations, Appium, AI triage, cloud, dashboards | Medium-High (differentiation) | High | **P3** |
| Linux build; retire WPF | Medium | Medium | **P3** |

---

## 20. Success Metrics / KPIs

- **Support posture:** 100% on .NET 10 LTS before 2026-11-10; supported UI framework (Avalonia).
- **Cross-platform:** functional Windows + macOS + Linux builds from one codebase; macOS lights up iOS DVT features unavailable on Windows.
- **Engine-agnostic coverage:** performance metrics (FPS, jank %, CPU, mem, thermal, power, network) captured for **any** app with zero engine integration, validated against Unity/Unreal/Godot + a proprietary sample.
- **Profiler quality:** FPS sampling overhead < a few % CPU; frame-timeline accuracy validated against Perfetto ground truth.
- **Reliability:** no UI freeze > 250 ms during 1M-line capture; no ghost polls after navigation/theme switch.
- **Testability:** VM coverage 0% → 60%+; ratcheting overall coverage in a cross-OS CI matrix.
- **Privacy/security:** SecureMode default; redacted, minimized bug bundles; signed/notarized releases + SBOM; clean NuGet audit.
- **Maintainability:** no VM/service file > ~400 LOC; single app-data location; single CI workflow.
- **Automation reach:** CLI usable in CI; ≥1 issue-tracker integration in production use.
- **Usability (zero-confusion goal):** a new tester completes connect → capture → screenshot → export a bug bundle **without docs** in usability testing; primary action on every screen identifiable in < 3s; no disabled control without an explanatory tooltip; SUS (System Usability Scale) target ≥ 80.
- **Design consistency:** 100% of screens built from the shared component library + design tokens; both light & dark themes fully theme-aware for shell and content; WCAG contrast met in both.

---

## 21. Risks, Assumptions & Open Questions

**Assumptions**
- Avalonia + .NET 10 is the chosen path; the engine is extracted UI-agnostic first so the WPF app keeps working during migration.
- pymobiledevice3 stays **process-isolated** (GPL-3.0); its DVT flags/behaviors are version-gated behind a capability probe.
- Instrumentation is strictly **engine-agnostic** (OS surfaces only); anything needing an engine SDK is out of scope by design.
- The tactical `AUDIT-FINDINGS.md` remains the source of truth for line-level fixes; this blueprint sequences them.

**Risks**
- **Deadline compression:** retarget to .NET 10 first (Phase 0) — do not block it behind the Avalonia migration.
- **Avalonia XAML re-authoring cost:** real but bounded; mitigated by porting view-models unchanged and decoupling early.
- **iOS 17+ tunnel/elevated-privilege friction:** DVT services need the RemoteXPC tunnel and admin rights; UX must handle prompts gracefully; macOS is the most reliable host.
- **Native-tool drift:** adb/scrcpy/pymd3/Perfetto CLI changes can break parsing; mitigate with capability probes, version pinning, and golden-file tests.
- **Instrumentation overhead & accuracy:** high-frequency sampling can perturb the game; use background sampling, ring buffers, adaptive intervals, and validate against Perfetto/DVT ground truth.

**Open questions for stakeholders**
1. Confirm **Avalonia** as the target (vs. Uno if browser reach is desired later).
2. Is a **macOS testing lab** available/planned (required to unlock full iOS)?
3. Which **engines** must be validated first (Unity/Unreal/Godot/Cocos/in-house)?
4. Which **issue trackers / automation frameworks** are in use (drives §15 order)?
5. Is **AI triage** acceptable given unreleased-content sensitivity (local-only vs. cloud)?
6. Are **network/thermal/location simulation** in scope for v1 of the game suite, or fast-follow?

---

### Appendix A — Cross-reference to the tactical audit
Defect-level backing lives in [`AUDIT-FINDINGS.md`](./AUDIT-FINDINGS.md) (BUG/ERR/FEAT/SEC/COMP/LEGAL/UX/MISS/NEW/TOOL) and is scheduled in [`IMPLEMENTATION-PLAN.md`](./IMPLEMENTATION-PLAN.md). This blueprint groups those ~162 items into risk themes (R1–R11) and sequences them across the Track A phases.

### Appendix B — Sources (current-industry, 2026)
- .NET support & .NET 8/9 EOS (2026-11-10): [support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core), [.NET 8/9 EOS](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/); .NET 10 LTS: [Announcing .NET 10](https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/)
- Cross-platform UI: [Avalonia "What is WPF" 2026](https://avaloniaui.net/blog/what-is-wpf), [platform.uno WPF modernization guide](https://platform.uno/articles/wpf-modernization-in-2026-a-source-backed-decision-guide/), [MAUI vs Avalonia vs Uno](https://startdebugging.net/2026/05/maui-vs-avalonia-vs-uno-in-2026/), [UniGetUI Avalonia migration](https://www.ntcompatible.com/story/unigetui-v202623-releases-nativeaot-cuts-download-size-by/)
- iOS instrumentation: [pymobiledevice3](https://github.com/doronz88/pymobiledevice3), [device-operator services](https://github.com/doronz88/pymobiledevice3/blob/master/.codex/skills/pymobiledevice3-device-operator/SKILL.md), [iOS location spoofing](https://gist.github.com/lucasrod/52b8375d0b8a8212092c2440f0400fa3)
- Android performance / engine-agnostic: [tracking jank/FPS](https://developer.android.com/topic/performance/vitals/tracking_jank), [slow sessions (games)](https://developer.android.com/topic/performance/vitals/slow-session), [analyze/optimize game performance](https://developer.android.com/games/optimize/gameperformance), [Perfetto FrameTimeline](https://perfetto.dev/docs/data-sources/frametimeline), [gfxinfo janky frames](https://stackoverflow.com/questions/45236131/total-frames-and-janky-frames-in-dumpsys-gfxinfo-report), [GameBench FPS](https://docs.gamebench.net/docs/web-dashboard/the-performance-pane/)
- Game QA needs: [mobile game testing](https://yrkan.com/blog/mobile-game-testing/), [QA checklist](https://qawerk.com/blog/mobile-game-testing-detailed-qa-checklist/), [2026 studio guide](https://snoopgame.com/blog/mobile-game-testing-complete-guide-2026/)

*Content from external sources was rephrased/summarized for compliance with licensing restrictions.*
