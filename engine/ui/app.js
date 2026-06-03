"use strict";
// Bridge to the C# host over WebView2 postMessage. Outside the app (browser preview) it falls
// back to mock data so the UI still renders.
const inApp = !!(window.chrome && window.chrome.webview);
const _pending = new Map();
let _seq = 0;

function call(cmd, args) {
  if (!inApp) return Promise.resolve(mock(cmd, args));
  return new Promise((resolve) => {
    const id = ++_seq;
    _pending.set(id, resolve);
    window.chrome.webview.postMessage(JSON.stringify({ id, cmd, args: args || null }));
  });
}
if (inApp) {
  window.chrome.webview.addEventListener("message", (e) => {
    const m = e.data;
    if (m && m.id && _pending.has(m.id)) { _pending.get(m.id)(m.result); _pending.delete(m.id); }
    else if (m && m.event === "state") { applyState(m.data); }
  });
}
function mock(cmd) {
  const sources = [
    { name: "swtor", capturing: true, active: false, auto: true },
    { name: "Discord", capturing: true, active: true, auto: false },
  ];
  if (cmd === "snapshot") return {
    enabled: true, autoDetect: true, ducking: false, status: "Listening — swtor, Discord",
    sources,
    appVersion: "1.0.0", updateStaged: false, updateVersion: "", launchOnStartup: false,
    settings: { threshold: 0.35, minSpeechMs: 1, endBufferMs: 2000 },
  };
  if (cmd === "games") return {
    autoDetect: true, monitored: sources,
    audioApps: [{ pid: 1, name: "firefox" }, { pid: 2, name: "spotify" }],
    library: [
      { process: "swtor", title: "Star Wars: The Old Republic", monitored: true },
      { process: "cs2", title: "Counter-Strike 2", monitored: false },
      { process: "eldenring", title: "Elden Ring", monitored: false },
    ],
  };
  if (cmd === "apps") return {
    defaultAction: "duck", duckVolume: 0.2, rampMs: 300,
    playing: ["firefox", "spotify"],
    apps: [
      { name: "spotify", action: "duck" },
      { name: "vlc", action: "pause" },
      { name: "Discord", action: "ignore" },
    ],
  };
  return {};
}

const $ = (id) => document.getElementById(id);
const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));

// ---- navigation ----
document.querySelectorAll(".nav-item").forEach((b) =>
  b.addEventListener("click", () => {
    document.querySelectorAll(".nav-item").forEach((x) => x.classList.remove("active"));
    document.querySelectorAll(".view").forEach((x) => x.classList.remove("active"));
    b.classList.add("active");
    $("view-" + b.dataset.view).classList.add("active");
    if (b.dataset.view === "games") loadGames();
    if (b.dataset.view === "apps") loadApps();
  })
);

// ---- shared source rendering ----
function renderSources(el, sources, autoDetect, withRemove) {
  if (sources && sources.length) {
    el.innerHTML = sources.map((x) => {
      const cls = x.active ? "duck" : (x.capturing ? "cap" : "");
      const state = x.active ? "speaking" : (x.capturing ? "listening" : "waiting");
      const rm = withRemove && !x.auto ? `<button class="src-rm" data-name="${esc(x.name)}" title="Remove">✕</button>` : "";
      return `<div class="source"><span class="dot ${cls}"></span><span class="nm">${esc(x.name)}</span>` +
             `<span class="tag">${x.auto ? "auto" : "manual"} · ${state}</span>${rm}</div>`;
    }).join("");
    if (withRemove) el.querySelectorAll(".src-rm").forEach((b) =>
      b.addEventListener("click", () => call("removeSource", { name: b.dataset.name }).then(loadGames)));
  } else {
    el.innerHTML = `<div class="empty">Nothing monitored yet${autoDetect ? " — launch a game and it'll appear here." : "."}</div>`;
  }
}

