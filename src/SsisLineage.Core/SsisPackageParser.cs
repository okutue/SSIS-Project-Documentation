using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.SqlServer.Dts.Runtime;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;
using SsisLineage.Core.Models;

namespace SsisLineage.Core
{
    public class SsisPackageParser
    {
        private readonly string _projectDirectory;
        private readonly LineageGraph _graph;
        private readonly HashSet<string> _visitedPackages = new(StringComparer.OrdinalIgnoreCase);

        public SsisPackageParser(string projectDirectory)
        {
            _projectDirectory = projectDirectory;
            _graph = new LineageGraph();
        }

        public LineageGraph Parse(string rootPackagePath)
        {
            _visitedPackages.Clear();
            ParsePackageRecursive(rootPackagePath, null);
            return _graph;
        }

        private void ParsePackageRecursive(string packagePath, string? parentPackageId)
        {
            if (!File.Exists(packagePath))
            {
                Console.WriteLine($"[Warning] Package file not found: {packagePath}");
                return;
            }

            var packageName = Path.GetFileName(packagePath);
            if (_visitedPackages.Contains(packageName))
            {
                _graph.Warnings.Add($"Package cycle or duplicate visit skipped: {packageName}");
                return;
            }
            _visitedPackages.Add(packageName);

            Console.WriteLine($"[*] Parsing package: {packageName}");

            try
            {
                var app = new Application();
                var package = app.LoadPackage(packagePath, null);

                var packageId = package.ID;
                var packageNode = new PackageNode
                {
                    Id = packageId,
                    Name = package.Name,
                    Path = packagePath,
                    ProjectPath = _projectDirectory,
                    FileHash = ProjectLoader.ComputeFileHash(packagePath)
                };

                // Extract connection managers (names only — kept on the package node, not graph components)
                foreach (ConnectionManager cm in package.Connections)
                {
                    packageNode.ConnectionManagers.Add(cm.Name);
                }

                // Extract variables + project parameters (Project.params)
                var variables = ExpressionEvaluator.ExtractVariables(package);
                foreach (var kv in ExpressionEvaluator.LoadProjectParameters(_projectDirectory))
                {
                    variables.TryAdd(kv.Key, kv.Value);
                }
                foreach (var varKey in variables.Keys)
                {
                    packageNode.Variables.Add(varKey);
                }

                _graph.Packages.Add(packageNode);

                // Recursively walk Control Flow Executables
                ProcessExecutables(package.Executables, packageNode, variables);

                // Process Precedence Constraints (Execution Edges)
                ProcessPrecedenceConstraints(package.PrecedenceConstraints);
            }
            catch (Exception)
            {
                // The SSIS runtime DLLs (ManagedDTS) are .NET Framework assemblies and reference
                // types (e.g. Microsoft.SqlServer.Server.SqlContext) that were removed from
                // System.Data in .NET 5+. Native loading therefore always fails on .NET 10.
                // The XML parser below produces equivalent results — this is expected behaviour.
                Console.WriteLine($"[Info] Using XML-based SSIS parser for {packageName} (DTS runtime not compatible with .NET 10 — this is normal). Results are equivalent.");
                ParsePackageXmlFallback(packagePath, parentPackageId);
            }
        }

