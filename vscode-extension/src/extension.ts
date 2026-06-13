import * as vscode from "vscode";
import * as path from "node:path";
import { resolveCli, runScan } from "./cli";
import { readLineageGraph, LineageGraph } from "./lineage";
import { LineageTreeProvider } from "./lineageTree";
import { GraphPanel } from "./graphPanel";
import { Tracer, traceStepsToCsv, SearchHit, TraceDirection, TraceResult } from "./tracer";
import { state } from "./state";
import { registerTools } from "./tools";

const SECRET_CONN = "ssisLineage.sqlConnectionString";

let channel: vscode.OutputChannel;
let tree: LineageTreeProvider;
let lastGraph: LineageGraph | undefined;
let tracer: Tracer | undefined;
let lastTrace: TraceResult | undefined;

export function activate(context: vscode.ExtensionContext): void {
  channel = vscode.window.createOutputChannel("SSIS Lineage");
  tree = new LineageTreeProvider();
  context.subscriptions.push(
    channel,
    vscode.window.registerTreeDataProvider("ssisLineage.explorer", tree),
    vscode.commands.registerCommand("ssisLineage.scan", () => scanCommand(context)),
    vscode.commands.registerCommand("ssisLineage.openGraph", () => {
      if (lastGraph) {
        GraphPanel.showOrUpdate(context, lastGraph);
      } else {
        vscode.window.showInformationMessage("SSIS Lineage: run “Scan Project” first.");
      }
    }),
    vscode.commands.registerCommand("ssisLineage.trace", () => traceCommand(context)),
    vscode.commands.registerCommand("ssisLineage.exportTrace", () => exportTraceCommand()),
    vscode.commands.registerCommand("ssisLineage.setConnection", () => setConnectionCommand(context)),
    vscode.commands.registerCommand("ssisLineage.clearConnection", () => clearConnectionCommand(context))
  );

  // Expose lineage to Copilot agent mode (no-op on older VS Code without the LM tools API).
  if (vscode.lm && typeof vscode.lm.registerTool === "function") {
    registerTools(context);
  }
}

export function deactivate(): void {
  /* nothing to clean up beyond disposables */
}

async function scanCommand(context: vscode.ExtensionContext): Promise<void> {
  const cli = resolveCli(context);
  if (!cli) {
    return;
  }

  const project = await pickProject();
  if (!project) {
    return;
  }

  const startPackage = await resolveStartPackage(project);
  if (!startPackage) {
    return;
  }

  const cfg = vscode.workspace.getConfiguration("ssisLineage");
  const includeSqlProcedures = cfg.get<boolean>("includeSqlProcedures", false);
  // Connection precedence: explicit setting → stored secret (set via “Set SQL Connection…”).
  const settingConn = cfg.get<string>("sqlConnectionString", "").trim();
  const sqlConnectionString = settingConn || (includeSqlProcedures ? (await context.secrets.get(SECRET_CONN)) ?? "" : "");
  const opts = {
    projectPath: path.dirname(project.fsPath),
    startPackage,
    includeSqlProcedures,
    sqlConnectionString,
  };

  await vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: "SSIS Lineage: scanning…", cancellable: false },
    async () => {
      try {
        const result = await runScan(cli, opts, channel);
        lastGraph = readLineageGraph(result.lineageJsonPath);
        tracer = new Tracer(lastGraph);
        lastTrace = undefined;
        state.graph = lastGraph;
        state.tracer = tracer;
        tree.setGraph(lastGraph);
        GraphPanel.showOrUpdate(context, lastGraph);
        const m = lastGraph.ColumnMappings?.length ?? 0;
        vscode.window.showInformationMessage(`SSIS Lineage: scan complete — ${m} column mappings.`);
      } catch (err) {
        channel.show(true);
        vscode.window.showErrorMessage(`SSIS Lineage: ${err instanceof Error ? err.message : String(err)}`);
      }
    }
  );
}

/** Find .dtproj files in the workspace; prompt when there is more than one. */
async function pickProject(): Promise<vscode.Uri | undefined> {
  const found = await vscode.workspace.findFiles("**/*.dtproj", "**/{bin,obj,node_modules}/**");
  if (found.length === 0) {
    vscode.window.showErrorMessage("SSIS Lineage: no .dtproj found in the workspace.");
    return undefined;
  }
  if (found.length === 1) {
    return found[0];
  }
  const pick = await vscode.window.showQuickPick(
    found.map((u) => ({ label: path.basename(u.fsPath), description: vscode.workspace.asRelativePath(u), uri: u })),
    { placeHolder: "Select the SSIS project to scan" }
  );
  return pick?.uri;
}

