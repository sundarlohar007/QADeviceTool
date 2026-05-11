---
phase: 03-code-review-services-helpers
reviewed: 2026-05-07T00:00:00Z
depth: deep
files_reviewed: 14
files_reviewed_list:
  - src/QADeviceTool.App/Services/AdbService.cs
  - src/QADeviceTool.App/Services/SessionService.cs
  - src/QADeviceTool.App/Services/IosService.cs
  - src/QADeviceTool.App/Services/ScrcpyService.cs
  - src/QADeviceTool.App/Services/DeviceMonitorService.cs
  - src/QADeviceTool.App/Services/CrashDetector.cs
  - src/QADeviceTool.App/Services/MacroService.cs
  - src/QADeviceTool.App/Services/LogAnalyzerService.cs
  - src/QADeviceTool.App/Services/ProcessManagerService.cs
  - src/QADeviceTool.App/Services/PreferencesService.cs
  - src/QADeviceTool.App/Helpers/ToolLauncher.cs
  - src/QADeviceTool.App/Helpers/ToolResolver.cs
  - src/QADeviceTool.App/Helpers/SecurityHelper.cs
  - src/QADeviceTool.App/Helpers/PathHelper.cs
findings:
  critical: 7
  warning: 15
  info: 9
  total: 31
status: issues_found
---

# Phase 03: Services & Helpers Code Review Report

**Reviewed:** 2026-05-07
**Depth:** deep (cross-file call chain analysis, threading/race analysis, language-aware checks)
**Files Reviewed:** 14
**Status:** issues_found -- 7 BLOCKERs, 15 WARNINGs, 9 INFO items

## Summary

Adversarial deep review of 14 C# files across the Services and Helpers layers of LogPro v2.8.0. Every method was traced for thread safety (SemaphoreSlim usage, ConcurrentDictionary operations, static mutable state), resource management (Process, StreamWriter, Timer, CancellationTokenSource disposal), command injection attack surface (shell meta-character expansion in ADB/iOS tool arguments), and cross-file call chain integrity (who calls what, and whether errors propagate correctly).

**Top concerns:**

1. Command injection through `$()` expansion in Android shell double-quoted arguments -- `BroadcastIntentAsync` escapes `"` but not shell interpolation syntax (`$()`, backticks). A URL containing `$(reboot)` will execute `reboot` on the device.

2. Stale null entry in `_activeCaptures` after `StartCaptureAsync` fails -- when the process fails to start or writer creation throws, the null tombstone entry remains in the dictionary, permanently blocking any future start for that session ID.

3. `MacroService.StartRecordingAsync` spawns ADB processes directly (via `new Process()`) without acquiring `AdbService._adbLock`, defeating the semaphore designed to prevent concurrent USB transport access.

4. `_activeRecordProcess` accessed without synchronization across three methods -- concurrent read via `IsScreenRecording` can dereference a disposed Process.

5. Non-atomic settings file write -- a crash during `File.WriteAllText` destroys all user preferences.

---

## Critical Issues

### CR-01: Command Injection -- Android Shell `$()` Expansion in BroadcastIntentAsync

**File:** `src/QADeviceTool.App/Services/AdbService.cs:605-606`
**Severity:** BLOCKER
**Issue:** `BroadcastIntentAsync` escapes only double quotes in the URL but leaves `$()` and backticks unprotected. The argument is passed to `adb shell`, meaning the embedded URL string `"... \"{safeUrl}\" ..."` is parsed by the Android shell (`mksh`/`toybox sh`), which **expands `$(...)` and backticks inside double quotes**. A URL like `http://a.com/$(reboot)` or `` http://a.com/`reboot` `` executes arbitrary shell commands on the device.

```csharp
var safeUrl = url.Replace("\"", "\\\"");  // only escapes double-quote
var result = await RunAdbAsync(
    $"-s {serial} shell am start -a android.intent.action.VIEW -d \"{safeUrl}\"", 10000);
```

**Exploit:** User or caller supplies `http://x.com/$(rm -rf /sdcard/*)` -- Android shell expands `$(rm -rf /sdcard/*)` inside the double-quoted `-d` argument.

