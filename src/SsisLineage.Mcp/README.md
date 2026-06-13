# SSIS Lineage — MCP server

A [Model Context Protocol](https://modelcontextprotocol.io) server (stdio,
newline-delimited JSON-RPC 2.0) that exposes the SSIS lineage engine to **any** MCP
client — Claude Desktop, Cursor, VS Code agent mode, etc. It wraps the real
`SsisLineage.Core` engine (no logic duplication): the same parser and tracer used by
the CLI and the app.

## Tools

| Tool | Purpose |
|---|---|
| `scan_project` | Scan a `.dtproj` and load its lineage into the session (run first). Args: `projectPath`, `startPackage`, optional `includeSqlProcedures`, `sqlConnectionString`. |
| `search` | Find columns/tables by substring. Args: `term`, optional `scope` (`column`\|`table`). |
| `trace` | Trace a column/table. Args: `target`, optional `direction` (`both`\|`upstream`\|`downstream`). |
| `status` | Report whether a project is loaded, with counts. |

## Build

```bash
dotnet build src/SsisLineage.Mcp/SsisLineage.Mcp.csproj -c Release
```

Cross-platform (`net10.0`): runs anywhere the engine does. Stored-procedure
enrichment needs a reachable SQL Server (SQL/Entra auth off Windows).

## Wire it up

The server reads JSON-RPC from stdin and writes responses to stdout; all logging
goes to stderr (stdout stays a clean protocol stream).

### VS Code (`.vscode/mcp.json`)

```jsonc
{
  "servers": {
    "ssis-lineage": {
      "command": "dotnet",
      "args": ["<repo>/src/SsisLineage.Mcp/bin/Release/net10.0/SsisLineage.Mcp.dll"]
    }
  }
}
```

### Claude Desktop (`claude_desktop_config.json`)

```jsonc
{
  "mcpServers": {
    "ssis-lineage": {
      "command": "dotnet",
      "args": ["<repo>/src/SsisLineage.Mcp/bin/Release/net10.0/SsisLineage.Mcp.dll"]
    }
  }
}
```

Then ask the agent to scan a project and trace a column, e.g.
*“Scan the SSIS project at … from Master.dtsx, then what feeds DW.Dim_Customers.Email?”*

## Protocol smoke test

```bash
printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' \
  | dotnet src/SsisLineage.Mcp/bin/Debug/net10.0/SsisLineage.Mcp.dll
```
