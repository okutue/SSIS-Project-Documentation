import { LineageGraph } from "./lineage";
import { Tracer } from "./tracer";

/** Current scan, shared between commands and the language-model tools. */
export interface LineageState {
  graph?: LineageGraph;
  tracer?: Tracer;
}

export const state: LineageState = {};
