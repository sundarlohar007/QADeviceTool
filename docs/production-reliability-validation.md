# Production reliability changes and validation

Review date: 2026-10-08; release validation continued 2026-10-09. Branch: `codex/production-reliability-fixes`.

## Implemented changes

| Area | Change |
| --- | --- |
| iOS capture | Configure UTF-8 output inside the frozen Python entrypoint, including stderr; add a packaged Unicode pipe probe to CI. |
| Capture lifecycle | Reserve a device through start and finalization; guard old process-exit callbacks; serialize lifecycle and display delivery; fix timer ownership during overlapping captures. |
| Saved output | Wait for stdout drain and writer finalization; persist stop reason, exit code, line count and completeness; CLI and API wait before reporting saved output. |
| Live logs | Use bounded 500-line batches and a 20,000-line live window, incremental WPF appends, sequence-aware snapshot merging, explicit skipped-display counts and last-line age. Full capture remains on disk. |
| Crash evidence | Detect crashes in the capture service even when another session/tab is selected; persist redacted crash records alongside session metadata. |
| Recovery | Publish device readiness transitions after authorization/trust and brief disconnects. With auto-capture enabled, retry unexpected stream exits at most three times, respecting stop revisions and shutdown. |
| iOS concurrency/files | Give Shell log streaming its own process rather than holding the ordinary command gate; enumerate USB endpoints before per-device hydration; preserve cached metadata; use Documents mode for app file transfers. |
| App/device metadata | Add a bundled Android shell helper for localized app labels and display versions, with a visible package-ID fallback. Make device metadata observable and avoid product codenames replacing display names. |
| Device selection | Preserve local selections on unrelated global metadata refreshes; prevent profiler ComboBox reconciliation from clearing global selection; disable app/file actions for temporarily unavailable devices. |
| Install/shutdown | Prevent suspended iOS capture from resuming after cancellation, manual stop or shutdown; await screen-recording finalization before closing. |
| Performance correctness | Read actual SurfaceFlinger presentation timestamps and reject pending-frame sentinels; retain the original memory baseline beyond the displayed history window. |
| Log parsing | Share parsing between live display and exports, recognize additional logcat headers and make an empty severity selection show no lines. Raw records remain unclassified; long-format continuations inherit their header until a record boundary. Live view, full-file search and exports use stream-aware parsing. |
| Updates | Remove CLI trust regeneration; journal tool replacement and keep verified rollback backups; recover missing/corrupt selected tools without trusting unrelated modifications; check active work before rollback. |
| Update packaging | Track managed package revisions independently of upstream versions; fetch release metadata once per check; persist installation outcomes for Settings. Increase `ManagedRevision` when rebuilding unchanged upstream tools. |
| Privacy/preferences | Redact quoted JSON credentials and modern iOS IDs; handle uppercase text attachments, known device IDs and filename collisions; use unique preference temporary files, a process mutex, stale-write rejection and backup recovery. |
| UI/startup | Keep navigation highlights bound, make the sidebar scroll, avoid full detail reloads on every metadata event, use weak Settings subscriptions and surface initialization failures. Unexpected dispatcher failures close through capture finalization. |

## Completion of the remaining implementation work

The follow-up review identified 14 areas that were incomplete after the first implementation pass. Their source changes are now implemented locally:

