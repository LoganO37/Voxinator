// Cross-browser content script (Chrome/Chromium + Firefox). Pauses or ducks media while
// game/app dialog is active.
//   - "pause" -> instant hard cut (good for audiobooks / spoken-word).
//   - "duck"  -> volume drops INSTANTLY when dialog starts, then fades back in over rampMs
//               when dialog ends (good for music). The cut is always instant; rampMs only
//               controls the fade-back-in.
//
// Robust to real sites: remembers the current duck state and re-applies it to media added or
// started mid-dialog (YouTube SPA navigation, autoplay-next), and syncs state on init so a
// tab opened/reloaded during dialog still ducks. Only ever restores media that WE changed.
const api = (typeof browser !== "undefined") ? browser : chrome;

const SETTINGS_DEFAULTS = {
  defaultAction: "pause",
  duckVolume: 0.2,
  rampMs: 300,
  perSite: "youtube.com = pause\nmusic.youtube.com = duck\nmusic.amazon.com = duck\nsoundcloud.com = duck\naudible.com = pause",
};

let settings = SETTINGS_DEFAULTS;
let ducking = false; // current engine state as known to this frame

api.storage.local.get(SETTINGS_DEFAULTS).then((s) => (settings = s));
api.storage.onChanged.addListener((c, area) => {
  if (area === "local") api.storage.local.get(SETTINGS_DEFAULTS).then((s) => (settings = s));
});

function actionForHost() {
  // Among all matching rules, the most specific (longest) host pattern wins — so
  // "music.youtube.com = duck" overrides "youtube.com = pause" for that host, regardless
  // of the order they're listed in.
  const host = location.hostname.toLowerCase();
  let best = null;
  let bestLen = -1;
  for (const line of (settings.perSite || "").split("\n")) {
    const parts = line.split("=");
    if (parts.length !== 2) continue;
    const h = parts[0].trim().toLowerCase();
    const a = parts[1].trim().toLowerCase();
    if (!h || (a !== "pause" && a !== "duck")) continue;
    const matches = host === h || host.endsWith("." + h); // exact host or a subdomain of it
    if (matches && h.length > bestLen) { best = a; bestLen = h.length; }
  }
  return best || settings.defaultAction || "pause";
}

const media = () => Array.from(document.querySelectorAll("video, audio"));
const clamp01 = (v) => Math.max(0, Math.min(1, Number(v)));

// Plain element-volume control (0..1) for normal sites. YouTube-style players that route
// through Web Audio (where `.volume` is ignored) are handled by page.js in the page's MAIN
// world, which can reach the player's setVolume API in every browser.
function volController(el) {
  return { get: () => el.volume, set: (v) => { el.volume = clamp01(v); } };
}

// Tell page.js (main world) to duck/restore a Web-Audio player. Signaling goes through a
// shared DOM attribute — the one channel guaranteed to cross the isolated/main world boundary
// in both Chrome and Firefox. No-op on non-player sites (page.js finds no player).
function bridge(on) {
  if (actionForHost() !== "duck") return; // pause-sites are handled by pausing the element
  try {
    const payload = JSON.stringify({
      cmd: on ? "duck" : "unduck",
      target: clamp01(settings.duckVolume ?? 0.2),
      ramp: on ? 0 : Number(settings.rampMs ?? 300), // instant cut down, fade back in on restore
      t: Date.now(), // changes every time so the observer always fires
    });
    document.documentElement.setAttribute("data-gdd-bridge", payload);
    console.log("[gdd] bridge ->", on ? "duck" : "unduck");
  } catch (e) {}
}

