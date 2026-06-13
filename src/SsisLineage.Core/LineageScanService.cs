using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SsisLineage.Core.Models;

namespace SsisLineage.Core
{
    public class LineageScanOptions
    {
        public string ProjectPath { get; set; } = "";
        public string StartPackage { get; set; } = "";
        public string OutputDirectory { get; set; } = "./lineage-output";
        public bool UseCache { get; set; } = true;
        public bool IncludeSqlProcedures { get; set; }
        public string SqlConnectionString { get; set; } = "";
        /// <summary>
        /// Variable/parameter value overrides applied on top of design-time values, keyed
        /// "Namespace::Name" (e.g. "Project::TargetDb"). Typically extracted from an SSIS
        /// catalog environment — see docs/CI.md for the SSISDB query.
        /// </summary>
        public Dictionary<string, string> VariableOverrides { get; set; } = new();

        /// <summary>
        /// Linked-server name → actual server name (e.g. "LINKEDSRV" → "StagingServer").
        /// Auto-resolved from sys.servers on every SQL connection used during the scan;
        /// entries here override the auto-resolved values (useful offline, or when
        /// data_source is an IP/alias that doesn't match the connection-string host).
        /// </summary>
        public Dictionary<string, string> LinkedServerMap { get; set; } = new();

        /// <summary>
        /// When true (default), linked-server names are resolved to their data source via
        /// sys.servers on every SQL connection used during the scan. Disable to rely solely
        /// on <see cref="LinkedServerMap"/> (or keep raw linked-server names in the output).
        /// </summary>
        public bool AutoResolveLinkedServers { get; set; } = true;

        /// <summary>
        /// Values for stored-procedure variables/parameters used to build dynamic SQL
        /// (e.g. {"@Server":"LINKEDSRV", "@Database":"StagingDb"}). Empty by default (offline):
        /// dynamic SQL keeps placeholder names. Supplying these makes OPENQUERY linked-server
        /// and remote table names real so unqualified columns can be resolved to their owning
        /// remote table. Keys are matched with or without a leading '@'.
        /// </summary>
        public Dictionary<string, string> SqlVariableValues { get; set; } = new();

        /// <summary>
        /// Per-connection-manager connection-string overrides, keyed by connection-manager
        /// name or GUID (e.g. {"Staging":"Server=…;Database=Staging;…", "DW":"…"}). Override a
        /// specific manager's .conmgr value — useful when the project's connections point at a
        /// server you can't reach and each database needs different redirection. Takes
        /// precedence over the project .conmgr; <see cref="SqlConnectionString"/> remains the
        /// single fallback for any manager not listed here.
        /// </summary>
        public Dictionary<string, string> ConnectionManagerOverrides { get; set; } = new();
    }

    public class LineageScanResult
    {
        public LineageGraph Graph { get; set; } = new();
        public SsisProjectInfo Project { get; set; } = new();
        public string ProjectFilePath { get; set; } = "";
        public string RootPackagePath { get; set; } = "";
        public string OutputDirectory { get; set; } = "";
        public bool CacheHit { get; set; }
        public List<string> OutputFiles { get; set; } = new();
    }