// ---- live state ----
function applyState(s) {
  if (!s) return;
  const banner = $("statusBanner");
  banner.classList.remove("ducking", "listening");
  const monitoring = s.sources && s.sources.length;
  if (!s.enabled) { $("statusTitle").textContent = "Disabled"; $("statusSub").textContent = "Monitoring is off."; }
  else if (s.ducking) { banner.classList.add("ducking"); $("statusTitle").textContent = "Ducking"; $("statusSub").textContent = "Dialog detected — your media is lowered/paused."; }
  else if (monitoring) { banner.classList.add("listening"); $("statusTitle").textContent = "Listening"; $("statusSub").textContent = s.status || "Monitoring for dialog."; }
  else { $("statusTitle").textContent = "Idle"; $("statusSub").textContent = s.autoDetect ? "Watching for games…" : "No sources selected."; }

  $("footDot").style.background = !s.enabled ? "var(--muted2)" : (s.ducking ? "var(--good)" : (monitoring ? "var(--accent)" : "var(--muted2)"));
  $("footState").textContent = !s.enabled ? "Disabled" : (s.ducking ? "Ducking" : (monitoring ? "Listening" : "Idle"));

  $("tglEnabled").checked = !!s.enabled;
  $("tglAuto").checked = !!s.autoDetect;
  $("gAuto").checked = !!s.autoDetect;

  $("tglStartup").checked = !!s.launchOnStartup;
  $("appVer").textContent = "Version " + (s.appVersion || "—");
  const uw = $("updWrap");
  if (s.updateStaged) { uw.style.display = ""; $("updTag").textContent = "Update ready" + (s.updateVersion ? " (v" + s.updateVersion + ")" : ""); }
  else uw.style.display = "none";

  renderSources($("sourcesList"), s.sources, s.autoDetect, false);
  renderSources($("gMonitored"), s.sources, s.autoDetect, true);
}

// ---- settings ----
function bindRange(id, valId, value, fmt) {
  const el = $(id), out = $(valId);
  el.value = value;
  const update = () => (out.textContent = fmt(parseFloat(el.value)));
  el.oninput = update; update();
}
function fillSettings(st) {
  bindRange("setThreshold", "vThreshold", st.threshold, (v) => v.toFixed(2));
  bindRange("setAttack", "vAttack", st.minSpeechMs, (v) => v + " ms");
  bindRange("setEnd", "vEnd", st.endBufferMs, (v) => v + " ms");
}
$("btnSaveSettings").addEventListener("click", async () => {
  await call("saveSettings", {
    threshold: parseFloat($("setThreshold").value),
    minSpeechMs: parseInt($("setAttack").value, 10),
    endBufferMs: parseInt($("setEnd").value, 10),
  });
  const m = $("savedMsg"); m.classList.add("show"); setTimeout(() => m.classList.remove("show"), 1500);
});

// ---- dashboard toggles ----
$("tglEnabled").addEventListener("change", (e) => call("setEnabled", { on: e.target.checked }));
$("tglAuto").addEventListener("change", (e) => call("setAutoDetect", { on: e.target.checked }));

// ---- general (startup + update) ----
$("tglStartup").addEventListener("change", (e) => call("setStartup", { on: e.target.checked }));
$("btnRestart").addEventListener("click", () => call("restartToUpdate"));

// ---- games screen ----
let GAMES_LIB = [];
async function loadGames() {
  const g = await call("games");
  $("gAuto").checked = !!g.autoDetect;
  renderSources($("gMonitored"), g.monitored, g.autoDetect, true);
  renderAudioApps(g.audioApps, g.monitored);
  GAMES_LIB = g.library || [];
  $("gLibCount").textContent = "(" + GAMES_LIB.length + ")";
  renderLibrary($("gSearch").value);
}
function renderAudioApps(apps, monitored) {
  const mon = new Set((monitored || []).map((m) => m.name.toLowerCase()));
  const avail = (apps || []).filter((a) => !mon.has(a.name.toLowerCase()));
  const el = $("gApps");
  if (avail.length) {
    el.innerHTML = avail.map((a) => `<button class="chip" data-pid="${a.pid}" data-name="${esc(a.name)}">+ ${esc(a.name)}</button>`).join("");
    el.querySelectorAll(".chip").forEach((b) =>
      b.addEventListener("click", () => call("addSource", { pid: parseInt(b.dataset.pid, 10), name: b.dataset.name }).then(loadGames)));
  } else {
    el.innerHTML = `<div class="empty">No other apps playing audio right now.</div>`;
  }
}
function renderLibrary(q) {
  q = (q || "").toLowerCase().trim();
  const items = GAMES_LIB.filter((g) => !q || g.title.toLowerCase().includes(q) || g.process.toLowerCase().includes(q));
  $("gLibList").innerHTML = items.length
    ? items.slice(0, 400).map((g) => `<div class="libitem"><span class="lt">${esc(g.title)}</span>` +
        `<span class="lp">${esc(g.process)}</span>${g.monitored ? '<span class="badge">monitored</span>' : ""}</div>`).join("")
    : `<div class="empty" style="padding:14px">No matches.</div>`;
}
$("gAuto").addEventListener("change", (e) => call("setAutoDetect", { on: e.target.checked }).then(loadGames));
$("gAddBtn").addEventListener("click", () => {
  const v = $("gAddName").value.trim();
  if (v) call("addSource", { name: v }).then(() => { $("gAddName").value = ""; loadGames(); });
});
$("gAddName").addEventListener("keydown", (e) => { if (e.key === "Enter") $("gAddBtn").click(); });
$("gSearch").addEventListener("input", () => renderLibrary($("gSearch").value));

