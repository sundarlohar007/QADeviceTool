---
phase: 02-code-review-command
reviewed: 2026-05-07T00:00:00Z
depth: deep
files_reviewed: 22
files_reviewed_list:
  - src/QADeviceTool.App/Models/LogEntry.cs
  - src/QADeviceTool.App/Models/LogSession.cs
  - src/QADeviceTool.App/Models/DeviceInfo.cs
  - src/QADeviceTool.App/Models/DeviceFile.cs
  - src/QADeviceTool.App/Models/AppItem.cs
  - src/QADeviceTool.App/Models/ToolStatus.cs
  - src/QADeviceTool.App/Models/ScrcpyOptions.cs
  - src/QADeviceTool.App/Converters/LogLevelColorMultiConverter.cs
  - src/QADeviceTool.App/Converters/DeviceViewConverters.cs
  - src/QADeviceTool.App/Services/Interfaces/IAdbService.cs
  - src/QADeviceTool.App/Services/Interfaces/IIosService.cs
  - src/QADeviceTool.App/Services/Interfaces/ISessionService.cs
  - src/QADeviceTool.App/Services/Interfaces/IDeviceMonitorService.cs
  - src/QADeviceTool.App/Services/Interfaces/IScrcpyService.cs
  - src/QADeviceTool.App/FeatureFlags.cs
  - src/QADeviceTool.App/Services/AdbService.cs
  - src/QADeviceTool.App/Services/IosService.cs
  - src/QADeviceTool.App/Services/SessionService.cs
  - src/QADeviceTool.App/Services/DeviceMonitorService.cs
  - src/QADeviceTool.App/Services/ScrcpyService.cs
  - src/QADeviceTool.App/ViewModels/SessionViewModel.cs
  - src/QADeviceTool.App/MainWindow.xaml.cs
findings:
  critical: 5
  warning: 12
  info: 4
  total: 21
status: issues_found
---

# Phase 02: Code Review Report -- Models, Converters, Interfaces

**Reviewed:** 2026-05-07
**Depth:** Deep (cross-file analysis including interface conformance, implementation tracing, data flow from capture to export)
**Files Reviewed:** 22 (16 specified targets + 6 implementation files pulled in for interface conformance verification and data flow tracing)
**Status:** issues_found

## Summary

Deep adversarial review of LogPro v2.8.0 Models, Converters, and Interfaces. Traced data flow from raw logcat/iOS syslog output through capture, live display, and CSV/JSON export paths. Verified interface conformance for all 5 service interfaces against their implementations. Cross-referenced FeatureFlags against actual usage.

**Severity breakdown:** 5 BLOCKERS (must fix before shipping), 12 WARNINGS (should fix), 4 INFO items.

**Key systemic issues:**
- **Export data pipeline is broken** -- the CSV/JSON export parser (`ParseLogLine`) expects bracket-format log lines (`[timestamp]`) but the log file contains raw `logcat -v threadtime` output (lines like `12-31 14:23:45.678  1234  5678 F MyApp: ...`). All exported lines get Level="Unknown" and empty Timestamps.
- **Fatal severity downgrade in exports** -- `ParseLogLine` maps `F/` (Fatal) logcat lines to the same branch as `E/` (Error), silently collapsing Fatal into Error in all CSV/JSON exports. Meanwhile, `SessionViewModel.DetectLogLevel` correctly distinguishes Fatal, so live display works but exports don't.
- **Interface hollowing** -- `ISessionService` declares only `CreateSession` and `Export*` but is missing the core capture lifecycle (`StartCaptureAsync`, `StopCapture`, `StopAllCaptures`). Consumers coding against the interface cannot actually capture logs. `IAdbService` similarly omits 10+ methods exposed by `AdbService`.
- **Feature flags are dead** -- `FeatureFlags.AiLogAnalysis` and `FeatureFlags.MultiSelect` only control command palette entries with zero implementation behind them.

---

## Critical Issues

### CR-01: CSV/JSON export parser is fundamentally incompatible with the log format written to file

**File:** `src/QADeviceTool.App/Services/SessionService.cs:447-482`
**Issue:** `ParseLogLine()` -- used by both `ExportToCsvAsync()` and `ExportToJsonAsync()` -- enters its parsing path only when `line.StartsWith("[")` (line 458). However, the log file is written at line 146 with raw `logcat -v threadtime` output (`writer.WriteLineAsync(line)` where `line` comes from `process.StandardOutput.ReadLineAsync()`). The `threadtime` format produces lines like:

