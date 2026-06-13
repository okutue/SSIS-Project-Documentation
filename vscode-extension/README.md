# SSIS Lineage (VS Code extension)

Scan SSIS projects and explore / trace data lineage without leaving VS Code.

> **Status: Phase 1 (MVP).** This is an early scaffold living in the
> [SSIS-Project-Documentation](../) monorepo. It reuses the same .NET lineage
> engine and the same Cytoscape graph renderer as the desktop/web app.

## What it does today

- Detects `.dtproj` projects in the workspace.
- **SSIS Lineage: Scan Project** runs the engine CLI and loads the result.
- **Lineage** activity-bar view: a Package → Task → Component tree of the scan.
- **Lineage graph** webview: the object/data-flow and column views, with Fit and
  Reset (reusing the shared renderer).
- **SSIS Lineage: Trace Lineage** — search any column or table, choose a direction
  (full / origins / impact), and the column view renders the traced sub-graph with
  the focused node highlighted. Tracing runs in-process (a TypeScript port of the
  engine's tracer over the loaded `lineage.json`), so it is instant.
- **SSIS Lineage: Export Trace (CSV)** — opens the last trace as a CSV document.
- **Copilot agent tools** — once a project is scanned, Copilot agent mode can call
  `#ssisSearch`, `#ssisTrace`, and a status tool to answer questions like “what feeds
  `DW.Dim_Customers.Email`?” or “what breaks if I change `source.Customers`?”.
- **SSIS Lineage: Set SQL Connection…** — stores a connection string in VS Code
  Secret Storage (preferred over the plaintext setting) for stored-procedure enrichment.

## Requirements

The extension drives the .NET lineage engine via its CLI. The cross-platform
build (`net10.0`) parses SSIS package XML on Windows, macOS, and Linux. SQL
stored-procedure enrichment additionally needs a reachable SQL Server (and, off
Windows, SQL/Entra auth rather than integrated security).

### Pointing at the CLI (development)

Build the CLI from the monorepo and set `ssisLineage.cliPath`:

```bash
dotnet build ../src/SsisLineage.Cli/SsisLineage.Cli.csproj -f net10.0
```

```jsonc
// settings.json
"ssisLineage.cliPath": ".../src/SsisLineage.Cli/bin/Debug/net10.0/SsisLineage.Cli.dll"
```

A packaged release will instead bundle a self-contained CLI under `bin/`, so end
users won't need .NET installed or this setting.

## Settings

| Setting | Purpose |
|---|---|
| `ssisLineage.cliPath` | Path to the CLI (`.dll` run via `dotnet`, or a self-contained executable) |
| `ssisLineage.startPackage` | Entry/master package to scan from; prompts if empty |
| `ssisLineage.includeSqlProcedures` | Resolve stored-procedure lineage from SQL Server |
| `ssisLineage.sqlConnectionString` | Connection string for proc enrichment; resolves from `.conmgr` if empty |

## Develop

```bash
npm install
npm run compile      # copies shared graph assets, then builds TypeScript
# Press F5 in VS Code to launch an Extension Development Host
```

The webview assets (`media/vendor/`) are copied from the Blazor RCL by
`scripts/copy-assets.mjs` so there is a single source of truth for the renderer.

## Roadmap

- ~~Phase 2: column trace / impact analysis~~ ✓ (search + trace + CSV export). In-webview
  click-through drill-down is in [BACKLOG.md](BACKLOG.md).
- ~~Phase 3: expose lineage to AI agents~~ ✓ via VS Code Language Model Tools (Copilot
  agent mode) **and** a provider-agnostic MCP server ([../src/SsisLineage.Mcp](../src/SsisLineage.Mcp))
  for Claude Desktop / Cursor / VS Code agent mode.
- ~~Phase 4: connection UX~~ ✓ secure connection via Secret Storage. Deeper mssql
  connection-profile integration is a follow-up.
