---
phase: 02-code-review-viewmodels
reviewed: 2026-05-05T00:00:00Z
depth: deep
files_reviewed: 12
files_reviewed_list:
  - src/QADeviceTool.App/ViewModels/SessionViewModel.cs
  - src/QADeviceTool.App/ViewModels/MainViewModel.cs
  - src/QADeviceTool.App/ViewModels/AppManagementViewModel.cs
  - src/QADeviceTool.App/ViewModels/DashboardViewModel.cs
  - src/QADeviceTool.App/ViewModels/DeviceViewModel.cs
  - src/QADeviceTool.App/ViewModels/FileExplorerViewModel.cs
  - src/QADeviceTool.App/ViewModels/MacroViewModel.cs
  - src/QADeviceTool.App/ViewModels/StressTestViewModel.cs
  - src/QADeviceTool.App/ViewModels/ShellViewModel.cs
  - src/QADeviceTool.App/ViewModels/VitalsViewModel.cs
  - src/QADeviceTool.App/ViewModels/DeepLinkViewModel.cs
  - src/QADeviceTool.App/ViewModels/SettingsViewModel.cs
findings:
  critical: 1
  warning: 9
  info: 4
  total: 14
status: issues_found
---

# Phase 02: Code Review Report -- ViewModels

**Reviewed:** 2026-05-05
**Depth:** deep (cross-file analysis, call-chain tracing, threading audit)
**Files Reviewed:** 12
**Status:** issues_found

## Summary

Reviewed all 12 ViewModel files across the LogPro v2.8.0 application. Focus areas: UI thread safety, `Dispatcher` usage correctness, `ObservableCollection` manipulation threading, memory leaks from event subscriptions, fire-and-forget async patterns, `MessageBox` in ViewModels, direct service instantiation, and method length/complexity.

**Key concerns:** One stale-data race condition in FileExplorerViewModel where a `CancellationTokenSource` is created/cancelled but its token is never consumed by any async operation. Widespread `MessageBox.Show()` calls in ViewModels break MVVM testability. Every ViewModel subscribes to `DeviceMonitorService.DevicesChanged` but none unsubscribe, creating potential issues if ViewModel lifetimes change. Several empty `catch { }` blocks silently swallow exceptions in critical code paths.

---

## Critical Issues

### CR-01: CancellationTokenSource is created and cancelled but token never consumed -- stale data race

**File:** `src/QADeviceTool.App/ViewModels/FileExplorerViewModel.cs:19,80-83,115`
**Issue:** A `CancellationTokenSource _loadCts` field is declared (line 19) and a new one is created/cancelled in `OnDeviceSelected` (lines 80-83). However, the token is never passed to `LoadDirectoryAsync` (line 115 via `OnSelectedDeviceChanged`), which does not accept a `CancellationToken` parameter. The `Cancel()` call on line 80 is a no-op.

When a user rapidly switches devices (or `MainViewModel` propagates device changes), a slow `ListDirectoryAsync` call from the previous device may complete *after* the UI has switched to the new device. The stale task then overwrites `Files`, `CurrentPath`, and `StatusMessage` with data from the wrong device:
```csharp
// OnDeviceSelected (line 78-83)
public void OnDeviceSelected(DeviceInfo device)
{
    _loadCts?.Cancel();              // <-- no-op: token never consumed
    SelectedDevice = device;
    _loadCts = new CancellationTokenSource();
}

// OnSelectedDeviceChanged (line 115)
_ = Task.Run(async () => { try { await LoadDirectoryAsync(CurrentPath); } catch { } });

// LoadDirectoryAsync captures device at start but awaits without cancellation (line 127-138)
private async Task LoadDirectoryAsync(string path)
{
    var device = SelectedDevice;  // snapshot, but no cancellation mid-flight
    ...
    loadedFiles = await _adbService.ListDirectoryAsync(device.Serial, path); // no token
    _dispatcher.Invoke(() => { Files.Clear(); ... });  // STALE overwrite
}
```

