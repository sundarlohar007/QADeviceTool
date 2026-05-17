# QADeviceTool — Master Implementation Plan (Post-UI-Rework)

> **Source:** [AUDIT-FINDINGS.md](file:///d:/OpenCode/QAQC/QADeviceTool/.planning/AUDIT-FINDINGS.md)
> **Last Updated:** 2026-05-17 (re-audited after UI rework)
> **Total findings:** ~115 (18 original BUGs + 20 UX + 23 ERR + 21 FEAT + 13 SEC + 4 COMP + 5 LEGAL + 8 MISS + 15 NEW)
> **Strategy:** 9 waves. Wave 0 (new) handles critical UI rework regressions first.

---

## Guiding Principles

1. **Fix regressions before improvements.** Wave 0 addresses NEW bugs before touching original findings.
2. **Never change two coupled systems in the same commit.**
3. **Every fix gets a manual smoke test.**
4. **Fixes that share a file are grouped.**
5. **No architectural refactors until all behavioral bugs are fixed.**

---

## WAVE 0 — Critical UI Rework Regressions (8 tasks)

> **Risk:** HIGH. These are regressions introduced by the UI rework that must be fixed immediately.
> **Estimated time:** 1.5 hours
> **Checkpoint:** App launches with single VM, theme switch doesn't kill sessions, navigation works from command palette.

---

### W0-01 · Fix double MainViewModel instantiation (CRITICAL)

**Fixes:** NEW-01
**File:** `MainWindow.xaml` → lines 120-122
**Change:** Remove the `<Window.DataContext>` block entirely:

```xml
<!-- REMOVE THESE 3 LINES: -->
<Window.DataContext>
    <vm:MainViewModel />
</Window.DataContext>
```

Keep the `App.xaml.cs` line 74 assignment: `mw.DataContext = new LogPro.ViewModels.MainViewModel();`
**Verify:** Launch app → check Task Manager → only ONE set of ADB polling processes.
**Regression guard:** NONE — removing duplicate creation.

---

### W0-02 · Fix ThemeService destroying sessions on theme switch

**Fixes:** NEW-02, NEW-15
**File 1:** `MainWindow.xaml.cs` → `Close_Click` (lines 100-105)
**Change:**

```csharp
public bool IsThemeSwitching { get; set; }

private void Close_Click(object sender, RoutedEventArgs e)
{
    if (!IsThemeSwitching && DataContext is MainViewModel vm)
        vm.Cleanup();
    Close();
}
```

**File 2:** `ThemeService.cs` → `SwitchTheme` (lines 53-79)
**Change:** Set the flag before closing old window, and fix the duplicated null check:

```csharp
if (oldWindow is MainWindow mw)
    mw.IsThemeSwitching = true;

// Fix duplicated check at line 63:
if (dataContext != null) newWindow.DataContext = dataContext;

newWindow.Show();
if (oldWindow != null)
{
    oldWindow.DataContext = null;
    oldWindow.Close();
}
```

**Verify:** Switch theme while capturing logs → capture continues on new window.

---

### W0-03 · Fix ThemeService static constructor race

**Fixes:** NEW-03
**File:** `ThemeService.cs` → lines 33-36
**Change:**

```csharp
// BEFORE (static constructor):
static ThemeService()
{
    _currentTheme = PreferencesService.Current.ThemePreference ?? ThemeDark;
}

// AFTER:
static ThemeService() { }

public static void ApplyStartupTheme(Application app)
{
    _currentTheme = PreferencesService.Current.ThemePreference ?? ThemeDark;
    LoadThemeDictionary(app.Resources.MergedDictionaries, _currentTheme);
}
```

**Verify:** Set theme to Light → restart → app starts in Light theme.

---

### W0-04 · Fix Command Palette "devices" navigation route

**Fixes:** NEW-04
**File:** `MainViewModel.cs` → `Navigate` switch (lines 166-180)
**Change:** Add plural aliases:

```csharp
CurrentView = normalized switch
{
    "dashboard" => DashboardVM,
    "sessions" => SessionVM,
    "device" or "devices" => DeviceVM,
    "apps" => AppManagementVM,
    "shell" => ShellVM,
    "deeplink" => DeepLinkVM,
    "vitals" => VitalsVM,
    "files" => FileExplorerVM,
    "macros" => MacroVM,
    "stresstest" => StressTestVM,
    "settings" => SettingsVM,
    _ => DashboardVM
};
```

**Verify:** Open command palette → "Go to Devices" → Devices view shown.

---

### W0-05 · Fix Command Palette broken emoji icons

**Fixes:** UX-07 (worsened)
**File:** `MainWindow.xaml.cs` → lines 33-59
**Change:** Replace corrupted emoji strings with Segoe MDL2 Assets glyphs:

```csharp
_commandPalette.AddCommand("nav:dashboard", "Go to Dashboard", "Navigate to dashboard view", "\uE80F", "Ctrl+1");
_commandPalette.AddCommand("nav:devices", "Go to Devices", "Navigate to devices view", "\uE8EA", "Ctrl+2");
_commandPalette.AddCommand("nav:sessions", "Go to Sessions", "Navigate to sessions view", "\uE7C3", "Ctrl+3");
_commandPalette.AddCommand("nav:apps", "Go to Apps", "Navigate to app management", "\uECAA", "Ctrl+4");
_commandPalette.AddCommand("nav:files", "Go to Files", "Navigate to file explorer", "\uE838", "Ctrl+5");
_commandPalette.AddCommand("nav:shell", "Go to Shell", "Navigate to ADB shell", "\uE756", "Ctrl+6");
_commandPalette.AddCommand("nav:vitals", "Go to Vitals", "Navigate to device vitals", "\uE9D9", "Ctrl+7");
_commandPalette.AddCommand("nav:settings", "Go to Settings", "Navigate to settings", "\uE713", "Ctrl+,");
_commandPalette.AddCommand("action:newSession", "Start New Session", "Start capturing logs for selected device", "\uE7C3");
_commandPalette.AddCommand("action:screenshot", "Take Screenshot", "Capture screenshot from device", "\uE722");
_commandPalette.AddCommand("action:mirror", "Start Mirror", "Start screen mirroring", "\uE7F4");
_commandPalette.AddCommand("action:refresh", "Refresh Devices", "Refresh connected device list", "\uE72C");
_commandPalette.AddCommand("export:csv", "Export to CSV", "Export current session to CSV", "\uE78C");
_commandPalette.AddCommand("export:json", "Export to JSON", "Export current session to JSON", "\uE78C");
```

---

### W0-06 · Fix all "LogPro" branding in UI-visible locations

**Fixes:** UX-13 (worsened), NEW-14, NEW-20, ERR-18, NEW-06
**Files & Changes:**
- `MainWindow.xaml` line 8: `Title="QADeviceTool"`
- `MainWindow.xaml` line 140: `Text="QADeviceTool"`
- `MainWindow.xaml` line 163: `Text="QADeviceTool"`
- `MainWindow.xaml` line 164: `Text="Device Terminal v3.0"`
- `MainWindow.xaml` line 16: `Icon="QAQCDeviceIcon.ico"` (fix icon path, NEW-12)
- `SettingsView.xaml` line 306: `Text="QADeviceTool"`
- `App.xaml.cs` line 10: `"QADeviceTool_startup-debug.log"`
- `App.xaml.cs` line 88: `"QADeviceTool - Application Starting"`
- `App.xaml.cs` line 108: `"QADeviceTool - Error"`
- `SessionView.xaml.cs` lines 13-15: Change path to `"QAQCDeviceTool", "debug.log"` (or better, remove the rogue logger — see W1-09)

---

### W0-07 · Fix MainWindow.xaml.cs formatting

**Fixes:** NEW-11
**File:** `MainWindow.xaml.cs` → lines 14-16
**Change:**

```csharp
public MainWindow()
{
    InitializeComponent();
    PreviewKeyDown += OnPreviewKeyDown;
}
```

---

### W0-08 · Fix SettingsView ComboBox dark theme styling

**Fixes:** NEW-13
**File:** `Views/SettingsView.xaml` → lines 181-184
**Change:** Add a dark-themed ComboBox style to UserControl.Resources matching the existing `DarkInput` pattern, and apply it to the ComboBox.

---

## ✅ WAVE 0 CHECKPOINT

Build + launch. Single VM instance. Theme switch preserves sessions. Command palette icons render. All navigation routes work. Branding shows "QADeviceTool".

---

## WAVE 1 — Zero-Risk 1-Line Fixes (15 tasks)

> **Risk:** NONE. Unchanged from original plan.
> **Estimated time:** 30 minutes

### W1-01 · Fix log cleanup file extension glob
**Fixes:** BUG-06 — Change `"*.log"` to `"*.txt"` + `"*.log"` in PreferencesService.cs line 141.

### W1-02 · Fix ADB pair command syntax
**Fixes:** BUG-11 — Remove `--code` flag from AdbService.cs line 753.

### W1-03 · Fix macro directory path
**Fixes:** BUG-09 — Change `"LogPro"` to `"QAQCDeviceTool"` in MacroViewModel.cs + migration.

### W1-04 · Fix early startup log branding
**Fixes:** ERR-18 — Already covered in W0-06.

### W1-05 · Remove hardcoded pairing port scan
**Fixes:** BUG-10 — Return empty list from `DiscoverPairingPortsAsync`.

### W1-06 · Fix session restart append separator
**Fixes:** FEAT-21 — Add restart separator in SessionService.cs.

### W1-07 · Fix uninstall error message
**Fixes:** FEAT-07 — Return actual ADB output in AdbService.cs.

### W1-08 · Remove PATH dump from early startup log
**Fixes:** SEC-05 — Replace full PATH with count in App.xaml.cs line 60.

### W1-09 · Remove SessionView rogue logger
**Fixes:** ERR-17, NEW-07 — Delete `Log()` method and `LogPath` from SessionView.xaml.cs. Replace with `Services.AppLogger.Log.Debug(...)`.

### W1-10 · Fix IsSafePath: block pipe, ampersand, newline
**Fixes:** SEC-08, MISS-06 — Tighten validation in AdbService.cs.

### W1-11 · Fix hash truncation (8 → 16 hex chars)
**Fixes:** SEC-12 — Change `[..8]` to `[..16]` in SecurityHelper.cs.

### W1-12 · Fix settings.json raw serial keys
**Fixes:** SEC-06 — Hash serial before using as dict key in PreferencesService.cs.

### W1-13 · Add monkey args for device compatibility
**Fixes:** FEAT-12 — Add `--ignore-security-exceptions` etc. to StressTestViewModel.cs.

### W1-14 · Add monkey percentage validation
**Fixes:** MISS-01 — Add total check in StressTestViewModel.cs.

### W1-15 · Add iOS guard to StressTest
**Fixes:** MISS-02 — Add platform check in StressTestViewModel.cs.

---

## WAVE 2 — Add Logging to Silent Catch Blocks (4 batch tasks)

> **Risk:** NONE. Unchanged from original plan. Only adds `AppLogger.Log` calls.
> **Estimated time:** 45 minutes

### W2-01 · Replace silent catch blocks with logged catches
**Fixes:** ERR-01, ERR-04, ERR-07, ERR-16, ERR-21, ERR-22, ERR-23

### W2-02 · Add AppLogger to ViewModel catch blocks
**Fixes:** ERR-02

### W2-03 · Add navigation + operation logging
**Fixes:** ERR-15, ERR-11

### W2-04 · Fix Debug.WriteLine and inconsistent logger names
**Fixes:** ERR-06, ERR-17

---

## WAVE 3 — Process & Concurrency Bug Fixes (8 tasks)

> **Risk:** LOW-MEDIUM. These fix process lifecycle and thread-safety bugs.
> **Estimated time:** 1.5 hours
> **Checkpoint:** Run logcat capture for 5 minutes without freeze. Switch devices mid-capture. Verify no ghost processes.

### W3-01 · Fix ToolLauncher stdout deadlock
**Fixes:** BUG-01
**File:** `Helpers/ToolLauncher.cs` — `StartLongRunning`
**Change:** Add `drainStdout` parameter (default `true`). When true, call `process.BeginOutputReadLine()` with a no-op handler. When false (SessionService caller), skip it.
**Verify:** Run monkey for 2 minutes → process doesn't freeze.
**Regression guard:** Verify SessionService still receives logcat lines.

### W3-02 · Remove AdbService semaphore
**Fixes:** BUG-02
**File:** `Services/AdbService.cs`
**Change:** Remove `_adbSemaphore` field and all `WaitAsync`/`Release` calls.
**Verify:** Run logcat + list packages + install APK simultaneously.

### W3-03 · Fix SessionService flush timer race
**Fixes:** BUG-03
**File:** `Services/SessionService.cs`
**Change:** Add `private readonly object _bufferLock = new();`. Wrap all `_buffer` access in `lock(_bufferLock)`. In `StopCapture`: acquire lock → dispose timer → flush → close stream.
**Verify:** Start/stop capture rapidly 10 times → no `ObjectDisposedException`.

### W3-04 · Fix DeviceMonitorService initial broadcast
**Fixes:** BUG-04
**File:** `Services/DeviceMonitorService.cs`
**Change:** Add `_isFirstPoll = true` flag. Force `DevicesChanged` fire on first poll regardless of comparison.
**Verify:** Launch app with device connected → device appears in list immediately.

### W3-05 · Fix CrashDetector thread safety
**Fixes:** BUG-12
**File:** `Services/CrashDetector.cs`
**Change:** Replace `HashSet<string>` with `ConcurrentDictionary<string, byte>`.

### W3-06 · Remove IosService lock
**Fixes:** BUG-18
**File:** `Services/IosService.cs`
**Change:** Remove `_ipcLock` field and all `WaitAsync`/`Release` calls.

### W3-07 · Add DeviceMonitorService debounce
**Fixes:** BUG-16
**File:** `Services/DeviceMonitorService.cs`
**Change:** Add `Dictionary<string, int> _missCount`. Increment on missing device, reset on seen. Only fire disconnect after 3 misses.
**Verify:** Briefly unplug/replug USB → device stays in list.

### W3-08 · Fix SessionService.StopCapture double-call guard
**Fixes:** BUG-13
**File:** `Services/SessionService.cs`
**Change:** Add `if (!_isCapturing) return;` at top of `StopCapture()`.

---

## WAVE 4 — Error Handling & Crash Resilience (8 tasks)

> **Risk:** LOW. Only adds error handling and diagnostic capability.
> **Estimated time:** 1 hour
> **Checkpoint:** Trigger errors intentionally → check log file contains full diagnostics.

### W4-01 · Add crash report generation
**Fixes:** ERR-03
**File:** `App.xaml.cs`
**Change:** In `DispatcherUnhandledException`, generate `crash-report-{timestamp}.txt` containing: exception type/message/stack, OS version, app version, loaded assemblies, last 50 log lines.

### W4-02 · Add exit code logging to ToolLauncher
**Fixes:** ERR-08
**File:** `Helpers/ToolLauncher.cs`
**Change:** Log `process.ExitCode` after `WaitForExit`. Return exit code in result.

### W4-03 · Capture IosService stderr
**Fixes:** ERR-14
**File:** `Services/IosService.cs`
**Change:** Set `RedirectStandardError = true`. Read stderr alongside stdout. Log stderr as warning.

### W4-04 · Fix global exception handler UX
**Fixes:** ERR-13
**File:** `App.xaml.cs`
**Change:** Replace raw `ex.Message` with user-friendly dialog: "An unexpected error occurred. Details have been saved to the log file."

### W4-05 · Surface ScrcpyService start failures
**Fixes:** ERR-05
**File:** `Services/ScrcpyService.cs`
**Change:** Catch exceptions in `StartMirroring`, set `LastError` property for DeviceViewModel to display.

### W4-06 · Fix PreferencesService corrupted file backup
**Fixes:** ERR-12
**File:** `Services/PreferencesService.cs`
**Change:** In `Load()`, if deserialization fails, copy corrupted file to `settings.json.corrupt.{timestamp}` before overwriting.

### W4-07 · Log DeviceMonitorService poll failures
**Fixes:** ERR-07
**Change:** Add `AppLogger.Log.Warn("ADB devices poll failed", ex)` and set `IsHealthy = false`.

### W4-08 · Log DependencyChecker resolution failures
**Fixes:** ERR-19
**Change:** Add `AppLogger.Log.Warn(ex, $"Failed to resolve {toolName}")` in catch block.

---

## WAVE 5 — UI/UX Button States & Visual Fixes (12 tasks)

> **Risk:** LOW. XAML-only changes + minor ViewModel property additions.
> **Estimated time:** 1.5 hours
> **Checkpoint:** Click every button in valid/invalid states → correct enable/disable behavior. No crashes.

### W5-01 · Session Start/Stop button state binding
**Fixes:** UX-01
**Files:** `SessionView.xaml`, `SessionViewModel.cs`
**Change:** Bind Start `IsEnabled` to `!IsCapturing`. Bind Stop `IsEnabled` to `IsCapturing`.

### W5-02 · Mirror/Stop Mirror mutual exclusion
**Fixes:** UX-02
**Files:** `DeviceView.xaml`, `DeviceViewModel.cs`
**Change:** Bind Mirror `IsEnabled` to `!IsMirroring`. Bind Stop `IsEnabled` to `IsMirroring`.

### W5-03 · Delete operation confirmation dialogs
**Fixes:** UX-04
**Files:** `SessionViewModel.cs`, `MacroViewModel.cs`
**Change:** Add `if (!DialogService.Confirm("Delete this item?")) return;` before destructive operations.

### W5-04 · Uninstall button guard
**Fixes:** UX-10
**File:** `AppManagementView.xaml`
**Change:** Bind `IsEnabled="{Binding SelectedApp, Converter={StaticResource NotNullConverter}}"`.

### W5-05 · File Explorer button guards
**Fixes:** UX-11
**File:** `FileExplorerView.xaml`
**Change:** Bind Push/Pull/Delete `IsEnabled` to `SelectedFile != null`.

### W5-06 · Macro Play/Stop/Delete guards
**Fixes:** UX-12
**File:** `MacroView.xaml`
**Change:** Bind `IsEnabled` to `SelectedMacro != null`.

### W5-07 · Dashboard device status color
**Fixes:** UX-14
**File:** `DashboardView.xaml`
**Change:** Bind status dot color to device connection state via converter.

### W5-08 · Shell output auto-scroll
**Fixes:** UX-20
**File:** `ShellView.xaml`
**Change:** Add `ScrollViewer` with auto-scroll behavior on content change.

### W5-09 · StressTest event count validation
**Fixes:** MISS-03
**Change:** Clamp event count to 1-1,000,000. Show warning above 100,000.

### W5-10 · Dashboard quick action device guard
**Fixes:** MISS-04
**Change:** Disable quick action buttons when no device is connected.

### W5-11 · App version in UI
**Fixes:** MISS-08
**Change:** Show assembly version in Settings About and status bar.

### W5-12 · StressTest percentage display
**Fixes:** UX-19
**Change:** Show total percentage sum. Warning color when ≠ 100%.

---

## WAVE 6 — Feature-Level Bug Fixes (14 tasks)

> **Risk:** MEDIUM. Changes functional behavior.
> **Estimated time:** 3 hours
> **Checkpoint:** Each feature works end-to-end with correct status reporting.

### W6-01 · Fix deep link success detection
**Fixes:** FEAT-15
**File:** `DeepLinkViewModel.cs`
**Change:** Check for `"Starting: Intent"` in ADB output instead of current wrong indicator.

### W6-02 · Fix APK install status detection
**Fixes:** FEAT-06
**File:** `AdbService.cs`
**Change:** Check **last line** of output for `"Failure"` or `"Error"`. Return structured result.

### W6-03 · Fix SaveLog to copy raw file
**Fixes:** FEAT-02
**File:** `SessionViewModel.cs`
**Change:** Copy the on-disk raw log file to save destination instead of serializing in-memory data.

### W6-04 · Fix live log search debounce
**Fixes:** FEAT-03
**Change:** Add 300ms debounce on search TextChanged. Apply filter only to new entries.

### W6-05 · Fix live log trim for BulkObservableCollection
**Fixes:** FEAT-01
**Change:** Add `RemoveRange` to `BulkObservableCollection`. Use batch removal with single Reset notification.

### W6-06 · Fix delete session capture check
**Fixes:** FEAT-05
**Change:** Check `IsCapturing` before delete. Show warning dialog.

### W6-07 · Fix Android permission denied UX
**Fixes:** FEAT-09
**Change:** Detect "Permission denied" in `ls` output. Show message. Default to `/sdcard/`.

### W6-08 · Fix iOS ParseAfcLs heuristic
**Fixes:** FEAT-10
**File:** `IosService.cs`
**Change:** Replace `!name.Contains('.')` with proper directory detection.

### W6-09 · Fix VitalsViewModel lifecycle
**Fixes:** BUG-05
**File:** `VitalsViewModel.cs`
**Change:** Add `StartPolling()`/`StopPolling()` methods. Call from MainViewModel on navigation.

### W6-10 · Fix ShellViewModel string concat
**Fixes:** BUG-07
**File:** `ShellViewModel.cs`
**Change:** Replace `ShellOutput +=` with `StringBuilder`. Use `DispatcherTimer` (100ms) to copy to bound property.

### W6-11 · Fix AppManagementViewModel string concat
**Fixes:** BUG-14
**File:** `AppManagementViewModel.cs`
**Change:** Same `StringBuilder` pattern as W6-10.

### W6-12 · Fix macro replay timing drift
**Fixes:** FEAT-18
**File:** `MacroService.cs`
**Change:** Use `Stopwatch` for elapsed time. Adjust delays to compensate for ADB overhead.

### W6-13 · Fix uninstall error display
**Fixes:** FEAT-07
**Change:** Return actual ADB output instead of generic message.

### W6-14 · Fix session restart separator
**Fixes:** FEAT-21
**Change:** Write `--- SESSION RESTARTED ---` line on restart.

---

## WAVE 7 — Security, Compliance & Legal (10 tasks)

> **Risk:** LOW-MEDIUM. Mostly additive (new checks/filters).
> **Estimated time:** 2 hours
> **Checkpoint:** Enable Secure Mode → check logs contain no package names or serials.

### W7-01 · Implement Secure Mode for log redaction
**Fixes:** SEC-01
**File:** `Helpers/ToolLauncher.cs`, `Services/AppLogger.cs`
**Change:** Add `SecureMode` toggle in PreferencesService. When enabled: hash package names in logs, strip query params from deep links.

### W7-02 · Add session data retention cleanup
**Fixes:** COMP-01, SEC-02, SEC-03
**Change:** On startup, delete session files older than `LogRetentionDays`. Include screenshots and recordings.

### W7-03 · Fix ClearAllData to include sessions
**Fixes:** COMP-04
**File:** `PreferencesService.cs`
**Change:** Also delete `SessionsRootDirectory` contents.

### W7-04 · Filter getprop output in bug reports
**Fixes:** SEC-04
**File:** `SessionViewModel.cs`
**Change:** Whitelist only relevant `getprop` keys (model, OS version, build number).

### W7-05 · Add wireless ADB security warning
**Fixes:** SEC-07
**Change:** Show confirmation dialog before `adb tcpip 5555`.

### W7-06 · Fix iOS path quoting
**Fixes:** SEC-09
**File:** `IosService.cs`
**Change:** Quote all path arguments in pymobiledevice3 commands.

### W7-07 · Update THIRD_PARTY_NOTICES.txt
**Fixes:** LEGAL-01, LEGAL-03
**Change:** Add entries for ADB, scrcpy (with Apache NOTICE), and all NuGet packages.

### W7-08 · Document pymobiledevice3 GPL strategy
**Fixes:** LEGAL-02
**Change:** Document process-isolation model. Ensure pymobiledevice3 is invoked as subprocess, never bundled.

### W7-09 · Audit NuGet licenses
**Fixes:** LEGAL-04
**Change:** Run `dotnet nuget list --include-transitive`. Verify all compatible.

### W7-10 · Add LICENSE file
**Fixes:** LEGAL-05
**Change:** Add appropriate LICENSE file to project root.

---

## WAVE 8 — Theme System & New Infrastructure Fixes (10 tasks)

> **Risk:** MEDIUM. New wave for fixing the theme infrastructure.
> **Estimated time:** 3 hours


### W8-01 · Convert MainWindow.xaml to use DynamicResource
**Fixes:** NEW-05
Replace all hardcoded colors with theme resource references.

### W8-02 · Convert DashboardView.xaml to use DynamicResource
**Fixes:** NEW-06 (partial)

### W8-03 · Convert SettingsView.xaml to use DynamicResource
**Fixes:** NEW-06 (partial)

### W8-04 · Convert remaining views to use DynamicResource
**Fixes:** NEW-06 (complete)
All 12 views must reference theme resources instead of hardcoded colors.

### W8-05 · Wire up service interfaces to ViewModels
**Fixes:** NEW-08
Change VM constructors to accept interfaces instead of concrete types.

### W8-06 · Fix BulkObservableCollection selection reset
**Fixes:** NEW-10
Use `Add` action type with index instead of `Reset`, or preserve/restore selection.

### W8-07 · Wire up FeatureFlag commands
**Fixes:** NEW-09
Add handlers for `ai:analyze` and `action:selectAll` in `OnCommandExecuted`.

### W8-08 · Clean up dead code and unused imports
Remove unused converters (BooleanToPlayPauseConverter), verify all interface files are referenced.

### W8-09 · Add keyboard shortcuts (Ctrl+1 through Ctrl+7)
**Fixes:** UX-08
Add key handler cases in `MainWindow.xaml.cs`.

### W8-10 · Fix sidebar navigation active state sync
**Fixes:** UX-18
Bind RadioButton.IsChecked to SelectedNavItem comparison.

---

## WAVE 9 — Architecture & Enhancements (16 tasks)

> **Risk:** MEDIUM-HIGH. Unchanged from original plan.
> **Estimated time:** 4+ hours

W9-01 through W9-16 cover architecture improvements and polish. These are lowest priority and should only be done after all behavioral bugs are fixed.

### W9-01 · ToolResolver PATH deduplication and caching
**Fixes:** ToolResolver PATH pollution
**File:** `Helpers/ToolResolver.cs`
**Change:** Deduplicate PATH entries. Cache discovered tool paths per session.

### W9-02 · CrashDetector rolling limit
**File:** `Services/CrashDetector.cs`
**Change:** Limit `_detectedCrashes` to last 100 entries. Clear oldest when full.

### W9-03 · Add "View Logs" button in Settings
**Change:** Add button to open log directory in Explorer.

### W9-04 · Add loading indicators (IsBusy)
**Fixes:** UX-03
**Change:** Add `IsBusy` property to ViewModels. Show `ProgressRing` overlay during async ops.

### W9-05 · IDisposable on all ViewModels
**Fixes:** MISS-05
**Change:** Implement `IDisposable`. Unsubscribe from events, stop timers in `Dispose()`.

### W9-06 · Periodic stress test metrics sampling
**Fixes:** FEAT-13
**Change:** Sample CPU/memory every 5s during test. Aggregate min/max/avg.

### W9-07 · Improved stress test report
**Fixes:** FEAT-14
**File:** `StressReportBuilder.cs`
**Change:** Include time-series data, event counts, crash counts.

### W9-08 · Macro root detection warning
**Change:** Check if device has root access before recording macros via `sendevent`.

### W9-09 · ToolResolver fallback validation
**Change:** After finding a tool, verify it's executable by running `--version`.

### W9-10 · Wire NLog retention to preferences
**Fixes:** ERR-20
**Change:** Set `maxArchiveDays` from `PreferencesService.Current.LogRetentionDays` on startup.

### W9-11 · First-run privacy notice
**Fixes:** COMP-03
**Change:** Show notice on first launch about local data storage.

### W9-12 · Empty state illustrations
**Fixes:** UX-17
**Change:** Add instructional text/icons when lists are empty.

### W9-13 · iOS file pull implementation
**Fixes:** FEAT-11
**Change:** Implement `PullFileAsync` using `pymobiledevice3 afc pull`.

### W9-14 · iOS deep link implementation
**Fixes:** FEAT-16
**Change:** Implement `OpenUrlAsync` using `pymobiledevice3 apps open-url`.

### W9-15 · Data export/deletion for compliance
**Fixes:** COMP-02
**Change:** Add "Export My Data" and "Delete My Data" in Settings.

### W9-16 · Session file ACLs
**Fixes:** SEC-13
**Change:** Set file permissions to owner-only on session directories.

---

## MASTER TRACKING TABLE

| Wave | Tasks | Risk | Est. Time | Focus Area |
|:-----|:------|:-----|:----------|:-----------|
| **Wave 0** | 8 | HIGH | 1.5 hrs | **UI rework regressions** |
| **Wave 1** | 15 | NONE | 30 min | 1-line fixes, config, guards |
| **Wave 2** | 4 (batch) | NONE | 45 min | Logging in catch blocks |
| **Wave 3** | 8 | LOW-MED | 1.5 hrs | Process lifecycle, concurrency |
| **Wave 4** | 8 | LOW | 1 hr | Error handling, crash reports |
| **Wave 5** | 12 | LOW | 1.5 hrs | UI button states, XAML bindings |
| **Wave 6** | 14 | MEDIUM | 3 hrs | Feature bug fixes |
| **Wave 7** | 10 | LOW-MED | 2 hrs | Security, compliance, legal |
| **Wave 8** | 10 | MEDIUM | 3 hrs | **Theme system, new infrastructure** |
| **Wave 9** | 16 | MED-HIGH | 4+ hrs | Architecture, enhancements |
| **TOTAL** | **105** | — | **~19 hrs** | — |

---

## FINDING → WAVE CROSS-REFERENCE

| Finding ID | Wave | Task |
|:-----------|:-----|:-----|
| NEW-01 | W0 | W0-01 |
| NEW-02, NEW-15 | W0 | W0-02 |
| NEW-03 | W0 | W0-03 |
| NEW-04 | W0 | W0-04 |
| UX-07 | W0 | W0-05 |
| UX-13, NEW-14, ERR-18 | W0 | W0-06 |
| NEW-11 | W0 | W0-07 |
| NEW-13 | W0 | W0-08 |
| BUG-06 | W1 | W1-01 |
| BUG-11 | W1 | W1-02 |
| BUG-09 | W1 | W1-03 |
| BUG-10 | W1 | W1-05 |
| NEW-07, ERR-17 | W1 | W1-09 |
| BUG-01→18 (remaining) | W3 | W3-01→08 |
| ERR-01→23 | W2,W4 | W2-01→04, W4-01→08 |
| UX-01→20 (remaining) | W5 | W5-01→12 |
| FEAT-01→21 | W6 | W6-01→14 |
| SEC-01→13, COMP, LEGAL | W7 | W7-01→10 |
| NEW-05,06,08,09,10 | W8 | W8-01→10 |
| Architecture items | W9 | W9-01→16 |
.
