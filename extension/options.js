// Cross-browser options page (Chrome/Chromium + Firefox).
const api = (typeof browser !== "undefined") ? browser : chrome;

const DEFAULTS = {
  port: 8730,
  token: "changeme",
  defaultAction: "duck",
  duckVolume: 0.2,
  rampMs: 300,
  perSite: "youtube.com = pause\nyoutu.be = pause\nmusic.youtube.com = duck",
};

const $ = (id) => document.getElementById(id);

function setVolLabel(v) { $("duckVolumeLabel").textContent = Math.round(v) + "%"; }

async function load() {
  const s = await api.storage.local.get(DEFAULTS);
  $("port").value = s.port;
  $("token").value = s.token;
  $("defaultAction").value = s.defaultAction;
  $("duckVolume").value = Math.round(s.duckVolume * 100);
  setVolLabel(s.duckVolume * 100);
  $("rampMs").value = s.rampMs;
  $("perSite").value = s.perSite;
  refreshStatus();
}

async function save() {
  await api.storage.local.set({
    port: Number($("port").value) || DEFAULTS.port,
    token: $("token").value || DEFAULTS.token,
    defaultAction: $("defaultAction").value,
    duckVolume: (Number($("duckVolume").value) || 20) / 100,
    rampMs: Number($("rampMs").value) || 0,
    perSite: $("perSite").value,
  });
  $("saved").textContent = "Saved.";
  setTimeout(() => ($("saved").textContent = ""), 1500);
}

function refreshStatus() {
  Promise.resolve(api.runtime.sendMessage({ cmd: "status" }))
    .then((resp) => {
      if (!resp) { $("status").textContent = "Connection: unknown"; return; }
      $("status").textContent =
        "Connection: " + (resp.connected ? "connected to engine" : "not connected") +
        (resp.ducking ? " — DUCKING NOW" : "");
    })
    .catch(() => { $("status").textContent = "Connection: unknown"; });
}

$("duckVolume").addEventListener("input", (e) => setVolLabel(e.target.value));
$("save").addEventListener("click", save);
document.addEventListener("DOMContentLoaded", load);
setInterval(refreshStatus, 2000);