/** Resolve the entry package from settings, else let the user pick a .dtsx. */
async function resolveStartPackage(project: vscode.Uri): Promise<string | undefined> {
  const configured = vscode.workspace.getConfiguration("ssisLineage").get<string>("startPackage", "").trim();
  if (configured) {
    return configured;
  }
  const dir = path.dirname(project.fsPath);
  const rel = new vscode.RelativePattern(dir, "*.dtsx");
  const packages = await vscode.workspace.findFiles(rel);
  if (packages.length === 0) {
    vscode.window.showErrorMessage("SSIS Lineage: no .dtsx packages found next to the project.");
    return undefined;
  }
  if (packages.length === 1) {
    return path.basename(packages[0].fsPath);
  }
  const pick = await vscode.window.showQuickPick(
    packages.map((u) => path.basename(u.fsPath)),
    { placeHolder: "Select the entry/master package to scan from" }
  );
  return pick;
}

// ── trace ─────────────────────────────────────────────────────────────────

async function traceCommand(context: vscode.ExtensionContext): Promise<void> {
  if (!tracer || !lastGraph) {
    vscode.window.showInformationMessage("SSIS Lineage: run “Scan Project” first.");
    return;
  }

  const hit = await pickHit(tracer);
  if (!hit) {
    return;
  }

  const direction = await pickDirection();
  if (!direction) {
    return;
  }

  const result = tracer.trace(hit, direction);
  lastTrace = result;
  if (result.mappings.length === 0) {
    vscode.window.showInformationMessage(`SSIS Lineage: no ${direction} lineage for ${result.focusLabel}.`);
    return;
  }

  GraphPanel.showTrace(
    context,
    { Packages: lastGraph.Packages, Tasks: lastGraph.Tasks, Components: lastGraph.Components, ColumnMappings: result.mappings },
    result.focusLabel,
    result.focusScope
  );
  vscode.window.showInformationMessage(
    `Trace: ${result.focusLabel} — ${result.steps.length} steps across ${result.tableCount} tables. Run “Export Trace (CSV)” to save.`
  );
}

interface HitItem extends vscode.QuickPickItem { hit: SearchHit; }

/** Dynamic search QuickPick across columns and tables (typeahead via the tracer). */
function pickHit(t: Tracer): Promise<SearchHit | undefined> {
  return new Promise((resolve) => {
    const qp = vscode.window.createQuickPick<HitItem>();
    qp.placeholder = "Search a column or table to trace…";
    qp.matchOnDescription = true;

    const refresh = (term: string) => {
      const cols = t.search(term, "column", 30).map((h): HitItem => ({ label: h.display, description: "column", hit: h }));
      const tbls = t.search(term, "table", 20).map((h): HitItem => ({ label: h.display, description: "table", hit: h }));
      qp.items = [...cols, ...tbls];
    };
    refresh("");

    let resolved = false;
    qp.onDidChangeValue(refresh);
    qp.onDidAccept(() => { resolved = true; const sel = qp.selectedItems[0]; qp.hide(); resolve(sel?.hit); });
    qp.onDidHide(() => { qp.dispose(); if (!resolved) resolve(undefined); });
    qp.show();
  });
}

async function pickDirection(): Promise<TraceDirection | undefined> {
  const pick = await vscode.window.showQuickPick(
    [
      { label: "$(arrow-both) Full lineage", d: "both" as TraceDirection },
      { label: "$(arrow-up) Origins (upstream)", d: "upstream" as TraceDirection },
      { label: "$(arrow-down) Impact (downstream)", d: "downstream" as TraceDirection },
    ],
    { placeHolder: "Trace direction" }
  );
  return pick?.d;
}

async function exportTraceCommand(): Promise<void> {
  if (!lastTrace) {
    vscode.window.showInformationMessage("SSIS Lineage: run a trace first (“Trace Lineage”).");
    return;
  }
  const csv = traceStepsToCsv(lastTrace.steps);
  const doc = await vscode.workspace.openTextDocument({ content: csv, language: "csv" });
  await vscode.window.showTextDocument(doc);
}

// ── connection (stored in SecretStorage, not settings, since it may carry credentials) ──

async function setConnectionCommand(context: vscode.ExtensionContext): Promise<void> {
  const value = await vscode.window.showInputBox({
    prompt: "SQL Server connection string for stored-procedure enrichment (stored securely in VS Code Secret Storage).",
    placeHolder: "Server=…;Database=…;Integrated Security=true   (or User ID=…;Password=…)",
    password: true,
    ignoreFocusOut: true,
  });
  if (value === undefined) {
    return; // cancelled
  }
  if (value.trim() === "") {
    await context.secrets.delete(SECRET_CONN);
    vscode.window.showInformationMessage("SSIS Lineage: stored SQL connection cleared.");
    return;
  }
  await context.secrets.store(SECRET_CONN, value.trim());
  vscode.window.showInformationMessage("SSIS Lineage: SQL connection saved to Secret Storage.");
}

async function clearConnectionCommand(context: vscode.ExtensionContext): Promise<void> {
  await context.secrets.delete(SECRET_CONN);
  vscode.window.showInformationMessage("SSIS Lineage: stored SQL connection cleared.");
}
