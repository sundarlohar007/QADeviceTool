# Changelog

## v3.2.0 (2026-05-17)

### All 11 Waves — Complete Audit Remediation (152/152 tasks)

**Security & Critical Fixes:**
- SecureMode: log redaction for serials, paths, deep links
- Wireless ADB security warning before tcpip 5555
- Hash truncation 8→16 hex chars, serial hashing in prefs
- IsSafePath hardened against pipe, &&, ||, \n, \r, ${}, >, < injection
- GPL compliance documented (pymobiledevice3 process isolation)
- MIT LICENSE added, THIRD_PARTY_NOTICES.txt updated

**Performance:**
- O(n²) string concatenation → StringBuilder (ShellViewModel, AppManagementViewModel, StressTestViewModel)
- ExportToCsv/Json streaming via StreamReader (no more ReadAllLines OOM)
- ADB semaphore widened 1→4, iOS semaphore widened 1→3
- Parallel ADB + iOS device polling (Task.WhenAll)
- Batched ADB device detail commands (single shell call)
- Macro replay Stopwatch drift compensation

**UI/UX:**
- Double MainViewModel instantiation fixed (ghost ADB polls, memory leak)
- Theme switch no longer kills active sessions
- Command palette emoji → Segoe MDL2 glyphs
- 9 views converted to DynamicResource (theme-aware)
- Button IsEnabled guards (Start/Stop, Mirror, Uninstall, Delete, Upload)
- Dashboard status dots bound to ConnectionState
- Ctrl+1..7 keyboard shortcuts for navigation
- First-run privacy notice
- "View Logs" button in Settings

**Bug Fixes:**
- ADB pair syntax (--code flag removed)
- stdout pipe deadlock in ToolLauncher.StartLongRunning
- SessionService flush timer race (timer disposed before writers)
- DeviceMonitorService first poll broadcast
- CrashDetector thread safety (lock around list)
- VitalsViewModel navigation lifecycle (OnNavigatedFrom/To)
- Deep link positive "Starting: Intent" check
- InstallApkAsync last-line Success check
- SaveLog copies raw disk file (not truncated in-memory data)
- ParseAfcLs directory heuristic fixed
- Macro text input (base64 replaced with direct input text)
- Auto-capture race condition lock guard
- Anonymize regex narrowed to known serial patterns
- Clipboard copy capped at 10K entries
- iOS serial hashed in bug reports
- PACKAGE dumpsys removed from bug reports
- base64 removed from clipboard/notification commands
- iOS path validation (IsSafePath)
- Smart retry (skip non-retriable ADB failures)
- Developer Mode error detection in iOS screenshots
- iOS directory deletion (afc rmdir fallback)
- SIGINT/CloseMainWindow before process Kill
- Screen recording remote path stored

**Architecture:**
- 5 service interfaces (IAdbService, IIosService, etc.) wired to all 11 ViewModels
- IDisposable + event cleanup on all 12 ViewModels
- MainViewModel.Cleanup() disposes all child ViewModels
- BulkObservableCollection.RemoveRange method
- Stress test periodic metrics sampling (5s timer)
- StressReportBuilder time-series detailed report
- TrimLogEntries extracted method
- ToolResolver.ClearCache + PATH dedup guard
- NLog retention wired to PreferencesService
- ProcessManagerService: EnableRaisingEvents inside try, Dispose removed from KillAll

**Documentation:**
- GPL_COMPLIANCE.md, LICENSE (MIT), THIRD_PARTY_NOTICES.txt
- setup.iss updated with QADeviceTool branding v3.2.0
- .gitignore for build artifacts, CI logs, graphify cache

### Previous Releases
- v3.1.0: Stitch UI redesign, ThemeService, BulkObservableCollection
- v3.0.0: Initial release with pymobiledevice3, scrcpy, ADB support