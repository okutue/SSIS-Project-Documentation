using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SsisLineage.Core;
using SsisLineage.Core.Models;

namespace SsisLineage.Cli
{
    class Program
    {
        static int Main(string[] args)
        {
            Console.WriteLine("========================================");
            Console.WriteLine("    SSIS Project Lineage Utility");
            Console.WriteLine("========================================");

            if (args.Length == 0 || args[0].Equals("help", StringComparison.OrdinalIgnoreCase) || args[0].Equals("--help", StringComparison.OrdinalIgnoreCase))
            {
                PrintUsage();
                return 0;
            }

            if (args[0].Equals("scan", StringComparison.OrdinalIgnoreCase))
            {
                return RunScan(args.Skip(1).ToArray());
            }

            if (args[0].Equals("diff", StringComparison.OrdinalIgnoreCase))
            {
                return RunDiff(args.Skip(1).ToArray());
            }

            Console.WriteLine($"Unknown command: {args[0]}");
            PrintUsage();
            return 2;
        }

        /// <summary>
        /// Compares two lineage.json exports (e.g. main vs PR branch) and reports drift.
        /// Exit codes: 0 = no changes, 1 = changes detected (with --fail-on-changes), 2 = error.
        /// </summary>
        static int RunDiff(string[] diffArgs)
        {
            var positional = new List<string>();
            string? outputPath = null;
            var failOnChanges = false;

            for (int i = 0; i < diffArgs.Length; i++)
            {
                if ((diffArgs[i] == "--output" || diffArgs[i] == "-o") && i + 1 < diffArgs.Length)
                {
                    outputPath = diffArgs[++i];
                }
                else if (diffArgs[i] == "--fail-on-changes")
                {
                    failOnChanges = true;
                }
                else
                {
                    positional.Add(diffArgs[i]);
                }
            }

            if (positional.Count != 2)
            {
                Console.WriteLine("[Error] diff requires exactly two lineage.json files: diff <old.json> <new.json>");
                PrintUsage();
                return 2;
            }

            try
            {
                var oldGraph = JsonSerializer.Deserialize<LineageGraph>(File.ReadAllText(positional[0])) ?? new LineageGraph();
                var newGraph = JsonSerializer.Deserialize<LineageGraph>(File.ReadAllText(positional[1])) ?? new LineageGraph();

                var diff = LineageDiff.Compare(oldGraph, newGraph);
                var markdown = LineageDiff.GenerateMarkdown(diff);

                Console.WriteLine(markdown);
                if (!string.IsNullOrEmpty(outputPath))
                {
                    File.WriteAllText(outputPath, markdown);
                    Console.WriteLine($"[*] Diff report written to: {Path.GetFullPath(outputPath)}");
                }

                if (diff.HasChanges && failOnChanges)
                {
                    Console.WriteLine($"[!] {diff.TotalChanges} lineage change(s) detected — failing as requested.");
                    return 1;
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Fatal Error] Diff failed: {ex.Message}");
                return 2;
            }
        }

