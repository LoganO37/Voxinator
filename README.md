# Voxinator

**Hear your game's dialog over your music, audiobooks, and videos, automatically.**

Voxinator listens to a game's audio and detects when someone is speaking. When it hears dialog,
it lowers (ducks) or pauses your other apps so you don't miss a line, then brings them back when
the talking stops. It does this through Windows itself, using the per-app volume mixer and the
system media controls, so it works with any app (browsers, the desktop Spotify app, and so on)
and there's no browser extension to install.

You can watch more than one source at a time. Run it on a game and a Discord call together and
your music will dip whenever anyone talks. Voxinator also knows about 256 popular games: when one
of them starts, it gets monitored automatically. You can always add other apps by hand.

> Works on Windows 10 and 11 (build 19041 or newer). Installs per-user with no admin prompt and
> updates itself in the background.

## Install

1. Download `Voxinator-win-Setup.exe` from the
   [latest release](https://github.com/LoganO37/Voxinator/releases/latest).
2. Run it. It installs to `%LOCALAPPDATA%\Voxinator` for your account only, with no admin prompt,
   and adds Desktop and Start Menu shortcuts.
3. Launch Voxinator. It runs in the system tray; click the tray icon to open the window.

There's nothing else to set up. Voxinator controls your other apps directly through Windows.

Updates happen on their own. Each time the app starts it checks GitHub for a newer version,
downloads it in the background, and installs it the next time you close the app, with no prompts.
The Settings page shows your current version and a Restart now button if you'd rather update right
away.

If you'd rather not install anything, every release also includes a portable build,
`Voxinator-win-Portable.zip`.

## Using it

The app has two pages.

The **Dashboard** is where you'll spend most of your time. It has the master on/off switch, the
Duck or Pause choice and the duck level, a list of what's being monitored right now (each with a
Stop monitoring button), and a Sources area. Sources has toggles for auto-detecting games and for
ducking on voice chat, plus two lists: what's playing now and what you've used before, so you can
add a source with one click.

**Settings** holds the rest: launch on startup, your version and update status, the detection
controls (sensitivity, attack, and end-buffer), the fade-back time, and per-app rules. A rule
lets you override the default for one app, or set an app to Ignore so Voxinator never touches it.

For voice chat, turn on "Duck for voice chat" on the Dashboard. Voxinator will watch the common
call apps (Discord, TeamSpeak, Zoom, Teams, Slack, and Mumble) and treat someone talking the same
way it treats game dialog. Each one gets its own Stop monitoring button, like any other source.

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

The trick is that the engine only listens to the monitored source's own audio, captured with
Windows process loopback, so the media it's ducking can never fool the speech detector. Each app's
response (duck, pause, or ignore) comes from a global default that you can override per app. The
source you're monitoring and Voxinator itself are always left alone. If an app is set to pause but
has no media session to control, Voxinator ducks it instead.

## Build from source

You'll need Windows 10 version 2004 or newer (build 19041) and the .NET 9 SDK.

```powershell
dotnet build engine/Voxinator.csproj -c Release
# or a self-contained exe that needs no .NET install:
dotnet publish engine/Voxinator.csproj -c Release -r win-x64 --self-contained true -o engine/publish
```

Run the tray app, or run it headless:

```powershell
engine\publish\voxinator.exe              # tray app (no args)
engine\publish\voxinator.exe service --pids <gamePID>   # headless engine
engine\publish\voxinator.exe list                       # find a process's PID
engine\publish\voxinator.exe help                       # all commands
```

To cut a release, bump `<Version>` in `engine/Voxinator.csproj` and run:

```powershell
.\release.ps1 -Upload          # build, pack, and publish to GitHub Releases
```

That packages the app with [Velopack](https://velopack.io/) into `Releases\` (Setup.exe plus full
and delta packages) and publishes them as the feed the app updates from.

## Repo layout

```
engine/        C# / .NET 9 system-tray app (detector + native audio control)
  Voxinator.csproj
  Audio/                     NativeDucker (mixer), MediaSessionController (SMTC), MediaController
  VoiceApps.cs               known voice-chat apps for the "duck for voice chat" option
  models/silero_vad.onnx     Silero VAD v5 model (MIT)
  games.json                 256-game library for auto-detection (editable)
  ui/                        WebView2 dashboard (HTML/CSS/JS)
release.ps1    Build, package (Velopack), and publish a release
PLAN.md        Design and build plan
TESTING.md     How to build, run, and test every piece
```

## Built with

- [Silero VAD](https://github.com/snakers4/silero-vad) for voice activity detection (MIT)
- [ONNX Runtime](https://onnxruntime.ai/) and [NAudio](https://github.com/naudio/NAudio)
- [Velopack](https://velopack.io/) for packaging and background updates
- .NET 9, WASAPI process loopback and per-app session volume, WinRT System Media Transport Controls