**Fix:** Either pass the token through to `LoadDirectoryAsync` and have it check `_loadCts.Token.ThrowIfCancellationRequested()` after the await, or guard against stale completion by checking that `SelectedDevice` still matches the captured `device` before dispatching the UI update:

```csharp
private async Task LoadDirectoryAsync(string path, CancellationToken ct = default)
{
    if (SelectedDevice == null) return;
    IsLoading = true;
    var device = SelectedDevice;
    try
    {
        // ... await with ct passed through ...
        ct.ThrowIfCancellationRequested();
        _dispatcher.Invoke(() =>
        {
            // Guard: if device changed during the await, discard
            if (SelectedDevice?.Serial != device.Serial) return;
            Files.Clear();
            // ...
        });
    }
    // ...
}
```

---

## Warnings

### WR-01: MessageBox.Show() in ViewModels breaks MVVM testability and separation

**File:** Multiple files -- 6 call sites across 5 ViewModels
**Issue:** `System.Windows.MessageBox.Show()` is called directly from ViewModel command handlers. This couples ViewModels to WPF presentation, making the ViewModels impossible to unit test without a UI thread and violating MVVM separation of concerns.

Call sites:
| File | Line | Command |
|---|---|---|
| `SessionViewModel.cs` | 822 | `DeleteSession` |
| `AppManagementViewModel.cs` | 242 | `UninstallAppAsync` |
| `AppManagementViewModel.cs` | 310 | `ClearAppDataAsync` |
| `FileExplorerViewModel.cs` | 296 | `DeleteFileAsync` |
| `MacroViewModel.cs` | 191 | `DeleteMacroAsync` |
| `SettingsViewModel.cs` | 112 | `ClearAllData` |

**Fix:** Introduce an `IDialogService` interface with a `ShowConfirmationAsync(string message, string title)` method, inject it into these ViewModels, and implement it in the WPF layer. This enables unit testing with a mock dialog service.

```csharp
public interface IDialogService
{
    Task<bool> ShowConfirmationAsync(string message, string title);
}

// Usage in ViewModel:
var confirm = await _dialogService.ShowConfirmationAsync(
    $"Delete session '{SelectedSession.Name}'?", "Delete Session");
if (!confirm) return;
```

---

### WR-02: Direct AdbService instantiation bypasses dependency injection

**File:** `src/QADeviceTool.App/ViewModels/SettingsViewModel.cs:70`
**Issue:** `SettingsViewModel` creates its own `AdbService` instance via `_adbService = new AdbService()` on line 70, while `AdbService` is already injected into `MainViewModel` and passed to all other child ViewModels. This creates a second `AdbService` instance with independent state (potentially a separate ADB server connection or process tracking). The Settings view's wireless ADB pairing, discovery, and connect/disconnect functions (lines 174-243) use this separate instance, which could diverge from the main app's `AdbService` state.

**Fix:** Pass `AdbService` through the constructor like every other ViewModel. Update `MainViewModel` line 81:

```csharp
// Before:
SettingsVM = new SettingsViewModel(_dependencyChecker, _sessionService);
// After:
SettingsVM = new SettingsViewModel(_adbService, _dependencyChecker, _sessionService);
```

Update `SettingsViewModel` constructor to accept `AdbService` as the first parameter instead of instantiating it.

---

### WR-03: Empty catch blocks silently swallow exceptions in critical paths

**File:** `src/QADeviceTool.App/ViewModels/SessionViewModel.cs:262,275`
**File:** `src/QADeviceTool.App/ViewModels/FileExplorerViewModel.cs:115`

**Issue:** Three empty `catch { }` blocks swallow all exceptions without any logging or user feedback:

1. **SessionViewModel:262** -- `OnDeviceDisconnected`: If `StopCaptureForDevice` throws, the `_isSubscribedToLogBatch` flag stays `true` and `IsCapturing` stays `true`. This means the next capture attempt will skip subscribing to `LogBatchReceived` because the flag is already set, and the UI will show incorrect capturing state.