**Fix:** Use base64-encoding (as already done in `SetDeviceClipboardAsync` and `SendNotificationAsync`), or use single quotes inside the shell command (single quotes prevent ALL expansion):
```csharp
// Option A: base64 encode
var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(url));
var result = await RunAdbAsync(
    $"-s {serial} shell \"echo '{b64}' | base64 -d | xargs -0 am start -a android.intent.action.VIEW -d\"", 10000);

// Option B: use single-quote wrapping with proper escaping
var escapedUrl = url.Replace("'", "'\\''");
var result = await RunAdbAsync(
    $"-s {serial} shell am start -a android.intent.action.VIEW -d '{escapedUrl}'", 10000);
```

---

### CR-02: Stale Null Entry Blocks Session After Failed StartCaptureAsync

**File:** `src/QADeviceTool.App/Services/SessionService.cs:66,75,89-98`
**Severity:** BLOCKER
**Issue:** `StartCaptureAsync` inserts `null` into `_activeCaptures` (line 66) as a tombstone to prevent double-start. But if the method subsequently returns `false` (line 75: process is null, or lines 89-98: writer creation throws), the `null` entry is **never removed**. This makes the session ID permanently unusable -- any future `StartCaptureAsync` call fails because `TryAdd` returns false (key already exists), and `StopCapture` finds `null` context and returns without doing anything.

```csharp
// Line 66: tombstone inserted
if (!_activeCaptures.TryAdd(session.Id, null!)) return false;

// Line 70-75: process creation failure -- null entry LEAKED
Process? process = session.Platform switch { ... };
if (process == null) return false;  // BUG: did not remove null entry

// Lines 89-98: writer creation failure -- null entry LEAKED
catch (Exception ex) { ... return false; }  // BUG: did not remove null entry
```

**Consequence:** Once a session fails to start (e.g. device disconnected mid-start), that session object is forever dead. The user must create a brand new session (with a new GUID-based ID).

**Fix:** Remove the tombstone on every failure path:
```csharp
// At line 75:
if (process == null)
{
    _activeCaptures.TryRemove(session.Id, out _);
    return false;
}

// In the catch at line 89-98, before return false:
_activeCaptures.TryRemove(session.Id, out _);
```

Or restructure to use a single `try-finally` around the setup phase:
```csharp
if (!_activeCaptures.TryAdd(session.Id, null!)) return false;
try
{
    // ... all setup work ...
}
catch
{
    _activeCaptures.TryRemove(session.Id, out _);
    throw;
}
```

---

### CR-03: MacroService Bypasses AdbService._adbLock Semaphore

**File:** `src/QADeviceTool.App/Services/MacroService.cs:30-44`
**Severity:** BLOCKER
**Issue:** `StartRecordingAsync` creates and starts an ADB process directly via `new Process()` + `process.Start()`, completely bypassing `AdbService._adbLock` (the `SemaphoreSlim(1,1)` designed to serialize ALL ADB command execution and prevent concurrent USB transport access).

```csharp
var process = new System.Diagnostics.Process
{
    StartInfo = new System.Diagnostics.ProcessStartInfo
    {
        FileName = Helpers.ToolResolver.Resolve("adb"),
        Arguments = $"-s {serial} shell getevent -t",
        ...
    }
};
process.Start();  // <-- bypasses AdbService._adbLock entirely
```

**Consequence:** If a `getevent` recording is running while `AdbService` issues ADB commands (log capture, screenshot, property queries), both streams contend for the USB transport. This can cause corrupted output, command failures, or hung processes -- the exact scenario the semaphore was designed to prevent.

**Fix:** Route the recording through `AdbService` instead of creating the process directly. Either add a dedicated method or use `ExecuteCommandAsync`:
```csharp
// Option A: Add to AdbService
public async Task<System.Diagnostics.Process?> StartGetEventAsync(string serial)
{
    return await StartAdbLongRunning($"-s {serial} shell getevent -t");
}

// Option B: Use existing infrastructure
// But StartAdbLongRunning is private. Expose a controlled variant.
```

---

### CR-04: Race Condition on _activeRecordProcess -- Disposed Process Dereference