// ---- apps screen ----
function setSeg(container, action) {
  container.querySelectorAll(".seg-btn").forEach((b) => b.classList.toggle("active", b.dataset.action === action));
}
async function loadApps() {
  const w = await call("apps");
  setSeg($("aDefault"), w.defaultAction || "duck");
  bindRange("aDuck", "vDuck", Math.round((w.duckVolume ?? 0.2) * 100), (v) => v + "%");
  bindRange("aRamp", "vRamp", w.rampMs ?? 300, (v) => (v === 0 ? "instant" : v + " ms"));
  renderPlaying(w.playing || []);
  renderRules(w.apps || []);
}
function renderPlaying(playing) {
  const el = $("aPlaying");
  if (playing && playing.length) {
    el.innerHTML = playing.map((n) => `<button class="chip" data-name="${esc(n)}">+ ${esc(n)}</button>`).join("");
    el.querySelectorAll(".chip").forEach((b) =>
      b.addEventListener("click", () => call("setApp", { name: b.dataset.name, action: "duck" }).then(loadApps)));
  } else {
    el.innerHTML = `<div class="empty">No other apps playing audio right now.</div>`;
  }
}
const ruleSeg = (act, cur) =>
  `<button class="seg-btn${cur === act ? " active" : ""}" data-action="${act}">${act === "duck" ? "Duck" : act === "pause" ? "Pause" : "Ignore"}</button>`;
function renderRules(apps) {
  const el = $("aRules");
  if (!apps.length) { el.innerHTML = `<div class="empty">No per-app rules — the default applies to everything.</div>`; return; }
  el.innerHTML = apps.map((a) =>
    `<div class="siterow"><span class="host">${esc(a.name)}</span>` +
    `<div class="seg sm" data-name="${esc(a.name)}">${ruleSeg("duck", a.action)}${ruleSeg("pause", a.action)}${ruleSeg("ignore", a.action)}</div>` +
    `<button class="src-rm" data-name="${esc(a.name)}" title="Remove">✕</button></div>`).join("");
  el.querySelectorAll(".seg.sm .seg-btn").forEach((b) =>
    b.addEventListener("click", () => call("setApp", { name: b.parentElement.dataset.name, action: b.dataset.action }).then(loadApps)));
  el.querySelectorAll(".src-rm").forEach((b) =>
    b.addEventListener("click", () => call("removeApp", { name: b.dataset.name }).then(loadApps)));
}
$("aDefault").querySelectorAll(".seg-btn").forEach((b) =>
  b.addEventListener("click", () => { setSeg($("aDefault"), b.dataset.action); call("setDefaultAction", { action: b.dataset.action }); }));
$("aDuck").addEventListener("change", () => call("setDuckVolume", { value: parseInt($("aDuck").value, 10) / 100 }));
$("aRamp").addEventListener("change", () => call("setRampMs", { value: parseInt($("aRamp").value, 10) }));
$("aAddBtn").addEventListener("click", () => {
  const v = $("aAddName").value.trim().replace(/\.exe$/i, "");
  if (v) call("setApp", { name: v, action: "duck" }).then(() => { $("aAddName").value = ""; loadApps(); });
});
$("aAddName").addEventListener("keydown", (e) => { if (e.key === "Enter") $("aAddBtn").click(); });

// ---- init ----
(async () => {
  const snap = await call("snapshot");
  applyState(snap);
  if (snap.settings) fillSettings(snap.settings);
})();
