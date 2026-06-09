using System.Linq;
using SsisLineage.Core;
using SsisLineage.Core.Models;

namespace SsisLineage.Tests;

public class RealWorldImprovementsTests
{
    private static LineageGraph BuildChain() => new()
    {
        ColumnMappings =
        {
            new ColumnMap
            {
                SourceComponentName = "A", SourceColumnName = "c1",
                TargetComponentName = "B", TargetColumnName = "c2", OperationType = "INSERT"
            },
            new ColumnMap
            {
                SourceComponentName = "B", SourceColumnName = "c2",
                TargetComponentName = "C", TargetColumnName = "c3", OperationType = "INSERT"
            }
        }
    };

    // ── trace direction ────────────────────────────────────────────────────

    [Fact]
    public void Downstream_trace_returns_impact_only()
    {
        var tracer = new LineageTracer(BuildChain());
        var hit = tracer.Search("B.c2", SearchScope.Column).Single();

        var impact = tracer.Trace(hit, TraceDirection.Downstream);

        var step = Assert.Single(impact.Steps);
        Assert.Equal("B", step.SourceTable);
        Assert.Equal("C", step.TargetTable);
    }

    [Fact]
    public void Upstream_trace_returns_origins_only()
    {
        var tracer = new LineageTracer(BuildChain());
        var hit = tracer.Search("B.c2", SearchScope.Column).Single();

        var origins = tracer.Trace(hit, TraceDirection.Upstream);

        var step = Assert.Single(origins.Steps);
        Assert.Equal("A", step.SourceTable);
        Assert.Equal("B", step.TargetTable);
    }

    // ── lineage diff ───────────────────────────────────────────────────────

    [Fact]
    public void Diff_detects_added_and_removed_mappings()
    {
        var oldGraph = BuildChain();
        var newGraph = new LineageGraph
        {
            ColumnMappings =
            {
                new ColumnMap
                {
                    SourceComponentName = "A", SourceColumnName = "c1",
                    TargetComponentName = "B", TargetColumnName = "c2", OperationType = "INSERT"
                },
                // B→C removed; B→D added
                new ColumnMap
                {
                    SourceComponentName = "B", SourceColumnName = "c2",
                    TargetComponentName = "D", TargetColumnName = "c4", OperationType = "INSERT"
                }
            }
        };

        var diff = LineageDiff.Compare(oldGraph, newGraph);

        Assert.True(diff.HasChanges);
        Assert.Single(diff.AddedMappings);
        Assert.Single(diff.RemovedMappings);
        Assert.Contains("D.c4", diff.AddedMappings[0]);
        Assert.Contains("C.c3", diff.RemovedMappings[0]);

        var md = LineageDiff.GenerateMarkdown(diff);
        Assert.Contains("Column mappings added (1)", md);
        Assert.Contains("Column mappings removed (1)", md);
    }

    [Fact]
    public void Diff_of_identical_graphs_has_no_changes()
    {
        var diff = LineageDiff.Compare(BuildChain(), BuildChain());
        Assert.False(diff.HasChanges);
        Assert.Contains("No lineage changes", LineageDiff.GenerateMarkdown(diff));
    }

    // ── secret redaction ───────────────────────────────────────────────────

    [Fact]
    public void Exports_redact_credentials()
    {
        Assert.DoesNotContain("Hunter2",
            OutputGenerator.RedactSecrets("Server=.;Database=DW;User ID=svc;Password=Hunter2;TrustServerCertificate=True"));
        Assert.Contains("***REDACTED***",
            OutputGenerator.RedactSecrets("Provider=SQLOLEDB;PWD=abc123;Data Source=."));
        // Non-secret text untouched
        Assert.Equal("SELECT * FROM dbo.Users", OutputGenerator.RedactSecrets("SELECT * FROM dbo.Users"));
    }

    [Fact]
    public void Json_export_applies_redaction()
    {
        var graph = new LineageGraph
        {
            Components = { new ComponentNode { Id = "c1", Name = "Src", SqlQueryOrTable = "Server=.;Password=TopSecret;" } }
        };
        var json = OutputGenerator.GenerateJson(graph);
        Assert.DoesNotContain("TopSecret", json);
    }

    // ── Mermaid ────────────────────────────────────────────────────────────

    [Fact]
    public void Mermaid_export_renders_table_level_edges()
    {
        var mmd = OutputGenerator.GenerateMermaid(BuildChain());

        Assert.StartsWith("flowchart LR", mmd);
        Assert.Contains("[\"A\"]", mmd);
        Assert.Contains("-->|INSERT|", mmd);
    }

    // ── OpenLineage ────────────────────────────────────────────────────────

    [Fact]
    public void OpenLineage_export_emits_events_with_column_lineage()
    {
        var graph = BuildChain();
        graph.Tasks.Add(new TaskNode { Id = "", Name = "Load", PackageId = "p1" });
        var json = OutputGenerator.GenerateOpenLineage(graph);

        Assert.Contains("\"eventType\": \"COMPLETE\"", json);
        Assert.Contains("columnLineage", json);
        Assert.Contains("openlineage.io", json);
    }

    // ── positional parameters ──────────────────────────────────────────────

    [Fact]
    public void Positional_parameters_are_substituted_outside_strings_and_comments()
    {
        var sql = "INSERT INTO dbo.T (a) SELECT s.c FROM dbo.S s WHERE s.id = ? AND s.tag = 'why?' -- really?";

        var substituted = SqlProcedureParser.ReplacePositionalParameters(sql);

        Assert.Contains("= @P0", substituted);
        Assert.Contains("'why?'", substituted);      // literal untouched
        Assert.Contains("-- really?", substituted);  // comment untouched

        // And the substituted SQL parses into lineage
        var records = SqlProcedureParser.Parse(substituted, "MyDb", "MyServer");
        Assert.Contains(records, r => r.OperationType == "INSERT" && r.TargetTable == "T");
    }
}
