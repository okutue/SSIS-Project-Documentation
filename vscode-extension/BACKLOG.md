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

## Future / provider-agnostic
- **MCP server (.NET) wrapping the engine.** Phase 3 ships VS Code Language Model
  Tools (reusing the in-process TS tracer). A separate MCP server in .NET that wraps
  `SsisLineage.Core` directly would expose the *real* C# tracer to any MCP client
  (Claude Desktop, Cursor, VS Code agent mode) and remove the C#/TS tracer
  duplication. Bigger lift; revisit once the extension stabilizes.

## Maintenance
- **Tracer duplication.** `src/tracer.ts` is a hand-port of
  `SsisLineage.Core/LineageTracer.cs`. Keep them in sync, or collapse onto the MCP
  server above so there is a single tracer implementation.
