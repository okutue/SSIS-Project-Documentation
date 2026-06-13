# Backlog — SSIS Lineage extension

Deferred items, captured so they aren't lost.

## From Phase 2 (search/trace)
- **In-webview click-through drill-down.** Clicking a node/column in the graph
  webview should trace from it (and re-render), instead of only the QuickPick-driven
  trace.

## Done
- ~~**MCP server (.NET) wrapping the engine.**~~ Shipped as `src/SsisLineage.Mcp` —
  a stdio JSON-RPC MCP server exposing the real C# engine/tracer (`scan_project`,
  `search`, `trace`, `status`) to any MCP client. See its README for wiring.

## Future
- **Auto-register the MCP server from the extension** via
  `lm.registerMcpServerDefinitionProvider` (VS Code 1.101+), so installing the
  extension makes the MCP server available without hand-editing `mcp.json`.

## Done
- ~~**Collapse onto a single tracer.**~~ The extension now delegates tracing/labels/CSV
  to the engine (`ssis-lineage labels` / `trace`); the TS tracer port was removed. One
  `LineageTracer` (C#) serves the app, CLI, MCP server, and the extension.
