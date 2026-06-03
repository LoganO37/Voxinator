# Voxinator

**Hear your game's dialog over your music, audiobooks, and videos — automatically.**

Voxinator listens to a game's audio, detects when **speech/dialog** is playing, and ducks
(lowers) or pauses your *other* apps so you never miss a line — then restores them when the
dialog ends. It works **natively through Windows** — the per-app volume mixer and the system
media controls — so it covers any app (browsers, the desktop Spotify app, etc.) with **no
browser extension to install**. It can monitor more than one source at once (e.g. a game **and**
a Discord call), so your music dips whenever anyone is talking. It also **auto-detects popular
games** — when a known game (from a bundled, editable list of 256) starts, it's monitored
automatically; you can still add anything manually.

> **Windows 10/11** (build 19041+). Native control — no extension required.
> Installs per-user (no admin) and keeps itself up to date silently.

## Install

1. Download **`Voxinator-win-Setup.exe`** from the
   [latest release](https://github.com/LoganO37/Voxinator/releases/latest).
2. Run it. It installs per-user to `%LOCALAPPDATA%\Voxinator` (no admin prompt) and adds
   Desktop + Start-Menu shortcuts.
3. Launch Voxinator — it lives in the system tray. Open the window from the tray icon.

That's it. There's nothing else to install: Voxinator controls your other apps directly
through Windows.

**Updates are automatic.** On launch the app checks GitHub Releases in the background,
downloads any new version, and applies it the next time you close the app — no prompts. The
**Settings** page shows your version and a *Restart now* button if you'd rather update
immediately.

Prefer not to install? A portable build (`Voxinator-win-Portable.zip`) is attached to each
release too.

## Using it

The app has two pages:

- **Dashboard** — the master on/off switch, the quick **Duck / Pause** control and duck level,
  the list of what's **monitored now** (each with a *Stop monitoring* ✕), and **Sources**:
  toggles for *auto-detect games* and *duck for voice chat*, plus lists of what's **playing
  now** and what you've **used before** so you can add a source in one click.
- **Settings** — launch-at-startup, your version/update status, detection tuning (sensitivity,
  attack, end-buffer), the fade-back time, and **per-app rules** to override the default for a
  specific app or set one to **Ignore** so it's never touched.

**Voice chat:** turn on *Duck for voice chat* on the Dashboard and Voxinator monitors common
call apps (Discord, TeamSpeak, Zoom, Teams, Slack, Mumble) as dialog sources — your media dips
whenever someone is talking. Like any source, it has its own *Stop monitoring* button.

## How it works

```
GAME / Discord ─(WASAPI process loopback)─► Native engine (C#/.NET 9)
                                            • capture each source's audio in isolation
                                            • Silero VAD detects speech (16 kHz mono)
                                            • debounce: attack + end-buffer
                                            • on speech in ANY source, act on other apps:
                                                     │
                                          ┌──────────┴───────────┐
                                          ▼                      ▼
                              WASAPI per-app mixer      System Media Transport
                              (duck: instant cut +      Controls (pause / resume
                               fade back in)             media sessions)
```

The key idea: the engine analyzes **only the monitored source's own audio** (via Windows
process-loopback capture), so the media it's ducking never confuses the speech detector. Each
app's action — **duck**, **pause**, or **ignore** — comes from a global default plus optional
per-app overrides; the monitored source(s) and Voxinator itself are always left alone. Apps
set to *pause* without a media session fall back to ducking.

## Build from source

**Requirements:** Windows 10 v2004+ (build 19041+) and the **.NET 9 SDK**.

```powershell
dotnet build engine/Voxinator.csproj -c Release
# or a self-contained exe that needs no .NET install:
dotnet publish engine/Voxinator.csproj -c Release -r win-x64 --self-contained true -o engine/publish
```

Run the tray app, or run headless:

```powershell
engine\publish\voxinator.exe              # tray app (no args)
engine\publish\voxinator.exe service --pids <gamePID>   # headless engine
engine\publish\voxinator.exe list                       # find a process's PID
engine\publish\voxinator.exe help                       # all commands
```

To cut a release (installer + the auto-update feed), bump `<Version>` in
`engine/Voxinator.csproj` and run:

```powershell
.\release.ps1 -Upload          # build + pack + publish to GitHub Releases
```

This packages with [Velopack](https://velopack.io/) into `Releases\` (Setup.exe + full/delta
packages) and publishes them as the app's auto-update feed.

## Repo layout

```
engine/        C# / .NET 9 system-tray app (detector + native audio control)
  Voxinator.csproj
  Audio/                     NativeDucker (mixer), MediaSessionController (SMTC), MediaController
  VoiceApps.cs               known voice-chat apps for the "duck for voice chat" option
  models/silero_vad.onnx     Silero VAD v5 model (MIT)
  games.json                 256-game library for auto-detection (editable)
  ui/                        WebView2 dashboard (HTML/CSS/JS)
release.ps1    Build + package (Velopack) + publish a release
PLAN.md        Design & build plan / roadmap
TESTING.md     How to build, run, and test every piece
```

## Built with

- [Silero VAD](https://github.com/snakers4/silero-vad) (voice activity detection, MIT)
- [ONNX Runtime](https://onnxruntime.ai/) and [NAudio](https://github.com/naudio/NAudio)
- [Velopack](https://velopack.io/) for packaging and silent auto-updates
- .NET 9, WASAPI process loopback + per-app session volume, WinRT System Media Transport Controls
