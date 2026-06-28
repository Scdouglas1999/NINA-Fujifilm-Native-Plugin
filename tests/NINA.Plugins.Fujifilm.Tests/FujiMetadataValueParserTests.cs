using NINA.Plugins.Fujifilm.Imaging;

namespace NINA.Plugins.Fujifilm.Tests;

public sealed class FujiMetadataValueParserTests
{
    [Fact]
    public void Parse_ProducesStronglyTypedValues()
    {
        var exposure = FujiMetadataValueParser.Parse("EXPTIME", "12.5");
        var iso = FujiMetadataValueParser.Parse("ISO", "800");
        var cfa = FujiMetadataValueParser.Parse("CFA", "1");
        var pattern = FujiMetadataValueParser.Parse("BAYERPAT", "RGGB");

        Assert.Equal(FujiMetadataValueKind.Double, exposure.Kind);
        Assert.Equal(12.5, exposure.Value);
        Assert.Equal(FujiMetadataValueKind.Integer, iso.Kind);
        Assert.Equal(800, iso.Value);
        Assert.Equal(FujiMetadataValueKind.Boolean, cfa.Kind);
        Assert.Equal(true, cfa.Value);
        Assert.Equal(FujiMetadataValueKind.String, pattern.Kind);
        Assert.Equal("RGGB", pattern.Value);
    }

    [Fact]
    public void Parse_InvalidTypedValueFallsBackToString()
    {
        var value = FujiMetadataValueParser.Parse("ISO", "auto");

        Assert.Equal(FujiMetadataValueKind.String, value.Kind);
        Assert.Equal("auto", value.Value);
    }
}
