using SsisLineage.Core;
using SsisLineage.Core.Models;

namespace SsisLineage.Tests;

public class OutputGeneratorHtmlFragmentTests
{
    [Fact]
    public void GenerateHtmlFragment_includes_anchor_ids_for_detailed_report_navigation()
    {
        var graph = new LineageGraph
        {
            Packages = { new PackageNode { Id = "pkg1", Name = "Pkg1", Path = "Pkg1.dtsx" } },
            Tasks = { new TaskNode { Id = "tsk1", Name = "DFT", Type = "Data Flow Task", PackageId = "pkg1", PackageName = "Pkg1" } },
            Components =
            {
                new ComponentNode { Id = "cmp1", Name = "Src", Type = "Source", PackageId = "pkg1", TaskId = "tsk1" },
                new ComponentNode { Id = "cmp2", Name = "Dst", Type = "Destination", PackageId = "pkg1", TaskId = "tsk1" }
            },
            DataFlowEdges =
            {
                new DataFlowEdge { FromComponentId = "cmp1", ToComponentId = "cmp2", PathRefId = "path1" }
            },
            ColumnMappings =
            {
                new ColumnMap
                {
                    SourceComponentId = "cmp1",
                    TargetComponentId = "cmp2",
                    SourceComponentName = "Src",
                    TargetComponentName = "Dst",
                    SourceColumnName = "A",
                    TargetColumnName = "B"
                }
            },
            ExecutionEdges =
            {
                new ExecutionEdge { FromTaskId = "tsk1", ToTaskId = "tsk1", PrecedenceConstraintValue = "Success" }
            }
        };

        var html = OutputGenerator.GenerateHtmlFragment(graph);

        Assert.Contains("id=\"packages\"", html);
        Assert.Contains("id=\"tasks\"", html);
        Assert.Contains("id=\"data-flow-components\"", html);
        Assert.Contains("id=\"data-flow-paths\"", html);
        Assert.Contains("id=\"column-mappings\"", html);
        Assert.Contains("id=\"execution-flow\"", html);
        Assert.Contains("report-search", html);
        Assert.Contains("<details class=\"report-section\"", html);
        Assert.Contains("filterReportSections", html);
    }

    [Fact]
    public void GenerateColumnLineageCsv_includes_all_mappings()
    {
        var graph = new LineageGraph
        {
            ColumnMappings =
            {
                new ColumnMap { SourceComponentName = "A", SourceColumnName = "c1", TargetComponentName = "B", TargetColumnName = "c2", OperationType = "MAP" },
                new ColumnMap { SourceComponentName = "B", SourceColumnName = "c2", TargetComponentName = "C", TargetColumnName = "c3", OperationType = "MAP" }
            }
        };

        var csv = OutputGenerator.GenerateColumnLineageCsv(graph);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        // Header carries the expanded source/target server-database-schema-table-column schema
        Assert.Contains("SourceServer", lines[0]);
        Assert.Contains("SourceSchema", lines[0]);
        Assert.Contains("SourceTable", lines[0]);
        Assert.Contains("SourceColumn", lines[0]);
        Assert.Contains("TargetTable", lines[0]);
        Assert.Contains("TargetColumn", lines[0]);
    }
}
