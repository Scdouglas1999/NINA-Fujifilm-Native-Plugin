using NINA.Plugins.Fujifilm.Configuration;
using NINA.Plugins.Fujifilm.Devices;

namespace NINA.Plugins.Fujifilm.Tests;

public sealed class FujifilmShutterSpeedCatalogTests
{
    [Fact]
    public void Build_OnlyIncludesReportedAndDocumentedCodes()
    {
        var unknown = new List<int>();

        var result = FujifilmShutterSpeedCatalog.Build(
            new[] { 1000000, 32000000, 123456 },
            Array.Empty<ShutterSpeedMapping>(),
            bulbCapable: true,
            bulbMaxSeconds: 900,
            unknown.Add);

        Assert.Equal(3, result.Count);
        Assert.Equal(1.0, result[1000000], 6);
        Assert.Equal(30.0, result[32000000], 6);
        Assert.Equal(900.0, result[FujifilmShutterSpeedCatalog.BulbCode], 6);
        Assert.Equal(new[] { 123456 }, unknown);
    }

    [Fact]
    public void Build_ModelMappingOverridesUniversalEncoding()
    {
        var result = FujifilmShutterSpeedCatalog.Build(
            new[] { 5 },
            new[] { new ShutterSpeedMapping { SdkCode = 5, Duration = 0.2 } },
            bulbCapable: false,
            bulbMaxSeconds: 0);

        Assert.Equal(0.2, result[5], 6);
    }

    [Fact]
    public void SelectCode_UsesBulbBeyondTimedRange()
    {
        var map = new Dictionary<int, double>
        {
            [1000000] = 1,
            [32000000] = 30,
            [FujifilmShutterSpeedCatalog.BulbCode] = 3600
        };

        Assert.Equal(FujifilmShutterSpeedCatalog.BulbCode, FujifilmShutterSpeedCatalog.SelectCode(map, 31, true));
        Assert.Equal(32000000, FujifilmShutterSpeedCatalog.SelectCode(map, 29, true));
        Assert.Equal(1000000, FujifilmShutterSpeedCatalog.SelectCode(map, 0.1, true));
    }

    [Fact]
    public void SelectCode_ThrowsWhenNoUsableExposureModeExists()
    {
        Assert.Throws<InvalidOperationException>(() =>
            FujifilmShutterSpeedCatalog.SelectCode(new Dictionary<int, double>(), 1, false));
    }
}