function rampVolume(el, ctrl, target, ms, onDone) {
  if (el.__gdd_ramp) { clearInterval(el.__gdd_ramp); el.__gdd_ramp = null; }
  target = clamp01(target);
  const start = ctrl.get();
  if (ms <= 0 || Math.abs(start - target) < 0.005) { ctrl.set(target); if (onDone) onDone(); return; }
  const stepMs = 25;
  const steps = Math.max(1, Math.round(ms / stepMs));
  let i = 0;
  el.__gdd_ramp = setInterval(() => {
    i++;
    ctrl.set(start + (target - start) * (i / steps));
    if (i >= steps) { clearInterval(el.__gdd_ramp); el.__gdd_ramp = null; ctrl.set(target); if (onDone) onDone(); }
  }, stepMs);
}

function applyToEl(el) {
  if (el.paused || el.ended) return;
  if (actionForHost() === "pause") {
    el.__gdd_pausedByUs = true;
    el.pause();
  } else {
    if (el.__gdd_duckedByUs) return;
    const ctrl = volController(el);
    const target = clamp01(settings.duckVolume ?? 0.2);
    el.__gdd_ctrl = ctrl;
    el.__gdd_prevVol = ctrl.get();
    el.__gdd_duckedByUs = true;
    el.__gdd_duckTarget = target;
    rampVolume(el, ctrl, target, 0); // instant cut; the fade happens on restore (releaseEl)
  }
}

function releaseEl(el) {
  if (el.__gdd_pausedByUs) {
    el.__gdd_pausedByUs = false;
    el.play().catch(() => {});
  }
  if (el.__gdd_duckedByUs) {
    const ctrl = el.__gdd_ctrl || volController(el);
    const target = typeof el.__gdd_prevVol === "number" ? el.__gdd_prevVol : 1;
    el.__gdd_duckedByUs = false;
    el.__gdd_duckTarget = undefined;
    rampVolume(el, ctrl, target, Number(settings.rampMs ?? 300));
    el.__gdd_ctrl = undefined;
  }
}

const applyAll = () => media().forEach(applyToEl);
const releaseAll = () => media().forEach(releaseEl);

function onPlay(e) {
  const el = e.target;
  if (!ducking) return;
  if (el.__gdd_duckedByUs && el.__gdd_ctrl && typeof el.__gdd_duckTarget === "number") {
    el.__gdd_ctrl.set(el.__gdd_duckTarget); // re-assert on (re)play, e.g. a track change
  } else {
    applyToEl(el);
  }
}

// Some players reset the volume back to their own value, undoing our duck. Re-assert the
// ducked target when that happens (once our ramp is done).
function onVolumeChange(e) {
  const el = e.target;
  if (el.__gdd_duckedByUs && !el.__gdd_ramp && el.__gdd_ctrl &&
      typeof el.__gdd_duckTarget === "number" && el.__gdd_ctrl.get() > el.__gdd_duckTarget + 0.02) {
    el.__gdd_ctrl.set(el.__gdd_duckTarget);
  }
}

function track(el) {
  if (el.__gdd_tracked) return;
  el.__gdd_tracked = true;
  el.addEventListener("play", onPlay);
  el.addEventListener("loadstart", onPlay); // src swap (SPA navigation)
  el.addEventListener("volumechange", onVolumeChange);
}
const trackAll = () => media().forEach(track);

const observer = new MutationObserver((muts) => {
  for (const m of muts) {
    for (const node of m.addedNodes) {
      if (!(node instanceof Element)) continue;
      const els = [];
      if (node.matches && node.matches("video, audio")) els.push(node);
      if (node.querySelectorAll) els.push(...node.querySelectorAll("video, audio"));
      for (const el of els) { track(el); if (ducking) applyToEl(el); }
    }
  }
});
observer.observe(document.documentElement || document, { childList: true, subtree: true });

// Spotify web has NO <audio>/<video> element (Web Audio + EME), so we control it through its
// own UI. The "duck" action drives Spotify's volume <input type=range> (read/ramp/restore);
// the "pause" action clicks the transport play/pause button. The per-site rule picks which.
let spotifyPausedByUs = false;
let spotifyPrevVol = null; // saved volume fraction (0..1) while ducked

