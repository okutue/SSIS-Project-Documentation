// TypeScript port of the engine's LineageTracer — pure graph logic over the loaded
// lineage.json (no DB, no CLI round-trip), so search/trace is instant in the extension.
// Mirrors SsisLineage.Core/LineageTracer.cs (node identity, wildcard-preserving walk,
// longest-path ranks). Keep in sync if the C# tracer changes.
import { LineageGraph, LineageColumnMap, LineagePackage, LineageTask, LineageComponent } from "./lineage";

export type SearchScope = "table" | "column";
export type TraceDirection = "both" | "upstream" | "downstream";

export interface SearchHit { scope: SearchScope; key: string; display: string; }

export interface TraceStep {
  rank: number;
  sourceServer: string; sourceDatabase: string; sourceSchema: string; sourceTable: string; sourceColumn: string;
  operation: string; isRename: boolean; expression: string; filter: string; join: string;
  targetServer: string; targetDatabase: string; targetSchema: string; targetTable: string; targetColumn: string;
  packageName: string; taskName: string; procedure: string;
}

export interface TraceResult {
  focusLabel: string;
  focusScope: SearchScope;
  mappings: LineageColumnMap[]; // the traced sub-graph's column mappings (original objects)
  steps: TraceStep[];
  tableCount: number;
}

interface Side {
  nodeKey: string; server: string; database: string;
  dispSchema: string; dispTable: string; column: string; hasRealTable: boolean; fullKey: string;
}
interface Edge { source: Side; target: Side; map: LineageColumnMap; }
interface NodeDisplay { server: string; database: string; schema: string; table: string; fromRealTable: boolean; }

const lc = (s: string | undefined) => (s ?? "").toLowerCase();

export class Tracer {
  private edges: Edge[] = [];
  private fullNodes = new Map<string, { nodeKey: string; column: string }>();
  private nodeInfo = new Map<string, NodeDisplay>();
  private columnsByNode = new Map<string, string[]>();
  private outgoing = new Map<string, number[]>();
  private incoming = new Map<string, number[]>();
  private componentIds: Set<string>;
  private packages: LineagePackage[];
  private tasks: LineageTask[];
  private components: LineageComponent[];

  constructor(private graph: LineageGraph) {
    this.packages = graph.Packages ?? [];
    this.tasks = graph.Tasks ?? [];
    this.components = graph.Components ?? [];
    this.componentIds = new Set(this.components.map((c) => lc(c.Id)));
    this.build();
  }

  private resolveSide(componentId: string, server: string, database: string,
                      schema: string, table: string, componentName: string, column: string): Side {
    schema = schema ?? ""; table = table ?? ""; componentName = componentName ?? ""; componentId = componentId ?? "";
    const hasRealTable = table.length > 0;
    const isComp = componentId.length > 0 && this.componentIds.has(lc(componentId)) && !hasRealTable;

    let nodeKey: string;
    if (isComp) nodeKey = "c:" + lc(componentId);
    else if (hasRealTable) nodeKey = "t:" + lc(schema ? `${schema}.${table}` : table);
    else nodeKey = "x:" + lc(componentName || componentId);

    let dispSchema = schema, dispTable = table;
    if (!hasRealTable && componentName) {
      const i = componentName.indexOf(".");
      if (i > 0) { dispSchema = componentName.slice(0, i); dispTable = componentName.slice(i + 1); }
      else dispTable = componentName;
    }

    return {
      nodeKey, server: server ?? "", database: database ?? "",
      dispSchema, dispTable, column: column ?? "", hasRealTable,
      fullKey: `${nodeKey}|${lc(column).trim()}`,
    };
  }

  private register(s: Side): void {
    if (!s.column) return;
    this.fullNodes.set(s.fullKey, { nodeKey: s.nodeKey, column: s.column });
    const cols = this.columnsByNode.get(s.nodeKey) ?? [];
    if (!cols.some((c) => c.toLowerCase() === s.fullKey.toLowerCase())) cols.push(s.fullKey);
    this.columnsByNode.set(s.nodeKey, cols);

    const existing = this.nodeInfo.get(s.nodeKey);
    if (!existing ||
        (!existing.fromRealTable && s.hasRealTable) ||
        (!existing.fromRealTable && !s.hasRealTable && !existing.schema && !!s.dispSchema)) {
      this.nodeInfo.set(s.nodeKey, {
        server: s.server, database: s.database, schema: s.dispSchema, table: s.dispTable, fromRealTable: s.hasRealTable,
      });
    } else if (existing.fromRealTable === s.hasRealTable &&
               (!existing.server || !existing.database) && !!s.server) {
      this.nodeInfo.set(s.nodeKey, {
        server: s.server, database: existing.database || s.database,
        schema: existing.schema, table: existing.table, fromRealTable: existing.fromRealTable,
      });
    }
  }

