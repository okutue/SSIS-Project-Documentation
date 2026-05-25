---
name: ssis-lineage-utility
description: Build, test, extend, and safely operate the SSIS lineage extraction utility for C#/.NET projects that parse .dtsx SSIS packages, discover execution flow, extract data flow components, generate lineage JSON/Markdown reports, and protect sensitive project data. Use when working on this SSIS lineage utility, running it against SSIS projects, improving package parsing, generating outputs, debugging build/runtime issues, or reviewing artifacts for security and best practices.
---

# SSIS Lineage Utility

## Purpose

Use this skill when working on the SSIS lineage utility in this workspace. The project is a C#/.NET console tool that scans SSIS `.dtsx` packages, follows package execution relationships, extracts data flow components and paths, and writes usable lineage outputs.

Always apply `docs/RULE.md` before sharing, committing, logging, or exporting content from real SSIS projects. SSIS packages can contain connection strings, server names, file paths, credentials, SQL text, customer data structures, and proprietary process details.

## Project Workflow

1. Inspect the project before editing:
   - Find the console project under `src/SsisLineage.Cli` and the parser engine under `src/SsisLineage.Core`.
   - Read the relevant scanner, model, CLI, and report writer files before changing behavior.
   - Prefer existing project structure over new abstractions.
2. Build before running real package scans:
   - Run `dotnet build src/SsisLineage.Cli/SsisLineage.Cli.csproj`.
   - Fix compile errors before changing test inputs or package paths.
3. Smoke test with the sample package set when available:
   - Use `test-assets/SampleSsis`.
   - Confirm outputs are generated under a workspace-local output directory.
4. Run against real SSIS packages only with careful output handling:
   - Write generated reports to a workspace-local folder.
   - Do not expose raw package content or connection details in final responses.
   - Summarize counts, warnings, and high-level lineage findings.
5. Review generated outputs:
   - `lineage.json` for structured package/task/component/edge data.
   - `execution-flow.md` for package and task flow.
   - `dataflow-mappings.md` for data flow components, columns, and paths.

## Expected Commands

Build:

```powershell
dotnet build src/SsisLineage.Cli/SsisLineage.Cli.csproj
```

Show help:

```powershell
dotnet run --project src/SsisLineage.Cli -- --help
```

Run a sample scan:

```powershell
dotnet run --project src/SsisLineage.Cli -- --project-path test-assets\SampleSsis --start-package Root.dtsx --output test-output
```

Run a real project scan:

```powershell
dotnet run --project src/SsisLineage.Cli -- --project-path "<ssis-project-folder>" --start-package "<root-package>.dtsx" --output "<workspace-output-folder>"
```

## Implementation Guidelines

- Treat `.dtsx` files as XML and prefer structured XML parsing over text matching.
- Match XML element and attribute names by local name when namespaces vary.
- Preserve package traversal behavior:
  - Resolve the starting package.
  - Discover child package references from Execute Package tasks.
  - Avoid infinite loops by tracking visited package paths.
  - Emit warnings for unresolved references instead of crashing when possible.
- Preserve report compatibility:
  - Keep output file names stable unless the user asks for a breaking change.
  - Add fields to JSON rather than renaming existing fields.
  - Keep Markdown readable for non-developers.
- Keep tests and sample assets synthetic:
  - Do not copy real package secrets into fixtures.
  - Use fake connection names, fake servers, and fake data values.

## Parsing Priorities

When improving lineage extraction, prioritize in this order:

1. Correct package/task discovery.
2. Correct Execute Package task references.
3. Component-level data flow edges.
4. Source and destination column extraction.
5. Transformation-specific column mappings.
6. Connection manager metadata, with sensitive fields redacted.
7. SQL command and expression parsing, with sensitive literals minimized.

## Security And Privacy Guardrails

Use `docs/RULE.md` as the authoritative rule set for sensitive information handling.

Before final output or generated artifacts, check for:

- Connection strings and passwords.
- Server names, database names, usernames, file shares, and internal paths.
- SQL containing customer, account, or operational data.
- Excel sheet names, table names, and column names that reveal business-sensitive data.
- Generated JSON or Markdown that repeats sensitive metadata unnecessarily.

If sensitive values are needed to explain behavior, redact them with stable placeholders and preserve only the technical shape required to debug.

## Final Response Expectations

When reporting progress to the user:

- State whether build and run commands passed.
- Include package/task/edge/warning counts.
- Link to generated output files using absolute paths.
- Summarize important warnings or limitations.
- Do not paste raw sensitive package content.

