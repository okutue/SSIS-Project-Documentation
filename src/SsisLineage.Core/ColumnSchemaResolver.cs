using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.SqlClient;

namespace SsisLineage.Core
{
    /// <summary>
    /// A request to identify which of several candidate tables actually declares a column.
    /// Raised for unqualified columns in multi-table joins that static parsing cannot
    /// disambiguate without the table schema.
    /// </summary>
    public sealed class ColumnResolutionRequest
    {
        /// <summary>Linked-server name when the candidates live behind a linked server
        /// (queried via a 4-part name); null for tables local to the connection.</summary>
        public string? LinkedServer { get; init; }

        /// <summary>Database name(s) to try as the catalog, in order. The first that yields
        /// the column wins. Handles 2-part remote names where the database landed in the
        /// schema slot. Empty for the connection's current database.</summary>
        public IReadOnlyList<string> CatalogHints { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> CandidateTables { get; init; } = Array.Empty<string>();
        public string Column { get; init; } = "";
    }

    public interface IColumnSchemaResolver
    {
        /// <summary>Returns the single candidate table that declares <see cref="ColumnResolutionRequest.Column"/>,
        /// or null when it cannot be uniquely determined (offline, not found, or found in &gt;1 candidate).</summary>
        string? ResolveOwningTable(ColumnResolutionRequest request);
    }

    /// <summary>Offline resolver — never resolves. The default, so resolution is opt-in via a live connection.</summary>
    public sealed class NullColumnSchemaResolver : IColumnSchemaResolver
    {
        public static readonly NullColumnSchemaResolver Instance = new();
        public string? ResolveOwningTable(ColumnResolutionRequest request) => null;
    }

    /// <summary>
    /// Resolves a column's owning table by reading INFORMATION_SCHEMA.COLUMNS on the same
    /// connection used to load the procedure. Local tables are read directly; tables behind a
    /// linked server are read via a 4-part name. Column maps are cached per (linked server,
    /// catalog). Any failure (offline, permission, bad name) yields null so the caller keeps
    /// the ambiguous candidate list.
    /// </summary>
    public sealed class SqlColumnSchemaResolver : IColumnSchemaResolver
    {
        private readonly string _connectionString;

        // "linkedServer|catalog" → (table → its column set). A null value caches a failed
        // lookup so we don't retry the same dead catalog for every column.
        private readonly Dictionary<string, Dictionary<string, HashSet<string>>?> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        public SqlColumnSchemaResolver(string connectionString) => _connectionString = connectionString;

        public string? ResolveOwningTable(ColumnResolutionRequest request)
        {
            if (request.CandidateTables.Count < 2 || string.IsNullOrWhiteSpace(request.Column)) return null;
            if (string.IsNullOrWhiteSpace(_connectionString)) return null;

            // A linked-server lookup needs an explicit catalog; a local lookup may use the
            // connection's current database (represented by a single empty hint).
            var catalogs = request.CatalogHints.Count > 0
                ? request.CatalogHints
                : string.IsNullOrEmpty(request.LinkedServer) ? new[] { "" } : Array.Empty<string>();

            foreach (var catalog in catalogs)
            {
                var map = GetColumnMap(request.LinkedServer, catalog);
                if (map == null) continue;

                var owners = request.CandidateTables
                    .Where(t => map.TryGetValue(t, out var cols) && cols.Contains(request.Column))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (owners.Count == 1) return owners[0];
                if (owners.Count > 1) return null;   // genuinely ambiguous — keep the list
            }
            return null;
        }

        private Dictionary<string, HashSet<string>>? GetColumnMap(string? linkedServer, string catalog)
        {
            var key = $"{linkedServer}|{catalog}";
            if (_cache.TryGetValue(key, out var cached)) return cached;

            Dictionary<string, HashSet<string>>? result = null;
            var prefix = BuildCatalogPrefix(linkedServer, catalog);
            if (prefix != null)
            {
                try
                {
                    using var conn = new SqlConnection(
                        SqlProcedureDefinitionLoader.NormalizeToSqlClientConnectionString(_connectionString));
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT TABLE_NAME, COLUMN_NAME FROM {prefix}INFORMATION_SCHEMA.COLUMNS";
                    using var dr = cmd.ExecuteReader();
                    result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                    while (dr.Read())
                    {
                        if (dr.IsDBNull(0) || dr.IsDBNull(1)) continue;
                        var table = dr.GetString(0);
                        var column = dr.GetString(1);
                        if (!result.TryGetValue(table, out var set))
                            result[table] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        set.Add(column);
                    }
                }
                catch
                {
                    result = null;   // offline / no permission / invalid linked server
                }
            }

            _cache[key] = result;
            return result;
        }

        // [linkedServer].[catalog]. | [catalog]. | "" (current db). A placeholder catalog
        // (empty/DUMMY) is invalid for a linked-server hop.
        private static string? BuildCatalogPrefix(string? linkedServer, string catalog)
        {
            var hasCatalog = !string.IsNullOrWhiteSpace(catalog)
                             && !catalog.Equals("DUMMY", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(linkedServer))
            {
                if (linkedServer.Equals("DUMMY", StringComparison.OrdinalIgnoreCase) || !hasCatalog)
                    return null;
                return $"{Bracket(linkedServer)}.{Bracket(catalog)}.";
            }

            return hasCatalog ? $"{Bracket(catalog)}." : "";
        }

        private static string Bracket(string id) => $"[{id.Replace("]", "]]")}]";
    }
}
