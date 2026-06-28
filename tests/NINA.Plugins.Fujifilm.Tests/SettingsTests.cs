using NINA.Plugins.Fujifilm.Devices.LiveView;
using NINA.Plugins.Fujifilm.Settings;

namespace NINA.Plugins.Fujifilm.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void Normalize_ClampsDelayAndRepairsInvalidEnums()
    {
        var settings = new FujiSettings
        {
            BulbReleaseDelayMs = 50_000,
            PreviewDemosaicQuality = (DemosaicQuality)99,
            LiveViewQuality = (LiveViewQuality)99,
            LiveViewSize = (LiveViewSize)99
        };

        settings.Normalize();

        Assert.Equal(5000, settings.BulbReleaseDelayMs);
        Assert.Equal(DemosaicQuality.Fast, settings.PreviewDemosaicQuality);
        Assert.Equal(LiveViewQuality.Normal, settings.LiveViewQuality);
        Assert.Equal(LiveViewSize.Large, settings.LiveViewSize);
    }

    [Fact]
    public void Normalize_ClampsNegativeBulbDelayToZero()
    {
        var settings = new FujiSettings { BulbReleaseDelayMs = -1 };

        settings.Normalize();

        Assert.Equal(0, settings.BulbReleaseDelayMs);
    }
}
