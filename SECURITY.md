# Security Policy

## Trust boundary

LogPro is a **local, single-user desktop tool**. Its trust boundary is:

```
[local OS user] ──▶ LogPro process ──▶ spawned native tools ──▶ USB-attached devices
```

- **Local user:** the process runs with the invoking user's privileges. Files (logs,
  sessions, screenshots, recordings) are stored under `%LOCALAPPDATA%\LogPro` (or the
  configured sessions directory) and are owner-only where the platform supports it.
- **Spawned native tools** (`adb`, `scrcpy`, `pymobiledevice3`): invoked as child
  processes with output redirection; their outputs are treated as untrusted input
  (parsed defensively) and never interpolated into shell command lines without
  allowlist validation.
- **USB devices:** device output (logcat, dumpsys, AFC listings) is untrusted input.
  Device-targeted commands are built with strict allowlists (`IsSafePath`,
  package-name validation, quoted arguments) to prevent shell injection.
- **Local control API** (`logpro-cli serve`): binds to IPv4 `127.0.0.1` only and
  requires a per-process `X-LogPro-Api-Key` header for every data-changing or
  device-reading endpoint. `/health` is the only unauthenticated endpoint. The key
  is printed once to the serving process's stdout and is never accepted in a URL.
  Never expose the port by forwarding it or binding non-loopback interfaces.
- **Offline transport policy:** wireless ADB, network iOS discovery, URL/file schemes,
  port forwarding, arbitrary shell composition, and network-capable child commands are
  rejected by the engine. Device output paths must be local and free of reparse points.
- **Plugins:** declarative regex plugins are allowed; arbitrary assembly plugins are
  disabled by default because .NET assembly loading is not a security sandbox.

## Data sensitivity

This tool is used to test **unreleased games**. Redaction is always applied to exported
text and issue bundles; raw session capture remains local evidence. Bug-report bundles
are minimized (hashed serials, filtered device properties, no full package inventory).
See `GPL_COMPLIANCE.md`,
`THIRD_PARTY_NOTICES.txt`, and the privacy notice shown on first run.

## Reporting a vulnerability

Please report security issues privately to the maintainers (via GitHub's "Report a
vulnerability" flow on the repository). Please do not open a public issue for
suspected vulnerabilities. We will acknowledge within 5 business days and aim to
fix confirmed issues within 90 days.