| Area | Completed behavior | Verification scope |
| --- | --- | --- |
| Readiness guards | Apps and File Explorer check readiness, including temporary unavailability, before commands and queued transfers. App inventory reloads when readiness returns. | Readiness workflow regressions. |
| Live filtering | Filter work runs against immutable snapshots off the UI thread; generations discard stale results and arriving batches are merged. Raw display switches text bindings without reparsing history. | 20,000-row filtering, latest-filter wins, pause/resume and populated WPF tests. |
| Startup update recovery | Normal startup detects persisted update journals and recovers before launching device tools; elevation and another running instance are handled explicitly. | Fresh service instance recovers persisted journal. Installed elevation flow still needs manual testing. |
| Installer outcomes | Update checks distinguish failure, installed tools and downloaded application installers; setup displays/persists outcomes and removes stale transaction metadata during installation. | Bootstrap builds. Inno Setup compiler unavailable locally; installer script has not been compiled or installed in this pass. |
| Capture recovery | Retries preserve immutable capture options, respect manual stop and shutdown, and record interruption/resumption information. Old process callbacks cannot stop a replacement capture. | Concurrent starts, delayed exit, independent device timer delivery, manual stop/readiness and subprocess capture fixtures. |
| App inventory | Package IDs appear before label enrichment; label cache includes device, package, version and locale. Superseded loads are cancelled and selected packages survive enrichment. Missing labels have an explicit fallback notice. | App workflows and metadata fixtures; localized OEM behavior still requires devices. |
| iOS document transfers | Structured outcomes distinguish unsupported apps, trust issues, disconnection, missing files and cancellation; File Explorer displays the cause. | Error classification and file workflows. Real File Sharing compatibility remains unverified. |
| Log formats | Long-format continuations retain timestamp, tag and severity; blank records reset context. Raw logs remain unclassified. Search parses context before selecting matching records. | Parser, live view, search, CSV/JSON boundary regressions and existing export performance gates. |
| Full-run profiling | Constant-space aggregates preserve early samples after chart/history trimming. UI, CLI/API reports, soak and tier summaries use full-run data. | 30,000-sample aggregate regression and profiling/CLI/API suites. |
| Preferences | Updates serialize detached candidate snapshots under a lock and publish them only after a successful disk commit. Device preference drafts are detached. | Concurrent updates, stale writers, backups and existing preference tests. |
| Hidden-tab queries | Devices and Macros defer expensive queries until activated, cancel superseded/hidden work and retain stable capability results. | Hidden Devices activation/cancellation and navigation tests. |
| API lifecycle | Discovery and capture startup receive cancellation; immutable options avoid global target mutation; shutdown rejects late registration and waits for cleanup. | API shutdown during discovery, capture ownership and lifecycle tests. |
| Rollback preflight | Backup integrity and active work are checked before requesting elevated rollback and closing the application. | Missing/corrupt backup and transaction recovery regressions. |
| Status and layout | Session health combines saved/displayed counts, skipped display, last-line age, recording, stop reason and interruptions. Session controls wrap and Performance charts stretch. | Populated all-tab WPF checks in both themes, three window sizes and three layout scale factors. |

## Automated coverage

- Real subprocess fixtures emit 2,001 numbered Unicode lines through each platform capture path, verify saved files and detect crashes without a view.
- Concurrent API starts on one device produce one capture and one conflict; stop returns the saved count without changing global target preferences.
- Transaction tests interrupt replacement checkpoints, restore the previous package, repair a missing tool and reject unrelated tampering. These simulate exceptions, not power loss.
- Tests cover preference stale writers/backups, app labels, credential redaction, bundle collisions and authorization/reconnect events.
- Populated WPF tests use DataContexts for all tabs. Sessions coverage checks 20,000 retained rows, virtualization, incremental appends and background filtering. Performance selection survives metadata refresh.
- Existing million-line CSV/JSON export and tail-read performance gates remain enabled.
- CI builds the frozen runtime from pinned dependency inputs and checks actual CLI and Unicode output. Release packaging builds the Android helper and includes it in the manifest and managed ADB archive.

## Local verification results

- Final complete Release suite: **408 passed, 0 failed, 0 skipped** (1 minute 54 seconds). Result: `src/LogPro.Tests/TestResults/remaining-complete.trx`; console log: `publish/remaining-complete.log`.
- `dotnet format LogPro.sln --no-restore --verify-no-changes --verbosity quiet`: passed.
- `git diff --check`: passed (Git reports expected LF-to-CRLF normalization warnings for two test files).
- Focused readiness, lifecycle, preference/app, parser/export and profiler checks also passed during implementation; their tests are included in the final full-suite result above.
- Synthetic WPF measurements: appending/layout of 500 rows into a 20,000-row viewer took 3 ms in each theme. Filtering 20,500 rows plus WPF application took 52 ms in Dark and 26 ms in Light. These are local fixture observations, not hardware-device throughput guarantees.
- Earlier implementation pass: NuGet vulnerability audit reported no vulnerable packages from configured sources; frozen pymobiledevice3 command/Unicode checks and Android Java/D8 helper build passed. These unchanged packaging checks were not repeated in this follow-up.

## Remaining release validation

### Follow-up checks on 2026-10-09