**File:** `src/QADeviceTool.App/Services/AdbService.cs:322,340,374`
**Severity:** BLOCKER
**Issue:** `_activeRecordProcess` is written with a plain assignment (line 322), read-and-cleared with `Interlocked.Exchange` (line 340), and read without ANY synchronization in `IsScreenRecording` (line 374). This creates multiple unsafe access patterns:

1. **Dereferencing disposed Process:** If `StopScreenRecordAsync` swaps `_activeRecordProcess` to null via `Interlocked.Exchange`, then disposes the process (line 347), a concurrent read in `IsScreenRecording` (line 374) can see the **old** non-null reference (stale cache) and access `HasExited` on an already-disposed Process, causing `ObjectDisposedException`.

2. **Double Stop:** Two concurrent `StopScreenRecordAsync` calls both `Interlocked.Exchange` the reference -- one gets the actual Process, the other gets null. This is safe. But the plain write at line 322 (from `StartScreenRecordAsync`) is not atomic with `Interlocked.Exchange` -- a store buffer delay means the new reference may not be immediately visible to `StopScreenRecordAsync`.

```csharp
// Line 322: plain write
_activeRecordProcess = process;

// Line 340: interlocked read+clear
var process = Interlocked.Exchange(ref _activeRecordProcess, null);

// Line 374: plain read with NO synchronization
public bool IsScreenRecording => _activeRecordProcess != null && !_activeRecordProcess.HasExited;
```

**Fix:** Consistent memory barriers on all access paths:
```csharp
// Line 322: use Interlocked or volatile write
Interlocked.Exchange(ref _activeRecordProcess, process);

// Line 374: thread-safe read
public bool IsScreenRecording
{
    get
    {
        var p = Interlocked.CompareExchange(ref _activeRecordProcess, null, null);
        return p != null && !p.HasExited;
    }
}
```

Or guard the entire start/stop/is-recording logic with a lock:
```csharp
private readonly object _recordLock = new();
private System.Diagnostics.Process? _activeRecordProcess;

public bool IsScreenRecording
{
    get { lock (_recordLock) return _activeRecordProcess != null && !_activeRecordProcess.HasExited; }
}
```

---

### CR-05: Race Condition on _flushTimer in SessionService

**File:** `src/QADeviceTool.App/Services/SessionService.cs:108-109,224-226`
**Severity:** BLOCKER
**Issue:** `_flushTimer` is disposed and recreated on every `StartCaptureAsync` call. Two race windows exist:

1. **StartCaptureAsync + StopCapture race:** Thread A calls `StopCapture`, checks `_activeCaptures.Count == 0` (line 222), sees 1. Thread B's `StartCaptureAsync` completes and decrements some counter... Actually, `StopCapture` removes the entry and checks count AFTER removal. But between the `Count == 0` check and `_flushTimer.Dispose()`, a new capture could start and create a new timer -- the old timer's `Dispose()` may race with the new timer's allocation.

2. **Concurrent StopCapture calls:** Two threads call `StopCapture` for the last two captures. Both see `Count == 1` after their respective removals (neither sees 0). Then both call `Dispose()`. The older timer is disposed while a callback may still be executing.

```csharp
// In StopCapture, lines 222-226:
if (_activeCaptures.Count == 0)   // <-- TOCTOU: check then act
{
    _flushTimer?.Dispose();       // <-- race: timer may be in use
    _flushTimer = null;
}
```

**Fix:** Use a long-lived timer that checks whether work needs to be done:
```csharp
// Initialize ONCE (e.g., in constructor or static initializer):
private static readonly System.Threading.Timer _flushTimer = new(_ => FlushLogBuffer(), null, 200, 200);

// In FlushLogBuffer, guard the event invocation:
private static void FlushLogBuffer()
{
    if (_logBuffer.IsEmpty) return;
    // ... dequeue and build batch ...
    try { LogBatchReceived?.Invoke(batch.ToString()); }
    catch (Exception ex) { AppLogger.Log.Debug(ex, "LogBatch handler threw"); }
}
```

---

### CR-06: Non-Atomic Settings File Write -- Irreversible Data Loss

