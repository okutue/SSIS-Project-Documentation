// Webview controller. Receives the lineage graph from the extension and renders it
// with the shared cyLineage renderer (same code as the desktop/web app). The Blazor
// drill-down interop is opt-in (a .NET ref), so we pass null and get a read-only graph.
(function () {
  const vscode = acquireVsCodeApi();
  let graph = null;
  let dark = true;
  let mode = "object"; // 'object' | 'column'
  let focus = "";       // searched column/table display (for highlight)
  let focusScope = "";  // 'column' | 'table'

  function rerender() {
    if (!graph || !window.cyLineage) return;
    try {
      window.cyLineage.render("graph", graph, { focus, focusScope }, null, dark, mode);
    } catch (e) {
      console.error("[ssis-lineage] render failed", e);
    }
  }

  function setMode(next) {
    mode = next;
    document.getElementById("btn-object").classList.toggle("active", mode === "object");
    document.getElementById("btn-column").classList.toggle("active", mode === "column");
    rerender();
  }

  window.addEventListener("message", (ev) => {
    const msg = ev.data;
    if (msg && msg.type === "render") {
      graph = msg.graph;
      dark = !!msg.dark;
      focus = msg.focus || "";
      focusScope = msg.focusScope || "";
      if (msg.mode) setMode(msg.mode); // updates buttons + rerenders
      else rerender();
    }
  });

  // Drill-down: clicking a column in the column view traces from it.
  if (window.cyLineage && window.cyLineage.setColumnClickHandler) {
    window.cyLineage.setColumnClickHandler((tip) => {
      if (tip) vscode.postMessage({ type: "traceFrom", target: tip });
    });
  }

  document.getElementById("btn-object").addEventListener("click", () => setMode("object"));
  document.getElementById("btn-column").addEventListener("click", () => setMode("column"));
  document.getElementById("btn-fit").addEventListener("click", () => window.cyLineage && window.cyLineage.fit());
  document.getElementById("btn-reset").addEventListener("click", () => window.cyLineage && window.cyLineage.resetLayout());

  // Tell the extension we are ready to receive the graph.
  vscode.postMessage({ type: "ready" });
})();
