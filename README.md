# AudioSwitcher

[![build](https://github.com/thetrueartist/Audio-Switcher/actions/workflows/build.yml/badge.svg)](https://github.com/thetrueartist/Audio-Switcher/actions/workflows/build.yml)

A small Windows daemon that automatically lowers your output device's shared-mode
sample rate / bit depth when a game launches, and restores your high-quality
("audiophile") format when all games exit.

## Why

Some USB DACs (e.g. FiiO K7, Topping) advertise 384 kHz / 32-bit shared mode.
A number of game engines — UE4/UE5 in particular — crash on audio init at those
rates (null-pointer dereference down the legacy `winmm` path), or come up silent.
The usual fix is to set Windows to 24/192 (or 32/192) permanently and forget it.
This tool is for people who'd rather keep the high format for music and only drop
it, per game, when a game actually needs it — then put it back.

It manages the **current default playback device** out of the box, so it isn't
tied to any specific DAC.

## How it works

- **Detection:** WMI `Win32_ProcessStartTrace`/`StopTrace` (kernel-driven,
  sub-100 ms) spots game processes by install path (Steam/Epic/EA/…) or parent
  launcher, then fingerprints the engine (UE/Unity/Source/FMOD/Wwise/…).
- **Apply:** the device's shared-mode format is set via the private
  `IPolicyConfig::SetDeviceFormat` COM interface — the same path the Sound
  Control Panel uses. The audio service performs the privileged write, so it
  works from an elevated process with **no registry hacks and no PnP bounce**.
  If the device rejects a format, the daemon walks down the tier ladder to the
  next format it accepts.
- **Learning (per-game failure signals):** a game's tier is bumped down one step and
  remembered — so the next launch starts safe — on any of:
  - **crash** (process exits non-zero within ~25 s; a clean exit-0 quit never counts);
  - **ETW glitch storm** (Warning+ events from `Microsoft-Windows-Audio`);
  - **silence** (opt-in) — its audio session is *Active* but the peak meter stays at 0
    and it never produced sound: it failed to open audio at this format.
- **Restore:** when the last game exits, the idle/audiophile format is reapplied.

Most games are fine at your top format and are **left alone** — only UE4/UE5
start one tier down (their known crash), and any game is only dropped further
after an observed failure.

### Anti-cheat

The daemon never reads, writes, suspends, or injects into game processes. Format
changes go through the Windows audio service; detection is WMI/ETW observation.
EAC/BattlEye/VAC have nothing to react to. (If a future feature suspends a game
to win the audio-init race, it will be opt-in and off by default for this reason.)

## Tier ladder

Best → safest (index used internally for per-game learning):

| # | Format | Note |
|---|---|---|
| 0 | 384000 / 32 | audiophile / idle default |
| 1 | 192000 / 32 | safe for most modern engines |
| 2 | 192000 / 16 | some games need lower depth, not rate |
| 3 | 96000 / 32 | |
| 4 | 48000 / 32 | universal |
| 5 | 44100 / 16 | CD, last resort |

Formats your device doesn't support are skipped automatically.

## Requirements

- Windows 10/11
- .NET SDK 6+ (`dotnet --version`). If missing: `winget install Microsoft.DotNet.SDK.8`
- No third-party libraries. Pure P/Invoke + COM against built-in Windows APIs.
  (The build pulls the Microsoft `System.Management` NuGet package for WMI.)

## Build & install

Easiest — just double-click:

- **`Build.cmd`** — compiles it (no PowerShell / execution-policy hassle).
- **`Install.cmd`** — builds + enables auto-start (self-elevates to admin).

Once installed it runs at every logon and lives in your **system tray** (a small
speaker icon: green = full quality, amber = lowered for a game, grey = paused).
Right-click it for status, pause/resume, logs, or quit.

**Double-click the tray icon** (or "Open control panel...") for the full **GUI window**:
live device/format/state, running games, learned per-game profiles (clear one or
reset all), a pause toggle, and an auto-start toggle.

Prefer PowerShell? Same thing:

```powershell
.\AudioSwitcher.ps1 -Build      # compile only -> %LOCALAPPDATA%\AudioSwitcher\bin\AudioSwitcher.exe
.\AudioSwitcher.ps1 -Install    # build + install a logon scheduled task (elevated) + start it
```

`-Build` just wraps the committed project — contributors can build it directly:

```powershell
dotnet publish AudioSwitcher.csproj -c Release -r win-x64 --self-contained false
```

(`AudioSwitcher.cs` and `AudioSwitcher.csproj` must sit together.)

After install it auto-starts at logon; you can close the terminal.

Or just run the script with no arguments for an interactive menu:

```powershell
.\AudioSwitcher.ps1
```

## Day-to-day

```powershell
.\AudioSwitcher.ps1 -Status                       # what's running / current format
.\AudioSwitcher.ps1 -ListDevices                  # endpoints (active marked *), with GUIDs
.\AudioSwitcher.ps1 -ShowState                    # learned overrides + crash/glitch logs
.\AudioSwitcher.ps1 -Lock "Greylock-Win64-Shipping.exe" -LockTier 1   # pin a game
.\AudioSwitcher.ps1 -Reset                        # wipe learned overrides
.\AudioSwitcher.ps1 -Uninstall
```

### Debug / run in the foreground

```powershell
.\AudioSwitcher.ps1 -Foreground -Verbose2         # run in this terminal (elevated), live logs
.\AudioSwitcher.ps1 -TestETW                      # 30 s ETW capture (Verbose), prints events
```

### Diagnostics (run the exe directly)

```powershell
$e = "$env:LOCALAPPDATA\AudioSwitcher\bin\AudioSwitcher.exe"
& $e --dump-active                # dump every property of the active endpoints
& $e --probe-format               # show the device's current + mix WAVEFORMAT
& $e --set-format 192000 32       # apply a format directly (test what your device accepts)
& $e --sessions                   # list audio sessions on the default device (pid / state / peak)
& $e --test-etw2                  # 60s capture of the in-process Audio.Client provider (launch a game)
```

## Configuration

First run creates `%LOCALAPPDATA%\AudioSwitcher\config.json`. Editable fields:

- `TargetDeviceName` — **empty = current default playback device**. Set a name
  substring (e.g. `"FiiO K7"`) to pin one specific endpoint instead.
- `Channels` — output channel count (default 2).
- `IdleTier` — tier index used when no game is running (default 0).
- `ProfileTiers` — the rate/bit-depth ladder.
- `EngineDefaults` — starting tier per engine.
- `KnownQuirky` — hardcoded per-exe tiers.
- `LauncherProcesses` / `GamePathHints` — game detection.
- `CrashThresholdSeconds`, `GlitchThreshold`, `GlitchWindowSeconds` — learning sensitivity.
- `SilenceWindowSeconds` — **0 = off (default)**; set e.g. `15` to enable silence learning
  once `--sessions` confirms it reads your games' peak meter. `SilenceGraceSeconds` —
  ignore the first N seconds after launch (loading screens).
- `ProbeEveryLaunches` — **0 = off (default)**. Set e.g. `5` to occasionally retry a
  previously-dropped game one tier *higher*, to find its true ceiling and self-heal
  over-drops (a game promoted on a clean run; re-dropped if it fails). Trade-off: a game
  with a *genuine* limit will crash/glitch on the probe launch, so it's opt-in.

State files (`overrides.json`, `crash-log.json`, `glitch-log.json`) also live in
that folder. Delete the folder for a clean slate. Config is read at startup, so
restart the daemon after editing it.

## Status

Verified on real hardware: build, ETW capture, default-device targeting, format
swap via IPolicyConfig, restore on exit, exit-code-based crash detection,
device-aware fallback.

Built, pending more hardware testing: per-app silence detection (opt-in; validate
with `--sessions` first).

Deliberately **not** attempted (researched dead ends for finding a game's format
limit): reading the game's audio-init result from ETW (the `Microsoft-Windows-Audio`
provider never pairs a requested format with a PID and result), binary string
dumping, automated reverse-engineering, and ML (no labeled dataset). A game's limit
is only knowable by observing its reaction — which is what the crash/glitch/silence
signals do.

### Possible future work
- Coarse "audio-API risk" prior from a game's imports (xaudio2/winmm) to pick a
  smarter starting tier.
- Live re-resolution when the default device changes while running.

## License

MIT — see [LICENSE](LICENSE).
