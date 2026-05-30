// Cross-browser content script (Chrome/Chromium + Firefox). Pauses or ducks media while
// game/app dialog is active.
//   - "pause" -> instant hard cut (good for audiobooks / spoken-word).
//   - "duck"  -> smooth volume fade over rampMs (good for music); rampMs=0 is a hard cut.
//
// Robust to real sites: remembers the current duck state and re-applies it to media added or
// started mid-dialog (YouTube SPA navigation, autoplay-next), and syncs state on init so a
// tab opened/reloaded during dialog still ducks. Only ever restores media that WE changed.
const api = (typeof browser !== "undefined") ? browser : chrome;

const SETTINGS_DEFAULTS = {
  defaultAction: "duck",
  duckVolume: 0.2,
  rampMs: 300,
  perSite: "youtube.com = pause\nyoutu.be = pause\nmusic.youtube.com = duck",
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
  return best || settings.defaultAction || "duck";
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
      ramp: Number(settings.rampMs ?? 300),
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
    rampVolume(el, ctrl, target, Number(settings.rampMs ?? 300));
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

api.runtime.onMessage.addListener((msg) => {
  if (msg && msg.cmd === "duck") {
    ducking = !!msg.on;
    trackAll();
    if (ducking) applyAll(); else releaseAll();
    bridge(ducking);
  }
});

// On init, sync with current engine state (covers tabs opened mid-dialog).
trackAll();
Promise.resolve(api.runtime.sendMessage({ cmd: "getState" }))
  .then((r) => { if (r && r.ducking) { ducking = true; trackAll(); applyAll(); bridge(true); } })
  .catch(() => {});
