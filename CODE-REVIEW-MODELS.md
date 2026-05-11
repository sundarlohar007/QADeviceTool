---
phase: code-review-models-converters-interfaces-codebehind
reviewed: 2026-05-05T22:00:00Z
depth: standard
files_reviewed: 18
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
  - src/QADeviceTool.App/Services/Interfaces/IDeviceMonitorService.cs
  - src/QADeviceTool.App/Services/Interfaces/IIosService.cs
  - src/QADeviceTool.App/Services/Interfaces/IScrcpyService.cs
  - src/QADeviceTool.App/Services/Interfaces/ISessionService.cs
  - src/QADeviceTool.App/FeatureFlags.cs
  - src/QADeviceTool.App/App.xaml
  - src/QADeviceTool.App/App.xaml.cs
  - src/QADeviceTool.App/MainWindow.xaml.cs
findings:
  critical: 3
  warning: 8
  info: 6
  total: 17
status: issues_found
---

# Code Review Report: LogPro v2.8.0 Models, Converters, Interfaces, and Code-Behind

**Reviewed:** 2026-05-05T22:00:00Z
**Depth:** standard
**Files Reviewed:** 18
**Status:** issues_found

## Summary

Reviewed 18 files spanning the Model layer (7 files), WPF value converters (2 files), service interfaces (5 files), feature flags, App startup code-behind, and MainWindow code-behind. The review found 17 issues: 3 Critical, 8 Warning, 6 Info.

Key concerns:

1. The `LogLevel.Verbose` enum value is a misspelling that propagates through 9+ locations across the entire codebase. The string-based log level fallback parsing (`SessionViewModel.cs:429`) checks for "VERBOSE" (misspelled) and would NOT match the correctly-spelled "VERBOSE" from any external log source.

2. `NullToBoolConverter` returns `true` for `DependencyProperty.UnsetValue`, a binding correctness defect that causes bindings to report `true` before their source is resolved.

3. `FeatureFlags` has no mechanism to enable any flags. Both `AiLogAnalysis` and `MultiSelect` are always `false`, making the conditional blocks in `MainWindow.xaml.cs` permanently dead code.

4. Six model classes lack `INotifyPropertyChanged` despite being used as WPF bindable objects in `ObservableCollection<T>` instances across 12+ ViewModels. Property changes (including `LogEntry.IsBookmarked` toggles) are silently lost to the UI.

---

## Critical Issues

### CR-01: LogLevel enum misspelling "Verbose" breaks string-based log level parsing

**File:** `src/QADeviceTool.App/Models/LogEntry.cs:7`
**Issue:** The `LogLevel` enum member is named `Verbose` but the correct English word and standard Android logcat spelling is "Verbose". This misspelling propagates to at least 9 locations across the codebase:

| File | Line(s) | Usage |
|------|---------|-------|
| `LogEntry.cs` | 7 | Enum definition: `Verbose,` |
| `LogLevelColorMultiConverter.cs` | 29 | `VerboseBg` field |
| `LogLevelColorMultiConverter.cs` | 49 | Switch case: `LogLevel.Verbose =>` |
| `SessionViewModel.cs` | 42 | Default filter level |
| `SessionViewModel.cs` | 157 | Filter item initialization |
| `SessionViewModel.cs` | 417 | Char mapping: `'V' => LogLevel.Verbose` |
| `SessionViewModel.cs` | 429 | String fallback: `"VERBOSE"` |
| `SessionService.cs` | 475 | CSV export: `"Verbose"` |
| `DarkTheme.xaml` | 42, 75 | Theme color and brush |

The char-based mapping (`'V'`) at SessionViewModel.cs:417 correctly handles standard ADB `logcat -v threadtime` format, but the string fallback at line 429 tests `upper.Contains("VERBOSE")`. A correctly-spelled "VERBOSE" from any external log source, third-party tool, or file would silently fail to match. This is a silent data misclassification bug. Every log line with a verbose level from a non-ADB source gets classified as whatever the caller defaults to.