- Published the current Windows x64 application as a self-contained build into `publish/validation-20261009/app`; no application source changes were made in this follow-up. Publish log: `publish/validation-publish.log`.
- Reused the locally cached tool payload only after all 7,396 files matched its existing SHA-256 manifest. Ran the release preparation script against the fresh publish output, reusing the cached ADB archive and previously built iOS runtime and Android inventory helper. This checks packaging without changing upstream dependency versions. Preparation log: `publish/validation-tools.log`.
- The freshly published `LogPro.exe --verify-installation` passed with exit code 0 and isolated application data (`publish/validation-20261009/verification-data`). All required bundled legal notices were present. This verifies a publish directory, not an installer-created installation.
- Fresh payload executable checks passed for ADB 37.0.1, scrcpy 3.3.4 and pymobiledevice3 9.12.0. The frozen iOS runtime passed command-help and redirected UTF-8 stdout/stderr checks. Runtime log: `publish/validation-ios-runtime.log`.
- This local validation build uses placeholder version 3.2.0; it is not a new release or installer for distribution.
- Parsed all workflow YAML and PowerShell build scripts successfully. Syntax parsing alone does not establish that GitHub Actions or installer compilation will pass.
- USB discovery returned no devices from both bundled ADB (`devices -l`) and pymobiledevice3 (`usbmux list --usb --simple`). Physical-device capture, reconnect and soak validation could not run.
- Inno Setup was not installed. Automatic approval review rejected the attempted command to download and launch the official compiler in portable mode with the reason `blocked by policy`. No compiler was installed; installer compilation and elevation tests remain unverified.

### Outstanding checks

Automated tests do not establish a zero-defect guarantee. Before publishing a production installer, test the packaged Windows build against physical Android and iOS devices: USB unplug/replug, authorization/trust changes, device locking, app installs during capture, simultaneous devices, long recordings and shutdown. Check localized Android labels across supported OS versions and OEMs; restricted devices may require the package-ID fallback.

| Environment or scenario | Current evidence | Still required |
| --- | --- | --- |
| Windows desktop source build | Release automated service, subprocess and populated WPF tests; self-contained publish and payload verification | Installer-created build, physical high-DPI monitors and usability checks |
| Android USB | Synthetic ADB subprocess and workflow fixtures | Supported OS/OEM matrix, real labels, reconnects and long capture |
| iOS USB | Synthetic capture fixtures; fresh published runtime command and Unicode pipe checks | Supported iOS/device matrix, trust/lock transitions and File Sharing |
| Updater and installer | Transaction fault injection and persisted-journal recovery | Compile Inno Setup script; fresh-machine install/upgrade, elevated startup recovery and forced process/power interruption |
| Sustained operation | Bounded-history and million-line export checks | Eight-hour physical-device capture/soak, realistic bursts and slow-disk behavior |

No physical Android/iOS compatibility matrix has been certified by this work. Reconnection may leave an unavoidable gap in device logs; the application reports interruptions instead of implying continuous capture.

This report records validation of the reliability changes before release.

### Installer-only follow-up on 2026-10-09

- Release packaging now publishes only the Windows `LogPro_v*.exe` installer. The app payload remains an internal CI artifact used to compile the installer; portable, CLI ZIP and separate tool ZIP release assets were removed.
- Before copying the new program, setup checks the existing registered LogPro install and runs its uninstaller. Unknown leftovers in the old program directory are moved under `%LOCALAPPDATA%\LogPro\RecoveredInstallFiles`, or beside a custom installation on another drive if needed; setup stops if uninstall or preservation fails. Existing `%LOCALAPPDATA%\LogPro` settings, logs and sessions stay in place.
- Online setup checks Google's Windows Platform-Tools archive and Genymobile's Windows scrcpy release. Updates are staged, version/health checked and committed through the existing manifest-backed transaction; offline and failed portal checks retain the bundled baseline. The upstream pymobiledevice3 PyPI version is notification-only until a tested frozen Windows runtime ships. Setup also checks iTunes and Apple Mobile Device Service for iOS USB guidance.
- Official-source parser and archive tests include a successful staged ADB replacement retaining the Android inventory helper and rejected malformed ADB/scrcpy packages. The full Release suite passed **414/414**, and `dotnet format --verify-no-changes`, workflow YAML parsing and `git diff --check` passed.
- The source publish succeeded. An attempted validation publish into a previously populated output directory retained an unlisted old scrcpy directory and was correctly rejected by the tool manifest; this was a local test-output overlay. Automatic approval review blocked removal of that stale validation directory, so it was left untouched. A separate fresh self-contained publish then prepared all three cached tool payloads and passed `LogPro.exe --verify-installation` with exit code 0 using isolated application data.
- Inno Setup is still unavailable locally and the prior compiler download/launch was blocked by automatic approval review. Installer compilation, its upgrade smoke test and physical-device testing remain release gates on Windows CI and a test machine; they have not been claimed as locally passed.
