# Changelog — SSIS Lineage (VS Code extension)

## [0.1.0] - Unreleased

Phase 1 MVP scaffold.

### Added
- Detect `.dtproj` projects in the workspace.
- **SSIS Lineage: Scan Project** command — runs the engine CLI and loads the result.
- **Lineage** activity-bar view with a Package → Task → Component tree.
- Lineage graph webview reusing the shared Cytoscape renderer (object + column views, Fit, Reset).
- Settings for CLI path, entry package, and SQL stored-procedure enrichment.
- **SSIS Lineage: Trace Lineage** — typeahead search for a column/table, choose
  direction (full / origins / impact); the column view renders the traced sub-graph
  with the focused node highlighted. Tracing is a TypeScript port of the engine's
  tracer running in-process over the loaded graph (instant, no round-trips).
- **SSIS Lineage: Export Trace (CSV)** — opens the last trace as a CSV document
  matching the engine's trace-export format.
- **Copilot agent tools** (Language Model Tools): `ssisLineage_search`,
  `ssisLineage_trace`, and `ssisLineage_status`, so agent mode can query the scanned
  lineage (referenceable as `#ssisSearch` / `#ssisTrace`).
- **SSIS Lineage: Set SQL Connection… / Clear SQL Connection** — connection string
  stored in VS Code Secret Storage (preferred over the plaintext setting); scan uses
  setting → secret → project `.conmgr`, in that order.
- **Scan diagnostics** — engine warnings are written to the output channel, and when a
  data-flow source runs a stored procedure but SQL procedure enrichment is off (so it
  appears disconnected from its source tables), the scan offers “Enable & re-scan”.
  Previously this disconnect was silent.
