using System;
using System.Collections.Generic;
using NINA.Plugins.Fujifilm.Interop;
using NINA.Plugins.Fujifilm.Settings;

namespace NINA.Plugins.Fujifilm.Devices;

/// <summary>One camera property the plugin wants to change at connect.</summary>
/// <param name="Name">Human-readable name, used in diagnostics.</param>
/// <param name="SetApiCode">API code used to apply the value.</param>
/// <param name="GetApiCode">API code used to read the value back.</param>
/// <param name="Value">The SDK value to send.</param>
/// <param name="Describe">Renders an SDK value for the log.</param>
public sealed record CaptureQualityStep(
    string Name,
    int SetApiCode,
    int GetApiCode,
    int Value,
    Func<int, string> Describe);

/// <summary>
/// Translates the user's capture-quality settings into the concrete SDK calls to make, given what
/// the connected body says it supports.
/// </summary>
/// <remarks>
/// This is deliberately free of P/Invoke so the decisions can be tested without a camera: which
/// value each preference maps to, which steps are skipped on a body that does not advertise the
/// API, and that "leave alone" really does nothing.
/// </remarks>
public static class FujiCaptureQualityPlan
{
    public static int ToSdkValue(RawBitDepthPreference preference) => preference switch
    {
        RawBitDepthPreference.FourteenBit => FujifilmSdkWrapper.SDK_RAWOUTPUTDEPTH_14BIT,
        RawBitDepthPreference.SixteenBit => FujifilmSdkWrapper.SDK_RAWOUTPUTDEPTH_16BIT,
        _ => 0
    };

    public static int ToSdkValue(RawCompressionPreference preference) => preference switch
    {
        RawCompressionPreference.Uncompressed => FujifilmSdkWrapper.SDK_RAW_COMPRESSION_OFF,
        RawCompressionPreference.Lossless => FujifilmSdkWrapper.SDK_RAW_COMPRESSION_LOSSLESS,
        RawCompressionPreference.Lossy => FujifilmSdkWrapper.SDK_RAW_COMPRESSION_LOSSY,
        _ => 0
    };

    public static int ToSdkValue(CropModePreference preference) => preference switch
    {
        CropModePreference.Off => FujifilmSdkWrapper.SDK_CROPMODE_OFF,
        CropModePreference.Crop35mm => FujifilmSdkWrapper.SDK_CROPMODE_35MM,
        CropModePreference.SportsFinder125 => FujifilmSdkWrapper.SDK_CROPMODE_SPORTSFINDER_125,
        _ => -1
    };

    public static string DescribeBitDepth(int value) => value switch
    {
        FujifilmSdkWrapper.SDK_RAWOUTPUTDEPTH_14BIT => "14-bit",
        FujifilmSdkWrapper.SDK_RAWOUTPUTDEPTH_16BIT => "16-bit",
        _ => $"unknown(0x{value:X})"
    };

    public static string DescribeCompression(int value) => value switch
    {
        FujifilmSdkWrapper.SDK_RAW_COMPRESSION_OFF => "uncompressed",
        FujifilmSdkWrapper.SDK_RAW_COMPRESSION_LOSSLESS => "lossless",
        FujifilmSdkWrapper.SDK_RAW_COMPRESSION_LOSSY => "lossy",
        _ => $"unknown(0x{value:X})"
    };

    public static string DescribeCropMode(int value) => value switch
    {
        FujifilmSdkWrapper.SDK_CROPMODE_OFF => "off",
        FujifilmSdkWrapper.SDK_CROPMODE_35MM => "35mm",
        FujifilmSdkWrapper.SDK_CROPMODE_SPORTSFINDER_125 => "sports finder 1.25x",
        FujifilmSdkWrapper.SDK_CROPMODE_AUTO => "auto",
        _ => $"unknown(0x{value:X})"
    };

    public static string DescribeOnOff(int value) => value switch
    {
        FujifilmSdkWrapper.SDK_ON => "on",
        FujifilmSdkWrapper.SDK_OFF => "off",
        _ => $"unknown(0x{value:X})"
    };

    /// <summary>
    /// Builds the ordered list of property changes to apply for the given settings, skipping
    /// anything the camera does not advertise and anything the user asked to leave alone.
    /// </summary>
    public static IReadOnlyList<CaptureQualityStep> Build(FujiSettings settings, FujiApiCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(capabilities);

        var steps = new List<CaptureQualityStep>();

        var depth = ToSdkValue(settings.RawBitDepth);
        if (depth != 0 && capabilities.Supports(FujifilmSdkWrapper.API_CODE_SetRAWOutputDepth))
        {
            steps.Add(new CaptureQualityStep(
                "RAW bit depth",
                FujifilmSdkWrapper.API_CODE_SetRAWOutputDepth,
                FujifilmSdkWrapper.API_CODE_GetRAWOutputDepth,
                depth,
                DescribeBitDepth));
        }

        var compression = ToSdkValue(settings.RawCompression);
        if (compression != 0 && capabilities.Supports(FujifilmSdkWrapper.API_CODE_SetRAWCompression))
        {
            steps.Add(new CaptureQualityStep(
                "RAW compression",
                FujifilmSdkWrapper.API_CODE_SetRAWCompression,
                FujifilmSdkWrapper.API_CODE_GetRAWCompression,
                compression,
                DescribeCompression));
        }

        if (settings.DisableLongExposureNR && capabilities.Supports(FujifilmSdkWrapper.API_CODE_SetLongExposureNR))
        {
            steps.Add(new CaptureQualityStep(
                "Long exposure NR",
                FujifilmSdkWrapper.API_CODE_SetLongExposureNR,
                FujifilmSdkWrapper.API_CODE_GetLongExposureNR,
                FujifilmSdkWrapper.SDK_OFF,
                DescribeOnOff));
        }

        var crop = ToSdkValue(settings.CropMode);
        if (crop >= 0 && capabilities.Supports(FujifilmSdkWrapper.API_CODE_SetCropMode))
        {
            steps.Add(new CaptureQualityStep(
                "Crop mode",
                FujifilmSdkWrapper.API_CODE_SetCropMode,
                FujifilmSdkWrapper.API_CODE_GetCropMode,
                crop,
                DescribeCropMode));
        }

        return steps;
    }
}
