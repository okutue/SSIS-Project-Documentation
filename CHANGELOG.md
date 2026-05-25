# Changelog

All notable changes to this project will be documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

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

[Unreleased]: https://github.com/okutue/SSIS-Project-Documentation/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/okutue/SSIS-Project-Documentation/releases/tag/v1.0.0
