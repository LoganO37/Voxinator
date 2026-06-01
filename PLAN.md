# Voxinator — Design & Build Plan

> **Goal:** Improve consumption of secondary media (music, audiobooks, YouTube) while
> playing video games. The program detects when **game dialog** is playing — by analyzing
> the game's audio for human speech — and, based on the dialog's duration, automatically
> **pauses or ducks** the secondary media so the player can hear the dialog, then restores
> it afterward.

---

## 1. The core insight that drives the architecture

The make-or-break decision is **what audio we analyze**.

A naive "listen to everything and detect voice" approach **cannot work**, because the
audiobook / podcast / YouTube video we are trying to duck is *itself* human voice. The
detector would trip on the very media it is supposed to control, creating a feedback loop
(duck → less voice → unduck → voice → duck …).

**The fix: capture only the game's audio stream, in isolation, and run voice detection on
that.** Windows 10 version 2004+ (target machine is 22H2 / build 19045 — supported) exposes
**WASAPI Process Loopback**, which captures the audio of one specific process tree. The
engine listens to *just the game*, detects dialog there, and tells the browser to get out of
the way. The media we are controlling never enters the detector.

This single decision removes the hardest ambiguity in the project.

---

## 2. Scope decisions (v1)

| Decision | v1 behavior | Future |
|---|---|---|
| **Game selection** | Manual picker — user selects the game process | Automatic detection, esp. a library of popular games |
| **Controlled media** | **Browser only** (YouTube, web music players, web audiobook players) via the extension | Desktop apps (Spotify, Audible, etc.) via per-session volume + SMTC |
| **YouTube default action** | **Pause** (configurable) | — |
| **Settings** | Per-situation choice of **pause vs. reduce volume**, plus a **configurable duck amount (%)** | Per-game profiles |
| **Platform** | Windows-first | macOS / Linux ports |
| **Browsers** | Chrome + Firefox (WebExtensions / MV3) | Edge (Chromium, ~free) |

**Explicitly out of v1 scope** (deferred, but architected for):
- ~~Automatic game detection / popular-game library.~~ ✅ **Implemented** — bundled,
  editable `engine/games.json` (~130 popular games); the engine auto-adds any that are running.
- Control of non-browser desktop media apps.
- Cross-platform support.

---

## 3. High-level architecture

```
┌─────────────────────────── Windows PC ───────────────────────────┐
│                                                                   │
│   GAME process ──(WASAPI process loopback)──┐                     │
│                                             ▼                     │
│   ┌─────────────────── NATIVE ENGINE ───────────────────┐         │
│   │  1. Capture game audio (per-process, isolated)       │        │
│   │  2. Resample → 16 kHz mono                            │        │
│   │  3. Voice Activity Detection (Silero VAD)            │         │
│   │  4. Duration logic (attack / hold / release ramps)  │         │
│   │  5. Emit DIALOG_START / DIALOG_END events           │         │
│   └───────────────────────┬──────────────────────────────┘        │
│                           │ WebSocket (127.0.0.1 + token)          │
│                           ▼                                        │
│   ┌─────────────────── BROWSER EXTENSION ───────────────────┐     │
│   │  • Background worker: WS client, reconnect, keepalive    │     │
│   │  • Content script: find <video>/<audio>, pause or duck   │     │
│   │  • Resume only what WE paused (per-tab state)            │      │
│   │  • Options page: per-situation action + duck %          │      │
│   └──────────────────────────────────────────────────────────┘    │
└────────────────────────────────────────────────────────────────────┘
```

In v1 **all controlled media lives in the browser**, so the extension is the only action
sink. The native engine remains mandatory because game-audio capture + VAD cannot be done
inside a browser.

---

## 4. Component breakdown

### A. Native engine (the brain)

| Stage | Approach | Notes |
|---|---|---|
| **Capture** | WASAPI Process Loopback (`ActivateAudioInterfaceAsync` + `PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE`) targeting the selected game PID | Captures game-and-children only. v1 = manual picker. |
| **Pre-process** | Downmix to mono, resample to 16 kHz | Matches VAD model input; cheap. |
| **Detection** | **Silero VAD** (ONNX, ~1–2 MB, real-time on CPU) | See §5 for rationale vs. raw vocal-band gating. |
| **Duration logic** | Attack (~150–300 ms of speech before triggering), Hold/hangover (~600–1200 ms after speech stops), smooth state transitions | Implements the "through its duration" requirement; prevents flapping on natural pauses between lines. |
| **Dispatch** | Emit `DIALOG_START` / `DIALOG_END` over WebSocket | The extension decides pause vs. duck per its settings. |

