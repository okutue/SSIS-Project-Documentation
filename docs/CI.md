# Using the CLI in CI — lineage drift detection

The CLI's `scan` + `diff` commands let a pipeline detect **lineage drift**: column mappings,
tasks, or packages that appear or disappear when a pull request changes SSIS packages.

## Pattern

1. Scan the **base branch** (or use a committed baseline `lineage.json` artifact).
2. Scan the **PR branch**.
3. `diff` the two — exit code `1` (with `--fail-on-changes`) gates the merge; the markdown
   report becomes a PR comment or build artifact.

```powershell
# 1. Baseline (main)
dotnet run --project src/SsisLineage.Cli -- scan `
  --project-path "$env:BASE_CHECKOUT\SsisProject" --start-package "Master.dtsx" `
  --output ".\lineage-base" --no-cache

# 2. PR branch
dotnet run --project src/SsisLineage.Cli -- scan `
  --project-path "$env:PR_CHECKOUT\SsisProject" --start-package "Master.dtsx" `
  --output ".\lineage-pr" --no-cache

# 3. Diff — fails the build when lineage changed
dotnet run --project src/SsisLineage.Cli -- diff `
  ".\lineage-base\lineage.json" ".\lineage-pr\lineage.json" `
  --output ".\lineage-diff.md" --fail-on-changes
```

Exit codes: `0` no changes · `1` changes detected (only with `--fail-on-changes`) · `2` error.

> Tip: instead of failing the build, drop `--fail-on-changes` and post `lineage-diff.md`
> as a PR comment so reviewers see exactly which source→target flows a change introduces
> or removes.

## GitHub Actions sketch

```yaml
jobs:
  lineage-drift:
    runs-on: windows-latest   # SSIS parsing is Windows-only
    steps:
      - uses: actions/checkout@v4
        with: { path: pr }
      - uses: actions/checkout@v4
        with: { ref: main, path: base }
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 10.0.x }
      - name: Scan both branches and diff
        shell: pwsh
        run: |
          dotnet run --project pr/src/SsisLineage.Cli -- scan -p pr/SsisProject -s Master.dtsx -o lineage-pr --no-cache
          dotnet run --project pr/src/SsisLineage.Cli -- scan -p base/SsisProject -s Master.dtsx -o lineage-base --no-cache
          dotnet run --project pr/src/SsisLineage.Cli -- diff lineage-base/lineage.json lineage-pr/lineage.json -o lineage-diff.md --fail-on-changes
      - uses: actions/upload-artifact@v4
        if: always()
        with: { name: lineage-diff, path: lineage-diff.md }
```

## Variable overrides from an SSIS catalog environment

Design-time variable values sometimes differ from what runs in production. Extract the
environment values from SSISDB into a JSON file and pass it to `scan --variable-overrides`:

```sql
-- Run against the SSISDB catalog; produces Name/Value pairs for one environment
SELECT [name], [value]
FROM catalog.environment_variables ev
JOIN catalog.environments e ON ev.environment_id = e.environment_id
WHERE e.name = N'Production';
```

Shape the result as `"Namespace::Name": "value"` pairs:

```json
{
  "Project::TargetDatabase": "DW_Prod",
  "User::SourceFolder": "\\\\fileserver\\extracts"
}
```

```powershell
dotnet run --project src/SsisLineage.Cli -- scan `
  --project-path ".\SsisProject" --start-package "Master.dtsx" `
  --variable-overrides ".\prod-environment.json"
```

Overrides win over design-time values and `Project.params`, so variable-driven SQL and
child-package names resolve to their production values.

## Catalog exports

Every scan also writes:

| File | Use |
|------|-----|
| `lineage.mmd` | Mermaid flowchart — paste into a GitHub README/wiki/PR description |
| `lineage.openlineage.json` | OpenLineage run events — ingest into Marquez, Microsoft Purview (OpenLineage connector), or DataHub |

All outputs scrub credential values (`Password=`, `PWD=`, `AccountKey=`, …) automatically.