  private build(): void {
    for (const map of this.graph.ColumnMappings ?? []) {
      const s = this.resolveSide(map.SourceComponentId ?? "", map.SourceServer ?? "", map.SourceDatabase ?? "",
        map.SourceSchema ?? "", map.SourceTable ?? "", map.SourceComponentName ?? "", map.SourceColumnName ?? "");
      const t = this.resolveSide(map.TargetComponentId ?? "", map.TargetServer ?? "", map.TargetDatabase ?? "",
        map.TargetSchema ?? "", map.TargetTable ?? "", map.TargetComponentName ?? "", map.TargetColumnName ?? "");
      this.register(s);
      this.register(t);

      if (!s.column || !t.column || s.fullKey === t.fullKey) continue;
      const idx = this.edges.length;
      this.edges.push({ source: s, target: t, map });
      (this.outgoing.get(s.fullKey) ?? this.outgoing.set(s.fullKey, []).get(s.fullKey)!).push(idx);
      (this.incoming.get(t.fullKey) ?? this.incoming.set(t.fullKey, []).get(t.fullKey)!).push(idx);
    }
  }

  private tableDisplay(n: NodeDisplay): string {
    return n.schema ? `${n.schema}.${n.table}` : n.table;
  }
  private columnDisplay(nodeKey: string, column: string): string {
    const n = this.nodeInfo.get(nodeKey);
    const label = n ? this.tableDisplay(n) : nodeKey;
    return column ? `${label}.${column}` : label;
  }

  search(term: string, scope: SearchScope, max = 50): SearchHit[] {
    const t = (term ?? "").trim().toLowerCase();
    const match = (v: string) => !t || v.toLowerCase().includes(t);

    if (scope === "table") {
      const seen = new Set<string>();
      const out: SearchHit[] = [];
      for (const [key, info] of this.nodeInfo) {
        const display = this.tableDisplay(info);
        if (!display || !match(display) || seen.has(display.toLowerCase())) continue;
        seen.add(display.toLowerCase());
        out.push({ scope: "table", key, display });
      }
      return out.sort((a, b) => a.display.localeCompare(b.display)).slice(0, max);
    }

    const out: SearchHit[] = [];
    for (const [key, v] of this.fullNodes) {
      const display = this.columnDisplay(v.nodeKey, v.column);
      if (match(display)) out.push({ scope: "column", key, display });
    }
    return out.sort((a, b) => a.display.localeCompare(b.display)).slice(0, max);
  }

  private walk(seeds: string[], collected: Set<number>, adjacency: Map<string, number[]>, upstream: boolean): void {
    const visited = new Set<string>();
    const queue = [...seeds];
    while (queue.length) {
      const node = queue.shift()!;
      if (visited.has(node)) continue;
      visited.add(node);

      const edges = adjacency.get(node);
      const hasDirect = !!edges && edges.length > 0;
      if (hasDirect) {
        for (const idx of edges!) {
          collected.add(idx);
          const next = (upstream ? this.edges[idx].source : this.edges[idx].target).fullKey;
          if (!visited.has(next)) queue.push(next);
        }
      }

      const sep = node.lastIndexOf("|");
      if (hasDirect || sep < 0) continue;
      const nodeKey = node.slice(0, sep);
      const col = node.slice(sep + 1);
      if (col === "*") continue;

      const wild = adjacency.get(`${nodeKey}|*`);
      if (!wild) continue;
      for (const idx of wild) {
        const e = this.edges[idx];
        if (e.source.column !== "*" || e.target.column !== "*") continue;
        collected.add(idx);
        const far = upstream ? e.source : e.target;
        const farNamed = `${far.nodeKey}|${col}`;
        const emerge = this.fullNodes.has(farNamed) ? farNamed : far.fullKey;
        if (!visited.has(emerge)) queue.push(emerge);
      }
    }
  }

