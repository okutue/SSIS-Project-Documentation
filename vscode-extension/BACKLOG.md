# Backlog — SSIS Lineage extension

Deferred items, captured so they aren't lost.

## Feature parity
- (nothing outstanding — scan, diagrams, search/trace/impact, drill-down, exports,
  load, diff, AI tools + MCP, and per-connection-manager overrides are all ported.)

## Done
- ~~**MCP server (.NET) wrapping the engine.**~~ Shipped as `src/SsisLineage.Mcp` —
  a stdio JSON-RPC MCP server exposing the real C# engine/tracer (`scan_project`,
  `search`, `trace`, `status`) to any MCP client. See its README for wiring.

## Done
- ~~**Lineage diff in the extension.**~~ `Diff Lineage…` runs the engine's diff and opens
  the markdown report (current scan vs a baseline, or two files).
- ~~**In-webview click-through drill-down.**~~ Clicking a column in the column view
  traces from it (renderer exposes a column-click hook; the webview posts it to the
  extension, which traces and re-renders).
- ~~**Auto-register the MCP server from the extension**~~ via
  `lm.registerMcpServerDefinitionProvider` (VS Code 1.101+) — installing the extension
  makes the MCP server available without hand-editing `mcp.json`.
- ~~**Collapse onto a single tracer.**~~ The extension now delegates tracing/labels/CSV
  to the engine (`ssis-lineage labels` / `trace`); the TS tracer port was removed. One
  `LineageTracer` (C#) serves the app, CLI, MCP server, and the extension.
