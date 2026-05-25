using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using SsisLineage.Core.Models;

namespace SsisLineage.UI.Services
{
    /// <summary>
    /// Builds a multi-sheet Excel workbook from the full lineage report,
    /// mirroring the six tabs shown in the Detailed Report page.
    /// </summary>
    public static class ExcelExporter
    {
        public static byte[] GenerateDetailedReport(LineageReportData data)
        {
            var g = data.Graph;

            using var wb = new XLWorkbook();
            wb.Style.Font.FontName = "Segoe UI";
            wb.Style.Font.FontSize = 10;

            AddPackages(wb, g);
            AddTasks(wb, g);
            AddComponents(wb, g);
            AddDataPaths(wb, g);
            AddColumnMappings(wb, data.ColumnLineage, g);
            AddExecutionFlow(wb, g);

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
        }

        // ── helpers ──────────────────────────────────────────────────────────

        private static IXLWorksheet AddSheet(XLWorkbook wb, string name, IReadOnlyList<string> headers)
        {
            var ws = wb.AddWorksheet(name);

            // Header row
            for (var c = 0; c < headers.Count; c++)
            {
                var cell = ws.Cell(1, c + 1);
                cell.Value = headers[c];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1e3a5f");
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            }

            ws.SheetView.FreezeRows(1);
            return ws;
        }

        private static void AutoFit(IXLWorksheet ws)
        {
            ws.Columns().AdjustToContents(8, 60);
        }

        private static string TaskName(LineageGraph g, string id) =>
            g.Tasks.FirstOrDefault(t => t.Id == id)?.Name ?? id;

        private static string PackageName(LineageGraph g, string id) =>
            g.Packages.FirstOrDefault(p => p.Id == id)?.Name ?? id;

        private static string ComponentName(LineageGraph g, string id) =>
            g.Components.FirstOrDefault(c => c.Id == id)?.Name ?? id;

        // ── sheets ───────────────────────────────────────────────────────────

        private static void AddPackages(XLWorkbook wb, LineageGraph g)
        {
            var ws = AddSheet(wb, "Packages", new[] { "Package", "Path", "Variables", "Connection Managers" });
            var row = 2;
            foreach (var p in g.Packages)
            {
                ws.Cell(row, 1).Value = p.Name;
                ws.Cell(row, 2).Value = p.Path;
                ws.Cell(row, 3).Value = p.Variables.Count;
                ws.Cell(row, 4).Value = string.Join(", ", p.ConnectionManagers);
                row++;
            }
            AutoFit(ws);
        }

        private static void AddTasks(XLWorkbook wb, LineageGraph g)
        {
            var ws = AddSheet(wb, "Tasks", new[] { "Task", "Type", "Package", "Description" });
            var row = 2;
            foreach (var t in g.Tasks)
            {
                ws.Cell(row, 1).Value = t.Name;
                ws.Cell(row, 2).Value = t.Type;
                ws.Cell(row, 3).Value = t.PackageName;
                ws.Cell(row, 4).Value = t.Description;
                row++;
            }
            AutoFit(ws);
        }

        private static void AddComponents(XLWorkbook wb, LineageGraph g)
        {
            var ws = AddSheet(wb, "Components", new[] { "Component", "Type", "Task", "Connection", "SQL / Table" });
            var row = 2;
            foreach (var c in g.Components)
            {
                ws.Cell(row, 1).Value = c.Name;
                ws.Cell(row, 2).Value = c.Type;
                ws.Cell(row, 3).Value = TaskName(g, c.TaskId);
                ws.Cell(row, 4).Value = c.ConnectionManager;
                ws.Cell(row, 5).Value = c.SqlQueryOrTable;
                row++;
            }
            AutoFit(ws);
        }

        private static void AddDataPaths(XLWorkbook wb, LineageGraph g)
        {
            var ws = AddSheet(wb, "Data Paths", new[] { "From", "To", "Path Ref" });
            var row = 2;
            foreach (var e in g.DataFlowEdges)
            {
                ws.Cell(row, 1).Value = ComponentName(g, e.FromComponentId);
                ws.Cell(row, 2).Value = ComponentName(g, e.ToComponentId);
                ws.Cell(row, 3).Value = e.PathRefId;
                row++;
            }
            AutoFit(ws);
        }

        private static void AddColumnMappings(XLWorkbook wb, List<ColumnLineageRow> rows, LineageGraph g)
        {
            var ws = AddSheet(wb, "Column Mappings", new[]
            {
                "Step", "Package", "Task", "Procedure",
                "Source Server", "Source DB", "Source Schema", "Source Table", "Source Column", "Expression",
                "Target Server", "Target DB", "Target Schema", "Target Table", "Target Column",
                "Operation", "Filter Conditions", "Join Condition"
            });

            var row = 2;
            foreach (var r in rows)
            {
                ws.Cell(row,  1).Value = r.Level;
                ws.Cell(row,  2).Value = r.SSISPackage;
                ws.Cell(row,  3).Value = r.TaskName;
                ws.Cell(row,  4).Value = r.ProcedureName;
                ws.Cell(row,  5).Value = r.SourceServer;
                ws.Cell(row,  6).Value = r.SourceDatabase;
                ws.Cell(row,  7).Value = r.SourceSchema;
                ws.Cell(row,  8).Value = r.SourceTables;
                ws.Cell(row,  9).Value = r.SourceColumns;
                ws.Cell(row, 10).Value = r.SourceExpression;
                ws.Cell(row, 11).Value = r.TargetServer;
                ws.Cell(row, 12).Value = r.TargetDatabase;
                ws.Cell(row, 13).Value = r.TargetSchema;
                ws.Cell(row, 14).Value = r.TargetTable;
                ws.Cell(row, 15).Value = r.TargetColumn;
                ws.Cell(row, 16).Value = r.OperationType;
                ws.Cell(row, 17).Value = r.FilterConditions;
                ws.Cell(row, 18).Value = r.JoinCondition;
                row++;
            }
            AutoFit(ws);
        }

        private static void AddExecutionFlow(XLWorkbook wb, LineageGraph g)
        {
            var ws = AddSheet(wb, "Execution Flow", new[] { "From", "To", "Constraint", "Expression" });
            var row = 2;
            foreach (var e in g.ExecutionEdges)
            {
                var toName = TaskName(g, e.ToTaskId);
                if (toName == e.ToTaskId) toName = PackageName(g, e.ToTaskId);
                ws.Cell(row, 1).Value = TaskName(g, e.FromTaskId);
                ws.Cell(row, 2).Value = toName;
                ws.Cell(row, 3).Value = e.PrecedenceConstraintValue;
                ws.Cell(row, 4).Value = e.Expression;
                row++;
            }
            AutoFit(ws);
        }
    }
}
