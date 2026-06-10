using System.Linq;
using SsisLineage.Core;
using SsisLineage.Core.Models;

namespace SsisLineage.Tests;

public class LineageTracerTests
{
    // A.c1 → B.c2 (INSERT) → C.c3 (SQL_PROC_INSERT). B is the mid-stream node.
    private static LineageGraph BuildChain() => new()
    {
        ColumnMappings =
        {
            new ColumnMap
            {
                SourceComponentName = "A", SourceColumnName = "c1",
                TargetComponentName = "B", TargetColumnName = "c2",
                OperationType = "INSERT"
            },
            new ColumnMap
            {
                SourceComponentName = "B", SourceColumnName = "c2",
                TargetComponentName = "C", TargetColumnName = "c3",
                OperationType = "SQL_PROC_INSERT", ProcedureName = "dbo.usp_Load"
            }
        }
    };

    [Fact]
    public void Search_includes_midstream_node()
    {
        var tracer = new LineageTracer(BuildChain());

        var hits = tracer.Search("B", SearchScope.Column).ToList();

        // The intermediate B.c2 — neither an ultimate source nor a final target — is searchable.
        Assert.Contains(hits, h => h.Display == "B.c2");
    }

    [Fact]
    public void Trace_from_midstream_expands_both_directions()
    {
        var tracer = new LineageTracer(BuildChain());
        var hit = tracer.Search("B", SearchScope.Column).Single(h => h.Display == "B.c2");

        var result = tracer.Trace(hit);

        // Full chain A → B → C captured from a mid-stream seed.
        Assert.Equal(2, result.Steps.Count);

        var ordered = result.Steps.OrderBy(s => s.Rank).ToList();
        Assert.Equal("A", ordered[0].SourceTable);
        Assert.Equal("B", ordered[0].TargetTable);
        Assert.Equal("B", ordered[1].SourceTable);
        Assert.Equal("C", ordered[1].TargetTable);

        // Rank orders source→target so the table reads top-to-bottom.
        Assert.True(ordered[0].Rank < ordered[1].Rank);

        // Sub-graph carries exactly the two path edges for diagram rendering.
        Assert.Equal(2, result.SubGraph.ColumnMappings.Count);
    }

    [Fact]
    public void Trace_table_scope_includes_every_column_path()
    {
        var tracer = new LineageTracer(BuildChain());
        var tableHit = tracer.Search("B", SearchScope.Table).Single(h => h.Display == "B");

        var result = tracer.Trace(tableHit);

        Assert.Equal(2, result.Steps.Count);
    }

    // Mirrors a proc-backed OLE DB Source feeding a destination: the stored proc's internal
    // lineage (source.Customers → proc output) and the data-flow lineage (proc → DW.Dim_Customers)
    // must stitch into ONE path via the shared component id, so Email traces back to source.Customers.
    private static LineageGraph BuildProcBackedDataFlow() => new()
    {
        Components =
        {
            new ComponentNode { Id = "src1", Name = "OLE DB Source", Type = "OLE DB Source" },
            new ComponentNode { Id = "dst1", Name = "OLE DB Destination", Type = "OLE DB Destination" }
        },
        ColumnMappings =
        {
            // Proc-internal SELECT: source.Customers.Email → (component src1, table empty)
            new ColumnMap
            {
                SourceComponentId = "src1::source.Customers", SourceComponentName = "source.Customers",
                SourceSchema = "source", SourceTable = "Customers", SourceColumnName = "Email",
                TargetComponentId = "src1", TargetComponentName = "OLE DB Source",
                TargetColumnName = "Email", OperationType = "SQL_PROC_SELECT",
                ProcedureName = "stage.usp_Get_LoadCustomers"
            },
            // Data-flow XML_FALLBACK: proc-backed source side stays component-keyed (the
            // enricher leaves schema/table empty for EXEC-backed components so this row
            // stitches to the proc-internal record above); the display carries the proc name.
            new ColumnMap
            {
                SourceComponentId = "src1", SourceComponentName = "stage.usp_Get_LoadCustomers",
                SourceColumnName = "Email",
                TargetComponentId = "dst1", TargetComponentName = "DW.Dim_Customers",
                TargetSchema = "DW", TargetTable = "Dim_Customers", TargetColumnName = "Email",
                OperationType = "XML_FALLBACK"
            }
        }
    };

