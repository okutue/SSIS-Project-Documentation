using System.Linq;
using SsisLineage.Core;

namespace SsisLineage.Tests;

/// <summary>
/// Coverage for common warehouse-procedure T-SQL shapes: a MERGE-based dimension load
/// from a 4-part linked-server staging table (TRY/CATCH, table-variable OUTPUT clause,
/// bare summary SELECT), and a source-layer extract that composes dynamic SQL and pulls
/// remote data through OPENQUERY into a temp table.
/// </summary>
public class TSqlVariantParserTests
{
    // MERGE from [LinkedServer].[Db].[Schema].[Table] into a local dimension, wrapped in
    // TRY/CATCH with an OUTPUT … INTO @tableVar clause and a parenthesized summary SELECT.
    private const string DimLoadProc = """
        ALTER PROCEDURE [Load_DW].[usp_Load_Dim_Product]

        AS

        BEGIN

            SET NOCOUNT ON;

            BEGIN TRY

                DECLARE @DimChanges TABLE
                (
                    [ChangeType] VARCHAR(10),
                    [ID] INT NULL
                );

                MERGE [DW].[Dim_Product] AS [Dim]
                USING [LINKEDSRV].[StagingDb].[Stage2].[lkup_Product] AS [Stage]
                ON [Dim].[Ext_Product_ID] = [Stage].[Product_ID]
                   AND [Dim].[Dim_System_ID] = [Stage].[System_ID]
                WHEN MATCHED AND (
                                     ISNULL([Dim].[Product_Name], '') <> ISNULL([Stage].[Product_Name], '')
                                     OR ISNULL([Dim].[Product_Code], '') <> ISNULL([Stage].[Product_Code], '')
                                     OR ISNULL([Dim].[Category], '') <> ISNULL([Stage].[Category], '')
                                     OR ISNULL([Dim].[Discontinued_Date], '') <> ISNULL([Stage].[Discontinued_Date], '')
                                 ) THEN
                    UPDATE SET [Dim].[Product_Name] = [Stage].[Product_Name],
                               [Dim].[Product_Code] = [Stage].[Product_Code],
                               [Dim].[Category] = [Stage].[Category],
                               [Dim].[Discontinued_Date] = [Stage].[Discontinued_Date],
                               [Dim].[Last_Updated] = GETDATE()
                WHEN NOT MATCHED THEN
                    INSERT
                    (
                        [Ext_Source_ID],
                        [Ext_Product_ID],
                        [Product_Name],
                        [Product_Code],
                        [Category],
                        [Discontinued_Date],
                        [Dim_System_ID],
                        [Valid_From],
                        [Valid_To],
                        [Is_Current],
                        [Is_Inferred],
                        [Last_Updated]
                    )
                    VALUES
                    ([Stage].[ID], [Stage].[Product_ID], [Stage].[Product_Name], [Stage].[Product_Code],
                     [Stage].[Category], [Stage].[Discontinued_Date], [Stage].[System_ID],
                     GETDATE(), NULL, 1, 0, GETDATE())
                OUTPUT $ACTION,
                       [Inserted].[Ext_Product_ID]
                INTO @DimChanges;

                (SELECT ISNULL(SUM(   CASE
                                          WHEN [ChangeType] = 'INSERT' THEN
                                              1
                                      END
                                  ),
                               0
                              ) AS [Inserted],
                        ISNULL(SUM(   CASE
                                          WHEN [ChangeType] = 'UPDATE' THEN
                                              1
                                      END
                                  ),
                               0
                              ) AS [Updated]
                 FROM @DimChanges);

            END TRY
            BEGIN CATCH

                INSERT INTO [LINKEDSRV].[StagingDb].[Audit].[tbl_ErrorLog]
                (
                    [Process],
                    [ErrorDateTime],
                    [ErrorNumber],
                    [ErrorState],
                    [ErrorSeverity],
                    [ErrorProcedure],
                    [ErrorLine],
                    [ErrorMessage]
                )
                SELECT '[Load_DW].[usp_Load_Dim_Product]' AS [Process],
                       GETDATE() AS [ErrorDateTime],
                       ERROR_NUMBER() AS [ErrorNumber],
                       ERROR_STATE() AS [ErrorState],
                       ERROR_SEVERITY() AS [ErrorSeverity],
                       ERROR_PROCEDURE() AS [ErrorProcedure],
                       ERROR_LINE() AS [ErrorLine],
                       ERROR_MESSAGE() AS [ErrorMessage];

                DECLARE @Message VARCHAR(MAX) = ERROR_MESSAGE(),
                        @Severity INT = ERROR_SEVERITY(),
                        @State SMALLINT = ERROR_STATE();

                RAISERROR(@Message, @Severity, @State);

            END CATCH;

        END;
        """;