### B. Browser extension (the only action sink in v1)

- **MV3 extension**, Chrome + Firefox via WebExtensions APIs.
- **Background service worker:** WebSocket client to the engine; auto-reconnect; keepalive
  ping to survive MV3 worker suspension.
- **Content script:** locates `<video>` / `<audio>` elements; on `DIALOG_START` pauses or
  lowers `.volume` per settings; on `DIALOG_END` restores — **only resuming what it paused**
  (tracks per-tab state so it never un-pauses something the user paused themselves, and never
  restores a volume the user changed mid-duck).
- **Options page (settings):**
  - Per **situation** (per site/category — YouTube, music players, audiobook players):
    choose **Pause** or **Reduce volume**.
  - **Duck amount** slider (target volume %, e.g. reduce to 20%).
  - YouTube default = **Pause**.
  - Master enable/disable; per-site allow-list.

### C. IPC bridge: engine ↔ extension

**Local WebSocket server in the engine**, bound to `127.0.0.1` with a shared handshake token.

- *Why WebSocket over Native Messaging?* Native Messaging is request/response *initiated by
  the extension*, and the MV3 worker can be killed at any time — awkward for the engine
  *pushing* "duck now" events. A WebSocket gives clean, low-latency server→client push and
  trivial reconnect. (Native Messaging is the fallback if a listening loopback socket is
  undesirable.)
- Message shape (illustrative): `{type: "DIALOG_START"|"DIALOG_END", ts, confidence}`.

---

## 5. Key technical recommendations

**On detecting "the narrow human vocal range":** the instinct points at the right physics
(voiced speech sits in a recognizable pitch/formant region — fundamentals ~85–255 Hz,
formants up to ~3.4 kHz). But raw band-energy gating **false-triggers constantly** on game
music and SFX, which are loud in exactly that band. A purpose-built VAD is dramatically more
reliable on mixed audio (dialog + soundtrack + effects in one stream).

- **Primary:** **Silero VAD** — small, fast, free, robust; real time on CPU via ONNX Runtime.
- **Optional refinement (a nod to the band idea):** a band-energy / pitch pre-gate *in series*
  with the VAD to suppress obvious non-speech and cut false positives further.
- **Honest limitation:** no detector perfectly separates *game dialog* from *vocals in the
  game's own soundtrack*. Mitigate with sensitivity + duration tuning, and (later) per-game
  profiles. This is the #1 quality risk — validated in Phase 0.

**Latency target:** keep capture → detect → act in the tens-to-low-hundreds of ms so ducking
feels responsive without jitter. The duration/hold logic keeps it smooth.

---

## 6. Recommended tech stack

| Piece | Recommendation | Alternatives |
|---|---|---|
| Native engine | **C# / .NET** with **NAudio** + **ONNX Runtime** for Silero | Rust (`windows-rs`, `cpal`), or C++/WinRT (Microsoft's `ApplicationLoopback` sample) |
| VAD model | **Silero VAD** (ONNX) | WebRTC VAD (lighter, noisier) |
| IPC | Local **WebSocket** (`127.0.0.1` + token) | Native Messaging |
| Extension | **MV3 / WebExtensions** (Chrome + Firefox) | — |
| Tray / settings UI | Engine: minimal tray app for game picker + status; settings live in the extension options page | Web-based config served by the engine |

> **Stack caveat to confirm in Phase 0:** NAudio's support for *process-specific* loopback
> (as opposed to whole-device `WasapiLoopbackCapture`) is recent/limited and may require
> direct interop with `ActivateAudioInterfaceAsync`. Spike 1 settles this and picks the final
> capture implementation.

---

## 7. Phased roadmap

- **Phase 0 — De-risking spikes.** ✅ **COMPLETE** — all five spikes pass; isolated capture,
  VAD, accuracy, live ducking, and the WS bridge are validated on real hardware. See
  [PHASE0_FINDINGS.md](PHASE0_FINDINGS.md) and [TESTING.md](TESTING.md). A working prototype
  engine (`engine/`) and extension (`extension/`) already exist from this phase.
- **Phase 1 — Engine MVP + hardening.** ✅ **BUILT** (pending interactive tray/extension
  testing). The spike engine is now a real service: a **system-tray app** (`voxinator tray`) with
  a live game picker, enable/disable, a settings dialog, game re-resolution by name + a
  watchdog for relaunches; a **persistent JSON settings store** (`%APPDATA%`) for threshold,
  attack, end buffer, port/token, and **one or more monitored sources** — you can watch a game
  *and* e.g. Discord at once, and media ducks when **any** source has speech (each source gets
  its own capture + VAD; OR-aggregated). A shared `DetectionEngine` (also runnable headless via
  `voxinator service --pids ...`); and **extension volume ramps** — duck = smooth fade (music),
  pause = instant hard cut (audiobooks), with configurable ramp duration. Validated: settings
  load drives the engine, the service path captures two live sources at once + serves the WS,
  and the tray launches/runs.
