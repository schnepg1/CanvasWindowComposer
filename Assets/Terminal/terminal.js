const host = window.chrome.webview;
const surface = document.getElementById("terminal");
const term = new Terminal({
  allowProposedApi: true, disableStdin: true, scrollback: 1500, fontSize: 13,
  fontFamily: "Cascadia Mono, Consolas, monospace",
  theme: { background: "#121823", foreground: "#e5eaf4", cursor: "#c4b5fd" }
});
const fit = new FitAddon.FitAddon();
term.loadAddon(fit);
term.open(surface);
let controlling = false;
let resizePending;
let grid = { cols: 80, rows: 24 };
function measure() {
  const size = fit.proposeDimensions();
  if (!size || size.cols < 2 || size.rows < 2) return;
  if (controlling) fit.fit();
  host.postMessage({ type: "size", cols: size.cols, rows: size.rows });
}
new ResizeObserver(() => {
  clearTimeout(resizePending);
  resizePending = setTimeout(measure, 100);
}).observe(surface);
term.onData(data => { if (controlling) host.postMessage({ type: "input", data }); });
surface.addEventListener("focusin", () => host.postMessage({ type: "focus", active: true }));
surface.addEventListener("focusout", () => host.postMessage({ type: "focus", active: false }));
host.addEventListener("message", ({ data: message }) => {
  switch (message.type) {
    case "disable":
      controlling = false;
      term.options.disableStdin = true;
      break;
    case "reset":
      controlling = message.control;
      term.options.disableStdin = !controlling;
      term.reset();
      term.options.fontSize = 13;
      if (controlling) { measure(); term.focus(); }
      break;
    case "grid":
      if (!controlling) {
        grid = message;
        // Fit the remote grid without sending a resize to the active viewer.
        term.options.fontSize = 13;
        const available = fit.proposeDimensions();
        if (available) term.options.fontSize = Math.max(5, Math.min(13,
          Math.floor(13 * Math.min(available.cols / grid.cols, available.rows / grid.rows))));
        term.resize(grid.cols, grid.rows);
      }
      break;
    case "data": {
      const bytes = Uint8Array.from(atob(message.data), ch => ch.charCodeAt(0));
      term.write(bytes, () => host.postMessage({ type: "painted", id: message.id }));
      break;
    }
  }
});
host.postMessage({ type: "ready" });
measure();
