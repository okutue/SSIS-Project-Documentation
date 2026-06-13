using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SsisLineage.Core;
using SsisLineage.Core.Models;

namespace SsisLineage.Mcp
{
    /// <summary>
    /// Minimal Model Context Protocol server (stdio, newline-delimited JSON-RPC 2.0) that
    /// exposes the SSIS lineage engine to any MCP client (Claude Desktop, Cursor, VS Code).
    /// Wraps the real engine (LineageScanService + LineageTracer) — no logic duplication.
    /// </summary>
    internal static class Program
    {
        private const string ProtocolVersion = "2024-11-05";
        private static LineageGraph? _graph;
        private static LineageTracer? _tracer;

        private static readonly StreamWriter Out = new(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

        private static int Main()
        {
            // stdout is reserved for JSON-RPC (held by `Out`). Redirect Console.Out to stderr
            // so the engine's progress logging (Console.WriteLine) can't corrupt the protocol.
            Console.SetOut(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });

            SqlServerAssemblyResolver.Register(); // harmless off-Windows; XML parser is used anyway
            using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

            string? line;
            while ((line = stdin.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonNode? req;
                try { req = JsonNode.Parse(line); }
                catch { continue; } // ignore non-JSON lines
                if (req is null) continue;

                var id = req["id"];
                var method = (string?)req["method"];
                if (method is null) continue;

                try
                {
                    switch (method)
                    {
                        case "initialize": Respond(id, InitializeResult()); break;
                        case "tools/list": Respond(id, ToolsList()); break;
                        case "tools/call": Respond(id, ToolsCall(req["params"])); break;
                        case "ping": Respond(id, new JsonObject()); break;
                        case "notifications/initialized":
                        case "notifications/cancelled":
                            break; // notifications — no response
                        default:
                            if (id != null) RespondError(id, -32601, $"Method not found: {method}");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[mcp] " + ex);
                    if (id != null) RespondError(id, -32603, ex.Message);
                }
            }
            return 0;
        }

        // ── JSON-RPC plumbing ────────────────────────────────────────────────

        private static void Send(JsonObject msg) => Out.Write(msg.ToJsonString() + "\n");

        private static void Respond(JsonNode? id, JsonNode result) =>
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result });

        private static void RespondError(JsonNode? id, int code, string message) =>
            Send(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
            });

        // ── MCP handshake ────────────────────────────────────────────────────

