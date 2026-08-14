# GPL-3.0 COMPLIANCE — pymobiledevice3

pymobiledevice3 is licensed under GPL-3.0. To ensure license compliance
while keeping QADeviceTool's own licensing independent:

1. **PROCESS ISOLATION:** pymobiledevice3 is invoked exclusively as a subprocess
   via `ToolLauncher`. It is never linked into QADeviceTool code — no library
   references, no derived code. All communication occurs via
   stdin/stdout/stderr of the child process (the standard process boundary).

2. **UNMODIFIED BINARY:** The release bundles an unmodified PyInstaller build of
   pymobiledevice3 (`tools/pymobiledevice3/pymobiledevice3.exe`) for
   out-of-the-box iOS support. It is redistributed as-is, unmodified.

3. **SOURCE OFFER:** Under GPL-3.0 §6, the complete corresponding source code
   for the bundled pymobiledevice3 build is available from the upstream
   project: <https://github.com/doronz88/pymobiledevice3> (the exact tag used
   for the bundled build is noted in the release notes). A written source
   offer is included with each distribution per GPL-3.0 §6(b).

4. **SYSTEM FALLBACK:** If the bundled binary is unavailable, IosService
   automatically falls back to a system-installed `python -m pymobiledevice3`.
   Users may install pymobiledevice3 independently (`pip install pymobiledevice3`).

5. **NO DERIVED CODE:** No pymobiledevice3 source code is included, modified,
   or linked into QADeviceTool's own source or binaries.

Related third-party licensing: see `THIRD_PARTY_NOTICES.txt` (scrcpy —
Apache-2.0 with NOTICE, Android platform-tools, Google USB driver).
