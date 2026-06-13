import * as vscode from "vscode";
import { spawn } from "node:child_process";
import * as fs from "node:fs";
import * as path from "node:path";
import * as os from "node:os";

/** How to launch the engine CLI: an executable, or `dotnet <dll>`. */
interface CliInvocation {
  command: string;
  baseArgs: string[];
  label: string;
}

/**
 * Resolves how to run the SSIS Lineage CLI:
 *   1. `ssisLineage.cliPath` setting — a self-contained .exe or a `*.Cli.dll` (via dotnet)
 *   2. a binary bundled with the extension under bin/ (packaged distribution)
 * Returns null with a guidance message when neither is available.
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

  // Bundled binary (packaged distribution ships a self-contained CLI per platform).
  const exe = process.platform === "win32" ? "SsisLineage.Cli.exe" : "SsisLineage.Cli";
  const bundled = path.join(context.extensionPath, "bin", exe);
  if (fs.existsSync(bundled)) {
    return { command: bundled, baseArgs: [], label: exe };
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
}

export interface ScanResult {
  outputDir: string;
  lineageJsonPath: string;
}

/**
 * Runs `scan` and returns the output directory. Streams CLI stdout/stderr to the
 * provided output channel. Rejects on non-zero exit.
 */
export function runScan(
  cli: CliInvocation,
  opts: ScanOptions,
  channel: vscode.OutputChannel
): Promise<ScanResult> {
  const outputDir = fs.mkdtempSync(path.join(os.tmpdir(), "ssis-lineage-"));
  const args = [
    ...cli.baseArgs,
    "scan",
    "--project-path", opts.projectPath,
    "--start-package", opts.startPackage,
    "--output", outputDir,
  ];
  if (opts.includeSqlProcedures) {
    args.push("--include-sql-procedures");
    if (opts.sqlConnectionString) {
      args.push("--sql-connection-string", opts.sqlConnectionString);
    }
  }

  channel.appendLine(`[ssis-lineage] ${cli.command} ${args.join(" ")}`);

  return new Promise<ScanResult>((resolve, reject) => {
    const proc = spawn(cli.command, args, { windowsHide: true });
    proc.stdout.on("data", (d) => channel.append(d.toString()));
    proc.stderr.on("data", (d) => channel.append(d.toString()));
    proc.on("error", (err) => reject(err));
    proc.on("close", (code) => {
      if (code !== 0) {
        reject(new Error(`CLI exited with code ${code}. See the SSIS Lineage output for details.`));
        return;
      }
      const lineageJsonPath = path.join(outputDir, "lineage.json");
      if (!fs.existsSync(lineageJsonPath)) {
        reject(new Error("Scan finished but lineage.json was not produced."));
        return;
      }
      resolve({ outputDir, lineageJsonPath });
    });
  });
}
