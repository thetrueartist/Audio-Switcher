<div align="center">

# AudioSwitcher

**Per-game audio sample-rate switcher for Windows.**

Keep your DAC at its top quality (e.g. 384 kHz / 32-bit) for music — AudioSwitcher
automatically drops the rate *only* when a game needs it (many crash or go silent at
high rates), then restores it the moment you're done.

[![build](https://github.com/thetrueartist/Audio-Switcher/actions/workflows/build.yml/badge.svg)](https://github.com/thetrueartist/Audio-Switcher/actions/workflows/build.yml)
[![release](https://img.shields.io/github/v/release/thetrueartist/Audio-Switcher?sort=semver)](https://github.com/thetrueartist/Audio-Switcher/releases)
[![license](https://img.shields.io/github/license/thetrueartist/Audio-Switcher)](LICENSE)
![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6)

</div>

## The problem

Many USB DACs (FiiO K7, Topping, etc.) advertise **384 kHz / 32-bit** shared mode on
Windows. A number of game engines — UE4/UE5 in particular — **crash on audio init** at
those rates (null-pointer down the legacy `winmm` path) or **come up silent**. The usual
"fix" is to drop Windows to 24/192 permanently and lose quality for music.

AudioSwitcher is for people who'd rather keep the high format and only lower it, per
game, exactly when a game needs it — automatically.

## Screenshots

> _Coming soon — see [`assets/`](assets) for the shot list. (System-tray icon, the GUI
> control panel, and the console log.)_

## Features

- **Automatic.** Detects games at launch (Steam/Epic/EA/Ubisoft/GOG/Battle.net/Xbox…),
  lowers the rate, restores your audiophile format when they exit.
- **Manages the current default playback device** out of the box — not tied to one DAC, and it
  **follows the default if you change it**. The GUI dropdown (or `--set-device`) also works as an
  **output switcher** — pick a device and it routes your Windows audio there *and* manages it.
- **Learns each game's limit** from real signals: a crash (non-zero exit), an ETW audio
  glitch storm, or "running but silent" — and remembers it. An optional upward probe
  re-tests whether a game can go higher, to find the true ceiling.
- **System-tray app + GUI control panel** — live status, running games, per-game
  profiles, pause, and auto-start toggle.
- **Anti-cheat safe.** Never reads, writes, or injects into game processes. To nail the switch it
  briefly suspends the game across the format change — but auto-skips that the moment a known
  anti-cheat driver (EAC/BattlEye/Vanguard/…) is loaded, so it only ever fires on offline titles.
- **No third-party dependencies.** Pure Windows API (P/Invoke + COM). Releases are a
  self-contained exe — no .NET install required.

## Install

> **Extract the zip first.** Right-click the downloaded `.zip` → **Extract All**, then run
> the `.cmd` from the extracted folder. Running a `.cmd` from *inside* the zip only unpacks
> that one file, so it can't find the rest.

Download the latest [release](https://github.com/thetrueartist/Audio-Switcher/releases),
extract, then double-click:

| File | What it does |
|---|---|
| **`Install.cmd`** | Installs + enables auto-start (self-elevates). Re-run to cleanly reinstall. |
| **`Uninstall.cmd`** | Removes it (stops it, deletes the task + files). Standalone. |
| **`AudioSwitcher.cmd`** | Interactive menu (status, devices, run live, etc.). |
| **`Build.cmd`** | Compile from source (needs the .NET SDK; not needed for releases). |

After install it runs at every logon and lives in your **system tray**.

## The tray icon + GUI

The tray speaker icon shows state at a glance: **green** = full quality, **amber** =
lowered for a game, **grey** = paused. Right-click for status, pause/resume, logs, quit.

**Double-click the icon** (or *Open control panel…*) for the full **GUI window**: a **device
dropdown** that switches your Windows output device (routes audio there and manages it), format /
state, running games, learned
per-game profiles (clear one or reset all), a pause toggle, and an auto-start toggle.
**Right-click** a game or profile row to **Exclude** it (never manage) or **Lock it to a tier** —
applied to the running game right away.

## How it works

1. **Detect.** A game we've *already* learned is caught the instant its process is created, via
   the **ETW kernel process provider** — early enough to set the format (and freeze the game)
   *before* it opens its audio device. First-time / unknown games are flagged by WMI process-start
   events (install path or launcher parent), then fingerprinted for engine (UE/Unity/Source/FMOD/…).
2. **Apply.** The device's shared-mode format is set via the private
   `IPolicyConfig::SetDeviceFormat` COM interface — the same path the Sound Control Panel
   uses (no registry hacks, no PnP bounce). If the device rejects a format, it walks down
   to the next one it accepts. For a game we've **already learned**, the format is applied the
   instant its process appears — skipping the path/fingerprint step (~1 s) so the switch lands
   *before* the game opens its audio device, not after.
3. **Learn.** A game's tier is bumped down and remembered on any of:
   - **crash** — process exits non-zero within ~25 s (a clean exit is never a crash);
   - **glitch storm** — Warning+ events from `Microsoft-Windows-Audio`;
   - **silence** *(opt-in)* — the **focused** game's audio session stays at zero (a backgrounded
     game going quiet is ignored — many mute on focus loss, so judging it would misfire).
   If a game is silent *even at the lowest rate set before it opened audio* (a clean pre-set,
   guaranteed by the freeze), the rate was never the cause — so it **stops dropping the format,
   restores full quality, and suggests you `--exclude` it** rather than pinning it at the floor.
4. **Probe** *(opt-in).* Occasionally retry a dropped game one tier higher to find its
   real ceiling and self-heal over-drops.
5. **Restore.** When the last game exits, the idle/audiophile format is reapplied.

Most games are fine at your top format and are **left alone** — only UE4/UE5 start one
tier down (their known crash), and any game is only dropped further after an observed
failure. The first crash of a brand-new game is unavoidable — no method predicts a
game's limit before it runs; after that, it's remembered.

## Tier ladder

Best → safest (`config.json → ProfileTiers`; formats your device doesn't support are
skipped automatically):

| # | Format | Note |
|---|---|---|
| 0 | 384000 / 32 | audiophile — idle default |
| 1 | 192000 / 32 | safe for most modern engines |
| 2 | 192000 / 16 | some games need lower depth, not rate |
| 3 | 96000 / 32 | |
| 4 | 48000 / 32 | universal |
| 5 | 44100 / 16 | CD — last resort |

## Configuration

First run creates `%LOCALAPPDATA%\AudioSwitcher\config.json`. Notable fields:

- `TargetDeviceName` — **empty = current default playback device**; set a name substring
  (e.g. `"FiiO K7"`) to pin one endpoint.
- `CheckForUpdates` — check the GitHub Releases API at startup and show a notification if a
  newer version exists (default true; notify only, never auto-installs). `--check-update` on
  the CLI does the same on demand.
- `ProfileTiers`, `EngineDefaults`, `KnownQuirky` — the ladder, per-engine start tier,
  hardcoded per-exe tiers.
- `LauncherProcesses` / `GamePathHints` — how games are detected. `GameProcesses` — exe
  names always treated as games (add any game here, e.g. a Store/UWP or portable game).
  `IgnoreProcesses` — never games (launcher helpers, non-game Store apps).
- `AutoLearnFullscreenSeconds` — **auto-learn** (default 15; 0 = off): an exclusive-fullscreen
  Direct3D app that isn't already known gets added to `GameProcesses` after this long, so its
  next launch is caught early. Low false-positive (only exclusive fullscreen, which browsers/
  video don't use), and a false-add is harmless (the app is tracked, not altered).
- `CrashThresholdSeconds`, `GlitchThreshold`, `GlitchWindowSeconds` — learning sensitivity.
- `SilenceWindowSeconds` — **on by default (15s)**: a game that comes up Active but silent
  for this long gets bumped down. Safe on-by-default because the daemon self-verifies the
  peak meter reads sound before ever acting (a broken meter can't misfire). 0 = off.
  `SilenceGraceSeconds` ignores the first N seconds (loading).
- `ProbeEveryLaunches` — **0 = off**; set e.g. `5` to enable the upward probe.
- `SuspendDuringSwitch` — **on by default.** For a *known* game (already learned or known-quirky),
  freeze it the instant it launches, change the format, then resume — so it can't open its audio
  device at the old rate before the switch lands. Guarantees the switch wins the race even for games
  that init audio the moment they start (documented ntdll suspend, no injection). Stays safe via:
- `SuspendSkipIfAntiCheat` — **on by default.** Before freezing, check for a loaded anti-cheat
  *kernel driver* (EAC, BattlEye, Vanguard, …); if one is present, skip the freeze entirely (the
  fast-apply still runs). So the freeze only ever fires on offline titles, and online/anti-cheat
  games are never suspended. Set `false` only if you want the freeze unconditionally.

Config is validated on load (bad values can't crash it) and regenerated if it's from an
older version (the old one is kept as `config.json.old`). State files
(`overrides.json`, `crash-log.json`, `glitch-log.json`) live in the same folder; delete
the folder for a clean slate.

## Update safety (signed releases)

When an update is available, the tray/GUI shows **"Update available — click to install"**;
clicking it does a **user-triggered** one-click install: download the zip, verify its
SHA-256 against the signed manifest, swap the exe (rename-based, with rollback), and
restart. It never installs silently on its own - you choose when.

The in-app update check can require releases to be **cryptographically signed** so a
compromised repo/account can't push a malicious update. Each release carries a
`manifest.json` (version + SHA-256 of the zip) signed with the maintainer's **offline**
ECDSA P-256 key; the app has the matching public key embedded and rejects any release
whose manifest isn't validly signed (an attacker without the private key can't forge one).
Pure .NET crypto, no third-party libs.

Signing is **active** (a public key is embedded), so the updater **fails closed**: a release
without a valid signed manifest is never offered or applied.

Maintainer flow (the signing key never touches CI/GitHub) — sign each release you want
delivered via auto-update; mark anything you don't want to sign as a GitHub **pre-release**
(the updater ignores pre-releases):

```powershell
AudioSwitcher.exe --gen-signing-key    # once: writes the key to Documents (keep OFFLINE), prints the public key
.\Sign-Release.ps1 v1.7.2              # per release: downloads the CI zip, signs locally, attaches manifest.json + manifest.sig
```

## Anti-cheat

By default the daemon never reads, writes, suspends, or injects into game processes. Format
changes go through the Windows audio service; detection is WMI/ETW observation. EAC/BattlEye/VAC
have nothing to react to.

The **one** thing that touches a game is `SuspendDuringSwitch` (on by default): it briefly suspends
a *known* game across the format switch (documented ntdll process suspend — still no memory access,
no injection, no hooking) so it can't open audio at the wrong rate first. Because suspending a
protected game is the one thing an anti-cheat could object to, it's gated by `SuspendSkipIfAntiCheat`
(also on by default): if a known anti-cheat kernel driver (EAC/BattlEye/Vanguard/…) is loaded, the
freeze is skipped and only the hands-off fast-apply runs. Net result: the freeze fires only on
offline titles; online/anti-cheat games are never suspended.

## FAQ

**Will it fix a game that crashes at 384 kHz the *first* time?** No — nothing can predict
a game's limit before it runs. The first crash is unavoidable; after it, the safe rate is
learned and every later launch starts there.

**Does it work with any DAC / speakers?** Yes — it manages the default playback device.
Unsupported tiers are skipped, so it adapts to whatever formats your device exposes.

**Does it need .NET installed?** Release builds are self-contained — no. Building from
source needs the .NET 8 SDK.

**Where are the logs?** `%LOCALAPPDATA%\AudioSwitcher\daemon.log` (or the tray → *Open log
folder*).

## Building from source

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download). Then either double-click
`Build.cmd`, or:

```powershell
dotnet publish AudioSwitcher.csproj -c Release -r win-x64 --self-contained false
```

Diagnostics (run the exe directly) — handy when adding support for a new device:

```powershell
$e = "$env:ProgramFiles\AudioSwitcher\AudioSwitcher.exe"
& $e --list-devices     # endpoints (active marked *), with GUIDs
& $e --probe-format     # the device's current + mix WAVEFORMAT
& $e --set-format 192000 32   # apply a format directly (test what the device accepts)
& $e --sessions         # audio sessions on the default device (pid / state / peak)
```

**Exclusions** — tell AudioSwitcher to never manage an app (leaves its format alone):

```powershell
& $e --exclude Game.exe       # never touch this app (also drops any learned profile for it)
& $e --unexclude Game.exe     # start managing it again
& $e --list-excluded          # show the exclusion list
```

The binary installs to `%ProgramFiles%\AudioSwitcher` (admin-only — it runs elevated at logon, so it
must not be user-writable); learned state stays in `%LOCALAPPDATA%\AudioSwitcher`.

## License

[MIT](LICENSE) © thetrueartist