- **Phase 2 — Browser bridge + extension.** ✅ **DONE** (built in Phase 0/1; hardened here).
  Local WebSocket server; MV3 extension; content script pauses/ducks web `<video>`/`<audio>`;
  resume-only-what-we-paused. Hardened for real sites: a MutationObserver + per-element play
  listeners re-apply the current duck state to media added or started **mid-dialog** (YouTube
  SPA navigation, autoplay-next), and content scripts query engine state on init so tabs
  opened/reloaded during dialog still duck. **Cross-browser**: one codebase loads in both
  Chrome/Chromium and Firefox (dual `service_worker`+`scripts` background, `gecko` id, and a
  `browser.*`/`chrome.*` namespace shim). Needs interactive browser verification (TESTING.md).
- **Phase 3 — Settings & UX.** Two settings surfaces: the **extension options** (per-situation
  pause vs. duck, duck-amount slider, per-site rules, master toggle; YouTube default = pause)
  and the **engine settings** (threshold, attack, **end buffer**, selected game, port/token),
  ideally exposed in the tray UI. Both prototyped in Phase 0; this phase makes them persistent
  and user-friendly.
- **Phase 4 — Polish.** Installer, auto-start, WS reconnect robustness, multi-output-device
  handling, logging/diagnostics, sensitivity slider.
- **Phase 5+ (post-v1).** ✅ **Automatic game detection** built — a bundled popular-game
  library (`engine/games.json`, ~130 titles keyed by executable name); the engine scans
  running processes each watchdog tick and auto-monitors matches (and drops them when closed),
  alongside manual sources, toggled by "Auto-detect games". Remaining: desktop-app media
  control (per-session volume + SMTC), per-game profiles, macOS/Linux.

---

## 8. Phase 0 — Concrete spike checklist

Five focused spikes, each independently time-boxed, with a clear question, success criteria,
and fallback. Spikes 1 and 3 are the project's true risk; the **go/no-go gate** depends on
both passing.

> Suggested order: **1 → 2 → 3 → 4 → 5.** Spikes 1 and 2 can run in parallel if two people are
> available. Spike 3 depends on 1 (needs real captured audio) and 2 (needs the VAD running).

### Spike 1 — Isolated game-audio capture (WASAPI Process Loopback)
- **Question:** Can we capture a single game process's audio in isolation on Windows 22H2?
- **Steps:**
  1. Enumerate processes; pick a target PID (start with a simple app, then a real game).
  2. Activate process loopback (`ActivateAudioInterfaceAsync` with
     `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`, mode
     `INCLUDE_TARGET_PROCESS_TREE`).
  3. Write the captured stream to a WAV file for ~30 s.
  4. While capturing, play audio in **another** app (e.g., a browser tab) simultaneously.
- **Success criteria:** WAV contains the target's audio and **excludes** the other app's
  audio. Format/sample-rate is known and usable.
- **Tools:** C#/.NET + NAudio first; if NAudio lacks process-loopback, fall back to direct
  interop or port Microsoft's C++ `ApplicationLoopback` sample.
- **Fallback if it fails:** Use whole-device loopback in *exclude* mode (capture everything
  except the browser/music app) — less clean but still avoids the feedback loop. Document the
  trade-off.
- **Time-box:** 2–3 days. **(Gate-critical.)**

### Spike 2 — Real-time Silero VAD inference
- **Question:** Does Silero VAD run comfortably in real time on CPU via ONNX Runtime?
- **Steps:**
  1. Load the Silero ONNX model in the chosen language (ONNX Runtime).
  2. Feed 16 kHz mono frames (e.g., 30 ms) from a WAV; collect per-frame speech probabilities.
  3. Measure per-frame inference time and steady-state CPU/memory over several minutes.
- **Success criteria:** Per-frame inference ≪ frame duration (comfortable real-time factor);
  no memory growth over a sustained run.
- **Tools:** ONNX Runtime; the Spike 1 WAV as input.
- **Fallback:** WebRTC VAD (lighter) if Silero is too heavy; revisit accuracy in Spike 3.
- **Time-box:** 1–2 days.

### Spike 3 — Detection accuracy on real mixed game audio  ⚠️ highest risk
- **Question:** On real game audio (dialog + soundtrack + SFX in one stream), how good is
  detection, and how bad are false positives?
