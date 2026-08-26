---
phase: Code-Review
fixed_at: 2026-05-11T11:32:00Z
review_path: D:\OpenCode\QAQC\QADeviceTool\REVIEW.md
iteration: 1
findings_in_scope: 7
fixed: 6
skipped: 1
status: partial
---
# Phase Code-Review: Code Review Fix Report

**Fixed at:** 2026-05-11T11:32:00Z
**Source review:** D:\OpenCode\QAQC\QADeviceTool\REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 7
- Fixed: 6
- Skipped: 1

## Fixed Issues

### CR-01: Shared Unbounded Log Buffer Memory Leak and Multi-Session Bleed
**Files modified:** `src/QADeviceTool.App/Services/SessionService.cs`, `src/QADeviceTool.App/Models/LogSession.cs`
**Commit:** fd9644a
**Applied fix:** Replaced global shared log buffer with per-session buffers in `CaptureContext`. Modified `LogSession` to fire `LogBatchReceived` with session instance context. Implemented individual background flush tasks for each session.

### CR-02: NullReferenceException in StopAllCaptures during StartCaptureAsync
**Files modified:** `src/QADeviceTool.App/Services/SessionService.cs`
**Commit:** fd9644a
**Applied fix:** `StartCapture` now initializes `CaptureContext` correctly and synchronously. A safety `if (kvp.Value == null)` check was added in `StopAllCaptures` to handle potential concurrent scenarios.

### CR-03: UI Subscription Breaking Concurrent Logging
**Files modified:** `src/QADeviceTool.App/ViewModels/SessionViewModel.cs`
**Commit:** 893eb89
**Applied fix:** Modified `SessionViewModel` to subscribe/unsubscribe directly from `SelectedSession.LogBatchReceived` or the specific auto-captured session's event instead of the global event. Validates event source matches selected session.

### WR-01: UI Thread Freeze on Bulk Collection Modification
**Files modified:** `src/QADeviceTool.App/ViewModels/SessionViewModel.cs`, `src/QADeviceTool.App/Models/BulkObservableCollection.cs`
**Commit:** 893eb89
**Applied fix:** Created `BulkObservableCollection<T>` class with suppressed notifications during bulk adds/removes. Updated `SessionViewModel.LogEntries` to use this class to prevent UI freezing during log pruning.

### WR-02: Unmanaged Process Handle Leak
**Files modified:** `src/QADeviceTool.App/Services/ProcessManagerService.cs`
**Commit:** 3a5d0eb
**Applied fix:** Ensured `process.Dispose()` is called after removing the exited process from `_trackedProcesses` in `ProcessManagerService`.

### WR-03: TaskCanceledException in App PID Resolver Logs Spurious Errors
**Files modified:** `src/QADeviceTool.App/Services/SessionService.cs`
**Commit:** fd9644a
**Applied fix:** Handled `OperationCanceledException` explicitly in `StartCapture`'s background PID resolution loop to swallow the exception correctly without logging spurious warnings.

## Skipped Issues

### IN-01: Cross-Contaminated Crash Detection State
**File:** `D:\OpenCode\QAQC\QADeviceTool\src\QADeviceTool.App\ViewModels\SessionViewModel.cs:351`
**Reason:** code context differs from review (Crash detector functionality `_crashDetector` has been removed or refactored out of `SessionViewModel.cs`)
**Original issue:** `OnLogBatchReceived` calls `_crashDetector.ScanLine(...)` using `SelectedSession?.Platform`. Logs coming from background iOS sessions while an Android session is selected would scan with Android regexes.

---

_Fixed: 2026-05-11T11:32:00Z_
_Fixer: the agent (gsd-code-fixer)_
_Iteration: 1_