**File:** `src/QADeviceTool.App/Services/PreferencesService.cs:70-76`
**Severity:** BLOCKER
**Issue:** `File.WriteAllText` overwrites `settings.json` in-place. If the process crashes (or the machine loses power) during the write, the file is left empty or partially truncated. **All user preferences are permanently lost** -- session directory paths, device notes, retention settings, everything.

```csharp
public static void Save()
{
    var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(_settingsFilePath, json);  // in-place overwrite -- NOT atomic
}
```

**Fix:** Write to a temporary file, then atomically replace:
```csharp
public static void Save()
{
    try
    {
        var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
        var tmpPath = _settingsFilePath + ".tmp";
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, _settingsFilePath, overwrite: true);
    }
    catch (Exception ex)
    {
        AppLogger.Log.Error(ex, "Failed to save preferences.");
    }
}
```

---

### CR-07: ProcessManagerService Memory Leak -- Orphaned Entries on EnableRaisingEvents Failure

**File:** `src/QADeviceTool.App/Services/ProcessManagerService.cs:17-28`
**Severity:** BLOCKER
**Issue:** `TrackProcess` adds the process to `_trackedProcesses` (line 18) **before** calling `EnableRaisingEvents` (line 20). If the process has already exited, `EnableRaisingEvents` throws `InvalidOperationException`, which is caught at line 26. But the process **remains in the dictionary** (the `TryAdd` at line 18 already succeeded), and since the `Exited` event handler was never registered (the exception occurred before line 21), the entry is never removed.

```csharp
public static void TrackProcess(Process process)
{
    try
    {
        var id = process.Id;
        _trackedProcesses.TryAdd(id, process);    // Step 1: ADD to dictionary

        process.EnableRaisingEvents = true;        // Step 2: MIGHT THROW
        process.Exited += (s, e) =>                // Step 3: Never reached if step 2 threw
        {
            _trackedProcesses.TryRemove(id, out _);
        };
    }
    catch (InvalidOperationException)
    {
        // Process already exited -- but the entry is STILL in the dictionary!
    }
}
```

**Consequence:** Over time (especially with short-lived ADB commands), `_trackedProcesses` accumulates dead entries. `KillAllTrackedProcesses` will iterate over these (attempting to access disposed Process objects), potentially throwing.

**Fix:** Add to dictionary AFTER enabling events, and clean up on failure:
```csharp
public static void TrackProcess(Process process)
{
    if (process == null) return;
    try
    {
        process.EnableRaisingEvents = true;
        var id = process.Id;
        _trackedProcesses.TryAdd(id, process);
        process.Exited += (s, e) => _trackedProcesses.TryRemove(id, out _);
    }
    catch (InvalidOperationException)
    {
        // Process already exited -- nothing to track
        try { process.Dispose(); } catch { }
    }
}
```

---

## Warnings

### WR-01: SendNotificationAsync -- channelId Not Quoted in Shell Command

**File:** `src/QADeviceTool.App/Services/AdbService.cs:597-601`
**Issue:** The `channelId` parameter is interpolated directly into the Android shell command without quoting. If `channelId` contains spaces, the shell command breaks. While channel IDs are conventionally simple strings, this is unvalidated user input.

```csharp
var shellCmd = $"t=$(echo '{titleB64}'|base64 -d);b=$(echo '{bodyB64}'|base64 -d);" +
               $"cmd notification post -t \"$t\" \"$b\" --channel {channelId} {tag}";
//                                                             ^^^^^^^^^ unquoted
```

**Fix:** Quote or validate channelId:
```csharp
// Option A: Validate that channelId is safe
if (!Regex.IsMatch(channelId, @"^[a-zA-Z0-9_]+$"))
    throw new ArgumentException("channelId must be alphanumeric");

// Option B: Quote it
$"cmd notification post -t \"$t\" \"$b\" --channel '{channelId}' {tag}";
```

---

### WR-02: packageId Not Quoted in Multiple ADB Commands

**File:** `src/QADeviceTool.App/Services/AdbService.cs:552,558,564,570`
**Issue:** `UninstallAppAsync`, `ForceStopAppAsync`, `ClearAppDataAsync`, and `GetAppDetailsAsync` all interpolate `packageId` directly into ADB shell commands without quoting. While Android package names follow `com.example.app` format and cannot contain whitespace, this is still a command injection vector if an attacker can control this input.

