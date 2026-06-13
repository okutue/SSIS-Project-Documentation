import * as vscode from "vscode";
import { state } from "./state";
import { SearchScope, TraceDirection } from "./tracer";

// VS Code Language Model Tools — let Copilot agent mode query the scanned lineage.
// They run against the in-process tracer over the loaded graph (same data as the UI).

const NO_SCAN = "No SSIS project is loaded. Ask the user to run “SSIS Lineage: Scan Project” first.";

function textResult(text: string): vscode.LanguageModelToolResult {
  return new vscode.LanguageModelToolResult([new vscode.LanguageModelTextPart(text)]);
}

export function registerTools(context: vscode.ExtensionContext): void {
  context.subscriptions.push(
    vscode.lm.registerTool("ssisLineage_status", {
      invoke: async () => {
        const g = state.graph;
        if (!g) {
          return textResult(NO_SCAN);
        }
        return textResult(
          `SSIS project loaded. Packages: ${g.Packages?.length ?? 0}, Tasks: ${g.Tasks?.length ?? 0}, ` +
          `Components: ${g.Components?.length ?? 0}, Column mappings: ${g.ColumnMappings?.length ?? 0}.`
        );
      },
    }),

    vscode.lm.registerTool<{ term: string; scope?: string }>("ssisLineage_search", {
      invoke: async (options) => {
        if (!state.tracer) {
          return textResult(NO_SCAN);
        }
        const term = (options.input.term ?? "").trim();
        const scope: SearchScope = options.input.scope === "table" ? "table" : "column";
        const hits = state.tracer.search(term, scope, 50);
        if (hits.length === 0) {
          return textResult(`No ${scope}s match "${term}".`);
        }
        const lines = hits.map((h) => `- ${h.display}`).join("\n");
        return textResult(`${hits.length} ${scope}(s) matching "${term}":\n${lines}`);
      },
    }),

    vscode.lm.registerTool<{ target: string; direction?: string }>("ssisLineage_trace", {
      invoke: async (options) => {
        if (!state.tracer) {
          return textResult(NO_SCAN);
        }
        const target = (options.input.target ?? "").trim();
        const direction = normalizeDirection(options.input.direction);

        // Resolve the target to a single hit: exact column match, else exact table, else best search hit.
        const cols = state.tracer.search(target, "column", 50);
        const tbls = state.tracer.search(target, "table", 50);
        const eq = (s: string) => s.toLowerCase() === target.toLowerCase();
        const hit = cols.find((h) => eq(h.display)) ?? tbls.find((h) => eq(h.display)) ?? cols[0] ?? tbls[0];
        if (!hit) {
          return textResult(`Nothing in the lineage matches "${target}".`);
        }

        const result = state.tracer.trace(hit, direction);
        if (result.steps.length === 0) {
          return textResult(`No ${direction} lineage for ${result.focusLabel}.`);
        }

        const hops = result.steps
          .map((s) => {
            const src = compose(s.sourceServer, s.sourceDatabase, s.sourceSchema, s.sourceTable, s.sourceColumn);
            const tgt = compose(s.targetServer, s.targetDatabase, s.targetSchema, s.targetTable, s.targetColumn);
            const op = s.operation ? ` [${s.operation}]` : "";
            return `${s.rank + 1}. ${src} -> ${tgt}${op}`;
          })
          .join("\n");

        return textResult(
          `Lineage trace for ${result.focusLabel} (${direction}) — ${result.steps.length} hops across ${result.tableCount} tables:\n${hops}`
        );
      },
    })
  );
}

function normalizeDirection(d: string | undefined): TraceDirection {
  return d === "upstream" || d === "downstream" ? d : "both";
}

function compose(server: string, db: string, schema: string, table: string, column: string): string {
  const parts = [server, db, schema, table].filter((p) => p && p.length > 0);
  const tbl = parts.join(".");
  return column ? `${tbl}.${column}` : tbl;
}
