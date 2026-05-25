using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace SsisLineage.Core
{
    public static class SqlServerAssemblyResolver
    {
        private static bool _registered;

        public static void Register()
        {
            if (_registered)
            {
                return;
            }

            AssemblyLoadContext.Default.Resolving += ResolveSqlServerAssembly;
            _registered = true;
        }

        private static Assembly? ResolveSqlServerAssembly(AssemblyLoadContext context, AssemblyName assemblyName)
        {
            var name = assemblyName.Name;
            if (string.IsNullOrWhiteSpace(name)
                || !name.StartsWith("Microsoft.SqlServer.", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            foreach (var probePath in GetProbePaths(name))
            {
                if (File.Exists(probePath))
                {
                    return context.LoadFromAssemblyPath(probePath);
                }
            }

            return null;
        }

        private static string[] GetProbePaths(string assemblyName)
        {
            var fileName = assemblyName + ".dll";
            var baseDirectory = AppContext.BaseDirectory;

            return new[]
            {
                Path.Combine(baseDirectory, fileName),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "Microsoft.NET",
                    "assembly",
                    "GAC_MSIL",
                    assemblyName,
                    $"v4.0_16.0.0.0__89845dcd8080cc91",
                    fileName),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "Microsoft.NET",
                    "assembly",
                    "GAC_64",
                    assemblyName,
                    $"v4.0_16.0.0.0__89845dcd8080cc91",
                    fileName)
            };
        }
    }
}
