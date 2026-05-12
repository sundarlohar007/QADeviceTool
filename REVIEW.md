---
phase: 02-code-review
reviewed: 2026-05-11T14:30:00Z
depth: standard
files_reviewed: 5
files_reviewed_list:
  - src/QADeviceTool.App/Services/IosService.cs
  - src/QADeviceTool.App/Services/SessionService.cs
  - src/QADeviceTool.App/ViewModels/SessionViewModel.cs
  - src/QADeviceTool.App/Helpers/ToolLauncher.cs
  - src/QADeviceTool.App/Services/ProcessManagerService.cs
findings:
  critical: 4
  warning: 3
  info: 1
  total: 8
status: issues_found
---

# Phase 2: Code Review Report (LogPro v3.1.0)

**Reviewed:** 2026-05-11
**Depth:** Standard
**Files Reviewed:** 5
**Status:** issues_found

## Summary

The audit of the LogPro v3.1.0 codebase revealed several critical architectural flaws and regression issues. Most notably, the "multi-device logging" feature is severely broken due to a shared global log buffer that mixes data from all active sessions. Additionally, the promised performance improvements (specifically `BulkObservableCollection`) are completely missing from the implementation, leading to significant UI instability during heavy logging. Several process management issues also pose risks of resource leaks and data corruption.

## Critical Issues

### CR-01: Shared Log Buffer Causes Cross-Device Log Contamination
**File:** `src/QADeviceTool.App/Services/SessionService.cs:22`, `126`, `133`
**Issue:** The `SessionService` uses a single, global `ConcurrentQueue<string> _logBuffer` for ALL active log captures. When multiple devices are captured simultaneously, their log lines are interleaved into this single queue. The `FlushLogBuffer` timer then broadcasts this mixed data via a single `LogBatchReceived` event. This makes multi-device logging unusable as the UI shows a combined stream of logs from every connected device.
**Fix:**
Implement per-session buffers within the `CaptureContext` record and fire session-specific events:
```csharp
private record CaptureContext(
    Process Process, 
    StreamWriter Writer, 
    ConcurrentQueue<string> Buffer, // New isolated buffer
    LogSession Session, 
    CancellationTokenSource Cts
);
```

### CR-02: Missing BulkObservableCollection Implementation
**File:** `src/QADeviceTool.App/ViewModels/SessionViewModel.cs:31`, `361`
**Issue:** The implementation of `BulkObservableCollection.cs` (a high-priority v3.1.0 feature) is missing from the codebase. `SessionViewModel` is still using the standard `ObservableCollection<LogEntry>`. During log pruning (triggered at 200,000 lines), the code performs 150,000 sequential `.Add()` calls. Because `ObservableCollection` fires a `CollectionChanged` notification for every single addition, this results in a multi-second UI freeze, making the application appear hung.
**Fix:**
Re-implement or restore `BulkObservableCollection<T>` with a `AddRange` method that calls `OnCollectionChanged` only once at the end.

### CR-03: PyInstaller Process Tree Leak
**File:** `src/QADeviceTool.App/Services/SessionService.cs:157`
**Issue:** `StopCapture` calls `ctx.Process.Kill(entireProcessTree: false)`. The `pymobiledevice3.exe` tool is a PyInstaller standalone executable that spawns multiple Python child processes. Killing only the parent leaves these children orphaned. Over time, this leads to a massive accumulation of `pymobiledevice3` processes, leaking memory and potentially blocking USB ports.
**Fix:**
```csharp
// Change to true to clean up the entire process tree
try { ctx.Process.Kill(entireProcessTree: true); } catch { }
```

### CR-04: Brittle Regex Truncates iOS Device Info
**File:** `src/QADeviceTool.App/Services/IosService.cs:176`
**Issue:** The regex used to parse `pymobiledevice3 lockdown info` fails for values containing commas. The pattern `(?<v3>[^,\r\n}]+)` for unquoted values explicitly stops at the first comma. Since many Apple devices contain commas in their model names (e.g., "iPhone 15, Pro") or user-defined names, this data is truncated in the UI.
**Fix:**
Update the regex to be less restrictive for the trailing value or use the JSON output format exclusively:
```csharp
// Safer fallback regex that only stops at end-of-line or closing brace
new Regex(@"['""]?(?<key>[A-Za-z0-9]+)['""]?\s*[:=]\s*(?:'(?<v1>[^']*)'|""(?<v2>[^""]*)""|(?<v3>[^\r\n}]+))");
```

## Warnings

### WR-01: Handle Leak in ProcessManagerService
**File:** `src/QADeviceTool.App/Services/ProcessManagerService.cs:20`
**Issue:** The `Exited` event handler removes the process from tracking but fails to call `Dispose()`. This leaks OS process handles for every external tool that exits naturally or crashes, which can eventually lead to system resource exhaustion in long-running QA sessions.
**Fix:**
```csharp
process.Exited += (s, e) =>
{
    if (_trackedProcesses.TryRemove(id, out var p))
    {
        p.Dispose();
    }
};
```

### WR-02: Non-Graceful Screen Record Stop on Windows
**File:** `src/QADeviceTool.App/Services/AdbService.cs:302`
**Issue:** `process.Kill(false)` does not send a graceful `SIGINT` on Windows. It forcefully terminates the `screenrecord` process. This often prevents the MP4 header from being finalized, resulting in a corrupted or unplayable video file on the device/local machine.
**Fix:**
Use a more graceful termination method for ADB shell processes, such as sending a 'CTRL+C' signal via native Windows APIs or using `adb shell kill -2 <PID>` before closing the process.

### WR-03: Heavy UI Refresh on Bookmark Toggles
**File:** `src/QADeviceTool.App/ViewModels/SessionViewModel.cs:574`
**Issue:** Toggling a bookmark calls `LogEntriesView.Refresh()`. Because the log view can contain up to 200,000 items, `Refresh()` forces WPF to re-evaluate filtering and sorting for the entire set on the UI thread, causing a noticeable "stutter" whenever a user marks a log line.
**Fix:**
Implement `INotifyPropertyChanged` on `LogEntry` so the UI can update the specific bound row without a full collection refresh.

## Info

### IN-01: Missing INotifyPropertyChanged on LogEntry
**File:** `src/QADeviceTool.App/Models/LogEntry.cs`
**Issue:** `LogEntry` uses auto-properties without change notification. This prevents the UI from reacting to property changes (like `IsBookmarked`) through standard data binding, necessitating the inefficient workarounds (like manual `Refresh()`) noted in WR-03.
**Fix:** Use `CommunityToolkit.Mvvm` `ObservableObject` or manually implement `INotifyPropertyChanged` for the `LogEntry` class.

---
_Reviewed: 2026-05-11 14:30_
_Reviewer: gsd-code-reviewer_
_Depth: standard_