```
12-31 14:23:45.678  1234  5678 F MyApp: Something crashed
```

No logcat format produces lines starting with `[`. The bracket-format check always fails, and every exported line falls through to the default: Level="Unknown", Timestamp="" (empty), Message=entire raw line.

**Reproduction:**
1. Start a log capture session
2. Let logcat produce any lines
3. Export to CSV or JSON
4. Observe: all rows have `"Level":"Unknown"` and `"Timestamp":""` with the raw logcat prefix embedded in the Message field

**Fix:** Rewrite `ParseLogLine` to parse raw logcat format. Reuse the proven `DetectLogLevel()` logic from `SessionViewModel` (lines 400-432) which correctly handles all logcat prefixes including `F/`, `E/`, `W/`, `I/`, `D/`, `V/`. Extract timestamps from the threadtime format:

```csharp
private static Dictionary<string, string> ParseLogLine(string line)
{
    var result = new Dictionary<string, string>
    {
        { "Timestamp", "" },
        { "Level", "Unknown" },
        { "Message", line }
    };

    if (string.IsNullOrWhiteSpace(line)) return result;

    try
    {
        var detectedLevel = SessionViewModel.DetectLogLevelStatic(line);
        result["Level"] = detectedLevel.ToString();

        // Parse threadtime timestamp: MM-DD HH:MM:SS.fff
        if (line.Length >= 18)
        {
            var tsCandidate = line.Substring(0, 18);
            if (Regex.IsMatch(tsCandidate, @"^\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3}$"))
            {
                result["Timestamp"] = tsCandidate;
                result["Message"] = line.Substring(18).TrimStart();
            }
        }
    }
    catch { }

    return result;
}
```

(Note: `DetectLogLevel` must be made `public static` or extracted to a shared utility.)

---

### CR-02: Fatal (`F/`) logcat lines are downgraded to "Error" level in CSV/JSON exports

**File:** `src/QADeviceTool.App/Services/SessionService.cs:467-468`
**Issue:** Inside `ParseLogLine`, the level detection groups `F/` (Fatal) together with `E/` (Error) and maps both to `"Error"`:

```csharp
if (rest.Contains(" E ") || rest.Contains(" F ") || rest.StartsWith("E/") || rest.StartsWith("F/"))
    result["Level"] = "Error";
```

Meanwhile, `SessionViewModel.DetectLogLevel()` (line 410-412) correctly handles `F/` as `LogLevel.Fatal`:

```csharp
return trimmed[0] switch
{
    'F' => LogLevel.Fatal,   // Correct
    'E' => LogLevel.Error,
    // ...
};
```

**Impact:** Fatal/crash events exported to CSV/JSON are indistinguishable from ordinary errors. Forensics and post-mortem analysis on exported logs will miss all fatal events. Silent data degradation in the export path.

**Fix:** Add a dedicated Fatal detection branch that runs before the Error branch:

```csharp
if (rest.StartsWith("F/") || rest.Contains("FATAL") || rest.Contains("FTL/"))
    result["Level"] = "Fatal";
else if (rest.Contains(" E ") || rest.StartsWith("E/") || rest.Contains("ERROR"))
    result["Level"] = "Error";
```

---

### CR-03: IosService.ListDirectoryAsync never sets ModifiedDate on DeviceFile entries

**File:** `src/QADeviceTool.App/Services/IosService.cs:292-298`
**Issue:** When constructing `DeviceFile` entries for iOS directory listings, `ModifiedDate` is never assigned:

```csharp
files.Add(new DeviceFile
{
    Name = name,
    Path = path == "/" ? $"/{name}" : $"{path}/{name}",
    IsDirectory = mode.StartsWith("d"),
    Size = size
    // ModifiedDate NOT SET -- defaults to DateTime.MinValue
});
```

`DeviceFile.DisplayDate` (line 39-41 of DeviceFile.cs) checks `ModifiedDate != DateTime.MinValue` and returns empty string when equal. Since `DateTime.MinValue` is the default for uninitialized `DateTime`, all iOS file dates display as blank in the UI.

Compare to the Android path (`AdbService.ListDirectoryAsync`, line 483) which correctly parses and sets `ModifiedDate`.

