# QADeviceTool / LogPro v4.0 — Migration Plan (Complete · Self-Contained Bundling)

> **Stack decision:** WPF (.NET 8) → **Tauri v2 + React 19 + TypeScript + Rust** [1][2]
> **Status:** Approved with amendments · **Revision:** Zero-Dependency Installer
> **Last updated:** June 2026

---

## 1. Executive Summary

Tauri is the recommended first choice for developer tools and internal utilities, winning specifically when small installers, low idle memory, and scoped native permissions are product requirements — all core goals of this migration [2]. The accepted trade-offs are **Rust ownership and OS-WebView QA**, both explicitly mitigated below [2].

This revision **narrows scope** to prevent over-engineering (the tool stays true to its identity as a fast, lightweight **device + log + performance** tool [1]) while adding a **zero-dependency installer goal**: users install once and run immediately, with no manual installation of ADB, scrcpy, Python, Java, or WebView runtimes.

---

## 2. Non-Goals for v4.0 (Anti-Scope-Creep Guardrails)

To keep the tool lean, v4.0 will **NOT** include:

- ❌ **AI-assisted log summarization** — deferred to post-GA [2]
- ❌ **Plugin marketplace UI** — ship the plugin *system* first [2]
- ❌ **Full cloud sync infrastructure** — limited to *linking out* to device farms only
- ❌ **AR/VR sensor logging** — moved to a Future Ideas backlog
- ❌ **Time-scrubbing session replay** — replaced by a static side-by-side diff [2]
- ❌ **APK/AAB/IPA recompile or re-signing** — decompile-only in v1

---

## 3. MVP Definition (Minimum Shippable v4.0)

A releasable v4.0 = **WPF feature parity**, nothing more:

- ✅ Auto-detect Android/iOS devices
- ✅ Real-time virtualized log capture + viewer [1]
- ✅ App-specific filtered logging (PID tracking) [1]
- ✅ Session management [1]
- ✅ Screen mirroring + screenshot capture
- ✅ Bug report generation (.zip export) [1]
- ✅ Settings/preferences [1]

Everything beyond this list is **additive** and must not block GA.

---

## 4. Framework Decision — Research-Validated

| Framework | Verdict for This Project |
|---|---|
| **Tauri v2 ✅** | Best fit: tiny footprint, permission scoping, first-class sidecars, Rust async process management [1][2] |
| Electron | Best for mature Chromium products; largest footprint; overkill here |
| Qt | Strong for enterprise device support, but C++/QML raises team ramp-up cost |
| .NET MAUI / Avalonia | Lowest-risk .NET path, but loses footprint + sidecar advantages |
| Flutter | Best when one Dart UI spans mobile + desktop; not the priority here |

**Accepted trade-offs:** (1) team owns the Rust/native seam; (2) OS WebView rendering differs per platform (WebView2 / WKWebView / WebKitGTK) — requires cross-platform QA from day one [2].

---

## 5. Updated Technology Stack

### 5.1 Frontend

| Technology | Purpose | Status |
|---|---|---|
| React 19 + TypeScript 5.x | UI framework | Unchanged [2] |
| Vite 6 | Build tool | Unchanged [2] |
| Zustand | UI state | Unchanged [2] |
| **TanStack Query** | Async command results (device info, sessions) | 🆕 Added — prevents "everything in one store" anti-pattern [2] |
| @tanstack/react-virtual | Log virtualization (100K+ rows @ 60fps) | Unchanged [2] |
| Tailwind CSS v4 | Styling | Unchanged [2] |
| shadcn/ui | Component library | ⚠️ Amended — see §5.2 [2] |
| Radix UI primitives | Dialogs, selects | ⚠️ Amended — see §5.2 [2] |
| Framer Motion, Sonner, cmdk | Animations, toasts, command palette | Unchanged [2] |

### 5.2 shadcn/ui / Radix UI Risk (Amended)

