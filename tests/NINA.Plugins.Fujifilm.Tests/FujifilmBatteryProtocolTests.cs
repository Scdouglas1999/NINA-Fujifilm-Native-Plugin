using NINA.Plugins.Fujifilm.Devices;

namespace NINA.Plugins.Fujifilm.Tests;

public sealed class FujifilmBatteryProtocolTests
{
    [Theory]
    [InlineData("FUJIFILM GFX100S II", FujifilmBatteryProtocol.NewModelParameterCount)]
    [InlineData("FUJIFILM X-H2S", FujifilmBatteryProtocol.NewModelParameterCount)]
    [InlineData("X-T3", FujifilmBatteryProtocol.OldModelParameterCount)]
    [InlineData("FUJIFILM GFX 50S II", FujifilmBatteryProtocol.OldModelParameterCount)]
    public void UsesVerifiedModelSpecificSignature(string model, int expected)
    {
        Assert.Equal(expected, FujifilmBatteryProtocol.GetParameterCount(model));
    }

    [Theory]
    [InlineData("FUJIFILM X-T2")]
    [InlineData("FUJIFILM GFX100RF")]
    [InlineData("Unknown Camera")]
    [InlineData(null)]
    public void SkipsUnsafeVariadicCallForUnverifiedModel(string? model)
    {
        Assert.Null(FujifilmBatteryProtocol.GetParameterCount(model));
    }

    [Theory]
    [InlineData("FUJIFILM X-T30")]
    [InlineData("FUJIFILM GFX50")]
    public void DoesNotMatchSimilarUnsupportedModelPrefixes(string model)
    {
        Assert.Null(FujifilmBatteryProtocol.GetParameterCount(model));
    }
}
