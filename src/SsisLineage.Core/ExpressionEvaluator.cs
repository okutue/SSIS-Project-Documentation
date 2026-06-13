using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SsisLineage.Core
{
    public class ExpressionEvaluator
    {
        // Matches @[User::VarName] or $[Project::ParamName] or $[Package::ParamName]
        private static readonly Regex VariableRegex = new(@"@\[([a-zA-Z0-9_\-:]+)\]|\$\[([a-zA-Z0-9_\-:]+)\]", RegexOptions.Compiled);

        public static string Evaluate(string expression, Dictionary<string, object> variables)
        {
            if (string.IsNullOrWhiteSpace(expression))
                return "";

            // If it's a simple variable reference like @[User::SQLQuery], evaluate it directly
            var singleVarMatch = Regex.Match(expression.Trim(), @"^(@\[[a-zA-Z0-9_\-:]+\]|\$\[[a-zA-Z0-9_\-:]+\])$");
            if (singleVarMatch.Success)
            {
                var varKey = GetVariableKeyFromReference(singleVarMatch.Value);
                if (variables.TryGetValue(varKey, out var val) && val != null)
                {
                    return val.ToString() ?? "";
                }
            }

            // Otherwise, it could be a concatenation or complex expression.
            // Let's do basic string parsing.
            try
            {
                return ParseExpression(expression, variables);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed to fully evaluate expression: '{expression}'. Error: {ex.Message}. Returning raw expression.");
                return expression;
            }
        }

        private static string ParseExpression(string expr, Dictionary<string, object> variables)
        {
            // 1. Resolve all variable references first by replacing them with their values
            var resolvedExpr = VariableRegex.Replace(expr, match =>
            {
                var key = GetVariableKeyFromReference(match.Value);
                if (variables.TryGetValue(key, out var val) && val != null)
                {
                    // If it is a string value, wrap it in double quotes so tokenization knows it's a string literal,
                    // unless it's already in string parsing mode.
                    return $"\"{val}\"";
                }
                return "\"\"";
            });

            // 2. Parse concatenation of string literals
            // e.g. "SELECT * FROM " + "MyTable" + " WHERE ID = 1"
            var parts = resolvedExpr.Split('+');
            var result = "";
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                // Strip double quotes if it's a string literal
                if (trimmed.StartsWith("\"") && trimmed.EndsWith("\""))
                {
                    result += trimmed.Substring(1, trimmed.Length - 2);
                }
                else
                {
                    // If it's a number or something else, just append it
                    result += trimmed;
                }
            }

            return result;
        }

        private static string GetVariableKeyFromReference(string reference)
        {
            // Input: @[User::MyVar] or $[Project::MyParam]
            // Output: User::MyVar or Project::MyParam
            return reference.Trim('[', ']', '@', '$');
        }

#if WINDOWS
        /// <summary>
        /// Extract variables and parameters from a loaded SSIS Package (native DTS runtime,
        /// Windows only). The cross-platform build reads variables from package XML instead.
        /// </summary>
        public static Dictionary<string, object> ExtractVariables(Microsoft.SqlServer.Dts.Runtime.Package package)
        {
            var variables = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            // Extract package variables
            foreach (Microsoft.SqlServer.Dts.Runtime.Variable variable in package.Variables)
            {
                var key = $"{variable.Namespace}::{variable.Name}";
                variables[key] = variable.Value;
            }

            return variables;
        }
#endif

        /// <summary>True when the value is exactly one variable/parameter reference, e.g. "@[User::SQLQuery]".</summary>
        public static bool IsSingleVariableReference(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            Regex.IsMatch(value.Trim(), @"^(@\[[a-zA-Z0-9_\-:]+\]|\$\[[a-zA-Z0-9_\-:]+\])$");

        /// <summary>
        /// Loads project parameters from Project.params (and any other *.params file) in the
        /// project directory as "Project::Name" → design-time value.
        /// </summary>
        public static Dictionary<string, object> LoadProjectParameters(string projectDirectory)
        {
            var parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory))
                return parameters;

            XNamespace ssis = "www.microsoft.com/SqlServer/SSIS";
            foreach (var paramsFile in Directory.EnumerateFiles(projectDirectory, "*.params", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var doc = XDocument.Load(paramsFile);
                    foreach (var param in doc.Descendants(ssis + "Parameter"))
                    {
                        var name = param.Attribute(ssis + "Name")?.Value;
                        if (string.IsNullOrEmpty(name)) continue;

                        var value = param.Descendants(ssis + "Property")
                            .FirstOrDefault(p => p.Attribute(ssis + "Name")?.Value == "Value")
                            ?.Value ?? "";
                        parameters[$"Project::{name}"] = value;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Warning] Failed to read project parameters from {Path.GetFileName(paramsFile)}: {ex.Message}");
                }
            }
            return parameters;
        }

        /// <summary>
        /// Extracts package variables from a .dtsx XML document (used by the XML fallback parser,
        /// which is the normal path on .NET 10). Keys are "Namespace::Name".
        /// </summary>
        public static Dictionary<string, object> ExtractVariablesFromXml(XDocument packageDoc)
        {
            var variables = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            XNamespace dts = "www.microsoft.com/SqlServer/Dts";

            foreach (var variable in packageDoc.Descendants(dts + "Variable"))
            {
                var ns = variable.Attribute(dts + "Namespace")?.Value ?? "User";
                var name = variable.Attribute(dts + "ObjectName")?.Value;
                if (string.IsNullOrEmpty(name)) continue;

                var value = variable.Element(dts + "VariableValue")?.Value ?? "";
                variables[$"{ns}::{name}"] = value;
            }
            return variables;
        }
    }
}