    // Source-layer extract: builds a remote SELECT in @SQL, wraps it in
    // SELECT * INTO … FROM OPENQUERY(@Server, @SQL) via @OpenQuery, then EXEC(@OpenQuery).
    private const string SourceExtractProc = """
        ALTER PROCEDURE [Source].[usp_Get_Product_Fields]
            @Server VARCHAR(64), @Database VARCHAR(32), @StartDate DATETIME, @ImportType SMALLINT
        AS
        BEGIN
            SET NOCOUNT ON;
            BEGIN TRY
                IF EXISTS ( SELECT * FROM [sys].[objects]
                            WHERE [object_id] = OBJECT_ID(N'[Source].[tbl_Product_TMP]') AND [type] IN ( N'U' ) )
                    DROP TABLE [Source].[tbl_Product_TMP];

                DECLARE @OpenQuery AS VARCHAR(MAX);
                DECLARE @SQL VARCHAR(MAX);
                DECLARE @Start DATETIME;
                SET @Start = @StartDate;

                DECLARE @RecordIDs VARCHAR(MAX);
                SET @RecordIDs = ( SELECT [Record_IDs] FROM [Audit].[tbl_LoadParameters] WITH ( NOLOCK )
                                   WHERE [Import_Type] = @ImportType );

                SET @SQL = 'SELECT  [D].[id]
                         ,[D].[record_head_id] AS Record_ID
                         ,[product_name]
                         ,[product_code]
                         ,[product_category]
                FROM      ' + @Database + '.[record_head] [H] WITH (NOLOCK)
                INNER JOIN    ' + @Database + '.[record_product_detail] [D] WITH (NOLOCK) ON [H].[id] = [D].[record_head_id]
                WHERE h.[dt_updated] >= CONVERT(Datetime,'''''+ CONVERT(VARCHAR(19) , @Start , 121) + ''''', 121)
                OR [H].[id] IN (' + @RecordIDs + ');
                ;';

                SET @OpenQuery = 'SELECT *  INTO [Source].[tbl_Product_TMP] FROM OPENQUERY(' + @Server + ', ''' + @SQL + ''')';

                EXEC (@OpenQuery);

                SELECT  COUNT([ID]) AS [IDCount]
                FROM    [Source].[tbl_Product_TMP];
            END TRY
            BEGIN CATCH
                INSERT INTO [Audit].[tbl_ErrorLog]
                SELECT '[Source].[usp_Get_Product_Fields]' AS [Process], GETDATE() AS [ErrorDateTime],
                       ERROR_NUMBER() AS [ErrorNumber], ERROR_STATE() AS [ErrorState],
                       ERROR_SEVERITY() AS [ErrorSeverity], ERROR_PROCEDURE() AS [ErrorProcedure],
                       ERROR_LINE() AS [ErrorLine], ERROR_MESSAGE() AS [ErrorMessage];
                DECLARE @Message VARCHAR(MAX) = ERROR_MESSAGE(), @Severity INT = ERROR_SEVERITY(),
                        @State SMALLINT = ERROR_STATE();
                RAISERROR (@Message, @Severity, @State);
            END CATCH;
        END;
        """;