function spotifyVolInput() {
  const vb = document.querySelector('[data-testid="volume-bar"]');
  return vb ? (vb.querySelector('input[type="range"]') || vb.querySelector("input")) : null;
}
function spotifyGetVol(input) {
  const min = parseFloat(input.min) || 0, max = (parseFloat(input.max) || 100); // unset max => range default 100
  return clamp01((parseFloat(input.value) - min) / ((max - min) || 1));
}
// Set a React-controlled range input so Spotify reacts: native value setter + 'input' event
// (live volume). 'change' is fired separately, once, to persist — see commit() below.
function spotifySetVol(input, frac) {
  const min = parseFloat(input.min) || 0, max = (parseFloat(input.max) || 100); // unset max => range default 100
  const desc = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value");
  desc.set.call(input, String(min + (max - min) * clamp01(frac)));
  input.dispatchEvent(new Event("input", { bubbles: true }));
}

function spotifyDuck(on) {
  if (location.hostname !== "open.spotify.com") return;

  if (actionForHost() === "pause") {
    const btn = document.querySelector('[data-testid="control-button-playpause"]');
    if (!btn) return;
    const label = (btn.getAttribute("aria-label") || "").trim().toLowerCase();
    if (on) { if (label === "pause") { btn.click(); spotifyPausedByUs = true; } } // pause if playing
    else { if (spotifyPausedByUs && label === "play") btn.click(); spotifyPausedByUs = false; }
    return;
  }

  // "duck": reduce Spotify's own volume slider, then restore it.
  const input = spotifyVolInput();
  if (!input) { console.log("[gdd] spotify: no volume input found"); return; }
  const ctrl = { get: () => spotifyGetVol(input), set: (f) => spotifySetVol(input, f) };
  const commit = () => input.dispatchEvent(new Event("change", { bubbles: true })); // persist at ramp end
  const ramp = Number(settings.rampMs ?? 300);
  if (on) {
    if (spotifyPrevVol === null) spotifyPrevVol = spotifyGetVol(input);
    rampVolume(input, ctrl, clamp01(settings.duckVolume ?? 0.2), 0, commit); // instant cut
  } else if (spotifyPrevVol !== null) {
    rampVolume(input, ctrl, spotifyPrevVol, ramp, commit); // fade back in
    spotifyPrevVol = null;
  }
}

// Amazon Music web (music.amazon.com), like Spotify, plays through Web Audio — its only media
// element is an empty <video> whose .volume is ignored. We drive its own UI instead: "duck"
// ramps the Volume-flyout slider, "pause" clicks the transport button. Controls live in shadow
// DOM, so pierce open shadow roots (deepFind).
function deepFind(test, root = document) {
  let nodes;
  try { nodes = root.querySelectorAll("*"); } catch { return null; }
  for (const el of nodes) {
    try { if (test(el)) return el; } catch {}
    if (el.shadowRoot) { const r = deepFind(test, el.shadowRoot); if (r) return r; }
  }
  return null;
}
function amazonBtn(labelRe) {
  return deepFind((e) => e.tagName === "BUTTON" && labelRe.test(e.getAttribute("aria-label") || ""));
}
// Amazon's real volume control is an <input aria-label="Volume Level"> on a 0..1 scale, but it
// only mounts when the "Volume" flyout is open — so deploy it on demand, then ramp it like
// Spotify (React-controlled: native value setter + 'input', with a 'change' to persist).
function amazonVolInput() {
  return deepFind((e) => e.tagName === "INPUT" && /volume/i.test(e.getAttribute("aria-label") || ""));
}
function amazonWithSlider(cb) {
  const found = amazonVolInput();
  if (found) { cb(found); return; } // already mounted from a previous open / user interaction
  const volBtn = amazonBtn(/^volume$/i);
  if (!volBtn) { cb(null); return; }
  volBtn.click(); // open the flyout so the slider mounts
  setTimeout(() => cb(amazonVolInput()), 350);
}
function amazonGetFrac(input) {
  const min = parseFloat(input.min) || 0, max = (parseFloat(input.max) || 1);
  return clamp01((parseFloat(input.value) - min) / ((max - min) || 1));
}
function amazonSetFrac(input, frac) {
  const min = parseFloat(input.min) || 0, max = (parseFloat(input.max) || 1);
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value").set
    .call(input, String(min + (max - min) * clamp01(frac)));
  input.dispatchEvent(new Event("input", { bubbles: true }));
}

