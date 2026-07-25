using NINA.Plugins.Fujifilm.Devices;

namespace NINA.Plugins.Fujifilm.Tests;

/// <summary>
/// XSDK_CapSensitivity returns auto-ISO modes mixed in with real sensitivities, encoded as
/// non-positive values. This only became visible once the capability query started working: while
/// it was silently failing, the plugin used a hardcoded fallback list and never saw them.
/// </summary>
public sealed class FujifilmSensitivityCatalogTests
{
    [Theory]
    [InlineData(-1)]     // AUTO 1
    [InlineData(-2)]     // AUTO 2
    [InlineData(-3)]     // AUTO 3
    [InlineData(-4)]     // AUTO 4
    [InlineData(-10)]    // AUTO
    [InlineData(-400)]   // AUTO capped at 400
    [InlineData(-6400)]  // AUTO capped at 6400
    [InlineData(0)]
    public void AutoModesAreNotFixedSensitivities(int sdkValue)
        => Assert.False(FujifilmSensitivityCatalog.IsFixedSensitivity(sdkValue));

    [Theory]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(1600)]
    [InlineData(12800)]
    public void RealSensitivitiesAreKept(int sdkValue)
        => Assert.True(FujifilmSensitivityCatalog.IsFixedSensitivity(sdkValue));

    [Fact]
    public void AutoModesAreFilteredOutAndCounted()
    {
        // Shape of a real reported list: fixed values with the auto modes mixed in.
        var reported = new[] { -1, -2, -3, 80, 100, 125, 160, 200, 12800 };

        var result = FujifilmSensitivityCatalog.SelectFixedSensitivities(reported, out var ignored);

        Assert.Equal(new[] { 80, 100, 125, 160, 200, 12800 }, result);
        Assert.Equal(3, ignored);
    }

    [Fact]
    public void OrderIsPreserved()
    {
        var reported = new[] { 400, -10, 200, -1, 100 };

        var result = FujifilmSensitivityCatalog.SelectFixedSensitivities(reported, out _);

        Assert.Equal(new[] { 400, 200, 100 }, result);
    }

    [Fact]
    public void AListOfNothingButAutoModesYieldsNoSensitivities()
    {
        var result = FujifilmSensitivityCatalog.SelectFixedSensitivities(new[] { -1, -2, -10 }, out var ignored);

        Assert.Empty(result);
        Assert.Equal(3, ignored);
    }
}
