// Cross-browser status/connection page (Chrome/Chromium + Firefox). Behavior settings (site
// rules, duck level, fade) live in the Voxinator desktop app and arrive over the WebSocket as a
// CONFIG message — this page only shows connection status and the connection bootstrap.
const api = (typeof browser !== "undefined") ? browser : chrome;

const DEFAULTS = { port: 8730, token: "changeme" };
const $ = (id) => document.getElementById(id);

async function load() {
  const s = await api.storage.local.get(DEFAULTS);
  $("port").value = s.port;
  $("token").value = s.token;
  refreshStatus();
}

async function save() {
  await api.storage.local.set({
    port: Number($("port").value) || DEFAULTS.port,
    token: $("token").value || DEFAULTS.token,
  });
  $("saved").textContent = "Saved.";
  setTimeout(() => ($("saved").textContent = ""), 1500);
}

function refreshStatus() {
  const el = $("status");
  Promise.resolve(api.runtime.sendMessage({ cmd: "status" }))
    .then((resp) => {
      if (!resp) { el.textContent = "Connection: unknown"; el.className = ""; return; }
      el.textContent =
        "Connection: " + (resp.connected ? "connected to the app" : "not connected") +
        (resp.ducking ? " — ducking now" : "");
      el.className = resp.connected ? "ok" : "bad";
    })
    .catch(() => { el.textContent = "Connection: unknown"; el.className = ""; });
}

$("save").addEventListener("click", save);
document.addEventListener("DOMContentLoaded", load);
setInterval(refreshStatus, 2000);
