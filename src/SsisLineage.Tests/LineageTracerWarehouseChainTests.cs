using System.Linq;
using SsisLineage.Core;
using SsisLineage.Core.Models;

namespace SsisLineage.Tests;

/// <summary>
/// End-to-end trace test for a typical multi-package warehouse chain: Source proc →
/// SELECT * staging hop → Stage1 → Stage2 → ADO NET data flow (proc-backed source,
/// derived column, lookup) → Load_DW.STAGE_Fact_* → Fact Load data flow → DW.Fact_*.
/// A column on the DW fact table must trace upstream across packages to the Source
/// schema on the staging server.
/// </summary>
public class LineageTracerWarehouseChainTests
{
    private const string StagingServer = "StagingServer";
    private const string StagingDb = "StagingDb";
    private const string DwServer = "DWServer";
    private const string DwDb = "DW";

    private static LineageGraph BuildWarehouseGraph()
    {
        var graph = new LineageGraph();

        // Data-flow components (node reconciliation by id needs them registered)
        foreach (var (id, name, type) in new[]
        {
            ("ADO_SRC", "Staging Sales Records", "Microsoft.ManagedComponentHost"),
            ("DERIVED", "Derived Column", "Microsoft.DerivedColumn"),
            ("ADO_DEST", "STAGE Fact Sales", "Microsoft.ManagedComponentHost"),
            ("OLE_SRC", "STAGE Fact Sales", "Microsoft.OLEDBSource"),
            ("LOOKUP", "Lookup Product", "Microsoft.Lookup"),
            ("OLE_DEST", "Fact Sales", "Microsoft.OLEDBDestination"),
        })
        {
            graph.Components.Add(new ComponentNode { Id = id, Name = name, Type = type });
        }

        void Map(string op, string srcCompId, string srcSchema, string srcTable, string srcCol,
                 string srcServer, string srcDb,
                 string tgtCompId, string tgtSchema, string tgtTable, string tgtCol,
                 string tgtServer, string tgtDb)
        {
            graph.ColumnMappings.Add(new ColumnMap
            {
                OperationType = op,
                SourceComponentId = srcCompId,
                SourceComponentName = string.IsNullOrEmpty(srcTable) ? srcCompId : $"{srcSchema}.{srcTable}",
                SourceServer = srcServer,
                SourceDatabase = srcDb,
                SourceSchema = srcSchema,
                SourceTable = srcTable,
                SourceColumnName = srcCol,
                TargetComponentId = tgtCompId,
                TargetComponentName = string.IsNullOrEmpty(tgtTable) ? tgtCompId : $"{tgtSchema}.{tgtTable}",
                TargetServer = tgtServer,
                TargetDatabase = tgtDb,
                TargetSchema = tgtSchema,
                TargetTable = tgtTable,
                TargetColumnName = tgtCol
            });
        }

        // 1. Source proc dumps into a staging temp table via SELECT * (wildcard hop)
        Map("SQL_PROC_SELECTINTO", "E1::Source.usp_Get_Sales_Fields", "Source", "usp_Get_Sales_Fields", "*",
            StagingServer, StagingDb,
            "E1", "Source", "tbl_Sales_TMP", "*", StagingServer, StagingDb);

        // 2. Stage1 proc reads named columns out of the temp table
        Map("SQL_PROC_INSERT", "E2::Source.tbl_Sales_TMP", "Source", "tbl_Sales_TMP", "Amount",
            StagingServer, StagingDb,
            "E2", "Stage1", "tbl_Sales_Fields", "Amount", StagingServer, StagingDb);

        // 3. Stage2 proc
        Map("SQL_PROC_INSERT", "E3::Stage1.tbl_Sales_Fields", "Stage1", "tbl_Sales_Fields", "Amount",
            StagingServer, StagingDb,
            "E3", "Stage2", "tbl_STAGE_Sales", "Amount", StagingServer, StagingDb);

        // 4. ADO NET source runs EXEC Stage2.usp_Get_Stage_Sales_Records — the proc's SELECT
        //    records target the component (no real table → component-keyed)
        Map("SQL_PROC_SELECT", "ADO_SRC::Stage2.tbl_STAGE_Sales", "Stage2", "tbl_STAGE_Sales", "Amount",
            StagingServer, StagingDb,
            "ADO_SRC", "", "", "Amount", StagingServer, StagingDb);

        // 5. Data flow: ADO NET source → derived column → ADO NET destination
        Map("XML_FALLBACK", "ADO_SRC", "", "", "Amount", "", "",
            "DERIVED", "", "", "Amount", "", "");
        Map("XML_FALLBACK", "DERIVED", "", "", "Amount", "", "",
            "ADO_DEST", "Load_DW", "STAGE_Fact_Sales", "Amount", DwServer, DwDb);

        // 6. Fact Load package: OLE DB source reads the same staging table (cross-package
        //    stitch happens via the shared table identity), feeds a lookup, then the fact
        Map("XML_FALLBACK", "OLE_SRC", "Load_DW", "STAGE_Fact_Sales", "Amount", DwServer, DwDb,
            "LOOKUP", "", "", "Amount", "", "");
        Map("XML_FALLBACK", "OLE_SRC", "Load_DW", "STAGE_Fact_Sales", "Ext_Order_ID", DwServer, DwDb,
            "LOOKUP", "", "", "Ext_Order_ID", "", "");
        Map("XML_FALLBACK", "LOOKUP", "", "", "Amount", "", "",
            "OLE_DEST", "DW", "Fact_Sales", "Amount", DwServer, DwDb);
        Map("XML_FALLBACK", "LOOKUP", "", "", "Dim_Product_ID", "", "",
            "OLE_DEST", "DW", "Fact_Sales", "Dim_Product_ID", DwServer, DwDb);

        // 7. Lookup reference query lineage (reference table → lookup component)
        Map("LOOKUP_REF", "LOOKUP::DW.Dim_Product", "DW", "Dim_Product", "Dim_Product_ID", DwServer, DwDb,
            "LOOKUP", "", "", "Dim_Product_ID", DwServer, DwDb);

        return graph;
    }