**Impact:** iOS file browser always shows empty date column. Users cannot determine file modification times on iOS devices. Regression vs Android where dates display correctly.

**Fix:** Parse the date from `afcclient ls -l` output. The output format from afcclient includes date/time fields in specific columns. Extract and parse them:

```csharp
// In IosService.ListDirectoryAsync, after parsing mode/size/name:
// afcclient ls -l shows columns like: mode links owner group size month day time/year name
// Adjust indices based on actual afcclient output format
DateTime date = DateTime.MinValue;
if (parts.Length >= 8)
{
    var monthDay = $"{parts[5]} {parts[6]}";  // e.g., "Jan 15"
    var timeOrYear = parts[7];                 // e.g., "14:30" or "2023"
    var dateStr = $"{monthDay} {timeOrYear}";
    DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}

files.Add(new DeviceFile
{
    // ...
    ModifiedDate = date
});
```

---

### CR-04: ISessionService interface missing core capture lifecycle methods

**Files:**
- `src/QADeviceTool.App/Services/Interfaces/ISessionService.cs` (lines 8-17)
- `src/QADeviceTool.App/Services/SessionService.cs` (lines 64, 185, 229, 257, 281, 312, 322, 339, 341, 353)

**Issue:** The interface declares 4 members: `LogBatchReceived` event, `SessionsRootDirectory` property, `CreateSession`, `ExportToCsvAsync`, and `ExportToJsonAsync`. But the implementation exposes 10 additional public members that form the core session lifecycle:

| Method/Property | In Interface? |
|---|---|
| `StartCaptureAsync(LogSession, LogcatBuffer, LogcatFormat)` | NO |
| `StopCapture(LogSession)` | NO |
| `StopAllCaptures()` | NO |
| `SaveLogToFileAsync(LogSession, string)` | NO |
| `ReadLogContentAsync(LogSession, int)` | NO |
| `GetSavedSessions()` | NO |
| `DeleteSession(LogSession)` | NO |
| `HasActiveCapture` (property) | NO |
| `GetActiveSessionForDevice(string)` | NO |
| `StopCaptureForDevice(string, IEnumerable<LogSession>)` | NO |

**Impact:** Any consumer coding against `ISessionService` cannot start or stop log capture, read log content, list saved sessions, or delete sessions. The interface provides only session creation and export -- but session capture is unreachable through the interface. This makes the interface effectively useless for the primary use case and prevents unit testing of capture-dependent code.

Note: Currently, consumers use the concrete `SessionService` type directly (confirmed by grep -- `ISessionService` has zero matches as a variable type), so the missing members don't cause runtime crashes. But this defeats the purpose of having an interface.

**Fix:** Add missing members to `ISessionService`:

```csharp
public interface ISessionService
{
    event Action<string>? LogBatchReceived;
    string SessionsRootDirectory { get; set; }

    LogSession CreateSession(DeviceInfo device, string? customSessionName = null);
    Task<bool> StartCaptureAsync(LogSession session, LogcatBuffer buffer = LogcatBuffer.Main,
        LogcatFormat format = LogcatFormat.ThreadTime);
    void StopCapture(LogSession session);
    void StopAllCaptures();
    Task<string> SaveLogToFileAsync(LogSession session, string logContent);
    Task<string> ReadLogContentAsync(LogSession session, int maxLines = 200000);
    List<LogSession> GetSavedSessions();
    bool DeleteSession(LogSession session);
    bool HasActiveCapture { get; }
    LogSession? GetActiveSessionForDevice(string deviceSerial);
    LogSession? StopCaptureForDevice(string deviceSerial, IEnumerable<LogSession> sessions);
    Task<bool> ExportToCsvAsync(LogSession session, string outputPath, bool anonymize = false);
    Task<bool> ExportToJsonAsync(LogSession session, string outputPath, bool anonymize = false);
}
```

Also: `IAdbService` is similarly hollow -- missing 10+ methods that `AdbService` exposes (`GetDevicePropertyAsync`, `ListInstalledAppsAsync`, `UninstallAppAsync`, `ForceStopAppAsync`, `ClearAppDataAsync`, `GetAppDetailsAsync`, `SetDeviceClipboardAsync`, `GetDeviceClipboardAsync`, `SendNotificationAsync`, `StartScreenRecordAsync`, `StopScreenRecordAsync`, `IsScreenRecording`). Same for `IIosService` (missing `UninstallAppAsync`).

