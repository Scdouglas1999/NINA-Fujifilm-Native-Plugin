using NINA.Plugins.Fujifilm.Devices;
using NINA.Plugins.Fujifilm.Interop;
using NINA.Plugins.Fujifilm.Settings;

namespace NINA.Plugins.Fujifilm.Tests;

/// <summary>
/// Covers the decisions the plugin makes before touching the camera: which SDK value each
/// preference maps to, and which steps are skipped on a body that does not advertise the API.
///
/// The capability lists here are the ones a real GFX100S II (firmware 1.20) reported over USB,
/// which advertised all four capture-quality Set codes.
/// </summary>
public sealed class FujiCaptureQualityPlanTests
{
    private static FujiApiCapabilities Gfx100SII => new(new[]
    {
        FujifilmSdkWrapper.API_CODE_SetRAWOutputDepth,
        FujifilmSdkWrapper.API_CODE_SetRAWCompression,
        FujifilmSdkWrapper.API_CODE_SetLongExposureNR,
        FujifilmSdkWrapper.API_CODE_SetCropMode
    });

    [Fact]
    public void DefaultSettings_SetDepthCompressionAndNoiseReduction_ButLeaveFramingAlone()
    {
        var plan = FujiCaptureQualityPlan.Build(new FujiSettings(), Gfx100SII);

        Assert.Equal(3, plan.Count);

        Assert.Equal("RAW bit depth", plan[0].Name);
        Assert.Equal(FujifilmSdkWrapper.API_CODE_SetRAWOutputDepth, plan[0].SetApiCode);
        Assert.Equal(FujifilmSdkWrapper.SDK_RAWOUTPUTDEPTH_16BIT, plan[0].Value);

        Assert.Equal("RAW compression", plan[1].Name);
        Assert.Equal(FujifilmSdkWrapper.SDK_RAW_COMPRESSION_LOSSLESS, plan[1].Value);

        Assert.Equal("Long exposure NR", plan[2].Name);
        // SDK_OFF is 2, not 0. Sending 0 is what made the media-record call fail for years.
        Assert.Equal(FujifilmSdkWrapper.SDK_OFF, plan[2].Value);
        Assert.Equal(2, FujifilmSdkWrapper.SDK_OFF);

        Assert.DoesNotContain(plan, step => step.Name == "Crop mode");
    }

    [Fact]
    public void StepsAreSkippedWhenTheBodyDoesNotAdvertiseTheApi()
    {
        // An X-series body: RAW bit depth is not settable, everything else is.
        var xBody = new FujiApiCapabilities(new[]
        {
            FujifilmSdkWrapper.API_CODE_SetRAWCompression,
            FujifilmSdkWrapper.API_CODE_SetLongExposureNR
        });

        var plan = FujiCaptureQualityPlan.Build(new FujiSettings(), xBody);

        Assert.DoesNotContain(plan, step => step.Name == "RAW bit depth");
        Assert.Contains(plan, step => step.Name == "RAW compression");
        Assert.Contains(plan, step => step.Name == "Long exposure NR");
    }

    [Fact]
    public void LeaveAlonePreferences_ProduceNoSteps()
    {
        var settings = new FujiSettings
        {
            RawBitDepth = RawBitDepthPreference.LeaveAlone,
            RawCompression = RawCompressionPreference.LeaveAlone,
            CropMode = CropModePreference.LeaveAlone,
            DisableLongExposureNR = false
        };

        Assert.Empty(FujiCaptureQualityPlan.Build(settings, Gfx100SII));
    }

    [Fact]
    public void CropModeIsIncludedOnlyWhenExplicitlyChosen()
    {
        var settings = new FujiSettings { CropMode = CropModePreference.Crop35mm };

        var plan = FujiCaptureQualityPlan.Build(settings, Gfx100SII);
        var crop = Assert.Single(plan, step => step.Name == "Crop mode");

        Assert.Equal(FujifilmSdkWrapper.SDK_CROPMODE_35MM, crop.Value);
    }

    [Fact]
    public void CropModeOff_IsAValidRequestNotAnAbsentOne()
    {
        // SDK_CROPMODE_OFF is 0, so "off" must not be confused with "no preference".
        var settings = new FujiSettings { CropMode = CropModePreference.Off };

        var crop = Assert.Single(FujiCaptureQualityPlan.Build(settings, Gfx100SII), step => step.Name == "Crop mode");
        Assert.Equal(FujifilmSdkWrapper.SDK_CROPMODE_OFF, crop.Value);
        Assert.Equal(0, FujifilmSdkWrapper.SDK_CROPMODE_OFF);
    }

    [Theory]
    [InlineData(RawBitDepthPreference.FourteenBit, 1)]
    [InlineData(RawBitDepthPreference.SixteenBit, 2)]
    [InlineData(RawBitDepthPreference.LeaveAlone, 0)]
    public void BitDepthMapping(RawBitDepthPreference preference, int expected)
        => Assert.Equal(expected, FujiCaptureQualityPlan.ToSdkValue(preference));

    [Theory]
    [InlineData(RawCompressionPreference.Uncompressed, 1)]
    [InlineData(RawCompressionPreference.Lossless, 2)]
    [InlineData(RawCompressionPreference.Lossy, 3)]
    [InlineData(RawCompressionPreference.LeaveAlone, 0)]
    public void CompressionMapping(RawCompressionPreference preference, int expected)
        => Assert.Equal(expected, FujiCaptureQualityPlan.ToSdkValue(preference));

    [Theory]
    [InlineData(CropModePreference.Off, 0x0000)]
    [InlineData(CropModePreference.Crop35mm, 0x0001)]
    [InlineData(CropModePreference.SportsFinder125, 0x0002)]
    [InlineData(CropModePreference.LeaveAlone, -1)]
    public void CropMapping(CropModePreference preference, int expected)
        => Assert.Equal(expected, FujiCaptureQualityPlan.ToSdkValue(preference));

    [Fact]
    public void DescriptionsMatchTheValuesTheCameraReports()
    {
        // Values observed on the GFX100S II: depth=2, compression=1, LENR=1, crop=0x8001.
        Assert.Equal("16-bit", FujiCaptureQualityPlan.DescribeBitDepth(2));
        Assert.Equal("uncompressed", FujiCaptureQualityPlan.DescribeCompression(1));
        Assert.Equal("on", FujiCaptureQualityPlan.DescribeOnOff(1));
        Assert.Equal("off", FujiCaptureQualityPlan.DescribeOnOff(2));
        Assert.Equal("auto", FujiCaptureQualityPlan.DescribeCropMode(0x8001));
    }

    [Fact]
    public void UnknownCapabilities_StillProduceAPlan()
    {
        // A body that did not report its API list must not silently lose every setting.
        var plan = FujiCaptureQualityPlan.Build(new FujiSettings(), FujiApiCapabilities.Unknown);
        Assert.Equal(3, plan.Count);
    }
}
