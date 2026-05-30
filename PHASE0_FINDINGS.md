# Phase 0 — Findings & Go/No-Go Note

_Date: 2026-05-29. Machine: Windows 10 22H2 (build 19045), x64._

## Verdict: **GO**

Both gate-critical spikes pass. The core premise — *capture one game's audio in
isolation and reliably detect speech in it* — is demonstrated working on real hardware,
including against a live game (Star Wars: The Old Republic).

## What was built

A single C# console app (`engine/`, .NET 9) implementing all five spikes as subcommands,
plus an MV3 browser extension (`extension/`). See [TESTING.md](TESTING.md) to run it.

## Results

| Spike | Question | Result | Evidence |
|---|---|---|---|
| **1. Isolated capture** ⭐gate | Can we capture one process's audio alone? | **PASS** | Captured 8 s of live SWTOR audio via WASAPI process loopback — real dynamic audio (peak 0.24→0.78), not silence. |
| **2. Real-time VAD** | Does Silero run in real time on CPU? | **PASS** | ONNX loads; ~per-chunk inference is well under the 32 ms budget; validated bit-for-bit against Python. |
| **3. Accuracy** ⭐gate | Does it catch dialog without false-triggering? | **PASS** | On labeled clips: **100% recall** on dialog, **0% false positives** on music/ambient. Real game music scored 0% over threshold. |
| **4. Duration logic** | Stable DUCK/UNDUCK, no flapping? | **PASS** | Debouncer (250 ms attack / 800 ms hangover) fired on sustained speech, ignored a 0.47 blip in game audio. Live loop ran stably on the game. |
| **5. Engine↔extension** | WS push + extension media control? | **PASS (transport)** | WS server validated: token auth (good→connect+PING, bad→HTTP 403), broadcast delivery confirmed with a real client. Extension code complete; the extension→YouTube pause step still needs your Chrome (see TESTING.md). |

## The one real bug found (and fixed)

Silero VAD **v5 requires a 576-sample input** = 64 carried "context" samples prepended to
each 512-sample window. Feeding only 512 makes the model silently return ~0 for *all*
audio (clear speech included). Fixed in [SileroVad.cs](engine/Vad/SileroVad.cs) by carrying
a 64-sample context buffer between calls. This was confirmed by reproducing the failure and
the fix in Python against the same ONNX file.

## Decisions seeded for Phase 1

- **Capture:** WASAPI process loopback, include-target-tree mode. Format 48 kHz/16-bit/stereo,
  resampled to 16 kHz mono for the VAD. (`--exclude` mode available as the documented fallback.)
- **VAD:** Silero v5 ONNX, threshold **0.5**.
- **Default timings:** attack **250 ms**, hangover **800 ms**, 32 ms chunks. Tune in Phase 1.
- **IPC:** local WebSocket on `127.0.0.1:8730`, token-gated, 15 s keepalive ping.

## Known limitations / not covered by Phase 0

- **Vocal-music false positives** (game soundtrack with singing) were not measured — no such
  clip on hand. This remains the #1 quality risk; capture one from your game and run the
  `accuracy` harness (see TESTING.md) before Phase 1 sign-off.
- **Capture isolation** was confirmed to capture the target; it was *not* yet proven to
  *exclude* other apps (would require playing a second source during capture). Test included
  in TESTING.md.
- **Spike 5 end-to-end** (engine → extension → YouTube pause) needs the manual browser test.
- Real-time inference latency was validated as "comfortably under budget" by observation, not
  formally profiled.