---

### CR-05: LogEntry.LogLevel serialization default conflicts with intended default

**File:** `src/QADeviceTool.App/Models/LogEntry.cs:40`
**Issue:** `LogEntry.Level` has the explicit default `LogLevel.Unknown` (enum value 6). However, `LogLevel.Verbose` is the first enum member (value 0). If a `LogEntry` is JSON-deserialized and the `"Level"` field is absent from the JSON, `System.Text.Json` assigns `default(LogLevel)` which is `LogLevel.Verbose` (0) -- NOT the property initializer's `LogLevel.Unknown` (6). Property initializers run only during `new LogEntry()` construction, not during deserialization.

**Impact:** Any deserialized log entry with a missing "Level" field silently gets `Verbose` severity instead of `Unknown`. This causes Verbose-colored rendering and wrong filtering for entries where the level was genuinely unknown or unparseable. If session save/load is ever implemented (persisting and restoring log entries), level information is silently corrupted.

The same issue exists for any future API that returns `LogEntry` as JSON -- missing `level` in the payload means `Verbose` instead of `Unknown`.

**Fix:** Reorder the enum so `Unknown = 0` is the default value:

```csharp
public enum LogLevel
{
    Unknown = 0,  // Default -- must be zero for safe serialization defaults
    Verbose,
    Debug,
    Info,
    Warning,
    Error,
    Fatal
}
```

All existing switch expressions already have `_ =>` default arms or explicit `Unknown` cases, so reordering will not break existing logic.

---

## Warnings

### WR-01: AdbService passes unsanitized `serial` string into shell commands throughout

**File:** `src/QADeviceTool.App/Services/AdbService.cs` -- lines 276, 383, 458, 506, 512, 518, 551, 558, 563, 579, 598, 606
**Issue:** The `serial` parameter (derived from `DeviceInfo.Serial`, which is populated from raw ADB device listing output -- see line 135: `var serial = parts[0]`) is directly interpolated into ADB shell command strings without validation:

```csharp
var result = await RunAdbAsync($"-s {serial} shell ps -A -o PID,NAME", 10000);  // line 383
```

ADB serial numbers are typically alphanumeric (e.g., `emulator-5554`, `R58M35QN7FV`), but a spoofed ADB daemon or a malicious device can return arbitrary strings. The serial is taken from the first whitespace-delimited token of the `adb devices -l` output with no validation. A serial like `emulator-5554; rm -rf /` would inject shell commands.

**Impact:** Low-probability but high-impact command injection. Mitigated somewhat by the fact that serials come from local ADB output (not remote input), but ADB daemons can be impersonated.

**Fix:** Validate `serial` against a safe character set before any shell command use:

```csharp
private static readonly Regex SafeSerialPattern = new(@"^[A-Za-z0-9\-_.:]+$",
    RegexOptions.Compiled);

// At the top of each method using serial:
if (!SafeSerialPattern.IsMatch(serial))
{
    AppLogger.Log.Warn("[AdbService] Rejected unsafe serial: {Serial}", serial);
    return defaultValue; // or throw ArgumentException
}
```

---

### WR-02: LogSession properties (except Status) do not raise PropertyChanged

**Files:**
- `src/QADeviceTool.App/Models/LogSession.cs:10-28` (10 plain auto-properties)
- `src/QADeviceTool.App/Services/SessionService.cs:105, 216` (mutation points)

**Issue:** `LogSession` extends `ObservableObject` but uses `[ObservableProperty]` only on `_status`. All other properties (`Id`, `Name`, `StartTime`, `EndTime`, `DeviceId`, `DeviceSerial`, `DeviceName`, `Platform`, `LogFilePath`, `AppLogFilePath`, `SessionDirectory`, `LogLineCount`) are plain auto-properties. When `SessionService` mutates them, no notification fires:

```csharp
session.StartTime = DateTime.Now;   // line 105 -- no PropertyChanged
session.EndTime = DateTime.Now;     // line 216 -- no PropertyChanged
session.LogLineCount++;             // line 156 -- no PropertyChanged
```

