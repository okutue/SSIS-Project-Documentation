import { LineageGraph } from "./lineage";
import { CliInvocation, LabelHit } from "./cli";

/**
 * Current scan, shared between commands and the language-model tools. The engine (CLI)
 * is the single source of truth for tracing/labels — the extension holds only the loaded
 * graph, the lineage.json path to trace against, the cached label list, and how to invoke
 * the engine.
 */
export interface LineageState {
  graph?: LineageGraph;
  lineageJsonPath?: string;
  outputDir?: string;
  labels?: LabelHit[];
  cli?: CliInvocation;
}

export const state: LineageState = {};

/** Case-insensitive substring filter over the cached labels (instant typeahead). */
export function filterLabels(term: string, scope: "column" | "table" | "any", max = 50): LabelHit[] {
  const labels = state.labels ?? [];
  const t = (term ?? "").trim().toLowerCase();
  const out: LabelHit[] = [];
  for (const l of labels) {
    if (scope !== "any" && l.scope !== scope) continue;
    if (t && !l.display.toLowerCase().includes(t)) continue;
    out.push(l);
    if (out.length >= max) break;
  }
  return out;
}
