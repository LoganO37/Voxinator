# Phase 0 — How to Test

This guide walks through running each Phase 0 spike yourself. A summary of what's already
been validated is in [PHASE0_FINDINGS.md](PHASE0_FINDINGS.md).

## Layout

```
GameAudioProject/
  engine/                 C# .NET 9 console app — capture + VAD + debouncer + WebSocket
    publish/voxinator.exe    Self-contained build (RUN THIS — no .NET install needed)
    models/silero_vad.onnx
    testaudio/clips/      Sample labeled clips used below
  extension/              MV3 browser extension (load unpacked in Chrome)
  PLAN.md                 Overall project plan
  PHASE0_FINDINGS.md      Go/No-Go note
```

## Quick start

Open PowerShell and use the published exe (it bundles the runtime, so it works even though
this machine has no .NET 9 runtime installed):

```powershell
cd D:\GameAudioProject\engine\publish
.\voxinator.exe            # prints help / all commands
.\voxinator.exe list       # find your game's PID
```

> The bare `dotnet` command on this machine resolves to a runtime-only install with **no
> SDK**, so use `.\voxinator.exe` to run. To *rebuild* from source, use the full path to the
> SDK installed for this project: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build engine\Voxinator.csproj`.

---

## Phase 1 — Running the product (tray + settings)

This is the intended way to use it now; the spike subcommands below are still there for
diagnostics.

**1. Launch the tray app** — launch it detached so it gets (and auto-hides) its own console:
```powershell
Start-Process .\voxinator.exe -ArgumentList tray
```
A tray icon appears with no console window. (Running `.\voxinator.exe tray` directly in a
terminal also works and no longer hides your shell — but it keeps that terminal occupied
until you Quit the tray. A proper double-click/installer launcher is a Phase 4 polish item.)

Right-click the tray icon (left-click also opens the menu):
- **Status** — Disabled / No sources / Listening / **DUCKING**.
- **Sources (games / apps) ▶** — check **one or more** processes to monitor (e.g. your game
  *and* Discord). Media ducks when **any** checked source has speech. Items are checkable;
  click to toggle. "Clear all sources" removes them. Auto-detected games show a `•auto` tag.
- **Auto-detect games** — when on (default), any **known game from `engine/games.json`** (~130
  popular titles, matched by executable name) is monitored automatically while it's running and
  dropped when it closes. Extend the list by adding `{ "process": "exe-name-without-.exe",
  "title": "..." }` entries to `games.json` (next to the exe).
- **Enabled** — master on/off.
- **Settings…** — port, token, detection threshold, attack (min-speech ms), **end buffer** ms.
- **Quit**.

Settings persist to `%APPDATA%\Voxinator\settings.json`. Sources are remembered by name
and **re-resolved on next launch**; the engine watchdog re-attaches within ~5 s if a source
closes/relaunches. A log is written to `%APPDATA%\Voxinator\log.txt`.

**2. Configure the extension** (options page): set the **per-site action** — `Duck` (smooth
fade, good for music) or `Pause` (instant hard cut, good for audiobooks) — plus the **ramp
duration** (ms; 0 = instant) and duck level. e.g. `music.youtube.com = duck`,
`youraudiobooksite.com = pause`.

**3. Use it:** with the tray running and a source selected, play media in the browser and
trigger game/app dialog. Music fades down/up; audiobooks/paused sites cut instantly.

**Extension robustness (reload after updating the files at `chrome://extensions`).** The
content script now keeps up with real-site behavior — verify:
- Start dialog, then **navigate to a new YouTube video** (SPA swap) → the new video ducks too.
- Start dialog, then let **autoplay-next** load a new video → it ducks.
- With dialog active, **open a new media tab / reload one** → it picks up the duck on load.
- When dialog ends, only media *we* paused/ducked is restored.

**Headless alternative (no tray):**
```powershell
.\voxinator.exe service                      # uses saved settings.json
.\voxinator.exe service --pids 1234,5678      # override the monitored sources for this run
```

---

## Spike 1 — Isolated game-audio capture  ⭐ gate-critical

**Goal:** capture one process's audio, and *only* that process's audio.

```powershell
.\voxinator.exe list                                    # note your game's PID (it changes each launch)
.\voxinator.exe capture --pid <PID> --out game.wav --seconds 15
```

**Pass if:** it writes a WAV whose reported **peak is well above 0** (not silence). Open
`game.wav` in any player — you should hear the game.

**Isolation test (the real proof):** start the capture, and *while it runs*, play something
in another app (e.g. a YouTube tab or Spotify). Open `game.wav` afterward — you should hear
**only the game**, not the other audio. If you do, isolation works.

**Fallback mode:** if a game refuses to be captured (rare; exclusive-mode/DRM), try
`--exclude` to capture everything *except* that process tree:
```powershell
.\voxinator.exe capture --pid <music_app_PID> --out everything_but_music.wav --seconds 15 --exclude
```

---

## Spike 2 — Run the VAD over a recording

```powershell
.\voxinator.exe vad --in testaudio\clips\dialog_ldc.wav      # real speech sample
.\voxinator.exe vad --in game.wav                            # your captured game audio
```

**Pass if:** the speech clip reports a high mean/max probability and lists speech segments;
a music/ambient clip reports near-zero. Tune with `--threshold 0.5`.

---

## Spike 3 — Accuracy on labeled clips  ⭐ gate-critical

Put WAV clips in a folder, named by category prefix:
`dialog_*`, `music_*`, `sfx_*`, `ambient_*`, `vocalmusic_*`.

