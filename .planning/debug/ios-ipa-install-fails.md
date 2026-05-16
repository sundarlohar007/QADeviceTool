---
status: fixing
trigger: "iOS IPA installations fail but app list and uninstall work correctly"
created: 2026-05-05
updated: 2026-05-07
---

## Current Focus

hypothesis: "ToolResolver.Resolve('ideviceinstaller.exe') fails (double .exe lookup) and returns bare name. IosService.InstallIpaAsync uses Path.GetDirectoryName on bare name → returns '' → IPA copied to CWD instead of tool's WorkingDirectory → tool can't find IPA."
test: "Verify by fixing ToolResolver to not append .exe when already present, and fix IosService to resolve toolDir correctly."
expecting: "Both changes fix IPA file location. Install should proceed past argument parsing (it does parse correctly — all 3 formats work from CLI)."
next_action: "Apply structured reasoning checkpoint then implement fix"

reasoning_checkpoint:
  hypothesis: "ToolResolver appends '.exe' even when the input name already contains it, causing ALL iOS tool lookups to fail. Fallback returns bare name 'ideviceinstaller.exe'. Then Path.GetDirectoryName('ideviceinstaller.exe') returns '' (empty) instead of the actual tools directory. This causes the IPA file to be copied to the app's CWD rather than the tool's WorkingDirectory (tools/iMobileDevice/). When the tool runs, it looks for the IPA in its WorkingDirectory and doesn't find it, failing the install."
  confirming_evidence:
    - "Direct CLI tests confirm ideviceinstaller v1.2.0 correctly parses all three argument formats: 'install -u UDID PATH', '-u UDID install PATH', and 'install PATH' — all produce specific errors (device not found, file not found, zip error), NONE produce usage text"
    - "ToolResolver.ResolveInternal() appends .exe unconditionally: toolName + '.exe' → for input 'ideviceinstaller.exe' searches for 'ideviceinstaller.exe.exe' — file never found"
    - "Path.GetDirectoryName('ideviceinstaller.exe') returns '' (bare name has no directory component) — confirmed by .NET API docs"
    - "ToolLauncher sets WorkingDirectory = _toolsDir = tools/iMobileDevice/, but IPA is copied to CWD (wherever app was launched from) — directories mismatch"
    - "list and uninstall work because they don't need a file path — only text arguments are passed to the tool"
    - "For comparison: list uses '-u UDID list --all' and uninstall uses '-u UDID uninstall BUNDLEID' — no file paths needed"
  falsification_test: "If we fix ToolResolver to return the full path and then Path.GetDirectoryName returns the correct iMobileDevice directory, the IPA would be found by the tool. If install still fails after this fix, the hypothesis is wrong."
  fix_rationale: "Fix 1 (ToolResolver): remove .exe appending when name already ends with .exe — fixes all iOS tool lookups. Fix 2 (IosService): use ToolLauncher.ToolsDirectory for IPA copy location instead of Path.GetDirectoryName on the bare resolved name — ensures IPA is placed where the tool's process can find it."
  blind_spots: "The original symptom describes 'usage/help text' output, but CLI tests show the tool only produces usage when install is called WITHOUT a PATH argument. The actual error from C# would be 'No such file or directory' (file not found) or 'No device found' (device offline), not usage text. The 'usage text' description may be inaccurate or from an earlier test. But regardless, the IPA-is-in-wrong-directory bug is real and would cause install failures."

## Symptoms

expected: `ideviceinstaller -u UDID install PATH` installs IPA on iOS device
actual: Installation fails. Tool reports error (exact error depends on device state and file location)
errors: Install attempts produce non-zero exit codes; tool may report file-not-found or device-not-found
reproduction: Call IosService.InstallIpaAsync with valid UDID and IPA path
started: Always been broken for install command

## Working Commands (same tool, same device)
- `-u UDID list --all` → WORKS
- `-u UDID uninstall BUNDLEID` → WORKS

## Attempted (all failed in C# context)
1. `install -u UDID filename.ipa`
2. `-u UDID install filename.ipa`
3. `install filename.ipa`

## Context
- Tool: `ideviceinstaller.exe` v1.2.0 — mingw-compiled Windows binary in `tools/iMobileDevice/`
- Path: resolved via `ToolResolver.Resolve("ideviceinstaller.exe")`
- `-u UDID` flag works for `list` and `uninstall`
- Only `install` command fails

## Eliminated

- hypothesis: "install command doesn't exist in this build"
  evidence: "CLI tests confirm `install PATH` is listed in COMMANDS and the tool accepts it. Running `install test.ipa` produces 'ERROR: zip_open: test.ipa: 19' — proves install is recognized."
  timestamp: 2026-05-07