**Fix:** Rename the enum member to `Verbose` (correct spelling) and update all 9+ referencing locations. The string fallback should also use correct spelling:
```csharp
// SessionViewModel.cs:429
if (upper.Contains("VERBOSE") || upper.StartsWith("VRB")) return LogLevel.Verbose;
```

---

### CR-02: NullToBoolConverter returns true for DependencyProperty.UnsetValue

**File:** `src/QADeviceTool.App/Converters/DeviceViewConverters.cs:28-31`
**Issue:** The `Convert` method checks `value != null` and returns `true` for any non-null value. `DependencyProperty.UnsetValue` is a static sentinel object that WPF binding engine passes when a binding source is not yet resolved. Since `UnsetValue` is a valid object reference (not null), the converter returns `true`. During the momentary window between binding creation and source resolution, any UI element using this converter will display its "true" state, causing flicker or incorrect initial rendering.

This also affects the logical meaning: a binding that has not yet resolved should be treated as "not present" (false), not "present" (true).

**Fix:**
```csharp
public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
{
    if (value == null || value == DependencyProperty.UnsetValue)
        return false;
    return true;
}
```

---

### CR-03: FeatureFlags dead code -- both flags are permanently disabled with no toggle mechanism

**File:** `src/QADeviceTool.App/FeatureFlags.cs:12,17`
**Issue:** Both `AiLogAnalysis` and `MultiSelect` are public static settable properties initialized to `false` and never set to `true` anywhere in the codebase. The only read sites are in `MainWindow.xaml.cs:51` and `:56`:

```csharp
if (FeatureFlags.AiLogAnalysis)
    _commandPalette.AddCommand("ai:analyze", "AI Log Analysis", ...);

if (FeatureFlags.MultiSelect)
    _commandPalette.AddCommand("action:selectAll", "Select All Devices", ...);
```

These conditional blocks are permanently dead code paths. There is:
- No config file binding (appsettings.json, XML config)
- No environment variable reading
- No registry or app settings integration
- No admin toggle UI in the Settings view
- No command-line argument parsing

The feature flag system is non-functional. It creates the illusion of gated features while providing no mechanism to ever un-gate them.

**Fix:** Either wire the flags to a real configuration source or remove the class and conditionals:
```csharp
// Option A: Wire to configuration
// In App.xaml.cs OnStartup
FeatureFlags.AiLogAnalysis = ConfigurationHelper.GetBool("FeatureFlags:AiLogAnalysis", false);
FeatureFlags.MultiSelect = ConfigurationHelper.GetBool("FeatureFlags:MultiSelect", false);

// Option B: Remove until infrastructure exists
// Delete FeatureFlags.cs and remove the two if-blocks in MainWindow.xaml.cs
```

---

## Warnings

### WR-01: Six model classes missing INotifyPropertyChanged -- UI property changes are silently lost

**Files:**
- `src/QADeviceTool.App/Models/LogEntry.cs:37-50`
- `src/QADeviceTool.App/Models/DeviceInfo.cs:6-34`
- `src/QADeviceTool.App/Models/DeviceFile.cs:8-42`
- `src/QADeviceTool.App/Models/AppItem.cs:3-9`
- `src/QADeviceTool.App/Models/ToolStatus.cs:6-17`
- `src/QADeviceTool.App/Models/ScrcpyOptions.cs:3-13`

**Issue:** All six model classes are plain CLR objects with public `{ get; set; }` auto-properties. None implement `INotifyPropertyChanged`. These types are placed in `ObservableCollection<T>` instances across at least 12 ViewModels. While `ObservableCollection` fires `CollectionChanged` on add/remove/replace, it does NOT propagate `PropertyChanged` from individual items.

Concrete impact:
- `LogEntry.IsBookmarked` is toggled at `SessionViewModel.cs:1040` -- the bookmark icon in the DataGrid row will NOT update until the collection is manually refreshed or the row is re-virtualized.
- `DeviceInfo.ConnectionState` changes via polling -- the status indicator in all device lists will NOT update for individual items.
- `ToolStatus.IsInstalled` updates -- the availability indicators will NOT refresh.
- All computed display properties (`DisplayName`, `DisplayNotes`, `PlatformIcon`, `StatusText`, `StatusIcon`, `StatusColor`, `DisplaySize`, `DisplayDate`) are read-only getters whose underlying source properties do not notify.