```csharp
var result = await RunAdbAsync($"-s {serial} uninstall {packageId}", DefaultTimeoutMs);
```

**Fix:** Quote the packageId or validate against a known-safe pattern:
```csharp
var safePkg = packageId; // or Regex.Replace(packageId, @"[^\w.]", "")
var result = await RunAdbAsync($"-s {serial} uninstall '{safePkg}'", DefaultTimeoutMs);
```

---

### WR-03: StartCaptureAsync Read Loop Ignores CancellationToken

**File:** `src/QADeviceTool.App/Services/SessionService.cs:137-162`
**Issue:** The `Task.Run` lambda that reads `process.StandardOutput` passes `cts.Token` but the loop body never checks `cts.Token.IsCancellationRequested`. When `StopCapture` cancels the token and disposes the writer, the read loop keeps running:
- `ReadLineAsync()` may still return lines
- Writing to the disposed writer throws `ObjectDisposedException` (caught silently at line 146)
- `_logBuffer.Enqueue(line)` still succeeds -- these orphaned lines sit in the buffer but are never dispatched because the timer is being torn down

```csharp
while (!process.HasExited)  // no token check
{
    var line = await process.StandardOutput.ReadLineAsync();  // no cancellation
    ...
}
```

**Fix:** Break out of the loop when canceled:
```csharp
while (!process.HasExited && !cts.Token.IsCancellationRequested)
{
    var line = await process.StandardOutput.ReadLineAsync();
    if (line == null) break;
    // ... rest ...
}
```

---

### WR-04: Non-Thread-Safe List in CrashDetector.ScanLine

**File:** `src/QADeviceTool.App/Services/CrashDetector.cs:52,81`
**Issue:** `_detectedCrashes` is a `List<CrashEvent>` -- not thread-safe. `ScanLine` can be called from multiple threads (e.g., log batch processing on timer thread + explicit calls). Concurrent `List<T>.Add()` corrupts the internal array.

```csharp
private readonly List<CrashEvent> _detectedCrashes = new();
// ...
_detectedCrashes.Add(crash);  // RACE: concurrent Add
```

**Fix:**
```csharp
private readonly ConcurrentBag<CrashEvent> _detectedCrashes = new();
// Or use lock:
lock (_detectedCrashes) { _detectedCrashes.Add(crash); }
```

---

### WR-05: Non-Thread-Safe Event Invocation in CrashDetector and LogAnalyzerService

**File:** `src/QADeviceTool.App/Services/CrashDetector.cs:82` and `src/QADeviceTool.App/Services/LogAnalyzerService.cs:48`
**Issue:** The `Handler?.Invoke(args)` pattern is not thread-safe. A subscriber can be removed between the null check and the invocation, causing `NullReferenceException`:

```csharp
CrashDetected?.Invoke(crash);       // CrashDetector.cs:82
RuleMatched?.Invoke(rule, line, lineIndex);  // LogAnalyzerService.cs:48
```

**Fix:** Capture the delegate locally before invoking:
```csharp
var handler = CrashDetected;
handler?.Invoke(crash);
```

---

### WR-06: Race Condition on _mirrorProcess in ScrcpyService

**File:** `src/QADeviceTool.App/Services/ScrcpyService.cs:14,52-58,73-77`
**Issue:** `_mirrorProcess` is set, read, and cleared without synchronization. Two concurrent `StartMirroringAsync` calls both check `if (_mirrorProcess != null)`, then both call `StopMirroring()`, then both proceed to set `_mirrorProcess` to their own new process. The first process reference is overwritten without cleanup (orphaned process).

**Fix:** Guard with a semaphore or lock:
```csharp
private readonly SemaphoreSlim _mirrorLock = new(1, 1);

public async Task<bool> StartMirroringAsync(string serial, ScrcpyOptions? options = null)
{
    await _mirrorLock.WaitAsync();
    try
    {
        if (_mirrorProcess != null) StopMirroring();
        // ... rest of method ...
    }
    finally { _mirrorLock.Release(); }
}
```

---

### WR-07: GetDevicePreference Creates Entry Without Saving

