# LogPro / QADeviceTool — Modernization, Enhancement & Full-Rework Blueprint

> **Author:** Engineering audit (Kiro)
> **Date:** 2026-07-31
> **Scope:** Forward-looking transformation plan — architecture rework, technology updates, performance & security hardening, UX modernization, feature enhancements, new capabilities, and a phased roadmap aligned with the current (2026) desktop / device-QA industry.
> **Relationship to existing docs:** This document is *strategic and forward-looking*. It complements — and does not repeat — the tactical, line-by-line defect registry in [`AUDIT-FINDINGS.md`](./AUDIT-FINDINGS.md) (~162 catalogued bugs across 12 categories) and the wave-based fix schedule in [`IMPLEMENTATION-PLAN.md`](./IMPLEMENTATION-PLAN.md). Where a theme here maps to specific bug IDs, they are cross-referenced.

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [What the Tool Is Today](#2-what-the-tool-is-today)
3. [Current-State Assessment](#3-current-state-assessment)
4. [Critical Risks & Structural Debt](#4-critical-risks--structural-debt)
5. [Technology Update & Modernization](#5-technology-update--modernization)
6. [Architecture Rework (Target State)](#6-architecture-rework-target-state)
7. [Performance & Optimization](#7-performance--optimization)
8. [Security, Privacy & Compliance](#8-security-privacy--compliance)
9. [Testing, Quality & CI/CD](#9-testing-quality--cicd)
10. [UX / UI Modernization](#10-ux--ui-modernization)
11. [Enhancements to Existing Features](#11-enhancements-to-existing-features)
12. [New Features (Industry-Aligned)](#12-new-features-industry-aligned)
13. [Cross-Platform Strategy](#13-cross-platform-strategy)
14. [Full-Rework Target Architecture](#14-full-rework-target-architecture)
15. [Phased Roadmap](#15-phased-roadmap)
16. [Prioritization Matrix](#16-prioritization-matrix)
17. [Success Metrics / KPIs](#17-success-metrics--kpis)
18. [Risks, Assumptions & Open Questions](#18-risks-assumptions--open-questions)

---

## 1. Executive Summary

**LogPro** (repo: `QADeviceTool`) is a Windows-only WPF desktop utility for QA/QC game testers. It auto-captures device logs, manages timestamped test sessions, mirrors Android screens, takes snapshots, records screens, replays touch macros, runs monkey stress tests, and provides file/app management for both **Android** (via bundled `adb` + `scrcpy`) and **iOS** (via bundled `pymobiledevice3`).

The product is feature-rich and clearly the result of significant iteration (currently v3.2.0). It has a working CI pipeline, an installer + portable distribution, a test project, and — notably — an unusually thorough self-audit already exists in `.planning/`.

However, the codebase carries meaningful structural debt and is approaching a hard technology deadline:

- **Time-critical:** The app targets **.NET 8**, which reaches **end of support on November 10, 2026** — approximately three months from this writing. .NET 9 shares the same EOS date. The industry-current LTS target is **.NET 10** (supported through November 2028). ([Microsoft support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core), [.NET 8/9 EOS announcement](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/))
- **No dependency-injection container.** The entire service + view-model object graph is hand-assembled with `new` inside a single `MainViewModel` constructor, which doubles as a God/mediator object. This blocks testability, complicates lifecycle management, and leaks event subscriptions.
- **God classes.** `SessionViewModel` is 1,354 lines and mixes capture, filtering, crash detection, recording, export, and AI concerns. `AdbService` (787) and `IosService` (651) are similarly overloaded.
- **Concurrency & correctness gaps.** Documentation describes an ADB serialization semaphore that (per the code-level deep dive) does not consistently exist; device-shell command construction relies on per-call allowlists with known bypass paths; several async methods perform blocking I/O.
- **Brand / identity inconsistency.** Namespaces, assembly name, product name, and app-data folders are split across "LogPro", "QADeviceTool", and "QAQCDeviceTool".

This blueprint proposes a **two-track strategy**:

- **Track A — Stabilize & Modernize In-Place (0–3 months):** Retarget to .NET 10 LTS, introduce a DI container, break up God classes, fix the concurrency/security/perf themes, unify branding, and raise test coverage. This preserves the working WPF product while removing the support-deadline risk and the worst debt.
- **Track B — Full Rework Toward a Modern Device-QA Platform (3–12 months):** Re-architect around a clean layered/plugin model with a device-abstraction layer, add cloud/team features, automation-framework integrations (Appium/ADB-over-network), richer analytics and reporting, and evaluate a cross-platform UI (Avalonia/Uno) to escape the Windows-only constraint that limits macOS-based iOS testing.

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

### 2.3 Feature surface (12 views / view-models)
Dashboard, Sessions (log capture), Devices (info + mirroring), App Management (install/uninstall/control), File Explorer, Shell (adb/pymd3 terminal), Deep Link, Vitals (live memory/CPU), Macros (record/replay), Stress Test (monkey), Settings, plus a Command Palette (Ctrl+K).

### 2.4 Size & shape
- ~10,900 lines of C# across 78 files; ~4,700 lines of XAML across 17 files.
- Largest units: `SessionViewModel.cs` (1,354), `AdbService.cs` (787), `IosService.cs` (651), `SessionService.cs` (604).

---

## 3. Current-State Assessment

### 3.1 Strengths (what to preserve)
- **Genuinely useful, differentiated feature set** for game QA (dual app-specific logging, PID tracking through crashes, bug-report zips, macro replay, monkey GUI).
- **Robust external-process plumbing** in `ToolLauncher`/`ToolResolver`: stdout/stderr draining to avoid pipe deadlock, process tracking + kill-on-exit via `ProcessManagerService`, per-binary working directories, dynamic path resolution from `AppContext.BaseDirectory`.
- **Resilience touches**: device-monitor missed-poll debounce (3 polls before disconnect), parallel Android+iOS polling, atomic preferences save (temp + move), early startup diagnostics log.
- **Interface seams already exist** for the five core services (`IAdbService`, `IIosService`, `IScrcpyService`, `IDeviceMonitorService`, `ISessionService`) — a strong foundation for DI.
- **Security-aware in places**: `SecureMode` log redaction, serial hashing, path allowlists, GPL-compliance documentation for pymobiledevice3.
- **Mature process artifacts**: working CI, installer, changelog, and an extensive existing self-audit.

### 3.2 Weaknesses (what to fix)
- **No DI container**; hand-wired composition root; static ambient singletons (`PreferencesService`, `ThemeService`, `AppLogger`, `DialogService`, `ProcessManagerService`).
- **God objects** (`MainViewModel`, `SessionViewModel`) and **duplicated device-list/selection logic** across nearly every view-model.
- **Broken lifecycle**: `IDisposable` is declared widely but child view-models are never disposed; event subscriptions leak.
- **Heavyweight theme switch** recreates the entire `MainWindow` rather than swapping resource dictionaries in place.
- **Concurrency documentation ≠ implementation** (the "phantom semaphore"), blocking I/O in `async` methods, and stringly-typed navigation with dead switch arms.
- **Identity fragmentation** across LogPro / QADeviceTool / QAQCDeviceTool.
- **Windows-only**, which structurally limits full iOS support (many iOS features are gated behind macOS-only tooling).

---

## 4. Critical Risks & Structural Debt

Ranked by business/technical risk. (Tactical bug IDs from `AUDIT-FINDINGS.md` referenced in brackets.)

| # | Risk | Impact | Evidence / refs |
|---|---|---|---|
| R1 | **.NET 8 EOS on 2026-11-10** | No security patches; compliance/audit failures; blocked from new tooling | Runtime = `net8.0-windows`; [Microsoft EOS notice](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/) |
| R2 | **No DI / God composition root** | Untestable VMs, leaks, fragile startup, high change cost | `MainViewModel` ctor wires everything |
| R3 | **Device-shell command construction** relies on per-call allowlists with known bypass gaps | Potential injection into device shell via file paths / macro text / raw command passthrough | [TOOL-18, TOOL-19, FEAT-34], `ExecuteCommandAsync` passthrough |
| R4 | **Concurrency: documented ADB/iOS serialization not consistently implemented** | USB transport contention, device "offline" flapping, stalls during polling | [BUG-02, TOOL-01, TOOL-02], deep-dive found no lock in `AdbService` |
| R5 | **Memory/perf: O(n²) string building, full-file loads, Reset-based collection churn** | UI freezes, OOM on large logs, dropped log lines | [BUG-07/14, FEAT-25/29/30/31/32] |
| R6 | **Lifecycle leaks (no real Dispose chain)** | Ghost polls, memory growth, actions on detached VMs, especially across theme switch | [MISS-05, FEAT-35, FEAT-36] |
| R7 | **Privacy: logs/bug-reports can leak serials, package inventory, deep-link secrets** | Data-leak risk for studios testing unreleased titles | [SEC-01/04/06, FEAT-22/23, TOOL-13] |
| R8 | **Identity fragmentation** (namespaces, app-data folders, branding) | Lost/duplicated user data, support confusion, unprofessional | README/CHANGELOG vs `QAQCDeviceTool` app-data folder |
| R9 | **Windows-only iOS limitations** | Half the mobile market only partially supported | `.continue-here.md` constraint notes |
| R10 | **GPL-3.0 dependency (pymobiledevice3) bundled** | Source-disclosure obligations if linked/bundled improperly | [LEGAL-02]; mitigated via process isolation, must stay isolated |

---

## 5. Technology Update & Modernization

### 5.1 Runtime: retarget to .NET 10 LTS (highest priority)
- **Change** `net8.0-windows` → `net10.0-windows` and republish self-contained `win-x64` (add `win-arm64` — see 5.5).
- **Why:** .NET 8 and 9 both hit end of support 2026-11-10; .NET 10 is LTS through Nov 2028 and brings runtime/GC/JIT performance gains and updated WPF fixes. ([Announcing .NET 10](https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/))
- **Effort:** Low-to-moderate. WPF on .NET 10 is largely a drop-in retarget; validate third-party packages, trimming/R2R settings, and single-file behavior.
- **Bonus:** Enables modern language features (collection expressions, primary constructors already usable, `System.Text.Json` source-gen improvements, `TimeProvider` for testable timers).

### 5.2 Dependency updates & hygiene
- **CommunityToolkit.Mvvm** — currently 8.4.0; keep on the latest 8.4.x (the current stable line per [CommunityToolkit/dotnet](https://github.com/CommunityToolkit/dotnet)).
- **NLog 6.1.0** — evaluate staying on NLog vs. standardizing on **`Microsoft.Extensions.Logging`** as the abstraction (with NLog or Serilog as the sink). This aligns with the DI work in §6 and enables structured logging.
- Add **Central Package Management** (`Directory.Packages.props`) so versions are pinned in one place across app + tests.
- Enable **`Deterministic`**, **`ContinuousIntegrationBuild`**, and **NuGet audit** (`<NuGetAudit>true</NuGetAudit>`) to surface vulnerable transitive packages in CI.
- Test stack: bump `Microsoft.NET.Test.Sdk`, `xunit`, `coverlet`; consider migrating assertions to the maintained line (FluentAssertions licensing changed in recent versions — evaluate **Shouldly** or built-in assertions as an alternative to avoid license surprises).

### 5.3 Adopt `Microsoft.Extensions.*` foundation
- `Microsoft.Extensions.Hosting` + `DependencyInjection` + `Logging` + `Configuration` + `Options`.
- This gives a real composition root, typed options (replace static `PreferencesService`), structured logging, and a graceful startup/shutdown pipeline. (See §6.)

### 5.4 Bundled native tool strategy
- **Version-pin and auto-update** `adb`/`platform-tools` and `scrcpy`; today they are committed binaries with no update path. Add a manifest (tool name, version, sha256, source URL) and a first-run/periodic integrity + update check.
- **pymobiledevice3**: keep the **process-isolation** model (critical for GPL compliance per [LEGAL-02]). Track upstream CLI changes (the deep dive flagged version-sensitive flags: `--no-color` position, `crash pull` args) behind a small capability/version-probe layer.
- Consider replacing the PyInstaller bundle friction with a documented "managed dependency" install flow (like adb), reducing AV false-positives and binary bloat.

### 5.5 Build/packaging modernization
- Add **`win-arm64`** RID (Windows on ARM is now mainstream for dev/test laptops).
- Evaluate **MSIX** packaging alongside Inno Setup for cleaner install/update/uninstall and enterprise deployment; keep portable zip.
- Turn on **ReadyToRun** (already on) and evaluate **partial trimming** for the managed assemblies (native tools stay excluded, as they are today).
- Add **code signing** (Authenticode / trusted signing) to reduce SmartScreen friction — important for a tool that spawns adb and mirrors screens.

---

## 6. Architecture Rework (Target State)

### 6.1 Introduce a DI container (foundational)
Replace the hand-wired `MainViewModel` composition root with `Microsoft.Extensions.DependencyInjection`:

```
Program/App startup:
  services.AddSingleton<IAdbService, AdbService>();
  services.AddSingleton<IIosService, IosService>();
  services.AddSingleton<IScrcpyService, ScrcpyService>();
  services.AddSingleton<IDeviceMonitorService, DeviceMonitorService>();
  services.AddSingleton<ISessionService, SessionService>();
  services.AddSingleton<IPreferencesService, PreferencesService>();   // de-static
  services.AddSingleton<IDialogService, DialogService>();             // de-static
  services.AddSingleton<IThemeService, ThemeService>();               // de-static
  services.AddSingleton<IDeviceStore, DeviceStore>();                 // NEW (see 6.3)
  services.AddTransient<MainViewModel>();
  services.AddTransient<SessionViewModel>();  // + all child VMs
  services.AddSingleton<MainWindow>();
```

- **De-static** `PreferencesService`, `ThemeService`, `DialogService`, `AppLogger` into injectable services behind interfaces. Keep thin static shims temporarily if needed for migration.
- **Add interfaces** for the remaining concretes used by VMs (`MacroService`, `CrashDetector`, `LogAnalyzerService`, `DependencyChecker`).
- **Note the existing constraint** in `.continue-here.md`: "No DI container — do not add without refactoring the entire startup pipeline." That refactor is exactly what this section authorizes and scopes.

### 6.2 Kill the God objects
- **`MainViewModel`** → shrink to navigation + shell state. Move device-selection fan-out into a shared store (6.3). Use a navigation service (6.4).
- **`SessionViewModel` (1,354 LOC)** → split by responsibility:
  - `LogCaptureViewModel` (start/stop, status, batching)
  - `LogViewerViewModel` (filtering, search, bookmarks, color coding)
  - `CrashPanelViewModel` (crash detection UI)
  - `EvidenceViewModel` (screenshot, recording, bug-report zip)
  - `SessionExportService` (CSV/JSON/raw copy — move export logic out of the VM)
- **`AdbService` / `IosService`** → decompose into focused capability services (DeviceInfo, Logcat/Syslog, Files, Apps, Input/Macro, Media) behind a common device-capability abstraction (6.3).

### 6.3 Introduce a Device Abstraction Layer + shared Device Store
Two of the largest sources of duplication and coupling are (a) every VM independently subscribing to `DevicesChanged` and rebuilding its own list, and (b) `AdbService`/`IosService` exposing parallel-but-different APIs.

- **`IDevice` capability model:** define capability interfaces (`ILogSource`, `IScreenCapture`, `IScreenRecorder`, `IFileSystem`, `IAppManager`, `IInputInjector`, `IScreenMirror`). Android and iOS backends implement the subset they support; the UI queries capabilities instead of branching on `Platform == "iOS"` everywhere.
- **`IDeviceStore` (single source of truth):** one observable collection of connected devices + the selected device. VMs bind to it; no per-VM `DevicesChanged` subscriptions, no manual fan-out from `MainViewModel`. This directly resolves [FEAT-36] (N×M event flood) and much of the lifecycle-leak problem.
- **Marshalling:** the store (or a small `IUiDispatcher` abstraction wrapping `Application.Current.Dispatcher`) centralizes UI-thread marshalling so VMs stop each capturing the dispatcher and become unit-testable off the UI thread.

### 6.4 Navigation & messaging
- Replace the stringly-typed `Navigate(string)` switch (and the duplicated `navMap` in code-behind, with its dead `"StressTest"`/`"devices"` arms) with a **typed navigation service** (`INavigationService.NavigateTo<TViewModel>()`) and a view-locator registered in DI.
- Use CommunityToolkit's **`WeakReferenceMessenger`** for cross-VM events (crash detected, device selected, capture started) instead of direct references and code-behind reaching into VM internals (as the command palette does today).

### 6.5 Concurrency model
- Replace the ambiguous/absent ADB semaphore with an **explicit transport policy**: allow bounded concurrency for read commands (ADB server multiplexes fine), but **serialize state-changing operations** (`tcpip`, `pair`, `connect`) behind a dedicated lock. Document the actual behavior so code and comments agree. [BUG-02, TOOL-01/02]
- Make all `async` methods truly async (no blocking `File.ReadAllLines`, no `async` without `await`). Use `TimeProvider` for timers to make debounce/flush logic testable.

### 6.6 Lifecycle
- Implement a real **Dispose chain**: `MainViewModel.Dispose()` disposes all child VMs; each VM unsubscribes from the store/messenger and stops its timers. Adopt navigation lifecycle hooks (`OnNavigatedTo/From`) to start/stop expensive pollers (Vitals). [MISS-05, BUG-05, FEAT-35]
- Replace the window-recreating theme switch with **in-place `ResourceDictionary` swap** using `DynamicResource` everywhere (removes the `IsThemeSwitching` hack and the "theme kills sessions" regression). [NEW-02, NEW-05, NEW-06]

---

## 7. Performance & Optimization

| Area | Problem | Fix |
|---|---|---|
| Console/log text | `string +=` in loops → O(n²), UI jank | `StringBuilder` + throttled (100 ms) flush, or bind an `ObservableCollection`/virtualized list. [BUG-07/14, FEAT-31/32/42] |
| Large log files | `File.ReadAllLines`/`ReadAllText` → OOM on 50–100 MB+ logs | Stream with `StreamReader`; tail-read for preview; `Utf8JsonWriter` streaming for JSON export. [BUG-17, FEAT-29/30] |
| Live log collection | `Clear()` + `AddRange()` fires double `Reset`, multi-second freeze + selection loss | In-place `RemoveRange`; batch notifications; incremental filtering of only new entries. [FEAT-25, NEW-10] |
| Device detail fetch | Sequential getprop/dumpsys per device, worst-case tens of seconds | Batch into a single shell round-trip; parallelize where safe. [TOOL-16] (partially done per changelog — verify) |
| Polling | Sequential Android→iOS; blocks on slow USB | `Task.WhenAll` parallel poll; fire property-change updates (battery/auth) not just connect/disconnect. [TOOL-14/15] |
| Clipboard copy | Materializes entire filtered view (~20 MB string) | Cap to last N; warn on truncation. [FEAT-39] |
| Rendering | Ensure `VirtualizingStackPanel` + `Recycling` on all log/app/file lists; freeze brushes; enable UI virtualization for 100k+ rows | WPF virtualization + `Freezable.Freeze()` |
| Startup | Self-contained + R2R already; measure cold start | Add startup timing telemetry; lazy-init non-critical services via DI |

**Method:** add lightweight **BenchmarkDotNet** micro-benchmarks for the hot paths (log parsing, filtering, export) and a **repeatable perf smoke test** (capture 1M-line synthetic log) to catch regressions in CI.

---

## 8. Security, Privacy & Compliance

This tool tests **unreleased games**, so data minimization is a first-class requirement.

- **Command-injection hardening (device shell):** move from blocklists to strict **allowlists** for all path/package/URL arguments; validate iOS AFC paths (currently unvalidated); escape or reject macro `input text`; stop passing raw command strings through `ExecuteCommandAsync` without validation. [TOOL-18/19, FEAT-34, R3]
- **Log/PII redaction by default:** make `SecureMode` the default; centralize a `SanitizeForLog` that redacts serials, file paths, and deep-link query params before *any* sink writes them. [SEC-01, TOOL-13]
- **Bug-report minimization:** hash iOS serials (Android already hashed), drop full `dumpsys package` inventory, scope to the target package only. [FEAT-22/23, SEC-04]
- **Data lifecycle:** implement retention (delete sessions/screenshots/recordings older than N days on startup), and a real "Delete my data" / "Export my data" in Settings. [SEC-02/03, COMP-01/02/04]
- **At-rest protection:** offer optional encryption of session artifacts (DPAPI per-user) and owner-only ACLs on session directories. [SEC-13]
- **Wireless ADB safety:** confirm/warn before `adb tcpip` (opens the device to the LAN). [SEC-07]
- **Serial storage:** never key `settings.json` on raw serials; hash consistently (`SecurityHelper`, 16+ hex chars). [SEC-06/12]
- **Supply chain:** enable NuGet audit + Dependabot; sign releases; publish an SBOM.
- **Licensing:** keep pymobiledevice3 **process-isolated** (GPL-3.0); ship complete `THIRD_PARTY_NOTICES` incl. scrcpy Apache NOTICE; audit transitive NuGet licenses. [LEGAL-01..05]
- **Threat model doc:** add a short `SECURITY.md` describing the trust boundary (local user, USB-attached devices, spawned native tools) and a vulnerability-reporting process.

---

## 9. Testing, Quality & CI/CD

**Current:** xUnit tests exist for services/models/helpers, but **zero view-model coverage** (VMs were untestable pre-DI). CI builds + tests on Windows.

**Target:**
- **Unlock VM tests** via DI + interfaces (mock services with Moq/NSubstitute). Cover navigation, device selection, capture start/stop, export, and error branches.
- **Coverage gate:** wire `coverlet` output into CI with a ratcheting threshold (start where you are; forbid regressions). Publish coverage to the PR.
- **Golden-file/parser tests:** the log parsers, `getevent` macro parser, and pymd3 output parsers are fragile; add fixture-driven tests with real captured output samples (already partly present — expand).
- **Process/integration tests:** a fake "adb" shim (a small stub exe) to exercise `ToolLauncher`/`AdbService` behavior (timeouts, non-zero exit, stderr) deterministically without a real device.
- **Static analysis:** enable `TreatWarningsAsErrors` incrementally, `Nullable` is already on, add `.editorconfig` + Roslyn analyzers (`Microsoft.CodeAnalysis.NetAnalyzers`), and consider `dotnet format` + a formatting check in CI.
- **CI cleanup:** two overlapping workflows exist (`build-release.yml`, `dotnet-desktop.yml`). Consolidate; add analyzer/format/coverage/security-audit stages; cache NuGet; run tests with results published.
- **Release quality:** attach SBOM + checksums to releases; auto-generate changelog is already present.

---

## 10. UX / UI Modernization

- **Fluent theming:** WPF on .NET 10 supports a modern Fluent look; adopt consistent `DynamicResource`-driven light/dark themes so the shell *and* content respond to theme changes (today the shell stays dark due to hardcoded colors). ([WPF modernization 2026](https://platform.uno/articles/wpf-modernization-in-2026-a-source-backed-decision-guide/)) [NEW-05/06]
- **Consistent component library:** unify buttons, cards, inputs, combo boxes (dark-theme combo is currently broken) into a styled control set / resource dictionary.
- **Interaction safety:** bind `IsEnabled`/`CanExecute` so Start/Stop, Mirror/Stop, Uninstall, Delete, Push/Pull, Play/Stop only enable in valid states; add confirmations for destructive actions. [UX-01/02/04/10/11/12]
- **Feedback:** global busy/progress indicators for async ops; toast/status confirmations for saves; live status dots that reflect real connection state. [UX-03/09/14]
- **Empty states & onboarding:** helpful empty-state illustrations + a first-run guided setup (enable USB debugging, trust computer, driver check). [UX-17]
- **Command palette & shortcuts:** fix corrupted glyphs (use Segoe MDL2/Fluent icons), wire all shortcuts (Ctrl+1..n), and add arrow-key navigation. [UX-07/08]
- **Accessibility:** AutomationPeers/`AutomationProperties`, keyboard-only navigation, high-contrast theme, and screen-reader labels — currently unaddressed.
- **Layout:** responsive/resizable panels, dockable log viewer, and per-view density options for testers on small laptops.

---

## 11. Enhancements to Existing Features

- **Live log viewer:** debounced search, incremental filtering, regex + level + tag filters, sticky bookmarks, column layout, and a "follow/tail" toggle that survives batches without selection loss. [FEAT-01/03]
- **Sessions:** highlight the actively-capturing session; guard delete while capturing; add a session-restart separator; "copy raw file" export that copies the on-disk log rather than truncated in-memory data. [FEAT-02/04/05, FEAT-27]
- **Bug report:** minimized, structured (JSON + human summary), correct product branding, configurable sections, and attach a short device/app snapshot instead of full inventory. [FEAT-23/24]
- **Macros:** auto-detect the touchscreen input device (event node varies per OEM), fix text input on stock Android (avoid `base64`), record multi-touch, and compensate replay drift precisely. [FEAT-18/19/33/34]
- **Stress test (monkey):** periodic metric sampling (min/max/avg CPU/mem), richer time-series report, defensive monkey flags, percentage-sum validation, event-count clamping, and an explicit "not supported on iOS" guard. [FEAT-12/13/14, MISS-01/02/03]
- **Vitals:** GPU/jank (`gfxinfo`) metrics, start/stop on navigation, charts over time rather than instantaneous numbers. [FEAT-20, BUG-05]
- **iOS parity where the platform allows:** implement AFC pull, fix `afc ls` file/dir detection, directory delete via `rmdir`, and surface actionable "enable Developer Mode" guidance. [FEAT-10/11, TOOL-09/10/11/17]
- **Screen recording:** free-space check + duration/size warnings; store the remote path instead of glob-searching on stop. [FEAT-38, TOOL-20]

---

## 12. New Features (Industry-Aligned)

Modern device-QA tooling (device farms, Appium ecosystems, vendor consoles) sets expectations the tool can meet incrementally:

### 12.1 Multi-device & fleet
- **Multi-device dashboard**: capture from several devices at once, side-by-side log/vitals, and a fleet health view. (The service layer already keys captures per device.)
- **Device groups / labels** and saved device profiles (notes already exist per device).

### 12.2 Automation & integration
- **Appium / automation bridge:** launch and attach to Appium sessions; capture logs/video correlated to a test run; expose a local control API (named pipe / localhost HTTP) so CI or an automation harness can start/stop capture and pull artifacts.
- **Issue-tracker integrations:** one-click "file bug" to **Jira / Azure DevOps / GitHub Issues / TestRail** with the bug-report zip, redacted logs, screenshot, and device metadata attached.
- **CI hooks:** a headless/CLI mode (`QADeviceTool capture --serial ... --package ... --out ...`) so the same engine runs in pipelines, not just the GUI.

### 12.3 Evidence & analysis
- **Annotated screenshots** (draw/blur before attaching — blur is important for unreleased content).
- **Screen recording with markers/timestamps** synced to log events; auto-clip around detected crashes.
- **AI-assisted log triage:** local/offline-first crash clustering and "explain this stack trace / ANR" summaries; group duplicate crashes; suggest likely root cause. (There is already an "Analyze with AI" command hook to build on — ensure it is privacy-respecting and opt-in.)
- **Log analytics:** error-rate timelines, top exceptions, memory-leak trend detection, frame-drop hotspots.

### 12.4 Team & cloud (opt-in)
- **Shared session library** with retention + access control; upload redacted artifacts to a team store (S3/Azure Blob) with signed links.
- **Reporting:** export a polished HTML/PDF test-session report (timeline, crashes, vitals, evidence) for stakeholders.
- **Templates & checklists:** test-plan templates (the repo already has a `TEST_PLAN.md`) surfaced in-app as guided runs.

### 12.5 Reliability & self-service
- **Built-in dependency doctor:** verify adb/scrcpy/pymd3 versions, USB driver, developer mode, and offer one-click remediation.
- **Auto-update** for the app and bundled tools.
- **Crash reporting for the tool itself** (structured crash dump + optional opt-in telemetry). [ERR-03]

---

## 13. Cross-Platform Strategy

The single biggest structural limiter is **Windows-only**: full iOS support (screen recording, some diagnostics, developer shell) is constrained by the availability/quality of Windows iOS tooling. macOS is the natural home for iOS testing.

Options (2026 landscape):

| Path | Reach | XAML reuse | Effort | Notes |
|---|---|---|---|---|
| **Stay WPF + .NET 10** | Windows only | 100% | Low | Correct for Track A; modernize in place with Fluent theming. ([platform.uno guide](https://platform.uno/articles/wpf-modernization-in-2026-a-source-backed-decision-guide/)) |
| **Avalonia** | Win/macOS/Linux (+mobile/web experimental) | High (WPF-like XAML) | Medium-High | Renders its own UI via SkiaSharp; strong fit to escape Windows-only and enable a macOS build for real iOS testing. ([Avalonia vs MAUI](https://startdebugging.net/2026/05/maui-vs-avalonia-vs-uno-in-2026/)) |
| **Uno Platform** | Win/macOS/Linux/mobile/**web** | High (closest XAML) | High | Choose if browser reach matters. |
| **.NET MAUI** | Native iOS/Android + desktop | Low (not WPF XAML) | High | Best when you need first-party native mobile; weaker desktop story. |

**Recommendation:** Keep the WPF build for Track A. In parallel, invest in the **service/engine layer being UI-agnostic** (pure .NET, no WPF references) so that a future **Avalonia** front-end can target Windows *and macOS* with maximum reuse. A macOS build unlocks the iOS features that Windows tooling cannot provide. This is the single highest-leverage rework decision.

---

## 14. Full-Rework Target Architecture

```
+-------------------------------------------------------------+
|  Presentation (WPF today; Avalonia-ready)                   |
|   Views (XAML) + ViewModels (CommunityToolkit.Mvvm)         |
|   Navigation service · WeakReferenceMessenger · UiDispatcher|
+-------------------------------------------------------------+
|  Application layer (UI-agnostic, testable)                  |
|   Session orchestration · Export · Reporting · AI triage    |
|   DeviceStore (single source of truth) · Capabilities query |
+-------------------------------------------------------------+
|  Domain / Device Abstraction                                |
|   IDevice + capability interfaces:                          |
|   ILogSource · IScreenCapture · IScreenRecorder ·           |
|   IFileSystem · IAppManager · IInputInjector · IScreenMirror|
+-------------------------------------------------------------+
|  Platform backends (plugins)                                |
|   Android (adb, scrcpy)      iOS (pymobiledevice3, isolated)|
|   + future: cloud device providers, Appium bridge          |
+-------------------------------------------------------------+
|  Infrastructure                                             |
|   ToolLauncher/ToolResolver · ProcessManager ·              |
|   Preferences(Options) · Logging(MEL+sink) · Storage        |
+-------------------------------------------------------------+
|  Host: Microsoft.Extensions.Hosting + DI + Configuration    |
+-------------------------------------------------------------+
```

**Key principles:**
1. **UI-agnostic engine** — everything below Presentation has zero WPF references (enables Avalonia/macOS + a CLI/headless host).
2. **Capabilities over platform branches** — the UI asks "can this device record its screen?" not "is this iOS?".
3. **Single device store** — no duplicated device lists or event fan-out.
4. **Everything injectable** — no static singletons; testable by construction.
5. **Plugin backends** — Android/iOS/cloud providers register capabilities; adding a provider doesn't touch the UI.

---

## 15. Phased Roadmap

### Track A — Stabilize & Modernize In-Place

**Phase 0 — Identity & deadline (Week 1–2)**
- Retarget to **.NET 10 LTS**; verify build/tests/publish; add `win-arm64`. [R1]
- Unify branding & app-data folders (LogPro/QADeviceTool/QAQCDeviceTool → one), with a one-time data-migration. [R8]
- Consolidate CI workflows; add analyzers, format check, NuGet audit, coverage publish.

**Phase 1 — DI & lifecycle (Week 2–5)**
- Introduce `Microsoft.Extensions.Hosting`/DI; de-static core services; wire interfaces to all VMs. [R2]
- Implement the real Dispose chain + navigation lifecycle; in-place theme swap. [R6]
- Land the `IDeviceStore` + `IUiDispatcher`; remove per-VM `DevicesChanged` subscriptions. [R6]

**Phase 2 — Correctness, concurrency, security (Week 4–8, overlaps)**
- Fix concurrency model (explicit transport policy); make async truly async. [R4]
- Harden command construction (allowlists, iOS path validation, macro text). [R3]
- Default `SecureMode`; minimize bug reports; add retention + data export/delete. [R7]
- Burn down the P0/P1 items from `AUDIT-FINDINGS.md` (deadlocks, pair syntax, install/deep-link detection, O(n²), leaks).

**Phase 3 — Perf, UX, tests (Week 6–10, overlaps)**
- Streaming exports + tail reads + virtualization; StringBuilder/throttled output. [R5]
- UX safety bindings, busy indicators, theming, empty states, palette fixes.
- VM unit tests + parser golden files + adb-shim integration tests; coverage gate.

### Track B — Full Rework Toward a Platform

**Phase 4 — Engine extraction (Month 3–5)**
- Extract UI-agnostic Application/Domain/Infrastructure assemblies; define capability interfaces; move Android/iOS into plugin backends.
- Ship a **CLI/headless host** reusing the engine (enables CI capture).

**Phase 5 — Cross-platform & fleet (Month 5–8)**
- Prototype **Avalonia** front-end (Windows first, then **macOS** for real iOS support).
- Multi-device dashboard; device groups.

**Phase 6 — Integrations & intelligence (Month 8–12)**
- Appium bridge + local control API; Jira/ADO/TestRail/GitHub bug filing.
- AI-assisted crash triage/clustering; log analytics; HTML/PDF reporting; opt-in team cloud store.

---

## 16. Prioritization Matrix

| Initiative | Impact | Effort | Priority |
|---|---|---|---|
| Retarget to .NET 10 LTS | Critical (support deadline) | Low | **P0 — now** |
| DI container + de-static services | High (unblocks everything) | Medium | **P0** |
| Concurrency + command-injection hardening | High (reliability + security) | Medium | **P0/P1** |
| Perf: streaming/virtualization/StringBuilder | High (usability) | Medium | **P1** |
| Lifecycle/Dispose + DeviceStore | High (leaks, coupling) | Medium | **P1** |
| Branding/app-data unification | Medium (data integrity, polish) | Low | **P1** |
| Privacy: SecureMode default, retention, minimization | High (studio risk) | Low-Med | **P1** |
| UX safety + theming + a11y | Medium-High | Medium | **P1/P2** |
| VM tests + coverage gate + adb shim | High (regression safety) | Medium | **P1/P2** |
| Split God classes | Medium (maintainability) | Medium-High | **P2** |
| Engine extraction (UI-agnostic) | High (enables CLI + cross-platform) | High | **P2** |
| CLI/headless + Appium/CI hooks | High (automation reach) | High | **P2/P3** |
| Avalonia + macOS build (iOS parity) | High (market reach) | High | **P3** |
| AI triage, analytics, reporting, team cloud | Medium-High (differentiation) | High | **P3** |

---

## 17. Success Metrics / KPIs

- **Support posture:** 100% on a supported LTS runtime (.NET 10) before 2026-11-10.
- **Reliability:** zero UI freezes > 250 ms during 1M-line capture; no ghost adb polls after navigation/theme switch; crash-free session rate tracked via tool crash reports.
- **Testability:** view-model line coverage from ~0% → 60%+; overall coverage ratchet with no regressions in CI.
- **Security/privacy:** SecureMode default on; bug reports contain no raw serials or full package inventory; NuGet audit clean; releases signed with SBOM.
- **Performance:** log export memory bounded (streaming, flat regardless of file size); device-detail fetch p95 under a few seconds.
- **Maintainability:** no source file > ~400 LOC for VMs/services; single app-data location; single CI workflow.
- **Reach (Track B):** functional macOS build with expanded iOS capability parity; CLI capture usable in CI.

---

## 18. Risks, Assumptions & Open Questions

**Assumptions**
- The product should remain a first-class Windows desktop tool in the near term (Track A), with cross-platform as a strategic Track B bet.
- pymobiledevice3 stays **process-isolated** to preserve GPL-3.0 compliance.
- The existing `AUDIT-FINDINGS.md` bug registry remains the tactical source of truth; this blueprint sequences and frames those fixes rather than re-listing them.

**Risks**
- **Deadline compression:** the .NET 8 EOS window is short; Phase 0 must not be blocked behind larger refactors — retarget first, refactor second.
- **DI refactor blast radius:** the current `.continue-here.md` explicitly warns against adding DI casually. Do it as a dedicated phase with the full startup rewrite, behind good tests.
- **Native-tool drift:** adb/scrcpy/pymd3 CLI changes can silently break features; the capability/version-probe layer and integration shims mitigate this.
- **Cross-platform cost:** Avalonia migration is real effort; de-risk by extracting the UI-agnostic engine first (valuable even if the WPF UI is kept).

**Open questions for stakeholders**
1. Is **cross-platform (macOS) iOS support** a strategic goal, or is Windows-only acceptable long-term?
2. Is **team/cloud** functionality desired, or should the tool stay strictly local (which simplifies privacy posture)?
3. What issue trackers / automation frameworks are in use (drives §12.2 integration order)?
4. Is an **AI triage** feature acceptable given unreleased-content sensitivity (local-only vs. cloud)?
5. Preferred packaging for enterprise rollout: MSIX, Inno Setup, or both?

---

### Appendix A — Cross-reference to the tactical audit
The defect-level backing for the themes above lives in [`AUDIT-FINDINGS.md`](./AUDIT-FINDINGS.md) (categories: BUG, ERR, FEAT, SEC/COMP/LEGAL, UX, MISS, NEW, TOOL) and is scheduled in [`IMPLEMENTATION-PLAN.md`](./IMPLEMENTATION-PLAN.md) (Waves 0–11). This blueprint groups those ~162 items into the ten risk/theme areas (R1–R10) and sequences them within the Track A phases.

### Appendix B — Sources (current-industry, 2026)
- .NET support policy & .NET 8/9 end of support (2026-11-10): [Microsoft support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core), [.NET 8/9 EOS](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/)
- .NET 10 LTS (supported to Nov 2028): [Announcing .NET 10](https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/)
- WPF modernization vs. migration guidance: [platform.uno decision guide](https://platform.uno/articles/wpf-modernization-in-2026-a-source-backed-decision-guide/), [Avalonia "What is WPF" 2026](https://avaloniaui.net/blog/what-is-wpf)
- Cross-platform framework comparison: [Avalonia vs MAUI vs Uno (2026)](https://startdebugging.net/2026/05/maui-vs-avalonia-vs-uno-in-2026/)
- CommunityToolkit.Mvvm current line (8.4.x): [CommunityToolkit/dotnet](https://github.com/CommunityToolkit/dotnet)

*Content from external sources was rephrased/summarized for compliance with licensing restrictions.*
