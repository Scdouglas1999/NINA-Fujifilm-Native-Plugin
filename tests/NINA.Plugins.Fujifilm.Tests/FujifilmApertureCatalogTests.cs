using NINA.Plugins.Fujifilm.Devices;

namespace NINA.Plugins.Fujifilm.Tests;

public class FujifilmApertureCatalogTests
{
    [Fact]
    public void SelectManualValues_RemovesSentinelsDuplicatesAndSorts()
    {
        var values = FujifilmApertureCatalog.SelectManualValues(
            new[] { 800, 0, 280, 0xFFFF, 400, 280, -1 });

        Assert.Equal(new[] { 280, 400, 800 }, values);
    }

    [Theory]
    [InlineData(120, 1.2)]
    [InlineData(280, 2.8)]
    [InlineData(1100, 11.0)]
    public void ConvertsSdkValues(int sdkValue, double fNumber)
    {
        Assert.Equal(fNumber, FujifilmApertureCatalog.ToFNumber(sdkValue), 3);
        Assert.Equal(sdkValue, FujifilmApertureCatalog.ToSdkValue(fNumber));
    }

    [Fact]
    public void Describe_UsesPhotographyNotation()
    {
        Assert.Equal("f/1.2", FujifilmApertureCatalog.Describe(120));
        Assert.Equal("f/2.8", FujifilmApertureCatalog.Describe(280));
    }
}
