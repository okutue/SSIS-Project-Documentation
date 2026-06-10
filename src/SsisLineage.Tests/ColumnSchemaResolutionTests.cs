using System.Collections.Generic;
using System.Linq;
using SsisLineage.Core;

namespace SsisLineage.Tests;

/// <summary>
/// Resolution of unqualified-column candidate lists to their owning table. The DB-querying
/// resolver is replaced with a fake that knows which table declares which column, so the
/// orchestration (candidate parsing, local vs linked-server routing, collapse-or-keep) is
/// verified without a live SQL Server.
/// </summary>
public class ColumnSchemaResolutionTests
{
    // Fake resolver: column → owning table, optionally scoped to a linked server.
    private sealed class FakeResolver : IColumnSchemaResolver
    {
        private readonly Dictionary<string, string> _owner;
        private readonly string? _requireLinkedServer;
        public List<ColumnResolutionRequest> Requests { get; } = new();

        public FakeResolver(Dictionary<string, string> owner, string? requireLinkedServer = null)
        {
            _owner = owner;
            _requireLinkedServer = requireLinkedServer;
        }

        public string? ResolveOwningTable(ColumnResolutionRequest request)
        {
            Requests.Add(request);
            if (_requireLinkedServer != null && request.LinkedServer != _requireLinkedServer) return null;
            if (!_owner.TryGetValue(request.Column, out var table)) return null;
            return request.CandidateTables.Contains(table) ? table : null;
        }
    }

    private static SqlLineageRecord Candidate(string server, string db, string schema, string table, string col) =>
        new()
        {
            OperationType = "SELECTINTO",
            SourceServer = server, SourceDatabase = db, SourceSchema = schema,
            SourceTable = table, SourceColumnName = col,
            TargetSchema = "Source", TargetTable = "tbl_TMP", TargetColumnName = col
        };

    [Fact]
    public void Local_candidate_list_collapses_to_owning_table()
    {
        var rec = Candidate("StagingServer", "StagingDb", "dbo",
            $"Orders{SqlProcedureEnricher.CandidateTableDelimiter}Customers", "CustomerName");

        var resolver = new FakeResolver(new() { ["CustomerName"] = "Customers" });
        SqlProcedureEnricher.ResolveAmbiguousColumnSources(new[] { rec }, resolver, "StagingServer");

        Assert.Equal("Customers", rec.SourceTable);
        // Local: source server equals the connection server → no linked-server hop requested.
        Assert.Null(Assert.Single(resolver.Requests).LinkedServer);
    }

    [Fact]
    public void Remote_candidate_list_routes_through_linked_server()
    {
        var rec = Candidate("LINKEDSRV", "StagingDb", "RemoteDb",
            $"remote_head{SqlProcedureEnricher.CandidateTableDelimiter}remote_detail", "amount_raw");

        var resolver = new FakeResolver(new() { ["amount_raw"] = "remote_detail" }, requireLinkedServer: "LINKEDSRV");
        SqlProcedureEnricher.ResolveAmbiguousColumnSources(new[] { rec }, resolver, "StagingServer");

        Assert.Equal("remote_detail", rec.SourceTable);
        var req = Assert.Single(resolver.Requests);
        Assert.Equal("LINKEDSRV", req.LinkedServer);
        // Both the database and the (2-part) schema slot are offered as catalog hints.
        Assert.Contains("StagingDb", req.CatalogHints);
        Assert.Contains("RemoteDb", req.CatalogHints);
    }

    [Fact]
    public void Unresolved_candidate_list_is_left_intact()
    {
        var combined = $"Orders{SqlProcedureEnricher.CandidateTableDelimiter}Customers";
        var rec = Candidate("StagingServer", "StagingDb", "dbo", combined, "Mystery");

        // Resolver knows nothing about this column → offline-safe, list preserved.
        SqlProcedureEnricher.ResolveAmbiguousColumnSources(
            new[] { rec }, new FakeResolver(new()), "StagingServer");

        Assert.Equal(combined, rec.SourceTable);
    }

    [Fact]
    public void Non_candidate_records_are_untouched()
    {
        var rec = Candidate("StagingServer", "StagingDb", "sales", "Orders", "OrderId");
        SqlProcedureEnricher.ResolveAmbiguousColumnSources(
            new[] { rec }, new FakeResolver(new() { ["OrderId"] = "SomethingElse" }), "StagingServer");

        Assert.Equal("Orders", rec.SourceTable);
    }

    [Fact]
    public void Dummy_placeholder_server_is_not_treated_as_linked_server()
    {
        var rec = Candidate("DUMMY", "DUMMY", "DUMMY",
            $"a{SqlProcedureEnricher.CandidateTableDelimiter}b", "col");

        var resolver = new FakeResolver(new() { ["col"] = "b" });
        SqlProcedureEnricher.ResolveAmbiguousColumnSources(new[] { rec }, resolver, "StagingServer");

        // DUMMY (offline placeholder) must not become a linked-server name or a catalog hint.
        var req = Assert.Single(resolver.Requests);
        Assert.Null(req.LinkedServer);
        Assert.DoesNotContain("DUMMY", req.CatalogHints);
    }
}
