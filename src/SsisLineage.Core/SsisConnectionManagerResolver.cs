using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace SsisLineage.Core
{
    /// <summary>
    /// Resolves connection strings from SSIS project .conmgr files and package connection metadata.
    /// </summary>
    public class SsisConnectionManagerResolver
    {
        private readonly Dictionary<string, string> _connectionStrings = new(StringComparer.OrdinalIgnoreCase);

        // Caller-supplied overrides keyed by connection-manager name or GUID. Take precedence
        // over the project's .conmgr values — e.g. redirect "Staging"/"DW" to a reachable server.
        private readonly Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

        public SsisConnectionManagerResolver(string projectDirectory, IDictionary<string, string>? overrides = null)
        {
            if (overrides != null)
            {
                foreach (var kv in overrides)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                        _overrides[kv.Key.Trim()] = kv.Value;
                }
            }

            if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory))
            {
                return;
            }

            foreach (var conmgrPath in Directory.EnumerateFiles(projectDirectory, "*.conmgr", SearchOption.AllDirectories))
            {
                TryLoadConmgrFile(conmgrPath);
            }
        }

        public IReadOnlyDictionary<string, string> ConnectionStrings => _connectionStrings;

        public string? TryResolveConnectionString(string? connectionManagerRef)
        {
            if (string.IsNullOrWhiteSpace(connectionManagerRef))
            {
                return null;
            }

            // Overrides win over the project's .conmgr values; same matching either way.
            return MatchIn(_overrides, connectionManagerRef) ?? MatchIn(_connectionStrings, connectionManagerRef);
        }

        // Resolve a connection-manager reference against a key→value map: exact ref, bare GUID,
        // extracted name, then a substring fallback (handles suffixes like "{guid}:external").
        private static string? MatchIn(Dictionary<string, string> map, string connectionManagerRef)
        {
            if (map.Count == 0) return null;
            var trimmed = connectionManagerRef.Trim();

            if (map.TryGetValue(trimmed, out var direct)) return direct;

            var bareGuid = trimmed.Trim('{', '}');
            if (!string.IsNullOrEmpty(bareGuid) && map.TryGetValue(bareGuid, out var byGuid)) return byGuid;

            var name = ExtractConnectionManagerName(connectionManagerRef);
            if (!string.IsNullOrEmpty(name) && map.TryGetValue(name, out var byName)) return byName;

            foreach (var pair in map)
            {
                if (connectionManagerRef.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }
            return null;
        }

        public string? TryResolveFirstSqlConnectionString()
        {
            foreach (var value in _connectionStrings.Values)
            {
                if (LooksLikeSqlConnection(value))
                {
                    return value;
                }
            }

            return null;
        }

        private void TryLoadConmgrFile(string path)
        {
            try
            {
                var doc = XDocument.Load(path);
                var root = doc.Root;
                if (root == null)
                {
                    return;
                }

                XNamespace dts = "www.microsoft.com/SqlServer/Dts";
                var objectName = root.Attribute(dts + "ObjectName")?.Value
                    ?? root.Attribute("ObjectName")?.Value
                    ?? Path.GetFileNameWithoutExtension(path);

                var connectionString = FindConnectionString(root);
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    return;
                }

                _connectionStrings[objectName] = connectionString;

                var aliases = new List<string> { objectName };
                var dtsId = root.Attribute(dts + "DTSID")?.Value?.Trim();
                if (!string.IsNullOrEmpty(dtsId))
                {
                    var bareId = dtsId.Trim('{', '}');
                    var braced = "{" + bareId + "}";
                    _connectionStrings[dtsId] = connectionString;
                    _connectionStrings[bareId] = connectionString;
                    _connectionStrings[braced] = connectionString;
                    aliases.Add(dtsId);
                    aliases.Add(bareId);
                    aliases.Add(braced);
                }

                // If any alias of this manager is overridden, propagate the override to ALL its
                // aliases so a name-keyed override matches a GUID reference (and vice versa).
                var overrideValue = aliases
                    .Select(a => _overrides.TryGetValue(a, out var v) ? v : null)
                    .FirstOrDefault(v => v != null);
                if (overrideValue != null)
                {
                    foreach (var a in aliases) _overrides[a] = overrideValue;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed to read connection manager file {path}: {ex.Message}");
            }
        }

        private static string? FindConnectionString(XElement root)
        {
            XNamespace dts = "www.microsoft.com/SqlServer/Dts";

            foreach (var element in root.Descendants())
            {
                if (element.Name.LocalName.Equals("EncryptedData", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var attributeValue = element.Attribute(dts + "ConnectionString")?.Value
                    ?? element.Attribute("ConnectionString")?.Value;
                if (!string.IsNullOrWhiteSpace(attributeValue))
                {
                    return attributeValue.Trim();
                }

                var localName = element.Name.LocalName;
                if (localName.Equals("connectionString", StringComparison.OrdinalIgnoreCase)
                    || localName.Equals("ConnectionString", StringComparison.OrdinalIgnoreCase))
                {
                    var value = element.Value?.Trim();
                    if (!string.IsNullOrEmpty(value))
                    {
                        return value;
                    }
                }
            }

            return null;
        }

        public static string? ExtractConnectionManagerName(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                return null;
            }

            var trimmed = reference.Trim();
            var bracketStart = trimmed.IndexOf('[');
            var bracketEnd = trimmed.LastIndexOf(']');
            if (bracketStart >= 0 && bracketEnd > bracketStart)
            {
                return trimmed.Substring(bracketStart + 1, bracketEnd - bracketStart - 1).Trim();
            }

            if (trimmed.Contains('\\'))
            {
                return trimmed.Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            }

            return trimmed;
        }

        private static bool LooksLikeSqlConnection(string value)
        {
            var lower = value.ToLowerInvariant();
            return lower.Contains("data source=")
                || lower.Contains("server=")
                || lower.Contains("initial catalog=")
                || lower.Contains("database=");
        }
    }
}
