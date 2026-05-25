using SsisLineage.Core;

namespace SsisLineage.Tests;

public class SqlProcedureDefinitionLoaderTests
{
    [Theory]
    [InlineData("dbo.usp_Load", "dbo", "usp_Load")]
    [InlineData("[staging].[usp_Foo]", "staging", "usp_Foo")]
    [InlineData("EXEC dbo.usp_Bar", "dbo", "usp_Bar")]
    [InlineData("execute [hr].[sp_Update]", "hr", "sp_Update")]
    [InlineData("usp_Simple", "dbo", "usp_Simple")]
    public void TryParseProcedureReference_parses_common_formats(string input, string expectedSchema, string expectedName)
    {
        var ok = SqlProcedureDefinitionLoader.TryParseProcedureReference(input, out var schema, out var name);

        Assert.True(ok);
        Assert.Equal(expectedSchema, schema, ignoreCase: true);
        Assert.Equal(expectedName, name, ignoreCase: true);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseProcedureReference_rejects_empty_references(string input)
    {
        var ok = SqlProcedureDefinitionLoader.TryParseProcedureReference(input, out _, out _);

        Assert.False(ok);
    }
}
