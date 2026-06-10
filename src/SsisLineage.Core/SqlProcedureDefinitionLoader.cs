using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace SsisLineage.Core
{
    public class SqlProcedureDefinitionLoader
    {
        private readonly string _connectionString;

        public SqlProcedureDefinitionLoader(string connectionString)
        {
            _connectionString = connectionString;
        }

        public string? TryLoadDefinition(string procedureReference)
        {
            if (string.IsNullOrWhiteSpace(_connectionString)
                || !TryParseProcedureReference(procedureReference, out var schema, out var name))
            {
                return null;
            }
            var effectiveConnectionString = NormalizeToSqlClientConnectionString(_connectionString);
            using var connection = new SqlConnection(effectiveConnectionString);
            connection.Open();

            // When schema is known, prefer a precise schema+name lookup first
            if (!string.IsNullOrEmpty(schema))
            {
                using var schemaCmd = connection.CreateCommand();
                schemaCmd.CommandText = """
                    SELECT sm.definition
                    FROM sys.sql_modules sm
                    INNER JOIN sys.objects o ON sm.object_id = o.object_id
                    INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
                    WHERE s.name = @schemaName
                      AND o.name = @procedureName
                      AND o.type IN ('P', 'PC')
                    """;
                schemaCmd.Parameters.AddWithValue("@schemaName", schema);
                schemaCmd.Parameters.AddWithValue("@procedureName", name);
                var definition = schemaCmd.ExecuteScalar() as string;
                if (!string.IsNullOrWhiteSpace(definition))
                {
                    return definition;
                }
            }

            // Fall back to name-only search (handles cases where schema was guessed wrong)
            using var nameCmd = connection.CreateCommand();
            nameCmd.CommandText = """
                SELECT sm.definition
                FROM sys.sql_modules sm
                INNER JOIN sys.objects o ON sm.object_id = o.object_id
                WHERE o.name = @procedureName
                  AND o.type IN ('P', 'PC')
                """;
            nameCmd.Parameters.AddWithValue("@procedureName", name);
            return nameCmd.ExecuteScalar() as string;
        }

        /// <summary>
        /// Returns linked-server name → data source (actual server) from sys.servers on this
        /// connection. Returns an empty map when the query fails (offline, no permission) —
        /// callers treat the map as best-effort.
        /// </summary>
        public Dictionary<string, string> TryLoadLinkedServerMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(_connectionString)) return map;

            try
            {
                var effectiveConnectionString = NormalizeToSqlClientConnectionString(_connectionString);
                using var connection = new SqlConnection(effectiveConnectionString);
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    SELECT name, data_source
                    FROM sys.servers
                    WHERE is_linked = 1
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var name = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    var dataSource = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(dataSource))
                        map[name] = dataSource;
                }
            }
            catch
            {
                // Best-effort: no linked-server resolution without a reachable connection.
            }
            return map;
        }

        /// <summary>Returns (server, database) from any supported connection string format.</summary>
        public static (string Server, string Database) ExtractServerAndDatabase(string conn)
        {
            if (string.IsNullOrWhiteSpace(conn)) return ("", "");
            try
            {
                var builder = new DbConnectionStringBuilder { ConnectionString = conn };

                // OLE DB / ODBC style
                if (builder.ContainsKey("Provider"))
                {
                    object? tmp;
                    var server = "";
                    var db = "";
                    if (builder.TryGetValue("Data Source", out tmp) || builder.TryGetValue("Server", out tmp) || builder.TryGetValue("Address", out tmp))
                        server = tmp?.ToString() ?? "";
                    if (builder.TryGetValue("Initial Catalog", out tmp) || builder.TryGetValue("Database", out tmp))
                        db = tmp?.ToString() ?? "";
                    return (server, db);
                }

                // SqlClient style
                var csb = new SqlConnectionStringBuilder(conn);
                return (csb.DataSource ?? "", csb.InitialCatalog ?? "");
            }
            catch { return ("", ""); }
        }

        public static string NormalizeToSqlClientConnectionString(string conn)
        {
            if (string.IsNullOrWhiteSpace(conn)) return conn;

            try
            {
                var builder = new DbConnectionStringBuilder();
                builder.ConnectionString = conn;

                SqlConnectionStringBuilder sqlBuilder;

                // Already a SqlClient-style string — parse it so we can ensure TrustServerCertificate
                if (!builder.ContainsKey("Provider") && (builder.ContainsKey("Data Source") || builder.ContainsKey("Server") || builder.ContainsKey("Initial Catalog") || builder.ContainsKey("Database")))
                {
                    sqlBuilder = new SqlConnectionStringBuilder(conn);
                }
                else
                {
                    // Build a SqlClient connection string from common OLE DB / provider keys
                    sqlBuilder = new SqlConnectionStringBuilder();

                    object? tmp;
                    if (builder.TryGetValue("Data Source", out tmp) || builder.TryGetValue("Server", out tmp) || builder.TryGetValue("Address", out tmp))
                        sqlBuilder.DataSource = tmp?.ToString() ?? sqlBuilder.DataSource;

                    if (builder.TryGetValue("Initial Catalog", out tmp) || builder.TryGetValue("Database", out tmp))
                        sqlBuilder.InitialCatalog = tmp?.ToString() ?? sqlBuilder.InitialCatalog;

                    if (builder.TryGetValue("Integrated Security", out tmp) || builder.TryGetValue("Trusted_Connection", out tmp))
                    {
                        var s = tmp?.ToString();
                        if (!string.IsNullOrEmpty(s) && (s.Equals("SSPI", StringComparison.OrdinalIgnoreCase) || s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase)))
                            sqlBuilder.IntegratedSecurity = true;
                    }

                    if (builder.TryGetValue("User ID", out tmp) || builder.TryGetValue("UID", out tmp))
                        sqlBuilder.UserID = tmp?.ToString() ?? sqlBuilder.UserID;

                    if (builder.TryGetValue("Password", out tmp) || builder.TryGetValue("PWD", out tmp))
                        sqlBuilder.Password = tmp?.ToString() ?? sqlBuilder.Password;

                    if (builder.TryGetValue("TrustServerCertificate", out tmp) || builder.TryGetValue("Trust Server Certificate", out tmp))
                    {
                        if (bool.TryParse(tmp?.ToString(), out var trust))
                            sqlBuilder.TrustServerCertificate = trust;
                    }

                    if (string.IsNullOrEmpty(sqlBuilder.DataSource))
                        return conn;
                }

                // Microsoft.Data.SqlClient v4+ defaults TrustServerCertificate to false.
                // SSIS projects target internal servers; ensure the flag is set so resolved
                // .conmgr connection strings don't fail with SSL chain errors.
                if (!sqlBuilder.TrustServerCertificate)
                    sqlBuilder.TrustServerCertificate = true;

                return sqlBuilder.ConnectionString;
            }
            catch { /* Fall back to original connection string on any error */ }

            return conn;
        }

        public static bool TryParseProcedureReference(string value, out string schema, out string name)
        {
            schema = string.Empty;
            name = string.Empty;

            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var trim = value.Trim();
            var lower = trim.ToLowerInvariant();
            if (lower.StartsWith("execute ") || lower.StartsWith("exec "))
            {
                var firstSpace = trim.IndexOf(' ');
                if (firstSpace >= 0 && firstSpace + 1 < trim.Length)
                {
                    trim = trim.Substring(firstSpace + 1).Trim();
                }
            }

            if (trim.Length == 0)
            {
                return false;
            }

            var tokens = trim.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (tokens.Count == 0)
            {
                return false;
            }

            var firstToken = tokens[0];
            if (tokens.Count >= 3 && tokens[0].StartsWith("@") && tokens[1] == "=")
            {
                firstToken = tokens[2];
            }

            firstToken = firstToken.Trim().TrimEnd(';', ',');
            var parts = firstToken.Split('.');
            var cleanedParts = new List<string>(parts.Length);
            foreach (var part in parts)
            {
                var cleaned = part.Trim().Trim('[', ']', ' ', ';', ',');
                if (!string.IsNullOrEmpty(cleaned))
                {
                    cleanedParts.Add(cleaned);
                }
            }

            if (cleanedParts.Count == 0)
            {
                return false;
            }

            name = cleanedParts[^1];
            schema = cleanedParts.Count > 1 ? cleanedParts[^2] : "dbo";

            // Reject bare SQL DML/DDL keywords — not a procedure reference
            var sqlKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "select", "insert", "update", "delete", "merge", "truncate",
                  "create", "alter", "drop", "with", "declare", "set", "if", "begin", "end" };
            if (sqlKeywords.Contains(name))
                return false;

            return true;
        }
    }
}
