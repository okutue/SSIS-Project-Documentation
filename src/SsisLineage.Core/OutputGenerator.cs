using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using SsisLineage.Core.Models;

namespace SsisLineage.Core
{
    public class OutputGenerator
    {
        public static string GenerateJson(LineageGraph graph)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            return RedactSecrets(JsonSerializer.Serialize(graph, options));
        }

        public static string GenerateYaml(LineageGraph graph)
        {
            var serializer = new SerializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();
            return RedactSecrets(serializer.Serialize(graph));
        }

        public static string GenerateCypher(LineageGraph graph)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// SSIS Lineage Graph Import Script");
            sb.AppendLine("// Schema: see docs/neo4j-schema.md in the repository.");
            sb.AppendLine("// Run in Neo4j Browser or cypher-shell against an empty or dedicated database.");
            sb.AppendLine("CREATE CONSTRAINT IF NOT EXISTS FOR (p:Package) REQUIRE p.id IS UNIQUE;");
            sb.AppendLine("CREATE CONSTRAINT IF NOT EXISTS FOR (t:Task) REQUIRE t.id IS UNIQUE;");
            sb.AppendLine("CREATE CONSTRAINT IF NOT EXISTS FOR (c:Component) REQUIRE c.id IS UNIQUE;");
            sb.AppendLine();

            // Create Packages
            sb.AppendLine("// Packages");
            foreach (var pkg in graph.Packages)
            {
                sb.AppendLine($"MERGE (p:Package {{id: '{Escape(pkg.Id)}', name: '{Escape(pkg.Name)}', path: '{Escape(pkg.Path)}'}});");
            }
            sb.AppendLine();

            // Create Tasks
            sb.AppendLine("// Tasks");
            foreach (var task in graph.Tasks)
            {
                sb.AppendLine($"MERGE (t:Task {{id: '{Escape(task.Id)}', name: '{Escape(task.Name)}', type: '{Escape(task.Type)}'}});");
            }
            sb.AppendLine();

            // Create Components
            sb.AppendLine("// Components");
            foreach (var comp in graph.Components)
            {
                sb.AppendLine($"MERGE (c:Component {{id: '{Escape(comp.Id)}', name: '{Escape(comp.Name)}', type: '{Escape(comp.Type)}', sql: '{Escape(comp.SqlQueryOrTable)}'}});");
            }
            sb.AppendLine();

            // Task relationships
            sb.AppendLine("// Task Parent-Child relationships");
            foreach (var task in graph.Tasks)
            {
                sb.AppendLine($"MATCH (t:Task {{id: '{Escape(task.Id)}'}}), (p:Package {{id: '{Escape(task.PackageId)}'}}) MERGE (t)-[:BELONGS_TO]->(p);");
            }
            sb.AppendLine();

            // Component relationships
            sb.AppendLine("// Component Parent-Child relationships");
            foreach (var comp in graph.Components)
            {
                if (!string.IsNullOrEmpty(comp.TaskId))
                {
                    sb.AppendLine($"MATCH (c:Component {{id: '{Escape(comp.Id)}'}}), (t:Task {{id: '{Escape(comp.TaskId)}'}}) MERGE (c)-[:BELONGS_TO]->(t);");
                }
            }
            sb.AppendLine();

            // Execution edges
            sb.AppendLine("// Execution Flow");
            foreach (var edge in graph.ExecutionEdges)
            {
                if (!string.IsNullOrEmpty(edge.FromTaskId) && !string.IsNullOrEmpty(edge.ToTaskId))
                {
                    sb.AppendLine($"MATCH (t1:Task {{id: '{Escape(edge.FromTaskId)}'}}), (t2:Task {{id: '{Escape(edge.ToTaskId)}'}}) MERGE (t1)-[:PRECEDES {{value: '{Escape(edge.PrecedenceConstraintValue)}', expr: '{Escape(edge.Expression)}'}}]->(t2);");
                }
            }
            sb.AppendLine();

            // Data Flow edges
            sb.AppendLine("// Data Flow connections");
            foreach (var edge in graph.DataFlowEdges)
            {
                sb.AppendLine($"MATCH (c1:Component {{id: '{Escape(edge.FromComponentId)}'}}), (c2:Component {{id: '{Escape(edge.ToComponentId)}'}}) MERGE (c1)-[:FLOWS_TO]->(c2);");
            }
            sb.AppendLine();

            // Column Mappings
            sb.AppendLine("// Column-level Lineage mappings");
            foreach (var map in graph.ColumnMappings)
            {
                sb.AppendLine($"MATCH (c1:Component {{id: '{Escape(map.SourceComponentId)}'}}), (c2:Component {{id: '{Escape(map.TargetComponentId)}'}}) " +
                              $"CREATE (c1)-[:MAPS_TO {{srcCol: '{Escape(map.SourceColumnName)}', destCol: '{Escape(map.TargetColumnName)}', expr: '{Escape(map.SourceExpression)}', opType: '{Escape(map.OperationType)}'}}]->(c2);");
            }

