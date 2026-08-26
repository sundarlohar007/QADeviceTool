# Remaining Work Plan — Track B & Track A Leftovers

> Audit source: `.planning/MODERNIZATION-AND-REWORK-BLUEPRINT-CONSOLIDATED.md` (sections cross-referenced).
> Audit date: 2026-08-14. Track A engineering is complete; this plan covers the gaps and all of Track B.

## PRIVACY HARD GATE (mandatory, supersedes any feature)

This tool processes **unannounced, unreleased game content**. The following are NON-NEGOTIABLE:

- **Zero outbound network calls** — no telemetry, no analytics, no crash upload, no cloud sync, no AI/LLM integration of any kind (local or cloud). Verified by CI grep: the only network surface is the loopback-only control API (`127.0.0.1`).
- **AI triage (§16 of the blueprint) is REMOVED from scope** per stakeholder decision — even an opt-in local model adds attack surface for pre-release data.
- **Redaction by default** (`SecureMode`) stays on; bug-report minimization and serial hashing remain mandatory.
- Any future feature that would touch the network must be re-approved explicitly by the owner.

*Violating this gate is a release blocker.*

## 0. Track A leftovers (close before Track B ships)

| # | Item | Blueprint ref | Testable headless? |
|---|---|---|---|
| A1 | `SECURITY.md` — trust boundary (local user, USB devices, spawned native tools) + vuln-reporting process | §10 | doc |
| A2 | Regex search filter in log viewer | §15 FEAT-01/03 | VM test |
| A3 | Safe macro `input text` (reject/escape `'`, backtick, `$(`; `%s` for spaces) | §15 FEAT-34 | parser test |
| A4 | Guard session delete while capturing + restart markers | §15 FEAT-05/21 | service test |
| A5 | Wire `RestrictDirectoryAccess` call sites (session dirs) | §10 SEC-13 | n/a |
| A6 | `.github/dependabot.yml` + `SECURITY.md` + CPM (`Directory.Packages.props`) | §6.1 | n/a |
| A7 | De-static `PreferencesService`/`AppLogger`/`ProcessManagerService`/`DialogService`/`ThemeService` | §8.1 | build |
| A8 | `IDialogService` replacing the 7 VM MessageBox confirm-gates | §6 | build |

Order: A1–A6 (low risk, this session) → A7/A8 ride along with the Avalonia port (services must be UI-free anyway).

## 1. Phase 5 — Engine-agnostic profiler (Android first) — blueprint P1

The core value proposition. Pure engine work — buildable and testable without any device (parsers are validated against synthetic `dumpsys` output; live path uses the fake-adb e2e harness).

**1.1 Parsers** (LogPro.Core, each with synthetic-output tests):
- `SurfaceFlingerLatencyParser` — `dumpsys SurfaceFlinger --latency <layer>` → FPS/frame-time (ground truth; works for SurfaceView games)
- `GfxInfoFrameStatsParser` — `gfxinfo framestats` → jank %, P90/P95 frame time (Android 12+ caveat)
- `CpuInfoParser` — `dumpsys cpuinfo` → per-package % + `/proc/<pid>/stat` delta
- `MemInfoParser` — `dumpsys meminfo <pkg>` → PSS/RSS/JAVA heap/native
- `ThermalParser` — `dumpsys thermalservice` → throttle temps/status
- `BatteryStatsParser` — `dumpsys batterystats` → drain rate, coulomb delta
- `NetStatsParser` — `dumpsys netstats` → per-uid rx/tx

**1.2 Sampling service** (`AndroidPerformanceProfiler`): background thread, ring buffer of snapshots, adaptive interval, throttled event emission — per §9.4 (never on UI thread). Emits `ProfilerSnapshot` (Timestamp, Fps, FrameTimeP90Ms, JankyFrames, CpuPercent, PssKb, ThermalStatus, BatteryPercent).

**1.3 Report writers**: streaming JSON (Utf8JsonWriter) + CSV; stability metrics (1%/0.1% lows, jank events, memory growth, drain rate); slow-session flags per §12.1 (frame > 50 ms casual / > 34 ms action).

