---
phase: code-review-services-helpers
reviewed: 2026-05-05T00:00:00Z
depth: deep
files_reviewed: 15
files_reviewed_list:
  - src/QADeviceTool.App/Services/AdbService.cs
  - src/QADeviceTool.App/Services/SessionService.cs
  - src/QADeviceTool.App/Services/CrashDetector.cs
  - src/QADeviceTool.App/Services/LogAnalyzerService.cs
  - src/QADeviceTool.App/Services/MacroService.cs
  - src/QADeviceTool.App/Services/DeviceMonitorService.cs
  - src/QADeviceTool.App/Services/IosService.cs
  - src/QADeviceTool.App/Services/ScrcpyService.cs
  - src/QADeviceTool.App/Services/ProcessManagerService.cs
  - src/QADeviceTool.App/Services/PreferencesService.cs
  - src/QADeviceTool.App/Helpers/ToolResolver.cs
  - src/QADeviceTool.App/Helpers/ToolLauncher.cs
  - src/QADeviceTool.App/Helpers/PathHelper.cs
  - src/QADeviceTool.App/Helpers/SecurityHelper.cs
findings:
  critical: 8
  warning: 14
  info: 9
  total: 31
status: issues_found
---

# Phase: Services & Helpers Code Review Report

**Reviewed:** 2026-05-05
**Depth:** deep (cross-file call chain analysis, thread safety tracing, plus language-aware checks)
**Files Reviewed:** 15
**Status:** issues_found — 8 BLOCKERs, 14 WARNINGs, 9 INFO items

## Summary

Deep review of 15 C# files spanning the Services and Helpers layers of LogPro v2.8.0. Analysis covered thread safety (SemaphoreSlim, ConcurrentDictionary, lock usage), resource management (Process, StreamWriter, IDisposable), async patterns (fire-and-forget, ConfigureAwait consistency), command injection attack surface (ADB shell argument unsanitized interpolation), and cross-file call chains.

**Key concerns:**
1. Four distinct command injection vulnerabilities where user-supplied strings (`path`, `url`, `remotePath`) are interpolated directly into ADB shell commands without sanitization — a `path` containing `'; reboot; echo '` would execute arbitrary commands on the device.
2. Two serious race conditions: a TOCTOU gap in `SessionService.StartCaptureAsync` (ConcurrentDictionary.ContainsKey followed by indexer set) and unsynchronized access to `_activeRecordProcess` in AdbService.
3. No `ConfigureAwait(false)` anywhere in the Helpers directory (0 of ~6 async methods), risking deadlocks when called from UI synchronization contexts.
4. Path traversal via unsanitized `..` in session names (SecurityHelper.SanitizeFileName does not strip `.` / `..`).

---

## Critical Issues

### CR-01: Command Injection — ADB Shell via unsanitized path in ListDirectoryAsync

**File:** `src/QADeviceTool.App/Services/AdbService.cs:457`
**Issue:** The `path` parameter is embedded in a shell command using single-quote wrapping without escaping interior single quotes. A path like `'; reboot; echo '` breaks out of the quotes and executes `reboot` on the device.
```csharp
var command = $"-s {serial} shell \"ls -lAL '{path}'\"";
```
**Fix:**
```csharp
// Escape single quotes for shell: replace ' with '\''
var escapedPath = path.Replace("'", "'\\''");
var command = $"-s {serial} shell \"ls -lAL '{escapedPath}'\"";
```
Alternatively, base64-encode the path (as already done in `SetDeviceClipboardAsync`/`SendNotificationAsync`):
```csharp
var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
var command = $"-s {serial} shell \"echo '{b64}' | base64 -d | xargs ls -lAL\"";
```

### CR-02: Command Injection — ADB Shell via unsanitized remotePath in DeleteFileAsync

**File:** `src/QADeviceTool.App/Services/AdbService.cs:517`
**Issue:** Identical pattern to CR-01. `remotePath` is single-quote-wrapped without escaping.
```csharp
var result = await RunAdbAsync($"-s {serial} shell \"rm -rf '{remotePath}'\"");
```
**Fix:** Same escaping strategy as CR-01.