The original Radix UI team has shifted focus to Base UI, raising long-term stability questions for shadcn/ui-based projects [2]. **Mitigations:** (1) vendor components — you own the code, insulating you from upstream abandonment [2]; (2) keep primitives behind our own `ui/` wrappers so a swap to **Base UI or React Aria** touches one folder, not fifty files [2]; (3) a small, timeboxed design-token layer (CSS variables only) to avoid a generic look without delaying GA.

### 5.3 Backend (Rust)

| Technology | Purpose | Status |
|---|---|---|
| Tauri v2, Tokio, Serde, tokio::process | Core shell, async, serialization | Unchanged [2] |
| tauri-plugin-shell / -fs / -dialog / -notification / -updater | Sidecars, FS, dialogs, notifications, updates | Unchanged [2] |
| rusqlite | Session DB + search index | Amended — schema versioning via `PRAGMA user_version` from day one [2] |
| **thiserror + anyhow** | Typed / command-boundary errors | 🆕 Added [2] |
| **tracing + tracing-subscriber** | Structured logging | 🆕 Added [2] |
| **DashMap** | Per-device semaphore registry | 🆕 Added — see §6.2 [2] |

---

## 6. Architecture — Critical Fixes

### 6.1 IPC Backpressure & Event Batching
A ring buffer in Rust is the source of truth; batched events emit every ~16ms (one frame) or every 500 lines, whichever first; the frontend virtualizes history (pull via command) + renders only the live tail (push via events) [2].

### 6.2 Per-Device Semaphores
Replaces the WPF `SemaphoreSlim(1,1)` bottleneck [1] with per-device locking [2]:

```rust
struct AdbManager {
    device_locks: DashMap<String, Arc<Semaphore>>, // 1 permit per device
    global_limit: Semaphore,                        // cap total ADB processes (~8)
}
```
→ Parallel **across** devices, serialized **per** device [2].

### 6.3 Plugin/Extension System
Solves Critical Issue #8 (new device types/log formats required code changes) [1]: manifest-based plugins (TOML/JSON) for new device types, log parsers, and custom commands; WASM-sandboxed parsers (wasmtime/extism) for third-party log formats [2]. **Note:** the *system* ships; the *marketplace* does not (§2).

### 6.4 Legacy Data Migration
One-time `migrate_legacy_settings` command imports existing WPF configs and sessions [2].

---

## 7. New Features for v4.0 (Trimmed per Audit)

| Feature | Description | Priority | Change |
|---|---|---|---|
| Command palette (cmdk) | Cmd+K access to every action | P0 | Kept [2] |
| Multi-device parallel logging | Enabled by §6.2 | P0 | Kept [2] |
| Screen mirroring/recording | scrcpy sidecar integration | P1 | Kept [2] |
| Saved filter profiles | Named regex/tag presets, shareable | P1 | Kept [2] |
| Crash reporting (Sentry) | A QA tool must report its own crashes | P0 | Kept [2] |
| **Log session diff** | Static side-by-side comparison | P1 | ✂️ Simplified from "replay & diff" [2] |
| ~~Plugin marketplace~~ | — | — | 🗑️ Deferred (§2) [2] |
| ~~AI log summarization~~ | — | — | 🗑️ Deferred (§2) [2] |

### 7.1 Low-Cost, High-Value Additions (from Audit)
- **Log export** — plain text / CSV / JSON of filtered logs [1][2]
- **Cross-session global search** — expose the rusqlite search index across all sessions [2]
- **Sidecar health indicator** — live status of `adb`, `scrcpy`, `pymobiledevice3` (addresses fragile WPF process management) [2]

---

## 8. Game & App Tester Feature Set (Trimmed)

Extends LogPro into mobile QA / game testing on the existing Rust process-streaming pipeline [2].

