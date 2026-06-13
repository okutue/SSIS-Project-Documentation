using System;
using System.Collections.Generic;
using System.Linq;
using SsisLineage.Core.Models;

namespace SsisLineage.Core
{
    public static class SqlProcedureEnricher
    {
        public static void EnrichFromStoredProcedures(
            LineageGraph graph,
            string projectDirectory,
            string? overrideConnectionString,
            bool includeDataFlowComponents,
            bool includeExecuteSqlTasks,
            IDictionary<string, string>? linkedServerMap = null,
            bool autoResolveLinkedServers = true,
            IDictionary<string, string>? sqlVariableValues = null,
            IDictionary<string, string>? connectionManagerOverrides = null)
        {
            var connectionResolver = new SsisConnectionManagerResolver(projectDirectory, connectionManagerOverrides);
            var defaultConnectionString = overrideConnectionString;
            if (string.IsNullOrWhiteSpace(defaultConnectionString))
            {
                defaultConnectionString = connectionResolver.TryResolveFirstSqlConnectionString();
            }

            if (string.IsNullOrWhiteSpace(defaultConnectionString))
            {
                if (includeDataFlowComponents || includeExecuteSqlTasks)
                {
                    graph.Warnings.Add(
                        "SQL procedure lineage skipped: no connection string override and no .conmgr SQL connection found in the project.");
                }

                // Manual linked-server mappings still apply to records produced from package XML.
                ApplyLinkedServerMap(graph, linkedServerMap);
                return;
            }

            // Linked-server name → actual server. Auto-resolved from sys.servers on every
            // connection used to load procs; explicit entries override auto-resolved ones.
            var combinedLinkedServers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var queriedConnections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void HarvestLinkedServers(string connectionString, SqlProcedureDefinitionLoader loader)
            {
                if (!autoResolveLinkedServers) return;
                if (!queriedConnections.Add(connectionString)) return;
                foreach (var kv in loader.TryLoadLinkedServerMap())
                    combinedLinkedServers.TryAdd(kv.Key, kv.Value);
            }

            if (string.IsNullOrWhiteSpace(overrideConnectionString) && connectionResolver.ConnectionStrings.Count > 0)
            {
                graph.Warnings.Add(
                    $"Resolved SQL connection from project .conmgr ({connectionResolver.ConnectionStrings.Count} connection manager(s)).");
            }

            var defaultLoader = new SqlProcedureDefinitionLoader(defaultConnectionString);
            HarvestLinkedServers(defaultConnectionString, defaultLoader);

            // One column-schema resolver per connection string — resolves unqualified
            // columns (candidate lists) to their owning table from live schema. Local tables
            // resolve automatically; remote (linked-server) tables resolve only when
            // sqlVariableValues supplies @Server/@Database so the names are real.
            var resolvers = new Dictionary<string, SqlColumnSchemaResolver>(StringComparer.OrdinalIgnoreCase);
            SqlColumnSchemaResolver ResolverFor(string conn) =>
                resolvers.TryGetValue(conn, out var r) ? r : resolvers[conn] = new SqlColumnSchemaResolver(conn);

            // Data-flow components whose SQL is a stored proc (a proc body was loaded). Their
            // internal lineage records target the component id (table-less), so the component's
            // XML_FALLBACK side must stay keyed by component — never stamped with the proc name
            // as a table — or the two halves won't stitch into one path.
            var procBackedComponentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var component in graph.Components)
            {
                var isExecuteSql = component.Type.Contains("Execute SQL", StringComparison.OrdinalIgnoreCase);
                if (isExecuteSql && !includeExecuteSqlTasks)
                {
                    continue;
                }

                if (!isExecuteSql)
                {
                    if (!includeDataFlowComponents
                        || component.Type.Contains("Destination", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Lookup components: their reference query is the upstream of the columns
                    // they add to the flow — trace reference table → lookup component.
                    if (component.Type.Contains("Lookup", StringComparison.OrdinalIgnoreCase))
                    {
                        var lookupConnection = connectionResolver.TryResolveConnectionString(component.ConnectionManager)
                            ?? defaultConnectionString;
                        EnrichLookupReference(graph, component, lookupConnection);
                        continue;
                    }
                }

                if (!SqlProcedureDefinitionLoader.TryParseProcedureReference(component.SqlQueryOrTable, out var schema, out var procName))
                {
                    continue;
                }

                var componentConnection = connectionResolver.TryResolveConnectionString(component.ConnectionManager)
                    ?? defaultConnectionString;
                var loader = string.Equals(componentConnection, defaultConnectionString, StringComparison.OrdinalIgnoreCase)
                    ? defaultLoader
                    : new SqlProcedureDefinitionLoader(componentConnection);
                HarvestLinkedServers(componentConnection, loader);

                var definition = loader.TryLoadDefinition(component.SqlQueryOrTable);
                if (string.IsNullOrWhiteSpace(definition))
                {
                    graph.Warnings.Add($"Stored procedure definition not found: {schema}.{procName} (task/component: {component.Name})");
                    continue;
                }

                // This is a proc-backed data-flow component — keep its XML side component-keyed.
                if (!isExecuteSql)
                    procBackedComponentIds.Add(component.Id);

                // Derive server/database from the resolved connection string (handles OLE DB + SqlClient formats)
                var (connServer, connDatabase) = SqlProcedureDefinitionLoader.ExtractServerAndDatabase(componentConnection);

                var sqlRecords = SqlProcedureParser.Parse(definition, connDatabase, connServer, sqlVariableValues);

                // Resolve unqualified-column candidate lists to their owning table via live schema.
                ResolveAmbiguousColumnSources(sqlRecords, ResolverFor(componentConnection), connServer);

                foreach (var record in sqlRecords)
                {
                    var srcTable  = string.IsNullOrWhiteSpace(record.SourceTable) ? record.ProcedureName : record.SourceTable;
                    var srcSchema = record.SourceSchema;
                    var tgtTable  = string.IsNullOrWhiteSpace(record.TargetTable) ? "" : record.TargetTable;
                    var tgtSchema = record.TargetSchema;

                    graph.ColumnMappings.Add(new ColumnMap
                    {
                        PackageId = component.PackageId,
                        TaskId = component.TaskId,
                        SourceComponentId = $"{component.Id}::{srcSchema}.{srcTable}",
                        SourceComponentName = string.IsNullOrWhiteSpace(record.SourceTable)
                            ? record.ProcedureName
                            : $"{srcSchema}.{srcTable}",
                        SourceServer   = record.SourceServer,
                        SourceDatabase = record.SourceDatabase,
                        SourceSchema   = srcSchema,
                        SourceTable    = srcTable,
                        SourceColumnName = record.SourceColumnName,
                        SourceExpression = record.SourceExpression,
                        TargetComponentId = component.Id,
                        TargetComponentName = string.IsNullOrWhiteSpace(tgtTable)
                            ? component.Name
                            : $"{tgtSchema}.{tgtTable}",
                        TargetServer   = record.TargetServer,
                        TargetDatabase = record.TargetDatabase,
                        TargetSchema   = tgtSchema,
                        TargetTable    = tgtTable,
                        TargetColumnName = record.TargetColumnName,
                        OperationType    = $"SQL_PROC_{record.OperationType}",
                        ProcedureName    = $"{schema}.{procName}",
                        JoinDetails      = record.JoinDetails,
                        FilterConditions = record.FilterConditions
                    });
                }

                if (sqlRecords.Count == 0)
                {
                    graph.Warnings.Add($"No column lineage extracted from procedure {schema}.{procName} ({component.Name}).");
                }
            }

            // Enrich XML_FALLBACK mappings (SSIS data flow OLE DB columns) with connection/table metadata
            EnrichXmlFallbackMappings(graph, connectionResolver, defaultConnectionString, procBackedComponentIds);

            // Explicit mappings win over sys.servers auto-resolution.
            if (linkedServerMap != null)
            {
                foreach (var kv in linkedServerMap)
                    if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                        combinedLinkedServers[kv.Key] = kv.Value;
            }

            if (combinedLinkedServers.Count > 0)
            {
                graph.Warnings.Add(
                    $"Linked-server resolution active: {combinedLinkedServers.Count} mapping(s) " +
                    $"({string.Join(", ", combinedLinkedServers.Select(kv => $"{kv.Key} → {kv.Value}"))}).");
            }

            ApplyLinkedServerMap(graph, combinedLinkedServers);
        }

        /// <summary>
        /// Adds reference-table → lookup-component column mappings from a Lookup's reference
        /// query (SELECT mode) or reference table (table mode), so columns the lookup ADDS to
        /// the data flow (e.g. Dim_X_ID) trace back to the table they came from.
        /// </summary>
        private static void EnrichLookupReference(LineageGraph graph, ComponentNode component, string connection)
        {
            var sql = component.SqlQueryOrTable;
            if (string.IsNullOrWhiteSpace(sql)) return;

            var (server, database) = SqlProcedureDefinitionLoader.ExtractServerAndDatabase(connection);
            var trim = sql.Trim();

            // Table mode: reference is a bare table name — emit a *→* pass-through edge.
            if (!trim.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                !trim.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
            {
                var (schema, table) = ParseSchemaTable(trim);
                if (string.IsNullOrEmpty(table)) return;
                graph.ColumnMappings.Add(new ColumnMap
                {
                    PackageId = component.PackageId,
                    TaskId = component.TaskId,
                    SourceComponentId = $"{component.Id}::{schema}.{table}",
                    SourceComponentName = string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}",
                    SourceServer = server,
                    SourceDatabase = database,
                    SourceSchema = schema,
                    SourceTable = table,
                    SourceColumnName = "*",
                    TargetComponentId = component.Id,
                    TargetComponentName = component.Name,
                    TargetServer = server,
                    TargetDatabase = database,
                    TargetColumnName = "*",
                    OperationType = "LOOKUP_REF"
                });
                return;
            }

            // SELECT mode: parse the reference query for table/column sources.
            var records = SqlProcedureParser.Parse(sql, database, server);
            foreach (var rec in records)
            {
                if (string.IsNullOrWhiteSpace(rec.SourceTable) || string.IsNullOrWhiteSpace(rec.SourceColumnName))
                    continue;

                graph.ColumnMappings.Add(new ColumnMap
                {
                    PackageId = component.PackageId,
                    TaskId = component.TaskId,
                    SourceComponentId = $"{component.Id}::{rec.SourceSchema}.{rec.SourceTable}",
                    SourceComponentName = string.IsNullOrEmpty(rec.SourceSchema)
                        ? rec.SourceTable
                        : $"{rec.SourceSchema}.{rec.SourceTable}",
                    SourceServer = string.IsNullOrEmpty(rec.SourceServer) ? server : rec.SourceServer,
                    SourceDatabase = string.IsNullOrEmpty(rec.SourceDatabase) ? database : rec.SourceDatabase,
                    SourceSchema = rec.SourceSchema,
                    SourceTable = rec.SourceTable,
                    SourceColumnName = rec.SourceColumnName,
                    SourceExpression = rec.SourceExpression,
                    TargetComponentId = component.Id,
                    TargetComponentName = component.Name,
                    TargetServer = server,
                    TargetDatabase = database,
                    TargetColumnName = string.IsNullOrWhiteSpace(rec.TargetColumnName)
                        ? rec.SourceColumnName
                        : rec.TargetColumnName,
                    OperationType = "LOOKUP_REF"
                });
            }
        }

        /// <summary>Delimiter used by the parser to mark an unqualified column whose owning
        /// table is ambiguous across a multi-table FROM (e.g. "Orders | Customers").</summary>
        public const string CandidateTableDelimiter = " | ";

        /// <summary>
        /// Collapses unqualified-column candidate lists to their single owning table using a
        /// schema resolver. A record whose <c>SourceTable</c> lists several candidates is
        /// rewritten to the one table that actually declares the column; if the resolver
        /// can't decide (offline, not found, still ambiguous) the candidate list is kept.
        /// Public + resolver-injected so it is testable without a live database.
        /// </summary>
        public static void ResolveAmbiguousColumnSources(
            IEnumerable<SqlLineageRecord> records, IColumnSchemaResolver resolver, string connectionServer)
        {
            if (resolver == null) return;

            foreach (var rec in records)
            {
                if (string.IsNullOrEmpty(rec.SourceTable) ||
                    !rec.SourceTable.Contains(CandidateTableDelimiter, StringComparison.Ordinal))
                    continue;

                var candidates = rec.SourceTable
                    .Split(CandidateTableDelimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (candidates.Length < 2) continue;

                // A source server different from the connection's own server means the tables
                // sit behind a linked server (4-part lookup); same/blank means local.
                var linked = !string.IsNullOrEmpty(rec.SourceServer)
                             && !rec.SourceServer.Equals(connectionServer, StringComparison.OrdinalIgnoreCase)
                             && !rec.SourceServer.Equals("DUMMY", StringComparison.OrdinalIgnoreCase)
                    ? rec.SourceServer
                    : null;

                // For a 2-part remote name the database can land in the schema slot, so offer
                // both as catalog hints; the resolver tries them in order.
                var hints = new List<string>();
                void AddHint(string? v)
                {
                    if (!string.IsNullOrWhiteSpace(v) && !v.Equals("DUMMY", StringComparison.OrdinalIgnoreCase)
                        && !hints.Contains(v, StringComparer.OrdinalIgnoreCase))
                        hints.Add(v);
                }
                AddHint(rec.SourceDatabase);
                if (linked != null) AddHint(rec.SourceSchema);

                var owner = resolver.ResolveOwningTable(new ColumnResolutionRequest
                {
                    LinkedServer = linked,
                    CatalogHints = hints,
                    CandidateTables = candidates,
                    Column = rec.SourceColumnName
                });

                if (!string.IsNullOrEmpty(owner))
                    rec.SourceTable = owner;
            }
        }

        /// <summary>
        /// Replaces linked-server names with the actual server name on every column mapping,
        /// so records parsed from procs ([LINKEDSRV].[Db]…) and records from SSIS connection
        /// managers report the same server in the lineage output.
        /// </summary>
        private static void ApplyLinkedServerMap(LineageGraph graph, IDictionary<string, string>? map)
        {
            if (map == null || map.Count == 0) return;

            foreach (var m in graph.ColumnMappings)
            {
                if (!string.IsNullOrEmpty(m.SourceServer) && map.TryGetValue(m.SourceServer, out var src))
                    m.SourceServer = src;
                if (!string.IsNullOrEmpty(m.TargetServer) && map.TryGetValue(m.TargetServer, out var tgt))
                    m.TargetServer = tgt;
            }
        }

        private static void EnrichXmlFallbackMappings(
            LineageGraph graph,
            SsisConnectionManagerResolver connectionResolver,
            string defaultConnectionString,
            HashSet<string> procBackedComponentIds)
        {
            // Index components by ID for fast lookup
            var compById = new System.Collections.Generic.Dictionary<string, ComponentNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in graph.Components)
                compById[c.Id] = c;

            foreach (var map in graph.ColumnMappings)
            {
                if (!string.Equals(map.OperationType, "XML_FALLBACK", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Source side (typically OLE DB Source)
                if (compById.TryGetValue(map.SourceComponentId, out var srcComp))
                {
                    var conn = connectionResolver.TryResolveConnectionString(srcComp.ConnectionManager)
                               ?? defaultConnectionString;
                    var (srv, db) = SqlProcedureDefinitionLoader.ExtractServerAndDatabase(conn);
                    map.SourceServer   = srv;
                    map.SourceDatabase = db;

                    // A proc-backed source carries its lineage through the proc's internal
                    // records (keyed by component id). Stamping the proc name on as a "table"
                    // would re-key this side and sever the stitch — so leave it component-keyed.
                    if (!procBackedComponentIds.Contains(map.SourceComponentId))
                    {
                        var (schema, table) = ParseSchemaTable(srcComp.SqlQueryOrTable);
                        if (!string.IsNullOrEmpty(table))
                        {
                            map.SourceSchema = schema;
                            map.SourceTable  = table;
                            map.SourceComponentName = string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";
                        }
                    }
                }

                // Target side (typically OLE DB Destination)
                if (compById.TryGetValue(map.TargetComponentId, out var tgtComp))
                {
                    var conn = connectionResolver.TryResolveConnectionString(tgtComp.ConnectionManager)
                               ?? defaultConnectionString;
                    var (srv, db) = SqlProcedureDefinitionLoader.ExtractServerAndDatabase(conn);
                    map.TargetServer   = srv;
                    map.TargetDatabase = db;

                    if (!procBackedComponentIds.Contains(map.TargetComponentId))
                    {
                        var (schema, table) = ParseSchemaTable(tgtComp.SqlQueryOrTable);
                        if (!string.IsNullOrEmpty(table))
                        {
                            map.TargetSchema = schema;
                            map.TargetTable  = table;
                            map.TargetComponentName = string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";
                        }
                    }
                }
            }
        }

        // Parses [schema].[table], "schema"."table" (ADO NET), or schema.table.
        // Returns ("","") for SQL statements and EXEC proc references: a proc-backed
        // component must stay keyed by component id so the proc's internal lineage records
        // (which target the component) stitch to the data-flow rows that read from it.
        private static (string schema, string table) ParseSchemaTable(string? sqlOrTable)
        {
            if (string.IsNullOrWhiteSpace(sqlOrTable)) return ("", "");
            var trim = sqlOrTable.Trim();

            if (trim.StartsWith("EXEC ", StringComparison.OrdinalIgnoreCase) ||
                trim.StartsWith("EXECUTE ", StringComparison.OrdinalIgnoreCase) ||
                trim.StartsWith("EXEC[", StringComparison.OrdinalIgnoreCase) ||
                trim.StartsWith("EXECUTE[", StringComparison.OrdinalIgnoreCase))
            {
                return ("", "");
            }

            // ADO NET TableOrViewName quotes identifiers: "Load_DW"."STAGE_Fact_Sales"
            if (trim.Contains('"')) trim = trim.Replace("\"", "");

            // Skip multi-line SQL or bare SELECT/FROM blocks
            if (trim.Contains('\n') || trim.Contains('\r') ||
                trim.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
                trim.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
            {
                return ("", "");
            }

            // [Schema].[Table] form
            if (trim.StartsWith("[") && trim.Contains("].["))
            {
                var inner = trim.TrimStart('[');
                var sep = inner.IndexOf("].[", StringComparison.Ordinal);
                if (sep > 0)
                    return (inner[..sep].Trim('[', ']'), inner[(sep + 3)..].Trim('[', ']'));
            }

            // schema.table form (may have dots inside brackets — handle simply)
            if (trim.Contains('.'))
            {
                var dotIdx = trim.LastIndexOf('.');
                return (trim[..dotIdx].Trim('[', ']', ' '), trim[(dotIdx + 1)..].Trim('[', ']', ' '));
            }

            return ("", trim.Trim('[', ']'));
        }
    }
}
