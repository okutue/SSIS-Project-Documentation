import * as vscode from "vscode";
import * as path from "node:path";
import { resolveCli, runScan, runLabels, runTrace, LabelHit } from "./cli";
import { readLineageGraph, LineageGraph } from "./lineage";
import { LineageTreeProvider } from "./lineageTree";
import { GraphPanel } from "./graphPanel";
import { state, filterLabels } from "./state";
import { registerTools } from "./tools";

const SECRET_CONN = "ssisLineage.sqlConnectionString";
type Direction = "both" | "upstream" | "downstream";

let channel: vscode.OutputChannel;
let tree: LineageTreeProvider;
let lastGraph: LineageGraph | undefined;
let lastTraceCsv: string | undefined;

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
        lastTraceCsv = undefined;
        state.graph = lastGraph;
        state.lineageJsonPath = result.lineageJsonPath;
        state.cli = cli;
        state.labels = await runLabels(cli, result.lineageJsonPath);
        tree.setGraph(lastGraph);
        GraphPanel.showOrUpdate(context, lastGraph);
        const m = lastGraph.ColumnMappings?.length ?? 0;
        vscode.window.showInformationMessage(`SSIS Lineage: scan complete — ${m} column mappings.`);
        surfaceScanDiagnostics(context, lastGraph, includeSqlProcedures);
      } catch (err) {
        channel.show(true);
        vscode.window.showErrorMessage(`SSIS Lineage: ${err instanceof Error ? err.message : String(err)}`);
      }
    }
  );
}

/**
 * Surface why lineage may be incomplete: engine warnings, and — the common case —
 * a data-flow source backed by a stored procedure that wasn't traced through because
 * stored-procedure enrichment is off. (Execute SQL tasks enrich regardless; data-flow
 * components only when Include SQL Procedures is on, which is why a Source running a
 * proc can appear disconnected from its staging table.)
 */
function surfaceScanDiagnostics(context: vscode.ExtensionContext, graph: LineageGraph, includeSqlProcedures: boolean): void {
  const warnings = graph.Warnings ?? [];
  if (warnings.length) {
    channel.appendLine(`\n[ssis-lineage] ${warnings.length} warning(s):`);
    for (const w of warnings) {
      channel.appendLine("  - " + w);
    }
  }

  if (!includeSqlProcedures && hasProcBackedDataFlowSource(graph)) {
    vscode.window.showInformationMessage(
      "SSIS Lineage: a data-flow source runs a stored procedure, but SQL procedure enrichment is off — " +
      "so it isn't traced through to its source tables (it shows as a standalone node). Enable it and re-scan to connect the chain.",
      "Enable & re-scan"
    ).then((pick) => {
      if (pick === "Enable & re-scan") {
        vscode.workspace.getConfiguration("ssisLineage")
          .update("includeSqlProcedures", true, vscode.ConfigurationTarget.Workspace)
          .then(() => scanCommand(context));
      }
    });
  } else if (warnings.length) {
    vscode.window.showWarningMessage(
      `SSIS Lineage: scan completed with ${warnings.length} warning(s) — some lineage may be incomplete (e.g. a procedure body couldn't be loaded).`,
      "Show details"
    ).then((pick) => { if (pick === "Show details") { channel.show(true); } });
  }
}

/** Heuristic: a data-flow Source/Destination whose SQL is a stored-proc reference. */
function hasProcBackedDataFlowSource(graph: LineageGraph): boolean {
  const procRef = /^\s*(exec(ute)?\b|\[?[\w]+\]?\.\[?[\w]+\]?\s*;?\s*$)/i;
  return (graph.Components ?? []).some((c) => {
    const type = (c.Type ?? "").toLowerCase();
    const sql = (c.SqlQueryOrTable ?? "").trim();
    if (!sql || sql.includes("\n")) return false;
    if (/^(select|with)\b/i.test(sql)) return false;
    const isDataFlow = type.includes("source") || type.includes("destination") || type.includes("oledb");
    return isDataFlow && procRef.test(sql);
  });
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
  if (!state.cli || !state.lineageJsonPath || !state.labels || !lastGraph) {
    vscode.window.showInformationMessage("SSIS Lineage: run “Scan Project” first.");
    return;
  }

  const hit = await pickHit();
  if (!hit) {
    return;
  }

  const direction = await pickDirection();
  if (!direction) {
    return;
  }

  try {
    const result = await runTrace(state.cli, state.lineageJsonPath, hit.display, direction);
    if (!result.found || !result.subGraph) {
      vscode.window.showInformationMessage(`SSIS Lineage: no ${direction} lineage for ${hit.display}.`);
      return;
    }
    lastTraceCsv = result.csv;
    GraphPanel.showTrace(context, result.subGraph as LineageGraph, result.focusLabel ?? hit.display, result.focusScope ?? hit.scope);
    vscode.window.showInformationMessage(
      `Trace: ${result.focusLabel} — ${result.stepCount} steps across ${result.tableCount} tables. Run “Export Trace (CSV)” to save.`
    );
  } catch (err) {
    vscode.window.showErrorMessage(`SSIS Lineage: trace failed — ${err instanceof Error ? err.message : String(err)}`);
  }
}

interface HitItem extends vscode.QuickPickItem { hit: LabelHit; }

/** Dynamic search QuickPick across columns and tables (instant filter over cached labels). */
function pickHit(): Promise<LabelHit | undefined> {
  return new Promise((resolve) => {
    const qp = vscode.window.createQuickPick<HitItem>();
    qp.placeholder = "Search a column or table to trace…";
    qp.matchOnDescription = true;

    const refresh = (term: string) => {
      qp.items = filterLabels(term, "any", 100).map((h): HitItem => ({ label: h.display, description: h.scope, hit: h }));
    };
    refresh("");

    let resolved = false;
    qp.onDidChangeValue(refresh);
    qp.onDidAccept(() => { resolved = true; const sel = qp.selectedItems[0]; qp.hide(); resolve(sel?.hit); });
    qp.onDidHide(() => { qp.dispose(); if (!resolved) resolve(undefined); });
    qp.show();
  });
}

async function pickDirection(): Promise<Direction | undefined> {
  const pick = await vscode.window.showQuickPick(
    [
      { label: "$(arrow-both) Full lineage", d: "both" as Direction },
      { label: "$(arrow-up) Origins (upstream)", d: "upstream" as Direction },
      { label: "$(arrow-down) Impact (downstream)", d: "downstream" as Direction },
    ],
    { placeHolder: "Trace direction" }
  );
  return pick?.d;
}

async function exportTraceCommand(): Promise<void> {
  if (!lastTraceCsv) {
    vscode.window.showInformationMessage("SSIS Lineage: run a trace first (“Trace Lineage”).");
    return;
  }
  const doc = await vscode.workspace.openTextDocument({ content: lastTraceCsv, language: "csv" });
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
