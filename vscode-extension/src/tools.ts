import * as vscode from "vscode";
import { state, filterLabels } from "./state";
import { runTrace } from "./cli";

// VS Code Language Model Tools — let Copilot agent mode query the scanned lineage.
// They delegate to the engine (CLI) so there is a single tracer implementation.

const NO_SCAN = "No SSIS project is loaded. Ask the user to run “SSIS Lineage: Scan Project” first.";

function textResult(text: string): vscode.LanguageModelToolResult {
  return new vscode.LanguageModelToolResult([new vscode.LanguageModelTextPart(text)]);
}

export function registerTools(context: vscode.ExtensionContext): void {
  context.subscriptions.push(
    vscode.lm.registerTool("ssisLineage_status", {
      invoke: async () => {
        const g = state.graph;
        if (!g) return textResult(NO_SCAN);
        return textResult(
          `SSIS project loaded. Packages: ${g.Packages?.length ?? 0}, Tasks: ${g.Tasks?.length ?? 0}, ` +
          `Components: ${g.Components?.length ?? 0}, Column mappings: ${g.ColumnMappings?.length ?? 0}.`
        );
      },
    }),

    vscode.lm.registerTool<{ term: string; scope?: string }>("ssisLineage_search", {
      invoke: async (options) => {
        if (!state.labels) return textResult(NO_SCAN);
        const term = (options.input.term ?? "").trim();
        const scope = options.input.scope === "table" ? "table" : "column";
        const hits = filterLabels(term, scope, 50);
        if (hits.length === 0) return textResult(`No ${scope}s match "${term}".`);
        return textResult(`${hits.length} ${scope}(s) matching "${term}":\n` + hits.map((h) => "- " + h.display).join("\n"));
      },
    }),

    vscode.lm.registerTool<{ target: string; direction?: string }>("ssisLineage_trace", {
      invoke: async (options, token) => {
        if (!state.cli || !state.lineageJsonPath) return textResult(NO_SCAN);
        const target = (options.input.target ?? "").trim();
        const direction = normalizeDirection(options.input.direction);
        try {
          const r = await runTrace(state.cli, state.lineageJsonPath, target, direction);
          if (!r.found || !r.steps || r.steps.length === 0) {
            return textResult(`No ${direction} lineage found for "${target}".`);
          }
          const hops = r.steps.map((s) => `${s.rank + 1}. ${s.source} -> ${s.target}${s.operation ? ` [${s.operation}]` : ""}`).join("\n");
          return textResult(
            `Lineage for ${r.focusLabel} (${direction}) — ${r.stepCount} hops across ${r.tableCount} tables:\n${hops}`
          );
        } catch (e) {
          return textResult(`Trace failed: ${e instanceof Error ? e.message : String(e)}`);
        }
      },
    })
  );
}

function normalizeDirection(d: string | undefined): "both" | "upstream" | "downstream" {
  return d === "upstream" || d === "downstream" ? d : "both";
}
