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
- **Local control API** (`logpro-cli serve`): binds to `127.0.0.1` only, no
  authentication — intended for same-user CI/Appium harnesses. Never expose it by
  forwarding the port or binding non-loopback interfaces.

## Data sensitivity

This tool is used to test **unreleased games**. Redaction is on by default
(`SecureMode`); bug-report bundles are minimized (hashed serials, filtered device
properties, no full package inventory). See `GPL_COMPLIANCE.md`,
`THIRD_PARTY_NOTICES.txt`, and the privacy notice shown on first run.

## Reporting a vulnerability

Please report security issues privately to the maintainers (via GitHub's "Report a
vulnerability" flow on the repository). Please do not open a public issue for
suspected vulnerabilities. We will acknowledge within 5 business days and aim to
fix confirmed issues within 90 days.
