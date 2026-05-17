GPL-3.0 COMPLIANCE — pymobiledevice3

pymobiledevice3 is licensed under GPL-3.0. To ensure license compliance
while keeping QADeviceTool's own licensing independent:

1. PROCESS ISOLATION: pymobiledevice3 is invoked exclusively as a subprocess
   via ToolLauncher. It is never bundled, linked, or distributed with QADeviceTool.

2. SYSTEM DEPENDENCY: pymobiledevice3 is documented as a system dependency,
   similar to ADB. Users must install it independently.

3. NO DERIVED CODE: No pymobiledevice3 source code is included, modified,
   or linked into QADeviceTool binaries.

4. SEPARATE PROCESS: All communication occurs via stdin/stdout/stderr of
   the child process, which is the standard process boundary.