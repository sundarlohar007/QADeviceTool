# QADeviceTool Code Audit Findings

This document contains a comprehensive, line-by-line code audit of the QADeviceTool project. The audit was conducted in 5-10% slices to ensure high-quality review.

> **Original Audit Date:** 2026-05-13
> **Post-UI-Rework Re-Audit Date:** 2026-05-17
> **Total Source Files Audited:** 79 (.cs + .xaml)
> **Total Findings:** ~115 across 10 categories

---

## Slice 1: Core Foundation Services (10%)
*Files Audited: `AppLogger.cs`, `CrashDetector.cs`, `DependencyChecker.cs`, `PreferencesService.cs`, `ProcessManagerService.cs`*

### Bugs Found:
1. **Log Cleanup File Extension Mismatch (`PreferencesService.cs` line 141):**
   - NLog generates log files as `.txt` (configured as `app-log-${shortdate}.txt` in AppLogger.cs), but `CleanupOldLogs()` searches for `"*.log"`. Old logs are **never cleaned up** and accumulate indefinitely.
   - **Fix:** Change glob to `"*.txt"` or add both `"*.log"` and `"*.txt"`.

2. **CrashDetector `_detectedCrashes` HashSet Not Thread-Safe (`CrashDetector.cs`):**
   - `_detectedCrashes` is a plain `HashSet<string>` accessed from the ADB logcat output callback (background thread) and checked from the UI thread. No synchronization.
   - **Fix:** Replace with `ConcurrentDictionary<string, byte>` or add `lock`.

3. **DependencyChecker Silent Failures (`DependencyChecker.cs`):**
   - When `ToolResolver.FindTool()` throws or returns null for ADB/scrcpy/pymobiledevice3, the catch block swallows the exception. The user sees tools as "Not Found" but the **reason** (PATH issue, permissions, etc.) is never logged.
   - **Fix:** Add `AppLogger.Log.Warn(ex, ...)` inside the catch.

4. **PreferencesService.Save() File Write Race:**
   - Uses atomic write pattern (`.tmp` → `Move`) which is good. However, `Load()` is called in the static constructor, and if two threads access `PreferencesService` simultaneously during startup, `Current` could be read in a partially-initialized state.
   - **Fix:** Add `lock` around `Load()` and `Save()`.

### Suggestions:
1. **Namespace Mismatch:** All files use `namespace LogPro.Services` but the project is `QADeviceTool.App`. This is a legacy naming issue throughout the entire codebase.
2. **NLog Config Review:** `AppLogger.cs` configures NLog with a 7-day retention (`maxArchiveDays: 7`), 10MB file limit. Consider making retention configurable (already exposed via `PreferencesService.Current.LogRetentionDays` but not wired to NLog config).

---

## Slice 2-3: Device & Session Services (20%)
*Files Audited: `AdbService.cs`, `IosService.cs`, `ScrcpyService.cs`, `SessionService.cs`, `LogAnalyzerService.cs`, `MacroService.cs`, `DeviceMonitorService.cs`*

### Bugs Found:
1. **ADB Semaphore Misuse (`AdbService.cs`):**
   - `_adbSemaphore = new SemaphoreSlim(1, 1)` is acquired in `RunAdbCommandAsync` but NOT in `StartAdbLongRunning`. Long-running commands (logcat, monkey) bypass the semaphore entirely, so the semaphore provides false safety — short commands can still execute while a long-running process holds the ADB connection.
   - **Fix:** Either remove the semaphore (ADB server handles multiplexing) or apply it consistently to ALL commands including long-running ones. Recommendation: Remove it — ADB server is designed for concurrent access.

2. **ADB Pair Command Wrong Syntax (`AdbService.cs` line ~753):**
   - Uses `adb pair {ip}:{port} --code {code}` but ADB pair syntax is `adb pair {ip}:{port} {code}` — the `--code` flag doesn't exist. Pairing will ALWAYS fail.
   - **Fix:** Change to `$"pair {ip}:{port} {code}"`.

3. **SessionService Flush Timer Race (`SessionService.cs`):**
   - `_flushTimer` fires every 200ms to write buffered log lines to disk. `StopCapture()` disposes the timer then calls `FlushBuffer()`. But if the timer callback is already executing when `StopCapture()` is called, the buffer could be written to after the stream is closed, causing `ObjectDisposedException`.
   - **Fix:** Use `lock` around buffer access. In `StopCapture`, acquire the lock, dispose timer, flush, close stream — all under the same lock.

4. **SessionService.StopCapture Double-Call Race:**
   - If `StopCapture` is called twice rapidly (e.g., user double-clicks Stop button), the second call may try to dispose already-disposed resources. No guard against re-entrance.
   - **Fix:** Add `if (_isCapturing == false) return;` guard at the top.

5. **SessionService Regex Injection via PID (`SessionService.cs`):**
   - PID from `adb shell pidof` output is used directly in a regex pattern without sanitization. If the PID string contains regex metacharacters (unlikely but possible with malformed output), the regex could throw or match incorrectly.
   - **Fix:** Use `Regex.Escape(pid)`.

6. **DeviceMonitorService Suppresses Initial Broadcast:**
   - On first poll, the service compares against an empty `_previousDevices` set. All connected devices appear as "new" but the initial `DevicesChanged` event may be suppressed if the comparison logic has an off-by-one.
   - **Fix:** Ensure the first poll always fires `DevicesChanged`.

7. **DeviceMonitorService Premature Device Removal:**
   - A single failed ADB poll (e.g., USB hiccup, ADB server restart) causes all devices to be marked as disconnected. No debounce or retry logic.
   - **Fix:** Add a miss counter per device. Only remove after 2-3 consecutive misses.

8. **MacroService Per-Event ADB Execution:**
   - `ReplayMacroAsync` executes a separate `adb shell sendevent` command for **every single recorded touch event**. ADB has 30-50ms overhead per command on Windows. A fast swipe with 100 events will take 3-5 seconds instead of being instant.
   - **Fix:** Batch all sendevent commands into a single shell script, push it to the device, and execute it once.

9. **IosService `_ipcLock` Inconsistently Applied:**
   - `_ipcLock` (SemaphoreSlim) is acquired in `RunAsync` but NOT in `StartLongRunning` or `StartLong`. Long-running iOS commands bypass the lock entirely.
   - **Fix:** Either apply the lock to all methods or remove it (pymobiledevice3 processes are independent).

10. **Hardcoded Pairing Ports (`AdbService.cs`):**
    - `DiscoverPairingPortsAsync` scans a hardcoded range. If Android changes default pairing port ranges, this breaks silently.
    - **Fix:** Return empty list from the discovery method and let the user enter the port manually (which is already supported).

---

## Slice 4: Core ViewModels (40%)
*Files Audited: `MainViewModel.cs`, `ShellViewModel.cs`, `DeviceViewModel.cs`, `DashboardViewModel.cs`, `SettingsViewModel.cs`*

### Bugs Found:
1. **ShellViewModel O(n²) String Concatenation (`ShellViewModel.cs`):**
   - Every ADB shell output line does `ShellOutput += newLine + "\n"`. In C#, string concatenation creates a new string object each time. After 10,000 lines, each append copies the entire accumulated string. This causes exponential memory allocation and severe UI freezing.
   - **Fix:** Use `StringBuilder` and periodically copy to the bound `ShellOutput` property (e.g., every 100ms via dispatcher timer), or use an `ObservableCollection<string>` bound to a `ListBox`.

2. **VitalsViewModel Polling Never Stops on Navigation (`VitalsViewModel.cs`):**
   - `_pollTimer` triggers `dumpsys meminfo` and `top` over ADB every 3 seconds. It only stops if the device is disconnected or explicitly deselected. It does **not** stop when the user navigates away from the Vitals view. This creates constant background ADB traffic.
   - **Fix:** Implement `IDisposable` or add `OnNavigatedFrom()` lifecycle method to stop the timer when leaving the Vitals view.

