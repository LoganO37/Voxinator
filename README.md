# Voxinator

**Hear your game's dialog over your music, audiobooks, and videos — automatically.**

Voxinator listens to a game's audio, detects when **speech/dialog** is playing, and ducks
(lowers) or pauses your *other* apps so you never miss a line — then restores them when the
dialog ends. It works **natively through Windows** — the per-app volume mixer and the system
media controls — so it covers any app (browsers, the desktop Spotify app, etc.) with **no
browser extension to install**. It can monitor more than one source at once (e.g. a game **and**
a Discord call), so your music dips whenever anyone is talking. It also **auto-detects popular
games** — when a known game (from a bundled, editable list of ~130) starts, it's monitored
automatically; you can still pick anything manually.

> Status: working on **Windows 10/11** (build 19041+). Native control — no extension required.
> See [PLAN.md](PLAN.md) for the design and roadmap, and [TESTING.md](TESTING.md) to run it.

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

The key idea: the engine analyzes **only the game's own audio** (via Windows process-loopback
capture), so the media it's ducking never confuses the speech detector. Each app's action —
**duck**, **pause**, or **ignore** — comes from a global default plus optional per-app overrides;
the monitored game(s) and Voxinator itself are always left alone.

## Repo layout

```
engine/        C# / .NET 9 console + system-tray app (detector + native audio control)
  Voxinator.csproj
  Audio/                     NativeDucker (mixer), MediaSessionController (SMTC), coordinator
  models/silero_vad.onnx     Silero VAD v5 model (MIT)
  games.json                 popular-games library for auto-detection (editable)
PLAN.md        Design & build plan / roadmap
TESTING.md     How to build, run, and test every piece
PHASE0_FINDINGS.md   De-risking results
```

## Quick start

**Requirements:** Windows 10 v2004+ (build 19041+), and the **.NET 9 SDK** to build.

**Engine:**
```powershell
dotnet build engine/Voxinator.csproj -c Release
# or a self-contained exe that needs no .NET install:
dotnet publish engine/Voxinator.csproj -c Release -r win-x64 --self-contained true -o engine/publish
```
Run the tray app (pick your game under **Sources**), or run headless:
```powershell
engine\publish\voxinator.exe tray
engine\publish\voxinator.exe service --pids <gamePID>     # headless
voxinator.exe help                                         # all commands
```

In the app, the **Apps** tab sets the global action (duck or pause), the duck level and
fade-back time, and any per-app overrides (duck / pause / ignore). There's nothing else to
install — Voxinator controls other apps directly through Windows.

Full step-by-step instructions and troubleshooting are in [TESTING.md](TESTING.md).

## Built with

- [Silero VAD](https://github.com/snakers4/silero-vad) (voice activity detection, MIT)
- [ONNX Runtime](https://onnxruntime.ai/) and [NAudio](https://github.com/naudio/NAudio)
- .NET 9, WASAPI process loopback + per-app session volume, WinRT System Media Transport Controls
