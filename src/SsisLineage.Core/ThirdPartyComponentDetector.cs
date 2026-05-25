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

        public static bool IsLikelyThirdParty(string? componentTypeOrClassId, string? componentName)
        {
            var combined = $"{componentTypeOrClassId} {componentName}";
            if (string.IsNullOrWhiteSpace(combined))
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