let amazonPrevVol = null;   // saved fraction (0..1) while ducked
let amazonPausedByUs = false;

function amazonDuck(on) {
  amazonWithSlider((input) => {
    if (!input) return;
    const ctrl = { get: () => amazonGetFrac(input), set: (f) => amazonSetFrac(input, f) };
    const commit = () => input.dispatchEvent(new Event("change", { bubbles: true })); // persist at ramp end
    const ramp = Number(settings.rampMs ?? 300);
    if (on) {
      if (amazonPrevVol === null) amazonPrevVol = ctrl.get();
      rampVolume(input, ctrl, clamp01(settings.duckVolume ?? 0.2), 0, commit); // instant cut
    } else if (amazonPrevVol !== null) {
      rampVolume(input, ctrl, amazonPrevVol, ramp, commit); // fade back in
      amazonPrevVol = null;
    }
  });
}
function amazonPause(on) {
  if (on) {
    const b = amazonBtn(/^pause$/i); // present only while actually playing
    if (b) { b.click(); amazonPausedByUs = true; }
  } else if (amazonPausedByUs) {
    const b = amazonBtn(/^play$/i); // present only while paused
    if (b) b.click();
    amazonPausedByUs = false;
  }
}
function amazonControl(on) {
  if (location.hostname !== "music.amazon.com") return;
  if (actionForHost() === "pause") amazonPause(on);
  else amazonDuck(on);
}

// Bandcamp (bandcamp.com): unpurchased streams have NO UI volume control, and audio is wired
// through Web Audio (a gain node), so the element's `.volume` is ignored — ducking can't work,
// so Bandcamp is pause-only. Bandcamp does expose a real <audio> element on most pages, in which
// case the generic pause path (el.pause()/el.play()) already handles it. This bespoke handler is
// a FALLBACK for pages where no media element is reachable (pure Web Audio): it clicks Bandcamp's
// own play/pause transport button. The media()-length guard keeps the two paths from fighting.
let bandcampPausedByUs = false;
function bandcampPlayBtn() {
  return document.querySelector(".inline_player .playbutton")
      || document.querySelector("#player .playbutton")
      || document.querySelector(".playbutton")
      || document.querySelector('[aria-label="Play"], [aria-label="Pause"]');
}
function bandcampPlaying(btn) {
  // Classic Bandcamp toggles a `playing` class on .playbutton; newer controls flip the aria-label.
  return btn.classList.contains("playing") || /pause/i.test(btn.getAttribute("aria-label") || "");
}
function bandcampControl(on) {
  if (!/(^|\.)bandcamp\.com$/.test(location.hostname)) return;
  if (media().length > 0) return; // a real <audio>/<video> exists — generic pause path owns it
  const btn = bandcampPlayBtn();
  if (!btn) { console.log("[gdd] bandcamp: no media element and no play/pause button found"); return; }
  if (on) {
    if (bandcampPlaying(btn)) { btn.click(); bandcampPausedByUs = true; }
  } else if (bandcampPausedByUs) {
    if (!bandcampPlaying(btn)) btn.click();
    bandcampPausedByUs = false;
  }
}

