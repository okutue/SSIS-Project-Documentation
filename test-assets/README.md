# Test assets

Add synthetic SSIS sample projects here for local validation (for example `SampleSsis/` with a `.dtproj` and `.dtsx` files).

- Use fake servers, databases, and connection manager names only.
- Do not copy production packages or real credentials into this repository.

Run a sample scan:

```powershell
dotnet run --project src/SsisLineage.Cli -- scan --project-path test-assets\SampleSsis --start-package Root.dtsx --output test-output
```
