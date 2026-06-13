import * as vscode from "vscode";
import { spawn } from "node:child_process";
import * as fs from "node:fs";
import * as path from "node:path";
import * as os from "node:os";

/** How to launch the engine CLI: an executable, or `dotnet <dll>`. */
export interface CliInvocation {
  command: string;
  baseArgs: string[];
  label: string;
}

/**
 * Resolves how to run the SSIS Lineage CLI:
 *   1. `ssisLineage.cliPath` setting — a self-contained .exe or a `*.Cli.dll` (via dotnet)
 *   2. a binary bundled with the extension under bin/ (packaged distribution)
 *   3. dev fallback: the sibling net10.0 build output in the monorepo
 * Returns null with a guidance message when none is available.
 */
export function resolveCli(context: vscode.ExtensionContext): CliInvocation | null {
  const configured = vscode.workspace
    .getConfiguration("ssisLineage")
    .get<string>("cliPath", "")
    .trim();

  if (configured) {
    if (!fs.existsSync(configured)) {
      vscode.window.showErrorMessage(`SSIS Lineage: configured cliPath does not exist: ${configured}`);
      return null;
    }
    return configured.toLowerCase().endsWith(".dll")
      ? { command: "dotnet", baseArgs: [configured], label: `dotnet ${path.basename(configured)}` }
      : { command: configured, baseArgs: [], label: path.basename(configured) };
  }

  const exe = process.platform === "win32" ? "SsisLineage.Cli.exe" : "SsisLineage.Cli";
  const bundled = path.join(context.extensionPath, "bin", exe);
  if (fs.existsSync(bundled)) {
    return { command: bundled, baseArgs: [], label: exe };
  }

  for (const cfg of ["Debug", "Release"]) {
    const devDll = path.join(
      context.extensionPath, "..", "src", "SsisLineage.Cli", "bin", cfg, "net10.0", "SsisLineage.Cli.dll"
    );
    if (fs.existsSync(devDll)) {
      return { command: "dotnet", baseArgs: [devDll], label: `dotnet SsisLineage.Cli.dll (${cfg})` };
    }
  }

  vscode.window.showErrorMessage(
    "SSIS Lineage: no CLI found. Set 'ssisLineage.cliPath' to the built SsisLineage.Cli (.dll or executable).",
    "Open Settings"
  ).then((pick) => {
    if (pick === "Open Settings") {
      vscode.commands.executeCommand("workbench.action.openSettings", "ssisLineage.cliPath");
    }
  });
  return null;
}

export interface ScanOptions {
  projectPath: string;
  startPackage: string;
  includeSqlProcedures: boolean;
  sqlConnectionString: string;
  connectionManagerOverrides?: Record<string, string>;
}

export interface ScanResult {
  outputDir: string;
  lineageJsonPath: string;
}

export interface LabelHit { scope: "column" | "table"; display: string; }

export interface TraceResult {
  found: boolean;
  focusLabel?: string;
  focusScope?: "column" | "table";
  tableCount?: number;
  stepCount?: number;
  steps?: { rank: number; source: string; target: string; operation: string; rename: boolean }[];
  csv?: string;
  subGraph?: unknown; // a LineageGraph (passed straight to the renderer)
}

/** Runs `scan`, streaming engine logs to the channel; returns the output dir + lineage.json path. */
export function runScan(cli: CliInvocation, opts: ScanOptions, channel: vscode.OutputChannel): Promise<ScanResult> {
  const outputDir = fs.mkdtempSync(path.join(os.tmpdir(), "ssis-lineage-"));
  const args = [
    ...cli.baseArgs, "scan",
    "--project-path", opts.projectPath,
    "--start-package", opts.startPackage,
    "--output", outputDir,
  ];
  if (opts.includeSqlProcedures) {
    args.push("--include-sql-procedures");
    if (opts.sqlConnectionString) args.push("--sql-connection-string", opts.sqlConnectionString);
  }
  const overrides = opts.connectionManagerOverrides;
  if (overrides && Object.keys(overrides).length > 0) {
    const file = path.join(outputDir, "connection-managers.json");
    fs.writeFileSync(file, JSON.stringify(overrides));
    args.push("--connection-managers", file);
  }
  // Don't echo full args — they can include a connection string.
  channel.appendLine(`[ssis-lineage] scan "${opts.projectPath}" (start: ${opts.startPackage}, procs: ${opts.includeSqlProcedures})`);

  return new Promise<ScanResult>((resolve, reject) => {
    const proc = spawn(cli.command, args, { windowsHide: true });
    proc.stdout.on("data", (d) => channel.append(d.toString()));
    proc.stderr.on("data", (d) => channel.append(d.toString()));
    proc.on("error", reject);
    proc.on("close", (code) => {
      if (code !== 0) { reject(new Error(`CLI exited with code ${code}. See the SSIS Lineage output.`)); return; }
      const lineageJsonPath = path.join(outputDir, "lineage.json");
      if (!fs.existsSync(lineageJsonPath)) { reject(new Error("Scan finished but lineage.json was not produced.")); return; }
      resolve({ outputDir, lineageJsonPath });
    });
  });
}

/** Runs an engine subcommand that emits JSON to stdout, and parses it. */
function runJson<T>(cli: CliInvocation, args: string[]): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const proc = spawn(cli.command, [...cli.baseArgs, ...args], { windowsHide: true });
    let out = "";
    let err = "";
    proc.stdout.on("data", (d) => (out += d.toString()));
    proc.stderr.on("data", (d) => (err += d.toString()));
    proc.on("error", reject);
    proc.on("close", (code) => {
      if (code !== 0) { reject(new Error(err.trim() || `CLI exited with code ${code}`)); return; }
      try { resolve(JSON.parse(out) as T); }
      catch (e) { reject(new Error(`Could not parse engine output: ${e instanceof Error ? e.message : String(e)}`)); }
    });
  });
}

/** All searchable column/table names (engine-canonical) — for instant typeahead. */
export function runLabels(cli: CliInvocation, lineageJsonPath: string): Promise<LabelHit[]> {
  return runJson<LabelHit[]>(cli, ["labels", "--input", lineageJsonPath]);
}

/** Trace a column/table — returns the sub-graph, ordered steps, and engine-generated CSV. */
export function runTrace(
  cli: CliInvocation, lineageJsonPath: string, target: string, direction: "both" | "upstream" | "downstream"
): Promise<TraceResult> {
  return runJson<TraceResult>(cli, ["trace", "--input", lineageJsonPath, "--target", target, "--direction", direction]);
}

/** Diff two lineage.json exports; returns the markdown drift report. */
export function runDiff(cli: CliInvocation, oldJson: string, newJson: string): Promise<string> {
  const reportPath = path.join(os.tmpdir(), `ssis-lineage-diff-${Date.now()}.md`);
  return new Promise<string>((resolve, reject) => {
    const proc = spawn(cli.command, [...cli.baseArgs, "diff", oldJson, newJson, "--output", reportPath], { windowsHide: true });
    let err = "";
    proc.stderr.on("data", (d) => (err += d.toString()));
    proc.on("error", reject);
    proc.on("close", (code) => {
      // diff exits 1 only with --fail-on-changes (not passed here); treat 0/1 as success.
      if (code !== 0 && code !== 1) { reject(new Error(err.trim() || `diff exited with code ${code}`)); return; }
      try { resolve(fs.readFileSync(reportPath, "utf8")); }
      catch (e) { reject(new Error(`Could not read diff report: ${e instanceof Error ? e.message : String(e)}`)); }
    });
  });
}