**Fix:** Implement `INotifyPropertyChanged` on all model classes. Since the project already depends on CommunityToolkit.Mvvm (used by `LogSession`), the cleanest approach is:
```csharp
// Option A: Use ObservableObject base class
public partial class DeviceInfo : ObservableObject
{
    [ObservableProperty]
    private string _serial = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;
    // ...
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Model : Name;
}

// Option B: Manual INotifyPropertyChanged
public class DeviceInfo : INotifyPropertyChanged
{
    private string _serial = string.Empty;
    public string Serial
    {
        get => _serial;
        set { _serial = value; PropertyChanged?.Invoke(this, new(nameof(Serial))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
```
For computed display properties, call `OnPropertyChanged(nameof(DisplayName))` whenever the source property (Name, Model) changes.

---

### WR-02: LogSession has 12 non-notifying public auto-properties despite deriving from ObservableObject

**File:** `src/QADeviceTool.App/Models/LogSession.cs:10-20,27`
**Issue:** `LogSession` derives from CommunityToolkit.Mvvm `ObservableObject` and correctly uses `[ObservableProperty]` on `_status` (line 25) with `[NotifyPropertyChangedFor]` chaining to `StatusIcon` and `DurationText`. However, twelve other public properties are plain auto-properties without source-generator backing fields:

- `Id` (line 10)
- `Name` (line 11)
- `StartTime` (line 12)
- `EndTime` (line 13)
- `DeviceId` (line 14)
- `DeviceSerial` (line 15)
- `DeviceName` (line 16)
- `Platform` (line 17)
- `LogFilePath` (line 18)
- `AppLogFilePath` (line 19)
- `SessionDirectory` (line 20)
- `LogLineCount` (line 27)

When `SessionService.CreateSession` (SessionService.cs:48-59) populates these properties via object initializer, or when any external code sets them, no `PropertyChanged` event fires. If a ViewModel later updates `LogLineCount` or `EndTime`, the UI binding will not see the change.

**Fix:** Convert all settable properties to source-generator pattern:
```csharp
[ObservableProperty]
private string _name = string.Empty;

[ObservableProperty]
private DateTime _startTime = DateTime.Now;

[ObservableProperty]
private long _logLineCount;
```

---

### WR-03: App.xaml.cs exception handlers call potentially uninitialized AppLogger

**File:** `src/QADeviceTool.App/App.xaml.cs:80-108`
**Issue:** The three global exception handlers call `Services.AppLogger.Log.Fatal(...)` and `Services.AppLogger.Log.Error(...)`. If the application fails before `AppLogger` is initialized (e.g., during `ToolResolver.InitializeNativePaths()` at line 50, or if the DI container throws during service resolution), these handlers will throw a `NullReferenceException` themselves. The original exception is swallowed by the handler crash, and the application terminates with no diagnostic output -- the crash reason is completely masked.

Additionally, the `OnStartup` try/catch at lines 74-77 logs to the early log file but shows no message box to the user. If startup initialization fails, the user sees the application silently exit with zero visual feedback.

**Fix:** Add null guards around all `AppLogger` calls in exception handlers:
```csharp
private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
{
    EarlyLog("DispatcherUnhandledException caught!", e.Exception);
    try
    {
        Services.AppLogger?.Log?.Fatal(e.Exception, "DispatcherUnhandledException");
    }
    catch { /* AppLogger may not be initialized */ }

    MessageBox.Show(
        $"An error occurred:\n\n{e.Exception.Message}\n\nCheck startup log at:\n{EarlyLogPath}",
        "LogPro - Error",
        MessageBoxButton.OK,
        MessageBoxImage.Warning);

    e.Handled = true;
}
```
Apply the same null-guarded pattern to `CurrentDomain_UnhandledException` and `TaskScheduler_UnobservedTaskException`. Also add a `MessageBox.Show` in the `OnStartup` catch block at line 76 so the user knows the app failed.

---

### WR-04: App.xaml theme reference -- load failure causes unhandled crash before exception handlers