3. **MacroViewModel Saves to Legacy Path (`MacroViewModel.cs`):**
   - Macros are saved to `%LocalAppData%/LogPro/Macros` instead of `QAQCDeviceTool/Macros`.
   - **Fix:** Change the path to use `QAQCDeviceTool`.

### Suggestions:
1. **Navigation is Manual String Mapping:** `MainViewModel.Navigate()` uses a string-to-ViewModel switch. Adding new views requires manual switch case additions. Consider a dictionary or attribute-based routing.
2. **DialogService should be Interface-Based:** Currently `DialogService` is static. ViewModels calling `DialogService.Confirm()` are untestable. Recommend `IDialogService` with DI.

---

## Slice 5: Secondary ViewModels (50%)
*Files Audited: `AppManagementViewModel.cs`, `FileExplorerViewModel.cs`, `SessionViewModel.cs`, `MacroViewModel.cs`, `StressTestViewModel.cs`, `VitalsViewModel.cs`, `DeepLinkViewModel.cs`*

### Bugs Found:
1. **AppManagementViewModel O(n²) String Concatenation (`AppManagementViewModel.cs`):**
   - Same pattern as ShellViewModel — `InstallOutput += line + "\n"` in a loop. During APK install, ADB can output hundreds of progress lines, causing the same exponential memory issue.
   - **Fix:** Use `StringBuilder` with periodic UI update.

2. **StressTestViewModel Output Truncation Allocation (`StressTestViewModel.cs`):**
   - `StressOutput` uses the same `+=` pattern. Additionally, monkey output can be thousands of lines, and the output is truncated by checking `StressOutput.Length > maxLength` — but by then the large string has already been allocated.
   - **Fix:** Use `StringBuilder` with a max capacity. Truncate the `StringBuilder` before converting to string.

3. **SessionService.ReadLogContentAsync Loads Full File (`SessionService.cs`):**
   - `File.ReadAllTextAsync()` loads the entire log file into memory. Session log files can be 50-100MB+ for long test runs. This will cause `OutOfMemoryException` on 32-bit builds.
   - **Fix:** Use `StreamReader` with tail-read (last N lines) for the preview, and full-file-read only for export.

---

## Slice 6: Views & UI Code (60%)
*Files Audited: `SessionView.xaml`, `SessionView.xaml.cs`, `CommandPaletteWindow.xaml`, `CommandPaletteWindow.xaml.cs`, `SettingsView.xaml`, `SettingsView.xaml.cs`*

### Bugs Found:
1. **SessionView.xaml.cs Rogue Logger (`SessionView.xaml.cs` lines 13-63):**
   - Contains its own static `Log()` method that writes to `%LocalAppData%/LogPro/debug.log`, completely bypassing `AppLogger`. This creates a separate, unmanaged log file that grows without rotation.
   - **Fix:** Replace all `Log(...)` calls with `AppLogger.Log.Debug(...)` and remove the static `Log` method and `LogPath` field.

2. **Command Palette Emoji Icons Corrupted (`MainWindow.xaml.cs` lines 33-59):**
   - All command palette entries use emoji strings (`"📊"`, `"📱"`, etc.) but they render as `"?"`, `"??"` due to encoding issues. The command palette shows garbled icons.
   - **Fix:** Replace emoji strings with Segoe MDL2 Assets glyph codes (e.g., `"\uE80F"` for dashboard).

### Suggestions:
1. **CommandPaletteWindow keyboard navigation:** Only Enter is handled. Arrow key navigation within the filtered list is missing.

---

## Slice 7: App Bootstrap, MainWindow & Helpers (70%)
*Files Audited: `App.xaml.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`, `ToolLauncher.cs`, `ToolResolver.cs`, `SecurityHelper.cs`, `PathHelper.cs`*

### Bugs Found:
1. **ToolLauncher.StartLongRunning stdout Deadlock (`ToolLauncher.cs`):**
   - `StartLongRunning` sets `RedirectStandardOutput = true` but **never reads** from `process.StandardOutput`. If a long-running process (like ADB) outputs more data than the OS pipe buffer (4KB on Windows), the child process will **permanently freeze/deadlock** waiting for the buffer to be drained.
   - **Fix:** Add `process.BeginOutputReadLine()` with a drain handler, OR set `RedirectStandardOutput = false` if the output isn't needed. **CAUTION:** `SessionService` also calls `BeginOutputReadLine` on the same process — if both drain, lines will be split randomly. The fix must ensure only ONE consumer reads stdout. Add a `drainStdout` parameter (default `true`), and have SessionService pass `drainStdout: false` since it attaches its own handler.

2. **ToolResolver PATH Pollution (`ToolResolver.cs`):**
   - `FindTool()` iterates the entire `%PATH%` variable, splitting on `;`. If PATH contains 200+ entries (common on dev machines), this creates 200+ `File.Exists()` calls per tool lookup. Additionally, duplicate PATH entries cause redundant checks.
   - **Fix:** Deduplicate PATH entries. Cache found tool paths.

3. **SecurityHelper Hash Truncation (`SecurityHelper.cs`):**
   - `Convert.ToHexString(hash)[..8].ToLower()` truncates SHA256 to 32 bits. Android serials follow predictable patterns (`RFXXXXXXXX` for Samsung, `HNXXXXXXXX` for Huawei). With only 32 bits of hash output and a known prefix, the serial can be brute-forced in seconds.
   - **Fix:** Use at minimum 16 hex chars (64 bits). `[..16]` instead of `[..8]`.

4. **App.xaml.cs Logs Full PATH Variable (`App.xaml.cs` line 60):**
   - `EarlyLog($"PATH Variable: {Environment.GetEnvironmentVariable("PATH")}")` dumps the entire system PATH to a log file. This can contain sensitive directory names (user-specific paths, internal tool locations).
   - **Fix:** Replace with `EarlyLog($"PATH entries: {pathEntries.Length}")`.

5. **MainWindow.xaml Hardcoded Legacy Branding:**
   - Title bar shows "LogPro" (line 8), sidebar shows "LogPro Terminal v3.0" (lines 163-164), icon references `Assets/LogProIcon.ico` (line 16).
   - **Fix:** Replace with "QADeviceTool" throughout.

---

## Slice 8-10: Models, Converters, Remaining Views, & Testing (100%)

**Files Audited:**
- `src/QADeviceTool.App/Models/*` (AppItem, BulkObservableCollection, DeviceFile, DeviceInfo, LogEntry, LogSession, ScrcpyOptions, ToolStatus)
- `src/QADeviceTool.App/Converters/*` (DeviceViewConverters, LogLevelColorMultiConverter)
- `src/QADeviceTool.App/Views/DeviceView.xaml`, `AppManagementView.xaml`, `ShellView.xaml`, `StressTestView.xaml`, `DashboardView.xaml`, `FileExplorerView.xaml`, `MacroView.xaml`, `VitalsView.xaml`, `DeepLinkView.xaml`
- `src/LogPro.Tests/*` (AdbServiceCommandTests, IosServiceParserTests, PathHelperTests, DeviceInfoTests, LogSessionTests, etc.)

### Bugs Found:
1. **BulkObservableCollection.AddRange Fires Reset:**
   - `OnCollectionChanged(NotifyCollectionChangedAction.Reset)` tells WPF the entire collection changed. Any `SelectedItem` binding on a ListBox bound to this collection will be cleared to null. During heavy log capture, the selected log entry jumps/resets every batch.
   - **Fix:** Use `Add` action type with index range, or preserve and re-apply selection after AddRange.

