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
    else if (m && m.event === "extUpdate") { showExtUpdate(m.data); }
  });
}
function mock(cmd) {
  const sources = [
    { name: "swtor", capturing: true, active: false, auto: true },
    { name: "Discord", capturing: true, active: true, auto: false },
  ];
  if (cmd === "snapshot") return {
    enabled: true, autoDetect: true, ducking: false, status: "Listening — swtor, Discord",
    extensionClients: 1, sources,
    appVersion: "1.0.0", updateStaged: false, updateVersion: "", launchOnStartup: false,
    settings: { threshold: 0.35, minSpeechMs: 1, endBufferMs: 2000, port: 8730, token: "changeme" },
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
  if (cmd === "websites") return {
    defaultAction: "pause", duckVolume: 0.2, rampMs: 300, extensionClients: 1,
    sites: [
      { host: "youtube.com", action: "pause" },
      { host: "music.youtube.com", action: "duck" },
      { host: "music.amazon.com", action: "duck" },
      { host: "soundcloud.com", action: "duck" },
      { host: "audible.com", action: "pause" },
    ],
  };
  if (cmd === "extensionInfo") return { bundledVersion: "0.3.0", folderPath: "C:\\Users\\you\\Downloads\\Voxinator-extension", repoUrl: "https://github.com/LoganO37/Voxinator" };
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
    if (b.dataset.view === "websites") loadWebsites();
    if (b.dataset.view === "extension") loadExtension();
  })
);