**File:** `src/QADeviceTool.App/App.xaml:8`
**Issue:** The XAML root declares `<ResourceDictionary Source="Themes/DarkTheme.xaml" />` in the merged dictionaries. If this file is missing, corrupted, or contains invalid XAML, WPF throws a `XamlParseException` during application initialization -- before `OnStartup` executes and before any exception handlers are registered. This produces a hard crash with no user-visible diagnostic and no entry in the early log file (since `App` never constructs fully).

While `DarkTheme.xaml` currently exists on disk, this is a deployment-time risk: an incomplete installer, file system corruption, or accidental deletion would produce a cryptic crash dialog.

**Fix:** Load the theme resource dictionary programmatically in `OnStartup` with error handling:
```csharp
// In App.xaml.cs OnStartup, BEFORE base.OnStartup(e):
try
{
    var themeDict = new ResourceDictionary
    {
        Source = new Uri("Themes/DarkTheme.xaml", UriKind.Relative)
    };
    Resources.MergedDictionaries.Add(themeDict);
}
catch (Exception ex)
{
    EarlyLog("FATAL: Cannot load theme", ex);
    MessageBox.Show("Failed to load application theme. Please reinstall LogPro.",
        "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
    Shutdown(1);
    return;
}
```
Then remove the `<ResourceDictionary Source="Themes/DarkTheme.xaml" />` line from `App.xaml`.

---

### WR-05: CommandPaletteWindow event lambda captures MainWindow in strong circular reference

**File:** `src/QADeviceTool.App/MainWindow.xaml.cs:62`
**Issue:**
```csharp
_commandPalette.WindowClosed += () => _commandPalette = null;
```
This lambda captures `this` (MainWindow), creating the following reference chain:
`MainWindow` (root) -> `_commandPalette` (field) -> `WindowClosed` (event delegate list) -> `delegate` -> `this` (closure captures MainWindow again)

This is a strong circular reference. The `CommandPaletteWindow` cannot be garbage collected after closing -- its event subscription keeps the MainWindow alive, and MainWindow's field keeps the CommandPaletteWindow alive. Both objects live until the entire MainWindow is torn down. For a modal popup that may be opened and closed many times during a session, this is a memory leak.

**Fix:** Either use a named method or use the standard WPF `Closed` routed event:
```csharp
_commandPalette.Closed += (s, e) => _commandPalette = null;
```
This still captures `this` but uses the standard WPF event pattern. For a full fix without any circular reference, use a named method and unsubscribe:
```csharp
private void CommandPalette_Closed(object? sender, EventArgs e)
{
    if (sender is CommandPaletteWindow cpw)
        cpw.Closed -= CommandPalette_Closed;
    _commandPalette = null;
}
```

---

### WR-06: ScrcpyOptions has no property validation -- invalid values pass through to scrcpy

**File:** `src/QADeviceTool.App/Models/ScrcpyOptions.cs:3-13`
**Issue:** All properties have no validation:
- `MaxFps` (line 6): accepts negative values and zero. Scrcpy requires `max-fps >= 1`.
- `WindowW` and `WindowH` (lines 10-11): accept zero or negative dimensions, which produce invalid scrcpy arguments.
- `WindowX` and `WindowY` (lines 8-9): negative values are valid for multi-monitor setups, but extreme values may be problematic.
- `BitRate` (line 5): a `string` with no format validation. Valid scrcpy bitrate formats are like `"2M"`, `"800K"`. Values like `""`, `"abc"`, or `"-1"` pass through to the scrcpy process and cause silent failures.

Invalid values passed directly to scrcpy process arguments produce cryptic errors or no-ops that are hard to debug.

**Fix:** Add property validation with clamping:
```csharp
private int _maxFps = 60;
public int MaxFps
{
    get => _maxFps;
    set => _maxFps = Math.Clamp(value, 1, 120);
}

private int _windowW;
public int WindowW
{
    get => _windowW;
    set => _windowW = value < 0 ? 0 : value; // 0 = auto/default
}
```
For `BitRate`, match against a regex: `^\d+[KM]?$` or validate in the scrcpy service before passing to the process.

