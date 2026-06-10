using System;
using System.Collections.Generic;
using System.Linq;
using SsisLineage.Core.Models;

namespace SsisLineage.Core
{
    /// <summary>What kind of thing a <see cref="SearchHit"/> refers to.</summary>
    public enum SearchScope
    {
        Table,
        Column
    }

    /// <summary>One autocomplete result — a distinct table or column found anywhere in the graph.</summary>
    public sealed record SearchHit(SearchScope Scope, string Key, string Display);

    /// <summary>Which way to expand a trace from the seed node.</summary>
    public enum TraceDirection
    {
        /// <summary>Full lineage: upstream to ultimate sources AND downstream to final targets.</summary>
        Both,
        /// <summary>Origins only — where the data comes from.</summary>
        Upstream,
        /// <summary>Impact analysis — everything downstream that this node feeds.</summary>
        Downstream
    }

    /// <summary>One hop of a traced lineage path (a single source-column → target-column edge).</summary>
    public sealed class TraceStep
    {
        public int Rank { get; init; }
        public string SourceServer { get; init; } = "";
        public string SourceDatabase { get; init; } = "";
        public string SourceSchema { get; init; } = "";
        public string SourceTable { get; init; } = "";
        public string SourceColumn { get; init; } = "";
        public string Operation { get; init; } = "";
        public bool IsRename { get; init; }
        public string Expression { get; init; } = "";
        public string FilterConditions { get; init; } = "";
        public string JoinDetails { get; init; } = "";
        public string TargetServer { get; init; } = "";
        public string TargetDatabase { get; init; } = "";
        public string TargetSchema { get; init; } = "";
        public string TargetTable { get; init; } = "";
        public string TargetColumn { get; init; } = "";
        public string PackageName { get; init; } = "";
        public string TaskName { get; init; } = "";
        public string ProcedureName { get; init; } = "";

        public string SourceLabel => Compose(SourceSchema, SourceTable, SourceColumn);
        public string TargetLabel => Compose(TargetSchema, TargetTable, TargetColumn);

        private static string Compose(string schema, string table, string column)
        {
            var t = string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";
            return string.IsNullOrEmpty(column) ? t : $"{t}.{column}";
        }
    }

    /// <summary>Result of a trace: ordered steps plus a filtered sub-graph for diagram rendering.</summary>
    public sealed class TraceResult
    {
        public IReadOnlyList<TraceStep> Steps { get; init; } = Array.Empty<TraceStep>();
        public LineageGraph SubGraph { get; init; } = new();
        public string FocusLabel { get; init; } = "";
        /// <summary>Distinct table-level stages the path spans (source.Customers → proc → DW.Dim = 3).</summary>
        public int TableCount { get; init; }
    }

    /// <summary>
    /// Builds a directed column-level graph from <see cref="LineageGraph.ColumnMappings"/> and
    /// traces the full upstream + downstream path through any node — including mid-stream
    /// intermediates that are neither an ultimate source nor a final target.
    ///
    /// Node identity mirrors the Cytoscape column view's <c>assetOf</c> reconciliation: a
    /// data-flow component (e.g. an OLE DB Source whose SQL is a stored proc) is keyed by its
    /// component id so the proc's internal lineage (source.Customers → proc output) and the
    /// data-flow lineage (proc → destination) stitch into one continuous path.
    /// </summary>
    public sealed class LineageTracer
    {
        private readonly LineageGraph _graph;
        private readonly HashSet<string> _componentIds;

        private readonly List<Edge> _edges = new();

        // fullKey ("nodeKey|column") → (nodeKey, column)
        private readonly Dictionary<string, (string NodeKey, string Column)> _fullNodes =
            new(StringComparer.OrdinalIgnoreCase);
        // nodeKey → best-known display (server/db/schema/table)
        private readonly Dictionary<string, NodeDisplay> _nodeInfo = new(StringComparer.OrdinalIgnoreCase);
        // nodeKey → its column fullKeys
        private readonly Dictionary<string, List<string>> _columnsByNode = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, List<int>> _outgoing = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<int>> _incoming = new(StringComparer.OrdinalIgnoreCase);