**File:** `src/QADeviceTool.App/Services/PreferencesService.cs:81-87`
**Issue:** `GetDevicePreference` adds a new `DevicePreference` to `Current.DevicePreferences` but never calls `Save()`. If the app crashes or is terminated before the next explicit `Save()`, the newly created preference is permanently lost.

```csharp
public static DevicePreference GetDevicePreference(string serial)
{
    if (Current.DevicePreferences.TryGetValue(serial, out var pref))
        return pref;
    
    var newPref = new DevicePreference();
    Current.DevicePreferences[serial] = newPref;
    return newPref;  // <-- never saved
}
```

**Fix:** Call Save after creating the new entry, or at minimum document the behavior:
```csharp
Current.DevicePreferences[serial] = newPref;
Save();  // persist immediately
```

---

### WR-08: PreferencesService.Current -- Shared Mutable Static Without Synchronization

**File:** `src/QADeviceTool.App/Services/PreferencesService.cs:27,45,70,89-92`
**Issue:** `Current` is a static mutable reference. `Load()` replaces it with a deserialized object. `SaveDevicePreference` mutates `Current.DevicePreferences[serial]` then calls `Save()`. If `Load()` runs concurrently (e.g., from a settings refresh), the deserialized object may not see the in-progress mutation, or worse, `Save()` may serialize a partially-mutated state.

**Fix:** Use `ReaderWriterLockSlim` or load into a local, merge, then swap:
```csharp
private static readonly ReaderWriterLockSlim _prefsLock = new();
```

---

### WR-09: No ConfigureAwait(false) in ToolLauncher (Library Code)

**File:** `src/QADeviceTool.App/Helpers/ToolLauncher.cs:68,79,89,100`
**Issue:** `ToolLauncher.RunAsync` contains four `await` calls without `ConfigureAwait(false)`. Since `ToolLauncher` is a library helper called from UI contexts, the default `await` captures the `SynchronizationContext` and resumes on the UI thread, causing UI thread starvation or deadlocks.

```csharp
var line = await process.StandardOutput.ReadLineAsync();  // captures UI context
```

**Fix:** Add `.ConfigureAwait(false)` to all awaits in library code:
```csharp
var line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
```

---

### WR-10: Output Reading Tasks May Hang After Process.Kill

**File:** `src/QADeviceTool.App/Helpers/ToolLauncher.cs:64-87,93,100`
**Issue:** When `process.Kill(true)` executes on timeout (line 93), the asynchronous output and error reading tasks (lines 64-87) may still be parked in `ReadLineAsync()`. After `Kill()`, the stream's `EndOfStream` flag may not transition cleanly on all platforms, causing `ReadLineAsync()` to hang. The subsequent `await Task.WhenAll(outputTask, errorTask)` on line 100 then blocks indefinitely.

**Fix:** Pass a `CancellationToken` to the reading tasks and cancel after process exit:
```csharp
using var readCts = new CancellationTokenSource();
var outputTask = Task.Run(async () =>
{
    while (!readCts.Token.IsCancellationRequested)
    {
        var line = await process.StandardOutput.ReadLineAsync();
        // ...
    }
}, readCts.Token);
// After WaitForExit:
readCts.Cancel();
await Task.WhenAll(outputTask, errorTask);
```

---

### WR-11: Full-File Read Risks OutOfMemoryException for Large Logs

**File:** `src/QADeviceTool.App/Services/SessionService.cs:316-319,374-375,415-416`
**Issue:** `ReadLogContentAsync`, `ExportToCsvAsync`, and `ExportToJsonAsync` all call `File.ReadAllLinesAsync` which loads the entire log file into memory. Multi-hour game QA sessions can produce 500MB+ log files, causing `OutOfMemoryException` and process termination.

**Fix:** Stream the file instead:
```csharp
// For ReadLogContentAsync:
var lines = new LinkedList<string>();
await foreach (var line in File.ReadLinesAsync(session.LogFilePath))
{
    lines.AddLast(line);
    if (lines.Count > maxLines) lines.RemoveFirst();
}
```

---

### WR-12: Regex Created Per-Call in AnonymizeDeviceInfo