---

### WR-07: EarlyLog empty catch block swallows all exception types

**File:** `src/QADeviceTool.App/App.xaml.cs:29`
**Issue:**
```csharp
catch { /* Cannot log the logging failure */ }
```
This intentionally swallows exceptions when the fallback logging mechanism itself fails (which is defensible). However, it also swallows fatal exceptions like `OutOfMemoryException`, `AccessViolationException`, or `StackOverflowException` that should propagate. More practically, if the temp directory is on a full disk or the process lacks write permissions, every call to `EarlyLog` silently fails with zero indication -- and all the diagnostic information it tried to capture is lost.

**Fix:** Narrow the catch to expected IO failures:
```csharp
catch (IOException) { /* Cannot write early log: disk full or path inaccessible */ }
catch (UnauthorizedAccessException) { /* Cannot write early log: permission denied */ }
```
This preserves the defensive behavior for the expected failure modes while letting truly unexpected exceptions propagate.

---

### WR-08: Unused local variable `prefs` in App.OnStartup

**File:** `src/QADeviceTool.App/App.xaml.cs:59`
**Issue:**
```csharp
var prefs = Services.PreferencesService.Current;
EarlyLog("PreferencesService initialized.");
Services.PreferencesService.CleanupOldLogs();
```
The local variable `prefs` is assigned the PreferencesService instance but never read. The next line calls `CleanupOldLogs()` as a static method on the type, not through `prefs`. This suggests a refactoring where instance access was replaced with static access but the local variable was left behind. It also hints that the intent was to ensure PreferencesService is initialized (via the `.Current` property getter) before calling `CleanupOldLogs`.

**Fix:** Either remove the unused variable or use it consistently:
```csharp
// Option A: Remove unused variable
Services.PreferencesService.CleanupOldLogs();

// Option B: Use the variable (clearer intent)
var prefs = Services.PreferencesService.Current;
prefs.CleanupOldLogs();
```

---

## Info

### IN-01: Model types used in collections lack IEquatable<T> and GetHashCode overrides

**Files:**
- `src/QADeviceTool.App/Models/DeviceInfo.cs:6-34`
- `src/QADeviceTool.App/Models/LogEntry.cs:37-50`
- `src/QADeviceTool.App/Models/LogSession.cs:8-48`
- `src/QADeviceTool.App/Models/AppItem.cs:3-9`

**Issue:** These types are used in `ObservableCollection<T>` across ViewModels. No `Dictionary<TKey, TValue>` or `HashSet<T>` usage was found in the current codebase, but these are public types in a shared `Models` namespace that consumers could plausibly use as keys or in set operations. Without `GetHashCode`/`Equals` overrides, LINQ operations like `Distinct()`, `Except()`, `Intersect()`, or `Contains()` will use reference equality rather than value equality based on identity fields (`DeviceInfo.Serial`, `LogSession.Id`, `AppItem.PackageId`, `LogEntry` composite key).

**Fix:** Implement `IEquatable<T>` and override `Equals`/`GetHashCode` on types with a natural identity:
```csharp
public class DeviceInfo : IEquatable<DeviceInfo>
{
    public bool Equals(DeviceInfo? other) => other != null && Serial == other.Serial;
    public override bool Equals(object? obj) => Equals(obj as DeviceInfo);
    public override int GetHashCode() => Serial.GetHashCode();
}
```

---

### IN-02: LogLevelColorMultiConverter and other converters lack explicit DependencyProperty.UnsetValue guard

**File:** `src/QADeviceTool.App/Converters/LogLevelColorMultiConverter.cs:36-37` and `src/QADeviceTool.App/Converters/DeviceViewConverters.cs:13,45`

**Issue:** `LogLevelColorMultiConverter` guards with `values[0] is not LogLevel` (pattern matching fails for `UnsetValue`), so it falls through to the default brushes -- which is correct behavior. `IntToBoolConverter` and `InverseBoolConverter` use `value is int intVal` and `value is bool b` which also fail correctly for `UnsetValue`. However, none of these converters explicitly check for `DependencyProperty.UnsetValue`, making their behavior implicit rather than explicit. If pattern matching behavior ever changes (unlikely but possible in a future .NET version), these converters could silently change behavior.