```powershell
.\voxinator.exe accuracy --dir testaudio\clips
```

**This is the important one for your game.** Record real clips from *your* games:

1. `.\voxinator.exe capture --pid <PID> --out dialog_questA.wav --seconds 20` **during a
   voiced conversation/cutscene** → rename with a `dialog_` prefix.
2. Capture during **music-only / combat / menu** → `music_*`, `sfx_*`, `ambient_*`.
3. **Capture during a song with singing in the soundtrack → `vocalmusic_*`** (this is the
   hard case — the harness reports its false-trigger rate separately).
4. `.\voxinator.exe accuracy --dir <your-folder>`

**Pass if:** recall on `dialog_` clips is high (target ≥90%) and false positives on
non-dialog clips are low. Adjust `--threshold`, `--min-speech-ms`, `--hang-ms` to taste.

---

## Spike 4 — Live DUCK/UNDUCK against a running game

```powershell
.\voxinator.exe live --pid <PID>
```

Now **trigger dialog in the game** (start an NPC conversation / cutscene). You should see:

```
[mm:ss.x] >>> DUCK   (dialog start, p=0.93)
[mm:ss.x] <<< UNDUCK (dialog end)
```

Music/combat/ambient should **not** trigger it. Press Ctrl+C to stop. Tune responsiveness:
- `--min-speech-ms 250` — how long speech must persist before DUCK (lower = snappier, more false triggers).
- `--hang-ms 800` — silence tolerated before UNDUCK (higher = fewer flickers between lines).
- `--threshold 0.5` — detection sensitivity.

**Offline sanity check (no game needed):** pipe any WAV through the *exact* live pipeline:
```powershell
.\voxinator.exe simlive --in testaudio\clips\dialog_ldc.wav   # should print ">>> DUCK ... LIVE PATH OK"
.\voxinator.exe simlive --in <your_game_capture>.wav
```

---

## Spike 5 — End-to-end: engine → browser extension

**1. Load the extension** (one-time; the *same* `D:\GameAudioProject\extension` folder works
in both browsers):
- **Chrome / Chromium / Edge** → `chrome://extensions` → enable **Developer mode** → **Load
  unpacked** → select `D:\GameAudioProject\extension`.
- **Firefox** → `about:debugging#/runtime/this-firefox` → **Load Temporary Add-on…** → pick
  `manifest.json` inside that folder. (Temporary add-ons are cleared on Firefox restart —
  reload the same way. You may need to grant `<all_urls>` host access via `about:addons` →
  the extension → **Permissions**.)
- Each browser prints a harmless warning about the *other's* background key
  (`background.scripts` on Chrome, `service_worker` on Firefox) — expected; it still loads.
- Open the extension's **options** (Chrome: Details → Extension options; Firefox: about:addons
  → the extension → Preferences) and confirm port `8730` / token `changeme`; set per-site
  actions, duck level, and ramp. YouTube defaults to **Pause**.

**2. Start the engine in serve mode:**
```powershell
.\voxinator.exe serve --pid <game_PID> --port 8730 --token changeme
```
The console prints `ws://127.0.0.1:8730/?token=changeme`. The options page should flip to
**"connected to engine"** within a couple of seconds.

**3. Test:** play a YouTube video, then **trigger game dialog**. The video should **pause**
(or duck, per your setting) on dialog start and **resume** on dialog end. The options page
shows **"DUCKING NOW"** while active.

> Quick check without a game: you can confirm the bridge end-to-end by capturing the WS — if
> the options page says "connected", the transport works; the DUCK/UNDUCK depends on the
> engine detecting dialog.

---

## Rebuilding from source

```powershell
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
& $dotnet build   "D:\GameAudioProject\engine\Voxinator.csproj" -c Debug          # dev build (needs .NET 9 runtime via the muxer)
& $dotnet publish "D:\GameAudioProject\engine\Voxinator.csproj" -c Release -r win-x64 --self-contained true -o "D:\GameAudioProject\engine\publish"
```

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `You must install or update .NET` when running `voxinator.exe` from `bin\` | That's the framework-dependent build; run `engine\publish\voxinator.exe` instead (self-contained). |
| Capture WAV is silent / "near silence" warning | Wrong PID, or the app wasn't playing audio. Re-run `voxinator list` (PIDs change each launch). |
| `IAudioClient.Initialize failed (0x88890008)` | The loopback device rejected the format on your system; tell me and I'll add a float-format fallback. |
| Options page stuck on "not connected" | Engine not in `serve` mode, port mismatch, or token mismatch. Check the `serve` console line matches the options page. |
| **Firefox** not connecting; console shows `Content-Security-Policy: Upgrading insecure request 'ws://...' to use 'wss'` | Firefox was upgrading `ws://`→`wss://`; the engine is plain `ws`. Fixed by the extension's explicit CSP (manifest v0.2.1+). **Reload the extension.** If it still upgrades, check `about:preferences#privacy` → **HTTPS-Only Mode** and add an exception for the loopback (or disable it). |
| `HttpListener` access denied / port in use | Another process holds 8730 — pass `--port 8731` to `serve` and set the same in options. |
| VAD returns ~0 on obvious speech | Should be fixed (the 64-sample context bug); if you see it, the model file may be a non-v5 build. |

## What Phase 0 did NOT cover (by design)

Automatic game detection, desktop-app (non-browser) control, per-game profiles, smooth
volume ramps, installer/autostart, and formal latency profiling — these are Phase 1+ in
[PLAN.md](PLAN.md).
