# Changelog

All notable changes to this project will be documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

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

[Unreleased]: https://github.com/okutue/SSIS-Project-Documentation/compare/v1.1.1...HEAD
[1.1.1]: https://github.com/okutue/SSIS-Project-Documentation/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/okutue/SSIS-Project-Documentation/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/okutue/SSIS-Project-Documentation/releases/tag/v1.0.0
