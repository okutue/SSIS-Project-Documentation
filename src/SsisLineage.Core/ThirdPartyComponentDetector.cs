using System;
using System.Collections.Generic;

namespace SsisLineage.Core
{
    public static class ThirdPartyComponentDetector
    {
        private static readonly string[] KnownVendors =
        {
            "CozyRoc", "KingswaySoft", "Kingsway", "ZappySys", "Attunity", "PragmaticWorks",
            "Script", "Custom", "COZY", "KingswaySoft."
        };

        /// <summary>
        /// True for the Microsoft Script Component (data-flow script transformation).
        /// It is a first-party component, but its transformation logic is compiled code the
        /// static analyser cannot see — only its declared input/output columns are captured.
        /// </summary>
        public static bool IsScriptComponent(string? componentTypeOrClassId, string? componentName)
        {
            var combined = $"{componentTypeOrClassId} {componentName}";
            return combined.Contains("ScriptComponentHost", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("TxScript", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("Script Component", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsLikelyThirdParty(string? componentTypeOrClassId, string? componentName)
        {
            var combined = $"{componentTypeOrClassId} {componentName}";
            if (string.IsNullOrWhiteSpace(combined))
            {
                return false;
            }

            // Script Components are first-party Microsoft — reported separately, not as third-party.
            if (IsScriptComponent(componentTypeOrClassId, componentName))
            {
                return false;
            }

            foreach (var vendor in KnownVendors)
            {
                if (combined.Contains(vendor, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (componentTypeOrClassId != null
                && componentTypeOrClassId.Contains('{')
                && !IsMicrosoftComponentClassId(componentTypeOrClassId))
            {
                return true;
            }

            return false;
        }

        public static string NormalizeComponentType(string? rawType, string? componentName)
        {
            if (IsScriptComponent(rawType, componentName))
            {
                return "Script Component";
            }

            if (IsLikelyThirdParty(rawType, componentName))
            {
                var label = !string.IsNullOrWhiteSpace(componentName) ? componentName : "Unknown";
                return $"Third-Party: {label}";
            }

            return string.IsNullOrWhiteSpace(rawType) ? "Component" : rawType;
        }

        private static bool IsMicrosoftComponentClassId(string classId)
        {
            var microsoftMarkers = new[]
            {
                "Microsoft.SqlServer", "Microsoft.DataTransformationServices",
                "DTS.Pipeline", "OLEDB", "SQLNCLI", "MSOLAP"
            };

            foreach (var marker in microsoftMarkers)
            {
                if (classId.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
