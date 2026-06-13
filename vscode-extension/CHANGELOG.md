# Changelog — SSIS Lineage (VS Code extension)

## [0.1.0] - 2026-06-13

First release. Scan SSIS projects and explore/trace column-level lineage in VS Code.

### Added
- **Scan Project** — scan a `.dtproj` from a chosen entry package; stored-procedure
  lineage resolved from SQL Server (on by default), with connections read from the
  project's `.conmgr` files.
- **Interactive graph** — object/data-flow and column-level views (shared renderer),
  zoom-to-fit, reset layout, PNG export.
- **Lineage** activity-bar view — Package → Task → Component tree.
- **Trace Lineage** — typeahead search for a column/table and trace upstream (origins),
  downstream (impact), or both; focused node highlighted, traced sub-graph rendered.
- **Click-through drill-down** — click a column in the column view to trace from it.
- **Export Trace (CSV)**, **Open Exports…** (JSON / YAML / Cypher / Markdown / HTML /
  Mermaid / OpenLineage), and **Load Lineage (JSON)…** to reopen a saved scan.
- **Diff Lineage…** — markdown drift report between a baseline and the current scan.
- **AI agents** — Language Model Tools (`#ssisSearch`, `#ssisTrace`, status) for Copilot
  agent mode, plus auto-registration of the bundled MCP server (VS Code 1.101+) for any
  MCP client.
- **Connections** — secure connection via Secret Storage (**Set SQL Connection…**), a
  single fallback connection, and per-connection-manager overrides (by name or GUID).
- **Scan diagnostics** — engine warnings surfaced; when a procedure-backed source isn't
  traced through because enrichment is off, offers “Enable & re-scan”.
- Ships as a self-contained, platform-specific VSIX — no .NET runtime required to use it.