- hypothesis: "Argument ordering matters (options must come before command)"
  evidence: "CLI tests confirm ALL three orderings work: 'install -u UDID PATH', '-u UDID install PATH', and 'install PATH' all parse correctly. None produce usage text."
  timestamp: 2026-05-07

- hypothesis: "install requires mandatory --sinf or --metadata flags"
  evidence: "CLI test: 'install test.ipa' without any flags produces 'ERROR: zip_open' — tool tries to install, no flags required."
  timestamp: 2026-05-07

- hypothesis: "mingw build has different command name (e.g., install-app, deploy)"
  evidence: "Usage text confirms command is 'install PATH', same as standard ideviceinstaller. No alternative command names exist."
  timestamp: 2026-05-07

## Evidence

- timestamp: 2026-05-07
  checked: "ideviceinstaller --help output"
  found: "Tool v1.2.0, 'install PATH' listed as valid COMMAND with optional -s/--sinf and -m/--metadata flags. OPTIONS listed: -u/--udid UDID for target device."
  implication: "Tool supports install command, syntax is standard"

- timestamp: 2026-05-07
  checked: "Direct CLI test with all 3 argument formats"
  found: "All formats parse correctly: 'install -u UDID PATH', '-u UDID install PATH', 'install PATH' all produce specific errors (device not found or zip error), NONE produce usage text"
  implication: "Argument parsing is not the issue. The tool recognizes install command with PATH argument."

- timestamp: 2026-05-07
  checked: "ToolResolver.ResolveInternal() implementation"
  found: "Unconditionally appends '.exe': `toolName + '.exe'`. When input is 'ideviceinstaller.exe', searches for 'ideviceinstaller.exe.exe' — never found. Falls back to returning bare name 'ideviceinstaller.exe'."
  implication: "ALL iOS tool lookups fail; _ideviceInstaller = 'ideviceinstaller.exe' (no path). list/uninstall still work because ToolLauncher.RunAsync combines with hardcoded _toolsDir."

- timestamp: 2026-05-07
  checked: "IosService.InstallIpaAsync IPA file placement"
  found: "`var toolDir = Path.GetDirectoryName(_ideviceInstaller) ?? '.'` → Path.GetDirectoryName('ideviceinstaller.exe') returns '' (empty). '' is NOT null, so ?? '.' does NOT trigger. IPA copied to Path.Combine('', ipaFileName) = bare filename in CWD. Tool runs with WorkingDirectory = tools/iMobileDevice/. IPA not found in WorkingDirectory."
  implication: "IPA is copied to the WRONG directory. When device is connected, tool tries to open IPA in WorkingDirectory and fails with 'No such file or directory'."

- timestamp: 2026-05-07
  checked: "Binary identity in build output"
  found: "Source tools/iMobileDevice/ideviceinstaller.exe and bin/Release/.../tools/iMobileDevice/ideviceinstaller.exe are identical (432,387 bytes, Mar 8 17:37)"
  implication: "No binary difference between source and deployed tool. Same version used everywhere."

- timestamp: 2026-05-07
  checked: "ToolLauncher._toolsDir construction"
  found: "Hardcoded to Path.Combine(AppContext.BaseDirectory, 'tools', 'iMobileDevice'). ToolResolver searches all subdirectories of tools/ but ToolLauncher hardcodes iMobileDevice/."
  implication: "Design inconsistency: ToolLauncher assumes all tools are in iMobileDevice/ while ToolResolver searches all subdirectories. Works for now but fragile."

- timestamp: 2026-05-07
  checked: "When tool shows usage text"
  found: "Tool ONLY prints usage text when 'install' is called WITHOUT a PATH argument: 'ERROR: Missing filename for 'install' command.' followed by full usage."
  implication: "If C# code observes usage text, the PATH argument must be missing or empty when the tool is called. This rules out a tool bug and points to a code-side issue with argument construction."

## Resolution

root_cause: "Two bugs: (1) ToolResolver.Resolve appends '.exe' unconditionally, causing all iOS tool lookups to fail with double-extension filenames like 'ideviceinstaller.exe.exe'. Fallback returns bare name. (2) IosService.InstallIpaAsync uses Path.GetDirectoryName on the bare name, which returns empty string. The IPA is copied to CWD instead of the tool's working directory (tools/iMobileDevice/), so the tool can't find the IPA file."
fix: "Fix 1 (ToolResolver.cs): Check if toolName already ends with '.exe' before appending. Fix 2 (IosService.cs): Fall back to ToolLauncher.ToolsDirectory when Path.GetDirectoryName returns empty/null — ensures IPA is copied to the same directory the tool runs from."
verification: "Build succeeds (0 errors). All 4 ToolResolver tests pass. Manual verification needed: install an IPA on a real iOS device."
files_changed: ["src\\QADeviceTool.App\\Helpers\\ToolResolver.cs", "src\\QADeviceTool.App\\Services\\IosService.cs"]
