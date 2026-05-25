using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace SsisLineage.Core
{
    public class ProjectLoader
    {
        public static SsisProjectInfo LoadProject(string dtprojPath)
        {
            if (!File.Exists(dtprojPath))
            {
                throw new FileNotFoundException("SSIS project file not found", dtprojPath);
            }

            var projectDir = Path.GetDirectoryName(dtprojPath) ?? "";
            var packageNames = new List<string>();

            try
            {
                var doc = XDocument.Load(dtprojPath);
                XNamespace ssisNs = "www.microsoft.com/SqlServer/SSIS";

                var packagesElement = doc.Descendants(ssisNs + "Packages");
                foreach (var pkg in packagesElement.Descendants(ssisNs + "Package"))
                {
                    var nameAttr = pkg.Attribute(ssisNs + "Name");
                    if (nameAttr != null && !string.IsNullOrEmpty(nameAttr.Value))
                    {
                        packageNames.Add(nameAttr.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed to parse .dtproj XML directly. Falling back to listing directory: {ex.Message}");
                // Fallback: list all .dtsx files in projectDir
                foreach (var file in Directory.GetFiles(projectDir, "*.dtsx"))
                {
                    packageNames.Add(Path.GetFileName(file));
                }
            }

            var projectInfo = new SsisProjectInfo
            {
                ProjectPath = dtprojPath,
                ProjectDirectory = projectDir,
                Name = Path.GetFileNameWithoutExtension(dtprojPath)
            };

            foreach (var name in packageNames)
            {
                var pkgPath = Path.Combine(projectDir, name);
                if (File.Exists(pkgPath))
                {
                    var hash = ComputeFileHash(pkgPath);
                    projectInfo.Packages.Add(new SsisPackageFileInfo
                    {
                        Name = name,
                        Path = pkgPath,
                        FileHash = hash
                    });
                }
            }

            return projectInfo;
        }

        public static string ComputeFileHash(string filePath)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hashBytes = sha.ComputeHash(stream);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
        }
    }

    public class SsisProjectInfo
    {
        public string Name { get; set; } = "";
        public string ProjectPath { get; set; } = "";
        public string ProjectDirectory { get; set; } = "";
        public List<SsisPackageFileInfo> Packages { get; set; } = new();
    }

    public class SsisPackageFileInfo
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string FileHash { get; set; } = "";
    }
}
