using System;
using System.Collections.Generic;

namespace SsisLineage.Core.Models
{
    public class LineageGraph
    {
        public List<PackageNode> Packages { get; set; } = new();
        public List<TaskNode> Tasks { get; set; } = new();
        public List<ComponentNode> Components { get; set; } = new();
        public List<ColumnMap> ColumnMappings { get; set; } = new();
        public List<ExecutionEdge> ExecutionEdges { get; set; } = new();
        public List<DataFlowEdge> DataFlowEdges { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }

    public class PackageNode
    {
        public string Id { get; set; } = ""; // Package GUID
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string ProjectPath { get; set; } = "";
        public string FileHash { get; set; } = "";
        public List<string> Variables { get; set; } = new();
        public List<string> ConnectionManagers { get; set; } = new();
    }

    public class TaskNode
    {
        public string Id { get; set; } = ""; // Task GUID/RefId
        public string Name { get; set; } = "";
        public string Type { get; set; } = ""; // e.g. Execute SQL Task, Data Flow Task
        public string PackageId { get; set; } = "";
        public string PackageName { get; set; } = "";
        public string Description { get; set; } = "";
    }

    public class ComponentNode
    {
        public string Id { get; set; } = ""; // Component RefId/GUID
        public string Name { get; set; } = "";
        public string Type { get; set; } = ""; // e.g. OLE DB Source, Derived Column, OLE DB Destination
        public string PackageId { get; set; } = "";
        public string TaskId { get; set; } = "";
        public string ConnectionManager { get; set; } = ""; // Connection name if applicable
        public string SqlQueryOrTable { get; set; } = ""; // Table, SQL Query or referenced Stored Procedure
    }

    public class ColumnMap
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string PackageId { get; set; } = "";
        public string TaskId { get; set; } = "";
        public string SourceComponentId { get; set; } = "";
        public string SourceComponentName { get; set; } = "";
        public string SourceServer { get; set; } = "";
        public string SourceDatabase { get; set; } = "";
        public string SourceSchema { get; set; } = "";
        public string SourceTable { get; set; } = "";
        public string SourceColumnName { get; set; } = "";
        public string SourceExpression { get; set; } = "";
        public string TargetComponentId { get; set; } = "";
        public string TargetComponentName { get; set; } = "";
        public string TargetServer { get; set; } = "";
        public string TargetDatabase { get; set; } = "";
        public string TargetSchema { get; set; } = "";
        public string TargetTable { get; set; } = "";
        public string TargetColumnName { get; set; } = "";
        public string OperationType { get; set; } = ""; // e.g. OLEDB_DEST, DERIVED, SQL_PROC_INSERT
        public string ProcedureName { get; set; } = "";  // populated for SQL_PROC rows; e.g. "dbo.usp_Load"
        public string JoinDetails { get; set; } = "";     // actual SQL JOIN conditions from AST (not the proc name)
        public string FilterConditions { get; set; } = "";
    }

    public class ExecutionEdge
    {
        public string FromTaskId { get; set; } = "";
        public string ToTaskId { get; set; } = "";
        public string PrecedenceConstraintValue { get; set; } = ""; // Success, Failure, Completion
        public string Expression { get; set; } = "";
    }

    public class DataFlowEdge
    {
        public string FromComponentId { get; set; } = "";
        public string ToComponentId { get; set; } = "";
        public string PathRefId { get; set; } = "";
    }
}
