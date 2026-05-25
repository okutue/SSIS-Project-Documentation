# SSIS Lineage Utility

[![MIT License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D4?logo=windows)](https://github.com/okutue/SSIS-Project-Documentation)
[![Releases](https://img.shields.io/github/v/release/okutue/SSIS-Project-Documentation?include_prereleases)](https://github.com/okutue/SSIS-Project-Documentation/releases)
[![Offline](https://img.shields.io/badge/offline-100%25%20local-brightgreen)](https://github.com/okutue/SSIS-Project-Documentation#privacy--offline)

Scans Microsoft SSIS projects (`.dtproj` / `.dtsx`), builds a lineage graph (packages, tasks, data-flow components, column mappings, execution and data-flow edges), optionally enriches lineage from SQL Server stored procedures, and exports JSON, YAML, Neo4j Cypher, Markdown, and HTML.

## Requirements

| Component | Notes |
|-----------|-------|
| .NET SDK 10.0 | `winget install Microsoft.DotNet.SDK.10` |
| Windows 10/11 x64 | SSIS DTS assemblies are Windows-only |
| SSIS runtime assemblies | Provided by any of: full SQL Server 2017–2022, the **SSIS Projects** extension for Visual Studio 2017/2019/2022, or **SSMS 18–22** |

SSIS package parsing requires the Microsoft SSIS runtime DLLs present locally. Linux/macOS cannot run the DTS parser. See [First-time setup](#build) below.

## Solution layout

| Project | Purpose |
|---------|---------|
| `src/SsisLineage.Core` | Lineage engine (parse, cache, SQL enrich, exports) |
| `src/SsisLineage.Cli` | Console `scan` command |
| `src/SsisLineage.UI` | Shared Razor Class Library — MudBlazor UI, interactive Cytoscape DAG, services (used by both hosts) |
| `src/SsisLineage.Web` | Thin Blazor Server host (browser, for dev/advanced use) |
| `src/SsisLineage.Desktop` | Photino native desktop host (primary app — no web server, no open port) |
| `src/SsisLineage.Tests` | Unit tests (parsing and HTML anchors) |

The UI (Blazor + MudBlazor) lives once in `SsisLineage.UI` and is hosted two ways: a native desktop window (`SsisLineage.Desktop`, recommended) and a local web app (`SsisLineage.Web`). The interactive data-flow diagram is built on **Cytoscape.js** (vendored locally) — click any node or edge to drill into package/task/component/edge details and column lineage.

Additional docs: [`docs/SKILL.md`](docs/SKILL.md), [`docs/RULE.md`](docs/RULE.md).

## Build

**First time after cloning** — run the setup script once (requires admin, will auto-elevate):

```powershell
powershell -ExecutionPolicy Bypass -File setup-ssis-refs.ps1
```

This locates the SSIS runtime DLLs on your machine (GAC, Visual Studio extension, or SSMS) and copies them into `lib/ssis/`. See [`CONTRIBUTING.md`](CONTRIBUTING.md) for troubleshooting.

```powershell
dotnet build SsisLineage.slnx
dotnet test src/SsisLineage.Tests/SsisLineage.Tests.csproj
```

## CLI

```powershell
dotnet run --project src/SsisLineage.Cli -- scan `
  --project-path "<ssis-project-folder>" `
  --start-package "<root-package>.dtsx" `
  --output "./lineage-output"
```

Neo4j graph schema: [`docs/neo4j-schema.md`](docs/neo4j-schema.md).

Optional SQL procedure enrichment (uses override connection string or resolves from project `.conmgr` files):

```powershell
dotnet run --project src/SsisLineage.Cli -- scan `
  --project-path "<folder>" `
  --start-package "Root.dtsx" `
  --include-sql-procedures `
  --sql-connection-string "Server=.;Database=MyDb;Integrated Security=True;TrustServerCertificate=True"
```

## Desktop app (recommended)

```powershell
dotnet run --project src/SsisLineage.Desktop
```

Launches a native window — no web server, no open network port. Use **Browse…** to pick an SSIS project folder; the start package field autocompletes from the project's `.dtsx` files; recent projects are remembered locally.

## Web UI

```powershell
dotnet run --project src/SsisLineage.Web
```

Open `http://localhost:5057` (see `launchSettings.json`). Generate a report on the home page; the interactive diagram supports zoom/pan and click-to-drill-down; the column-lineage grid supports sort, per-column filters, global search, and column show/hide. Summary tiles deep-link to the **Detailed Report** tabs.

## Privacy / offline

Everything runs **fully local**. There are no cloud calls, no telemetry, and no CDN dependencies — MudBlazor and Cytoscape.js assets are vendored and served from the app itself. The desktop host has no web server or open port at all. Your SSIS projects never leave your machine. Recent-project history is stored locally under `%APPDATA%\SsisLineage`.

## Sample SSIS assets

Place synthetic sample projects under `test-assets/SampleSsis` (not committed by default). Use fake connection strings and servers per [`docs/RULE.md`](docs/RULE.md). Validate against a real project at `C:\Project\SSIS\` when available.

## Security

Do not commit connection strings, passwords, or customer-specific metadata. Redact sensitive values in logs and shared outputs; see [`docs/RULE.md`](docs/RULE.md).

## License

[MIT](LICENSE)
