using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SsisLineage.Core;
using SsisLineage.Core.Models;

namespace SsisLineage.UI.Services
{
    public class ColumnLineageRow
    {
        public int Level { get; set; }
        public string ProcedureName { get; set; } = "";
        public string SSISPackage { get; set; } = "";
        public string TaskName { get; set; } = "";
        public string ColumnName { get; set; } = "";
        public string SourceServer { get; set; } = "";
        public string SourceDatabase { get; set; } = "";
        public string SourceSchema { get; set; } = "";
        public string SourceTables { get; set; } = "";
        public string SourceColumns { get; set; } = "";
        public string SourceExpression { get; set; } = "";
        public string TargetServer { get; set; } = "";
        public string TargetDatabase { get; set; } = "";
        public string TargetSchema { get; set; } = "";
        public string TargetTable { get; set; } = "";
        public string TargetColumn { get; set; } = "";
        public string OperationType { get; set; } = "";
        public string JoinCondition { get; set; } = "";
        public string FilterConditions { get; set; } = "";
    }

    public class LineageReportData
    {
        public string HtmlFragment { get; set; } = "";
        public LineageGraph Graph { get; set; } = new();
        public Dictionary<string, object> Summary { get; set; } = new();
        public List<ColumnLineageRow> ColumnLineage { get; set; } = new();
        public string JsonExport { get; set; } = "";
        public string YamlExport { get; set; } = "";
        public string CypherExport { get; set; } = "";
        public string CsvExport { get; set; } = "";
        public string MarkdownExport { get; set; } = "";
        public string MermaidExport { get; set; } = "";
        public string OpenLineageExport { get; set; } = "";
        /// <summary>Inputs that produced this report — restored into the form (and used for
        /// the entry-package highlight) when returning to the page after navigation.</summary>
        public string ProjectPath { get; set; } = "";
        public string StartPackage { get; set; } = "";
    }

    public class LineageService
    {
        private readonly LineageUserSession _session;
        private readonly LineageReportStore _reportStore;

        public LineageService(LineageUserSession session, LineageReportStore reportStore)
        {
            _session = session;
            _reportStore = reportStore;
        }

        public LineageReportData? CurrentReportData { get; private set; }

        public void PublishReport(LineageReportData report)
        {
            CurrentReportData = report;
            _reportStore.Set(_session.SessionKey, report);
        }

        public LineageReportData? GetPublishedReport()
        {
            return CurrentReportData ?? _reportStore.Get(_session.SessionKey);
        }

