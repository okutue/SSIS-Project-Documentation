using SsisLineage.Core;

namespace SsisLineage.Tests;

public class SqlProcedureReferenceTests
{
    [Theory]
    [InlineData("stage.usp_stage_load_customers", "stage", "usp_stage_load_customers")]
    [InlineData("stage.usp_stage_load_orderitems", "stage", "usp_stage_load_orderitems")]
    [InlineData("stage.usp_stage_load_orders", "stage", "usp_stage_load_orders")]
    public void TryParseProcedureReference_parses_stage_package_procedures(string input, string schema, string name)
    {
        var ok = SqlProcedureDefinitionLoader.TryParseProcedureReference(input, out var parsedSchema, out var parsedName);

        Assert.True(ok);
        Assert.Equal(schema, parsedSchema, ignoreCase: true);
        Assert.Equal(name, parsedName, ignoreCase: true);
    }
}