2. **Test Suite Missing ViewModel Coverage:**
   - `LogPro.Tests` has xUnit tests for `AdbService`, `IosService`, `PathHelper`, `DeviceInfo`, `LogSession` — but **zero ViewModel tests**. All ViewModels create real services in constructors, making them untestable without IoC.
   - **Fix:** Introduce `IService` interfaces and constructor injection to enable mocking.

3. **Test Namespace Mismatch:**
   - Tests use `namespace LogPro.Tests` — should match project rename.

### Suggestions:
1. **Comprehensive Test Suite Bootstrap:** The highest priority enhancement before any refactoring is introducing an IoC container (e.g., `Microsoft.Extensions.DependencyInjection`). Once ViewModels use constructor injection, we can immediately write unit tests for every navigation path, device state change, and error handling branch.

---
---

## 🔴 DEEP BUG HUNT — Consolidated Bug Registry

> After a complete line-by-line re-read of every source file (76+ files), the following 18 confirmed bugs were catalogued. Each entry includes precise file/line references, root cause, user-visible impact, and safe fix instructions.

### BUG-01 — ToolLauncher.StartLongRunning stdout deadlock (P0 — CRITICAL)

**File:** `Helpers/ToolLauncher.cs` — `StartLongRunning` method
**Root Cause:** Sets `RedirectStandardOutput = true` but never reads from `process.StandardOutput`. The OS pipe buffer (4KB on Windows) fills up and the child process freezes permanently.
**Impact:** Long-running ADB processes (logcat, monkey) silently freeze after ~4KB of output.
**Fix:** Add `process.BeginOutputReadLine()` with a drain handler OR set `RedirectStandardOutput = false`. **CAUTION:** SessionService also calls `BeginOutputReadLine` — add a `drainStdout` parameter (default `true`), SessionService passes `false` since it attaches its own handler.
**Regression guard:** After fix, verify SessionService still receives logcat output. Verify StressTest monkey output still flows.

---

### BUG-02 — AdbService semaphore misuse (P1)

**File:** `Services/AdbService.cs`
**Root Cause:** `_adbSemaphore` acquired in `RunAdbCommandAsync` but NOT in `StartAdbLongRunning`. Long-running commands bypass it entirely.
**Fix:** Remove the semaphore — ADB server handles concurrent access natively. Keeping it creates false safety.
**Regression guard:** Test concurrent operations (e.g., logcat running while listing packages).

---

### BUG-03 — SessionService flush timer race (P2)

**File:** `Services/SessionService.cs`
**Root Cause:** `_flushTimer` fires every 200ms. `StopCapture()` disposes timer then calls `FlushBuffer()`. If timer callback is mid-execution during Stop, buffer write races with stream close → `ObjectDisposedException`.
**Fix:** Wrap buffer access in `lock`. In `StopCapture`, acquire lock → dispose timer → flush → close stream.

---

### BUG-04 — DeviceMonitorService suppresses initial broadcast (P2)

**File:** `Services/DeviceMonitorService.cs`
**Root Cause:** First poll compares against empty `_previousDevices`. The initial `DevicesChanged` event may not fire if comparison logic has an off-by-one.
**Fix:** Force `DevicesChanged` on first poll regardless of comparison result.

---

### BUG-05 — VitalsViewModel polling never stops on navigation (P2)

**File:** `ViewModels/VitalsViewModel.cs`
**Root Cause:** `_pollTimer` fires `dumpsys meminfo` and `top` every 3 seconds. Only stops on device disconnect, NOT on view navigation. Creates constant invisible ADB traffic.
**Fix:** Implement `IDisposable` or add navigation lifecycle (stop timer on leave, restart on enter).

---

### BUG-06 — CleanupOldLogs searches wrong file extension (P2)

**File:** `Services/PreferencesService.cs` line 141
**Root Cause:** NLog generates `app-log-*.txt` but cleanup searches `"*.log"`. Logs never cleaned up.
**Fix:** Change to `"*.txt"` or search both patterns.

---

### BUG-07 — ShellViewModel O(n²) string concatenation (P1)

**File:** `ViewModels/ShellViewModel.cs`
**Root Cause:** `ShellOutput += newLine + "\n"` creates a new string each append. After 10K lines, each append copies the entire accumulated string → exponential memory allocation, severe UI freezing.
**Fix:** Use `StringBuilder` with periodic (100ms) copy to bound property, or `ObservableCollection<string>` bound to `ListBox`.

---

### BUG-08 — SessionService regex injection via PID (P3)

**File:** `Services/SessionService.cs`
**Root Cause:** PID from `adb shell pidof` used directly in regex without sanitization.
**Fix:** Use `Regex.Escape(pid)`.

---

### BUG-09 — MacroViewModel saves to legacy "LogPro" path (P2)

**File:** `ViewModels/MacroViewModel.cs`
**Root Cause:** Macros saved to `%LocalAppData%/LogPro/Macros` instead of `QAQCDeviceTool/Macros`.
**Fix:** Change path. Add migration to move existing macros.

---

### BUG-10 — Hardcoded pairing port scan (P2)

**File:** `Services/AdbService.cs`
**Root Cause:** `DiscoverPairingPortsAsync` scans hardcoded port range. If Android changes defaults, breaks silently.
**Fix:** Return empty list; user enters port manually (already supported).

---

### BUG-11 — adb pair wrong `--code` syntax (P0 — CRITICAL)

**File:** `Services/AdbService.cs` line ~753
**Root Cause:** Uses `adb pair {ip}:{port} --code {code}` but ADB syntax is `adb pair {ip}:{port} {code}` — no `--code` flag. Pairing ALWAYS fails.
**Fix:** Change to `$"pair {ip}:{port} {code}"`.

---

### BUG-12 — CrashDetector `_detectedCrashes` not thread-safe (P2)

**File:** `Services/CrashDetector.cs`
**Root Cause:** Plain `HashSet<string>` accessed from background ADB callback and UI thread without synchronization.
**Fix:** Replace with `ConcurrentDictionary<string, byte>` or add `lock`.

---

### BUG-13 — SessionService.StopCapture double-call race (P3)

**File:** `Services/SessionService.cs`
**Root Cause:** No re-entrance guard. Double-clicking Stop disposes already-disposed resources.
**Fix:** Add `if (!_isCapturing) return;` guard.

---

### BUG-14 — AppManagementViewModel O(n²) string concatenation (P1)

**File:** `ViewModels/AppManagementViewModel.cs`
**Root Cause:** Same `InstallOutput += line + "\n"` pattern as ShellViewModel.
**Fix:** Use `StringBuilder` with periodic UI update.

---

### BUG-15 — StressTestViewModel truncation allocation (P3)

**File:** `ViewModels/StressTestViewModel.cs`
**Root Cause:** `StressOutput += line` then checks length. The large string is already allocated before truncation.
**Fix:** Use `StringBuilder` with max capacity.

---

### BUG-16 — DeviceMonitorService premature device removal (P2)

**File:** `Services/DeviceMonitorService.cs`
**Root Cause:** Single failed ADB poll causes all devices to be marked disconnected. No debounce.
**Fix:** Add miss counter per device. Only remove after 2-3 consecutive misses.

---

### BUG-17 — SessionService.ReadLogContentAsync loads full file (P2)

**File:** `Services/SessionService.cs`
**Root Cause:** `File.ReadAllTextAsync()` loads entire log file into memory. Session logs can be 50-100MB+.
**Fix:** Use `StreamReader` with tail-read (last N lines) for preview.

---

### BUG-18 — IosService `_ipcLock` inconsistently applied (P1)

**File:** `Services/IosService.cs`
**Root Cause:** Lock acquired in `RunAsync` but NOT in `StartLongRunning` or `StartLong`.
**Fix:** Remove the lock (pymobiledevice3 processes are independent) or apply consistently.
**Regression risk:** LOW — removing the lock improves throughput.

---

### Bug Priority Summary