`DurationText` is notified only via `[NotifyPropertyChangedFor(nameof(DurationText))]` on `Status`. When `Status` and `EndTime` are set together (as in `StopCapture`), the notification works. But if `EndTime` is ever set independently or if `DurationText` is bound while `Status` stays unchanged, it goes stale.

**Impact:** UI bindings to `Name`, `EndTime`, `LogLineCount`, etc. will not refresh. `DurationText` becomes stale if `EndTime` changes without a simultaneous `Status` change. This is a latent UI staleness bug.

**Fix:** Convert mutation-sensitive properties to observable pattern:

```csharp
[ObservableProperty]
[NotifyPropertyChangedFor(nameof(DurationText))]
private DateTime _startTime = DateTime.Now;

[ObservableProperty]
[NotifyPropertyChangedFor(nameof(DurationText))]
private DateTime? _endTime;

[ObservableProperty]
private long _logLineCount;
```

Or, at minimum, fire `OnPropertyChanged(nameof(EndTime))` and `OnPropertyChanged(nameof(DurationText))` after mutation in SessionService.

---

### WR-03: IosService.StartLogCapture ignores its `outputFilePath` parameter

**File:** `src/QADeviceTool.App/Services/IosService.cs:154-165`
**Issue:** The method signature accepts `outputFilePath` but completely ignores it:

```csharp
public System.Diagnostics.Process? StartLogCapture(string udid, string outputFilePath)
{
    try
    {
        return ToolLauncher.StartLongRunning(_ideviceSyslog, $"-u {udid}");
        // outputFilePath is never used
    }
    // ...
}
```

File output is handled separately by `SessionService` which opens its own `StreamWriter`. The unused parameter creates a misleading API contract -- callers might assume the service handles file redirection.

**Impact:** Callers who depend on the parameter doing anything get silently different behavior. The parameter is dead weight that misleads about the method's responsibilities.

**Fix:** Either remove the unused parameter, or implement file redirection:

```csharp
Process? StartLogCapture(string serial, string logFilePath); // Remove if unused

// OR implement it:
Process? StartLogCapture(string serial, string logFilePath);
```

(If implementing: redirect stdout to the file in a background task.)

---

### WR-04: DeviceMonitorService Timer uses async void with no back-pressure

**File:** `src/QADeviceTool.App/Services/DeviceMonitorService.cs:41-45`
**Issue:** `System.Threading.Timer` fires on threadpool threads and does not understand `async`. The callback uses `async void`:

```csharp
_pollTimer = new Timer(async _ =>
{
    try { await PollDevicesAsync(); }
    catch (Exception ex) { AppLogger.Log.Error(ex, "[DeviceMonitor] Poll timer crashed"); }
}, null, 2000, intervalMs);
```

If `PollDevicesAsync` takes longer than `intervalMs`, the timer fires again while the previous poll runs. The `Interlocked.Exchange` guard on `_isPolling` (line 62) prevents concurrent `PollDevicesAsync` body execution, but the timer still queues callbacks. Under slow device enumeration (many devices, wireless latency), dozens of callbacks stack up and immediately return when the guard is hit.

Also, `async void` cannot be awaited and unhandled exceptions in the state machine beyond the synchronous `try/catch` boundary can crash the process.

**Impact:** Threadpool exhaustion under slow enumeration. Delayed device detection due to stacked callbacks. Process crash risk from unhandled async exceptions.

**Fix:** Replace `System.Threading.Timer` with a `Task.Run` + `Task.Delay` loop, or use `PeriodicTimer`:

```csharp
private CancellationTokenSource? _pollCts;

public void StartMonitoring(int intervalMs = 10000)
{
    StopMonitoring();
    _pollCts = new CancellationTokenSource();
    _ = Task.Run(async () =>
    {
        await Task.Delay(2000, _pollCts.Token);
        while (!_pollCts.Token.IsCancellationRequested)
        {
            try { await PollDevicesAsync(); }
            catch (Exception ex) { AppLogger.Log.Error(ex, "[DeviceMonitor] Poll failed"); }
            await Task.Delay(intervalMs, _pollCts.Token);
        }
    });
}
```

---

### WR-05: IntToBoolConverter returns false for negative integers

**File:** `src/QADeviceTool.App/Converters/DeviceViewConverters.cs:13`
**Issue:** `return intVal > 0;` maps negative integers (e.g., `-1` commonly used as a sentinel/error indicator) to `false`. If a ViewModel uses `-1` to signal "uninitialized" or "error" state, the converter masks the error by showing it as disabled/off:

```csharp
if (value is int intVal)
    return intVal > 0;
```

**Impact:** UI elements bound via this converter will show "inactive" for error-state values. An error indicator that should draw attention becomes invisible. Silent masking of sentinel values.

**Fix:** Use `!= 0` or handle the `-1` case explicitly:

```csharp
if (value is int intVal)
    return intVal != 0;
```

---

### WR-06: Three DeviceViewConverters throw NotImplementedException on ConvertBack

**File:** `src/QADeviceTool.App/Converters/DeviceViewConverters.cs:19-21, 33-36, 67-69`
**Issue:** `IntToBoolConverter`, `NullToBoolConverter`, and `BooleanToPlayPauseConverter` all throw `NotImplementedException` in `ConvertBack`. If any of these converters are used in `Mode=TwoWay` bindings, the application crashes at runtime when WPF attempts the reverse conversion.

**Impact:** Any XAML binding using `TwoWay` mode (e.g., `ToggleButton.IsChecked` bound through `IntToBoolConverter`) will throw `NotImplementedException` and crash. This is a latent crash risk that any developer adding a two-way binding will trigger.

**Fix:** Implement `ConvertBack` or explicitly return `Binding.DoNothing`:

```csharp
// IntToBoolConverter:
public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
{
    if (value is bool b) return b ? 1 : 0;
    return Binding.DoNothing;
}

// NullToBoolConverter (one-way only -- can't convert back):
public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    => Binding.DoNothing;

// BooleanToPlayPauseConverter (one-way only):
public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    => Binding.DoNothing;
```

---

### WR-07: SessionService and DeviceMonitorService depend on concrete types instead of interfaces

**Files:**
- `src/QADeviceTool.App/Services/SessionService.cs:17-18, 30`
- `src/QADeviceTool.App/Services/DeviceMonitorService.cs:11-12, 29`

**Issue:** Both services accept `AdbService` and `IosService` (concrete classes) as constructor parameters:

```csharp
public SessionService(AdbService adbService, IosService iosService)
```

This prevents dependency injection of mock/stub implementations for unit testing. It also tightly couples these services to exact implementations, violating the Dependency Inversion Principle.

**Impact:** Unit testing `SessionService` and `DeviceMonitorService` requires real ADB/iOS tooling. Cannot substitute test doubles.

**Fix:** Change constructor parameters to interfaces:

```csharp
private readonly IAdbService _adbService;
private readonly IIosService _iosService;

public SessionService(IAdbService adbService, IIosService iosService)
{
    _adbService = adbService;
    _iosService = iosService;
}
```

---

### WR-08: ScrcpyOptions.BitRate regex rejects decimal bitrates (e.g., "1.5M")

**File:** `src/QADeviceTool.App/Services/ScrcpyService.cs:90-91`
**Issue:** The bitrate validation regex allows only integer values:

```csharp
if (Regex.IsMatch(options.BitRate, @"^\d+[KMG]?$"))
    args += $" --bit-rate={options.BitRate}";
```

Valid scrcpy bitrates like `"1.5M"`, `"0.8M"`, `"800K"` are rejected because the regex `^\d+[KMG]?$` requires a contiguous run of digits with no decimal point.

Additionally, the check `options.BitRate != "2M"` on line 90 causes the default value to be silently dropped even if the user explicitly set it, potentially overriding a scrcpy config default that differs from 2M.

**Impact:** Users cannot configure decimal bitrates. Explicitly setting `BitRate = "2M"` silently produces no `--bit-rate` argument.

**Fix:** Accept decimal values and remove the default-equals check:

```csharp
if (!string.IsNullOrEmpty(options.BitRate)
    && Regex.IsMatch(options.BitRate, @"^\d+(\.\d+)?[KMG]?$"))
    args += $" --bit-rate={options.BitRate}";
```

---

### WR-09: DeviceFile.DisplaySize uses repeated magic number `1024` across four thresholds

**File:** `src/QADeviceTool.App/Models/DeviceFile.cs:24-27`
**Issue:** The `DisplaySize` property repeats the literal `1024` and its powers:

```csharp
if (Size < 1024) return $"{Size} B";
if (Size < 1024 * 1024) return $"{Size / 1024.0:F1} KB";
if (Size < 1024 * 1024 * 1024) return $"{Size / (1024.0 * 1024.0):F2} MB";
return $"{Size / (1024.0 * 1024.0 * 1024.0):F2} GB";
```

The repeated arithmetic is fragile -- a typo in any of the 8 occurrences of `1024` silently produces wrong sizes. Although the compiler optimizes these to constants, each is hand-typed separately.

**Impact:** Any copy-paste error in the repeated `1024` literals causes incorrect size display. The current values appear correct but are brittle to modification.

**Fix:** Use named constants:

```csharp
private const long KB = 1024L;
private const long MB = KB * 1024L;
private const long GB = MB * 1024L;

public string DisplaySize
{
    get
    {
        if (IsDirectory) return string.Empty;
        if (Size < KB) return $"{Size} B";
        if (Size < MB) return $"{Size / (double)KB:F1} KB";
        if (Size < GB) return $"{Size / (double)MB:F2} MB";
        return $"{Size / (double)GB:F2} GB";
    }
}
```

---

### WR-10: LogEntry class is not observable despite UI data template binding

**File:** `src/QADeviceTool.App/Models/LogEntry.cs:37-50`
**Issue:** `LogEntry` does not implement `INotifyPropertyChanged`. It is stored in `ObservableCollection<LogEntry>` in `SessionViewModel.LogEntries`, so the collection notifies on add/remove, but individual property changes (e.g., `IsBookmarked = true` on line 44) do not fire any notification.

**Impact:** If a user bookmarks a log entry (sets `IsBookmarked = true`), the UI data template bound to `IsBookmarked` will not update the bookmark icon. The user sees no visual feedback for the bookmark action until the row is recycled or the collection is refreshed. This creates a "stale UI" bug where bookmark toggling appears broken.

**Fix:** Make `LogEntry` extend `ObservableObject` and convert `IsBookmarked` to an observable property:

```csharp
public partial class LogEntry : ObservableObject
{
    public string Timestamp { get; set; } = string.Empty;
    public LogLevel Level { get; set; } = LogLevel.Unknown;
    // ... other plain properties ...

    [ObservableProperty]
    private bool _isBookmarked;
}
```

---

### WR-11: ToolStatus.StatusColor returns string color name instead of Brush

**File:** `src/QADeviceTool.App/Models/ToolStatus.cs:16`
**Issue:** `StatusColor` returns `"Green"` or `"Red"` as a `string`:

```csharp
public string StatusColor => IsInstalled ? "Green" : "Red";
```

WPF's built-in `ColorConverter` can convert these to `SolidColorBrush` at binding time, but this depends on implicit framework conversion. If bound to a non-Brush property or used in code-behind, it breaks. This is a "stringly-typed" API anti-pattern.

**Impact:** Fragile binding -- any change to the XAML binding target type or removal of implicit conversion causes a runtime binding failure. Code-behind consumers cannot use this as a Brush without manual parsing.

**Fix:** Return `SolidColorBrush` directly:

```csharp
private static readonly SolidColorBrush GreenBrush = new(Colors.Green);
private static readonly SolidColorBrush RedBrush = new(Colors.Red);

public SolidColorBrush StatusColor => IsInstalled ? GreenBrush : RedBrush;
```

---

### WR-12: IosService hardcodes `.exe` suffix on all tool filenames -- not portable

**File:** `src/QADeviceTool.App/Services/IosService.cs:23-28`
**Issue:** Tool executable names are hardcoded with Windows `.exe` extension:

```csharp
_ideviceId = "idevice_id.exe";
_ideviceInfo = "ideviceinfo.exe";
_ideviceSyslog = "idevicesyslog.exe";
_ideviceScreenshot = "idevicescreenshot.exe";
_ideviceInstaller = "ideviceinstaller.exe";
_afcClient = "afcclient.exe";
```

Unlike `AdbService` (line 29) which uses `ToolResolver.Resolve("adb")` (platform-aware path resolution), `IosService` uses bare filenames with hardcoded `.exe`. On macOS and Linux, libimobiledevice binaries have no extension.

**Impact:** iOS device support is completely non-functional on macOS and Linux. The `CheckAvailabilityAsync` method will report "not found" for all iOS tools on non-Windows platforms because the files `idevice_id.exe` etc. don't exist -- the actual binaries are named `idevice_id` (no extension).