        public LineageReportData GenerateLineageReport(string projectPath, string startPackage,
            bool useCache = true, bool includeSqlProcedures = false, string sqlConnectionString = "",
            Dictionary<string, string>? linkedServerMap = null, bool autoResolveLinkedServers = true,
            Dictionary<string, string>? sqlVariableValues = null)
        {
            try
            {
                // Create scan options
                var options = new LineageScanOptions
                {
                    ProjectPath = projectPath,
                    StartPackage = startPackage,
                    UseCache = useCache,
                    IncludeSqlProcedures = includeSqlProcedures,
                    SqlConnectionString = sqlConnectionString,
                    LinkedServerMap = linkedServerMap ?? new Dictionary<string, string>(),
                    AutoResolveLinkedServers = autoResolveLinkedServers,
                    SqlVariableValues = sqlVariableValues ?? new Dictionary<string, string>(),
                    OutputDirectory = Path.Combine(Path.GetTempPath(), "ssis-lineage-temp")
                };

                // Ensure output directory exists
                Directory.CreateDirectory(options.OutputDirectory);

                // Perform scan
                var scanService = new LineageScanService();
                var result = scanService.Scan(options);

                var reportData = BuildReportData(
                    result.Graph,
                    Path.GetFileName(projectPath),
                    result.Project.ProjectDirectory,
                    result.CacheHit,
                    startPackage);
                reportData.ProjectPath = projectPath;

                PublishReport(reportData);
                return reportData;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to generate lineage report: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Builds full report data (summary, column lineage, all export formats) from an
        /// already-available graph — used after a scan AND when loading a saved
        /// .lineage.json report file.
        /// </summary>
        public LineageReportData BuildReportData(LineageGraph graph, string projectName,
            string projectDirectory = "", bool isCached = false, string startPackage = "")
        {
            var columnRows = BuildColumnLineage(graph, startPackage);

            var summary = new Dictionary<string, object>
            {
                { "projectName", projectName },
                { "isCached", isCached },
                { "projectDirectory", projectDirectory },
                { "totalNodes", graph.Packages.Count + graph.Tasks.Count + graph.Components.Count },
                { "totalEdges", graph.DataFlowEdges.Count + graph.ExecutionEdges.Count },
                { "packages", graph.Packages.Count },
                { "tasks", graph.Tasks.Count },
                { "components", graph.Components.Count },
                { "dataFlowEdges", graph.DataFlowEdges.Count },
                { "columnMappings", graph.ColumnMappings.Count },
                { "executionEdges", graph.ExecutionEdges.Count },
                { "warnings", graph.Warnings.Count },
                { "generatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }
            };

            return new LineageReportData
            {
                HtmlFragment = OutputGenerator.GenerateHtmlFragment(graph),
                Graph = graph,
                Summary = summary,
                ColumnLineage = columnRows,
                JsonExport = OutputGenerator.GenerateJson(graph),
                YamlExport = OutputGenerator.GenerateYaml(graph),
                CypherExport = OutputGenerator.GenerateCypher(graph),
                CsvExport = OutputGenerator.GenerateColumnLineageCsv(graph),
                MarkdownExport = OutputGenerator.GenerateMarkdownReport(graph),
                MermaidExport = OutputGenerator.GenerateMermaid(graph),
                OpenLineageExport = OutputGenerator.GenerateOpenLineage(graph),
                ProjectPath = projectDirectory,
                StartPackage = startPackage
            };
        }

        /// <summary>
        /// Restores a report from a previously saved .lineage.json file (the JSON export
        /// of the lineage graph). Returns null when the content is not a valid graph.
        /// </summary>
        public LineageReportData? LoadFromJson(string json, string fileName)
        {
            try
            {
                var graph = System.Text.Json.JsonSerializer.Deserialize<LineageGraph>(json,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (graph == null || (graph.Packages.Count == 0 && graph.ColumnMappings.Count == 0))
                    return null;

                var reportData = BuildReportData(graph, fileName);
                PublishReport(reportData);
                return reportData;
            }
            catch
            {
                return null;
            }
        }

        private static List<ColumnLineageRow> BuildColumnLineage(LineageGraph graph, string startPackage)
        {
            var columnRows = new List<ColumnLineageRow>();
            // Step grouping: rows in the same table-level stage share one number, ordered
            // source→target (e.g. every column row of one Execute SQL INSERT shares a step).
            var steps = new LineageTracer(graph).GetMappingSteps();
            var index = 0;
            foreach (var map in graph.ColumnMappings)
            {
                var pkgName = graph.Packages.Find(p => p.Id == map.PackageId)?.Name
                    ?? Path.GetFileName(startPackage);
                var taskName = graph.Tasks.Find(t => t.Id == map.TaskId)?.Name ?? "";
                // ProcedureName: show actual join condition from JoinDetails only.
                // Do NOT fall back to SourceComponentName — that is the procedure name,
                // not a join condition, and would mislead the column lineage grid.
                var procedure = map.ProcedureName ?? "";  // actual stored-proc name (blank for non-SQL_PROC rows)

                // Derive schema/table from ComponentName when dedicated fields are empty (e.g. XML_FALLBACK)
                var srcSchema = map.SourceSchema;
                var srcTable  = map.SourceTable;
                if (string.IsNullOrEmpty(srcTable))
                {
                    var p = map.SourceComponentName.Split('.', 2);
                    srcSchema = p.Length == 2 ? p[0] : srcSchema;
                    srcTable  = p.Length == 2 ? p[1] : map.SourceComponentName;
                }
                var tgtSchema = map.TargetSchema;
                var tgtTable  = map.TargetTable;
                if (string.IsNullOrEmpty(tgtTable))
                {
                    var p = map.TargetComponentName.Split('.', 2);
                    tgtSchema = p.Length == 2 ? p[0] : tgtSchema;
                    tgtTable  = p.Length == 2 ? p[1] : map.TargetComponentName;
                }

                columnRows.Add(new ColumnLineageRow
                {
                    Level = steps[index++],
                    ProcedureName = procedure,
                    SSISPackage = pkgName,
                    TaskName = taskName,
                    ColumnName = map.TargetColumnName,
                    SourceServer   = map.SourceServer,
                    SourceDatabase = map.SourceDatabase,
                    SourceSchema   = srcSchema,
                    SourceTables   = srcTable,
                    SourceColumns  = map.SourceColumnName,
                    SourceExpression = map.SourceExpression,
                    TargetServer   = map.TargetServer,
                    TargetDatabase = map.TargetDatabase,
                    TargetSchema   = tgtSchema,
                    TargetTable    = tgtTable,
                    TargetColumn   = map.TargetColumnName,
                    OperationType  = string.IsNullOrWhiteSpace(map.OperationType) ? "Data Flow Mapping" : map.OperationType,
                    JoinCondition  = map.JoinDetails,  // actual JOIN conditions from SQL AST
                    FilterConditions = map.FilterConditions
                });
            }

            // Present in step order: source stages first, final targets last.
            return columnRows
                .OrderBy(r => r.Level)
                .ThenBy(r => r.SSISPackage, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.TaskName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.TargetColumn, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
