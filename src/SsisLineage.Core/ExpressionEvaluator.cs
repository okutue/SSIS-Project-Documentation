using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

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

        /// <summary>
        /// Extract variables and parameters from SSIS Package and Project.
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

            // Note: In a real project deployment, project parameters can also be extracted.
            // Microsoft.SqlServer.Dts.Runtime.Project class can be used to load project parameters.

            return variables;
        }
    }
}