### CR-03: Command Injection — ADB Shell via unsanitized URL in BroadcastIntentAsync

**File:** `src/QADeviceTool.App/Services/AdbService.cs:601`
**Issue:** The `url` parameter is double-quote-wrapped but interior double quotes are not escaped. A URL containing `" && reboot && echo "` would inject commands.
```csharp
var result = await RunAdbAsync(
    $"-s {serial} shell am start -a android.intent.action.VIEW -d \"{url}\"", 10000);
```
**Fix:** Escape double quotes or use base64 encoding:
```csharp
var escapedUrl = url.Replace("\\", "\\\\").Replace("\"", "\\\"");
var result = await RunAdbAsync(
    $"-s {serial} shell am start -a android.intent.action.VIEW -d \"{escapedUrl}\"", 10000);
```

### CR-04: Command Injection — AFC Client via unsanitized path in IosService.ListDirectoryAsync

**File:** `src/QADeviceTool.App/Services/IosService.cs:250`
**Issue:** The `path` parameter is double-quote-wrapped without escaping interior double quotes.
```csharp
var result = await ToolLauncher.RunAsync(_afcClient, $"-u {udid} ls -l \"{path}\"")
    .ConfigureAwait(false);
```
**Fix:** Escape double quotes in `path` before interpolation.

### CR-05: Race Condition — TOCTOU on ConcurrentDictionary in SessionService.StartCaptureAsync

**File:** `src/QADeviceTool.App/Services/SessionService.cs:66,102`
**Issue:** `ContainsKey` check followed by indexer assignment is not atomic. If two threads start capture for the same session ID simultaneously, both pass the `ContainsKey` check (line 66), and the second `_activeCaptures[session.Id] = ctx` (line 102) overwrites the first `CaptureContext` without disposing its Process, StreamWriter, or CancellationTokenSource.
```csharp
if (_activeCaptures.ContainsKey(session.Id)) return false;
// ... other work ...
_activeCaptures[session.Id] = ctx;  // race window between ContainsKey and this line
```
**Fix:**
```csharp
if (!_activeCaptures.TryAdd(session.Id, ctx)) return false;
```

### CR-06: Race Condition — Unsynchronized _activeRecordProcess in AdbService

**File:** `src/QADeviceTool.App/Services/AdbService.cs:320,339-340,374-375`
**Issue:** `_activeRecordProcess` is read and written from `StartScreenRecordAsync`, `StopScreenRecordAsync`, and the `IsScreenRecording` property with no synchronization. Two concurrent calls to `StopScreenRecordAsync` both capture the same non-null process reference, one kills and disposes it, the other then operates on a disposed process (ObjectDisposedException or accessing freed handle).
```csharp
// Thread A                           // Thread B
var process = _activeRecordProcess;   var process = _activeRecordProcess;
_activeRecordProcess = null;          _activeRecordProcess = null;
process.Kill(false);                  process.Kill(false);  // process already disposed!
```
**Fix:** Use `Interlocked.Exchange` for the swap or guard the entire block with a `SemaphoreSlim`:
```csharp
private readonly object _recordLock = new();
private System.Diagnostics.Process? _activeRecordProcess;

// In StopScreenRecordAsync:
System.Diagnostics.Process? process;
lock (_recordLock) { process = _activeRecordProcess; _activeRecordProcess = null; }
```

### CR-07: Path Traversal — Session Name Allows Parent Directory Escape

