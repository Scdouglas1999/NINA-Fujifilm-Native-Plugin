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
        // Normal is rejected outright by a GFX100S II, so the repaired value is Fine.
        Assert.Equal(LiveViewQuality.Fine, settings.LiveViewQuality);
        Assert.Equal(LiveViewSize.Large, settings.LiveViewSize);
    }

    [Fact]
    public void Normalize_ClampsNegativeBulbDelayToZero()
    {
        var settings = new FujiSettings { BulbReleaseDelayMs = -1 };

        settings.Normalize();

        Assert.Equal(0, settings.BulbReleaseDelayMs);
    }

    [Fact]
    public void Normalize_RepairsInvalidCaptureQualityEnums()
    {
        var settings = new FujiSettings
        {
            RawBitDepth = (RawBitDepthPreference)99,
            RawCompression = (RawCompressionPreference)99,
            CropMode = (CropModePreference)99,
            FocusDistanceUnit = (FocusDistanceUnit)99
        };

        settings.Normalize();

        Assert.Equal(RawBitDepthPreference.SixteenBit, settings.RawBitDepth);
        Assert.Equal(RawCompressionPreference.Lossless, settings.RawCompression);
        Assert.Equal(CropModePreference.LeaveAlone, settings.CropMode);
        Assert.Equal(FocusDistanceUnit.Meters, settings.FocusDistanceUnit);
    }

    [Fact]
    public void Defaults_FavourImageQualityAndLeaveFramingAlone()
    {
        var settings = new FujiSettings();

        // Better data by default...
        Assert.Equal(RawBitDepthPreference.SixteenBit, settings.RawBitDepth);
        Assert.Equal(RawCompressionPreference.Lossless, settings.RawCompression);
        Assert.True(settings.DisableLongExposureNR);
        Assert.True(settings.DisableCameraCardRecording);
        Assert.True(settings.ForceManualFocusMode);

        // ...but never silently change how the frame is composed.
        Assert.Equal(CropModePreference.LeaveAlone, settings.CropMode);
    }

    [Fact]
    public void Normalize_KeepsValidCaptureQualityChoices()
    {
        var settings = new FujiSettings
        {
            RawBitDepth = RawBitDepthPreference.LeaveAlone,
            RawCompression = RawCompressionPreference.Uncompressed,
            CropMode = CropModePreference.Crop35mm,
            FocusDistanceUnit = FocusDistanceUnit.Feet
        };

        settings.Normalize();

        Assert.Equal(RawBitDepthPreference.LeaveAlone, settings.RawBitDepth);
        Assert.Equal(RawCompressionPreference.Uncompressed, settings.RawCompression);
        Assert.Equal(CropModePreference.Crop35mm, settings.CropMode);
        Assert.Equal(FocusDistanceUnit.Feet, settings.FocusDistanceUnit);
    }
}