**Fix:** Use `ToolResolver.Resolve()` for each tool, consistent with `AdbService`:

```csharp
public IosService()
{
    _ideviceId = ToolResolver.Resolve("idevice_id");
    _ideviceInfo = ToolResolver.Resolve("ideviceinfo");
    _ideviceSyslog = ToolResolver.Resolve("idevicesyslog");
    _ideviceScreenshot = ToolResolver.Resolve("idevicescreenshot");
    _ideviceInstaller = ToolResolver.Resolve("ideviceinstaller");
    _afcClient = ToolResolver.Resolve("afcclient");
}
```

If `ToolResolver.Resolve` does not handle these tools, extend it to do so (or use `PathHelper` to locate tools in the platform-appropriate way).

---

## Info

### IN-01: FeatureFlags control only command palette entries -- no implementation exists

**Files:**
- `src/QADeviceTool.App/FeatureFlags.cs` (lines 7-18)
- `src/QADeviceTool.App/MainWindow.xaml.cs` (lines 51, 56)

**Issue:** `FeatureFlags.AiLogAnalysis` and `FeatureFlags.MultiSelect` are checked only in `MainWindow.xaml.cs` to conditionally add command palette entries. A grep of the entire `src` directory confirms zero other usages. No AI log analysis implementation exists. No multi-device selection logic exists. Setting these flags to `true` shows non-functional UI commands.

**Impact:** Enabling these public-settable flags adds command palette entries that execute no action. Confusing to end users and misleading to developers who may assume the feature is implemented behind the flag. Dead code with a public API surface.

**Fix:** Either implement the features behind these flags, or remove the flags and the unused command palette entries until the features are ready for release.

---

### IN-02: LogLevelColorMultiConverter ignores CultureInfo parameter

**File:** `src/QADeviceTool.App/Converters/LogLevelColorMultiConverter.cs:32`
**Issue:** The `culture` parameter in the `Convert` method is declared but never used. While terminal/log colors are not culture-dependent, the unused parameter violates the `IMultiValueConverter` contract. Some static analysis tools flag this.

**Fix:** Add an explicit discard to document the intent:

```csharp
public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
{
    _ = culture; // Terminal color mapping is culture-invariant
    // ...
}
```

---

### IN-03: BooleanToPlayPauseConverter uses Unicode glyphs with platform-dependent rendering

**File:** `src/QADeviceTool.App/Converters/DeviceViewConverters.cs:62-63`
**Issue:** The converter returns strings containing Unicode code points U+23F8 (double vertical bar) and U+25B6 (black right-pointing triangle). These glyphs are not guaranteed in all fonts. On older Windows versions or custom WPF font configurations, they render as tofu (empty rectangles).

**Impact:** On systems without these glyphs in the UI font, the play/pause button text displays as empty boxes. Cosmetic but user-facing.

**Fix:** Use plain text labels ("Pause" / "Play") for universal compatibility, or use Segoe MDL2 Assets / Material Design icon font codes consistently with other parts of the application that use Unicode icon codes.

---

### IN-04: IAdbService has redundant parallel wireless connection APIs

**Files:**
- `src/QADeviceTool.App/Services/Interfaces/IAdbService.cs:23-29`
- `src/QADeviceTool.App/Services/AdbService.cs:418-646`

**Issue:** The interface declares two parallel sets of wireless methods:
- `EnableWirelessAsync(serial, port)` / `ConnectWirelessAsync(ipAddress, port)` / `DisconnectWirelessAsync(ipAddress, port)` -- traditional TCP/IP wireless ADB
- `PairAsync(ipPort, code)` / `ConnectAsync(ipPort)` / `DisconnectAsync(ipPort)` -- pairing-code flow (Android 11+)

While these serve different ADB versions, the API surface is confusing. Parameter naming is inconsistent (`serial` + `port` as separate params vs `ipPort` as a combined `"IP:port"` string). No XML doc distinguishes which to use for which Android version.

**Impact:** Developers must know which method pair to call for which Android version. Wrong selection results in silently failed wireless connections with confusing error messages.

**Fix:** Add XML documentation comments distinguishing the two workflows, or consolidate into a `ConnectWirelessAsync` overload with an options enum/parameter indicating the pairing method.

---

_Reviewed: 2026-05-07T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: deep_
