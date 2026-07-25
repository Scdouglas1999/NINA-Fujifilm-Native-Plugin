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

    // XSDK_SHUTTER_* codes are the exposure time in microseconds (XAPI.h: 1/8000" = 122,
    // 1" = 1000000, 30" = 32000000), with a separate T-mode series for 2-60 minutes.
    // The catalog previously stopped at 60", so every longer code the camera advertised was
    // rejected as undocumented and the exposure was forced onto the bulb path.
    [Theory]
    [InlineData(35000000, 35.0)]
    [InlineData(60000000, 60.0)]
    [InlineData(256000000, 250.0)]
    [InlineData(2048000000, 2000.0)]
    [InlineData(64000030, 120.0)]    // XSDK_SHUTTER_2M
    [InlineData(64000060, 240.0)]    // XSDK_SHUTTER_4M
    [InlineData(64000090, 480.0)]    // XSDK_SHUTTER_8M
    [InlineData(64000120, 900.0)]    // XSDK_SHUTTER_15M
    [InlineData(64000150, 1800.0)]   // XSDK_SHUTTER_30M
    [InlineData(64000180, 3600.0)]   // XSDK_SHUTTER_60M
    public void Build_ResolvesLongExposureCodes(int sdkCode, double expectedSeconds)
    {
        var unknown = new List<int>();

        var result = FujifilmShutterSpeedCatalog.Build(
            new[] { sdkCode },
            Array.Empty<ShutterSpeedMapping>(),
            bulbCapable: false,
            bulbMaxSeconds: 0,
            unknown.Add);

        Assert.Empty(unknown);
        Assert.Equal(expectedSeconds, result[sdkCode], 6);
    }

    [Fact]
    public void GetTimedMaximum_ReachesOneHourWhenBodyAdvertisesTMode()
    {
        var map = FujifilmShutterSpeedCatalog.Build(
            new[] { 1000000, 32000000, 64000000, 64000120, 64000180 },
            Array.Empty<ShutterSpeedMapping>(),
            bulbCapable: false,
            bulbMaxSeconds: 0);

        Assert.Equal(3600.0, FujifilmShutterSpeedCatalog.GetTimedMaximum(map, fallback: 0), 6);
    }

    [Fact]
    public void SelectCode_PrefersNativeTimedCodeOverBulbForLongSubExposures()
    {
        // A 300s sub-exposure is the common astrophotography case. With the long codes present the
        // catalog can pick a timed code instead of falling through to bulb.
        var map = FujifilmShutterSpeedCatalog.Build(
            new[] { 32000000, 64000000, 64000060, 64000090 },
            Array.Empty<ShutterSpeedMapping>(),
            bulbCapable: true,
            bulbMaxSeconds: 3600);

        Assert.Equal(64000090, FujifilmShutterSpeedCatalog.SelectCode(map, 480.0, bulbCapable: true));
    }

    [Fact]
    public void SelectCode_ThrowsRatherThanSilentlyShorteningTheExposure()
    {
        // Without bulb, a request beyond the longest timed speed used to return the nearest timed
        // code. The frame was then exposed for 60s while the caller recorded 300s, so the sub was
        // written to FITS with an EXPTIME that did not match the light it collected.
        var map = FujifilmShutterSpeedCatalog.Build(
            new[] { 1000000, 32000000, 64000000 },
            Array.Empty<ShutterSpeedMapping>(),
            bulbCapable: false,
            bulbMaxSeconds: 0);

        var ex = Assert.Throws<InvalidOperationException>(
            () => FujifilmShutterSpeedCatalog.SelectCode(map, 300.0, bulbCapable: false));

        Assert.Contains("300", ex.Message);
        Assert.Contains("60", ex.Message);
    }

    [Fact]
    public void SelectCode_StillUsesBulbForLongRequestsWhenAvailable()
    {
        var map = FujifilmShutterSpeedCatalog.Build(
            new[] { 1000000, 64000000 },
            Array.Empty<ShutterSpeedMapping>(),
            bulbCapable: true,
            bulbMaxSeconds: 3600);

        Assert.Equal(FujifilmShutterSpeedCatalog.BulbCode,
            FujifilmShutterSpeedCatalog.SelectCode(map, 300.0, bulbCapable: true));
    }

    [Fact]
    public void SelectCode_RoundsWithinTheTimedRangeAsBefore()
    {
        var map = FujifilmShutterSpeedCatalog.Build(
            new[] { 1000000, 32000000, 64000000 },
            Array.Empty<ShutterSpeedMapping>(),
            bulbCapable: false,
            bulbMaxSeconds: 0);

        Assert.Equal(32000000, FujifilmShutterSpeedCatalog.SelectCode(map, 29.0, bulbCapable: false));
        Assert.Equal(64000000, FujifilmShutterSpeedCatalog.SelectCode(map, 60.0, bulbCapable: false));
    }
}