2. **SessionViewModel:275** -- `LoadSessions`: If `GetSavedSessions` throws (e.g., corrupted session files), the sessions list silently stays empty with no user indication.

3. **FileExplorerViewModel:115** -- `OnSelectedDeviceChanged`: If `LoadDirectoryAsync` throws, the user sees no error and the file list remains from the previous device (or empty).

**Fix:** At minimum, log the exception. For the critical disconnect path (line 262), reset state even on failure:

```csharp
// SessionViewModel:262
catch (Exception ex)
{
    Services.AppLogger.Log.Debug(ex, "[SessionVM] OnDeviceDisconnected cleanup failed");
    _isSubscribedToLogBatch = false;
    IsCapturing = false;
    StatusMessage = $"[STOP] Device disconnected (cleanup warning)";
}
```

---

### WR-04: BeginInvoke with async lambda creates async void that could crash the app

**File:** `src/QADeviceTool.App/ViewModels/SessionViewModel.cs:202`
**Issue:** `_dispatcher.BeginInvoke(async () => { ... })` wraps an async lambda in `BeginInvoke`. In WPF, this produces an `async void` delegate dispatched to the UI thread. While the body is wrapped in `try/catch`, the `async void` nature means that any exception thrown in the continuation of `await _sessionService.StartCaptureAsync(...)` on line 217 -- *after* the try/catch scope, or from a code path not covered by the try/catch -- would bypass the exception handler and crash the application via `DispatcherUnhandledException`.

Currently the entire body is inside the `try` block, so the immediate risk is mitigated, but `async void` in `BeginInvoke` is a fragile pattern that future maintainers could easily break.

**Fix:** Convert to an async method called from the dispatcher:

```csharp
private void OnDeviceConnected(DeviceInfo device)
{
    if (!AutoCapture) return;
    _dispatcher.BeginInvoke(() => _ = OnDeviceConnectedAsync(device));
}

private async Task OnDeviceConnectedAsync(DeviceInfo device)
{
    try
    {
        // ... original body ...
    }
    catch (Exception ex)
    {
        StatusMessage = $"[!] Auto-capture error: {ex.Message}";
    }
}
```

---

### WR-05: DeviceMonitorService event handlers never unsubscribed across all ViewModels

**File:** All 12 ViewModels
**Issue:** Every ViewModel subscribes to `_deviceMonitor.DevicesChanged` (and in SessionViewModel, additionally `DeviceConnected` and `DeviceDisconnected`) in its constructor, but **none of them unsubscribe**. If any ViewModel's lifetime were to become shorter than the application's lifetime (e.g., future lazy-loading or tab recycling), the `DeviceMonitorService` would hold strong references to disposed ViewModels, causing memory leaks and attempting UI updates on disposed `Dispatcher` objects.

Currently the risk is low because all child ViewModels are created in `MainViewModel`'s constructor and live for the application lifetime. However, `MainViewModel.Cleanup()` (line 166) disposes `_deviceMonitor` without first unsubscribing child ViewModel handlers -- the handlers become dangling references to a disposed object's event.

**Fix:** Implement `IDisposable` on each ViewModel (or a common base class) and unsubscribe in `Dispose`:

```csharp
public void Dispose()
{
    _deviceMonitor.DevicesChanged -= OnDevicesChanged;
    // also DeviceConnected, DeviceDisconnected for SessionViewModel
}
```

Call `Dispose` from `MainViewModel.Cleanup()` before disposing `_deviceMonitor`.

---

### WR-06: DispatcherTimer in VitalsViewModel runs continuously with no visibility-aware lifecycle

**File:** `src/QADeviceTool.App/ViewModels/VitalsViewModel.cs:42-50`
**Issue:** A `DispatcherTimer` with a 3-second interval is created in the constructor and started via `TogglePolling`. Once started, the timer's `Tick` handler fires `_ = PollVitalsAsync()` every 3 seconds indefinitely -- even when the user has navigated to a different tab (Sessions, Devices, etc.). This wastes resources (CPU, ADB round-trips, battery on the connected device) and has no mechanism to pause when the Vitals view is not visible.

