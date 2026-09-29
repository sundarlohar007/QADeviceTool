# GPL-3.0 COMPLIANCE - pymobiledevice3

pymobiledevice3 is licensed under GPL-3.0. To ensure license compliance
while keeping QADeviceTool's own licensing independent:

1. **PROCESS ISOLATION:** pymobiledevice3 is invoked exclusively as a subprocess
   via `ToolLauncher`. It is never linked into QADeviceTool code — no library
   references, no derived code. All communication occurs via
   stdin/stdout/stderr of the child process (the standard process boundary).

2. **BUNDLED EXECUTABLE:** The release bundles a PyInstaller build of unmodified
   pymobiledevice3 9.12.0 (`tools/pymobiledevice3/pymobiledevice3.exe`).
   The entrypoint and build recipe are in `scripts/pymobiledevice3_entrypoint.py`
   and `scripts/build-pymobiledevice3.ps1`; the smoke test is in
   `scripts/test-pymobiledevice3.ps1`.

3. **SOURCE OFFER:** Under GPL-3.0 §6, the complete corresponding source code
   for the bundled pymobiledevice3 9.12.0 build is available from the upstream
   project: <https://github.com/doronz88/pymobiledevice3/tree/v9.12.0>, together
   with this repository's build scripts. A written source offer is included with each distribution in
   `licenses/SOURCE-OFFER-pymobiledevice3.txt`.

4. **SYSTEM FALLBACK:** If the bundled binary is unavailable or fails its syslog
   command probe, IosService falls back to a working system-installed `python -m pymobiledevice3`.
   Users may install pymobiledevice3 independently (`pip install pymobiledevice3`).

5. **NO DERIVED CODE:** No pymobiledevice3 source code is included, modified,
   or linked into QADeviceTool's own source or binaries.

Related third-party licensing: see `THIRD_PARTY_NOTICES.txt` (scrcpy —
Apache-2.0 with NOTICE, Android platform-tools, Google USB driver).
