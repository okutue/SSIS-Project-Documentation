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