| Priority | Bug IDs | Description |
|:---------|:--------|:------------|
| **P0 — Fix Immediately** | BUG-01, BUG-11 | stdout deadlock, ADB pair syntax |
| **P1 — Fix Before Release** | BUG-02, BUG-07, BUG-14, BUG-18 | Semaphore misuse, O(n²) string concat ×2, iOS lock |
| **P2 — Fix Soon** | BUG-03, BUG-04, BUG-05, BUG-06, BUG-09, BUG-10, BUG-12, BUG-16, BUG-17 | Timer races, polling leaks, path/extension bugs, thread safety, debounce |
| **P3 — Minor** | BUG-08, BUG-13, BUG-15 | Regex escape, double-call guard, truncation alloc |

### Recommended Fix Order
1. **BUG-01** → **BUG-11** → **BUG-07** → **BUG-14** (performance + critical)
2. **BUG-02** → **BUG-18** (concurrency cleanup)
3. **BUG-03** → **BUG-04** → **BUG-05** → **BUG-12** → **BUG-16** (stability)
4. **BUG-06** → **BUG-09** → **BUG-10** → **BUG-17** (correctness)
5. **BUG-08** → **BUG-13** → **BUG-15** (defensive fixes)

---
---

## 🎨 UI/UX AUDIT — Button States, Placements, Visual Feedback & Interaction Design

> Comprehensive audit of all 13 XAML views, covering: button enabled/disabled states, visual feedback, layout consistency, keyboard navigation, and interaction design.

### UX-01 — Session Start/Stop buttons always clickable (P1)

**File:** `Views/SessionView.xaml` + `ViewModels/SessionViewModel.cs`
**Issue:** Both "Start Capture" and "Stop Capture" buttons are always enabled. No `IsEnabled` binding to `IsCapturing` / `!IsCapturing`. User can click Start repeatedly (spawning multiple logcat processes) or click Stop when nothing is running.
**Fix:** Bind `IsEnabled="{Binding IsCapturing, Converter={StaticResource InverseBoolConverter}}"` on Start, and `IsEnabled="{Binding IsCapturing}"` on Stop.

---

### UX-02 — Mirror/Stop Mirror buttons no mutual exclusion (P1)

**File:** `Views/DeviceView.xaml` + `ViewModels/DeviceViewModel.cs`
**Issue:** Both "Mirror Screen" and "Stop Mirror" buttons are always enabled. `IsMirroring` property exists in DeviceViewModel but neither button binds to it.
**Fix:** Bind `IsEnabled="{Binding IsMirroring, Converter={StaticResource InverseBoolConverter}}"` on Mirror, `IsEnabled="{Binding IsMirroring}"` on Stop.

---

### UX-03 — No loading spinners for async operations (P3)

**Issue:** Install APK, Start Capture, Refresh Devices, File Explorer navigation — all async operations show no loading indicator. User has no feedback that an operation is in progress.
**Fix:** Add `IsBusy` property to each ViewModel. Show a `ProgressRing` or `BusyIndicator` overlay while `IsBusy` is true.

---

### UX-04 — Delete operations lack confirmation (P2)

**Issue:** Delete Session, Delete Macro, Clear Shell Output — all execute immediately on click with no confirmation dialog. Accidental clicks cause data loss.
**Fix:** Add `DialogService.Confirm("Are you sure?")` before destructive operations.

---

### UX-05 — Card styles inconsistent across views ✅ FIXED BY UI REWORK

**Status:** ✅ **FIXED** — All views now use consistent dark card design (`#1A1A1F` bg, `#252530` border, `CornerRadius=12`). Previously mixed `AppleCard` and `GlassCard` styles.

---

### UX-06 — Device sidebar duplicated in each view ✅ FIXED BY UI REWORK

**Status:** ✅ **FIXED** — Global device selection moved to MainViewModel. Per-view device sidebars removed.

---

### UX-07 — Command Palette emoji icons corrupted 🔴 WORSENED

**File:** `MainWindow.xaml.cs` lines 33-59
**Status:** 🔴 **WORSENED** — Emojis still render as `"?"`, `"??"` in the command palette window.
**Fix:** Replace emoji strings with Segoe MDL2 Assets glyphs.

---

### UX-08 — Keyboard shortcuts decorative only (P3)

**File:** `MainWindow.xaml.cs`
**Issue:** Command palette shows shortcuts (Ctrl+1 through Ctrl+7) but only Ctrl+K is actually wired in `OnPreviewKeyDown`. All other shortcuts are decorative.
**Fix:** Add key handler cases in `OnPreviewKeyDown`.

---

### UX-09 — No visual feedback on Save operations (P3)

**Issue:** Settings Save, Preferences Save — no toast/snackbar/status bar message confirming the save completed.
**Fix:** Show a brief status message ("Settings saved ✓") in the status bar or as a toast.

---

### UX-10 — Uninstall clickable with no app selected (P2)

**File:** `Views/AppManagementView.xaml`
**Issue:** Uninstall button is always enabled. Clicking with no app selected causes a silent failure or null reference.
**Fix:** Bind `IsEnabled="{Binding SelectedApp, Converter={StaticResource NotNullConverter}}"`.

---

### UX-11 — File Explorer buttons always enabled (P2)

**File:** `Views/FileExplorerView.xaml`
**Issue:** Push, Pull, Delete file buttons are always enabled regardless of selection state.
**Fix:** Bind `IsEnabled` to `SelectedFile != null`.

---

### UX-12 — Macro Play/Stop/Delete enabled when no macro selected (P2)

**File:** `Views/MacroView.xaml`
**Issue:** Play, Stop, Delete buttons always enabled.
**Fix:** Bind `IsEnabled` to `SelectedMacro != null`.

---

### UX-13 — Legacy "LogPro" branding throughout 🔴 WORSENED

**Status:** 🔴 **WORSENED** — UI rework added MORE LogPro references:
- `MainWindow.xaml` line 8: `Title="LogPro"` (now fixed to QADeviceTool)
- `MainWindow.xaml` line 140: `Text="LogPro"`
- `MainWindow.xaml` lines 163-164: `Text="LogPro"`, `Text="Terminal v3.0"`
- `SettingsView.xaml` line 306: About section says "LogPro"
- `App.xaml.cs` line 10: `LogPro_startup-debug.log`
- `App.xaml.cs` line 88: `"LogPro - Application Starting"`
- `App.xaml.cs` line 108: `"LogPro - Error"`
- ALL C# namespaces still use `LogPro.*`
**Fix:** Rename all UI-visible references to "QADeviceTool". Namespace rename is a lower priority separate task.

---

### UX-14 — Dashboard device status always green (P2)

**File:** `Views/DashboardView.xaml` line ~241
**Issue:** Device status indicator uses hardcoded `#4ADE80` (green). Never changes based on actual device state.
**Fix:** Bind to device connection status with a color converter.

---

### UX-15 — BooleanToVisibilityConverter missing ✅ FIXED BY UI REWORK

**Status:** ✅ **FIXED** — Now declared globally in `App.xaml`.

---

### UX-16 — FileExplorerView binds to wrong DataContext ✅ FIXED BY UI REWORK

**Status:** ✅ **FIXED** — Now uses local ViewModel binding.

---

### UX-17 — No empty state illustrations (P3)

**Issue:** Empty session list, no devices connected, no macros — all show blank white space with no guidance.
**Fix:** Add empty state illustrations with instructional text.

---

### UX-18 — Sidebar nav not synced with programmatic navigation (P3)

**Issue:** Sidebar RadioButtons don't sync `IsChecked` when navigation happens programmatically (via command palette or DashboardView quick actions).
**Fix:** Bind `RadioButton.IsChecked` to a `SelectedNavItem` comparison.

---

### UX-19 — StressTest percentage inputs no validation feedback (P3)

**Issue:** Monkey event percentages (touch, motion, trackball, etc.) accept any value. No validation that they sum to 100%.
**Fix:** Show warning when total ≠ 100%.

