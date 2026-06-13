// Copies the shared graph renderer + Cytoscape libs from the Blazor RCL into the
// extension's media/vendor folder, so the webview reuses the exact same rendering
// code as the desktop/web app (single source of truth in this monorepo).
import { copyFileSync, mkdirSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const wwwroot = join(here, "..", "..", "src", "SsisLineage.UI", "wwwroot");
const vendor = join(here, "..", "media", "vendor");

const assets = [
  ["lib/cytoscape/cytoscape.min.js", "cytoscape.min.js"],
  ["lib/cytoscape/dagre.min.js", "dagre.min.js"],
  ["lib/cytoscape/cytoscape-dagre.min.js", "cytoscape-dagre.min.js"],
  ["lib/cytoscape/cytoscape-node-html-label.min.js", "cytoscape-node-html-label.min.js"],
  ["graph-cytoscape.js", "graph-cytoscape.js"],
  // Object-view node-card / legend / tooltip styles (the .cy-* classes the HTML labels use).
  ["app.css", "graph.css"],
];

mkdirSync(vendor, { recursive: true });

let copied = 0;
for (const [src, dest] of assets) {
  const from = join(wwwroot, src);
  if (!existsSync(from)) {
    console.error(`[copy-assets] missing source: ${from}`);
    process.exitCode = 1;
    continue;
  }
  copyFileSync(from, join(vendor, dest));
  copied++;
}
console.log(`[copy-assets] copied ${copied}/${assets.length} assets to media/vendor`);