**Fix:** Add explicit UnsetValue checks for defensive programming and code clarity:
```csharp
public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
{
    if (values.Length < 2
        || values[0] == DependencyProperty.UnsetValue
        || values[1] == DependencyProperty.UnsetValue
        || values[0] is not LogLevel level
        || values[1] is not bool enabled)
        return isBackground ? DefaultBg : DefaultFg;
    // ...
}
```

---

### IN-03: LogLevelColorMultiConverter has hardcoded color literals duplicating theme

**File:** `src/QADeviceTool.App/Converters/LogLevelColorMultiConverter.cs:18-30`
**Issue:** The converter defines 8 `SolidColorBrush` fields with hardcoded RGB values (e.g., `Color.FromRgb(0xEF, 0x44, 0x44)` for `FatalBg`). These duplicate the log level colors already defined in `DarkTheme.xaml` (`LogFatal`, `LogError`, `LogWarning`, `LogInfo`, `LogDebug`, `LogVerbose`). If the theme colors are updated, this converter will silently diverge, producing color mismatches between theme-defined and converter-defined elements. The previous UI review flagged hardcoded colors as BLOCKER findings.

**Fix:** Resolve colors from the application theme at runtime:
```csharp
private static SolidColorBrush GetBrush(string key)
    => Application.Current?.TryFindResource(key) as SolidColorBrush
        ?? new SolidColorBrush(Colors.Gray);

private static readonly SolidColorBrush FatalBg = GetBrush("BrushLogFatal");
// ...
```
Note: This requires `Application.Current` to be available, which is true after `Application.Run()` is called.

---

### IN-04: MainWindow OnPreviewKeyDown handler never unsubscribed

**File:** `src/QADeviceTool.App/MainWindow.xaml.cs:15`
**Issue:** `PreviewKeyDown += OnPreviewKeyDown;` is subscribed in the constructor but never unsubscribed. In most WPF scenarios this is harmless because the window lifecycle is tied to the subscription lifetime. However, if the window is ever reused or `InitializeComponent()` is called more than once, the handler would be registered multiple times, causing duplicate command palette invocations on each `Ctrl+K` press.

**Fix:** Unsubscribe in the `OnClosed` override:
```csharp
protected override void OnClosed(EventArgs e)
{
    PreviewKeyDown -= OnPreviewKeyDown;
    base.OnClosed(e);
}
```

---

### IN-05: LogSession.StartTime uses local time (DateTime.Now) rather than UTC

**File:** `src/QADeviceTool.App/Models/LogSession.cs:12,33`
**Issue:** `StartTime` defaults to `DateTime.Now` (local time). `DurationText` (line 33) computes `end - StartTime` where `end = EndTime ?? DateTime.Now`. If a session spans a DST transition ("spring forward" or "fall back"), or if the user changes system timezone mid-session, the duration calculation produces incorrect results (off by one hour). All recorded timestamps used for data analysis should use UTC to avoid timezone ambiguity.

**Fix:**
```csharp
public DateTime StartTime { get; set; } = DateTime.UtcNow;
// In DurationText:
var end = EndTime ?? DateTime.UtcNow;
```
Display conversion to local time should happen at the UI/presentation layer only.

---

### IN-06: LogEntry.ToString() omits Tag and RawLine -- lossy for debugging

**File:** `src/QADeviceTool.App/Models/LogEntry.cs:46-49`
**Issue:**
```csharp
public override string ToString()
{
    return $"[{Timestamp}] {Level}: {Message}";
}
```
For a log viewer application, the `Tag` property is essential context (identifies the Android log source component such as `ActivityManager`, `System.err`, or a custom app tag). Omitting it makes `ToString()` output ambiguous and less useful for debugging, quick watches, and diagnostic dumps.

**Fix:**
```csharp
public override string ToString()
{
    var tag = string.IsNullOrEmpty(Tag) ? "?" : Tag;
    return $"[{Timestamp}] {Level}/{tag}: {Message}";
}
```

---

_Reviewed: 2026-05-05T22:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
