using System.Linq;
using SsisLineage.Core;

namespace SsisLineage.Tests;

public class SqlProcedureParserCoverageTests
{
    [Fact]
    public void Delete_statement_captures_target_filter_and_joins()
    {
        var sql = """
            DELETE d
            FROM dbo.Orders d
            INNER JOIN dbo.Archive a ON a.OrderId = d.OrderId
            WHERE d.Status = 'Cancelled'
            """;

        var records = SqlProcedureParser.Parse(sql, "MyDb", "MyServer");

        var del = Assert.Single(records, r => r.OperationType == "DELETE");
        Assert.Equal("Orders", del.TargetTable);
        Assert.Equal("dbo", del.TargetSchema);
        Assert.Contains("Cancelled", del.FilterConditions);
        Assert.Contains("OrderId", del.JoinDetails);
    }

    [Fact]
    public void Insert_exec_records_proc_to_table_flow()
    {
        var sql = "INSERT INTO dbo.Results EXEC stage.usp_Compute;";

        var records = SqlProcedureParser.Parse(sql, "MyDb", "MyServer");

        var rec = Assert.Single(records, r => r.OperationType == "INSERT_EXEC");
        Assert.Equal("usp_Compute", rec.SourceTable);
        Assert.Equal("stage", rec.SourceSchema);
        Assert.Equal("Results", rec.TargetTable);
        Assert.Equal("*", rec.SourceColumnName);
        Assert.Equal("*", rec.TargetColumnName);
    }

    [Fact]
    public void Cte_lineage_chains_base_table_through_cte_to_target()
    {
        var sql = """
            WITH RecentOrders (OrderId, Amount) AS (
                SELECT o.OrderId, o.Amount FROM dbo.Orders o WHERE o.OrderDate > '2026-01-01'
            )
            INSERT INTO dbo.OrderSummary (OrderId, Amount)
            SELECT r.OrderId, r.Amount FROM RecentOrders r;
            """;

        var records = SqlProcedureParser.Parse(sql, "MyDb", "MyServer");

        // CTE definition: dbo.Orders → RecentOrders
        Assert.Contains(records, r =>
            r.OperationType == "CTE" && r.SourceTable == "Orders" && r.TargetTable == "RecentOrders");
        // Outer INSERT: RecentOrders → dbo.OrderSummary
        Assert.Contains(records, r =>
            r.OperationType == "INSERT" && r.SourceTable == "RecentOrders" && r.TargetTable == "OrderSummary");
        // Filter inside the CTE is captured
        Assert.Contains(records, r => r.OperationType == "CTE" && r.FilterConditions.Contains("OrderDate"));
    }

    [Fact]
    public void Merge_resolves_linked_server_using_source()
    {
        var sql = """
            ALTER PROCEDURE [Load_DW].[usp_Load_Dim_Region]
            AS
            BEGIN
                MERGE [DW].[Dim_Region] AS [Dim]
                USING [LINKEDSRV].[StagingDb].[Stage2].[lkup_Region] AS [Stage]
                ON [Dim].[Ext_Region_ID] = [Stage].[Region_ID]
                WHEN MATCHED THEN
                    UPDATE SET [Dim].[Region_Name] = [Stage].[Region_Name],
                               [Dim].[Last_Updated] = GETDATE()
                WHEN NOT MATCHED THEN
                    INSERT ([Ext_Source_ID], [Ext_Region_ID], [Region_Name], [Valid_From], [Is_Current])
                    VALUES ([Stage].[ID], [Stage].[Region_ID], [Stage].[Region_Name], GETDATE(), 1);
            END;
            """;

        var records = SqlProcedureParser.Parse(sql, "DWDb", "DWServer");

        // MERGE-UPDATE: Stage.Region_Name resolves to the 4-part linked-server source
        var upd = Assert.Single(records, r =>
            r.OperationType == "MERGE-UPDATE" && r.TargetColumnName == "Region_Name");
        Assert.Equal("LINKEDSRV", upd.SourceServer);
        Assert.Equal("StagingDb", upd.SourceDatabase);
        Assert.Equal("Stage2", upd.SourceSchema);
        Assert.Equal("lkup_Region", upd.SourceTable);
        Assert.Equal("Region_Name", upd.SourceColumnName);
        Assert.Equal("Dim_Region", upd.TargetTable);
        Assert.Equal("DW", upd.TargetSchema);

        // MERGE-INSERT: VALUES expressions pair positionally with target columns
        var ins = Assert.Single(records, r =>
            r.OperationType == "MERGE-INSERT" && r.TargetColumnName == "Ext_Source_ID");
        Assert.Equal("ID", ins.SourceColumnName);
        Assert.Equal("lkup_Region", ins.SourceTable);
        Assert.Equal("LINKEDSRV", ins.SourceServer);

        // Literals/GETDATE() produce no spurious source rows
        Assert.DoesNotContain(records, r =>
            r.OperationType == "MERGE-INSERT" &&
            (r.TargetColumnName == "Valid_From" || r.TargetColumnName == "Is_Current"));

        // No record may claim the target column name as its source with an empty table
        Assert.DoesNotContain(records, r =>
            r.OperationType == "MERGE-INSERT" && string.IsNullOrEmpty(r.SourceTable));
    }