        private static JsonObject InitializeResult() => new()
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "ssis-lineage", ["version"] = "0.1.0" },
        };

        // ── tools/list ───────────────────────────────────────────────────────

        private static JsonObject Tool(string name, string description, JsonObject props, params string[] required)
        {
            var req = new JsonArray();
            foreach (var r in required) req.Add(r);
            return new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = req },
            };
        }

        private static JsonObject Str(string desc) => new() { ["type"] = "string", ["description"] = desc };
        private static JsonObject Bool(string desc) => new() { ["type"] = "boolean", ["description"] = desc };
        private static JsonObject Enum(string desc, params string[] values)
        {
            var arr = new JsonArray();
            foreach (var v in values) arr.Add(v);
            return new JsonObject { ["type"] = "string", ["enum"] = arr, ["description"] = desc };
        }

        private static JsonObject ToolsList() => new()
        {
            ["tools"] = new JsonArray
            {
                Tool("scan_project",
                    "Scan an SSIS project (.dtproj folder) and load its lineage into the server session. Run this first.",
                    new JsonObject
                    {
                        ["projectPath"] = Str("Path to the .dtproj file or the folder containing it."),
                        ["startPackage"] = Str("Entry/master package, e.g. Master.dtsx."),
                        ["includeSqlProcedures"] = Bool("Resolve stored-procedure lineage from SQL Server (needs a reachable connection)."),
                        ["sqlConnectionString"] = Str("Connection string for proc enrichment; resolves from .conmgr if omitted."),
                    }, "projectPath", "startPackage"),

                Tool("search",
                    "Search the loaded project for columns or tables by substring. Returns matching fully-qualified names.",
                    new JsonObject
                    {
                        ["term"] = Str("Substring to match."),
                        ["scope"] = Enum("Search columns (default) or tables.", "column", "table"),
                    }, "term"),

                Tool("trace",
                    "Trace data lineage for a column or table. direction upstream=origins, downstream=impact, both=full path. Returns ordered hops.",
                    new JsonObject
                    {
                        ["target"] = Str("Column or table, e.g. 'DW.Dim_Customers.Email' or 'DW.Dim_Customers'."),
                        ["direction"] = Enum("upstream=origins, downstream=impact, both=full (default).", "both", "upstream", "downstream"),
                    }, "target"),

                Tool("status",
                    "Report whether a project is loaded and its counts (packages, tasks, components, column mappings).",
                    new JsonObject()),
            },
        };

        // ── tools/call ───────────────────────────────────────────────────────

        private static JsonObject ToolsCall(JsonNode? p)
        {
            var name = (string?)p?["name"] ?? "";
            var args = p?["arguments"] as JsonObject ?? new JsonObject();
            try
            {
                var text = name switch
                {
                    "scan_project" => ScanProject(args),
                    "search" => Search(args),
                    "trace" => Trace(args),
                    "status" => Status(),
                    _ => throw new InvalidOperationException($"Unknown tool: {name}"),
                };
                return ToolText(text, false);
            }
            catch (Exception ex)
            {
                return ToolText($"Error: {ex.Message}", true);
            }
        }

        private static JsonObject ToolText(string text, bool isError) => new()
        {
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
            ["isError"] = isError,
        };

        // ── tool implementations (reuse the real engine) ─────────────────────

        private static string ScanProject(JsonObject a)
        {
            var projectPath = (string?)a["projectPath"] ?? throw new ArgumentException("projectPath is required");
            var startPackage = (string?)a["startPackage"] ?? throw new ArgumentException("startPackage is required");
            var includeProcs = (bool?)a["includeSqlProcedures"] ?? false;
            var conn = (string?)a["sqlConnectionString"] ?? "";

            var outDir = Directory.CreateTempSubdirectory("ssis-mcp-").FullName;
            var result = new LineageScanService().Scan(new LineageScanOptions
            {
                ProjectPath = projectPath,
                StartPackage = startPackage,
                OutputDirectory = outDir,
                UseCache = false,
                IncludeSqlProcedures = includeProcs,
                SqlConnectionString = conn,
            });

            _graph = result.Graph;
            _tracer = new LineageTracer(_graph);
            try { Directory.Delete(outDir, true); } catch { /* best effort */ }

            return $"Scanned. Packages: {_graph.Packages.Count}, Tasks: {_graph.Tasks.Count}, " +
                   $"Components: {_graph.Components.Count}, Column mappings: {_graph.ColumnMappings.Count}.";
        }

        private static string Search(JsonObject a)
        {
            var tracer = _tracer ?? throw new InvalidOperationException("No project loaded. Call scan_project first.");
            var term = (string?)a["term"] ?? "";
            var scope = ((string?)a["scope"] == "table") ? SearchScope.Table : SearchScope.Column;
            var hits = tracer.Search(term, scope, 50).ToList();
            if (hits.Count == 0) return $"No {scope.ToString().ToLowerInvariant()}s match \"{term}\".";
            return $"{hits.Count} match(es):\n" + string.Join("\n", hits.Select(h => "- " + h.Display));
        }

        private static string Trace(JsonObject a)
        {
            var tracer = _tracer ?? throw new InvalidOperationException("No project loaded. Call scan_project first.");
            var target = (string?)a["target"] ?? "";
            var direction = (string?)a["direction"] switch
            {
                "upstream" => TraceDirection.Upstream,
                "downstream" => TraceDirection.Downstream,
                _ => TraceDirection.Both,
            };

            bool Eq(SearchHit h) => string.Equals(h.Display, target, StringComparison.OrdinalIgnoreCase);
            var cols = tracer.Search(target, SearchScope.Column, 50).ToList();
            var tbls = tracer.Search(target, SearchScope.Table, 50).ToList();
            var hit = cols.FirstOrDefault(Eq) ?? tbls.FirstOrDefault(Eq) ?? cols.FirstOrDefault() ?? tbls.FirstOrDefault();
            if (hit is null) return $"Nothing in the lineage matches \"{target}\".";

            var trace = tracer.Trace(hit, direction);
            if (trace.Steps.Count == 0) return $"No {direction.ToString().ToLowerInvariant()} lineage for {trace.FocusLabel}.";

            var hops = trace.Steps.Select(s =>
            {
                var op = string.IsNullOrEmpty(s.Operation) ? "" : $" [{s.Operation}]";
                return $"{s.Rank + 1}. {s.SourceLabel} -> {s.TargetLabel}{op}";
            });
            return $"Lineage for {trace.FocusLabel} ({direction.ToString().ToLowerInvariant()}) — " +
                   $"{trace.Steps.Count} hops across {trace.TableCount} tables:\n" + string.Join("\n", hops);
        }

        private static string Status() =>
            _graph is null
                ? "No project loaded. Call scan_project first."
                : $"Loaded. Packages: {_graph.Packages.Count}, Tasks: {_graph.Tasks.Count}, " +
                  $"Components: {_graph.Components.Count}, Column mappings: {_graph.ColumnMappings.Count}.";
    }
}
