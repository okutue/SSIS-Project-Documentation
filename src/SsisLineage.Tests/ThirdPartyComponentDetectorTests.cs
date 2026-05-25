using SsisLineage.Core;

namespace SsisLineage.Tests;

public class ThirdPartyComponentDetectorTests
{
    [Theory]
    [InlineData("CozyRoc.DynamicsCRM", "CRM Source", true)]
    [InlineData("Microsoft.SqlServer.DTS.Pipeline", "OLE DB Source", false)]
    public void IsLikelyThirdParty_detects_vendor_components(string type, string name, bool expected)
    {
        var result = ThirdPartyComponentDetector.IsLikelyThirdParty(type, name);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void NormalizeComponentType_prefixes_third_party_label()
    {
        var normalized = ThirdPartyComponentDetector.NormalizeComponentType("CozyRoc.Something", "My Transform");

        Assert.StartsWith("Third-Party:", normalized);
        Assert.Contains("My Transform", normalized);
    }
}