            return RedactSecrets(sb.ToString());
        }

        public static string GenerateMarkdownReport(LineageGraph graph)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# SSIS Project Lineage Documentation");
            sb.AppendLine($"Generated on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            sb.AppendLine("## Packages Included");
            foreach (var pkg in graph.Packages)
            {
                sb.AppendLine($"- **{pkg.Name}** (GUID: `{pkg.Id}`)");
                sb.AppendLine($"  - Path: `{pkg.Path}`");
            }
            sb.AppendLine();

            sb.AppendLine("## Tasks");
            sb.AppendLine("| Package | Task | Type |");
            sb.AppendLine("| --- | --- | --- |");
            foreach (var task in graph.Tasks)
            {
                sb.AppendLine($"| {task.PackageName} | {task.Name} | `{task.Type}` |");
            }
            sb.AppendLine();

            sb.AppendLine("## Data Flow Components");
            sb.AppendLine("| Package | Task | Component | Type | Connection | SQL / Table |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (var component in graph.Components)
            {
                var pkgName = graph.Packages.Find(p => p.Id == component.PackageId)?.Name ?? "Unknown";
                var taskName = graph.Tasks.Find(t => t.Id == component.TaskId)?.Name ?? "";
                sb.AppendLine($"| {pkgName} | {taskName} | {component.Name} | `{component.Type}` | `{component.ConnectionManager}` | `{component.SqlQueryOrTable}` |");
            }
            sb.AppendLine();

            sb.AppendLine("## Data Flow Paths");
            sb.AppendLine("| From Component | To Component | Path |");
            sb.AppendLine("| --- | --- | --- |");
            foreach (var edge in graph.DataFlowEdges)
            {
                sb.AppendLine($"| `{edge.FromComponentId}` | `{edge.ToComponentId}` | `{edge.PathRefId}` |");
            }
            sb.AppendLine();

            sb.AppendLine("## Column-level Lineage Mappings");
            sb.AppendLine("| Package | Task | Source Component | Source Column | Destination Component | Destination Column | Operation |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");

            foreach (var map in graph.ColumnMappings)
            {
                var pkgName = graph.Packages.Find(p => p.Id == map.PackageId)?.Name ?? "Unknown";
                var taskName = graph.Tasks.Find(t => t.Id == map.TaskId)?.Name ?? "Unknown";

                sb.AppendLine($"| {pkgName} | {taskName} | {map.SourceComponentName} | `{map.SourceColumnName}` | {map.TargetComponentName} | `{map.TargetColumnName}` | {map.OperationType} |");
            }

            return RedactSecrets(sb.ToString());
        }

        public static string GenerateHtmlReport(LineageGraph graph)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html>");
            sb.AppendLine("<head>");
            sb.AppendLine("    <title>SSIS Lineage Report</title>");
            sb.AppendLine("    <style>");
            sb.AppendLine("        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background-color: #0f172a; color: #f8fafc; padding: 20px; }");
            sb.AppendLine("        h1, h2 { color: #38bdf8; }");
            sb.AppendLine("        table { width: 100%; border-collapse: collapse; margin-top: 20px; background-color: #1e293b; border-radius: 8px; overflow: hidden; }");
            sb.AppendLine("        th, td { padding: 12px 16px; text-align: left; border-bottom: 1px solid #334155; }");
            sb.AppendLine("        th { background-color: #334155; color: #38bdf8; }");
            sb.AppendLine("        tr:hover { background-color: #475569; }");
            sb.AppendLine("        .badge { background-color: #0284c7; color: white; padding: 2px 6px; border-radius: 4px; font-size: 0.85em; }");
            sb.AppendLine("        .summary-grid { display: grid; grid-template-columns: repeat(5, minmax(120px, 1fr)); gap: 12px; margin: 20px 0; }");
            sb.AppendLine("        .summary-grid div { background: #1e293b; border: 1px solid #334155; border-radius: 8px; padding: 14px; }");
            sb.AppendLine("        .summary-grid strong { display: block; font-size: 28px; color: #f8fafc; }");
            sb.AppendLine("        .summary-grid span { color: #94a3b8; font-size: 13px; font-weight: 700; }");
            sb.AppendLine("        code { color: #bae6fd; white-space: pre-wrap; overflow-wrap: anywhere; }");
            sb.AppendLine("    </style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("    <h1>SSIS Project Lineage Documentation</h1>");
            sb.AppendLine($"    <p>Generated on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>");

            sb.AppendLine("    <h2>Packages Included</h2>");
            sb.AppendLine("    <ul>");
            foreach (var pkg in graph.Packages)
            {
                sb.AppendLine($"        <li><strong>{pkg.Name}</strong> (GUID: {pkg.Id})</li>");
            }
            sb.AppendLine("    </ul>");

            sb.AppendLine("    <h2>Run Summary</h2>");
            sb.AppendLine("    <div class=\"summary-grid\">");
            sb.AppendLine($"        <div><strong>{graph.Packages.Count}</strong><span>Packages</span></div>");
            sb.AppendLine($"        <div><strong>{graph.Tasks.Count}</strong><span>Tasks</span></div>");
            sb.AppendLine($"        <div><strong>{graph.Components.Count}</strong><span>Components</span></div>");
            sb.AppendLine($"        <div><strong>{graph.DataFlowEdges.Count}</strong><span>Data Paths</span></div>");
            sb.AppendLine($"        <div><strong>{graph.ColumnMappings.Count}</strong><span>Column Mappings</span></div>");
            sb.AppendLine("    </div>");

            sb.AppendLine("    <h2>Tasks</h2>");
            sb.AppendLine("    <table>");
            sb.AppendLine("        <thead><tr><th>Package</th><th>Task</th><th>Type</th></tr></thead>");
            sb.AppendLine("        <tbody>");
            foreach (var task in graph.Tasks)
            {
                sb.AppendLine($"            <tr><td>{task.PackageName}</td><td>{task.Name}</td><td><code>{task.Type}</code></td></tr>");
            }
            sb.AppendLine("        </tbody>");
            sb.AppendLine("    </table>");

            sb.AppendLine("    <h2>Data Flow Components</h2>");
            sb.AppendLine("    <table>");
            sb.AppendLine("        <thead><tr><th>Package</th><th>Task</th><th>Component</th><th>Type</th><th>Connection</th><th>SQL / Table</th></tr></thead>");
            sb.AppendLine("        <tbody>");
            foreach (var component in graph.Components)
            {
                var pkgName = graph.Packages.Find(p => p.Id == component.PackageId)?.Name ?? "Unknown";
                var taskName = graph.Tasks.Find(t => t.Id == component.TaskId)?.Name ?? "";
                sb.AppendLine("            <tr>");
                sb.AppendLine($"                <td>{pkgName}</td>");
                sb.AppendLine($"                <td>{taskName}</td>");
                sb.AppendLine($"                <td>{component.Name}</td>");
                sb.AppendLine($"                <td><code>{component.Type}</code></td>");
                sb.AppendLine($"                <td><code>{component.ConnectionManager}</code></td>");
                sb.AppendLine($"                <td><code>{component.SqlQueryOrTable}</code></td>");
                sb.AppendLine("            </tr>");
            }
            sb.AppendLine("        </tbody>");
            sb.AppendLine("    </table>");

            sb.AppendLine("    <h2>Data Flow Paths</h2>");
            sb.AppendLine("    <table>");
            sb.AppendLine("        <thead><tr><th>From Component</th><th>To Component</th><th>Path</th></tr></thead>");
            sb.AppendLine("        <tbody>");
            foreach (var edge in graph.DataFlowEdges)
            {
                sb.AppendLine($"            <tr><td><code>{edge.FromComponentId}</code></td><td><code>{edge.ToComponentId}</code></td><td><code>{edge.PathRefId}</code></td></tr>");
            }
            sb.AppendLine("        </tbody>");
            sb.AppendLine("    </table>");

            sb.AppendLine("    <h2>Column Lineage Mappings</h2>");
            sb.AppendLine("    <table>");
            sb.AppendLine("        <thead>");
            sb.AppendLine("            <tr>");
            sb.AppendLine("                <th>Package</th>");
            sb.AppendLine("                <th>Task</th>");
            sb.AppendLine("                <th>Source Component</th>");
            sb.AppendLine("                <th>Source Column</th>");
            sb.AppendLine("                <th>Destination Component</th>");
            sb.AppendLine("                <th>Destination Column</th>");
            sb.AppendLine("                <th>Operation</th>");
            sb.AppendLine("            </tr>");
            sb.AppendLine("        </thead>");
            sb.AppendLine("        <tbody>");

            foreach (var map in graph.ColumnMappings)
            {
                var pkgName = graph.Packages.Find(p => p.Id == map.PackageId)?.Name ?? "Unknown";
                var taskName = graph.Tasks.Find(t => t.Id == map.TaskId)?.Name ?? "Unknown";

                sb.AppendLine("            <tr>");
                sb.AppendLine($"                <td>{pkgName}</td>");
                sb.AppendLine($"                <td>{taskName}</td>");
                sb.AppendLine($"                <td>{map.SourceComponentName}</td>");
                sb.AppendLine($"                <td><code>{map.SourceColumnName}</code></td>");
                sb.AppendLine($"                <td>{map.TargetComponentName}</td>");
                sb.AppendLine($"                <td><code>{map.TargetColumnName}</code></td>");
                sb.AppendLine($"                <td><span class=\"badge\">{map.OperationType}</span></td>");
                sb.AppendLine("            </tr>");
            }

            sb.AppendLine("        </tbody>");
            sb.AppendLine("    </table>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            return RedactSecrets(sb.ToString());
        }

        /// <summary>
        /// Generates an HTML fragment (no document wrapper) suitable for embedding inside a Blazor page.
        /// Contains the same tables and sections as the full report but without DOCTYPE/html/head/body/style tags.
        /// </summary>
        public static string GenerateColumnLineageCsv(LineageGraph graph)
        {
            var sb = new StringBuilder();
            // ProcedureName  = schema.proc for SQL_PROC rows, blank otherwise
            // JoinCondition  = actual SQL JOIN conditions from AST (usually blank until parser extracts them)
            // FilterConditions = WHERE-clause conditions
            // OperationType shows the full operation (e.g. SQL_PROC_INSERT, OLEDB_DEST, DERIVED)
            sb.AppendLine("Level,ProcedureName,PackageName,TaskName," +
                          "SourceServer,SourceDatabase,SourceSchema,SourceTable,SourceColumn,SourceExpression," +
                          "TargetServer,TargetDatabase,TargetSchema,TargetTable,TargetColumn," +
                          "OperationType,FilterConditions,JoinCondition");
            // Step grouping: mappings in the same table-level stage share one number,
            // ordered source→target — a stage label, not a serial row counter.
            var stepNumbers = new LineageTracer(graph).GetMappingSteps();
            var ordered = graph.ColumnMappings
                .Select((map, i) => (Map: map, Step: stepNumbers[i]))
                .OrderBy(x => x.Step)
                .ToList();

            foreach (var (map, step) in ordered)
            {
                var pkgName  = graph.Packages.Find(p => p.Id == map.PackageId)?.Name ?? "";
                var taskName = graph.Tasks.Find(t => t.Id == map.TaskId)?.Name ?? "";
                var procedure = map.ProcedureName;   // actual procedure name (blank for non-SQL_PROC rows)

                // Derive source table/schema from component name when individual fields are empty (XML_FALLBACK rows)
                var srcSchema = map.SourceSchema;
                var srcTable  = map.SourceTable;
                if (string.IsNullOrEmpty(srcTable) && !string.IsNullOrEmpty(map.SourceComponentName))
                {
                    var parts = map.SourceComponentName.Split('.', 2);
                    if (parts.Length == 2) { srcSchema = parts[0]; srcTable = parts[1]; }
                    else srcTable = map.SourceComponentName;
                }
                var tgtSchema = map.TargetSchema;
                var tgtTable  = map.TargetTable;
                if (string.IsNullOrEmpty(tgtTable) && !string.IsNullOrEmpty(map.TargetComponentName))
                {
                    var parts = map.TargetComponentName.Split('.', 2);
                    if (parts.Length == 2) { tgtSchema = parts[0]; tgtTable = parts[1]; }
                    else tgtTable = map.TargetComponentName;
                }

                sb.AppendLine(string.Join(",",
                    CsvEscape(step.ToString()),
                    CsvEscape(procedure),
                    CsvEscape(pkgName),
                    CsvEscape(taskName),
                    CsvEscape(map.SourceServer),
                    CsvEscape(map.SourceDatabase),
                    CsvEscape(srcSchema),
                    CsvEscape(srcTable),
                    CsvEscape(map.SourceColumnName),
                    CsvEscape(map.SourceExpression),
                    CsvEscape(map.TargetServer),
                    CsvEscape(map.TargetDatabase),
                    CsvEscape(tgtSchema),
                    CsvEscape(tgtTable),
                    CsvEscape(map.TargetColumnName),
                    CsvEscape(map.OperationType),
                    CsvEscape(map.FilterConditions),
                    CsvEscape(map.JoinDetails)));
            }

            return RedactSecrets(sb.ToString());
        }

        /// <summary>
        /// Flattens a traced lineage path (<see cref="TraceResult"/>) into CSV — one row per hop,
        /// ordered source→target. Join Condition is the last column to match the other exports.
        /// </summary>
        public static string GenerateTraceCsv(TraceResult trace)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Step,SourceServer,SourceDatabase,SourceSchema,SourceTable,SourceColumn," +
                          "Operation,Rename,Expression,FilterConditions," +
                          "TargetServer,TargetDatabase,TargetSchema,TargetTable,TargetColumn," +
                          "Package,Task,Procedure,JoinCondition");

            foreach (var s in trace.Steps)
            {
                // Step is a stage grouping shared by rows of the same source→target stage.
                sb.AppendLine(string.Join(",",
                    CsvEscape((s.Rank + 1).ToString()),
                    CsvEscape(s.SourceServer),
                    CsvEscape(s.SourceDatabase),
                    CsvEscape(s.SourceSchema),
                    CsvEscape(s.SourceTable),
                    CsvEscape(s.SourceColumn),
                    CsvEscape(s.Operation),
                    CsvEscape(s.IsRename ? "Yes" : ""),
                    CsvEscape(s.Expression),
                    CsvEscape(s.FilterConditions),
                    CsvEscape(s.TargetServer),
                    CsvEscape(s.TargetDatabase),
                    CsvEscape(s.TargetSchema),
                    CsvEscape(s.TargetTable),
                    CsvEscape(s.TargetColumn),
                    CsvEscape(s.PackageName),
                    CsvEscape(s.TaskName),
                    CsvEscape(s.ProcedureName),
                    CsvEscape(s.JoinDetails)));
            }

            return RedactSecrets(sb.ToString());
        }

        public static string GenerateHtmlFragment(LineageGraph graph)
        {
            var sb = new StringBuilder();

            sb.AppendLine("<div class=\"report-toolbar\">");
            sb.AppendLine("  <input type=\"search\" id=\"report-search\" class=\"report-search\" placeholder=\"Filter report sections and tables…\" oninput=\"window.filterReportSections && window.filterReportSections(this.value)\" />");
            sb.AppendLine("</div>");

            if (graph.Warnings.Count > 0)
            {
                sb.AppendLine("<details class=\"report-section\" id=\"warnings\" open>");
                sb.AppendLine($"  <summary>Warnings ({graph.Warnings.Count})</summary>");
                sb.AppendLine("  <ul class=\"report-list\">");
                foreach (var warning in graph.Warnings)
                {
                    sb.AppendLine($"    <li>{HtmlEncode(warning)}</li>");
                }
                sb.AppendLine("  </ul>");
                sb.AppendLine("</details>");
            }

            sb.AppendLine("<div class=\"report-summary-grid\">");
            sb.AppendLine($"    <div class=\"report-metric\"><strong>{graph.Packages.Count}</strong><span>Packages</span></div>");
            sb.AppendLine($"    <div class=\"report-metric\"><strong>{graph.Tasks.Count}</strong><span>Tasks</span></div>");
            sb.AppendLine($"    <div class=\"report-metric\"><strong>{graph.Components.Count}</strong><span>Components</span></div>");
            sb.AppendLine($"    <div class=\"report-metric\"><strong>{graph.DataFlowEdges.Count}</strong><span>Data Paths</span></div>");
            sb.AppendLine($"    <div class=\"report-metric\"><strong>{graph.ColumnMappings.Count}</strong><span>Column Mappings</span></div>");
            sb.AppendLine($"    <div class=\"report-metric\"><strong>{graph.ExecutionEdges.Count}</strong><span>Execution Edges</span></div>");
            sb.AppendLine("</div>");

            sb.AppendLine("<details class=\"report-section\" id=\"packages\" open>");
            sb.AppendLine($"  <summary>Packages ({graph.Packages.Count})</summary>");
            sb.AppendLine("  <ul class=\"report-list\">");
            foreach (var pkg in graph.Packages)
            {
                sb.AppendLine($"    <li><strong>{HtmlEncode(pkg.Name)}</strong> <code>{HtmlEncode(pkg.Id)}</code></li>");
            }
            sb.AppendLine("  </ul>");
            sb.AppendLine("</details>");

            sb.AppendLine("<details class=\"report-section\" id=\"tasks\" open>");
            sb.AppendLine($"  <summary>Tasks ({graph.Tasks.Count})</summary>");
            sb.AppendLine("<div class=\"report-table-wrapper\">");
            sb.AppendLine("<table class=\"report-table\">");
            sb.AppendLine("    <thead><tr><th>Package</th><th>Task</th><th>Type</th></tr></thead>");
            sb.AppendLine("    <tbody>");
            foreach (var task in graph.Tasks)
            {
                sb.AppendLine($"        <tr><td>{HtmlEncode(task.PackageName)}</td><td>{HtmlEncode(task.Name)}</td><td><code>{HtmlEncode(task.Type)}</code></td></tr>");
            }
            sb.AppendLine("    </tbody>");
            sb.AppendLine("</table>");
            sb.AppendLine("</div>");
            sb.AppendLine("</details>");

            sb.AppendLine("<details class=\"report-section\" id=\"data-flow-components\" open>");
            sb.AppendLine($"  <summary>Data Flow Components ({graph.Components.Count})</summary>");
            sb.AppendLine("<div class=\"report-table-wrapper\">");
            sb.AppendLine("<table class=\"report-table\">");
            sb.AppendLine("    <thead><tr><th>Package</th><th>Task</th><th>Component</th><th>Type</th><th>Connection</th><th>SQL / Table</th></tr></thead>");
            sb.AppendLine("    <tbody>");
            foreach (var component in graph.Components)
            {
                var pkgName = graph.Packages.Find(p => p.Id == component.PackageId)?.Name ?? "Unknown";
                var taskName = graph.Tasks.Find(t => t.Id == component.TaskId)?.Name ?? "";
                sb.AppendLine("        <tr>");
                sb.AppendLine($"            <td>{HtmlEncode(pkgName)}</td>");
                sb.AppendLine($"            <td>{HtmlEncode(taskName)}</td>");
                sb.AppendLine($"            <td>{HtmlEncode(component.Name)}</td>");
                sb.AppendLine($"            <td><code>{HtmlEncode(component.Type)}</code></td>");
                sb.AppendLine($"            <td><code>{HtmlEncode(component.ConnectionManager)}</code></td>");
                sb.AppendLine($"            <td><code>{HtmlEncode(component.SqlQueryOrTable)}</code></td>");
                sb.AppendLine("        </tr>");
            }
            sb.AppendLine("    </tbody>");
            sb.AppendLine("</table>");
            sb.AppendLine("</div>");
            sb.AppendLine("</details>");

            if (graph.DataFlowEdges.Count > 0)
            {
                sb.AppendLine("<details class=\"report-section\" id=\"data-flow-paths\" open>");
                sb.AppendLine($"  <summary>Data Flow Paths ({graph.DataFlowEdges.Count})</summary>");
                sb.AppendLine("<div class=\"report-table-wrapper\">");
                sb.AppendLine("<table class=\"report-table\">");
                sb.AppendLine("    <thead><tr><th>From</th><th>To</th><th>Path</th></tr></thead>");
                sb.AppendLine("    <tbody>");
                foreach (var edge in graph.DataFlowEdges)
                {
                    var fromName = graph.Components.Find(c => c.Id == edge.FromComponentId)?.Name ?? edge.FromComponentId;
                    var toName = graph.Components.Find(c => c.Id == edge.ToComponentId)?.Name ?? edge.ToComponentId;
                    sb.AppendLine($"        <tr><td>{HtmlEncode(fromName)}</td><td>{HtmlEncode(toName)}</td><td><code>{HtmlEncode(edge.PathRefId)}</code></td></tr>");
                }
                sb.AppendLine("    </tbody>");
                sb.AppendLine("</table>");
                sb.AppendLine("</div>");
                sb.AppendLine("</details>");
            }

            if (graph.ExecutionEdges.Count > 0)
            {
                sb.AppendLine("<details class=\"report-section\" id=\"execution-flow\" open>");
                sb.AppendLine($"  <summary>Execution Flow ({graph.ExecutionEdges.Count})</summary>");
                sb.AppendLine("<div class=\"report-table-wrapper\">");
                sb.AppendLine("<table class=\"report-table\">");
                sb.AppendLine("    <thead><tr><th>From Task</th><th>To Task</th><th>Constraint</th><th>Expression</th></tr></thead>");
                sb.AppendLine("    <tbody>");
                foreach (var edge in graph.ExecutionEdges)
                {
                    var fromName = graph.Tasks.Find(t => t.Id == edge.FromTaskId)?.Name ?? edge.FromTaskId;
                    var toName = graph.Tasks.Find(t => t.Id == edge.ToTaskId)?.Name ?? edge.ToTaskId;
                    sb.AppendLine("        <tr>");
                    sb.AppendLine($"            <td>{HtmlEncode(fromName)}</td>");
                    sb.AppendLine($"            <td>{HtmlEncode(toName)}</td>");
                    sb.AppendLine($"            <td><span class=\"badge\">{HtmlEncode(edge.PrecedenceConstraintValue)}</span></td>");
                    sb.AppendLine($"            <td><code>{HtmlEncode(edge.Expression)}</code></td>");
                    sb.AppendLine("        </tr>");
                }
                sb.AppendLine("    </tbody>");
                sb.AppendLine("</table>");
                sb.AppendLine("</div>");
                sb.AppendLine("</details>");
            }

            if (graph.ColumnMappings.Count > 0)
            {
                sb.AppendLine("<details class=\"report-section\" id=\"column-mappings\" open>");
                sb.AppendLine($"  <summary>Column Lineage Mappings ({graph.ColumnMappings.Count})</summary>");
                sb.AppendLine("<div class=\"report-table-wrapper\">");
                sb.AppendLine("<table class=\"report-table\">");
                sb.AppendLine("    <thead>");
                sb.AppendLine("        <tr>");
                sb.AppendLine("            <th>Package</th>");
                sb.AppendLine("            <th>Task</th>");
                sb.AppendLine("            <th>Source</th>");
                sb.AppendLine("            <th>Source Column</th>");
                sb.AppendLine("            <th>Destination</th>");
                sb.AppendLine("            <th>Dest Column</th>");
                sb.AppendLine("            <th>Operation</th>");
                sb.AppendLine("        </tr>");
                sb.AppendLine("    </thead>");
                sb.AppendLine("    <tbody>");

                foreach (var map in graph.ColumnMappings)
                {
                    var pkgName = graph.Packages.Find(p => p.Id == map.PackageId)?.Name ?? "Unknown";
                    var taskName = graph.Tasks.Find(t => t.Id == map.TaskId)?.Name ?? "Unknown";

                    sb.AppendLine("        <tr>");
                    sb.AppendLine($"            <td>{HtmlEncode(pkgName)}</td>");
                    sb.AppendLine($"            <td>{HtmlEncode(taskName)}</td>");
                    sb.AppendLine($"            <td>{HtmlEncode(map.SourceComponentName)}</td>");
                    sb.AppendLine($"            <td><code>{HtmlEncode(map.SourceColumnName)}</code></td>");
                    sb.AppendLine($"            <td>{HtmlEncode(map.TargetComponentName)}</td>");
                    sb.AppendLine($"            <td><code>{HtmlEncode(map.TargetColumnName)}</code></td>");
                    sb.AppendLine($"            <td><span class=\"badge\">{HtmlEncode(map.OperationType)}</span></td>");
                    sb.AppendLine("        </tr>");
                }

                sb.AppendLine("    </tbody>");
                sb.AppendLine("</table>");
                sb.AppendLine("</div>");
                sb.AppendLine("</details>");
            }

            sb.AppendLine("<script>");
            sb.AppendLine("window.filterReportSections = function (query) {");
            sb.AppendLine("  const q = (query || '').trim().toLowerCase();");
            sb.AppendLine("  document.querySelectorAll('.report-section').forEach(section => {");
            sb.AppendLine("    const text = section.textContent.toLowerCase();");
            sb.AppendLine("    section.style.display = !q || text.includes(q) ? '' : 'none';");
            sb.AppendLine("  });");
            sb.AppendLine("  document.querySelectorAll('.report-table tbody tr').forEach(row => {");
            sb.AppendLine("    const text = row.textContent.toLowerCase();");
            sb.AppendLine("    row.style.display = !q || text.includes(q) ? '' : 'none';");
            sb.AppendLine("  });");
            sb.AppendLine("};");
            sb.AppendLine("</script>");

            return RedactSecrets(sb.ToString());
        }

        // ── secret redaction ─────────────────────────────────────────────────

        private static readonly System.Text.RegularExpressions.Regex SecretRegex = new(
            @"(?i)\b(password|pwd|accountkey|account key|sharedaccesskey|secret|apikey|api key|token)\s*=\s*[^;""'\r\n}\]]+",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Scrubs credential values (Password=…, PWD=…, AccountKey=…, etc.) from any export
        /// text so connection-string secrets never leak into shared lineage outputs.
        /// </summary>
        public static string RedactSecrets(string content)
        {
            if (string.IsNullOrEmpty(content)) return content;
            return SecretRegex.Replace(content, m =>
                $"{m.Value[..m.Value.IndexOf('=')]}=***REDACTED***");
        }

        // ── Mermaid export ───────────────────────────────────────────────────

        /// <summary>
        /// Table-level lineage as a Mermaid flowchart — renders directly in GitHub
        /// READMEs, wikis, and docs. One node per table/stage, one edge per distinct
        /// source→target flow labelled with its operation.
        /// </summary>
        public static string GenerateMermaid(LineageGraph graph)
        {
            var nodeIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var edges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            sb.AppendLine("flowchart LR");

            string NodeId(string label)
            {
                if (!nodeIds.TryGetValue(label, out var id))
                {
                    id = $"n{nodeIds.Count}";
                    nodeIds[label] = id;
                    sb.AppendLine($"    {id}[\"{label.Replace("\"", "'")}\"]");
                }
                return id;
            }

            static string SideLabel(string schema, string table, string componentName)
            {
                if (string.IsNullOrEmpty(table) && !string.IsNullOrEmpty(componentName))
                {
                    var parts = componentName.Split('.', 2);
                    if (parts.Length == 2) { schema = parts[0]; table = parts[1]; }
                    else table = componentName;
                }
                return string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";
            }

            static string SimplifyOp(string op)
            {
                if (string.IsNullOrEmpty(op)) return "";
                var s = op.StartsWith("SQL_PROC_", StringComparison.OrdinalIgnoreCase)
                    ? op["SQL_PROC_".Length..] : op;
                return s == "XML_FALLBACK" ? "DATA FLOW" : s.Replace('_', ' ');
            }

            foreach (var map in graph.ColumnMappings)
            {
                var src = SideLabel(map.SourceSchema, map.SourceTable, map.SourceComponentName);
                var tgt = SideLabel(map.TargetSchema, map.TargetTable, map.TargetComponentName);
                if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(tgt) ||
                    string.Equals(src, tgt, StringComparison.OrdinalIgnoreCase))
                    continue;

                var op = SimplifyOp(map.OperationType);
                var key = $"{src}|{tgt}|{op}";
                if (!edges.Add(key)) continue;

                var line = string.IsNullOrEmpty(op)
                    ? $"    {NodeId(src)} --> {NodeId(tgt)}"
                    : $"    {NodeId(src)} -->|{op}| {NodeId(tgt)}";
                sb.AppendLine(line);
            }

            return RedactSecrets(sb.ToString());
        }

        // ── OpenLineage export ───────────────────────────────────────────────

        /// <summary>
        /// Emits OpenLineage 1.x COMPLETE run events (one per task that produced column
        /// mappings) with columnLineage facets, for ingestion into Marquez, Microsoft
        /// Purview (via OpenLineage connectors), DataHub, and similar catalogs.
        /// </summary>
        public static string GenerateOpenLineage(LineageGraph graph, string producer = "https://github.com/okutue/SSIS-Project-Documentation")
        {
            static string SideLabel(string schema, string table, string componentName)
            {
                if (string.IsNullOrEmpty(table) && !string.IsNullOrEmpty(componentName))
                {
                    var parts = componentName.Split('.', 2);
                    if (parts.Length == 2) { schema = parts[0]; table = parts[1]; }
                    else table = componentName;
                }
                return string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";
            }

            static string Namespace(string server, string database)
            {
                var srv = string.IsNullOrEmpty(server) ? "sqlserver" : server;
                return string.IsNullOrEmpty(database) ? srv : $"sqlserver://{srv}/{database}";
            }

            var eventTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var events = new List<object>();

            foreach (var taskGroup in graph.ColumnMappings.GroupBy(m => m.TaskId))
            {
                var task = graph.Tasks.Find(t => t.Id == taskGroup.Key);
                var pkg = graph.Packages.Find(p => p.Id == (task?.PackageId ?? taskGroup.First().PackageId));
                var jobName = $"{pkg?.Name ?? "package"}.{task?.Name ?? "task"}";

                var inputs = taskGroup
                    .Select(m => new { Ns = Namespace(m.SourceServer, m.SourceDatabase), Name = SideLabel(m.SourceSchema, m.SourceTable, m.SourceComponentName) })
                    .Where(x => !string.IsNullOrEmpty(x.Name))
                    .DistinctBy(x => $"{x.Ns}|{x.Name}".ToLowerInvariant())
                    .Select(x => (object)new { @namespace = x.Ns, name = x.Name })
                    .ToList();

                var outputs = taskGroup
                    .Where(m => !string.IsNullOrEmpty(SideLabel(m.TargetSchema, m.TargetTable, m.TargetComponentName)))
                    .GroupBy(m => $"{Namespace(m.TargetServer, m.TargetDatabase)}|{SideLabel(m.TargetSchema, m.TargetTable, m.TargetComponentName)}".ToLowerInvariant())
                    .Select(g =>
                    {
                        var first = g.First();
                        var ns = Namespace(first.TargetServer, first.TargetDatabase);
                        var name = SideLabel(first.TargetSchema, first.TargetTable, first.TargetComponentName);
                        var fields = g
                            .Where(m => !string.IsNullOrEmpty(m.TargetColumnName) && m.TargetColumnName != "*")
                            .GroupBy(m => m.TargetColumnName, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(
                                fg => fg.Key,
                                fg => (object)new
                                {
                                    inputFields = fg
                                        .Where(m => !string.IsNullOrEmpty(m.SourceColumnName))
                                        .Select(m => (object)new
                                        {
                                            @namespace = Namespace(m.SourceServer, m.SourceDatabase),
                                            name = SideLabel(m.SourceSchema, m.SourceTable, m.SourceComponentName),
                                            field = m.SourceColumnName
                                        })
                                        .ToList()
                                });

                        return (object)new
                        {
                            @namespace = ns,
                            name,
                            facets = new
                            {
                                columnLineage = new
                                {
                                    _producer = producer,
                                    _schemaURL = "https://openlineage.io/spec/facets/1-0-1/ColumnLineageDatasetFacet.json",
                                    fields
                                }
                            }
                        };
                    })
                    .ToList();

                events.Add(new
                {
                    eventType = "COMPLETE",
                    eventTime,
                    producer,
                    schemaURL = "https://openlineage.io/spec/1-0-5/OpenLineage.json",
                    run = new { runId = Guid.NewGuid().ToString() },
                    job = new { @namespace = $"ssis://{pkg?.Name ?? "project"}", name = jobName },
                    inputs,
                    outputs
                });
            }

            var json = JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true });
            return RedactSecrets(json);
        }

        private static string CsvEscape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace("'", "\\'").Replace("\r", "").Replace("\n", "\\n");
        }

        private static string HtmlEncode(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return System.Net.WebUtility.HtmlEncode(value);
        }
    }
}
