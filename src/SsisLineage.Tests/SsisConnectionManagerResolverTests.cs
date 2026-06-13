using SsisLineage.Core;

namespace SsisLineage.Tests;

public class SsisConnectionManagerResolverTests
{
    [Fact]
    public void ExtractConnectionManagerName_parses_bracketed_reference()
    {
        var name = SsisConnectionManagerResolver.ExtractConnectionManagerName("Package.ConnectionManagers[OLE DB Connection]");

        Assert.Equal("OLE DB Connection", name);
    }

    [Fact]
    public void TryResolveConnectionString_resolves_by_dtsid_guid()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ssis-lineage-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var conmgrPath = Path.Combine(tempDir, "Staging.conmgr");
            File.WriteAllText(conmgrPath, """
                <?xml version="1.0"?>
                <DTS:ConnectionManager xmlns:DTS="www.microsoft.com/SqlServer/Dts"
                  DTS:ObjectName="Staging"
                  DTS:DTSID="{AF2C2600-2DCE-498A-AC64-A7FDC44BDB39}">
                  <DTS:ObjectData>
                    <DTS:ConnectionManager DTS:ConnectionString="Data Source=.;Initial Catalog=Staging;Integrated Security=True" />
                  </DTS:ObjectData>
                </DTS:ConnectionManager>
                """);

            var resolver = new SsisConnectionManagerResolver(tempDir);
            var connection = resolver.TryResolveConnectionString("{AF2C2600-2DCE-498A-AC64-A7FDC44BDB39}");

            Assert.NotNull(connection);
            Assert.Contains("Staging", connection);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Override_takes_precedence_over_conmgr_by_name()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ssis-lineage-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Staging.conmgr"), """
                <?xml version="1.0"?>
                <DTS:ConnectionManager xmlns:DTS="www.microsoft.com/SqlServer/Dts"
                  DTS:ObjectName="Staging" DTS:DTSID="{AF2C2600-2DCE-498A-AC64-A7FDC44BDB39}">
                  <DTS:ObjectData>
                    <DTS:ConnectionManager DTS:ConnectionString="Data Source=prod;Initial Catalog=Staging;Integrated Security=True" />
                  </DTS:ObjectData>
                </DTS:ConnectionManager>
                """);

            var overrides = new Dictionary<string, string> { ["Staging"] = "Data Source=local;Initial Catalog=Staging;Integrated Security=True" };
            var resolver = new SsisConnectionManagerResolver(tempDir, overrides);

            // Override (by name) wins over the .conmgr value, whether referenced by name or GUID.
            Assert.Contains("Source=local", resolver.TryResolveConnectionString("Package.ConnectionManagers[Staging]"));
            Assert.Contains("Source=local", resolver.TryResolveConnectionString("{AF2C2600-2DCE-498A-AC64-A7FDC44BDB39}"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Unlisted_connection_manager_falls_back_to_conmgr()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ssis-lineage-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "DW.conmgr"), """
                <?xml version="1.0"?>
                <DTS:ConnectionManager xmlns:DTS="www.microsoft.com/SqlServer/Dts" DTS:ObjectName="DW">
                  <DTS:ObjectData>
                    <DTS:ConnectionManager DTS:ConnectionString="Data Source=.;Initial Catalog=DW;Integrated Security=True" />
                  </DTS:ObjectData>
                </DTS:ConnectionManager>
                """);

            // Override only Staging — DW must still resolve from its .conmgr.
            var resolver = new SsisConnectionManagerResolver(tempDir, new Dictionary<string, string> { ["Staging"] = "x" });
            Assert.Contains("Initial Catalog=DW", resolver.TryResolveConnectionString("Package.ConnectionManagers[DW]"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TryResolveConnectionString_reads_conmgr_from_temp_directory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ssis-lineage-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var conmgrPath = Path.Combine(tempDir, "OLE DB Connection.conmgr");
            File.WriteAllText(conmgrPath, """
                <?xml version="1.0"?>
                <DTS:ConnectionManager xmlns:DTS="www.microsoft.com/SqlServer/Dts"
                  DTS:ObjectName="OLE DB Connection">
                  <DTS:ObjectData>
                    <connectionManager>
                      <connectionString>Data Source=.;Initial Catalog=TestDb;Integrated Security=True</connectionString>
                    </connectionManager>
                  </DTS:ObjectData>
                </DTS:ConnectionManager>
                """);

            var resolver = new SsisConnectionManagerResolver(tempDir);
            var connection = resolver.TryResolveConnectionString("Package.ConnectionManagers[OLE DB Connection]");

            Assert.NotNull(connection);
            Assert.Contains("TestDb", connection);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