---

### UX-20 — Shell output doesn't auto-scroll (P3)

**Issue:** As shell output grows, the TextBlock doesn't auto-scroll to bottom. User must manually scroll.
**Fix:** Add `ScrollViewer` with auto-scroll behavior on content change.

---

### UI/UX Priority Summary

| Priority | Bug IDs | Description |
|:---------|:--------|:------------|
| **P1 — Critical** | UX-01, UX-02 | Button state safety (double-start, double-mirror) |
| **P2 — Important** | UX-04, UX-10, UX-11, UX-12, UX-14 | Delete confirm, button guards, dashboard colors |
| **P3 — Polish** | UX-03, UX-07, UX-08, UX-09, UX-13, UX-17, UX-18, UX-19, UX-20 | Feedback, branding, empty states, keyboard |

---
---

## 🔴 ERROR HANDLING, CRASH REPORTING & TOOL LOGGING AUDIT

> **Scope:** Purely internal tool logging — what happens inside the tool itself, NOT device logs. Covers exception handling, crash reporting, log quality, and diagnostic instrumentation.

### ERR-01 — 50+ silent catch blocks across codebase (P1)

**Files:** `AdbService.cs`, `IosService.cs`, `SessionService.cs`, `FileExplorerViewModel.cs`, `MacroService.cs`, `DeviceMonitorService.cs`, and others.
**Issue:** Dozens of `catch (Exception) { }` or `catch { }` blocks that swallow exceptions with no logging. When things go wrong, there's zero diagnostic trace.
**Fix:** Add `AppLogger.Log.Warn(ex, "Context message")` to every silent catch block.

---

### ERR-02 — ViewModels show ex.Message but never log (P1)

**Files:** All ViewModels
**Issue:** ViewModel catch blocks show `MessageBox.Show(ex.Message)` or set a `StatusMessage` property, but never call `AppLogger.Log`. The error is visible to the user but not in the log file. Post-mortem debugging is impossible.
**Fix:** Add `AppLogger.Log.Error(ex, ...)` alongside user-facing messages.

---

### ERR-03 — No crash report generation (P1)

**Issue:** `App.xaml.cs` has `DispatcherUnhandledException` and `AppDomain.UnhandledException` handlers, but they only log to AppLogger. No structured crash dump (minidump, exception details, environment info) is generated. No auto-restart or recovery.
**Fix:** Generate a `crash-report-{timestamp}.txt` in the app data directory containing: exception type/message/stack, OS version, app version, loaded assemblies, and the last 50 log lines.

---

### ERR-04 — AdbService.RunAdbCommandAsync empty catch (P2)

**File:** `Services/AdbService.cs`
**Issue:** Main ADB execution method has a catch that logs but returns empty string. Caller has no way to distinguish "ADB returned nothing" from "ADB failed."
**Fix:** Return a result object with success/failure status, or throw a typed exception.

---

### ERR-05 — ScrcpyService swallows mirror start failures (P2)

**File:** `Services/ScrcpyService.cs`
**Issue:** If scrcpy process fails to start (wrong path, missing dependency), the exception is caught and `IsMirroring` stays false. No error shown to user.
**Fix:** Surface the error to DeviceViewModel for display.

---

### ERR-06 — Debug.WriteLine used instead of AppLogger (P3)

**Files:** `SessionView.xaml.cs`, `FileExplorerView.xaml.cs`, and others
**Issue:** Some code-behind files use `System.Diagnostics.Debug.WriteLine` which only outputs to debugger, not to log files.
**Fix:** Replace all with `AppLogger.Log.Debug(...)`.

---

### ERR-07 — DeviceMonitorService poll failure silent (P2)

**File:** `Services/DeviceMonitorService.cs`
**Issue:** If `adb devices` fails (ADB server not running), the catch block silently returns an empty device list. No indication that monitoring is impaired.
**Fix:** Log the failure and set a `MonitoringHealthy` property to false.

---

### ERR-08 — ToolLauncher never logs process exit codes (P1)

**File:** `Helpers/ToolLauncher.cs`
**Issue:** Process exit codes are never checked or logged. A failed ADB command (exit code 1) is treated the same as success.
**Fix:** Log `process.ExitCode` after `WaitForExit`. Return it in the result.

---

### ERR-09 — SessionService.StartCapture no success confirmation (P2)

**Issue:** No log entry when capture starts successfully. Only errors are logged. Difficult to correlate session start times in log files.
**Fix:** Add `AppLogger.Log.Info($"Capture started for device {hashedSerial}")`.

---

### ERR-10 — CrashDetector exception patterns too broad (P3)