    [Fact]
    public void Dynamic_openquery_select_star_into_registers_remote_columns()
    {
        var records = SqlProcedureParser.Parse(SourceExtractProc, "StagingDb", "StagingServer");

        // The composed EXEC(@OpenQuery) is a SELECT * INTO — the *→* hop is recorded
        var star = records.Where(r =>
            r.OperationType == "SELECTINTO" && r.TargetTable == "tbl_Product_TMP" &&
            r.SourceColumnName == "*").ToList();
        Assert.NotEmpty(star);

        // The inner remote query's select list lands as named columns on the target,
        // so any column of tbl_Product_TMP traces through to the remote source.
        var named = records.Where(r =>
            r.OperationType == "SELECTINTO" && r.TargetTable == "tbl_Product_TMP" &&
            r.SourceColumnName != "*").ToList();
        Assert.Contains(named, r => r.TargetColumnName == "product_code");

        // Aliased column ([D].[record_head_id]) resolves to its exact table.
        var recordId = Assert.Single(named, r => r.TargetColumnName == "Record_ID");
        Assert.Equal("record_head_id", recordId.SourceColumnName);
        Assert.Equal("record_product_detail", recordId.SourceTable);

        // Unqualified columns are ambiguous across the two joined tables — both are listed
        // rather than one being wrongly asserted as the sole source.
        var code = Assert.Single(named, r => r.TargetColumnName == "product_code");
        Assert.Contains("record_head", code.SourceTable);
        Assert.Contains("record_product_detail", code.SourceTable);
        Assert.Contains("|", code.SourceTable);
    }

    [Fact]
    public void Variable_values_substitute_into_dynamic_openquery_names()
    {
        var vars = new System.Collections.Generic.Dictionary<string, string>
        {
            ["@Server"]   = "LINKEDSRV",
            ["@Database"] = "RemoteDb"
        };

        var records = SqlProcedureParser.Parse(SourceExtractProc, "StagingDb", "StagingServer", vars);

        // With @Server supplied, the OPENQUERY linked-server name is real on the remote rows.
        var remote = records.Where(r =>
            r.OperationType == "SELECTINTO" && r.TargetTable == "tbl_Product_TMP" &&
            r.SourceColumnName != "*").ToList();
        Assert.NotEmpty(remote);
        Assert.All(remote, r => Assert.Equal("LINKEDSRV", r.SourceServer));
        // @Database appears as the catalog qualifier on the remote tables (no DUMMY).
        Assert.Contains(remote, r => r.SourceSchema == "RemoteDb");
        Assert.DoesNotContain(remote, r => r.SourceServer == "DUMMY" || r.SourceSchema == "DUMMY");
    }

    [Fact]
    public void Offline_dynamic_sql_keeps_placeholder_names_without_variable_values()
    {
        var records = SqlProcedureParser.Parse(SourceExtractProc, "StagingDb", "StagingServer");

        // No variable values supplied → unmapped @Server/@Database stay as placeholders.
        var remote = records.Where(r =>
            r.OperationType == "SELECTINTO" && r.TargetTable == "tbl_Product_TMP" &&
            r.SourceColumnName != "*").ToList();
        Assert.NotEmpty(remote);
        Assert.Contains(remote, r => r.SourceServer == "DUMMY");
    }

    [Fact]
    public void Unqualified_column_in_single_table_select_is_attributed_directly()
    {
        var sql = """
            INSERT INTO dbo.Copy (Id, Name)
            SELECT Id, Name FROM sales.Orders;
            """;

        var records = SqlProcedureParser.Parse(sql, "MyDb", "MyServer");

        // Single-table FROM is unambiguous — no candidate list.
        Assert.All(records.Where(r => r.SourceColumnName is "Id" or "Name"), r =>
        {
            Assert.Equal("Orders", r.SourceTable);
            Assert.Equal("sales", r.SourceSchema);
            Assert.DoesNotContain("|", r.SourceTable);
        });
    }

    [Fact]
    public void Unqualified_column_in_multi_table_join_lists_all_candidate_tables()
    {
        var sql = """
            INSERT INTO dbo.Result (OrderId, CustomerName, Total)
            SELECT o.OrderId, CustomerName, Total
            FROM sales.Orders o
            INNER JOIN sales.Customers c ON c.CustomerId = o.CustomerId
            LEFT JOIN sales.Invoices i ON i.OrderId = o.OrderId;
            """;

        var records = SqlProcedureParser.Parse(sql, "MyDb", "MyServer");

        // Aliased column resolves exactly.
        Assert.Contains(records, r => r.TargetColumnName == "OrderId" && r.SourceTable == "Orders");

        // Unqualified CustomerName / Total list every joined table (order = FROM order),
        // and they share schema "sales".
        var name = Assert.Single(records, r => r.TargetColumnName == "CustomerName");
        Assert.Equal("Orders | Customers | Invoices", name.SourceTable);
        Assert.Equal("sales", name.SourceSchema);
    }