function setExtChip(el, n) {
  if (!el) return;
  el.textContent = "Extension: " + (n > 0 ? n + " connected" : "not connected");
  el.classList.toggle("ok", n > 0);
}

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

  setExtChip($("extChip"), s.extensionClients);
  setExtChip($("wExtChip"), s.extensionClients);

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
  $("setPort").value = st.port;
  $("setToken").value = st.token;
}
$("btnSaveSettings").addEventListener("click", async () => {
  await call("saveSettings", {
    threshold: parseFloat($("setThreshold").value),
    minSpeechMs: parseInt($("setAttack").value, 10),
    endBufferMs: parseInt($("setEnd").value, 10),
    port: parseInt($("setPort").value, 10),
    token: $("setToken").value,
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
  renderApps(g.audioApps, g.monitored);
  GAMES_LIB = g.library || [];
  $("gLibCount").textContent = "(" + GAMES_LIB.length + ")";
  renderLibrary($("gSearch").value);
}
function renderApps(apps, monitored) {
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

// ---- websites screen ----
function setSeg(container, action, btn) {
  container.querySelectorAll(".seg-btn").forEach((b) =>
    b.classList.toggle("active", btn ? b === btn : b.dataset.action === action));
}
async function loadWebsites() {
  const w = await call("websites");
  setSeg($("wDefault"), w.defaultAction || "pause");
  bindRange("wDuck", "vDuck", Math.round((w.duckVolume ?? 0.2) * 100), (v) => v + "%");
  bindRange("wRamp", "vRamp", w.rampMs ?? 300, (v) => (v === 0 ? "instant" : v + " ms"));
  setExtChip($("wExtChip"), w.extensionClients);
  renderSites(w.sites || []);
}
const segBtn = (act, cur) =>
  `<button class="seg-btn${cur === act ? " active" : ""}" data-action="${act}">${act === "pause" ? "Pause" : "Duck"}</button>`;
function renderSites(sites) {
  const el = $("wSites");
  if (!sites.length) { el.innerHTML = `<div class="empty">No site rules yet — add one above.</div>`; return; }
  el.innerHTML = sites.map((s) =>
    `<div class="siterow"><span class="host">${esc(s.host)}</span>` +
    `<div class="seg sm" data-host="${esc(s.host)}">${segBtn("pause", s.action)}${segBtn("duck", s.action)}</div>` +
    `<button class="src-rm" data-host="${esc(s.host)}" title="Remove">✕</button></div>`).join("");
  el.querySelectorAll(".seg.sm .seg-btn").forEach((b) =>
    b.addEventListener("click", () => call("setSite", { host: b.parentElement.dataset.host, action: b.dataset.action }).then(loadWebsites)));
  el.querySelectorAll(".src-rm").forEach((b) =>
    b.addEventListener("click", () => call("removeSite", { host: b.dataset.host }).then(loadWebsites)));
}
$("wDefault").querySelectorAll(".seg-btn").forEach((b) =>
  b.addEventListener("click", () => { setSeg($("wDefault"), b.dataset.action); call("setDefaultAction", { action: b.dataset.action }); }));
$("wDuck").addEventListener("change", () => call("setDuckVolume", { value: parseInt($("wDuck").value, 10) / 100 }));
$("wRamp").addEventListener("change", () => call("setRampMs", { value: parseInt($("wRamp").value, 10) }));
$("wAddBtn").addEventListener("click", () => {
  const v = $("wAddHost").value.trim().toLowerCase().replace(/^https?:\/\//, "").replace(/\/.*$/, "");
  if (v) call("setSite", { host: v, action: "duck" }).then(() => { $("wAddHost").value = ""; loadWebsites(); });
});
$("wAddHost").addEventListener("keydown", (e) => { if (e.key === "Enter") $("wAddBtn").click(); });

// ---- get extension wizard ----
let EXT_BROWSER = "chrome";
let EXT_FOLDER = "";
const EXT_URL = { chrome: "chrome://extensions", firefox: "about:debugging#/runtime/this-firefox" };
const FAQ = [
  ["The icon says \"not connected\"",
    "Make sure the Voxinator app is running — check the system tray. The extension connects to it locally on port 8730. If you changed the port in Settings, update it in the extension's Advanced → connection."],
  ["Chrome: \"Load unpacked\" is greyed out or missing",
    "Turn on Developer mode using the toggle in the top-right of chrome://extensions, then the button appears."],
  ["Firefox: the add-on vanished after I restarted",
    "Firefox removes temporary add-ons on restart. Reload it from about:debugging → This Firefox → Load Temporary Add-on. A permanently-installable signed version is coming."],
  ["I'm not sure which folder to pick",
    "Choose the folder that directly contains manifest.json. The \"Get extension & open folder\" button opens exactly the right one — point your browser at that."],
  ["A site won't duck or pause",
    "Open the Websites tab — that site may be set to the other action, or have no rule (so it uses the default). YouTube Music, Amazon Music, and SoundCloud duck by default; YouTube and Audible pause."],
  ["Still nothing happens",
    "Confirm the Dashboard shows \"Listening\" with your game monitored, and the extension icon shows \"connected\". After updating the extension files, reload it in your browser."],
];

function extStepsHtml(browser, folder) {
  const where = folder ? ` (<code>${esc(folder)}</code>)` : "";
  const steps = browser === "firefox" ? [
    `In Firefox, open <code>about:debugging</code> → <b>This Firefox</b>.`,
    `Click <b>Load Temporary Add-on…</b>`,
    `Select <code>manifest.json</code> inside the extension folder${where}.`,
    `The Voxinator icon appears in your toolbar — you're done.`,
    `<b>Heads-up:</b> Firefox removes temporary add-ons on restart, so you'll redo this until we ship a signed build.`,
  ] : [
    `In Chrome or Edge, open <code>chrome://extensions</code> (Edge: <code>edge://extensions</code>).`,
    `Turn on <b>Developer mode</b> (toggle, top-right).`,
    `Click <b>Load unpacked</b> and select the extension folder${where}.`,
    `The Voxinator icon appears in your toolbar — pin it to see status at a glance.`,
  ];
  return steps.map((s) => `<li>${s}</li>`).join("");
}
function renderExt() {
  $("extUrl").textContent = EXT_URL[EXT_BROWSER];
  $("extSteps").innerHTML = extStepsHtml(EXT_BROWSER, EXT_FOLDER);
}
function showExtUpdate(d) {
  const c = $("extUpdateCard");
  if (!c) return;
  if (d && d.updateAvailable && d.latestVersion) {
    c.style.display = "";
    c.innerHTML = `<div class="card-title" style="color:var(--accent)">⬆ Update available — v${esc(d.latestVersion)}</div>` +
      `<p class="row-sub">You have v${esc(d.bundledVersion || "?")}. <a href="#" id="extRepo">Open the GitHub repo</a>, ` +
      `update, then run <b>Get extension</b> again and reload it in your browser.</p>`;
    const a = $("extRepo");
    if (a) a.onclick = (e) => { e.preventDefault(); call("openUrl", { url: "https://github.com/LoganO37/Voxinator" }); };
  } else {
    c.style.display = "none";
  }
}
async function loadExtension() {
  const info = await call("extensionInfo");
  EXT_FOLDER = (info && info.folderPath) || "";
  renderExt();
  $("extFaq").innerHTML = FAQ.map(([q, a]) => `<details><summary>${esc(q)}</summary><div class="ans">${esc(a)}</div></details>`).join("");
  call("checkUpdate");
}
$("extBrowser").querySelectorAll(".seg-btn").forEach((b) =>
  b.addEventListener("click", () => { EXT_BROWSER = b.dataset.b; setSeg($("extBrowser"), null, b); renderExt(); }));
$("extGet").addEventListener("click", async () => {
  const r = await call("getExtension", { browser: EXT_BROWSER });
  if (r && r.path) { EXT_FOLDER = r.path; renderExt(); $("extPath").innerHTML = `Files copied to <code>${esc(r.path)}</code> — it just opened in Explorer.`; }
  else if (r && r.error) { $("extPath").textContent = "Couldn't copy the files: " + r.error; }
});
$("extZip").addEventListener("click", async () => {
  const r = await call("downloadZip");
  if (r && r.path) $("extPath").innerHTML = `Saved <code>${esc(r.path)}</code>.`;
});
$("extCopyUrl").addEventListener("click", () => call("copyText", { text: EXT_URL[EXT_BROWSER] }));

// ---- init ----
(async () => {
  const snap = await call("snapshot");
  applyState(snap);
  if (snap.settings) fillSettings(snap.settings);
  if (snap.firstRun) {
    document.querySelector('.nav-item[data-view="extension"]').click();
    call("seenWizard");
  }
})();
