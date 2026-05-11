---
phase: Code-Review
reviewed: 2024-05-24T00:00:00Z
depth: standard
files_reviewed: 7
files_reviewed_list:
  - D:\OpenCode\QAQC\QADeviceTool\src\QADeviceTool.App\Services\AdbService.cs
  - D:\OpenCode\QAQC\QADeviceTool\src\QADeviceTool.App\Services\IosService.cs
  - D:\OpenCode\QAQC\QADeviceTool\src\QADeviceTool.App\Services\SessionService.cs
  - D:\OpenCode\QAQC\QADeviceTool\src\QADeviceTool.App\Services\ProcessManagerService.cs
  - D:\OpenCode\QAQC\QADeviceTool\src\QADeviceTool.App\Services\DeviceMonitorService.cs
  - D:\OpenCode\QAQC\QADeviceTool\src\QADeviceTool.App\Services\CrashDetector.cs
  - D:\OpenCode\QAQC\QADeviceTool\src\QADeviceTool.App\ViewModels\SessionViewModel.cs
findings:
  critical: 3
  warning: 3
  info: 1
  total: 7
status: issues_found
---
# Phase Code-Review: Code Review Report

**Reviewed:** 2024-05-24T00:00:00Z
**Depth:** standard
**Files Reviewed:** 7
**Status:** issues_found

## Summary

The QADeviceTool source files were audited with a primary focus on the `Services` and `ViewModels`. The review found significant issues with thread-safety, state management for concurrent logging, and unmanaged process handle cleanup. The most critical issue is a severe memory leak/performance bottleneck in `SessionService`'s shared log buffer design, which will inevitably crash the application with unbounded memory growth on high-throughput ADB logging. Additionally, `SessionViewModel` improperly multiplexes concurrent logging events.

## Critical Issues

### CR-01: Shared Unbounded Log Buffer Memory Leak and Multi-Session Bleed
**File:** `D:\OpenCode\QAQC\QADeviceTool\src\QADeviceTool.App\Services\SessionService.cs:33`
**Issue:** `_logBuffer` is a single shared `ConcurrentQueue<string>` for ALL concurrent sessions. `FlushLogBuffer()` is called every 200ms by a shared `_flushTimer` and dequeues a hardcoded maximum of 200 lines. Since `logcat` often produces thousands of lines per second, this buffer will grow indefinitely, resulting in a severe memory leak (`OutOfMemoryException`). Additionally, logs from multiple devices are mixed together into this single queue, completely breaking session separation.
**Fix:**
```csharp
// Give each session its own buffer and flush task, or remove the shared buffer. 
// If retaining a queue, flush ALL items currently in the queue rather than 200:
private void FlushLogBuffer(CaptureContext ctx)
{
    if (ctx.LogBuffer.IsEmpty) return;
    var batch = new System.Text.StringBuilder();
    while (ctx.LogBuffer.TryDequeue(out var line))
    {
        batch.AppendLine(line);
    }
    if (batch.Length > 0)
    {
        ctx.Session.FireLogBatchReceived(batch.ToString());
    }
}
```

### CR-02: NullReferenceException in StopAllCaptures during StartCaptureAsync
**File:** `D:\OpenCode\QAQC\QADeviceTool\src\QADeviceTool.App\Services\SessionService.cs:64` and `198`
**Issue:** `StartCaptureAsync` inserts `null!` into `_activeCaptures` to reserve the ID before awaiting the external tool process start: `if (!_activeCaptures.TryAdd(session.Id, null!)) return false;`. If `StopAllCaptures` (or `StopCapture`) executes during this `await` window, it will iterate over `_activeCaptures.Values` and throw a `NullReferenceException` when calling `kvp.Value.Cts.Cancel()`.
**Fix:**
```csharp
// Do not insert null. Build the CaptureContext first, or use a separate state dictionary.
// Alternatively, safely check for null in StopAllCaptures:
foreach (var kvp in _activeCaptures.ToList())
{
    if (kvp.Value == null) continue; 
    try { kvp.Value.Cts.Cancel(); /* ... */ } 
    catch { /* ... */ }
}
```

### CR-03: UI Subscription Breaking Concurrent Logging
**File:** `D:\OpenCode\QAQC\QADeviceTool\src\QADeviceTool.App\ViewModels\SessionViewModel.cs:338` and `440`
**Issue:** `SessionViewModel` uses a single boolean `_isSubscribedToLogBatch` to track its subscription to the global `_sessionService.LogBatchReceived` event. If multiple sessions are running, it subscribes once. However, when the user stops *any* single session, `StopCapture` un-subscribes entirely (`_sessionService.LogBatchReceived -= OnLogBatchReceived;`). This instantly halts UI log updates for all other devices still actively capturing. 
**Fix:**
Bind log events at the `LogSession` object level rather than via a global `SessionService` event. Alternatively, only unsubscribe if there are zero active captures remaining.

## Warnings

### WR-01: UI Thread Freeze on Bulk Collection Modification
**File:** `D:\OpenCode\QAQC\QADeviceTool\src\QADeviceTool.App\ViewModels\SessionViewModel.cs:361`
**Issue:** When pruning `LogEntries` to constrain memory, the code clears the `ObservableCollection` and adds up to 150,000 items sequentially via `foreach (var e in keep) LogEntries.Add(e);`. Because WPF's `ObservableCollection` fires `CollectionChanged` for every single `.Add()`, this will lock up the UI thread for seconds/minutes.
**Fix:**
Use a custom `BulkObservableCollection` that allows suppressing change notifications during bulk inserts, or replace the entire list instance.

### WR-02: Unmanaged Process Handle Leak
**File:** `D:\OpenCode\QAQC\QADeviceTool\src\QADeviceTool.App\Services\ProcessManagerService.cs:18`
**Issue:** `TrackProcess` subscribes to `process.Exited` and removes the tracked process from the dictionary. However, it fails to call `process.Dispose()` when the process terminates naturally. This leaks OS process handles until application shutdown.
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

### WR-03: TaskCanceledException in App PID Resolver Logs Spurious Errors
**File:** `D:\OpenCode\QAQC\QADeviceTool\src\QADeviceTool.App\Services\SessionService.cs:122`
**Issue:** `await Task.Delay(3000, cts.Token);` is used inside a `while` loop that resolves PIDs. When the session stops (`cts.Cancel()`), `Task.Delay` throws a `TaskCanceledException`. This is caught by a generic `catch (Exception ex)` block that logs `[AppLogger] Failed to resolve package PID`.
**Fix:**
Catch `OperationCanceledException` and swallow it cleanly without logging an error.

## Info

### IN-01: Cross-Contaminated Crash Detection State
**File:** `D:\OpenCode\QAQC\QADeviceTool\src\QADeviceTool.App\ViewModels\SessionViewModel.cs:351`
**Issue:** `OnLogBatchReceived` calls `_crashDetector.ScanLine(line, LogEntries.Count - 1, platform);` using `SelectedSession?.Platform ?? DevicePlatform.Android`. If a batch comes from an active iOS session while an Android session is selected in the UI, the iOS logs will be scanned using Android crash regexes. 
**Fix:** 
Ensure log lines or batches carry the origin platform/session so they can be processed appropriately regardless of the currently viewed session.