There is no `StopPolling()` call in any cleanup path; `MainViewModel.Cleanup()` does not stop the timer. The timer is only stopped when `SelectedDevice` becomes null or `TogglePolling` is toggled off.

**Fix:** Expose a `StopPolling()` method publicly (or implement `IDisposable`) and call it from `MainViewModel` during navigation or cleanup. Alternatively, introduce a `IsVisible` property that `MainViewModel` sets based on the active tab:

```csharp
// In MainViewModel.Navigate:
if (destination != "vitals") VitalsVM.PausePolling();
if (destination == "vitals" && VitalsVM.IsPolling) VitalsVM.ResumePolling();
```

---

### WR-07: Fire-and-forget Task.Run in constructors without error handling

**File:** `src/QADeviceTool.App/ViewModels/DashboardViewModel.cs:85-105`
**File:** `src/QADeviceTool.App/ViewModels/SettingsViewModel.cs:77-81`
**Issue:** Both constructors spawn `Task.Run(async () => { ... })` without observing the returned `Task`. If the background work throws an exception, it surfaces as `TaskScheduler.UnobservedTaskException` after garbage collection, which is a crash in most .NET configurations.

- **DashboardViewModel:85-105**: The outer `try/catch` catches exceptions in the synchronous lambda preamble, but if `await LoadToolStatusesAsync()` on line 99 throws, the exception propagates to the `Task.Run` wrapper and goes unobserved.
- **SettingsViewModel:77-81**: No error handling at all around the `Task.Run` lambda.

**Fix:** Add a top-level try/catch inside the `Task.Run` lambda:

```csharp
Task.Run(async () =>
{
    try
    {
        await CheckDependenciesAsync();
    }
    catch (Exception ex)
    {
        // Log and suppress -- startup helper, not critical
        Services.AppLogger.Log.Debug(ex, "[Settings] Startup dependency check failed");
    }
});
```

---

### WR-08: Dispatcher.Invoke used instead of BeginInvoke in async method paths, blocking background threads unnecessarily

**File:** `src/QADeviceTool.App/ViewModels/AppManagementViewModel.cs:57,134,188`
**File:** `src/QADeviceTool.App/ViewModels/DeviceViewModel.cs:63`
**File:** `src/QADeviceTool.App/ViewModels/FileExplorerViewModel.cs:58,142`
**File:** `src/QADeviceTool.App/ViewModels/DashboardViewModel.cs:152`
**File:** `src/QADeviceTool.App/ViewModels/ShellViewModel.cs:145`
**File:** `src/QADeviceTool.App/ViewModels/VitalsViewModel.cs:57,149`
**File:** `src/QADeviceTool.App/ViewModels/DeepLinkViewModel.cs:43`
**Issue:** Multiple ViewModels use `_dispatcher.Invoke()` (synchronous) for UI updates from async method callbacks, while `SessionViewModel` consistently uses `_dispatcher.BeginInvoke()` (asynchronous). The synchronous `Invoke` blocks the calling background thread until the UI thread processes the action. While this won't deadlock in the current architecture, it ties up thread pool threads unnecessarily and introduces cross-ViewModel inconsistency that could confuse maintainers.

The `BeginInvoke` pattern used in `SessionViewModel` is the preferred approach for non-critical UI updates from event handlers.

**Fix:** Standardize on `BeginInvoke` for all non-blocking UI updates across all ViewModels. Reserve `Invoke` only for cases where the caller must block until the UI update completes (not applicable in any of the current call sites).

---

### WR-09: SelectedDevice property set from background Dispatcher.Invoke callback -- redundant dispatching