    [Fact]
    public void Trace_stitches_proc_lineage_to_dataflow_via_component_id()
    {
        var tracer = new LineageTracer(BuildProcBackedDataFlow());
        var hit = tracer.Search("DW.Dim_Customers.Email", SearchScope.Column).Single();

        var result = tracer.Trace(hit);

        // Two stitched hops spanning three table-level stages.
        Assert.Equal(2, result.Steps.Count);
        Assert.Equal(3, result.TableCount);

        // The ultimate source is reached — Email is no longer a dead-end XML_FALLBACK row.
        Assert.Contains(result.Steps, s => s.SourceTable == "Customers" && s.SourceSchema == "source");

        // The proc-backed component renders as the proc, not the raw component name.
        Assert.Contains(result.Steps, s => s.TargetTable == "usp_Get_LoadCustomers" || s.SourceTable == "usp_Get_LoadCustomers");
    }

    // Regression: a proc-backed OLE DB Source must stay keyed by component on its data-flow
    // (XML_FALLBACK) side so the proc's internal lineage stitches to it. The enricher must NOT
    // stamp the proc name onto that side as a "table" — doing so re-keys it to a table node and
    // severs the stitch, so DW.Dim_Customers.Email stops tracing back to source.Customers.
    [Fact]
    public void Proc_backed_source_left_component_keyed_still_stitches_to_real_table_destination()
    {
        var graph = new LineageGraph
        {
            Components =
            {
                new ComponentNode { Id = "src1", Name = "OLE DB Source", Type = "OLE DB Source" },
                new ComponentNode { Id = "dst1", Name = "OLE DB Destination", Type = "OLE DB Destination" }
            },
            ColumnMappings =
            {
                // Proc internal (SELECT * FROM source.Customers) → component src1, table-less.
                new ColumnMap
                {
                    SourceComponentId = "src1::source.Customers", SourceComponentName = "source.Customers",
                    SourceSchema = "source", SourceTable = "Customers", SourceColumnName = "*",
                    TargetComponentId = "src1", TargetComponentName = "stage.usp_Get_LoadCustomers",
                    TargetColumnName = "*", OperationType = "SQL_PROC_SELECT",
                    ProcedureName = "stage.usp_Get_LoadCustomers"
                },
                // Data flow: proc-backed source (component-keyed, NO table) → real-table destination.
                new ColumnMap
                {
                    SourceComponentId = "src1", SourceComponentName = "OLE DB Source",
                    SourceColumnName = "Email",
                    TargetComponentId = "dst1", TargetComponentName = "DW.Dim_Customers",
                    TargetSchema = "DW", TargetTable = "Dim_Customers", TargetColumnName = "Email",
                    OperationType = "XML_FALLBACK"
                }
            }
        };

        var tracer = new LineageTracer(graph);
        var hit = tracer.Search("DW.Dim_Customers.Email", SearchScope.Column).Single();
        var result = tracer.Trace(hit, TraceDirection.Upstream);

        Assert.Contains(result.Steps, s => s.SourceTable == "Customers" && s.SourceSchema == "source");
    }

    [Fact]
    public void GetMappingSteps_groups_same_stage_rows_and_orders_source_to_target()
    {
        // One INSERT stage (A→B) carrying two columns, then a second stage (B→C).
        var graph = new LineageGraph
        {
            ColumnMappings =
            {
                new ColumnMap { SourceComponentName = "A", SourceColumnName = "c1", TargetComponentName = "B", TargetColumnName = "c1", OperationType = "INSERT" },
                new ColumnMap { SourceComponentName = "A", SourceColumnName = "c2", TargetComponentName = "B", TargetColumnName = "c2", OperationType = "INSERT" },
                new ColumnMap { SourceComponentName = "B", SourceColumnName = "c1", TargetComponentName = "C", TargetColumnName = "c1", OperationType = "INSERT" }
            }
        };

        var steps = new LineageTracer(graph).GetMappingSteps();

        // Both column rows of the A→B stage share the SAME step number — a grouping, not a serial.
        Assert.Equal(steps[0], steps[1]);
        // The downstream B→C stage gets a higher number (source→target ordering).
        Assert.True(steps[2] > steps[0]);
        // 1-based.
        Assert.Equal(1, steps[0]);
        Assert.Equal(2, steps[2]);
    }

    [Fact]
    public void GenerateTraceCsv_emits_header_and_rows_with_join_last()
    {
        var tracer = new LineageTracer(BuildChain());
        var hit = tracer.Search("B", SearchScope.Column).Single(h => h.Display == "B.c2");
        var result = tracer.Trace(hit);

        var csv = OutputGenerator.GenerateTraceCsv(result);
        var lines = csv.Split('\n', System.StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length); // header + 2 hops
        Assert.Contains("SourceColumn", lines[0]);
        Assert.Contains("TargetColumn", lines[0]);
        Assert.EndsWith("JoinCondition", lines[0].TrimEnd('\r')); // Join Condition is the last column
    }
}
