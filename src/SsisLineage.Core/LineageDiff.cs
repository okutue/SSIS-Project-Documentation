using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SsisLineage.Core.Models;

namespace SsisLineage.Core
{
    /// <summary>Result of comparing two lineage scans (e.g. main branch vs PR branch).</summary>
    public sealed class LineageDiffResult
    {
        public List<string> AddedPackages { get; } = new();
        public List<string> RemovedPackages { get; } = new();
        public List<string> AddedTasks { get; } = new();
        public List<string> RemovedTasks { get; } = new();
        public List<string> AddedComponents { get; } = new();
        public List<string> RemovedComponents { get; } = new();
        public List<string> AddedMappings { get; } = new();
        public List<string> RemovedMappings { get; } = new();

        public bool HasChanges =>
            AddedPackages.Count + RemovedPackages.Count +
            AddedTasks.Count + RemovedTasks.Count +
            AddedComponents.Count + RemovedComponents.Count +
            AddedMappings.Count + RemovedMappings.Count > 0;

        public int TotalChanges =>
            AddedPackages.Count + RemovedPackages.Count +
            AddedTasks.Count + RemovedTasks.Count +
            AddedComponents.Count + RemovedComponents.Count +
            AddedMappings.Count + RemovedMappings.Count;
    }

    /// <summary>
    /// Compares two <see cref="LineageGraph"/>s by stable, name-based identities (not GUIDs,
    /// which change between scans) and reports added/removed packages, tasks, components, and
    /// column mappings. Used for lineage-drift detection in code review and CI.
    /// </summary>
    public static class LineageDiff
    {
        public static LineageDiffResult Compare(LineageGraph oldGraph, LineageGraph newGraph)
        {
            var result = new LineageDiffResult();

            DiffSets(
                oldGraph.Packages.Select(p => p.Name),
                newGraph.Packages.Select(p => p.Name),
                result.AddedPackages, result.RemovedPackages);

            DiffSets(
                oldGraph.Tasks.Select(t => TaskKey(oldGraph, t)),
                newGraph.Tasks.Select(t => TaskKey(newGraph, t)),
                result.AddedTasks, result.RemovedTasks);

            DiffSets(
                oldGraph.Components.Select(c => ComponentKey(oldGraph, c)),
                newGraph.Components.Select(c => ComponentKey(newGraph, c)),
                result.AddedComponents, result.RemovedComponents);

            DiffSets(
                oldGraph.ColumnMappings.Select(m => MappingKey(oldGraph, m)),
                newGraph.ColumnMappings.Select(m => MappingKey(newGraph, m)),
                result.AddedMappings, result.RemovedMappings);

            return result;
        }

        public static string GenerateMarkdown(LineageDiffResult diff)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Lineage Diff");
            sb.AppendLine();
            if (!diff.HasChanges)
            {
                sb.AppendLine("No lineage changes detected.");
                return sb.ToString();
            }

            sb.AppendLine($"**{diff.TotalChanges} change(s) detected.**");
            sb.AppendLine();
            AppendSection(sb, "Packages added", diff.AddedPackages);
            AppendSection(sb, "Packages removed", diff.RemovedPackages);
            AppendSection(sb, "Tasks added", diff.AddedTasks);
            AppendSection(sb, "Tasks removed", diff.RemovedTasks);
            AppendSection(sb, "Components added", diff.AddedComponents);
            AppendSection(sb, "Components removed", diff.RemovedComponents);
            AppendSection(sb, "Column mappings added", diff.AddedMappings);
            AppendSection(sb, "Column mappings removed", diff.RemovedMappings);
            return sb.ToString();
        }

        private static void AppendSection(StringBuilder sb, string title, List<string> items)
        {
            if (items.Count == 0) return;
            sb.AppendLine($"## {title} ({items.Count})");
            sb.AppendLine();
            foreach (var item in items.OrderBy(i => i, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"- {item}");
            sb.AppendLine();
        }

        private static void DiffSets(IEnumerable<string> oldKeys, IEnumerable<string> newKeys,
            List<string> added, List<string> removed)
        {
            var oldSet = new HashSet<string>(oldKeys, StringComparer.OrdinalIgnoreCase);
            var newSet = new HashSet<string>(newKeys, StringComparer.OrdinalIgnoreCase);
            added.AddRange(newSet.Where(k => !oldSet.Contains(k)));
            removed.AddRange(oldSet.Where(k => !newSet.Contains(k)));
        }

        // ── stable identity keys (names, not scan-specific GUIDs) ─────────────

        private static string PackageName(LineageGraph g, string packageId) =>
            g.Packages.Find(p => p.Id == packageId)?.Name ?? "";

        private static string TaskName(LineageGraph g, string taskId) =>
            g.Tasks.Find(t => t.Id == taskId)?.Name ?? "";

        private static string TaskKey(LineageGraph g, TaskNode t) =>
            $"{t.PackageName}/{t.Name} [{t.Type}]";

        private static string ComponentKey(LineageGraph g, ComponentNode c) =>
            $"{PackageName(g, c.PackageId)}/{TaskName(g, c.TaskId)}/{c.Name} [{c.Type}]";

        private static string MappingKey(LineageGraph g, ColumnMap m)
        {
            var src = SideLabel(m.SourceSchema, m.SourceTable, m.SourceComponentName, m.SourceColumnName);
            var tgt = SideLabel(m.TargetSchema, m.TargetTable, m.TargetComponentName, m.TargetColumnName);
            return $"{PackageName(g, m.PackageId)}/{TaskName(g, m.TaskId)}: {src} -> {tgt} [{m.OperationType}]";
        }

        // Same blank-field fallback used by the tracer / CSV export so identities match across scans.
        private static string SideLabel(string schema, string table, string componentName, string column)
        {
            if (string.IsNullOrEmpty(table) && !string.IsNullOrEmpty(componentName))
            {
                var parts = componentName.Split('.', 2);
                if (parts.Length == 2) { schema = parts[0]; table = parts[1]; }
                else table = componentName;
            }
            var t = string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";
            return string.IsNullOrEmpty(column) ? t : $"{t}.{column}";
        }
    }
}
