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

        public SsisConnectionManagerResolver(string projectDirectory)
        {
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

            var trimmedRef = connectionManagerRef.Trim();
            if (_connectionStrings.TryGetValue(trimmedRef, out var direct))
            {
                return direct;
            }

            var bareGuid = trimmedRef.Trim('{', '}');
            if (!string.IsNullOrEmpty(bareGuid) && _connectionStrings.TryGetValue(bareGuid, out var byGuid))
            {
                return byGuid;
            }

            var name = ExtractConnectionManagerName(connectionManagerRef);
            if (!string.IsNullOrEmpty(name) && _connectionStrings.TryGetValue(name, out var byName))
            {
                return byName;
            }

            foreach (var pair in _connectionStrings)
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

                var dtsId = root.Attribute(dts + "DTSID")?.Value?.Trim();
                if (!string.IsNullOrEmpty(dtsId))
                {
                    var bareId = dtsId.Trim('{', '}');
                    _connectionStrings[dtsId] = connectionString;
                    _connectionStrings[bareId] = connectionString;
                    _connectionStrings["{" + bareId + "}"] = connectionString;
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
