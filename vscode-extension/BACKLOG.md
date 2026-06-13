# Backlog — SSIS Lineage extension

Deferred items, captured so they aren't lost.

## From Phase 2 (search/trace)
- **In-webview click-through drill-down.** Clicking a node/column in the graph
  webview should trace from it (and re-render), instead of only the QuickPick-driven
  trace. Needs webview → extension messaging: the shared renderer's drill-down
  interop is a .NET ref (passed `null` here), so wire a Cytoscape tap handler in
  `media/main.js` that posts the picked node id to the extension, which then runs
  `tracer.trace` and re-renders. The column view's internal click-to-highlight-path
  already works without this.

## Done
- ~~**MCP server (.NET) wrapping the engine.**~~ Shipped as `src/SsisLineage.Mcp` —
  a stdio JSON-RPC MCP server exposing the real C# engine/tracer (`scan_project`,
  `search`, `trace`, `status`) to any MCP client. See its README for wiring.

## Future
- **Auto-register the MCP server from the extension** via
  `lm.registerMcpServerDefinitionProvider` (VS Code 1.101+), so installing the
  extension makes the MCP server available without hand-editing `mcp.json`.
- **Collapse the extension's LM tools onto the MCP server / C# tracer.** Today the
  in-extension LM tools use the TS tracer port (`src/tracer.ts`), while the MCP server
  uses the C# tracer. Pointing the extension at the engine would leave a single tracer
  implementation. Until then, keep `src/tracer.ts` in sync with
  `SsisLineage.Core/LineageTracer.cs`.
