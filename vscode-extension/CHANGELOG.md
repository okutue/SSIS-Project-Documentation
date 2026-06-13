# Changelog — SSIS Lineage (VS Code extension)

## [0.1.0] - Unreleased

Phase 1 MVP scaffold.

### Added
- Detect `.dtproj` projects in the workspace.
- **SSIS Lineage: Scan Project** command — runs the engine CLI and loads the result.
- **Lineage** activity-bar view with a Package → Task → Component tree.
- Lineage graph webview reusing the shared Cytoscape renderer (object + column views, Fit, Reset).
- Settings for CLI path, entry package, and SQL stored-procedure enrichment.