        private void ProcessExecutables(Executables executables, PackageNode packageNode, Dictionary<string, object> variables)
        {
            foreach (Executable executable in executables)
            {
                if (executable is TaskHost taskHost)
                {
                    var taskNode = new TaskNode
                    {
                        Id = taskHost.ID,
                        Name = taskHost.Name,
                        Type = taskHost.CreationName,
                        PackageId = packageNode.Id,
                        PackageName = packageNode.Name,
                        Description = taskHost.Description
                    };
                    _graph.Tasks.Add(taskNode);

                    // Execute SQL Task
                    if (taskHost.CreationName.Contains("ExecuteSQLTask"))
                    {
                        ProcessExecuteSqlTask(taskHost, packageNode, taskNode, variables);
                    }
                    // Execute Package Task (Child Package)
                    else if (taskHost.CreationName.Contains("ExecutePackageTask"))
                    {
                        ProcessExecutePackageTask(taskHost, packageNode, variables);
                    }
                    // Data Flow Task
                    else if (taskHost.CreationName.Contains("PipelineTask"))
                    {
                        ProcessDataFlowTask(taskHost, packageNode, taskNode, variables);
                    }
                    // Script Task — compiled C#/VB code is opaque to static lineage analysis
                    else if (taskHost.CreationName.Contains("ScriptTask", StringComparison.OrdinalIgnoreCase))
                    {
                        _graph.Warnings.Add(
                            $"Script Task '{taskHost.Name}' in package '{packageNode.Name}' — script code cannot be statically analysed; any data movement inside it is not captured in lineage.");
                    }
                }
                // If it's a sequence/container, drill down and collect its own precedence constraints
                else if (executable is IDTSSequence sequence)
                {
                    if (executable is Sequence seqContainer)
                        ProcessPrecedenceConstraints(seqContainer.PrecedenceConstraints);
                    else if (executable is ForEachLoop feContainer)
                        ProcessPrecedenceConstraints(feContainer.PrecedenceConstraints);
                    else if (executable is ForLoop flContainer)
                        ProcessPrecedenceConstraints(flContainer.PrecedenceConstraints);
                    ProcessExecutables(sequence.Executables, packageNode, variables);
                }
            }
        }

