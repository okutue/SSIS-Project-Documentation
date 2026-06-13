# Changelog

All notable changes to this project will be documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

## [1.2.0] - 2026-06-13

### Added
- **Cross-platform engine** — the core engine and CLI now also target `net10.0` (in
  addition to `net10.0-windows`), so scanning runs on Windows, macOS, and Linux via the
  XML package parser. The Windows-only SSIS runtime path is unchanged on Windows.
- **VS Code extension** (preview, `vscode-extension/`) — scan, visualize, search/trace,
  drill-down, exports, diff, AI agent tools, and an MCP server, shipped as a
  self-contained VSIX (no .NET runtime required). Reuses this engine and graph renderer.
- **MCP server** (`SsisLineage.Mcp`) — exposes scan/search/trace over the Model Context
  Protocol to AI agents (Claude Desktop, Cursor, VS Code agent mode).
- **Per-connection-manager overrides** — redirect specific connection managers (by name
  or GUID) to your own connection string, in the app's scan form and the CLI
  (`scan --connection-managers <file.json>`); the single connection stays the fallback.
- **CLI `labels` and `trace`** — machine-readable lineage search and tracing over a
  `lineage.json` (sub-graph + ordered steps + trace CSV), used by the extension.

### Changed
- Scan form: connection options are regrouped into “SQL Server connection” (per-database
  overrides + single fallback) and “Name resolution” (linked servers, variable values),
  with clearer labels to distinguish a fallback connection from per-manager overrides.

## [1.1.4] - 2026-06-11

### Added
- **Reset layout** — a Reset button (beside Fit) on both the discovery and lineage-search diagrams restores nodes to their original positions after they have been dragged, for both the object data-flow and column views
- **Search-focus highlight** — searching a column or table in lineage search now highlights the matching node in the diagram with the same amber border used for the entry package