| Priority | Feature |
|---|---|
| **P0** | Performance overlay — FPS, memory, CPU/GPU, battery, thermals |
| **P0** | Device matrix + session tagging |
| **P1** | Smoke/regression hooks (Appium, AltTester, Drizz) |
| **P1** | Screenshot diff across builds |
| **P2** | Localization + security/network capture |
| **P2** | Device-farm integration (link-out only) |
| ~~P3~~ | ~~AR/VR sensor logging~~ (cut to backlog) |

---

## 9. One-Click App Package Decompilation

> Extends LogPro's sidecar model — the same mechanism that bundles ADB/scrcpy/pymobiledevice3 [1] — to reverse-engineer app packages for QA, security, and debugging.

### 9.1 Supported Inputs (Phased)
- **APK** — full support (JADX + Apktool)
- **AAB** — convert to APK(s) via bundletool, then decompile *(Phase 5)*
- **IPA** — separate iOS toolchain *(later, research pending)*

### 9.2 Bundled Decompiler Sidecars
| Sidecar | Role |
|---|---|
| **JADX** (CLI) | DEX → Java source; service-style one-click use |
| **Apktool** | Full resource + manifest extraction (`--force-all`, `--no-res`) |
| **dex2jar + JD-GUI** | Optional alternate Java-source path |
| **strings** | Quick binary string extraction |

*Requires Java 17+ (64-bit) for JADX — bundled via trimmed JRE (§10.5); surfaced in the sidecar health indicator (§7.1).*

### 9.3 One-Click Workflow
1. User drops or selects an `.apk` (later `.aab` / `.ipa`)
2. Rust `DecompileManager` spawns JADX via `tokio::process`: `jadx -d <out> <apk>`
3. Runs Apktool in parallel for complete resources
4. Progress streamed to UI via Tauri events (reuses the log-streaming pipeline)
5. Output opened in an IDE-style file explorer for browsing source + resources

### 9.4 Rust Backend Additions
- `commands/decompile.rs` — Tauri command handlers
- `services/decompile_manager.rs` — orchestrates JADX/Apktool sidecars
- Capability scope: shell access limited to the new decompiler binaries only [1]

### 9.5 Usage/Legal & Non-Goals
- In-UI consent notice so testers only decompile packages they are authorized to inspect
- ❌ No recompile/re-sign in v1; ❌ no IPA support until the toolchain is validated

---

## 10. 🆕 Self-Contained (Zero-Dependency) Bundling Strategy

## 10. 🆕 Self-Contained (Zero-Dependency) Bundling Strategy

### 10.1 Goal
Everything required to run LogPro flawlessly ships **inside the installer** — no prerequisites, no PATH configuration, no separate downloads. Users install once and run immediately.

### 10.2 What Must Be Bundled

| Dependency | Purpose | Bundling Method |
|---|---|---|
| **ADB (platform-tools)** | Android detection, logcat, screenshots [1] | Sidecar via Tauri first-class `externalBin` support [1] |
| **scrcpy + deps** | Screen mirroring [1] | Sidecar + `scrcpy-server` + native libs |
| **pymobiledevice3** | iOS support [1] | Existing PyInstaller binary as sidecar (Python already embedded) [1] |
| **JADX** | APK decompilation | Sidecar |
| **Apktool** | APK resource extraction | Sidecar |
| **Java Runtime (JRE 17+)** | Required by JADX/Apktool | Trimmed JRE via `jlink` bundled in app resources |
| **WebView runtime** | Renders the React UI | Per-platform (see §10.4) |
| **App binary + assets** | LogPro itself | Tauri bundler output |