        private void ProcessExecuteSqlTask(TaskHost taskHost, PackageNode packageNode, TaskNode taskNode, Dictionary<string, object> variables)
        {
            try
            {
                var connection = taskHost.Properties["Connection"]?.GetValue(taskHost)?.ToString() ?? "";
                var sqlSource = taskHost.Properties["SqlStatementSource"]?.GetValue(taskHost)?.ToString() ?? "";

                // Evaluate expressions on sql source if applicable
                var sqlExpr = taskHost.Properties["SqlStatementSource"]?.GetExpression(taskHost) ?? "";
                if (!string.IsNullOrEmpty(sqlExpr))
                {
                    sqlSource = ExpressionEvaluator.Evaluate(sqlExpr, variables);
                }

                _graph.Components.Add(new ComponentNode
                {
                    Id = taskHost.ID + "_sql",
                    Name = taskHost.Name,
                    Type = "Execute SQL Task",
                    PackageId = packageNode.Id,
                    TaskId = taskHost.ID,
                    ConnectionManager = connection,
                    SqlQueryOrTable = sqlSource
                });

                if (!string.IsNullOrEmpty(sqlSource))
                {
                    if (SqlProcedureDefinitionLoader.TryParseProcedureReference(sqlSource, out _, out _))
                    {
                        _graph.Warnings.Add(
                            $"Execute SQL task '{taskHost.Name}' calls {sqlSource.Trim()} — procedure body will be loaded from SQL Server when a connection is available.");
                    }
                    else
                    {
                        var sqlRecords = SqlProcedureParser.Parse(sqlSource, connection, "LocalServer");
                        foreach (var rec in sqlRecords)
                        {
                            _graph.ColumnMappings.Add(BuildSqlTaskColumnMap(
                                rec, packageNode.Id, taskHost.ID, taskHost.ID + "_sql", taskHost.Name));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed to parse SQL Statement in {taskHost.Name}: {ex.Message}");
            }
        }

        private void ProcessExecutePackageTask(TaskHost taskHost, PackageNode packageNode, Dictionary<string, object> variables)
        {
            try
            {
                var childPackageName = taskHost.Properties["PackageName"]?.GetValue(taskHost)?.ToString() ?? "";
                if (string.IsNullOrEmpty(childPackageName))
                {
                    // Attempt to resolve via Expression
                    var expr = taskHost.Properties["PackageName"]?.GetExpression(taskHost) ?? "";
                    if (!string.IsNullOrEmpty(expr))
                    {
                        childPackageName = ExpressionEvaluator.Evaluate(expr, variables);
                    }
                }

                if (!string.IsNullOrEmpty(childPackageName))
                {
                    if (!childPackageName.EndsWith(".dtsx", StringComparison.OrdinalIgnoreCase))
                        childPackageName += ".dtsx";

                    var childPath = Path.Combine(_projectDirectory, childPackageName);
                    var packagesBefore = _graph.Packages.Count;
                    ParsePackageRecursive(childPath, packageNode.Id);

                    // Add a cross-package invokes edge so the layout pushes child package content below this task
                    if (_graph.Packages.Count > packagesBefore)
                    {
                        _graph.ExecutionEdges.Add(new ExecutionEdge
                        {
                            FromTaskId = taskHost.ID,
                            ToTaskId   = _graph.Packages[packagesBefore].Id,
                            PrecedenceConstraintValue = "Invokes"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed to parse Execute Package Task in {taskHost.Name}: {ex.Message}");
            }
        }

        private void ProcessDataFlowTask(TaskHost taskHost, PackageNode packageNode, TaskNode taskNode, Dictionary<string, object> variables)
        {
            try
            {
                var pipe = taskHost.InnerObject as MainPipe;
                if (pipe == null)
                {
                    // Fall back to XML representation if casting fails
                    throw new InvalidCastException("Cannot cast Data Flow Task to MainPipe");
                }

                // Component mappings
                var components = pipe.ComponentMetaDataCollection;
                var componentIdMap = new Dictionary<int, string>();

                foreach (IDTSComponentMetaData100 comp in components)
                {
                    try
                    {
                        var rawType = comp.ComponentClassID;
                        var compNode = new ComponentNode
                        {
                            Id = comp.ID.ToString(),
                            Name = comp.Name,
                            Type = ThirdPartyComponentDetector.NormalizeComponentType(rawType, comp.Name),
                            PackageId = packageNode.Id,
                            TaskId = taskHost.ID
                        };

                        if (ThirdPartyComponentDetector.IsScriptComponent(rawType, comp.Name))
                        {
                            _graph.Warnings.Add(
                                $"Script Component '{comp.Name}' in package '{packageNode.Name}' — only its declared input/output columns are captured; transformation logic inside the script is opaque.");
                        }
                        else if (ThirdPartyComponentDetector.IsLikelyThirdParty(rawType, comp.Name))
                        {
                            _graph.Warnings.Add(
                                $"Third-party or custom component '{comp.Name}' in package '{packageNode.Name}' — lineage metadata may be incomplete.");
                        }

                        if (comp.RuntimeConnectionCollection.Count > 0)
                        {
                            compNode.ConnectionManager = comp.RuntimeConnectionCollection[0].ConnectionManagerID;
                        }

                        foreach (IDTSCustomProperty100 prop in comp.CustomPropertyCollection)
                        {
                            var propValue = prop.Value?.ToString() ?? "";
                            if (prop.Name == "SqlCommand" && !string.IsNullOrEmpty(propValue))
                            {
                                compNode.SqlQueryOrTable = ExpressionEvaluator.Evaluate(propValue, variables);
                            }
                            else if (prop.Name == "OpenRowset" && !string.IsNullOrEmpty(propValue))
                            {
                                compNode.SqlQueryOrTable = ExpressionEvaluator.Evaluate(propValue, variables);
                            }
                        }

                        _graph.Components.Add(compNode);
                        componentIdMap[comp.ID] = comp.ID.ToString();
                    }
                    catch (Exception compEx)
                    {
                        _graph.Warnings.Add(
                            $"Failed to parse component '{comp.Name}' in data flow '{taskHost.Name}': {compEx.Message}");
                    }
                }

                // Paths connecting components
                foreach (IDTSPath100 path in pipe.PathCollection)
                {
                    _graph.DataFlowEdges.Add(new DataFlowEdge
                    {
                        FromComponentId = path.StartPoint.Component.ID.ToString(),
                        ToComponentId = path.EndPoint.Component.ID.ToString(),
                        PathRefId = path.ID.ToString()
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Native Data Flow processing failed for {taskHost.Name}: {ex.Message}. Falling back to XML-based parsing for this Data Flow.");
                ParseDataFlowXmlFallback(packageNode, taskNode, taskHost.ID);
            }
        }

        // Builds a ColumnMap from a parsed SQL lineage record, carrying ALL extracted fields
        // (source/target server·db·schema·table, expression, join + filter conditions).
        // Shared by the native and XML-fallback Execute SQL handlers so neither path drops data.
        private static ColumnMap BuildSqlTaskColumnMap(
            SqlLineageRecord rec, string packageId, string taskId, string componentId, string taskName)
        {
            return new ColumnMap
            {
                PackageId = packageId,
                TaskId = taskId,
                SourceComponentId = componentId,
                SourceComponentName = string.IsNullOrWhiteSpace(rec.SourceTable)
                    ? taskName
                    : (string.IsNullOrWhiteSpace(rec.SourceSchema) ? rec.SourceTable : $"{rec.SourceSchema}.{rec.SourceTable}"),
                SourceServer     = rec.SourceServer,
                SourceDatabase   = rec.SourceDatabase,
                SourceSchema     = rec.SourceSchema,
                SourceTable      = rec.SourceTable,
                SourceColumnName = rec.SourceColumnName,
                SourceExpression = rec.SourceExpression,
                TargetComponentId = componentId,
                TargetComponentName = string.IsNullOrWhiteSpace(rec.TargetTable)
                    ? taskName
                    : (string.IsNullOrWhiteSpace(rec.TargetSchema) ? rec.TargetTable : $"{rec.TargetSchema}.{rec.TargetTable}"),
                TargetServer     = rec.TargetServer,
                TargetDatabase   = rec.TargetDatabase,
                TargetSchema     = rec.TargetSchema,
                TargetTable      = rec.TargetTable,
                TargetColumnName = rec.TargetColumnName,
                OperationType    = rec.OperationType,
                JoinDetails      = rec.JoinDetails,
                FilterConditions = rec.FilterConditions
            };
        }

        private void ProcessPrecedenceConstraints(PrecedenceConstraints constraints)
        {
            foreach (PrecedenceConstraint pc in constraints)
            {
                var from = (pc.PrecedenceExecutable as TaskHost)?.ID ?? "";
                var to = (pc.ConstrainedExecutable as TaskHost)?.ID ?? "";
                if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) continue;
                _graph.ExecutionEdges.Add(new ExecutionEdge
                {
                    FromTaskId = from,
                    ToTaskId = to,
                    PrecedenceConstraintValue = pc.Value.ToString(),
                    Expression = pc.Expression
                });
            }
        }

        #region XML Fallback Parsers
        private void ParsePackageXmlFallback(string packagePath, string? parentPackageId)
        {
            try
            {
                var doc = XDocument.Load(packagePath);
                var root = doc.Root;
                if (root == null) return;

                XNamespace dts = "www.microsoft.com/SqlServer/Dts";

                var packageId = root.Attribute(dts + "DTSID")?.Value
                    ?? root.Attribute(dts + "refId")?.Value
                    ?? Path.GetFileNameWithoutExtension(packagePath);
                var packageNode = new PackageNode
                {
                    Id = packageId,
                    Name = root.Attribute(dts + "ObjectName")?.Value ?? Path.GetFileNameWithoutExtension(packagePath),
                    Path = packagePath,
                    ProjectPath = _projectDirectory,
                    FileHash = ProjectLoader.ComputeFileHash(packagePath)
                };
                // Package variables + project parameters — used for display and to resolve
                // variable-driven SQL statements (e.g. SqlStatementSource = @[User::SQLQuery]).
                var variables = ExpressionEvaluator.ExtractVariablesFromXml(doc);
                foreach (var kv in ExpressionEvaluator.LoadProjectParameters(_projectDirectory))
                {
                    variables.TryAdd(kv.Key, kv.Value);
                }
                foreach (var varKey in variables.Keys)
                {
                    packageNode.Variables.Add(varKey);
                }

                _graph.Packages.Add(packageNode);

                var packageExecutableRefId = root.Attribute(dts + "refId")?.Value ?? "Package";

                // Find child Executable elements. The root package is also an Executable, so skip it.
                var executables = doc.Descendants(dts + "Executable")
                    .Where(x => x != root);
                foreach (var exe in executables)
                {
                    var exeId = exe.Attribute(dts + "refId")?.Value ?? "";
                    var exeName = exe.Attribute(dts + "ObjectName")?.Value ?? "";
                    var exeType = exe.Attribute(dts + "ExecutableType")?.Value ?? "";

                    if (string.IsNullOrEmpty(exeId)) continue;

                    var taskNode = new TaskNode
                    {
                        Id = exeId,
                        Name = exeName,
                        Type = exeType,
                        PackageId = packageId,
                        PackageName = packageNode.Name
                    };
                    _graph.Tasks.Add(taskNode);

                    // Child package task
                    if (exeType.Contains("ExecutePackageTask"))
                    {
                        var childPkgNameNode = exe.Descendants("PackageName").FirstOrDefault();
                        if (childPkgNameNode != null && !string.IsNullOrEmpty(childPkgNameNode.Value))
                        {
                            var childName = childPkgNameNode.Value;
                            if (!childName.EndsWith(".dtsx", StringComparison.OrdinalIgnoreCase))
                                childName += ".dtsx";

                            var childPath = Path.Combine(_projectDirectory, childName);
                            var packagesBefore = _graph.Packages.Count;
                            ParsePackageRecursive(childPath, packageId);

                            // Cross-package invokes edge
                            if (_graph.Packages.Count > packagesBefore)
                            {
                                _graph.ExecutionEdges.Add(new ExecutionEdge
                                {
                                    FromTaskId = exeId,
                                    ToTaskId   = _graph.Packages[packagesBefore].Id,
                                    PrecedenceConstraintValue = "Invokes"
                                });
                            }
                        }
                    }
                    // Data Flow Task
                    else if (IsDataFlowTask(exeType))
                    {
                        ParseDataFlowXmlFallback(packageNode, taskNode, exeId);
                    }
                    else if (exeType.Contains("ExecuteSQLTask", StringComparison.OrdinalIgnoreCase))
                    {
                        ParseExecuteSqlTaskXmlFallback(exe, packageNode, taskNode, variables);
                    }
                    // Script Task — compiled C#/VB code is opaque to static lineage analysis
                    else if (exeType.Contains("ScriptTask", StringComparison.OrdinalIgnoreCase))
                    {
                        _graph.Warnings.Add(
                            $"Script Task '{exeName}' in package '{packageNode.Name}' — script code cannot be statically analysed; any data movement inside it is not captured in lineage.");
                    }
                }

                foreach (var constraint in doc.Descendants(dts + "PrecedenceConstraint"))
                {
                    var from = constraint.Attribute(dts + "From")?.Value ?? "";
                    var to = constraint.Attribute(dts + "To")?.Value ?? "";
                    if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                    {
                        continue;
                    }

                    _graph.ExecutionEdges.Add(new ExecutionEdge
                    {
                        FromTaskId = from,
                        ToTaskId = to,
                        PrecedenceConstraintValue = constraint.Attribute(dts + "Value")?.Value ?? "Success",
                        Expression = constraint.Attribute(dts + "Expression")?.Value ?? ""
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] XML fallback parsing failed for package {Path.GetFileName(packagePath)}: {ex.Message}");
            }
        }

        private void ParseExecuteSqlTaskXmlFallback(XElement exe, PackageNode packageNode, TaskNode taskNode,
            Dictionary<string, object> variables)
        {
            try
            {
                XNamespace sqlTask = "www.microsoft.com/sqlserver/dts/tasks/sqltask";
                var sqlData = exe.Descendants()
                    .FirstOrDefault(x => x.Name.LocalName.Equals("SqlTaskData", StringComparison.OrdinalIgnoreCase));

                if (sqlData == null)
                {
                    return;
                }

                var sqlSource = sqlData.Attribute(sqlTask + "SqlStatementSource")?.Value
                    ?? sqlData.Attribute("SqlStatementSource")?.Value
                    ?? "";
                var connection = sqlData.Attribute(sqlTask + "Connection")?.Value
                    ?? sqlData.Attribute("Connection")?.Value
                    ?? "";

                // Resolve a variable-driven statement (SqlStatementSource = @[User::SQLQuery])
                if (ExpressionEvaluator.IsSingleVariableReference(sqlSource))
                {
                    var resolved = ExpressionEvaluator.Evaluate(sqlSource, variables);
                    if (!string.IsNullOrWhiteSpace(resolved)) sqlSource = resolved;
                }

                var componentId = taskNode.Id + "_sql";
                _graph.Components.Add(new ComponentNode
                {
                    Id = componentId,
                    Name = taskNode.Name,
                    Type = "Execute SQL Task",
                    PackageId = packageNode.Id,
                    TaskId = taskNode.Id,
                    ConnectionManager = connection,
                    SqlQueryOrTable = sqlSource
                });

                if (string.IsNullOrWhiteSpace(sqlSource)) return;

                // Stored-proc reference → body is loaded from SQL Server by the enricher.
                if (SqlProcedureDefinitionLoader.TryParseProcedureReference(sqlSource, out _, out _))
                {
                    _graph.Warnings.Add(
                        $"Execute SQL task '{taskNode.Name}' calls {sqlSource.Trim()} — procedure body will be loaded from SQL Server when a connection is available.");
                    return;
                }

                // Inline SQL — parse for column lineage (INSERT/UPDATE/DELETE/MERGE/SELECT INTO,
                // CTEs, dynamic SQL), same as the native path.
                var sqlRecords = SqlProcedureParser.Parse(sqlSource, connection, "LocalServer");
                foreach (var rec in sqlRecords)
                {
                    _graph.ColumnMappings.Add(BuildSqlTaskColumnMap(
                        rec, packageNode.Id, taskNode.Id, componentId, taskNode.Name));
                }
            }
            catch (Exception ex)
            {
                _graph.Warnings.Add($"Failed to parse Execute SQL task '{taskNode.Name}' from XML: {ex.Message}");
            }
        }

        private void ParseDataFlowXmlFallback(PackageNode packageNode, TaskNode taskNode, string taskRefId)
        {
            try
            {
                var doc = XDocument.Load(packageNode.Path);
                XNamespace dts = "www.microsoft.com/SqlServer/Dts";

                // Locate the executable node with taskRefId
                var exeNode = doc.Descendants(dts + "Executable")
                    .FirstOrDefault(x => x.Attribute(dts + "refId")?.Value == taskRefId);

                if (exeNode == null) return;

                // Enumerate pipeline components
                var components = exeNode.Descendants()
                    .Where(x => x.Name.LocalName == "component");
                foreach (var comp in components)
                {
                    var compId = comp.Attribute("refId")?.Value ?? "";
                    var compName = comp.Attribute("name")?.Value ?? "";
                    var compType = comp.Attribute("componentClassID")?.Value ?? "";

                    var compNode = new ComponentNode
                    {
                        Id = compId,
                        Name = compName,
                        Type = ThirdPartyComponentDetector.NormalizeComponentType(compType, compName),
                        PackageId = packageNode.Id,
                        TaskId = taskNode.Id
                    };

                    if (ThirdPartyComponentDetector.IsScriptComponent(compType, compName))
                    {
                        _graph.Warnings.Add(
                            $"Script Component '{compName}' in package '{packageNode.Name}' — only its declared input/output columns are captured; transformation logic inside the script is opaque.");
                    }
                    else if (ThirdPartyComponentDetector.IsLikelyThirdParty(compType, compName))
                    {
                        _graph.Warnings.Add(
                            $"Third-party or custom component '{compName}' (XML fallback) — lineage metadata may be incomplete.");
                    }

                    // Extract open rowset (table name) or SqlCommand
                    var sqlProp = comp.Descendants()
                        .FirstOrDefault(x => x.Attribute("name")?.Value == "SqlCommand");
                    var tableProp = comp.Descendants()
                        .FirstOrDefault(x => x.Attribute("name")?.Value == "OpenRowset");

                    if (sqlProp != null && !string.IsNullOrEmpty(sqlProp.Value))
                    {
                        compNode.SqlQueryOrTable = sqlProp.Value;
                    }
                    else if (tableProp != null && !string.IsNullOrEmpty(tableProp.Value))
                    {
                        compNode.SqlQueryOrTable = tableProp.Value;
                    }

                    var connection = comp.Descendants()
                        .FirstOrDefault(x => x.Name.LocalName == "connection");
                    if (connection != null)
                    {
                        compNode.ConnectionManager = connection.Attribute("connectionManagerRefId")?.Value
                            ?? connection.Attribute("connectionManagerID")?.Value
                            ?? "";
                    }

                    _graph.Components.Add(compNode);

                    // Extract input/output columns mapping
                    var inputCols = comp.Descendants()
                        .Where(x => x.Name.LocalName == "inputColumn");
                    foreach (var inCol in inputCols)
                    {
                        var lineageId = inCol.Attribute("lineageId")?.Value ?? "";
                        var colName = inCol.Attribute("cachedName")?.Value ?? inCol.Attribute("name")?.Value ?? "";
                        var targetCol = ExtractColumnNameFromRefId(inCol.Attribute("refId")?.Value)
                            ?? inCol.Attribute("name")?.Value
                            ?? colName;
                        var sourceComponentId = ExtractComponentIdFromLineageId(lineageId) ?? lineageId;

                        _graph.ColumnMappings.Add(new ColumnMap
                        {
                            PackageId = packageNode.Id,
                            TaskId = taskNode.Id,
                            SourceComponentId = sourceComponentId,
                            SourceComponentName = ExtractComponentNameFromRefId(sourceComponentId) ?? sourceComponentId,
                            SourceColumnName = colName,
                            TargetComponentId = compId,
                            TargetComponentName = compName,
                            TargetColumnName = targetCol,
                            OperationType = "XML_FALLBACK"
                        });
                    }
                }

                // Enumerate pipeline paths — strip .Outputs[...] / .Inputs[...] suffixes so IDs match component refIds
                var paths = exeNode.Descendants()
                    .Where(x => x.Name.LocalName == "path");
                foreach (var path in paths)
                {
                    var pathId  = path.Attribute("refId")?.Value ?? "";
                    var startId = StripPathEndpointSuffix(path.Attribute("startId")?.Value ?? "");
                    var endId   = StripPathEndpointSuffix(path.Attribute("endId")?.Value ?? "");

                    _graph.DataFlowEdges.Add(new DataFlowEdge
                    {
                        FromComponentId = startId,
                        ToComponentId   = endId,
                        PathRefId       = pathId
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] XML fallback parsing failed for Data Flow {taskNode.Name}: {ex.Message}");
            }
        }

        private static bool IsDataFlowTask(string executableType)
        {
            return executableType.Contains("Pipeline", StringComparison.OrdinalIgnoreCase)
                || executableType.Contains("PipelineTask", StringComparison.OrdinalIgnoreCase);
        }

        private static string? ExtractColumnNameFromRefId(string? refId)
        {
            if (string.IsNullOrWhiteSpace(refId))
            {
                return null;
            }

            var marker = ".Columns[";
            var start = refId.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return null;
            }

            start += marker.Length;
            var end = refId.IndexOf(']', start);
            return end > start ? refId.Substring(start, end - start) : null;
        }

        // Strip .Outputs[...] or .Inputs[...] tail from a pipeline path endpoint ID to get the bare component refId
        private static string StripPathEndpointSuffix(string id)
        {
            if (string.IsNullOrEmpty(id)) return id;
            var idx = id.IndexOf(".Outputs[", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return id.Substring(0, idx);
            idx = id.IndexOf(".Inputs[", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return id.Substring(0, idx);
            return id;
        }

        private static string? ExtractComponentIdFromLineageId(string lineageId)
        {
            if (string.IsNullOrWhiteSpace(lineageId))
            {
                return null;
            }

            var outputsIndex = lineageId.IndexOf(".Outputs[", StringComparison.OrdinalIgnoreCase);
            return outputsIndex > 0 ? lineageId.Substring(0, outputsIndex) : lineageId;
        }

        private static string? ExtractComponentNameFromRefId(string refId)
        {
            if (string.IsNullOrWhiteSpace(refId))
            {
                return null;
            }

            var parts = refId.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[^1] : null;
        }
        #endregion
    }
}
