# SSIS Lineage

Scan SQL Server Integration Services (SSIS) projects and explore, trace, and
document column-level data lineage without leaving VS Code — including AI-agent
access to your lineage.

![Object / data-flow diagram — packages, tasks, and components with the entry package highlighted](https://raw.githubusercontent.com/okutue/SSIS-Project-Documentation/main/docs/ssis-lineage-diagram-objects.png)
![Column-level lineage diagram — source-to-target column mappings](https://raw.githubusercontent.com/okutue/SSIS-Project-Documentation/main/docs/ssis-lineage-diagram-columns.png)

## Features

- **Scan a project** — point at a `.dtproj`, pick the entry package, and get the full
  lineage graph. Stored-procedure lineage is resolved from SQL Server (on by default).
- **Interactive graph** — object/data-flow and column-level views, zoom-to-fit, reset
  layout, and PNG export.
- **Search & trace** — find any column or table and trace it upstream (origins),
  downstream (impact), or both; the focused node is highlighted and the traced
  sub-graph is rendered. Click a column to drill in from there.
- **Lineage tree** — a Package → Task → Component view of the scan.
- **Exports** — open the scan's JSON, YAML, Cypher, Markdown, HTML, Mermaid, or
  OpenLineage outputs; export a trace to CSV; or load a saved `lineage.json` without
  re-scanning.
- **Diff** — compare a baseline against the current scan for change/impact review.
- **AI agents** — ask Copilot agent mode (or any MCP client) to search and trace your
  lineage: *“what feeds `DW.Dim_Customers.Email`?”*, *“what breaks if I change
  `source.Customers`?”*

## Requirements

- Self-contained VSIXes bundle the lineage engine, so **no .NET runtime is required**
  to use the extension.
- **Stored-procedure enrichment** needs a reachable SQL Server. Connections are read
  automatically from the project's `.conmgr` connection managers; on Windows,
  integrated security works out of the box (use SQL/Entra auth on macOS/Linux).

## Install

Install the platform-specific VSIX via **Extensions ▸ ⋯ ▸ Install from VSIX…**, or:

```bash
code --install-extension ssis-lineage-<platform>.vsix
```

Then open a folder containing your SSIS project and run **SSIS Lineage: Scan Project**
from the Command Palette or the SSIS Lineage view in the activity bar.

## Settings

| Setting | Purpose |
|---|---|
| `ssisLineage.includeSqlProcedures` | Resolve stored-procedure lineage from SQL Server (on by default) |
| `ssisLineage.startPackage` | Entry/master package to scan from; prompts if empty |
| `ssisLineage.sqlConnectionString` | Fallback connection for components whose connection manager isn't in `.conmgr` (prefer **Set SQL Connection…**, which uses Secret Storage) |
| `ssisLineage.connectionManagerOverrides` | Per-connection-manager connection-string overrides, by name or GUID |
| `ssisLineage.cliPath` | Override the bundled engine with a specific CLI build (advanced) |

## AI agents

Once a project is scanned, Copilot agent mode can use the `#ssisSearch`, `#ssisTrace`,
and status tools. On VS Code 1.101+ the bundled Model Context Protocol (MCP) server is
also registered automatically, and it can be used by any MCP client (Claude Desktop,
Cursor, …) — see the engine's [MCP server](../src/SsisLineage.Mcp).

## Build from source

Requires the .NET 10 SDK and Node.js.

```bash
npm install
npm run package                 # self-contained VSIX for this platform
npm run package -- linux-x64    # or: win32-x64 | win32-arm64 | linux-x64 | linux-arm64 | darwin-x64 | darwin-arm64
```

To develop, `npm run compile` then press **F5** to launch an Extension Development
Host. The graph renderer is shared with the desktop/web app and copied into
`media/vendor/` by `scripts/copy-assets.mjs`.

## License

MIT — see [LICENSE](LICENSE).
