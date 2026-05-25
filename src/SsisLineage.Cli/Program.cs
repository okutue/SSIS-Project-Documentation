using System;
using System.Linq;
using SsisLineage.Core;

namespace SsisLineage.Cli
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("========================================");
            Console.WriteLine("    SSIS Project Lineage Utility");
            Console.WriteLine("========================================");

            if (args.Length == 0 || args[0].Equals("help", StringComparison.OrdinalIgnoreCase) || args[0].Equals("--help", StringComparison.OrdinalIgnoreCase))
            {
                PrintUsage();
                return;
            }

            if (args[0].Equals("scan", StringComparison.OrdinalIgnoreCase))
            {
                RunScan(args.Skip(1).ToArray());
            }
            else
            {
                Console.WriteLine($"Unknown command: {args[0]}");
                PrintUsage();
            }
        }

        static void RunScan(string[] scanArgs)
        {
            var options = new LineageScanOptions();

            for (int i = 0; i < scanArgs.Length; i++)
            {
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
            }

            if (string.IsNullOrEmpty(options.ProjectPath) || string.IsNullOrEmpty(options.StartPackage))
            {
                Console.WriteLine("[Error] Missing required arguments: --project-path and --start-package are required.");
                PrintUsage();
                return;
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
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Fatal Error] Scan failed: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }

        static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  ssis-lineage scan --project-path <path> --start-package <name> [--output <dir>] [--no-cache] [--include-sql-procedures] [--sql-connection-string <connection-string>]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  -p, --project-path    Path to the SSIS .dtproj file or its containing directory");
            Console.WriteLine("  -s, --start-package   Name of the starting entry package (e.g. Master.dtsx)");
            Console.WriteLine("  -o, --output          Directory where lineage outputs will be written (default: ./lineage-output)");
            Console.WriteLine("      --no-cache        Force a fresh scan instead of using the package hash cache");
            Console.WriteLine("      --include-sql-procedures");
            Console.WriteLine("                         Connect to SQL Server and retrieve stored procedure definitions for SQL lineage");
            Console.WriteLine("      --sql-connection-string");
            Console.WriteLine("                         SQL Server connection string used only when --include-sql-procedures is set");
        }
    }
}
