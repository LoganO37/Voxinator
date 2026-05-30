// Runs in the PAGE's main world (manifest world:"MAIN"), so it can reach the page's own
// player API — YouTube / YouTube Music expose setVolume/getVolume (0..100) on the player
// element and route audio through Web Audio, so the media element's `.volume` does nothing.
// The isolated content script can't see those page methods in Chrome, so it signals us via a
// shared DOM attribute (data-gdd-bridge). No-op on pages without such a player.
(() => {
  "use strict";
  try { document.documentElement.setAttribute("data-gdd-page", "1"); } catch (e) {}
  console.log("[gdd-page] main-world helper loaded");

  let ducked = false;
  let target = 0.2;
  let rampMs = 300;
  let saved = null;
  let timer = null;
  let lastT = 0;

  const clamp01 = (x) => Math.max(0, Math.min(1, Number(x)));

  function findPlayer() {
    const v = document.querySelector("video");
    for (let n = v; n; n = n.parentElement) {
      if (n && typeof n.setVolume === "function" && typeof n.getVolume === "function") return n;
    }
    const c = document.querySelector(".html5-video-player, #movie_player");
    return c && typeof c.setVolume === "function" ? c : null;
  }

  function ramp(p, to) {
    if (timer) { clearInterval(timer); timer = null; }
    to = clamp01(to);
    let from;
    try { from = clamp01(p.getVolume() / 100); } catch (e) { from = to; }
    if (rampMs <= 0 || Math.abs(from - to) < 0.005) {
      try { p.setVolume(Math.round(to * 100)); } catch (e) {}
      return;
    }
    const steps = Math.max(1, Math.round(rampMs / 25));
    let i = 0;
    timer = setInterval(() => {
      i++;
      try { p.setVolume(Math.round(clamp01(from + (to - from) * (i / steps)) * 100)); } catch (e) {}
      if (i >= steps) { clearInterval(timer); timer = null; }
    }, 25);
  }

  function applyDuck() {
    const p = findPlayer();
    console.log("[gdd-page] duck ->", target, "player found:", !!p);
    if (!p) return;
    if (saved === null) { try { saved = clamp01(p.getVolume() / 100); } catch (e) { saved = 1; } }
    ramp(p, target);
  }

  function release() {
    const p = findPlayer();
    console.log("[gdd-page] unduck, player found:", !!p, "saved:", saved);
    if (p && saved !== null) ramp(p, saved);
    saved = null;
  }

  function read() {
    const raw = document.documentElement.getAttribute("data-gdd-bridge");
    if (!raw) return;
    let d;
    try { d = JSON.parse(raw); } catch (e) { return; }
    if (!d || d.t === lastT) return;
    lastT = d.t;
    rampMs = Number(d.ramp) || 0;
    if (d.cmd === "duck") { ducked = true; target = clamp01(d.target); applyDuck(); }
    else if (d.cmd === "unduck") { ducked = false; release(); }
  }

  new MutationObserver(read).observe(document.documentElement, {
    attributes: true,
    attributeFilter: ["data-gdd-bridge"],
  });
  read(); // in case the attribute was already set before we loaded

  // Re-assert when a new track starts while ducked (YouTube Music reuses the element).
  document.addEventListener("play", (e) => {
    if (ducked && e.target && e.target.tagName === "VIDEO") setTimeout(applyDuck, 120);
  }, true);
})();
