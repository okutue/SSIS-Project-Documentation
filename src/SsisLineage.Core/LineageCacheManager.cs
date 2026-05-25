using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SsisLineage.Core.Models;

namespace SsisLineage.Core
{
    public class LineageCacheManager
    {
        private readonly string _cacheFilePath;

        public LineageCacheManager(string outputDir)
        {
            _cacheFilePath = Path.Combine(outputDir, ".ssis-lineage-cache.json");
        }

        public CacheData LoadCache()
        {
            if (!File.Exists(_cacheFilePath))
            {
                return new CacheData();
            }

            try
            {
                var json = File.ReadAllText(_cacheFilePath);
                return JsonSerializer.Deserialize<CacheData>(json) ?? new CacheData();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed to load cache: {ex.Message}. Starting fresh.");
                return new CacheData();
            }
        }

        public void SaveCache(CacheData cacheData)
        {
            try
            {
                var dir = Path.GetDirectoryName(_cacheFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(cacheData, options);
                File.WriteAllText(_cacheFilePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed to save cache: {ex.Message}");
            }
        }
    }

    public class CacheData
    {
        public Dictionary<string, CachedPackageResult> CachedPackages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public class CachedPackageResult
    {
        public string FileHash { get; set; } = "";
        public DateTime ScanTime { get; set; }
        public List<PackageNode> Packages { get; set; } = new();
        public List<TaskNode> Tasks { get; set; } = new();
        public List<ComponentNode> Components { get; set; } = new();
        public List<ColumnMap> ColumnMappings { get; set; } = new();
        public List<ExecutionEdge> ExecutionEdges { get; set; } = new();
        public List<DataFlowEdge> DataFlowEdges { get; set; } = new();
    }
}