    public class LineageScanService
    {
        public LineageScanResult Scan(LineageScanOptions options)
        {
            SqlServerAssemblyResolver.Register();

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (string.IsNullOrWhiteSpace(options.ProjectPath))
            {
                throw new ArgumentException("Project path is required.", nameof(options.ProjectPath));
            }

            if (string.IsNullOrWhiteSpace(options.StartPackage))
            {
                throw new ArgumentException("Start package is required.", nameof(options.StartPackage));
            }

            var projectFilePath = ResolveProjectFile(options.ProjectPath);
            var projectInfo = ProjectLoader.LoadProject(projectFilePath);
            var rootPackage = ResolveRootPackage(projectInfo, options.StartPackage);
            var outputDirectory = string.IsNullOrWhiteSpace(options.OutputDirectory)
                ? Path.Combine(Environment.CurrentDirectory, "lineage-output")
                : options.OutputDirectory;

            var cacheManager = new LineageCacheManager(outputDirectory);
            var effectiveUseCache = options.UseCache && !options.IncludeSqlProcedures;
            var cache = effectiveUseCache ? cacheManager.LoadCache() : new CacheData();
            var rootHash = ProjectLoader.ComputeFileHash(rootPackage.Path);
            var cacheKey = rootPackage.Name;

            LineageGraph graph;
            var cacheHit = false;

            if (effectiveUseCache &&
                cache.CachedPackages.TryGetValue(cacheKey, out var cachedResult) &&
                cachedResult.FileHash == rootHash)
            {
                cacheHit = true;
                graph = new LineageGraph
                {
                    Packages = cachedResult.Packages,
                    Tasks = cachedResult.Tasks,
                    Components = cachedResult.Components,
                    ColumnMappings = cachedResult.ColumnMappings,
                    ExecutionEdges = cachedResult.ExecutionEdges,
                    DataFlowEdges = cachedResult.DataFlowEdges
                };
            }
            else
            {
                var parser = new SsisPackageParser(projectInfo.ProjectDirectory, options.VariableOverrides,
                    options.SqlVariableValues, options.ConnectionManagerOverrides);
                graph = parser.Parse(rootPackage.Path);

                cache.CachedPackages[cacheKey] = new CachedPackageResult
                {
                    FileHash = rootHash,
                    ScanTime = DateTime.UtcNow,
                    Packages = graph.Packages,
                    Tasks = graph.Tasks,
                    Components = graph.Components,
                    ColumnMappings = graph.ColumnMappings,
                    ExecutionEdges = graph.ExecutionEdges,
                    DataFlowEdges = graph.DataFlowEdges
                };

                if (effectiveUseCache)
                {
                    cacheManager.SaveCache(cache);
                }
            }

            graph.ColumnMappings.RemoveAll(m =>
                m.OperationType?.StartsWith("SQL_PROC_", StringComparison.OrdinalIgnoreCase) == true);

            if (options.IncludeSqlProcedures
                && string.IsNullOrWhiteSpace(options.SqlConnectionString)
                && new SsisConnectionManagerResolver(projectInfo.ProjectDirectory).TryResolveFirstSqlConnectionString() == null)
            {
                throw new InvalidOperationException(
                    "SQL procedure retrieval for data flow components was requested, but no SQL connection string was provided and none could be resolved from project .conmgr files.");
            }

            SqlProcedureEnricher.EnrichFromStoredProcedures(
                graph,
                projectInfo.ProjectDirectory,
                options.SqlConnectionString,
                includeDataFlowComponents: options.IncludeSqlProcedures,
                includeExecuteSqlTasks: true,
                linkedServerMap: options.LinkedServerMap,
                autoResolveLinkedServers: options.AutoResolveLinkedServers,
                sqlVariableValues: options.SqlVariableValues,
                connectionManagerOverrides: options.ConnectionManagerOverrides);

            var outputFiles = WriteOutputs(graph, outputDirectory);

            return new LineageScanResult
            {
                Graph = graph,
                Project = projectInfo,
                ProjectFilePath = projectFilePath,
                RootPackagePath = rootPackage.Path,
                OutputDirectory = Path.GetFullPath(outputDirectory),
                CacheHit = cacheHit,
                OutputFiles = outputFiles
            };
        }

        private static string ResolveProjectFile(string projectPath)
        {
            var fullPath = Path.GetFullPath(projectPath);

            if (File.Exists(fullPath))
            {
                if (!fullPath.EndsWith(".dtproj", StringComparison.OrdinalIgnoreCase))
                {
                    throw new FileNotFoundException("Project path must point to a .dtproj file or a directory containing one.", fullPath);
                }

                return fullPath;
            }

            if (!Directory.Exists(fullPath))
            {
                throw new DirectoryNotFoundException($"Project path not found: {fullPath}");
            }

            var dtprojFiles = Directory.GetFiles(fullPath, "*.dtproj", SearchOption.TopDirectoryOnly);
            if (dtprojFiles.Length == 0)
            {
                throw new FileNotFoundException("No .dtproj file found in project directory.", fullPath);
            }

            if (dtprojFiles.Length > 1)
            {
                throw new InvalidOperationException($"Multiple .dtproj files found in {fullPath}. Provide the exact .dtproj path.");
            }

            return dtprojFiles[0];
        }

        private static SsisPackageFileInfo ResolveRootPackage(SsisProjectInfo projectInfo, string startPackage)
        {
            var packageName = startPackage.EndsWith(".dtsx", StringComparison.OrdinalIgnoreCase)
                ? startPackage
                : startPackage + ".dtsx";

            var package = projectInfo.Packages.FirstOrDefault(p =>
                string.Equals(p.Name, packageName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(p.Path), packageName, StringComparison.OrdinalIgnoreCase));

            if (package != null)
            {
                return package;
            }

            var fallbackPath = Path.Combine(projectInfo.ProjectDirectory, packageName);
            if (File.Exists(fallbackPath))
            {
                return new SsisPackageFileInfo
                {
                    Name = Path.GetFileName(fallbackPath),
                    Path = fallbackPath,
                    FileHash = ProjectLoader.ComputeFileHash(fallbackPath)
                };
            }

            throw new FileNotFoundException("Start package not found in the SSIS project directory.", fallbackPath);
        }

        private static List<string> WriteOutputs(LineageGraph graph, string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);

            var files = new Dictionary<string, string>
            {
                ["lineage.json"] = OutputGenerator.GenerateJson(graph),
                ["lineage.yaml"] = OutputGenerator.GenerateYaml(graph),
                ["lineage.cypher"] = OutputGenerator.GenerateCypher(graph),
                ["execution-flow.md"] = OutputGenerator.GenerateMarkdownReport(graph),
                ["lineage-report.html"] = OutputGenerator.GenerateHtmlReport(graph),
                ["lineage.mmd"] = OutputGenerator.GenerateMermaid(graph),
                ["lineage.openlineage.json"] = OutputGenerator.GenerateOpenLineage(graph)
            };

            var writtenFiles = new List<string>();
            foreach (var file in files)
            {
                var path = Path.Combine(outputDirectory, file.Key);
                File.WriteAllText(path, file.Value);
                writtenFiles.Add(Path.GetFullPath(path));
            }

            return writtenFiles;
        }
    }
}