- **Steps:**
  1. Collect/record labeled clips: (a) **dialog over music**, (b) **music only**, (c)
     **combat/SFX only**, (d) **ambient/quiet**, plus (e) **vocal-heavy soundtrack, no
     dialog** (the hard case).
  2. Run the Spike 2 VAD over each; record speech probabilities.
  3. Apply a candidate threshold + duration logic; compute confusion matrix
     (true/false positive/negative) per category.
  4. Test the optional band-energy pre-gate; re-measure.
- **Success criteria (proposed, tune with stakeholder):** catches **≥90%** of real dialog;
  false-positive rate on music-only/SFX-only categories is low enough to be tolerable with the
  hold timer; the vocal-soundtrack case is characterized (and the residual failure mode is
  documented as a known limitation).
- **Tools:** the Spike 1/2 pipeline + a small manual labeling sheet.
- **Fallback:** If accuracy is unacceptable: add the band pre-gate, raise the attack threshold,
  expose a sensitivity slider, and/or scope toward per-game tuning earlier than planned.
- **Time-box:** 3–4 days. **(Gate-critical.)**

### Spike 4 — Duration logic & end-to-end latency feel
- **Question:** Do attack/hold/release timings feel responsive but stable, and what is the
  capture→event latency?
- **Steps:**
  1. Implement a minimal debouncer (attack, hold, release) over the live VAD output.
  2. Print `DUCK` / `UNDUCK` to console on real game audio in real time.
  3. Eyeball responsiveness; measure capture→event latency; sweep timing values.
- **Success criteria:** No rapid flapping during natural speech pauses; perceived
  responsiveness within the tens-to-low-hundreds-of-ms target. Record the timing values that
  feel best as Phase 1 defaults.
- **Tools:** the live Spike 1+2 pipeline.
- **Fallback:** Adjust window sizes / hold duration; if latency too high, reduce frame size or
  buffering.
- **Time-box:** 1–2 days.

### Spike 5 — Engine ↔ extension WebSocket round-trip
- **Question:** Can the engine reliably push events to an MV3 extension and actually pause a
  YouTube video, including after the service worker sleeps?
- **Steps:**
  1. Stand up a minimal WS server (`127.0.0.1`) in the engine that emits a test event.
  2. Minimal MV3 extension: background worker connects, logs events; content script calls
     `video.pause()` on YouTube on event.
  3. Force the worker to suspend/idle, then send another event; verify reconnect + delivery.
  4. Measure event→pause latency in the page.
- **Success criteria:** Event delivered and the video pauses within the latency target;
  reconnect works after worker suspension; no duplicate/zombie connections.
- **Tools:** any WS library in the engine's language; a throwaway unpacked extension.
- **Fallback:** Add keepalive ping / `chrome.alarms`-driven reconnect; if MV3 lifecycle proves
  unreliable, evaluate Native Messaging.
- **Time-box:** 2 days.

### Phase 0 exit criteria (go/no-go gate)
- ✅ Spike 1 passes (isolated game capture works, or an acceptable exclude-mode fallback).
- ✅ Spike 3 meets the agreed accuracy bar (or a viable mitigation path is identified).
- ✅ Spikes 2, 4, 5 pass or have clear fallbacks.
- 📄 Produce a 1-page findings note: chosen capture method, chosen VAD + threshold, default
  timing values, measured latency, and known limitations. This seeds Phase 1.

---

## 9. Risks & mitigations

| Risk | Severity | Mitigation |
|---|---|---|
| VAD false positives on game soundtrack vocals | High | Sensitivity + duration tuning; band pre-gate; per-game profiles (later); validated in Spike 3 |
| Process loopback unsupported/awkward for some games (DRM/exclusive mode) | Med | Exclude-mode whole-device capture fallback (Spike 1) |
| MV3 service worker killed → WS drops | Med | Keepalive + auto-reconnect; idempotent state (Spike 5) |
| Resuming media the user paused/changed themselves | Med | Track per-tab "we paused this" state; restore only our own changes |
| Multiple audio output devices / routing | Med | Let user pick device + game explicitly |
| Latency vs. stability trade-off | Low–Med | Tune in Spike 4 against real audio |

---

## 10. Open questions

- Accuracy bar for Spike 3 — is "≥90% dialog caught, low music false-positive after hold"
  the right target, or stricter/looser?
- For the future auto-detection: detect by **process/executable name** (simplest), by window
  title, or via a maintained game database?
- Should the tray app expose a quick "snooze / disable for 10 min" control in v1?
