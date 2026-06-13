import * as vscode from "vscode";
import * as path from "node:path";
import { resolveCli, runScan } from "./cli";
import { readLineageGraph, LineageGraph } from "./lineage";
import { LineageTreeProvider } from "./lineageTree";
import { GraphPanel } from "./graphPanel";

let channel: vscode.OutputChannel;
let tree: LineageTreeProvider;
let lastGraph: LineageGraph | undefined;

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
    })
  );
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
  const opts = {
    projectPath: path.dirname(project.fsPath),
    startPackage,
    includeSqlProcedures: cfg.get<boolean>("includeSqlProcedures", false),
    sqlConnectionString: cfg.get<string>("sqlConnectionString", ""),
  };

  await vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: "SSIS Lineage: scanning…", cancellable: false },
    async () => {
      try {
        const result = await runScan(cli, opts, channel);
        lastGraph = readLineageGraph(result.lineageJsonPath);
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
