# Security

## Reporting

Found a vulnerability? Please open a **private** GitHub Security Advisory
(Repository → Security → Report a vulnerability) rather than a public issue.

## Model

AudioSwitcher runs a background daemon **elevated** — a Scheduled Task with
`RunLevel Highest` (High integrity), as the logged-in user. Elevation is required
because the ETW kernel-process session, the WMI process-start subscription, and the
`IPolicyConfig::SetDeviceFormat` write all need admin. The binary lives in
`%ProgramFiles%\AudioSwitcher` (admin-only-writable); only per-user *state*
(`config.json`, `overrides.json`, logs) lives in user-writable `%LOCALAPPDATA%`, and
none of it is ever used as a path, command, or DLL to load — only as data compared by
name or clamped format values.

## Hardening in place

- **Program Files install** — the elevated binary is not in a user-writable location,
  so a non-admin process can't replace it. Auto-start registration **fails closed** if
  the exe isn't the Program Files copy.
- **Update staging** is confined to an admin-only subfolder of the install dir (never
  `%TEMP%`), closing the verify-then-run TOCTOU.
- **Named pipe** is ACL'd to the current user only, created with `FirstPipeInstance`,
  fails closed rather than opening a default ACL, and reads are bounded and time-limited.
- **No injection** — CLI/config strings are JSON-escaped and used only as data;
  `schtasks` runs with `UseShellExecute=false` and by full System32 path.
- **COM-hijack guard** — the daemon activates a couple of system in-proc COM objects;
  because COM resolves `HKCU\...\CLSID` before HKLM, it checks for and removes any
  per-user override of those CLSIDs at startup and every few seconds (see below).

## Known residual risk

- **Elevated in-proc COM activation is subject to COM hijacking (MITRE T1546.015).**
  A same-user *medium*-integrity process that plants an `InprocServer32` override in
  `HKCU` could, in a tight race, get a DLL loaded into the High-integrity daemon. The
  guard above removes persistent overrides but is racy by nature; the only complete fix
  is architectural (don't run the monitor elevated), which isn't possible here because
  the monitoring itself requires elevation. If you don't trust code already running as
  your user, that code has many paths to escalate — this is one of them.
- **Auto-update authenticity** is enforced by a signed release manifest (ECDSA P-256,
  maintainer key offline). The updater **fails closed** — a release without a valid signed
  manifest is never offered or applied — so a repo/account/CI compromise can't push a
  trojaned update without the offline private key. Updates are notify-then-click, never silent.
