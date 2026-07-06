<div align="center">

# AudioSwitcher

**Per-game audio sample-rate switcher for Windows.**

Keep your DAC at its top quality (e.g. 384 kHz / 32-bit) for music — AudioSwitcher
automatically drops the rate *only* when a game needs it (many crash or go silent at
high rates), then restores it the moment you're done.

[![build](https://github.com/thetrueartist/Audio-Switcher/actions/workflows/build.yml/badge.svg)](https://github.com/thetrueartist/Audio-Switcher/actions/workflows/build.yml)
[![release](https://img.shields.io/badge/release-latest-2ea043)](https://github.com/thetrueartist/Audio-Switcher/releases)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
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
- **Manages the current default playback device** out of the box — not tied to one DAC.
- **Learns each game's limit** from real signals: a crash (non-zero exit), an ETW audio
  glitch storm, or "running but silent" — and remembers it. An optional upward probe
  re-tests whether a game can go higher, to find the true ceiling.
- **System-tray app + GUI control panel** — live status, running games, per-game
  profiles, pause, and auto-start toggle.
- **Anti-cheat safe.** Never reads, writes, suspends, or injects into game processes.
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

**Double-click the icon** (or *Open control panel…*) for the full **GUI window**: live
device / format / state, running games, learned per-game profiles (clear one or reset
all), a pause toggle, and an auto-start toggle.

## How it works

1. **Detect.** WMI process-start events (sub-100 ms) flag a game by install path or
   launcher parent, then fingerprint the engine (UE/Unity/Source/FMOD/Wwise…).
2. **Apply.** The device's shared-mode format is set via the private
   `IPolicyConfig::SetDeviceFormat` COM interface — the same path the Sound Control Panel
   uses (no registry hacks, no PnP bounce). If the device rejects a format, it walks down
   to the next one it accepts.
3. **Learn.** A game's tier is bumped down and remembered on any of:
   - **crash** — process exits non-zero within ~25 s (a clean exit is never a crash);
   - **glitch storm** — Warning+ events from `Microsoft-Windows-Audio`;
   - **silence** *(opt-in)* — audio session Active but the peak meter stays at zero.
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
- `ProfileTiers`, `EngineDefaults`, `KnownQuirky` — the ladder, per-engine start tier,
  hardcoded per-exe tiers.
- `LauncherProcesses` / `GamePathHints` — how games are detected (add your own).
- `CrashThresholdSeconds`, `GlitchThreshold`, `GlitchWindowSeconds` — learning sensitivity.
- `SilenceWindowSeconds` — **0 = off**; set e.g. `15` to enable silence learning (verify
  with `--sessions` first). `SilenceGraceSeconds` ignores the first N seconds (loading).
- `ProbeEveryLaunches` — **0 = off**; set e.g. `5` to enable the upward probe.

Config is validated on load (bad values can't crash it) and regenerated if it's from an
older version (the old one is kept as `config.json.old`). State files
(`overrides.json`, `crash-log.json`, `glitch-log.json`) live in the same folder; delete
the folder for a clean slate.

## Anti-cheat

The daemon never reads, writes, suspends, or injects into game processes. Format changes
go through the Windows audio service; detection is WMI/ETW observation. EAC/BattlEye/VAC
have nothing to react to.

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
$e = "$env:LOCALAPPDATA\AudioSwitcher\bin\AudioSwitcher.exe"
& $e --list-devices     # endpoints (active marked *), with GUIDs
& $e --probe-format     # the device's current + mix WAVEFORMAT
& $e --set-format 192000 32   # apply a format directly (test what the device accepts)
& $e --sessions         # audio sessions on the default device (pid / state / peak)
```

## License

[MIT](LICENSE) © thetrueartist