**File:** `src/QADeviceTool.App/Helpers/SecurityHelper.cs:31-49` and `src/QADeviceTool.App/Helpers/PathHelper.cs:44-54`
**Issue:** `SanitizeFileName` strips characters from `Path.GetInvalidFileNameChars()` (which on Windows includes `\`, `/`, `:`, `*`, `?`, `"`, `<`, `>`, `|`) but does **not** include `.`. The filename `..` passes through unsanitized. When combined via `Path.Combine(root, dirName)`, the `..` entry navigates to the parent directory. Although the full `dirName` includes a timestamp suffix (line 49: `$"{safeName}_{time}_{date}"`), a short session name combined with many `..` segments could escape. More critically, if the timestamp/date separator were removed or the API used elsewhere, path traversal is possible.
**Fix:** After sanitization, explicitly reject `.` and `..`, or strip leading dots:
```csharp
// In SecurityHelper.SanitizeFileName, after the loop:
if (result == "." || result == ".." || result.StartsWith("..\\") || result.StartsWith("../"))
    return string.Empty;
```

### CR-08: Race Condition — `_flushTimer` in SessionService

**File:** `src/QADeviceTool.App/Services/SessionService.cs:108-109`
**Issue:** `_flushTimer` is disposed and reassigned on every call to `StartCaptureAsync`. If two captures start concurrently, one thread disposes the timer while another is using it in `FlushLogBuffer`. Additionally, the timer fires `FlushLogBuffer` which reads from `_logBuffer` (safe, ConcurrentQueue) but also invokes `LogBatchReceived` event handlers — if those handlers throw, the exception propagates to the `System.Threading.Timer` callback with undefined behavior (process crash in .NET Framework, silent swallow in .NET 5+).
```csharp
_flushTimer?.Dispose();
_flushTimer = new System.Threading.Timer(_ => FlushLogBuffer(), null, 200, 200);
```
**Fix:** Use a lazy-initialized, long-lived timer that checks whether any captures exist before flushing:
```csharp
private static readonly System.Threading.Timer _flushTimer = new(_ => FlushLogBuffer(), null, 200, 200);
// In FlushLogBuffer: add try-catch around the event invocation
try { LogBatchReceived?.Invoke(batch.ToString()); } catch (Exception ex) { AppLogger.Log.Debug(ex, "LogBatch handler threw"); }
```

---

## Warnings

### WR-01: Fire-and-Forget Tasks with Swallowed Exceptions

**File:** `src/QADeviceTool.App/Services/SessionService.cs:114,136`
**Issue:** Two `Task.Run` calls are not awaited. If either task faults unexpectedly, the exception is observed only through the `TaskScheduler.UnobservedTaskException` handler (which fires during GC finalization, non-deterministically). Internal try-catch blocks catch expected errors but any unexpected exception type is silently lost.
**Fix:** Store the task in the `CaptureContext` and await it during `StopCapture` to surface exceptions, or use `Task.ContinueWith` with `TaskContinuationOptions.OnlyOnFaulted` to log unhandled errors.

### WR-02: Async Void in Timer Callback

**File:** `src/QADeviceTool.App/Services/DeviceMonitorService.cs:41`
**Issue:** `System.Threading.Timer` callback is `async void`. The timer does not understand async/await — if `PollDevicesAsync()` throws synchronously before the first `await`, the exception escapes the lambda's try-catch (it is thrown before the async state machine is set up) and crashes the thread pool thread. The interlocked re-entrancy guard on line 62 helps but does not prevent sync throws.
```csharp
_pollTimer = new Timer(async _ =>
{
    try { await PollDevicesAsync(); }
    catch (Exception ex) { AppLogger.Log.Error(ex, "[DeviceMonitor] Poll timer crashed"); }
}, null, 2000, intervalMs);
```
**Fix:** Use `PeriodicTimer` (.NET 6+) with an async loop, or wrap the entire body in a synchronous try-catch before the first await:
```csharp
_pollTimer = new Timer(_ =>
{
    try { PollDevicesAsync().GetAwaiter().GetResult(); }
    catch (Exception ex) { AppLogger.Log.Error(ex, "[DeviceMonitor] Poll timer crashed"); }
}, null, 2000, intervalMs);
```

### WR-03: Unobserved Timer Callback Overlap

**File:** `src/QADeviceTool.App/Services/DeviceMonitorService.cs:41,46`
**Issue:** `System.Threading.Timer` does not prevent overlapping callbacks. If `PollDevicesAsync` takes longer than `intervalMs` (default 10s), multiple callbacks stack up. The `_isPolling` interlocked guard prevents concurrent execution of the polling logic itself, but timer callbacks still fire, allocating async state machines that are immediately short-circuited.
**Fix:** Use `PeriodicTimer` with an async loop that naturally serializes invocations, or switch to `System.Timers.Timer` with `AutoReset = false` and manually restart after each poll completes.

### WR-04: Process Not Disposed After Recording in MacroService

**File:** `src/QADeviceTool.App/Services/MacroService.cs:28-71`
**Issue:** `StartRecordingAsync` creates a `Process` and returns it. The process is tracked by `ProcessManagerService` but is never disposed by MacroService itself. If the caller discards the reference, the process handle leaks until process exit + GC.
**Fix:** Document that the caller must call `Dispose()` on the returned Process, or wrap in a `RecordingSession` that implements `IDisposable` and handles cleanup.

### WR-05: Thread Pool Blocking via Task.Run wrapping WaitForExit

**File:** `src/QADeviceTool.App/Helpers/ToolLauncher.cs:89`
**Issue:** `process.WaitForExit(timeoutMs)` is a blocking call wrapped in `Task.Run`, consuming a thread pool thread for the entire timeout duration.
```csharp
var completed = await Task.Run(() => process.WaitForExit(timeoutMs));
```
**Fix:** Use `process.WaitForExitAsync(CancellationToken)` (.NET 5+) or combine `Task.Delay` with polling `process.HasExited`:
```csharp
var completed = process.WaitForExitAsync(new CancellationTokenSource(timeoutMs).Token);
```

### WR-06: Output/Error Reading Tasks After Process Kill

**File:** `src/QADeviceTool.App/Helpers/ToolLauncher.cs:91-100`
**Issue:** When `process.Kill(true)` executes on line 93, the output and error reading tasks (lines 64-87) may still be in `ReadLineAsync`. After the kill, `EndOfStream` may not become `true` on all platforms, causing `ReadLineAsync` to block indefinitely. The subsequent `await Task.WhenAll(outputTask, errorTask)` on line 100 may then hang for the same timeout or throw `ObjectDisposedException`.
**Fix:** Pass a `CancellationToken` to both reading tasks and cancel it after the process exits or times out.

### WR-07: No ConfigureAwait(false) in Helpers Directory

**File:** `src/QADeviceTool.App/Helpers/ToolLauncher.cs:64,77,89,100`
**Issue:** The Helpers directory has zero `ConfigureAwait(false)` calls across ~6 async methods. These library methods can be called from UI synchronization contexts (WPF/WinForms), where the default `await` behavior captures and resumes on the UI thread, causing deadlocks or UI thread starvation.
**Fix:** Add `.ConfigureAwait(false)` to all `await` calls in `ToolLauncher.RunAsync` and any other async methods in the Helpers namespace.

### WR-08: Non-Atomic File Save — Data Loss Risk

**File:** `src/QADeviceTool.App/Services/PreferencesService.cs:71`
**Issue:** `File.WriteAllText` overwrites the settings file in-place. If the process crashes or loses power during the write, the file is left empty or truncated — all user preferences are lost.
```csharp
File.WriteAllText(_settingsFilePath, json);
```
**Fix:** Write to a temp file first, then atomically replace:
```csharp
var tmpPath = _settingsFilePath + ".tmp";
File.WriteAllText(tmpPath, json);
File.Move(tmpPath, _settingsFilePath, overwrite: true);
```

### WR-09: Non-Thread-Safe List in CrashDetector

**File:** `src/QADeviceTool.App/Services/CrashDetector.cs:52,81`
**Issue:** `_detectedCrashes` is a `List<CrashEvent>` (not thread-safe). Multiple threads calling `ScanLine` (e.g., from `FlushLogBuffer` event handlers and explicit calls) race on `_detectedCrashes.Add(crash)`.
**Fix:** Use `ConcurrentBag<CrashEvent>` or `ImmutableList<CrashEvent>` with `Interlocked.Exchange` for updates, or lock around all accesses.

### WR-10: Race Condition on _mirrorProcess in ScrcpyService

**File:** `src/QADeviceTool.App/Services/ScrcpyService.cs:14,48,52,55-57**
**Issue:** `_mirrorProcess` is set and read without synchronization. Concurrent `StartMirroringAsync` calls both call `StopMirroring()` (line 57), then both set `_mirrorProcess`. The first process is never cleaned up.
**Fix:** Guard with a `SemaphoreSlim(1,1)` or `lock` around all start/stop operations.

### WR-11: OOM Risk — Full File Read for Large Logs

**File:** `src/QADeviceTool.App/Services/SessionService.cs:316-317,374,414`
**Issue:** `ReadLogContentAsync`, `ExportToCsvAsync`, and `ExportToJsonAsync` all call `File.ReadAllLinesAsync` which loads the entire file into memory. For multi-hour game QA sessions, log files can exceed 500MB. This causes `OutOfMemoryException` and process crash.
**Fix:** Stream lines using `File.ReadLinesAsync` (.NET 6+) or `StreamReader.ReadLineAsync`:
```csharp
var lines = new List<string>();
await foreach (var line in File.ReadLinesAsync(session.LogFilePath))
{
    lines.Add(line);
    if (lines.Count > maxLines) lines.RemoveAt(0);
}
```

### WR-12: Task.Delay Without CancellationToken in StopScreenRecordAsync

**File:** `src/QADeviceTool.App/Services/AdbService.cs:346`
**Issue:** `await Task.Delay(500)` has no cancellation token. During application shutdown, this delays the cleanup by 500ms or throws `TaskCanceledException` if the synchronization context is torn down.
**Fix:** Accept a `CancellationToken` parameter or use a short timeout with a CTS.

### WR-13: Regex Created Per Call — Memory Pressure

**File:** `src/QADeviceTool.App/Services/SessionService.cs:491,494`
**Issue:** `AnonymizeDeviceInfo` creates two new `Regex` instances on every call. Called once per log line during export, this puts pressure on the GC.
```csharp
var serialPattern = new System.Text.RegularExpressions.Regex(@"\b[A-Z0-9]{8,20}\b");
var ipPattern = new System.Text.RegularExpressions.Regex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b");
```
**Fix:** Promote to `static readonly` fields with `RegexOptions.Compiled`.

### WR-14: PreferencesService Thread Safety — Current Shared Mutable State

**File:** `src/QADeviceTool.App/Services/PreferencesService.cs:27,45,70,89-92`
**Issue:** `Current` is a static mutable reference. `SaveDevicePreference` (line 89-92) modifies `Current.DevicePreferences[serial]` then calls `Save()`, which serializes the state. If `Load()` is called concurrently (e.g., from a settings UI refresh), the deserialized object overwrites `Current`, losing the in-progress modification. Additionally, `Save()` reads `Current` while another thread may be mutating `DevicePreferences` dictionary.
**Fix:** Use `ReaderWriterLockSlim` or make all operations go through a single-threaded channel/queue, or use `ImmutableDictionary`.

---

## Info

### IN-01: Dead Parameter in RunAdbWithRetryAsync

**File:** `src/QADeviceTool.App/Services/AdbService.cs:39`
**Issue:** The `int attempt = 1` parameter is never used in the method body. It appears to be leftover from a prior recursive implementation.
**Fix:** Remove the unused `attempt` parameter.

### IN-02: Null-Forgiving Operator on Potentially-Null Result

**File:** `src/QADeviceTool.App/Services/AdbService.cs:54`
**Issue:** `return result!;` uses the null-forgiving operator to suppress a compiler warning. While the loop always assigns `result` before this point (MaxRetryAttempts = 2), the operator hides the fact that the analyzer detected a possible null path. If `MaxRetryAttempts` is ever changed to 0, this becomes a runtime NullReferenceException.
**Fix:** Initialize `result` with a non-null default or add a guard:
```csharp
return result ?? new ToolLauncherResult { Success = false, Error = "All retry attempts exhausted" };
```

### IN-03: Empty Catch Blocks Swallow All Exception Information

**File:** `src/QADeviceTool.App/Services/AdbService.cs:345`, `src/QADeviceTool.App/Services/SessionService.cs:197-206,331,479`, `src/QADeviceTool.App/Services/ScrcpyService.cs:136`, `src/QADeviceTool.App/Helpers/ToolResolver.cs:45,72,87,114`
**Issue:** 11 empty catch blocks across the codebase swallow all exceptions without even a debug log. While some are deliberate (e.g., process Kill/dispose failing during cleanup), others (like `DeleteSession` at SessionService:331, or the ToolResolver directory enumeration at line 45) would benefit from at least a `Debug`-level log entry.
**Fix:** Add `AppLogger.Log.Debug(ex, "Non-critical cleanup failure")` to each catch block, or use a helper like `SafeDispose`.

### IN-04: DateTime.Now Called Twice — Midnight Boundary Bug

**File:** `src/QADeviceTool.App/Helpers/PathHelper.cs:47-48`
**Issue:** Two separate `DateTime.Now` calls can cross a day boundary at midnight. The time portion (`hh.mm.sstt`) would reflect the new day while the date portion (`dd.MM.yyyy`) still reflects the old day (or vice versa).
```csharp
var time = DateTime.Now.ToString("hh.mm.sstt");
var date = DateTime.Now.ToString("dd.MM.yyyy");
```
**Fix:** Capture `DateTime.Now` once:
```csharp
var now = DateTime.Now;
var time = now.ToString("hh.mm.sstt");
var date = now.ToString("dd.MM.yyyy");
```

### IN-05: ToolResolver.InitializeNativePaths Duplicates PATH Entries

**File:** `src/QADeviceTool.App/Helpers/ToolResolver.cs:94-115**
**Issue:** If `InitializeNativePaths()` is called multiple times, each subdirectory in `tools/` is prepended to PATH again, causing unbounded growth of the PATH environment variable.
**Fix:** Check if paths are already in PATH before prepending, or use a `static readonly bool _initialized` flag.

### IN-06: Hardcoded .exe Extension — Non-Portable

**File:** `src/QADeviceTool.App/Services/IosService.cs:23-28`, `src/QADeviceTool.App/Helpers/ToolResolver.cs:37-48`
**Issue:** Tool names and resolution logic assume `.exe` extension. This is Windows-only. While the project may target Windows exclusively, calling this out as a portability concern.
**Fix:** Use `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)` to conditionally append `.exe`.

### IN-07: AlertRule.Regex Getter — Lazy Initialization Not Thread-Safe

**File:** `src/QADeviceTool.App/Services/LogAnalyzerService.cs:87-105`
**Issue:** The `_cachedRegex` field is read and written without synchronization. While the worst case is creating duplicate `Regex` instances (no data corruption), it wastes memory under concurrent access.
**Fix:** Use `Lazy<Regex>` or `Interlocked.CompareExchange` for thread-safe lazy initialization:
```csharp
private Lazy<Regex> _cachedRegex;
public Regex Regex => (_cachedRegex ??= new Lazy<Regex>(() => CreateRegex())).Value;
```

### IN-08: ToolLauncher Depends on Service Locator Pattern

**File:** `src/QADeviceTool.App/Helpers/ToolLauncher.cs:42,117,135,159`
**Issue:** Static helper class `ToolLauncher` calls `Services.AppLogger.Log` directly — a service locator anti-pattern. This creates a hidden dependency and makes unit testing difficult. The `try { } catch { }` around logging calls on lines 117 and 159 (swallowing logging failures) confirms this brittleness.
**Fix:** Accept `ILogger` as a method parameter or constructor inject via a factory.

### IN-09: SimpleMacroStep JSON Property Name Mismatch

**File:** `src/QADeviceTool.App/Services/MacroService.cs:239`
**Issue:** `SimpleMacroStep.Text` has `[JsonPropertyName("text")]` while other properties use capitalized names like `[JsonPropertyName("action")]`, `[JsonPropertyName("x")]`, etc. This is inconsistent but not a bug — the JSON property name is intentionally lowercase `"text"`. If this was accidental (the Java/Android `input text` command is lowercase), it may cause confusion when reading serialized JSON.
**Fix:** Align naming convention — either all lowercase or all PascalCase, and document the chosen convention.

---

_Reviewed: 2026-05-05T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: deep_