    [Fact]
    public void Fact_column_traces_upstream_to_source_schema_on_staging_server()
    {
        var tracer = new LineageTracer(BuildWarehouseGraph());

        var hit = tracer.Search("Fact_Sales.Amount", SearchScope.Column).FirstOrDefault();
        Assert.NotNull(hit);

        var result = tracer.Trace(hit!, TraceDirection.Upstream);
        Assert.NotEmpty(result.Steps);

        // The trace must cross from DW back through Stage2/Stage1 and the SELECT * hop
        // into the Source schema on the staging server.
        Assert.Contains(result.Steps, s => s.SourceSchema == "Stage2" && s.SourceTable == "tbl_STAGE_Sales");
        Assert.Contains(result.Steps, s => s.SourceSchema == "Stage1" && s.SourceTable == "tbl_Sales_Fields");
        Assert.Contains(result.Steps, s =>
            s.SourceSchema == "Source" &&
            s.SourceServer == StagingServer &&
            s.SourceDatabase == StagingDb);
    }

    [Fact]
    public void Lookup_supplied_dim_id_traces_back_to_reference_dim_table()
    {
        var tracer = new LineageTracer(BuildWarehouseGraph());

        var hit = tracer.Search("Fact_Sales.Dim_Product_ID", SearchScope.Column).FirstOrDefault();
        Assert.NotNull(hit);

        var result = tracer.Trace(hit!, TraceDirection.Upstream);
        Assert.Contains(result.Steps, s =>
            s.SourceSchema == "DW" && s.SourceTable == "Dim_Product" && s.Operation == "LOOKUP_REF");
    }