**File:** `src/QADeviceTool.App/Services/SessionService.cs:491-496`
**Issue:** Two `new Regex(...)` instances are created on every call to `AnonymizeDeviceInfo`, which is invoked once per log line during CSV/JSON export. This creates massive GC pressure for large exports.

```csharp
var serialPattern = new System.Text.RegularExpressions.Regex(@"\b[A-Z0-9]{8,20}\b");
var ipPattern = new System.Text.RegularExpressions.Regex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b");
```

**Fix:** Promote to `static readonly` fields at class level:
```csharp
private static readonly Regex SerialPattern = new(@"\b[A-Z0-9]{8,20}\b", RegexOptions.Compiled);
private static readonly Regex IpPattern = new(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", RegexOptions.Compiled);
```

---

### WR-13: Task.Delay Without CancellationToken in StopScreenRecordAsync

**File:** `src/QADeviceTool.App/Services/AdbService.cs:346`
**Issue:** `await Task.Delay(500)` has no cancellation token. During application shutdown or if the caller is canceled, this delays cleanup by 500ms or throws `TaskCanceledException` if the synchronization context is torn down, potentially leaking the ADB process.

**Fix:** Accept a `CancellationToken` or use a short timeout:
```csharp
// Option: use CancellationToken.None if no token is available
await Task.Delay(500, CancellationToken.None);
```

---

### WR-14: Fire-and-Forget Tasks with Swallowed Exceptions in SessionService

**File:** `src/QADeviceTool.App/Services/SessionService.cs:114,137`
**Issue:** Two `Task.Run` calls are not awaited or tracked. If unexpected exception types escape the internal try-catch blocks (e.g., `OutOfMemoryException`, `StackOverflowException`), they are silently dropped by the finalizer's `UnobservedTaskException` handler, which may never fire (or may be configured to crash the process).

**Fix:** Store the tasks in `CaptureContext` and await them during `StopCapture`:
```csharp
private record CaptureContext(Process Process, StreamWriter Writer, StreamWriter? AppWriter,
    LogSession Session, CancellationTokenSource Cts, Task? PidTask, Task? ReadTask);
```

---

### WR-15: Empty Catch Swallows All Exceptions in DeleteSession

**File:** `src/QADeviceTool.App/Services/SessionService.cs:332`
**Issue:** `DeleteSession` catches and silently drops all exceptions without any logging. If the directory is locked by another process, the user gets no feedback about WHY the delete failed.

```csharp
catch { }
```

**Fix:** At minimum, log at Debug level:
```csharp
catch (Exception ex) { AppLogger.Log.Debug(ex, "Failed to delete session directory"); }
```

---

## Info

### IN-01: Dead `attempt` Parameter in RunAdbWithRetryAsync

**File:** `src/QADeviceTool.App/Services/AdbService.cs:40`
**Issue:** The `int attempt = 1` parameter is never read in the method body. It appears to be leftover from a prior recursive implementation that was replaced with the for-loop retry pattern.

**Fix:** Remove the unused parameter:
```csharp
private async Task<ToolLauncherResult> RunAdbWithRetryAsync(string arguments, int timeoutMs, Action<string>? outputCallback)
```

---

### IN-02: IosService Hardcodes Tool Names Instead of Using ToolResolver

