using NINA.Plugins.Fujifilm.Devices;

namespace NINA.Plugins.Fujifilm.Tests;

/// <summary>
/// Regression cover for a reported failure: after upgrading from 3.0.1.0 to 3.0.2.0 the maximum
/// exposure N.I.N.A. offered dropped from 3600s to 60s, and rolling back restored it.
///
/// 3.0.2.0 changed bulb resolution to trust the SDK's flag alone. That flag reports "not capable"
/// on every body observed - 82 of 82 probes, including sessions that then ran a successful bulb
/// exposure - so bulb was treated as unavailable and the ceiling collapsed to the longest timed
/// shutter speed the catalog knew about, which was 60 seconds.
/// </summary>
public sealed class FujifilmBulbCapabilityTests
{
    [Fact]
    public void ReportedFailure_SdkDeniesBulb_MaximumExposureStaysAtTheConfiguredCeiling()
    {
        // Exactly the reported case: SDK says no bulb, the model configuration says the body has it,
        // and the catalog's longest timed speed is 60s.
        var bulbCapable = FujifilmBulbCapability.Resolve(sdkReportedBulbCapable: false, modelDefaultBulbCapable: true);
        var maximum = FujifilmBulbCapability.ResolveMaximumExposureSeconds(bulbCapable, 3600.0, timedMaximumSeconds: 60.0);

        Assert.True(bulbCapable);
        Assert.Equal(3600.0, maximum);

        // What 3.0.2.0 did, for contrast: trusting the SDK flag alone.
        var regressed = FujifilmBulbCapability.ResolveMaximumExposureSeconds(
            bulbCapable: false, configuredBulbMaximumSeconds: 3600.0, timedMaximumSeconds: 60.0);
        Assert.Equal(60.0, regressed);
    }

    [Theory]
    [InlineData(false, true, true)]    // SDK denies it, the model has it: believe the model
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]    // SDK confirms it: believe the SDK
    [InlineData(false, false, false)]  // neither: no bulb
    [InlineData(false, null, false)]   // no configuration for this body
    [InlineData(true, null, true)]
    public void BulbIsAvailableIfEitherSourceSaysSo(bool sdk, bool? config, bool expected)
        => Assert.Equal(expected, FujifilmBulbCapability.Resolve(sdk, config));

    [Fact]
    public void WithoutBulbTheCeilingIsTheLongestTimedSpeed()
    {
        Assert.Equal(60.0, FujifilmBulbCapability.ResolveMaximumExposureSeconds(false, 3600.0, 60.0));
    }

    [Fact]
    public void ABodyWhoseTimedShutterOutreachesTheConfiguredCeilingIsNotCappedByIt()
    {
        // Cameras advertising the T-mode codes can time 3600s natively; a lower configured ceiling
        // must not shorten that.
        Assert.Equal(3600.0, FujifilmBulbCapability.ResolveMaximumExposureSeconds(true, 900.0, 3600.0));
    }

    [Fact]
    public void AMissingOrNonsenseConfiguredCeilingFallsBackToAnHour()
    {
        Assert.Equal(3600.0, FujifilmBulbCapability.ResolveMaximumExposureSeconds(true, null, 60.0));
        Assert.Equal(3600.0, FujifilmBulbCapability.ResolveMaximumExposureSeconds(true, 0.0, 60.0));
        Assert.Equal(3600.0, FujifilmBulbCapability.ResolveMaximumExposureSeconds(true, -1.0, 60.0));
    }

    [Fact]
    public void TheTMoveCodesAloneAlsoRestoreTheCeiling()
    {
        // Independent of the bulb flag: once the catalog knows the T-mode codes, the timed maximum
        // reaches an hour by itself. Both routes were broken together in 3.0.2.0.
        var map = FujifilmShutterSpeedCatalog.Build(
            new[] { 1000000, 32000000, 64000000, 64000120, 64000180 },
            Array.Empty<NINA.Plugins.Fujifilm.Configuration.ShutterSpeedMapping>(),
            bulbCapable: false,
            bulbMaxSeconds: 0);

        var timedMax = FujifilmShutterSpeedCatalog.GetTimedMaximum(map, fallback: 0);
        Assert.Equal(3600.0, timedMax);
        Assert.Equal(3600.0, FujifilmBulbCapability.ResolveMaximumExposureSeconds(false, 3600.0, timedMax));
    }
}
