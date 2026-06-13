import * as vscode from "vscode";
import { LineageGraph, LineageComponent } from "./lineage";

type NodeKind = "package" | "task" | "component" | "info";

class LineageNode extends vscode.TreeItem {
  constructor(
    label: string,
    public readonly kind: NodeKind,
    public readonly id: string,
    collapsible: vscode.TreeItemCollapsibleState,
    description?: string
  ) {
    super(label, collapsible);
    this.description = description;
    this.contextValue = kind;
    this.iconPath = new vscode.ThemeIcon(
      kind === "package" ? "package" : kind === "task" ? "checklist" : kind === "component" ? "symbol-field" : "info"
    );
  }
}

/**
 * Explorer tree of the scanned project: Package → Task → Component, plus a summary
 * info node. Backed by the in-memory lineage graph; refreshed after each scan.
 */
export class LineageTreeProvider implements vscode.TreeDataProvider<LineageNode> {
  private readonly _onDidChange = new vscode.EventEmitter<LineageNode | undefined>();
  readonly onDidChangeTreeData = this._onDidChange.event;

  private graph: LineageGraph | undefined;

  setGraph(graph: LineageGraph | undefined): void {
    this.graph = graph;
    this._onDidChange.fire(undefined);
  }

  getTreeItem(element: LineageNode): vscode.TreeItem {
    return element;
  }

  getChildren(element?: LineageNode): LineageNode[] {
    if (!this.graph) {
      return [new LineageNode("No scan yet — run “SSIS Lineage: Scan Project”", "info", "empty", vscode.TreeItemCollapsibleState.None)];
    }

    if (!element) {
      const summary = new LineageNode(
        "Summary",
        "info",
        "summary",
        vscode.TreeItemCollapsibleState.None,
        `${this.graph.Packages?.length ?? 0} pkg · ${this.graph.Tasks?.length ?? 0} task · ${this.graph.ColumnMappings?.length ?? 0} mappings`
      );
      const pkgs = (this.graph.Packages ?? []).map(
        (p) => new LineageNode(p.Name, "package", p.Id, vscode.TreeItemCollapsibleState.Collapsed)
      );
      return [summary, ...pkgs];
    }

    if (element.kind === "package") {
      return (this.graph.Tasks ?? [])
        .filter((t) => t.PackageId === element.id)
        .map((t) => new LineageNode(t.Name, "task", t.Id, vscode.TreeItemCollapsibleState.Collapsed, t.Type));
    }

    if (element.kind === "task") {
      return (this.graph.Components ?? [])
        .filter((c) => c.TaskId === element.id)
        .map((c) => this.componentNode(c));
    }

    return [];
  }

  private componentNode(c: LineageComponent): LineageNode {
    const node = new LineageNode(c.Name, "component", c.Id, vscode.TreeItemCollapsibleState.None, c.Type);
    if (c.SqlQueryOrTable) {
      node.tooltip = c.SqlQueryOrTable;
    }
    return node;
  }
}