**File:** `src/QADeviceTool.App/ViewModels/AppManagementViewModel.cs:67-72`
**File:** `src/QADeviceTool.App/ViewModels/ShellViewModel.cs:57-62`
**File:** `src/QADeviceTool.App/ViewModels/DeepLinkViewModel.cs:58-63`
**File:** `src/QADeviceTool.App/ViewModels/VitalsViewModel.cs:72-77`
**Issue:** Inside `_dispatcher.Invoke(() => { ... })`, several ViewModels set `SelectedDevice` which triggers `OnSelectedDeviceChanged`. This partial method fires side effects -- in many cases, it starts async operations (`LoadAppsAsync`, `LoadDirectoryAsync`, etc.). Since this property change originates from inside `Invoke`, the side effects run immediately on the UI thread, blocking it. `SessionViewModel` avoids this issue because it does not set `SelectedDevice` inside the dispatcher callback for `OnDevicesChanged`.

**Fix:** After setting `SelectedDevice`, if the partial method triggers heavy work, that work is already fire-and-forget (`_ = ...`), so the blocking is minimal. But for consistency and to avoid any risk of long-running synchronous work blocking the dispatcher, consider using `BeginInvoke` consistently (see WR-08).

---

## Info

### IN-01: GenerateBugReportAsync is 196 lines -- should be decomposed

**File:** `src/QADeviceTool.App/ViewModels/SessionViewModel.cs:594-790`
**Issue:** The `GenerateBugReportAsync` method at 196 lines handles screenshot capture, log dumping, crash snippet extraction, device info gathering, `dumpsys` sections, crash buffer reading, tombstone/ANR inspection, iOS details, screen recording copying, zip archiving, and temp file cleanup. This violates single-responsibility and makes the method difficult to test, debug, or modify.

**Fix:** Extract helper methods:
```csharp
private async Task<string> CaptureScreenshotForReport(DeviceInfo device, string saveDir, DateTime timestamp)
private async Task<string> DumpLogForReport(string saveDir, DateTime timestamp)
private async Task<string> CollectDeviceInfoForReport(DeviceInfo device, string saveDir, DateTime timestamp)
private async Task<string> CollectAndroidDiagnosticsForReport(DeviceInfo device, string saveDir, DateTime timestamp)
private async Task<string> CopyScreenRecordingForReport(string saveDir, DateTime timestamp)
```

---

### IN-02: LoadSessionLogSafeAsync is 76 lines -- should be split

**File:** `src/QADeviceTool.App/ViewModels/SessionViewModel.cs:1122-1198`
**Issue:** This method handles log file path resolution, fallback file search across two extensions, content loading, line parsing, memory trimming, and status updates. The file path resolution logic (lines 1129-1164) could be extracted.

**Fix:** Extract `ResolveLogFilePathAsync(LogSession session)` as a separate method.

---

### IN-03: Inconsistent dispatcher pattern across ViewModels creates maintenance hazard

**File:** Cross-cutting -- all 12 ViewModels
**Issue:** The codebase has no consistent convention for UI thread dispatch:
- `SessionViewModel` uses `BeginInvoke` everywhere (correct, non-blocking)
- `AppManagementViewModel`, `DeviceViewModel`, `FileExplorerViewModel`, `ShellViewModel`, `VitalsViewModel`, `DeepLinkViewModel` use `Invoke` for `OnDevicesChanged` (blocking)
- `DashboardViewModel` uses `BeginInvoke` for `OnDevicesChanged` but `Invoke` for `LoadToolStatusesAsync` (mixed)
- `SessionViewModel` uses `BeginInvoke` with explicit `DispatcherPriority.Background` in some places (lines 141, 335) but priority-omitted in others (line 174, 202, 245, 360)

**Fix:** Establish a convention (either a base class helper method or a coding standard) and apply it uniformly. The recommended pattern for all non-critical UI updates from background threads is `_dispatcher.BeginInvoke(DispatcherPriority.Normal, () => { ... })`.

---

### IN-04: Magic number for screen recording max duration

**File:** `src/QADeviceTool.App/ViewModels/SessionViewModel.cs:938`
**Issue:** `maxDurationSec: 180` is a hardcoded magic number for the screen recording cap. This should be a configurable constant or user setting.

**Fix:** Define as a named constant or expose as a user-configurable setting:
```csharp
private const int ScreenRecordMaxDurationSeconds = 180;
```

---

_Reviewed: 2026-05-05_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: deep_