    [Fact]
    public void Merge_with_derived_table_source_chains_through_alias()
    {
        var sql = """
            MERGE dbo.Target AS t
            USING (SELECT s.Id, s.Name FROM staging.Source s) AS src
            ON t.Id = src.Id
            WHEN MATCHED THEN UPDATE SET t.Name = src.Name
            WHEN NOT MATCHED THEN INSERT (Id, Name) VALUES (src.Id, src.Name);
            """;

        var records = SqlProcedureParser.Parse(sql, "MyDb", "MyServer");

        // Subquery: staging.Source → pseudo-table "src"
        Assert.Contains(records, r =>
            r.OperationType == "MERGE-SOURCE" && r.SourceTable == "Source" && r.TargetTable == "src");
        // Update: src → dbo.Target
        Assert.Contains(records, r =>
            r.OperationType == "MERGE-UPDATE" && r.SourceTable == "src" && r.TargetTable == "Target"
            && r.SourceColumnName == "Name" && r.TargetColumnName == "Name");
        // Insert: src → dbo.Target
        Assert.Contains(records, r =>
            r.OperationType == "MERGE-INSERT" && r.SourceTable == "src" && r.TargetColumnName == "Id");
    }

    [Fact]
    public void Insert_select_from_linked_server_resolves_four_part_source()
    {
        var sql = """
            INSERT INTO dbo.Local (Id, Name)
            SELECT r.Id, r.Name FROM [LINKED].[RemoteDb].[sch].[Remote] r;
            """;

        var records = SqlProcedureParser.Parse(sql, "MyDb", "MyServer");

        var rec = Assert.Single(records, r => r.TargetColumnName == "Id");
        Assert.Equal("LINKED", rec.SourceServer);
        Assert.Equal("RemoteDb", rec.SourceDatabase);
        Assert.Equal("sch", rec.SourceSchema);
        Assert.Equal("Remote", rec.SourceTable);
    }

    [Fact]
    public void Linked_server_map_normalizes_server_names_on_mappings()
    {
        var graph = new SsisLineage.Core.Models.LineageGraph();
        graph.ColumnMappings.Add(new SsisLineage.Core.Models.ColumnMap
        {
            SourceServer = "LINKEDSRV",
            SourceTable = "lkup_Region",
            SourceColumnName = "Region_Name",
            TargetServer = "DWServer",
            TargetTable = "Dim_Region",
            TargetColumnName = "Region_Name"
        });

        var emptyProjectDir = System.IO.Directory.CreateTempSubdirectory("lineage-test").FullName;
        try
        {
            SqlProcedureEnricher.EnrichFromStoredProcedures(
                graph, emptyProjectDir, overrideConnectionString: null,
                includeDataFlowComponents: false, includeExecuteSqlTasks: false,
                linkedServerMap: new System.Collections.Generic.Dictionary<string, string>
                {
                    ["LINKEDSRV"] = "StagingServer"
                });
        }
        finally
        {
            System.IO.Directory.Delete(emptyProjectDir, recursive: true);
        }

        var map = Assert.Single(graph.ColumnMappings);
        Assert.Equal("StagingServer", map.SourceServer);
        Assert.Equal("DWServer", map.TargetServer);
    }

    [Fact]
    public void Script_component_is_first_party_and_normalized()
    {
        Assert.True(ThirdPartyComponentDetector.IsScriptComponent(
            "Microsoft.SqlServer.Dts.Pipeline.ScriptComponentHost", "Apply Business Rules"));
        Assert.False(ThirdPartyComponentDetector.IsLikelyThirdParty(
            "Microsoft.SqlServer.Dts.Pipeline.ScriptComponentHost", "Apply Business Rules"));
        Assert.Equal("Script Component", ThirdPartyComponentDetector.NormalizeComponentType(
            "Microsoft.SqlServer.Dts.Pipeline.ScriptComponentHost", "Apply Business Rules"));
    }
}