### 10.3 Sidecar & Resource Layout
- Executables declared under `bundle.externalBin` (leverages Tauri's first-class sidecar support [1])
- Non-executable resources (JRE, scrcpy-server, config templates) under `bundle.resources`
- Rust `PlatformResolver` trait resolves the correct bundled binary path per OS (already planned to abstract macOS/Linux ADB differences [1])

### 10.4 WebView Handling (Per Platform)
Since OS WebView rendering differs per platform (WebView2 / WKWebView / WebKitGTK) [2]:

| OS | WebView | Bundling Approach |
|---|---|---|
| **Windows** | WebView2 | Embed fixed-version WebView2 or Evergreen bootstrapper in the installer |
| **macOS** | WKWebView | Ships with the OS — no bundling needed |
| **Linux** | WebKitGTK | Ship an **AppImage** bundling WebKitGTK for a truly self-contained build |

### 10.5 Trimmed Java Runtime
- Use `jlink` to build a minimal JRE with only the modules JADX/Apktool need (~40–50MB vs ~200MB)
- Bundle per-platform JRE builds
- The sidecar health indicator validates the bundled JRE at startup instead of probing the system [1][2]

### 10.6 Updated Installer Targets

| Platform | Format | Self-Contained Mechanism |
|---|---|---|
| **Windows** | `.msi` + `.exe` [1] | All sidecars + JRE + WebView2 bootstrapper bundled |
| **macOS** | `.dmg` / `.app` | All sidecars + JRE inside the `.app` bundle |
| **Linux** | **AppImage** (primary) + `.deb`/`.rpm` | AppImage bundles WebKitGTK + JRE + sidecars |

---

## 11. Phased Implementation Plan (Consolidated)

| Phase | Weeks | Scope | Exit Criteria |
|---|---|---|---|
| **0 — Foundation** | 1–2 | Tauri scaffold, CI matrix (Win/mac/Linux), updater signing keys, code-signing certs [2] | Signed installer on all 3 OSes [2] |
| **1 — Core Value** | 3–5 | ADB detection + virtualized log viewer w/ batching (§6.1, §6.2); log export + cross-session search (§7.1) [2] | Alpha runs alongside WPF; 100K lines @ 60fps [2] |
| **2 — Feature Parity (MVP)** | 6–8 | Sessions, iOS (pymobiledevice3), screenshots, mirroring, settings + legacy migration (§6.4) [2] | **MVP shipped** — all WPF features ported [2] |
| **3 — Differentiators** | 9–11 | Performance overlay + device matrix (§8, promoted), design tokens, crash reporting [2] | GA release [2] |
| **4 — Extensibility & Decompilation** | 12–15 | Plugin *system* (§6.3), automation hooks, screenshot diff, APK decompilation (§9) [2] | Post-GA capability release [2] |
| **5 — Zero-Dependency Bundling & AAB** | 16–18 | Bundle all sidecars + trimmed JRE (§10.5) + WebView2 bootstrapper (§10.4); AAB → APK via bundletool | Installs + runs flawlessly on **fresh** Win/mac/Linux VMs with no prerequisites |

> **Numbering note:** This supersedes all prior phase schemes (including the original plan's Phase 1–7 references, e.g. Build & Distribution [1]). This is the single authoritative phase plan.

> **Clean-machine verification:** Phase 5 requires testing every installer on freshly provisioned VMs (no Java, no ADB, no WebView2, no dev tools) to prove zero-dependency operation.

---

## 12. Success Metrics

| Metric | WPF (Current) | Tauri v2 (New) | Notes |
|---|---|---|---|
| **Installer size** | ~150MB [1] | **~80–120MB** (self-contained) | ⚠️ Revised up from ~20MB [1][2] due to bundled JRE + sidecars |
| **Cold start time** | ~3–4s [1] | ~0.5–1s [2] | **3–4x faster** [1][2] |
| **RAM at idle** | ~200MB [1] | ~50MB [2] | **75% less** [1][2] |
| **Log viewer (50K–100K lines)** | Laggy, ~2fps [1] | Smooth, 60fps [2] | Virtualized rendering [1][2] |
| **ADB command latency** | Serialized bottleneck [1] | Async smart queuing | Parallel where safe [1] |
| **Cross-platform** | Windows only [1] | Win + macOS + Linux [2] | 3x platform reach [1][2] |
| **User setup steps** | Install .NET + tools | **Single installer, zero prerequisites** | 🆕 Primary goal of this revision |

> **Size trade-off:** The original plan targeted a ~20MB installer [1][2]. Full self-contained bundling (especially the JRE for JADX/Apktool) raises this to ~80–120MB — still smaller than the current ~150MB WPF installer [1], while delivering a true zero-dependency experience.

### 12.1 New Capability Metrics (v4.0)

| Capability | Before | After |
|---|---|---|
| **Performance profiling** | None | Real-time FPS / memory / CPU / GPU / battery [2] |
| **Device coverage** | Ad-hoc | Structured device matrix [2] |
| **Test automation** | Manual | Appium / AltTester / Drizz hooks [2] |
| **Sidecar reliability** | Fragile `Process.Start()` [1] | Live health indicator (adb/scrcpy/pymobiledevice3) [1] |
| **Log search** | Per-session only [1] | Cross-session global search (rusqlite index) [1] |
| **Package analysis** | None | One-click APK/AAB decompilation |
| **Dependency footprint** | Manual tool installs | Fully bundled, zero prerequisites |

### 12.2 Acceptance Thresholds (GA Gate)

- ✅ Installs **and runs** on a **fresh** OS image with **no** manually installed dependencies (Win/mac/Linux)
- ✅ ADB, scrcpy, iOS support, and APK decompilation all function using **bundled** binaries only
- ✅ Cold start **≤ 1s**, idle RAM **≤ 60MB** [1][2]
- ✅ Log viewer sustains **60fps at 100K lines** [2]
- ✅ Installer + all bundled executables are **code-signed** [2]
- ✅ **100% WPF feature parity** before GA (§3 MVP) [2]

---

## 13. Risk Register (Complete)

| Risk | Likelihood | Mitigation |
|---|---|---|
| **Radix UI unmaintained** | Medium | Vendored components (you own the code); Base UI/React Aria migration path [2] |
| **WebView rendering differences across OSes** | High | Cross-platform CI from week 1; documented WebView baseline [2][1] |
| **No Rust experience on team** | Medium | 2–4 wk ramp-up (tokio model transfers from C#) [2] |
| **Generic-looking UI** | Medium | Timeboxed design-token layer [2] |
| **IPC flooding at high log volume** | High | §6.1 batching architecture [2] |
| **Scope creep / over-engineering** | High | §2 Non-Goals + §3 MVP guardrails [2] |
| **Installer size grows large** (JRE + sidecars) | High | Trimmed `jlink` JRE (§10.5); compress installer; revised budget (§12) |
| **WebView2 absent on old Windows** | Medium | Embed fixed-version WebView2 or Evergreen bootstrapper (§10.4) |
| **WebKitGTK missing on Linux** | Medium | Ship AppImage bundling WebKitGTK (§10.4) |
| **Bundled binary licensing** (JRE, scrcpy, JADX) | Medium | Use OpenJDK-based JRE (GPL+CE); document all bundled tool licenses |
| **Bundled binaries flagged by antivirus** | Medium | Code-sign the installer and all bundled executables [2] |
| **macOS/Linux ADB differences** | Medium | Abstract platform specifics behind `PlatformResolver` trait [1] |
| **Updater key compromise** | Low | Private keys in CI secrets only; decided in Phase 0 [2] |

---

## 14. Summary & Changelog

### 14.1 Summary
LogPro v4.0 migrates from WPF/.NET 8 to **Tauri v2 + React 19 + TypeScript + Rust** [1][2], delivering a faster startup, 75% lower idle memory, virtualized 60fps logging, and true cross-platform reach [1][2]. This revision keeps the tool lean via explicit **Non-Goals** (§2) and a strict **MVP** definition (§3), adds high-value differentiators — a real-time **performance overlay**, **device matrix**, **automation hooks**, and **one-click APK/AAB decompilation** — and now guarantees a **zero-dependency installer** where every runtime (ADB, scrcpy, pymobiledevice3, JADX/Apktool, JRE, WebView) is bundled [1].

The stack is justified because Rust's `tokio` is the gold standard for async process management, Tauri has first-class sidecar support for bundling ADB/scrcpy/pymobiledevice3, React + Tailwind + shadcn/ui enables a world-class customizable UI, and capability-based permissions remove arbitrary shell access [1].

**Guiding principle:** ship a fast, focused device + log + performance tool first; layer extensibility (plugin system, automation, decompilation) only after MVP parity is proven [1][2].

### 14.2 Changelog vs. Original Plan
1. ⚠️ **Radix UI maintenance risk** added with mitigations (vendored components; Base UI/React Aria migration path) [2]
2. ➕ **TanStack Query, thiserror/anyhow, tracing, DashMap** added to the stack [2]
3. 🔧 **Architecture fixes** — per-device semaphores, IPC batching, plugin system, legacy migration [2]
4. 🗓️ **Consolidated phased rollout** replacing big-bang rewrite [2]
5. ✨ **Game QA suite** — performance overlay, device matrix, automation hooks (Appium/AltTester/Drizz) [2]
6. 🔍 **One-click APK/AAB decompilation** (§9) added as a new capability
7. 🆕 **Self-contained bundling strategy** (§10) — ADB, scrcpy, pymobiledevice3, JADX, Apktool, trimmed JRE, WebView2 bootstrapper all bundled
8. ⚠️ **Revised installer size budget** ~20MB → ~80–120MB (§12) — trade-off for zero-dependency UX
9. ✂️ **Scope trims** — plugin marketplace and AI summarization deferred; session replay simplified to diff; AR/VR cut to backlog [2]
10. 📋 **Non-Goals + MVP guardrails** to prevent over-engineering; full risk register (§13)

---

## 15. Appendix — Original Plan Task Reference (Preserved)

The following detailed task breakdown from the original migration plan is preserved and mapped into the consolidated phases above [1]:

- **Foundation** — Initialize Tauri v2 + React + TypeScript; set up structure, Tailwind, shadcn/ui; shell layout (Sidebar, Header, StatusBar); theme system (dark/light); Zustand stores with TypeScript interfaces; Tauri command stubs in Rust [1]
- **Log Capture Engine** — Rust `LogStreamer` spawning `adb logcat`, parsing, streaming via events; app-specific PID tracking; `LogViewer` React component with `@tanstack/react-virtual`; filtering (level, tag, keyword, regex); export (CSV, JSON, plaintext) [1]
- **Session Management** — Port `SessionManager` to Rust with SQLite metadata; session CRUD UI; auto-capture toggle; bug report generation (.zip export); session history and search [1]
- **Screen Mirroring & Screenshots** — Bundle `scrcpy` as Tauri sidecar; screenshot capture via ADB; viewer with zoom/pan; session screenshot gallery [1]
- **Settings & Polish** — Settings dialog; command palette (Cmd+K); keyboard shortcuts; onboarding flow; error handling/recovery UI; performance profiling and optimization [1]
- **Build & Distribution** — Configure Tauri bundler for Windows (.msi, .exe); auto-updater; installer with sidecar binaries; CI/CD (GitHub Actions); documentation/README; beta testing and bug fixes [1]

---

## 16. Decision Confirmation

The recommended stack is **Tauri v2 + React + TypeScript + Rust** for these reasons [1]:
1. **Process management** — Rust's `tokio` is the gold standard for async process management [1]
2. **Sidecar support** — Tauri has first-class support for bundling ADB, scrcpy, pymobiledevice3 (and now JADX/Apktool + JRE) [1]
3. **UI freedom** — React + Tailwind + shadcn/ui enables a world-class, highly customizable UI [1]
4. **Tiny runtime footprint** — 75% less RAM (installer now larger due to self-contained bundling — see §12) [1]
5. **Cross-platform** — Windows + macOS + Linux from one codebase [1]
6. **Security** — Capability-based permissions, no arbitrary shell access [1]
7. **Auto-updates** — Built into the framework [1]

*This document is the single authoritative migration plan and supersedes all prior revisions.*