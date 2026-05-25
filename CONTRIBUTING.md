# Contributing

Thank you for your interest in contributing to SSIS Lineage Utility.

## Prerequisites

| Requirement | Notes |
|---|---|
| Windows 10/11 x64 | SSIS DTS assemblies require Windows |
| .NET 10 SDK | `winget install Microsoft.DotNet.SDK.10` |
| SQL Server Integration Services | Any of: full SQL Server 2017–2022 install, the **SSIS Projects extension** for Visual Studio 2017/2019/2022, or **SSMS 18–22**. The DLLs just need to exist somewhere on the machine. |

> The project will not compile on Linux/macOS or on a machine without SSIS assemblies present.

## First-time setup after cloning

The four SSIS runtime DLLs are not on NuGet and cannot be committed to the repo (SQL Server licensing). After cloning, run the setup script **once** — it finds the DLLs wherever they are installed and copies them into `lib/ssis/`.

> **The script requires administrator privileges** to read from the Windows GAC and protected Program Files folders. It will automatically prompt for elevation if not already running as admin.

```powershell
# From the repo root — will auto-elevate if needed:
powershell -ExecutionPolicy Bypass -File setup-ssis-refs.ps1
```

You should see four green `OK` lines each showing the source path. If any are still missing, the script prints a search command you can run manually to locate the DLLs, then copy them into `lib\ssis\` by hand.

## Build & test

```powershell
dotnet build SsisLineage.slnx
dotnet test src/SsisLineage.Tests/SsisLineage.Tests.csproj
```

## Run locally

```powershell
# Desktop app (recommended)
dotnet run --project src/SsisLineage.Desktop

# Web UI
dotnet run --project src/SsisLineage.Web
# then open http://localhost:5057
```

## Submitting changes

1. Fork the repo and create a branch from `main`.
2. Keep changes focused — one logical change per PR.
3. Run tests before opening a PR: `dotnet test`.
4. Describe *why* in the PR body, not just what changed.

## Building a release binary

```powershell
dotnet publish src/SsisLineage.Desktop `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o ./publish
```

The output is a single `SsisLineage.Desktop.exe` in `./publish`.

## Security

Do not commit connection strings, server names, or any customer-specific metadata. See [`docs/RULE.md`](docs/RULE.md).