    [Fact]
    public void Staging_table_written_and_read_in_different_packages_is_one_node()
    {
        var tracer = new LineageTracer(BuildWarehouseGraph());

        // Exactly one searchable table node for Load_DW.STAGE_Fact_Sales — the ADO NET
        // destination (Stage Load) and OLE DB source (Fact Load) must reconcile.
        var hits = tracer.Search("STAGE_Fact_Sales", SearchScope.Table).ToList();
        Assert.Single(hits, h => h.Display.Equals("Load_DW.STAGE_Fact_Sales", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Wildcard_select_star_hop_does_not_break_named_column_trace()
    {
        var tracer = new LineageTracer(BuildWarehouseGraph());

        var hit = tracer.Search("Fact_Sales.Amount", SearchScope.Column).First();
        var result = tracer.Trace(hit, TraceDirection.Upstream);

        // The SELECT * edge (usp_Get_Sales_Fields → tbl_Sales_TMP) must be part of the path.
        Assert.Contains(result.Steps, s =>
            s.SourceTable == "usp_Get_Sales_Fields" && s.SourceColumn == "*");
    }

    [Fact]
    public void Node_display_backfills_server_and_database_from_better_informed_records()
    {
        // First record registers DW.Dim_Customer with no database (e.g. an inline Execute SQL
        // record whose connection couldn't be resolved); the enricher's MERGE record carries
        // the real server+database. The trace must show the informed values, not the blanks.
        var graph = new LineageGraph();
        graph.ColumnMappings.Add(new ColumnMap
        {
            OperationType = "UPDATE",
            SourceComponentName = "Stage2.lkup_Customer",
            SourceSchema = "Stage2", SourceTable = "lkup_Customer", SourceColumnName = "Customer_Name",
            TargetComponentName = "DW.Dim_Customer",
            TargetServer = "LocalServer", TargetDatabase = "",
            TargetSchema = "DW", TargetTable = "Dim_Customer", TargetColumnName = "Customer_Name"
        });
        graph.ColumnMappings.Add(new ColumnMap
        {
            OperationType = "SQL_PROC_MERGE-UPDATE",
            SourceComponentName = "Stage2.lkup_Customer",
            SourceServer = StagingServer, SourceDatabase = StagingDb,
            SourceSchema = "Stage2", SourceTable = "lkup_Customer", SourceColumnName = "Customer_Name",
            TargetComponentName = "DW.Dim_Customer",
            TargetServer = DwServer, TargetDatabase = DwDb,
            TargetSchema = "DW", TargetTable = "Dim_Customer", TargetColumnName = "Customer_Name"
        });

        var tracer = new LineageTracer(graph);
        var hit = tracer.Search("Dim_Customer.Customer_Name", SearchScope.Column).First();
        var result = tracer.Trace(hit, TraceDirection.Upstream);

        Assert.All(result.Steps, s =>
        {
            Assert.Equal(DwServer, s.TargetServer);
            Assert.Equal(DwDb, s.TargetDatabase);
        });
    }

    [Fact]
    public void Dynamic_sql_source_trace_stays_scoped_to_the_single_column()
    {
        // A SELECT * INTO tmp FROM OPENQUERY(...) proc emits BOTH per-column records
        // (parsed from the remote select list) AND a "*"→"*" record. Tracing one column
        // of the tmp table upstream must return only that column's remote source — not
        // every column the "*" represents.
        var graph = new LineageGraph();

        void Map(string op, string srcSchema, string srcTable, string srcCol,
                 string tgtSchema, string tgtTable, string tgtCol)
        {
            graph.ColumnMappings.Add(new ColumnMap
            {
                OperationType = op,
                SourceComponentName = string.IsNullOrEmpty(srcTable) ? "x" : $"{srcSchema}.{srcTable}",
                SourceServer = StagingServer, SourceDatabase = StagingDb,
                SourceSchema = srcSchema, SourceTable = srcTable, SourceColumnName = srcCol,
                TargetComponentName = $"{tgtSchema}.{tgtTable}",
                TargetServer = StagingServer, TargetDatabase = StagingDb,
                TargetSchema = tgtSchema, TargetTable = tgtTable, TargetColumnName = tgtCol
            });
        }

        // The "*"→"*" hop from the OPENQUERY wrapper.
        Map("SQL_PROC_SELECTINTO", "Source", "usp_Get_Sales_Fields", "*", "Source", "tbl_Sales_TMP", "*");
        // Per-column records parsed from the remote select list.
        Map("SQL_PROC_SELECTINTO", "Source", "remote_head", "amount_raw", "Source", "tbl_Sales_TMP", "Amount");
        Map("SQL_PROC_SELECTINTO", "Source", "remote_head", "qty_raw", "Source", "tbl_Sales_TMP", "Quantity");
        Map("SQL_PROC_SELECTINTO", "Source", "remote_head", "tax_raw", "Source", "tbl_Sales_TMP", "Tax");
        // Downstream consumer of the one column we trace.
        Map("SQL_PROC_INSERT", "Source", "tbl_Sales_TMP", "Amount", "Stage1", "tbl_Sales_Fields", "Amount");

        var tracer = new LineageTracer(graph);
        var hit = tracer.Search("tbl_Sales_TMP.Amount", SearchScope.Column).First();
        var result = tracer.Trace(hit, TraceDirection.Upstream);

        // Only Amount's source column is reached.
        Assert.Contains(result.Steps, s => s.SourceTable == "remote_head" && s.SourceColumn == "amount_raw");
        // The sibling columns must NOT be pulled in by the "*" hop.
        Assert.DoesNotContain(result.Steps, s => s.SourceColumn == "qty_raw");
        Assert.DoesNotContain(result.Steps, s => s.SourceColumn == "tax_raw");
        Assert.DoesNotContain(result.Steps, s => s.TargetColumn == "Quantity" || s.TargetColumn == "Tax");
    }

    [Fact]
    public void Downstream_impact_from_source_table_reaches_fact()
    {
        var tracer = new LineageTracer(BuildWarehouseGraph());

        var hit = tracer.Search("tbl_Sales_TMP.Amount", SearchScope.Column).FirstOrDefault();
        Assert.NotNull(hit);

        var result = tracer.Trace(hit!, TraceDirection.Downstream);
        Assert.Contains(result.Steps, s => s.TargetSchema == "DW" && s.TargetTable == "Fact_Sales");
    }
}