// SoundCloud (soundcloud.com), like Spotify, is pure Web Audio — there's NO media element at all,
// and its volume is a custom VERTICAL div-slider tucked behind the speaker icon (not a native
// <input> like Amazon's). We drive it with synthetic mouse events: "duck" reveals the slider,
// reads/saves the current level, then clicks the track at the duck target (instant both directions
// — a smooth fade would need the flyout held open the whole time); "pause" clicks the transport
// button. Verified: dispatching mousedown/up on .volume__sliderBackground at a computed clientY
// moves SoundCloud's real output volume (progress fill height / track height == the level).
let soundcloudPrevVol = null;   // saved fraction (0..1) while ducked
let soundcloudPausedByUs = false;

function soundcloudReveal(vol) {
  // The slider is collapsed via a class + hover state; nudge both so it lays out and stays open.
  vol.classList.remove("volume__hideVolume");
  ["mouseover", "mouseenter"].forEach((t) => vol.dispatchEvent(new MouseEvent(t, { bubbles: true })));
}
function soundcloudCollapse(vol) {
  ["mouseleave", "mouseout"].forEach((t) => vol.dispatchEvent(new MouseEvent(t, { bubbles: true })));
}
function soundcloudSetFrac(bg, frac) {
  const r = bg.getBoundingClientRect();
  const x = r.left + r.width / 2, y = r.bottom - r.height * clamp01(frac); // vertical: bottom=0, top=1
  const mk = (t) => new MouseEvent(t, { bubbles: true, cancelable: true, clientX: x, clientY: y, view: window });
  bg.dispatchEvent(mk("mousedown"));
  bg.dispatchEvent(mk("mousemove"));
  bg.dispatchEvent(mk("mouseup"));
  document.dispatchEvent(mk("mouseup"));
}
function soundcloudDuck(on) {
  const vol = document.querySelector(".volume");
  if (!vol) return;
  soundcloudReveal(vol);
  // The flyout renders a tick after hover, so measure/click after a short delay (imperceptible).
  setTimeout(() => {
    const bg = vol.querySelector(".volume__sliderBackground") || vol.querySelector(".volume__sliderWrapper");
    const prog = vol.querySelector(".volume__sliderProgress");
    const r = bg && bg.getBoundingClientRect();
    if (r && r.height) {
      if (on) {
        if (soundcloudPrevVol === null)
          soundcloudPrevVol = prog ? clamp01(prog.getBoundingClientRect().height / r.height) : 1;
        soundcloudSetFrac(bg, clamp01(settings.duckVolume ?? 0.2)); // instant cut
      } else if (soundcloudPrevVol !== null) {
        soundcloudSetFrac(bg, soundcloudPrevVol);                   // instant restore
        soundcloudPrevVol = null;
      }
    }
    soundcloudCollapse(vol);
  }, 80);
}
function soundcloudPause(on) {
  const btn = document.querySelector(".playControls__play, .playControl");
  if (!btn) return;
  const playing = btn.classList.contains("playing"); // SoundCloud flags the active state with .playing
  if (on) { if (playing) { btn.click(); soundcloudPausedByUs = true; } }
  else if (soundcloudPausedByUs) { if (!playing) btn.click(); soundcloudPausedByUs = false; }
}
function soundcloudControl(on) {
  if (location.hostname !== "soundcloud.com") return;
  if (actionForHost() === "pause") soundcloudPause(on);
  else soundcloudDuck(on);
}

api.runtime.onMessage.addListener((msg) => {
  if (msg && msg.cmd === "duck") {
    ducking = !!msg.on;
    trackAll();
    if (ducking) applyAll(); else releaseAll();
    bridge(ducking);
    spotifyDuck(ducking);
    amazonControl(ducking);
    bandcampControl(ducking);
    soundcloudControl(ducking);
  }
});

// On init, sync with current engine state (covers tabs opened mid-dialog).
trackAll();
Promise.resolve(api.runtime.sendMessage({ cmd: "getState" }))
  .then((r) => { if (r && r.ducking) { ducking = true; trackAll(); applyAll(); bridge(true); spotifyDuck(true); amazonControl(true); bandcampControl(true); soundcloudControl(true); } })
  .catch(() => {});
