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
    { name: "Discord", capturing: true, active: true, auto: true },
  ];
  if (cmd === "snapshot") return {
    enabled: true, autoDetect: true, voiceChat: true, ducking: false, status: "Listening — swtor, Discord",
    sources, appVersion: "1.0.3", updateStaged: false, updateVersion: "", launchOnStartup: false,
    defaultAction: "duck", duckVolume: 0.2, rampMs: 300,
    settings: { threshold: 0.35, minSpeechMs: 1, endBufferMs: 2000 },
  };
  if (cmd === "sources") return {
    running: [{ name: "firefox", title: "firefox" }, { name: "spotify", title: "spotify" }, { name: "Discord", title: "Discord" }],
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
    if (b.dataset.view === "settings") loadSettings();
  })
);

// ---- helpers ----
function setSeg(container, action) {
  container.querySelectorAll(".seg-btn").forEach((b) => b.classList.toggle("active", b.dataset.action === action));
}
// Set a range's value + label without clobbering it while the user is dragging.
function setRange(id, valId, value, fmt) {
  const el = $(id), out = $(valId);
  if (document.activeElement !== el) el.value = value;
  out.textContent = fmt(parseFloat(el.value));
}
// Bind a range's live label (used for detection sliders that only apply on Save).
function bindRange(id, valId, value, fmt) {
  const el = $(id), out = $(valId);
  el.value = value;
  const update = () => (out.textContent = fmt(parseFloat(el.value)));
  el.oninput = update; update();
}

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
      b.addEventListener("click", () => call("removeSource", { name: b.dataset.name }).then(loadSources)));
  } else {
    el.innerHTML = `<div class="empty">Nothing monitored yet${autoDetect ? " — launch a game or join a call and it'll appear here." : "."}</div>`;
  }
}

// ---- live state (dashboard + settings toggles) ----
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
  $("sAuto").checked = !!s.autoDetect;
  $("sVoice").checked = !!s.voiceChat;
  $("tglStartup").checked = !!s.launchOnStartup;

  // dashboard quick ducking controls
  if (s.defaultAction) setSeg($("dDefault"), s.defaultAction);
  if (typeof s.duckVolume === "number") setRange("dDuck", "vDDuck", Math.round(s.duckVolume * 100), (v) => v + "%");

  $("appVer").textContent = "Version " + (s.appVersion || "—");
  const uw = $("updWrap");
  if (s.updateStaged) { uw.style.display = ""; $("updTag").textContent = "Update ready" + (s.updateVersion ? " (v" + s.updateVersion + ")" : ""); }
  else uw.style.display = "none";

  renderSources($("sourcesList"), s.sources, s.autoDetect, true);
}

// ---- dashboard ducking controls ----
$("tglEnabled").addEventListener("change", (e) => call("setEnabled", { on: e.target.checked }));
$("dDefault").querySelectorAll(".seg-btn").forEach((b) =>
  b.addEventListener("click", () => { setSeg($("dDefault"), b.dataset.action); call("setDefaultAction", { action: b.dataset.action }); }));
$("dDuck").addEventListener("input", () => ($("vDDuck").textContent = $("dDuck").value + "%"));
$("dDuck").addEventListener("change", () => call("setDuckVolume", { value: parseInt($("dDuck").value, 10) / 100 }));

// ---- settings: sources ----
async function loadSettings() { await Promise.all([loadSources(), loadApps()]); }

async function loadSources() {
  const r = await call("sources");
  renderRunning((r && r.running) || []);
}
function renderRunning(running) {
  const el = $("sRunning");
  if (running && running.length) {
    el.innerHTML = running.map((a) => `<button class="chip" data-name="${esc(a.name)}" title="${esc(a.name)}">+ ${esc(a.title || a.name)}</button>`).join("");
    el.querySelectorAll(".chip").forEach((b) =>
      b.addEventListener("click", () => call("addSource", { name: b.dataset.name }).then((r) => { renderRunning((r && r.running) || []); })));
  } else {
    el.innerHTML = `<div class="empty">Nothing running to add right now.</div>`;
  }
}
$("sAuto").addEventListener("change", (e) => call("setAutoDetect", { on: e.target.checked }).then(loadSources));
$("sVoice").addEventListener("change", (e) => call("setVoiceChat", { on: e.target.checked }).then(loadSources));
$("sAddBtn").addEventListener("click", () => {
  const v = $("sAddName").value.trim().replace(/\.exe$/i, "");
  if (v) call("addSource", { name: v }).then(() => { $("sAddName").value = ""; loadSources(); });
});
$("sAddName").addEventListener("keydown", (e) => { if (e.key === "Enter") $("sAddBtn").click(); });

// ---- settings: detection ----
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

// ---- settings: ducking behavior (fade + per-app rules) ----
async function loadApps() {
  const w = await call("apps");
  setRange("sRamp", "vRamp", w.rampMs ?? 300, (v) => (v === 0 ? "instant" : v + " ms"));
  renderPlaying((w && w.playing) || []);
  renderRules((w && w.apps) || []);
}
function renderPlaying(playing) {
  const el = $("rulePlaying");
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
  const el = $("ruleList");
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
$("sRamp").addEventListener("input", () => ($("vRamp").textContent = $("sRamp").value === "0" ? "instant" : $("sRamp").value + " ms"));
$("sRamp").addEventListener("change", () => call("setRampMs", { value: parseInt($("sRamp").value, 10) }));
$("ruleAddBtn").addEventListener("click", () => {
  const v = $("ruleAddName").value.trim().replace(/\.exe$/i, "");
  if (v) call("setApp", { name: v, action: "duck" }).then(() => { $("ruleAddName").value = ""; loadApps(); });
});
$("ruleAddName").addEventListener("keydown", (e) => { if (e.key === "Enter") $("ruleAddBtn").click(); });

// ---- settings: startup + update ----
$("tglStartup").addEventListener("change", (e) => call("setStartup", { on: e.target.checked }));
$("btnRestart").addEventListener("click", () => call("restartToUpdate"));

// ---- init ----
(async () => {
  const snap = await call("snapshot");
  applyState(snap);
  if (snap.settings) fillSettings(snap.settings);
})();