### Changed
- **Long labels wrap instead of truncating** — long table and column names in the column diagram now wrap onto multiple lines and the node grows in height to fit, so full names stay readable in the diagram and in PNG/screenshot exports (previously truncated with a tooltip that exports couldn't capture)

## [1.1.3] - 2026-06-11

### Added
- **Loaded-report progress & confirmation** — loading a saved report now shows a progress indicator while it parses and a success notification on completion (with the column-mapping count), and surfaces errors as a notification; the heavy parse runs off the UI thread so large reports stay responsive
- **Loaded-report identity** — the Generate Report panel now shows the loaded report's filename (e.g. "Loaded · report.json") so you can tell which saved report is on screen after navigating to the detailed view and back

## [1.1.2] - 2026-06-10

### Added
- **Linked-server resolution** — linked-server names are auto-resolved to their actual server via `sys.servers` on each connection used, with an optional manual override map (CLI `--linked-servers <file.json>`, UI field, and an auto-map toggle)
- **Live-schema column resolution** — unqualified columns in multi-table joins are resolved to their owning table from `INFORMATION_SCHEMA` when a connection is available (local tables automatically; remote/linked-server tables when SQL variable values are supplied); offline-safe, falling back to listing all candidate tables when unresolved
- **SQL variable values** — `scan --sql-variables <file.json>` and a matching UI field supply stored-procedure variable values (e.g. server/database) so dynamic-SQL and `OPENQUERY` names resolve to their real sources; empty by default (offline)
- **Dynamic SQL & remote queries** — nested dynamic SQL (`DECLARE`/`SET`) is composed and parsed, and `OPENQUERY`/`OPENROWSET` inner queries are traced so `SELECT * INTO` registers remote columns by name
- **Lookup reference lineage** — Lookup transforms now trace their reference query/table columns back to source

### Fixed
- **MERGE lineage** — source tables (including linked-server multi-part names and derived-table subqueries) are resolved correctly, and `INSERT` values are paired positionally with their target columns so literals/functions no longer shift the mapping
- **Cross-package lineage** — ADO NET sources/destinations and staging tables now reconcile across packages, so columns trace end-to-end from warehouse tables back through staging to their sources
- **Procedure-backed data-flow sources** — sources whose SQL is a stored procedure now stitch to the procedure's internal lineage, so dimension/fact columns trace back to their source tables instead of dead-ending at the procedure
- **Column-scoped tracing** — tracing a single column through a `SELECT *` step stays scoped to that column instead of expanding to every unrelated column
- **Server/database accuracy** — trace and detailed-report rows show the resolved connection's server/database instead of placeholder values

## [1.1.1] - 2026-06-10

### Fixed
- Removed the stray built-in "Browse Files" button that MudBlazor's file-upload component rendered next to Generate Report, and added proper spacing to the "Load saved report…" button

## [1.1.0] - 2026-06-09

### Added
- **Impact analysis & origins tracing** — Lineage Search now has Full lineage / Impact ↓ (downstream-only) / Origins ↑ (upstream-only) trace directions
- **Lineage drift detection** — CLI `diff <old.json> <new.json> [--output report.md] [--fail-on-changes]` compares two scans by stable identities and exits non-zero on drift for CI gates (see `docs/CI.md`)
- **Save / load reports** — save a generated report as `.lineage.json` and reopen it later (or on another machine) without re-scanning
- **Mermaid export** — table-level lineage flowchart (`lineage.mmd`) that renders directly in GitHub READMEs and wikis
- **OpenLineage export** — OpenLineage 1.x run events with columnLineage facets (`lineage.openlineage.json`) for Marquez, Microsoft Purview, and DataHub ingestion
- **Variable overrides** — `scan --variable-overrides <file.json>` applies SSIS catalog environment values over design-time variables and `Project.params`
- **Execute SQL parameter/result bindings** — captured (`@0 ← User::Var (Input)`) and shown in the component drill-down; positional `?` markers substituted so parameterised SQL parses
- **Event handler parsing** — executables inside OnError/OnPostExecute/etc. handlers are walked by the native parser (the XML parser already covered them)
- `docs/CI.md` — CI pipeline patterns, GitHub Actions sketch, SSISDB environment-extraction query

### Security
- All exports (JSON, YAML, Cypher, Markdown, HTML, CSV, Mermaid, OpenLineage) automatically redact credential values (`Password=`, `PWD=`, `AccountKey=`, `Secret=`, `Token=`, …)

## [1.0.0] - 2026-06-08

### Added
- Interactive Cytoscape.js DAG — zoom, pan, click-to-drill-down on any node or edge
- Column-level lineage mode with single-point sankey-style edges
- SQL procedure enrichment — resolves stored procedure lineage from SQL Server
- Drill-down modal showing package/task/component/edge detail and column lineage
- Fabric-style lineage cards in the summary panel
- Persistent report across navigation; default SQL enrichment toggle
- Per-column filters, global search, and column show/hide in the column lineage grid
- Summary tiles that deep-link to Detailed Report tabs
- Recent projects history (stored locally under `%APPDATA%\SsisLineage`)
- Desktop host via Photino — native window, no web server, no open port
- Web host via Blazor Server for dev/advanced use
- CLI `scan` command with JSON, YAML, Neo4j Cypher, Markdown, and HTML export
- MIT license

[Unreleased]: https://github.com/okutue/SSIS-Project-Documentation/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/okutue/SSIS-Project-Documentation/compare/v1.1.4...v1.2.0
[1.1.4]: https://github.com/okutue/SSIS-Project-Documentation/compare/v1.1.3...v1.1.4
[1.1.3]: https://github.com/okutue/SSIS-Project-Documentation/compare/v1.1.2...v1.1.3
[1.1.2]: https://github.com/okutue/SSIS-Project-Documentation/compare/v1.1.1...v1.1.2
[1.1.1]: https://github.com/okutue/SSIS-Project-Documentation/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/okutue/SSIS-Project-Documentation/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/okutue/SSIS-Project-Documentation/releases/tag/v1.0.0