**File:** `Services/CrashDetector.cs`
**Issue:** Crash detection patterns may match non-crash logcat lines (e.g., app logging about "FATAL" errors that aren't actual crashes).
**Fix:** Use more specific regex patterns that match Android's actual crash format.

---

### ERR-11 — No operation start/end lifecycle logging (P3)

**Issue:** Major operations (install APK, run stress test, start macro) don't log their start and completion. Makes it impossible to measure operation duration from logs.
**Fix:** Add `AppLogger.Log.Info($"Starting {operation}...")` and `AppLogger.Log.Info($"Completed {operation} in {elapsed}ms")`.

---

### ERR-12 — PreferencesService.Load deserialize failure loses all prefs (P2)

**File:** `Services/PreferencesService.cs`
**Issue:** If settings.json is corrupted, `JsonSerializer.Deserialize` fails and `Current` is set to defaults. The original file is not backed up.
**Fix:** Back up the corrupted file as `settings.json.corrupt.{timestamp}` before overwriting.

---

### ERR-13 — Global exception handler shows raw ex.Message (P2)

**File:** `App.xaml.cs`
**Issue:** `DispatcherUnhandledException` handler shows `MessageBox.Show(ex.Message)`. Raw exception messages can be confusing to end users.
**Fix:** Show user-friendly message with option to view/copy technical details.

---

### ERR-14 — IosService RunAsync stderr not logged (P1)

**File:** `Services/IosService.cs`
**Issue:** pymobiledevice3 outputs errors to stderr but `RunAsync` only captures stdout. Stderr output is lost.
**Fix:** Capture stderr alongside stdout: `RedirectStandardError = true`, read both streams.

---

### ERR-15 — No navigation logging (P3)

**Issue:** View navigation changes are not logged. Can't trace user workflow from logs.
**Fix:** Add `AppLogger.Log.Debug($"Navigated to {viewName}")` in `MainViewModel.Navigate`.

---

### ERR-16 — MacroService replay errors silent (P2)

**File:** `Services/MacroService.cs`
**Issue:** If any `sendevent` command fails during macro replay, the error is swallowed and replay continues. The macro appears to work but input is partially lost.
**Fix:** Log each failed command. Optionally abort replay on critical failure.

---

### ERR-17 — SessionView rogue logger bypasses AppLogger (P2)

**File:** `Views/SessionView.xaml.cs` lines 13-63
**Issue:** Static `Log()` method writes to `%LocalAppData%/LogPro/debug.log`, completely bypassing AppLogger. Creates unmanaged log file.
**Fix:** Remove the static `Log` method. Replace with `AppLogger.Log.Debug(...)`.

---

### ERR-18 — App.xaml.cs early log uses "LogPro" branding (P3)

**File:** `App.xaml.cs` line 10
**Issue:** `"LogPro_startup-debug.log"` — should be `"QADeviceTool_startup-debug.log"`.

---

### ERR-19 — DependencyChecker swallows tool resolution failures (P2)

**File:** `Services/DependencyChecker.cs`
**Issue:** When `ToolResolver.FindTool()` fails, the reason is not logged. User sees "Not Found" but doesn't know why.
**Fix:** Log the exception in the catch block.

---

### ERR-20 — No log rotation awareness in NLog config (P3)

**Issue:** NLog is configured with 7-day retention, but `PreferencesService.Current.LogRetentionDays` is not wired to the NLog config. Changing retention in settings has no effect on actual log rotation.
**Fix:** Wire the preference to NLog's `maxArchiveDays` property on startup.

---

### ERR-21 — ProcessManagerService.KillAll failures silent (P2)

**File:** `Services/ProcessManagerService.cs`
**Issue:** When process termination fails (access denied, process already exited), the error is swallowed.
**Fix:** Log each failure with the process name and reason.

---

### ERR-22 — StressReportBuilder exception on empty results (P2)

**File:** `Services/StressReportBuilder.cs`
**Issue:** If no stress test results are available, the report builder may throw NullReferenceException when accessing empty collections.
**Fix:** Add null/empty checks before generating report.

---

### ERR-23 — FileExplorerViewModel fire-and-forget with silent catch (P2)

**File:** `ViewModels/FileExplorerViewModel.cs`
**Issue:** `_ = Task.Run(async () => { try { await LoadDirectoryAsync(CurrentPath); } catch { } });` — entire file listing wrapped in fire-and-forget with silent catch. If device disconnects mid-browse, file list stops updating with no feedback.
**Fix:** Add logging inside catch and update `StatusMessage`.

---

### Error Handling Priority Summary

| Priority | Bug IDs | Description |
|:---------|:--------|:------------|
| **P1 — Critical** | ERR-01, ERR-02, ERR-03, ERR-08, ERR-14 | Silent catches, no crash reports, no exit codes, no stderr |
| **P2 — Important** | ERR-04, ERR-05, ERR-07, ERR-09, ERR-12, ERR-13, ERR-16, ERR-17, ERR-19, ERR-21, ERR-22, ERR-23 | Silent failures, lost errors, missing context |
| **P3 — Polish** | ERR-06, ERR-10, ERR-11, ERR-15, ERR-18, ERR-20 | Debug.WriteLine, branding, lifecycle logging |

---
---

## 🔵 FEATURE-LEVEL DEEP AUDIT

> Functional correctness of every major feature. Each finding includes root cause and safe fix.

### FEAT-01 — Live log UI floods and drops lines (P2)
**File:** `SessionViewModel.cs` — 200ms flush timer + O(n) filter on every batch.
**Fix:** Use `BulkObservableCollection.AddRange` (already exists). Apply filter only to new entries, not the full collection.

### FEAT-02 — SaveLog saves truncated in-memory data (P1)
**File:** `SessionViewModel.cs` — `SaveLog` writes the in-memory `FilteredEntries` collection, not the full on-disk log file.
**Fix:** Copy the raw log file to the save destination instead of serializing in-memory data.

### FEAT-03 — Live log search re-filters entire collection (P2)
**Fix:** Debounce search input (300ms). Apply filter incrementally to new entries only.

### FEAT-04 — Session list doesn't highlight active capture (P3)
**Fix:** Bind background color of the active session's ListBoxItem to `IsCapturing`.

### FEAT-05 — Delete session doesn't check if capture is active (P3)
**Fix:** Check `IsCapturing` before allowing delete. Show warning if active.

### FEAT-06 — APK install reports "Success" when output contains "Failure" (P1)
**File:** `AdbService.cs` — Returns stdout without checking for failure indicators.
**Fix:** Check the **last line** of output for `"Failure"` or `"Error"`. Return structured result with success/failure.
**CAUTION:** Don't check `Contains("Failure")` on full output — package names could contain "Failure". Check only the last line.

### FEAT-07 — Uninstall hides actual error reason (P3)
**File:** `AdbService.cs` — Returns generic error string instead of actual ADB output.
**Fix:** Return actual ADB output to the ViewModel for display.

### FEAT-08 — App list doesn't show version/install location (P3)
**Fix:** Parse `dumpsys package` output to extract version and install location.

### FEAT-09 — Android /data/ inaccessible without root (P2)
**File:** `AdbService.cs` — `ls` on `/data/` fails silently on non-rooted devices.
**Fix:** Show "Permission denied" message. Default to `/sdcard/` on non-rooted devices.

### FEAT-10 — iOS ParseAfcLs uses broken heuristic (P2)
**File:** `IosService.cs` — `!name.Contains('.')` classifies files without extensions as folders.
**Fix:** Use `afcutil ls -l` for structured output, or check if entry has children.

### FEAT-11 — iOS file pull not implemented (P2)
**File:** `IosService.cs` — `PullFileAsync` returns hardcoded empty result.
**Fix:** Implement using `pymobiledevice3 afc pull`.

### FEAT-12 — Monkey fails on OEM devices (P2)
**File:** `StressTestViewModel.cs` — Missing `--ignore-security-exceptions`, `--ignore-timeouts`, `--ignore-crashes` flags.
**Fix:** Add defensive monkey flags.

### FEAT-13 — Stress test metrics are single post-run snapshot (P2)
**Fix:** Sample metrics periodically during test (every 5s) and aggregate min/max/avg.

### FEAT-14 — Stress test report quality is minimal (P3)
**File:** `StressReportBuilder.cs` — Only includes final snapshot.
**Fix:** Include time-series data, event counts, crash counts.

### FEAT-15 — Deep link always reports "Failed" (P1)
**File:** `DeepLinkViewModel.cs` + `AdbService.cs` — `am start` returns success on stdout but the ViewModel checks for wrong success indicator.
**Fix:** Check for `"Starting:"` in ADB output (indicates intent was dispatched).

### FEAT-16 — iOS deep link via `idevicediagnostics` stub (P2)
**File:** `IosService.cs` — `OpenUrlAsync` returns hardcoded "not supported".
**Fix:** Implement using `pymobiledevice3 apps open-url`.

### FEAT-17 — Installed apps list shows package names, not display names (P3)
**Fix:** Parse `dumpsys package` to extract display names alongside package names.

### FEAT-18 — Macro replay timing drift (P1)
**File:** `MacroService.cs` — `Task.Delay(ms)` between each event doesn't account for ADB execution time (~30-50ms). Over 100 events, timing drifts by 3-5 seconds.
**Fix:** Use `Stopwatch` to measure actual elapsed time and adjust delays accordingly. Better yet, batch all events into a device-side script.

### FEAT-19 — Macro record doesn't capture multi-touch (P3)
**Fix:** Record all `sendevent` slots, not just slot 0.

### FEAT-20 — VitalsViewModel doesn't show GPU metrics (P3)
**Fix:** Add `dumpsys gfxinfo` parsing for frame render times.

### FEAT-21 — Session restart appends to same file without separator (P3)
**Fix:** Add a `--- SESSION RESTARTED ---` separator line on restart.

---
---

## 🔴 SECURITY, COMPLIANCE & LEGAL AUDIT

> **Context:** This tool tests unreleased/unannounced games. Data leakage is a critical risk.

### SEC-01 — ToolLauncher logs every ADB command + full stdout (P0)
**File:** `Helpers/ToolLauncher.cs`
**Risk:** Every ADB command and its full output is logged to `%LocalAppData%/QAQCDeviceTool/logs/`. This includes unreleased game package names, deep link URIs, device serial numbers, and internal app data.
**Fix:** Implement "Secure Mode" toggle. When enabled: redact package names in logs (replace with hash), strip deep link query parameters, omit stdout from log entries.

### SEC-02 — Screenshots stored unencrypted with no retention (P1)
**Fix:** Add configurable retention. Delete screenshots older than N days on startup.

### SEC-03 — Screen recordings stored unencrypted (P1)
**Fix:** Same as SEC-02. Add retention policy.

### SEC-04 — Bug reports contain full `getprop` + `dumpsys package` (P1)
**File:** `SessionViewModel.cs`
**Risk:** Bug reports dump every installed package, device properties, and hardware info to a plain text file.
**Fix:** Filter `getprop` to only include relevant keys (model, OS version, build). Omit package list.

### SEC-05 — App.xaml.cs logs full PATH variable (P2)
**File:** `App.xaml.cs` line 60
**Fix:** Log entry count only: `$"PATH entries: {entries.Length}"`.

### SEC-06 — settings.json stores raw device serial numbers (P1)
**File:** `Services/PreferencesService.cs`
**Risk:** `DevicePreferences` dictionary uses raw serial as key. If settings.json is leaked, all device serials are exposed.
**Fix:** Hash serial before using as dictionary key.

### SEC-07 — Wireless ADB enables TCP mode without warning (P2)
**File:** `AdbService.cs`
**Risk:** `adb tcpip 5555` opens device to any machine on the network. No user warning about security implications.
**Fix:** Show warning dialog before enabling TCP mode.

### SEC-08 — IsSafePath incomplete (P2)
**File:** `AdbService.cs`
**Risk:** `IsSafePath` blocks `..` but doesn't block pipe (`|`), ampersand (`&`), newline (`\n`), or semicolon (`;`).
**Fix:** Add checks for shell metacharacters.

### SEC-09 — iOS commands don't quote paths (P2)
**File:** `IosService.cs`
**Risk:** File paths with spaces or special chars could cause command injection.
**Fix:** Quote all path arguments.

### SEC-10 — No outbound network calls ✅ PASS
**Verified:** Zero `HttpClient`, `WebClient`, `TcpClient`, or `WebSocket` usage in entire codebase.

### SEC-11 — No telemetry or analytics ✅ PASS
**Verified:** No analytics SDKs referenced in `.csproj`.

### SEC-12 — SecurityHelper hash truncation too short (P2)
**Fix:** Change `[..8]` to `[..16]` (64 bits minimum).

### SEC-13 — Session files have no ACLs (P3)
**Fix:** Set file permissions to owner-only on session directories.

### COMP-01 — No session data cleanup on app exit (P1)
**Fix:** Run `CleanupOldLogs()` on app startup. Add cleanup for session directories.

### COMP-02 — No data export/deletion feature for compliance (P2)
**Fix:** Add "Export My Data" and "Delete My Data" buttons in Settings.

### COMP-03 — No first-run privacy notice (P3)
**Fix:** Show a privacy notice on first launch explaining what data is collected and stored.

### COMP-04 — ClearAllData doesn't delete session recordings (P2)
**File:** `PreferencesService.cs`
**Fix:** Also delete the Sessions root directory contents.

### LEGAL-01 — THIRD_PARTY_NOTICES.txt incomplete (P2)
**Fix:** Add entries for all bundled tools (ADB, scrcpy, etc.) with correct license text.

### LEGAL-02 — pymobiledevice3 is GPL-3.0 (P0)
**Risk:** Bundling GPL-3.0 code triggers source-code disclosure obligations for the entire tool.
**Fix:** Move to process-isolation model — invoke pymobiledevice3 as a subprocess, don't bundle it. Document as a "system dependency" like ADB.

### LEGAL-03 — scrcpy is Apache-2.0 (OK but needs NOTICE)
**Fix:** Include Apache-2.0 NOTICE file as required by the license.

### LEGAL-04 — NuGet package licenses not audited (P3)
**Fix:** Run `dotnet nuget list --include-transitive` and verify all licenses are compatible.

### LEGAL-05 — No LICENSE file in project root (P3)
**Fix:** Add appropriate LICENSE file.

---
---

## ✅ CROSS-VERIFICATION PASS

### FIX-CLARIFY-01 — BUG-01 stdout drain must not conflict with SessionService
**Risk:** Both ToolLauncher and SessionService calling `BeginOutputReadLine` would split lines randomly.
**Safe Fix:** Add `drainStdout` parameter to `StartLongRunning`. Default `true`. SessionService passes `false`.

### FIX-CLARIFY-02 — BUG-02 semaphore removal must be tested under concurrency
**Safe Fix:** Remove semaphore, then test: run logcat + list packages + install APK simultaneously.

### FIX-CLARIFY-03 — FEAT-01 trim-by-chunk must not fire N×1000 CollectionChanged
**Safe Fix:** Use `BulkObservableCollection.RemoveRange` (add this method) with batch notification.

### FIX-CLARIFY-04 — FEAT-06 install status check must not false-positive
**Safe Fix:** Check only the **last line** of ADB output for failure indicators, not the full string.

### FIX-CLARIFY-05 — FEAT-15 deep link success check must match ADB format
**Safe Fix:** Check for `"Starting: Intent"` in output (exact ADB success format).

### FIX-CLARIFY-06 — BUG-07 StringBuilder must update UI on dispatcher
**Safe Fix:** Use `DispatcherTimer` (100ms) to copy `StringBuilder.ToString()` to bound property.

### FIX-CLARIFY-07 — BUG-16 debounce must preserve device state during USB hiccups
**Safe Fix:** Use per-device miss counter. Only fire `DeviceDisconnected` after 3 consecutive misses (9 seconds at 3s poll).

### MISS-01 — StressTest monkey percentages don't validate sum to 100%
**Fix:** Add setter validation. Show warning TextBlock when total ≠ 100%.

### MISS-02 — StressTest runs monkey on iOS devices (will fail)
**Fix:** Add platform guard: `if (device.Platform == "iOS") { StatusMessage = "Monkey not supported on iOS"; return; }`.

### MISS-03 — StressTest event count has no upper bound validation
**Fix:** Clamp to 1–1,000,000. Warn above 100,000.

### MISS-04 — DashboardViewModel quick actions don't check device state
**Fix:** Disable quick action buttons when no device connected.

### MISS-05 — No IDisposable on ViewModels (event subscription leaks)
**Fix:** Implement `IDisposable`. Unsubscribe from `DevicesChanged` and stop timers in `Dispose()`.

### MISS-06 — AdbService.IsSafePath allows backtick injection
**Fix:** Add backtick (`` ` ``) to blocked characters.

### MISS-07 — StressTestViewModel string concatenation (same as BUG-07 pattern)
**Fix:** Use `StringBuilder` (same as BUG-07/BUG-14 fix pattern).

### MISS-08 — No app version displayed in UI
**Fix:** Show assembly version in Settings About section and status bar.

### CORRECTION-01 — Original audit incorrectly reported "no test suite"
**Correction:** `src/LogPro.Tests/` exists with xUnit + FluentAssertions tests for AdbService, IosService, PathHelper, DeviceInfo, LogSession. But zero ViewModel test coverage.

### CORRECTION-02 — DeviceMonitorService is not missing, it was in a different namespace
**Correction:** File exists at `Services/DeviceMonitorService.cs` under `LogPro.Services` namespace.

### CORRECTION-03 — BulkObservableCollection exists and is used
**Correction:** `Models/BulkObservableCollection.cs` provides `AddRange` with batch notification. Currently used in SessionViewModel.

### CORRECTION-04 — DialogService exists but is static
**Correction:** `Services/DialogService.cs` was added during UI rework. Static class, not interface-based.

---
---

## 🆕 NEW BUGS INTRODUCED BY UI REWORK (2026-05-17)

> These bugs were introduced during the major UI redesign and did not exist in the original codebase.

### NEW-01 — Double MainViewModel instantiation (P0 — CRITICAL) 🆕

**Files:** `App.xaml.cs` line 74, `MainWindow.xaml` lines 120-122
**Root Cause:** XAML declares `<vm:MainViewModel />` in `<Window.DataContext>`, creating one instance during XAML parsing. Then `App.xaml.cs` OnStartup creates ANOTHER: `mw.DataContext = new MainViewModel();`. Result:
1. Two `DeviceMonitorService` instances polling ADB simultaneously
2. Two sets of 11 child ViewModels consuming memory
3. First VM is orphaned — its timers/events keep running (memory leak + ghost ADB polls)
**Fix:** Remove `<Window.DataContext>` block from MainWindow.xaml (lines 120-122). Keep only the code-behind assignment.

---

### NEW-02 — ThemeService.SwitchTheme destroys active sessions (P1) 🆕

**File:** `ThemeService.cs` lines 53-79
**Root Cause:** Theme switch creates new MainWindow and closes old. `MainWindow.Close_Click` calls `vm.Cleanup()` which calls `_sessionService.StopAllCaptures()`, `_scrcpyService.StopMirroring()`, `_deviceMonitor.Dispose()`. After switch, monitoring is dead.
**Fix:** Add `IsThemeSwitching` flag to MainWindow. Skip `Cleanup()` during theme switches.

---

### NEW-03 — ThemeService static constructor race with PreferencesService (P2) 🆕

**File:** `ThemeService.cs` line 35
**Root Cause:** Static constructor reads `PreferencesService.Current.ThemePreference` — but if ThemeService is accessed first, PreferencesService may not have loaded yet.
**Fix:** Read theme preference lazily in `ApplyStartupTheme()`, not in static constructor.

---

### NEW-04 — Command Palette "devices" navigation route broken (P1) 🆕

**File:** `MainWindow.xaml.cs` line 34, `MainViewModel.cs` line 170
**Root Cause:** Command palette sends `nav:devices` → extracts `"devices"`. Navigate switch has `"device"` (singular). Falls to default → shows Dashboard.
**Fix:** Add `"devices"` case: `"device" or "devices" => DeviceVM`.

---

### NEW-05 — MainWindow.xaml all colors hardcoded — theme system broken (P1) 🆕

**File:** `MainWindow.xaml` — throughout (lines 46, 56, 61-67, 92, 110, 124, 133, 140, 159, 267, 277-293)
**Root Cause:** Every color is a hex literal (`#060606`, `#0E0E0E`, `#BAC9CD`, `#8CEBFF`). When switching to Light theme, these don't change because they don't use `{DynamicResource}`.
**Impact:** Light theme only affects inner view content — the app shell stays permanently dark.
**Fix:** Replace all hardcoded colors with `{DynamicResource BrushVoid}`, `{DynamicResource BrushSurface}`, etc.

---

### NEW-06 — All view XAML files hardcode colors — theme bypassed (P1) 🆕

**Files:** `DashboardView.xaml`, `SettingsView.xaml`, `SessionView.xaml`, `DeviceView.xaml`, etc.
**Root Cause:** Same as NEW-05. All cards, backgrounds, text use hardcoded hex, not `{DynamicResource}`.
**Fix:** Convert all 12 view files to use theme resource references.

---

### NEW-07 — SessionView rogue logger STILL writes to LogPro/debug.log (P2) 🆕

**File:** `Views/SessionView.xaml.cs` lines 13-15, 45-63
**Status:** Unchanged from ERR-17. The static `Log()` method still writes to `%LocalAppData%/LogPro/debug.log`.

---

### NEW-08 — Service Interfaces created but never used (P3 — dead code) 🆕

**File:** `Services/Interfaces/` — 5 interfaces (IAdbService, IIosService, IDeviceMonitorService, IScrcpyService, ISessionService)
**Issue:** These interfaces exist but no code references them. All ViewModels still accept concrete types.

---

### NEW-09 — FeatureFlags gates commands that don't exist (P3) 🆕

**File:** `MainWindow.xaml.cs` lines 51-59
**Issue:** `ai:analyze` and `action:selectAll` commands added to palette but `OnCommandExecuted` doesn't handle them.

---

### NEW-10 — BulkObservableCollection Reset loses selection (P2) 🆕

**File:** `Models/BulkObservableCollection.cs` line 26
**Root Cause:** `OnCollectionChanged(Reset)` tells WPF entire collection changed. Any `SelectedItem` binding is cleared during heavy log batching.
**Fix:** Use `Add` action with index range, or preserve/restore selection.

---

### NEW-11 — MainWindow.xaml.cs formatting error (P3) 🆕

**File:** `MainWindow.xaml.cs` lines 14-16
**Root Cause:** Misaligned closing brace suggests sloppy merge.

---

### NEW-12 — MainWindow Icon references non-existent file (P3) 🆕

**File:** `MainWindow.xaml` line 16
**Issue:** `Icon="Assets/LogProIcon.ico"` — actual file is `QAQCDeviceIcon.ico`. (Already fixed in W0-01)

---

### NEW-13 — SettingsView ComboBox unstyled for dark theme (P2) 🆕

**File:** `Views/SettingsView.xaml` lines 181-184
**Issue:** Default WPF ComboBox chrome renders as white/gray on dark background.

---

### NEW-14 — "LogPro" branding added in new locations (P3) 🆕

**Locations:** MainWindow.xaml sidebar text, SettingsView About, App.xaml.cs startup/error strings, SessionView.xaml.cs logger path. See UX-13 for full list.

---

### NEW-15 — ThemeService.SwitchTheme duplicated null check (P3) 🆕

**File:** `ThemeService.cs` line 63
**Issue:** `if (dataContext != null) if (dataContext != null) newWindow.DataContext = dataContext;`

---

### New Bug Priority Summary

| Priority | Bug IDs | Description |
|:---------|:--------|:------------|
| **P0** | NEW-01 | Double VM instantiation — ghost ADB polls, memory leak |
| **P1** | NEW-02, NEW-04, NEW-05, NEW-06 | Theme kills sessions; broken nav; theme colors hardcoded |
| **P2** | NEW-03, NEW-07, NEW-10, NEW-13 | Init race; rogue logger; selection reset; unstyled combo |
| **P3** | NEW-08, NEW-09, NEW-11, NEW-12, NEW-14, NEW-15 | Dead code, branding, formatting |

---
---

## UI REWORK STATUS SUMMARY

### ✅ What the UI rework FIXED (4 items)
1. **UX-05** — Card styles now unified across all views
2. **UX-06** — Per-view device sidebars eliminated; global device selector
3. **UX-15** — BooleanToVisibilityConverter declared globally in App.xaml
4. **UX-16** — FileExplorerView now uses local ViewModel binding

### 🔴 What the UI rework WORSENED (2 items)
1. **UX-07** — Command palette emoji icons still corrupted
2. **UX-13** — More "LogPro" branding added in new locations

### ⚠️ What remains UNCHANGED (all other findings)
All 18 BUGs, 23 ERR issues, 21 FEAT issues, 13 SEC issues, 4 COMP issues, 5 LEGAL issues, and 8 MISS items remain untouched by the UI rework.

### 🆕 New infrastructure added (functional but incomplete)
1. **ThemeService** — Infrastructure exists but views don't use `{DynamicResource}` → non-functional
2. **DialogService** — Exists as static class, not interface-based → untestable
3. **BulkObservableCollection** — Works but `Reset` notification clears selection
4. **Service Interfaces** — 5 interfaces created but never referenced → dead code
5. **FeatureFlags** — Gates commands that have no handlers → no-op

---

## COMBINED MASTER PRIORITY TABLE

| Priority | Count | Finding IDs |
|:---------|:------|:------------|
| **P0 — Fix Immediately** | 5 | BUG-01, BUG-11, NEW-01, SEC-01, LEGAL-02 |
| **P1 — Fix Before Release** | 18 | BUG-02, BUG-07, BUG-14, BUG-18, NEW-02, NEW-04, NEW-05, NEW-06, ERR-01, ERR-02, ERR-03, ERR-08, ERR-14, FEAT-02, FEAT-06, FEAT-15, FEAT-18, SEC-06 |
| **P2 — Fix Soon** | ~40 | All P2 items across categories |
| **P3 — Polish** | ~50 | All P3 items across categories |
| **TOTAL** | **~115** | |

