---
status: resolved
trigger: "Post-pymobiledevice3 replacement integrity check — verify all iOS methods remapped correctly, no missing links, no broken references before testing"
created: 2026-05-05
updated: 2026-05-08
diagnose_only: true
---

## Symptoms
- Replaced 6 libimobiledevice tools with single pymobiledevice3 invocation
- Rewrote entire IosService.cs (all 11 methods + 9 new)
- Changed ToolLauncher._toolsDir
- Need to verify: no broken references, correct command strings, all callers intact, output parsers match new tool format

## Resolution (2026-05-08)

Full audit ran against IosService, ToolLauncher, ToolResolver, DependencyChecker,
ShellViewModel, DeepLinkViewModel, csproj, setup.iss, SettingsView.xaml,
SessionViewModel, README, CHANGELOG, TEST_PLAN — 39 issues identified and fixed.

### Critical fixes
- IosService now resolves a working pymobiledevice3 invoker:
  bundled PyInstaller exe (probed first) → fallback to `python -m pymobiledevice3`.
- All per-device subcommands now pass `--udid <serial>` so multi-device setups
  target the right device.
- Output parsing tolerates JSON, Python-dict, and plain-text formats from
  pymobiledevice3 (`lockdown info`, `apps list`, `afc ls`).
- Concurrency: `IosService` now serializes pymobiledevice3 invocations via a
  static `SemaphoreSlim`, mirroring `AdbService`'s ADB lock.
- ToolLauncher work dir no longer leaks pymobiledevice3 pairing state into the
  user's Python install.
- ToolResolver.InitializeNativePaths skips PyInstaller bundles so their
  `_internal/` runtime DLLs don't shadow system Python.

### CLI corrections (versus the original plan)
- `crash list` → `crash ls`
- `diagnostics` → `diagnostics info`
- `apps list --all` → `apps list` (default Type=Any)
- `usbmux list --mobdev2` → `usbmux list --network`
- `developer dvt --screenrecord` removed (no equivalent in pymobiledevice3)
- `developer dvt --open-url` removed (no equivalent)
- `house_arrest` → `apps query` (Container key extracted via regex)
- `notification post --title --message` → `notification post --insecure <name>`
  (pymd3 only takes Darwin notification names)

### Tests added
- `LogPro.Tests/Services/IosServiceParserTests.cs`:
  - `ParseLockdownInfo` JSON + Python-dict + empty
  - `ParseAppsList` JSON object + bundle-name fallback + empty
  - `ParseAfcLs` listing + dot-skip + root-path