**1.4 CLI**: `logpro-cli profile --serial X --seconds N [--package P] --out DIR` → `profile-report.json` + `profile.csv` + console summary. CI: fake-adb e2e (fake answers `SurfaceFlinger --latency`, `cpuinfo`, etc.).

**1.5 UI later**: Vitals view upgrades to live charts (LiveChartsCore) + HUD overlay (§12.1) — after the Avalonia shell lands (avoid double porting).

## 2. Phase 4b — Avalonia on Windows (blueprint P1/P2)

- New `LogPro.Avalonia` project: Avalonia 12.x, net10.0, compiled bindings, references `LogPro.Core` only.
- Port order: shell + sidebar + navigation → Dashboard → Sessions (log viewer w/ virtualization) → Settings → remaining views.
- Adapters: `AvaloniaUiDispatcher`, `IAvaloniaDialogService`, clipboard/file-dialog per-OS glue (§4.1).
- Theme: single token set (design tokens → light default, dark equal — §11.2).
- Gate: build + boot on Windows; WPF stays the shipping app until parity (§19).
- Then de-static (A7/A8) lands cleanly against the UI-free services.

## 3. Phase 6 — macOS + iOS DVT (blueprint P2)

Unlock order: macOS build of Avalonia app → pymobiledevice3 DVT capability probe (17.4+ userspace tunnel) → sysmon FPS/energy/network parsers (same pattern as 1.1) → iOS screen recording (DVT video stream) → screenshot on DVT path. macOS machine required for verification.

## 4. Phase 7 — Condition simulation, location, tier matrix, soak (§12.2–12.5)

Network conditioning (`tc`/proxy presets + iOS condition inducer), mock-GPS with mandatory reset, device-tier profiles + comparison reports, N-hour soak runs with memory-growth/FPS-decay flags, macro upgrades (multi-touch, gesture library, loop count — extends §15).

## 5. Phase 8 — Integrations & scale-out (§16)

Local control API (localhost HTTP / named pipe) → Appium bridge; **issue-tracker filing = export-only** (redacted evidence bundle + markdown template written to disk; the user attaches manually — the tool never touches the network); plugin manifest system; **AI triage REMOVED (privacy hard gate)**; team/cloud REMOVED; Linux build; retire WPF. Post-GA: decompiler add-on pack + Perfetto deep-dive + plugins marketplace.

## Execution order (now)

**DONE (Track A + Track B Windows-side):**
- Track A leftovers (A1-A6) ✅; PreferencesService de-static + ProcessManager de-static ✅
- Phase 5 profiler (parsers/service/report/CLI) ✅; Phase 1.5 profiler UI + HUD ✅
- Phase 4b Avalonia (all 11 views + palette + theme switching) ✅
- Phase 7 (soak, tier matrix, location, network conditioning) ✅
- Phase 8 non-network (control API, plugins, issue export) ✅; AI/cloud REMOVED (privacy hard gate)
- Benchmarks + KPI measurements ✅ (.planning/KPI-RESULTS.md)

**REMAINING:**
1. **Avalonia design-token sweep** — 209 hardcoded hex colors in Avalonia views → theme resources (light-theme surfaces). Windows-doable, 2-3 sessions.
2. **Mac Phase 6** — iOS DVT instrumentation (sysmon/FPS/energy/network, DVT screenshot + recording, capability probes), notarized .app. MacBook, 3-5 sessions.
3. **GA gate** — owner code-signing certs; fresh-OS install tests per platform; 60fps@100K UI measurement; installer ≤100MB via the Avalonia NativeAOT switchover (validated).
4. Optional/low: AppLogger de-static; WPF wizard/toasts/accessibility (fold into the Avalonia UX pass).

## KPI reminders (§19)

- Installer ≤100MB: needs NativeAOT + trimmed runtime (pymd3 54MB dominates; consider zip-level compression of the PyInstaller bundle).
- Measure cold start ≤2s, RAM parity, 60fps@100K log viewer once Avalonia lands.
- GA gate: fresh-OS install test per platform + code-signing (user-owned certs).