    [Fact]
    public void Full_proc_parses_without_errors_and_yields_merge_records()
    {
        var records = SqlProcedureParser.Parse(DimLoadProc, "DWDb", "DWServer");

        Assert.NotEmpty(records);
        Assert.Contains(records, r => r.OperationType == "MERGE-UPDATE");
        Assert.Contains(records, r => r.OperationType == "MERGE-INSERT");
    }

    [Fact]
    public void Merge_update_source_is_staging_table_not_proc()
    {
        var records = SqlProcedureParser.Parse(DimLoadProc, "DWDb", "DWServer");

        var upd = records.Where(r => r.OperationType == "MERGE-UPDATE").ToList();
        Assert.NotEmpty(upd);
        Assert.All(upd.Where(r => r.SourceColumnName != "" && !r.SourceExpression.Contains("GETDATE")), r =>
        {
            Assert.Equal("lkup_Product", r.SourceTable);
            Assert.Equal("Stage2", r.SourceSchema);
            Assert.Equal("StagingDb", r.SourceDatabase);
            Assert.Equal("LINKEDSRV", r.SourceServer);
        });
    }

    [Fact]
    public void Merge_insert_values_pair_with_target_columns()
    {
        var records = SqlProcedureParser.Parse(DimLoadProc, "DWDb", "DWServer");

        var ins = records.Where(r => r.OperationType == "MERGE-INSERT").ToList();
        Assert.NotEmpty(ins);

        // every record carries the staging table as source
        Assert.All(ins, r => Assert.Equal("lkup_Product", r.SourceTable));

        // positional pairing: Stage.ID → Ext_Source_ID, Stage.System_ID → Dim_System_ID
        Assert.Contains(ins, r => r.SourceColumnName == "ID" && r.TargetColumnName == "Ext_Source_ID");
        Assert.Contains(ins, r => r.SourceColumnName == "System_ID" && r.TargetColumnName == "Dim_System_ID");
        Assert.Contains(ins, r => r.SourceColumnName == "Product_ID" && r.TargetColumnName == "Ext_Product_ID");

        // GETDATE()/literal slots (Valid_From, Is_Current, …) yield no source rows
        Assert.DoesNotContain(ins, r => r.TargetColumnName == "Valid_From");
        Assert.DoesNotContain(ins, r => r.TargetColumnName == "Is_Current");
    }

    [Fact]
    public void Derived_table_in_from_chains_through_alias_pseudo_table()
    {
        var sql = """
            INSERT INTO dbo.Totals (CustomerId, OrderTotal)
            SELECT d.CustomerId, d.OrderTotal
            FROM (SELECT o.CustomerId, SUM(o.Amount) AS OrderTotal
                  FROM sales.Orders o GROUP BY o.CustomerId) AS d
            WHERE d.OrderTotal > 0;
            """;

        var records = SqlProcedureParser.Parse(sql, "MyDb", "MyServer");

        // Subquery: sales.Orders → pseudo-table "d"
        Assert.Contains(records, r =>
            r.OperationType == "DERIVED" && r.SourceTable == "Orders" && r.SourceSchema == "sales"
            && r.TargetTable == "d");
        // Outer insert: d → dbo.Totals, schema-less pseudo-table on the source side
        Assert.Contains(records, r =>
            r.OperationType == "INSERT" && r.SourceTable == "d" && r.SourceSchema == ""
            && r.TargetTable == "Totals" && r.TargetColumnName == "OrderTotal");
    }

    [Fact]
    public void Openrowset_inner_query_maps_into_target()
    {
        var sql = """
            SELECT *
            INTO dbo.RemoteSnapshot
            FROM OPENROWSET('SQLNCLI', 'Server=Remote;Trusted_Connection=yes;',
                'SELECT r.ItemId, r.ItemName FROM warehouse.Items r');
            """;

        var records = SqlProcedureParser.Parse(sql, "MyDb", "MyServer");

        Assert.Contains(records, r =>
            r.OperationType == "SELECTINTO" && r.TargetTable == "RemoteSnapshot"
            && r.SourceTable == "Items" && r.SourceColumnName == "ItemId");
    }
}