        // Table-level node pair per ColumnMap (parallel to _graph.ColumnMappings) — used for step grouping.
        private readonly List<(string SrcNode, string TgtNode)> _mapNodePairs = new();

        public LineageTracer(LineageGraph graph)
        {
            _graph = graph ?? new LineageGraph();
            _componentIds = new HashSet<string>(
                _graph.Components.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
            Build();
        }

        // ── graph construction ──────────────────────────────────────────────

        private void Build()
        {
            foreach (var map in _graph.ColumnMappings)
            {
                var op = map.OperationType ?? "";

                var s = ResolveSide(op, map.SourceComponentId, map.SourceServer, map.SourceDatabase,
                                    map.SourceSchema, map.SourceTable, map.SourceComponentName, map.SourceColumnName);
                var t = ResolveSide(op, map.TargetComponentId, map.TargetServer, map.TargetDatabase,
                                    map.TargetSchema, map.TargetTable, map.TargetComponentName, map.TargetColumnName);

                // Register both endpoints so even isolated/intermediate nodes are searchable.
                Register(s);
                Register(t);

                _mapNodePairs.Add((s.NodeKey, t.NodeKey));

                if (string.IsNullOrEmpty(s.Column) || string.IsNullOrEmpty(t.Column)) continue;
                if (s.FullKey == t.FullKey) continue;

                var idx = _edges.Count;
                _edges.Add(new Edge(s, t, map));

                if (!_outgoing.TryGetValue(s.FullKey, out var outList))
                    _outgoing[s.FullKey] = outList = new List<int>();
                outList.Add(idx);

                if (!_incoming.TryGetValue(t.FullKey, out var inList))
                    _incoming[t.FullKey] = inList = new List<int>();
                inList.Add(idx);
            }
        }

        private void Register(Side side)
        {
            if (string.IsNullOrEmpty(side.Column)) return;

            _fullNodes[side.FullKey] = (side.NodeKey, side.Column);

            if (!_columnsByNode.TryGetValue(side.NodeKey, out var cols))
                _columnsByNode[side.NodeKey] = cols = new List<string>();
            if (!cols.Contains(side.FullKey, StringComparer.OrdinalIgnoreCase))
                cols.Add(side.FullKey);

            // Prefer a display sourced from a real table over a component-name fallback;
            // among component-name fallbacks, prefer a dotted name (e.g. the proc behind
            // a proc-backed source) over a generic component label.
            if (!_nodeInfo.TryGetValue(side.NodeKey, out var existing) ||
                (!existing.FromRealTable && side.HasRealTable) ||
                (!existing.FromRealTable && !side.HasRealTable &&
                 string.IsNullOrEmpty(existing.Schema) && !string.IsNullOrEmpty(side.DisplaySchema)))
            {
                _nodeInfo[side.NodeKey] = new NodeDisplay
                {
                    Server = side.Server,
                    Database = side.Database,
                    Schema = side.DisplaySchema,
                    Table = side.DisplayTable,
                    FromRealTable = side.HasRealTable
                };
            }
            else if (existing.FromRealTable == side.HasRealTable &&
                     (string.IsNullOrEmpty(existing.Server) || string.IsNullOrEmpty(existing.Database)) &&
                     !string.IsNullOrEmpty(side.Server))
            {
                // Same display tier but the registered record lacked server/database
                // (e.g. an inline Execute SQL record whose connection couldn't be
                // resolved) — backfill from a record that knows them.
                _nodeInfo[side.NodeKey] = new NodeDisplay
                {
                    Server = side.Server,
                    Database = string.IsNullOrEmpty(side.Database) ? existing.Database : side.Database,
                    Schema = existing.Schema,
                    Table = existing.Table,
                    FromRealTable = existing.FromRealTable
                };
            }
        }

        // assetOf-style node identity + display resolution for one side of a mapping.
        private Side ResolveSide(string op, string componentId, string server, string database,
                                 string schema, string table, string componentName, string column)
        {
            schema ??= ""; table ??= ""; componentName ??= ""; componentId ??= "";
            var hasRealTable = !string.IsNullOrEmpty(table);

            // Node key (table-level): a side that resolves to a real table is keyed by the
            // table so the same physical table is ONE node everywhere it appears — across
            // packages (Stage Load writes Load_DW.STAGE_Fact_X, Fact Load reads it) and
            // across record kinds (XML data-flow rows vs SQL_PROC rows). Only sides with
            // no resolvable table (transforms, proc-backed sources) reconcile by component id.
            var isComp = !string.IsNullOrEmpty(componentId)
                         && _componentIds.Contains(componentId)
                         && !hasRealTable;

            string nodeKey;
            if (isComp) nodeKey = "c:" + componentId.ToLowerInvariant();
            else if (hasRealTable) nodeKey = "t:" + (string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}").ToLowerInvariant();
            else nodeKey = "x:" + (string.IsNullOrEmpty(componentName) ? componentId : componentName).ToLowerInvariant();

            // Display schema/table: use real fields, else derive from component name ("schema.table").
            var dispSchema = schema;
            var dispTable = table;
            if (!hasRealTable && !string.IsNullOrEmpty(componentName))
            {
                var parts = componentName.Split('.', 2);
                if (parts.Length == 2) { dispSchema = parts[0]; dispTable = parts[1]; }
                else { dispTable = componentName; }
            }

            return new Side
            {
                NodeKey = nodeKey,
                Server = server ?? "",
                Database = database ?? "",
                DisplaySchema = dispSchema,
                DisplayTable = dispTable,
                Column = column ?? "",
                HasRealTable = hasRealTable
            };
        }

        // ── search ──────────────────────────────────────────────────────────

        public IEnumerable<SearchHit> Search(string? term, SearchScope scope, int max = 50)
        {
            term = (term ?? "").Trim();

            if (scope == SearchScope.Table)
            {
                return _nodeInfo
                    .Select(kv => (Key: kv.Key, Display: TableDisplay(kv.Value)))
                    .Where(x => Match(x.Display, term))
                    .GroupBy(x => x.Display, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderBy(x => x.Display, StringComparer.OrdinalIgnoreCase)
                    .Take(max)
                    .Select(x => new SearchHit(SearchScope.Table, x.Key, x.Display));
            }

            return _fullNodes
                .Select(kv => (Key: kv.Key, Display: ColumnDisplay(kv.Value.NodeKey, kv.Value.Column)))
                .Where(x => Match(x.Display, term))
                .OrderBy(x => x.Display, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .Select(x => new SearchHit(SearchScope.Column, x.Key, x.Display));
        }

        private static bool Match(string value, string term) =>
            string.IsNullOrEmpty(term) || value.Contains(term, StringComparison.OrdinalIgnoreCase);

        private string TableDisplay(NodeDisplay n) =>
            string.IsNullOrEmpty(n.Schema) ? n.Table : $"{n.Schema}.{n.Table}";

        private string ColumnDisplay(string nodeKey, string column)
        {
            var label = _nodeInfo.TryGetValue(nodeKey, out var n) ? TableDisplay(n) : nodeKey;
            return string.IsNullOrEmpty(column) ? label : $"{label}.{column}";
        }

        // ── trace ───────────────────────────────────────────────────────────

        public TraceResult Trace(SearchHit hit, TraceDirection direction = TraceDirection.Both)
        {
            if (hit == null) return new TraceResult();

            var seeds = hit.Scope == SearchScope.Table
                ? (_columnsByNode.TryGetValue(hit.Key, out var cols) ? cols : new List<string>())
                : new List<string> { hit.Key };

            var collected = new HashSet<int>();
            if (direction != TraceDirection.Downstream)
                Walk(seeds, collected, _incoming, upstream: true);    // upstream
            if (direction != TraceDirection.Upstream)
                Walk(seeds, collected, _outgoing, upstream: false);   // downstream

            var edgeList = collected.Select(i => _edges[i]).ToList();
            var rank = ComputeRanks(edgeList);

            var steps = edgeList
                .Select(e => ToStep(e, rank.TryGetValue(e.Source.FullKey, out var r) ? r : 0))
                .OrderBy(s => s.Rank)
                .ThenBy(s => s.SourceLabel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.TargetLabel, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var tableCount = edgeList
                .SelectMany(e => new[] { e.Source.NodeKey, e.Target.NodeKey })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            var subGraph = new LineageGraph
            {
                ColumnMappings = edgeList.Select(e => e.Map).ToList(),
                Components = _graph.Components,
                Packages = _graph.Packages,
                Tasks = _graph.Tasks
            };

            return new TraceResult
            {
                Steps = steps,
                SubGraph = subGraph,
                FocusLabel = hit.Display,
                TableCount = tableCount
            };
        }

        // ── step grouping ───────────────────────────────────────────────────

        /// <summary>
        /// Computes a step/stage number for every <see cref="ColumnMap"/> in the graph
        /// (parallel to <c>graph.ColumnMappings</c>). Mappings belonging to the same
        /// table-level stage share the same number, and numbers increase from the
        /// ultimate source(s) toward the final target(s) — it is a grouping, not a
        /// serial row counter. (E.g. every column row of one Execute SQL INSERT step
        /// gets the same step number.)
        /// </summary>
        public int[] GetMappingSteps()
        {
            // Longest-path rank over the TABLE-level graph (one node per table/component stage).
            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var tableEdges = new List<(string Src, string Tgt)>();
            foreach (var (src, tgt) in _mapNodePairs)
            {
                rank.TryAdd(src, 0);
                rank.TryAdd(tgt, 0);
                if (!string.Equals(src, tgt, StringComparison.OrdinalIgnoreCase))
                    tableEdges.Add((src, tgt));
            }

            var nodeCount = rank.Count;
            for (var iter = 0; iter < nodeCount; iter++)
            {
                var changed = false;
                foreach (var (src, tgt) in tableEdges)
                {
                    var s = rank[src];
                    if (rank[tgt] < s + 1)
                    {
                        rank[tgt] = s + 1;
                        changed = true;
                    }
                }
                if (!changed) break;
            }

            // A mapping's step = its source stage rank + 1 (1-based, source→target order).
            var steps = new int[_mapNodePairs.Count];
            for (var i = 0; i < _mapNodePairs.Count; i++)
                steps[i] = rank[_mapNodePairs[i].SrcNode] + 1;
            return steps;
        }

        private void Walk(IEnumerable<string> seeds, HashSet<int> collected,
                          Dictionary<string, List<int>> adjacency, bool upstream)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>(seeds);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                if (!visited.Add(node)) continue;

                // Direct edges on this exact column node.
                var hasDirect = adjacency.TryGetValue(node, out var edges) && edges != null;
                if (hasDirect)
                {
                    foreach (var idx in edges!)
                    {
                        collected.Add(idx);
                        var next = (upstream ? _edges[idx].Source : _edges[idx].Target).FullKey;
                        if (!visited.Contains(next)) queue.Enqueue(next);
                    }
                }

                // Column-preserving wildcard bridge. A SELECT * produces a "*"→"*" edge that
                // carries every column by name. When tracing a specific column that has NO
                // direct edge of its own, cross the table's "*" edges and re-emerge as the
                // SAME column on the far side — so the trace stays scoped to that one column
                // instead of fanning out to every column the "*" represents. When a direct
                // named edge already exists (e.g. an OPENQUERY select list was parsed into
                // per-column records), the bridge is skipped entirely.
                var sep = node.LastIndexOf('|');
                if (hasDirect || sep < 0) continue;

                var nodeKey = node[..sep];
                var col = node[(sep + 1)..];
                if (col == "*") continue;

                if (!adjacency.TryGetValue($"{nodeKey}|*", out var wildEdges)) continue;
                foreach (var idx in wildEdges)
                {
                    var e = _edges[idx];
                    if (e.Source.Column != "*" || e.Target.Column != "*") continue;  // only "*"→"*"

                    collected.Add(idx);
                    var far = upstream ? e.Source : e.Target;
                    var farNamed = $"{far.NodeKey}|{col}";
                    var emerge = _fullNodes.ContainsKey(farNamed) ? farNamed : far.FullKey;
                    if (!visited.Contains(emerge)) queue.Enqueue(emerge);
                }
            }
        }

        // Longest-path-from-source rank within the traced sub-graph (cycle-safe: capped at node count).
        private static Dictionary<string, int> ComputeRanks(List<Edge> edges)
        {
            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in edges)
            {
                rank.TryAdd(e.Source.FullKey, 0);
                rank.TryAdd(e.Target.FullKey, 0);
            }

            var nodeCount = rank.Count;
            for (var iter = 0; iter < nodeCount; iter++)
            {
                var changed = false;
                foreach (var e in edges)
                {
                    var s = rank[e.Source.FullKey];
                    if (rank[e.Target.FullKey] < s + 1)
                    {
                        rank[e.Target.FullKey] = s + 1;
                        changed = true;
                    }
                }
                if (!changed) break;
            }
            return rank;
        }

        private TraceStep ToStep(Edge e, int rank)
        {
            var map = e.Map;
            var pkgName = _graph.Packages.Find(p => p.Id == map.PackageId)?.Name ?? "";
            var taskName = _graph.Tasks.Find(t => t.Id == map.TaskId)?.Name ?? "";

            var sInfo = _nodeInfo.TryGetValue(e.Source.NodeKey, out var si) ? si : e.Source.AsDisplay();
            var tInfo = _nodeInfo.TryGetValue(e.Target.NodeKey, out var ti) ? ti : e.Target.AsDisplay();

            var isRename =
                !string.IsNullOrEmpty(e.Source.Column) && e.Source.Column != "*" &&
                !string.IsNullOrEmpty(e.Target.Column) && e.Target.Column != "*" &&
                !string.Equals(e.Source.Column, e.Target.Column, StringComparison.OrdinalIgnoreCase);

            return new TraceStep
            {
                Rank = rank,
                SourceServer = sInfo.Server,
                SourceDatabase = sInfo.Database,
                SourceSchema = sInfo.Schema,
                SourceTable = sInfo.Table,
                SourceColumn = e.Source.Column,
                Operation = map.OperationType,
                IsRename = isRename,
                Expression = map.SourceExpression,
                FilterConditions = map.FilterConditions,
                JoinDetails = map.JoinDetails,
                TargetServer = tInfo.Server,
                TargetDatabase = tInfo.Database,
                TargetSchema = tInfo.Schema,
                TargetTable = tInfo.Table,
                TargetColumn = e.Target.Column,
                PackageName = pkgName,
                TaskName = taskName,
                ProcedureName = map.ProcedureName
            };
        }

        // ── internal types ──────────────────────────────────────────────────

        private sealed class Edge
        {
            public Side Source { get; }
            public Side Target { get; }
            public ColumnMap Map { get; }
            public Edge(Side source, Side target, ColumnMap map)
            {
                Source = source; Target = target; Map = map;
            }
        }

        private sealed class Side
        {
            public string NodeKey { get; init; } = "";
            public string Server { get; init; } = "";
            public string Database { get; init; } = "";
            public string DisplaySchema { get; init; } = "";
            public string DisplayTable { get; init; } = "";
            public string Column { get; init; } = "";
            public bool HasRealTable { get; init; }

            public string FullKey => $"{NodeKey}|{(Column ?? "").Trim().ToLowerInvariant()}";

            public NodeDisplay AsDisplay() => new()
            {
                Server = Server, Database = Database,
                Schema = DisplaySchema, Table = DisplayTable, FromRealTable = HasRealTable
            };
        }

        private sealed class NodeDisplay
        {
            public string Server { get; init; } = "";
            public string Database { get; init; } = "";
            public string Schema { get; init; } = "";
            public string Table { get; init; } = "";
            public bool FromRealTable { get; init; }
        }
    }
}
