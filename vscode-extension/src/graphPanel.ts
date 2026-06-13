import * as vscode from "vscode";
import { LineageGraph } from "./lineage";

/**
 * Hosts the lineage diagram in a webview, reusing the shared `cyLineage` renderer
 * (Cytoscape) copied from the Blazor RCL. One panel is reused across scans.
 */
export class GraphPanel {
  /** Drill-down: invoked when a column is clicked in the webview's column view. */
  static onTraceFrom: ((target: string) => void) | undefined;

  private static current: GraphPanel | undefined;
  private readonly panel: vscode.WebviewPanel;
  private graph: LineageGraph | undefined;
  private mode: "object" | "column" = "object";
  private focus = "";
  private focusScope = "";
  private disposables: vscode.Disposable[] = [];

  /** Show the full project graph (object/data-flow view). */
  static showOrUpdate(context: vscode.ExtensionContext, graph: LineageGraph): void {
    const p = GraphPanel.ensure(context, graph);
    p.mode = "object";
    p.focus = "";
    p.focusScope = "";
    p.postGraph();
    p.panel.reveal();
  }

  /** Show a traced sub-graph (column view) with the focused node highlighted. */
  static showTrace(
    context: vscode.ExtensionContext, subGraph: LineageGraph, focusLabel: string, focusScope: string
  ): void {
    const p = GraphPanel.ensure(context, subGraph);
    p.mode = "column";
    p.focus = focusLabel;
    p.focusScope = focusScope;
    p.postGraph();
    p.panel.reveal();
  }

  private static ensure(context: vscode.ExtensionContext, graph: LineageGraph): GraphPanel {
    if (GraphPanel.current) {
      GraphPanel.current.graph = graph;
      return GraphPanel.current;
    }
    GraphPanel.current = new GraphPanel(context, graph);
    return GraphPanel.current;
  }

  private constructor(private readonly context: vscode.ExtensionContext, graph: LineageGraph) {
    this.graph = graph;
    this.panel = vscode.window.createWebviewPanel(
      "ssisLineageGraph",
      "SSIS Lineage",
      vscode.ViewColumn.Active,
      {
        enableScripts: true,
        retainContextWhenHidden: true,
        localResourceRoots: [vscode.Uri.joinPath(context.extensionUri, "media")],
      }
    );

    this.panel.webview.html = this.html();
    this.panel.onDidDispose(() => this.dispose(), null, this.disposables);

    // The webview tells us when it is ready; reply with the current graph + theme.
    this.panel.webview.onDidReceiveMessage(
      (msg) => {
        if (msg?.type === "ready") {
          this.postGraph();
        } else if (msg?.type === "traceFrom" && typeof msg.target === "string") {
          GraphPanel.onTraceFrom?.(msg.target);
        }
      },
      null,
      this.disposables
    );

    // Re-post on theme change so the diagram tracks light/dark.
    vscode.window.onDidChangeActiveColorTheme(() => this.postGraph(), null, this.disposables);
  }

  private postGraph(): void {
    const dark = vscode.window.activeColorTheme.kind === vscode.ColorThemeKind.Dark
      || vscode.window.activeColorTheme.kind === vscode.ColorThemeKind.HighContrast;
    this.panel.webview.postMessage({
      type: "render",
      graph: this.graph,
      dark,
      mode: this.mode,
      focus: this.focus,
      focusScope: this.focusScope,
    });
  }

  private uri(file: string): vscode.Uri {
    return this.panel.webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, "media", file));
  }

  private html(): string {
    const w = this.panel.webview;
    const nonce = nonceString();
    const vendor = (f: string) => this.uri(`vendor/${f}`);
    const csp = [
      `default-src 'none'`,
      `img-src ${w.cspSource} data:`,
      `style-src ${w.cspSource} 'unsafe-inline'`,
      `script-src 'nonce-${nonce}'`,
      `font-src ${w.cspSource}`,
    ].join("; ");

    return /* html */ `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <meta http-equiv="Content-Security-Policy" content="${csp}" />
  <link rel="stylesheet" href="${vendor("graph.css")}" />
  <link rel="stylesheet" href="${this.uri("styles.css")}" />
  <title>SSIS Lineage</title>
</head>
<body>
  <div id="toolbar">
    <button id="btn-object" class="active" title="Object / data-flow view">Objects</button>
    <button id="btn-column" title="Column-level view">Columns</button>
    <span class="sep"></span>
    <button id="btn-fit" title="Zoom to fit">Fit</button>
    <button id="btn-reset" title="Reset moved nodes">Reset</button>
  </div>
  <div id="graph" class="graph-view"></div>

  <script nonce="${nonce}" src="${vendor("cytoscape.min.js")}"></script>
  <script nonce="${nonce}" src="${vendor("dagre.min.js")}"></script>
  <script nonce="${nonce}" src="${vendor("cytoscape-dagre.min.js")}"></script>
  <script nonce="${nonce}" src="${vendor("cytoscape-node-html-label.min.js")}"></script>
  <script nonce="${nonce}" src="${vendor("graph-cytoscape.js")}"></script>
  <script nonce="${nonce}" src="${this.uri("main.js")}"></script>
</body>
</html>`;
  }

  private dispose(): void {
    GraphPanel.current = undefined;
    this.panel.dispose();
    while (this.disposables.length) {
      this.disposables.pop()?.dispose();
    }
  }
}

function nonceString(): string {
  let text = "";
  const chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
  for (let i = 0; i < 32; i++) {
    text += chars.charAt(Math.floor(Math.random() * chars.length));
  }
  return text;
}
