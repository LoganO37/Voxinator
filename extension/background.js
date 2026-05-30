// Cross-browser (Chrome/Chromium + Firefox). Firefox runs this as a background script,
// Chrome as a service worker; it uses only APIs available in both. `api` resolves to the
// promise-based namespace (browser.* on Firefox, chrome.* on Chrome MV3).
const api = (typeof browser !== "undefined") ? browser : chrome;

const DEFAULTS = { port: 8730, token: "changeme" };

let ws = null;
let ducking = false;
let reconnectTimer = null;

async function getConn() {
  return api.storage.local.get(DEFAULTS);
}

async function connect() {
  if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;
  const { port, token } = await getConn();
  try {
    ws = new WebSocket(`ws://127.0.0.1:${port}/?token=${encodeURIComponent(token)}`);
  } catch (e) {
    scheduleReconnect();
    return;
  }
  ws.onopen = () => console.log("[gdd] connected to engine");
  ws.onmessage = (ev) => {
    let msg;
    try { msg = JSON.parse(ev.data); } catch { return; }
    if (msg.type === "DIALOG_START") setDucking(true);
    else if (msg.type === "DIALOG_END") setDucking(false);
  };
  ws.onclose = () => { ws = null; scheduleReconnect(); };
  ws.onerror = () => { try { ws.close(); } catch {} };
}

function scheduleReconnect() {
  if (reconnectTimer) return;
  reconnectTimer = setTimeout(() => { reconnectTimer = null; connect(); }, 2000);
}

async function setDucking(on) {
  ducking = on;
  const tabs = await api.tabs.query({});
  for (const t of tabs) {
    if (t.id == null) continue;
    Promise.resolve(api.tabs.sendMessage(t.id, { cmd: "duck", on })).catch(() => {}); // no content script in some tabs
  }
}

api.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg && msg.cmd === "status") {
    sendResponse({ connected: !!(ws && ws.readyState === WebSocket.OPEN), ducking });
  } else if (msg && msg.cmd === "getState") {
    sendResponse({ ducking }); // content scripts query this on init to sync mid-dialog
  }
  return false;
});

api.storage.onChanged.addListener((changes, area) => {
  if (area === "local" && (changes.port || changes.token)) {
    try { if (ws) ws.close(); } catch {}
    ws = null;
    connect();
  }
});

if (api.alarms) {
  api.alarms.create("keepalive", { periodInMinutes: 0.4 }); // ~24 s, keeps the worker warm
  api.alarms.onAlarm.addListener((a) => { if (a.name === "keepalive") connect(); });
}
api.runtime.onStartup.addListener(connect);
api.runtime.onInstalled.addListener(connect);
connect();