**File:** `src/QADeviceTool.App/Services/IosService.cs:23-28`
**Issue:** `IosService` stores hardcoded `.exe` names and passes them directly to `ToolLauncher.RunAsync`, which looks for them in `tools\iMobileDevice\`. `AdbService` uses `ToolResolver.Resolve("adb")` which searches all subdirectories of `tools\`. This inconsistency means iOS tools placed in a differently-named directory won't be found.

**Fix:** Use `ToolResolver.Resolve("idevice_id")` etc. to maintain consistency with `AdbService`'s resolution approach.

---

### IN-03: Redundant `|| result.ExitCode == 0` Check

**File:** `src/QADeviceTool.App/Services/IosService.cs:42`
**Issue:** `result.Success` is already defined as `process.ExitCode == 0`. Adding `|| result.ExitCode == 0` is dead logic -- both conditions are identical.

**Fix:** Remove the redundant check:
```csharp
if (result.Success)  // instead of: if (result.Success || result.ExitCode == 0)
```

---

### IN-04: ScrcpyService BitRate Default Mismatch

**File:** `src/QADeviceTool.App/Services/ScrcpyService.cs:90-92`
**Issue:** `ScrcpyOptions.BitRate` defaults to `"2M"`, and the code explicitly skips the `--bit-rate` flag when the value is `"2M"` (line 91: `options.BitRate != "2M"`). However, scrcpy's actual default bitrate when `--bit-rate` is omitted is **8M**, not 2M. A user setting `2M` thinking it's the same as "default" gets 2M, but a user who uses the default gets 8M -- inconsistent behavior.

**Fix:** Either change the default to `"8M"` or remove the `"2M"` skip so the setting is always explicit.

---

### IN-05: GetSavedSessions Ignores Secondary Log Files

**File:** `src/QADeviceTool.App/Services/SessionService.cs:301`
**Issue:** If a session directory contains multiple `.txt` or `.log` files, only `logFiles[0]` is associated with the session. Secondary files (e.g., app-specific logs) are invisible when browsing saved sessions.

```csharp
session.LogFilePath = logFiles[0];  // only first file
```

**Fix:** Consider exposing a `LogFilePaths` collection on the session, or identify the main vs. app log by naming convention.

---

### IN-06: DateTime.Now Called Twice -- Midnight Boundary Bug

**File:** `src/QADeviceTool.App/Helpers/PathHelper.cs:47-48`
**Issue:** Two separate `DateTime.Now` calls can span a midnight boundary. The time format (`hh.mm.sstt`) would reflect the new day while the date format (`dd.MM.yyyy`) still reflects the old day, producing a directory name from two different calendar days.

```csharp
var time = DateTime.Now.ToString("hh.mm.sstt");  // call #1
var date = DateTime.Now.ToString("dd.MM.yyyy");   // call #2 -- may be different day
```

**Fix:** Capture `DateTime.Now` once:
```csharp
var now = DateTime.Now;
var time = now.ToString("hh.mm.sstt");
var date = now.ToString("dd.MM.yyyy");
```

---

### IN-07: InitializeNativePaths Duplicates PATH Entries

**File:** `src/QADeviceTool.App/Helpers/ToolResolver.cs:94-115`
**Issue:** If `InitializeNativePaths()` is called multiple times, the `tools/` subdirectories are prepended to the PATH environment variable each time, causing unbounded PATH growth and potential command resolution issues on very long PATH strings (Windows has a 2047-character limit for the PATH variable).

**Fix:** Guard with a static flag:
```csharp
private static bool _pathsInitialized;
public static void InitializeNativePaths()
{
    if (_pathsInitialized) return;
    _pathsInitialized = true;
    // ... rest of method ...
}
```

---

### IN-08: AlertRule.Regex Lazy Initialization Not Thread-Safe

**File:** `src/QADeviceTool.App/Services/LogAnalyzerService.cs:87-105`
**Issue:** The `Regex` property's `_cachedRegex` null check is not synchronized. Under concurrent access, two threads could both create `Regex` instances. While this doesn't corrupt data (the second one simply overwrites the first -- `Regex` is immutable), it wastes memory and CPU.

**Fix:** Use `Lazy<Regex>`:
```csharp
private Lazy<Regex> _cachedRegex;
public Regex Regex => _cachedRegex.Value;
// Reset on Pattern change: _cachedRegex = new Lazy<Regex>(() => CreateRegex());
```

---

### IN-09: ToolLauncher Depends on Service Locator Anti-Pattern

**File:** `src/QADeviceTool.App/Helpers/ToolLauncher.cs:42,117,135,159`
**Issue:** The static `ToolLauncher` class calls `Services.AppLogger.Log` directly -- a service locator pattern that creates hidden dependencies and prevents unit testing. The `try { } catch { }` wrapping around logging calls (lines 117, 159) confirms this coupling is brittle.

**Fix:** Accept `ILogger` as a parameter or use dependency injection. At minimum, provide an optional logger parameter:
```csharp
public static async Task<ToolLauncherResult> RunAsync(string exeName, string arguments,
    int timeoutMs = 15000, Action<string>? outputCallback = null, ILogger? logger = null)
```

---

_Reviewed: 2026-05-07T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: deep_