        static int RunScan(string[] scanArgs)
        {
            var options = new LineageScanOptions();

            for (int i = 0; i < scanArgs.Length; i++)
            {
                if (scanArgs[i] == "--variable-overrides" && i + 1 < scanArgs.Length)
                {
                    var overridesFile = scanArgs[++i];
                    try
                    {
                        options.VariableOverrides = JsonSerializer.Deserialize<Dictionary<string, string>>(
                            File.ReadAllText(overridesFile)) ?? new Dictionary<string, string>();
                        Console.WriteLine($"[*] Loaded {options.VariableOverrides.Count} variable override(s) from {overridesFile}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Error] Failed to read variable overrides from {overridesFile}: {ex.Message}");
                        return 2;
                    }
                    continue;
                }
                if ((scanArgs[i] == "--project-path" || scanArgs[i] == "-p") && i + 1 < scanArgs.Length)
                {
                    options.ProjectPath = scanArgs[++i];
                }
                else if ((scanArgs[i] == "--start-package" || scanArgs[i] == "-s") && i + 1 < scanArgs.Length)
                {
                    options.StartPackage = scanArgs[++i];
                }
                else if ((scanArgs[i] == "--output" || scanArgs[i] == "-o") && i + 1 < scanArgs.Length)
                {
                    options.OutputDirectory = scanArgs[++i];
                }
                else if (scanArgs[i] == "--no-cache")
                {
                    options.UseCache = false;
                }
                else if (scanArgs[i] == "--include-sql-procedures")
                {
                    options.IncludeSqlProcedures = true;
                }
                else if (scanArgs[i] == "--sql-connection-string" && i + 1 < scanArgs.Length)
                {
                    options.SqlConnectionString = scanArgs[++i];
                }
                else if (scanArgs[i] == "--linked-servers" && i + 1 < scanArgs.Length)
                {
                    var linkedServersFile = scanArgs[++i];
                    try
                    {
                        options.LinkedServerMap = JsonSerializer.Deserialize<Dictionary<string, string>>(
                            File.ReadAllText(linkedServersFile)) ?? new Dictionary<string, string>();
                        Console.WriteLine($"[*] Loaded {options.LinkedServerMap.Count} linked-server mapping(s) from {linkedServersFile}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Error] Failed to read linked-server mappings from {linkedServersFile}: {ex.Message}");
                        return 2;
                    }
                }
                else if (scanArgs[i] == "--sql-variables" && i + 1 < scanArgs.Length)
                {
                    var sqlVarsFile = scanArgs[++i];
                    try
                    {
                        options.SqlVariableValues = JsonSerializer.Deserialize<Dictionary<string, string>>(
                            File.ReadAllText(sqlVarsFile)) ?? new Dictionary<string, string>();
                        Console.WriteLine($"[*] Loaded {options.SqlVariableValues.Count} SQL variable value(s) from {sqlVarsFile}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Error] Failed to read SQL variable values from {sqlVarsFile}: {ex.Message}");
                        return 2;
                    }
                }
            }

            if (string.IsNullOrEmpty(options.ProjectPath) || string.IsNullOrEmpty(options.StartPackage))
            {
                Console.WriteLine("[Error] Missing required arguments: --project-path and --start-package are required.");
                PrintUsage();
                return 2;
            }

            try
            {
                Console.WriteLine($"[*] Loading project: {options.ProjectPath}");
                Console.WriteLine($"[*] Start package: {options.StartPackage}");

                var result = new LineageScanService().Scan(options);
                var graph = result.Graph;

                Console.WriteLine($"[*] Project file: {result.ProjectFilePath}");
                Console.WriteLine($"[*] Project directory: {result.Project.ProjectDirectory}");
                Console.WriteLine($"[*] Discovered packages: {result.Project.Packages.Count}");
                Console.WriteLine(result.CacheHit ? "[*] Cache hit. Loaded lineage from cache." : "[*] Cache miss. Scan completed and cache updated.");
                Console.WriteLine($"[*] Wrote output files to: {result.OutputDirectory}");

                Console.WriteLine("========================================");
                Console.WriteLine("[OK] Success! SSIS Project Lineage scan completed.");
                Console.WriteLine($"   Packages:   {graph.Packages.Count}");
                Console.WriteLine($"   Tasks:      {graph.Tasks.Count}");
                Console.WriteLine($"   Components: {graph.Components.Count}");
                Console.WriteLine($"   Mappings:   {graph.ColumnMappings.Count}");
                Console.WriteLine("========================================");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Fatal Error] Scan failed: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return 2;
            }
        }

        static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  ssis-lineage scan --project-path <path> --start-package <name> [--output <dir>] [--no-cache] [--include-sql-procedures] [--sql-connection-string <connection-string>] [--variable-overrides <file.json>] [--linked-servers <file.json>] [--sql-variables <file.json>]");
            Console.WriteLine("  ssis-lineage diff <old-lineage.json> <new-lineage.json> [--output <report.md>] [--fail-on-changes]");
            Console.WriteLine();
            Console.WriteLine("scan options:");
            Console.WriteLine("  -p, --project-path    Path to the SSIS .dtproj file or its containing directory");
            Console.WriteLine("  -s, --start-package   Name of the starting entry package (e.g. Master.dtsx)");
            Console.WriteLine("  -o, --output          Directory where lineage outputs will be written (default: ./lineage-output)");
            Console.WriteLine("      --no-cache        Force a fresh scan instead of using the package hash cache");
            Console.WriteLine("      --include-sql-procedures");
            Console.WriteLine("                         Connect to SQL Server and retrieve stored procedure definitions for SQL lineage");
            Console.WriteLine("      --sql-connection-string");
            Console.WriteLine("                         SQL Server connection string used only when --include-sql-procedures is set");
            Console.WriteLine("      --variable-overrides");
            Console.WriteLine("                         JSON file of \"Namespace::Name\": \"value\" pairs applied over design-time values");
            Console.WriteLine("                         (e.g. values extracted from an SSIS catalog environment — see docs/CI.md)");
            Console.WriteLine("      --linked-servers");
            Console.WriteLine("                         JSON file of \"LinkedServerName\": \"ActualServerName\" pairs. Linked servers are");
            Console.WriteLine("                         auto-resolved from sys.servers when a SQL connection is available; entries in");
            Console.WriteLine("                         this file override the auto-resolved values");
            Console.WriteLine("      --sql-variables");
            Console.WriteLine("                         JSON file of \"@Variable\": \"value\" pairs for stored-proc variables used to");
            Console.WriteLine("                         build dynamic SQL (e.g. \"@Server\", \"@Database\"). Lets OPENQUERY linked-server");
            Console.WriteLine("                         and remote table names resolve so unqualified columns map to their real table");
            Console.WriteLine();
            Console.WriteLine("diff options:");
            Console.WriteLine("  -o, --output           Write the markdown diff report to a file");
            Console.WriteLine("      --fail-on-changes  Exit with code 1 when lineage changed (for CI gates)");
            Console.WriteLine();
            Console.WriteLine("Outputs: lineage.json, lineage.yaml, lineage.cypher, execution-flow.md, lineage-report.html, lineage.mmd (Mermaid), lineage.openlineage.json (OpenLineage)");
        }
    }
}