  private computeRanks(edges: Edge[]): Map<string, number> {
    const rank = new Map<string, number>();
    for (const e of edges) { rank.set(e.source.fullKey, 0); rank.set(e.target.fullKey, 0); }
    for (let i = 0; i < rank.size; i++) {
      let changed = false;
      for (const e of edges) {
        const s = rank.get(e.source.fullKey)!;
        if (rank.get(e.target.fullKey)! < s + 1) { rank.set(e.target.fullKey, s + 1); changed = true; }
      }
      if (!changed) break;
    }
    return rank;
  }

  private toStep(e: Edge, rank: number): TraceStep {
    const si = this.nodeInfo.get(e.source.nodeKey) ?? { server: e.source.server, database: e.source.database, schema: e.source.dispSchema, table: e.source.dispTable, fromRealTable: e.source.hasRealTable };
    const ti = this.nodeInfo.get(e.target.nodeKey) ?? { server: e.target.server, database: e.target.database, schema: e.target.dispSchema, table: e.target.dispTable, fromRealTable: e.target.hasRealTable };
    const m = e.map;
    const isRename = !!e.source.column && e.source.column !== "*" && !!e.target.column && e.target.column !== "*" &&
      e.source.column.toLowerCase() !== e.target.column.toLowerCase();
    return {
      rank,
      sourceServer: si.server, sourceDatabase: si.database, sourceSchema: si.schema, sourceTable: si.table, sourceColumn: e.source.column,
      operation: m.OperationType ?? "", isRename, expression: m.SourceExpression ?? "", filter: m.FilterConditions ?? "", join: m.JoinDetails ?? "",
      targetServer: ti.server, targetDatabase: ti.database, targetSchema: ti.schema, targetTable: ti.table, targetColumn: e.target.column,
      packageName: this.packages.find((p) => p.Id === m.PackageId)?.Name ?? "",
      taskName: this.tasks.find((t) => t.Id === m.TaskId)?.Name ?? "",
      procedure: m.ProcedureName ?? "",
    };
  }

  trace(hit: SearchHit, direction: TraceDirection): TraceResult {
    const seeds = hit.scope === "table" ? (this.columnsByNode.get(hit.key) ?? []) : [hit.key];
    const collected = new Set<number>();
    if (direction !== "downstream") this.walk(seeds, collected, this.incoming, true);
    if (direction !== "upstream") this.walk(seeds, collected, this.outgoing, false);

    const edgeList = [...collected].map((i) => this.edges[i]);
    const rank = this.computeRanks(edgeList);
    const steps = edgeList
      .map((e) => this.toStep(e, rank.get(e.source.fullKey) ?? 0))
      .sort((a, b) => a.rank - b.rank
        || `${a.sourceSchema}.${a.sourceTable}.${a.sourceColumn}`.localeCompare(`${b.sourceSchema}.${b.sourceTable}.${b.sourceColumn}`)
        || `${a.targetSchema}.${a.targetTable}.${a.targetColumn}`.localeCompare(`${b.targetSchema}.${b.targetTable}.${b.targetColumn}`));
    const tableCount = new Set(edgeList.flatMap((e) => [e.source.nodeKey, e.target.nodeKey])).size;

    return { focusLabel: hit.display, focusScope: hit.scope, mappings: edgeList.map((e) => e.map), steps, tableCount };
  }
}

/** CSV matching the engine's trace export column order. */
export function traceStepsToCsv(steps: TraceStep[]): string {
  const header = ["Step", "SourceServer", "SourceDatabase", "SourceSchema", "SourceTable", "SourceColumn",
    "Operation", "Rename", "Expression", "FilterConditions", "TargetServer", "TargetDatabase",
    "TargetSchema", "TargetTable", "TargetColumn", "Package", "Task", "Procedure", "JoinCondition"];
  const q = (v: string) => {
    const s = v ?? "";
    return /[",\r\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
  };
  const rows = steps.map((s) => [
    String(s.rank + 1), s.sourceServer, s.sourceDatabase, s.sourceSchema, s.sourceTable, s.sourceColumn,
    s.operation, s.isRename ? "Yes" : "", s.expression, s.filter, s.targetServer, s.targetDatabase,
    s.targetSchema, s.targetTable, s.targetColumn, s.packageName, s.taskName, s.procedure, s.join,
  ].map(q).join(","));
  return [header.join(","), ...rows].join("\n");
}
