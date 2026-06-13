// Minimal typings for the engine's lineage.json (PascalCase, as emitted by the CLI).
export interface LineagePackage { Id: string; Name: string; Path?: string; }
export interface LineageTask { Id: string; Name: string; Type?: string; PackageId?: string; PackageName?: string; }
export interface LineageComponent { Id: string; Name: string; Type?: string; TaskId?: string; SqlQueryOrTable?: string; }
export interface LineageColumnMap {
  PackageId?: string; TaskId?: string; ProcedureName?: string;
  SourceComponentId?: string; SourceComponentName?: string;
  SourceServer?: string; SourceDatabase?: string;
  SourceSchema?: string; SourceTable?: string; SourceColumnName?: string;
  SourceExpression?: string;
  TargetComponentId?: string; TargetComponentName?: string;
  TargetServer?: string; TargetDatabase?: string;
  TargetSchema?: string; TargetTable?: string; TargetColumnName?: string;
  OperationType?: string;
  JoinDetails?: string; FilterConditions?: string;
}
export interface LineageGraph {
  Packages?: LineagePackage[];
  Tasks?: LineageTask[];
  Components?: LineageComponent[];
  ColumnMappings?: LineageColumnMap[];
  Warnings?: string[];
}

import * as fs from "node:fs";

export function readLineageGraph(jsonPath: string): LineageGraph {
  const raw = fs.readFileSync(jsonPath, "utf8");
  return JSON.parse(raw) as LineageGraph;
}
