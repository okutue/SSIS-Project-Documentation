using System;
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
            bool includeExecuteSqlTasks)
        {
            var connectionResolver = new SsisConnectionManagerResolver(projectDirectory);
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

                return;
            }

            if (string.IsNullOrWhiteSpace(overrideConnectionString) && connectionResolver.ConnectionStrings.Count > 0)
            {
                graph.Warnings.Add(
                    $"Resolved SQL connection from project .conmgr ({connectionResolver.ConnectionStrings.Count} connection manager(s)).");
            }

            var defaultLoader = new SqlProcedureDefinitionLoader(defaultConnectionString);
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

                var definition = loader.TryLoadDefinition(component.SqlQueryOrTable);
                if (string.IsNullOrWhiteSpace(definition))
                {
                    graph.Warnings.Add($"Stored procedure definition not found: {schema}.{procName} (task/component: {component.Name})");
                    continue;
                }

                // Derive server/database from the resolved connection string (handles OLE DB + SqlClient formats)
                var (connServer, connDatabase) = SqlProcedureDefinitionLoader.ExtractServerAndDatabase(componentConnection);

                var sqlRecords = SqlProcedureParser.Parse(definition, connDatabase, connServer);
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
            EnrichXmlFallbackMappings(graph, connectionResolver, defaultConnectionString);
        }

        private static void EnrichXmlFallbackMappings(
            LineageGraph graph,
            SsisConnectionManagerResolver connectionResolver,
            string defaultConnectionString)
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

                    var (schema, table) = ParseSchemaTable(srcComp.SqlQueryOrTable);
                    if (!string.IsNullOrEmpty(table))
                    {
                        map.SourceSchema = schema;
                        map.SourceTable  = table;
                        map.SourceComponentName = string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";
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

        // Parses [schema].[table] or schema.table. Returns ("","") for multi-line SQL.
        private static (string schema, string table) ParseSchemaTable(string? sqlOrTable)
        {
            if (string.IsNullOrWhiteSpace(sqlOrTable)) return ("", "");
            var trim = sqlOrTable.Trim();

            // Skip multi-line SQL or bare SELECT/FROM blocks
            if (trim.Contains('\n') || trim.Contains('\r') ||
                trim.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
                trim.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
            {
                // Try to extract proc/table reference from EXEC statement
                if (SqlProcedureDefinitionLoader.TryParseProcedureReference(trim, out var ps, out var pn))
                    return (ps, pn);
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
