using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NINA.Plugins.Fujifilm.Configuration;
using NINA.Plugins.Fujifilm.Diagnostics;
using NINA.Plugins.Fujifilm.Devices;
using NINA.Plugins.Fujifilm.Interop;
using NINA.Plugins.Fujifilm.Settings;
using NINA.Profile.Interfaces;

namespace NINA.Plugins.Fujifilm.Imaging;

internal sealed class CameraImageBuilder
{
    private readonly IFujiSettingsProvider _settingsProvider;
    private readonly IFujifilmDiagnosticsService _diagnostics;
    private readonly IProfileService _profileService;

    public CameraImageBuilder(
        IFujiSettingsProvider settingsProvider, 
        IFujifilmDiagnosticsService diagnostics,
        IProfileService profileService)
    {
        _settingsProvider = settingsProvider;
        _diagnostics = diagnostics;
        _profileService = profileService;
    }

    public FujiImagePackage Build(
        RawCaptureResult raw,
        LibRawResult processed,
        FujiCameraCapabilities capabilities,
        CameraConfig? config)
    {
        var width = DetermineDimension(processed.Width, raw.Width, config?.CameraXSize ?? 0);
        var height = DetermineDimension(processed.Height, raw.Height, config?.CameraYSize ?? 0);

        // For X-Trans, we use DebayeredRgb and don't need BayerData, so skip the allocation
        var hasDebayeredRgb = processed.GetDebayeredRgb() is { Length: > 0 };
        ushort[] pixels;
        if (processed.Success && processed.BayerData.Length == width * height)
        {
            pixels = processed.BayerData;
        }
        else if (hasDebayeredRgb && processed.GetDebayeredRgb()!.Length == width * height * 3)
        {
            // X-Trans: GetExposure converts this RGB buffer to synthetic RGGB.
            pixels = Array.Empty<ushort>();
        }
        else
        {
            throw new InvalidDataException(
                $"RAW decoding returned no complete image buffer for {width}x{height} pixels " +
                $"(Bayer={processed.BayerData.Length}, RGB={processed.GetDebayeredRgb()?.Length ?? 0}).");
        }

        var (pattern, patternWidth, patternHeight) = ResolvePattern(processed, capabilities, config);
        var rafPath = ResolveRafSidecar(raw, processed);
        var metadata = BuildMetadata(raw, processed, capabilities, pattern, rafPath, config);
        
        // Get debayered RGB data if available (for X-Trans display in NINA)
        var debayeredRgb = processed.GetDebayeredRgb();
        if (debayeredRgb != null && debayeredRgb.Length == width * height * 3)
        {
            _diagnostics.RecordEvent("CameraImageBuilder", $"Debayered RGB data available: {debayeredRgb.Length} pixels (RGB)");
        }

        return new FujiImagePackage(
            pixels,
            width,
            height,
            pattern,
            patternWidth,
            patternHeight,
            metadata,
            rafPath,
            debayeredRgb,
            processed.GetCameraMultipliers(),
            processed.PreviewBitDepth);
    }

    private static int DetermineDimension(int libRawValue, int rawValue, int fallback)
    {
        if (libRawValue > 0)
        {
            return libRawValue;
        }

        if (rawValue > 0)
        {
            return rawValue;
        }

        return Math.Max(1, fallback);
    }

    private (string Pattern, int Width, int Height) ResolvePattern(
        LibRawResult processed,
        FujiCameraCapabilities capabilities,
        CameraConfig? config)
    {
        // If LibRaw provided pattern, use it (already validated in LibRawAdapter)
        if (!string.IsNullOrWhiteSpace(processed.ColorFilterPattern))
        {
            var pattern = processed.ColorFilterPattern;
            var width = processed.PatternWidth > 0 ? processed.PatternWidth : 
                       (pattern.StartsWith("XTRANS", StringComparison.OrdinalIgnoreCase) ? 6 : 2);
            var height = processed.PatternHeight > 0 ? processed.PatternHeight : 
                        (pattern.StartsWith("XTRANS", StringComparison.OrdinalIgnoreCase) ? 6 : 2);
            _diagnostics.RecordEvent("CameraImageBuilder", $"Using LibRaw pattern: {pattern} ({width}x{height})");
            return (pattern, width, height);
        }

        // Fallback: infer from camera model
        var fallbackPattern = InferPatternFromModel(capabilities.Metadata.ProductName, config?.ModelName);
        var fallbackSize = fallbackPattern.StartsWith("XTRANS", StringComparison.OrdinalIgnoreCase) ? 6 : 2;
        _diagnostics.RecordEvent("CameraImageBuilder", $"LibRaw pattern not available, inferred from model: {fallbackPattern} ({fallbackSize}x{fallbackSize})");
        return (fallbackPattern, fallbackSize, fallbackSize);
    }

    private static string InferPatternFromModel(string productName, string? modelName)
    {
        var name = (modelName ?? productName ?? string.Empty).Trim();
        if (name.StartsWith("GFX", StringComparison.OrdinalIgnoreCase))
        {
            return "RGGB";
        }

        if (name.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
        {
            return "XTRANS";
        }

        return "RGGB";
    }

    private string? ResolveRafSidecar(RawCaptureResult raw, LibRawResult processed)
    {
        if (!string.IsNullOrEmpty(processed.RafSidecarPath))
        {
            return processed.RafSidecarPath;
        }

        if (!_settingsProvider.Settings.SaveNativeRafSidecar || raw.RawBuffer.Length == 0)
        {
            return null;
        }

        try
        {
            // Use NINA's configured image directory
            var imageDirectory = _profileService.ActiveProfile.ImageFileSettings.FilePath;
            Directory.CreateDirectory(imageDirectory);
            
            // Generate filename with timestamp, exposure, and ISO
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            var exposureStr = $"{raw.ExposureSeconds:F1}s".Replace(".", "_");
            var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
            var fileName = $"Fuji_{timestamp}_{exposureStr}_ISO{raw.Iso}_{uniqueSuffix}.raf";
            
            var filePath = Path.Combine(imageDirectory, fileName);
            using (var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                stream.Write(raw.RawBuffer, 0, raw.RawBuffer.Length);
            }
            
            _diagnostics.RecordEvent("CameraImageBuilder", $"Saved RAF file to: {filePath}");
            return filePath;
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("CameraImageBuilder", $"Failed to save RAF file: {ex.Message}");
            return null;
        }
    }

    private IReadOnlyDictionary<string, string> BuildMetadata(
        RawCaptureResult raw,
        LibRawResult processed,
        FujiCameraCapabilities capabilities,
        string pattern,
        string? rafPath,
        CameraConfig? config)
    {
        var metadata = new Dictionary<string, string>
        {
            ["EXPTIME"] = raw.ExposureSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            ["ISO"] = raw.Iso.ToString(CultureInfo.InvariantCulture),
            ["ROWORDER"] = "TOP-DOWN"
        };

        // Critical bayer pattern metadata (required for PixInsight)
        if (!string.IsNullOrEmpty(pattern))
        {
            // Standardize pattern name for FITS/XISF compatibility
            var standardizedPattern = pattern.ToUpperInvariant();
            
            // For X-Trans, we are now providing a Synthetic RGGB image for NINA to debayer.
            // So we must report BAYERPAT=RGGB so NINA knows how to handle it.
            // We preserve the original X-Trans pattern in XTNSPAT.
            if (standardizedPattern.StartsWith("XTRANS", StringComparison.OrdinalIgnoreCase) || 
                standardizedPattern.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
            {
                // Report RGGB to NINA so it debayers the synthetic image
                metadata["BAYERPAT"] = "RGGB";
                metadata["XTNSPAT"] = "XTRANS"; // Custom keyword for the real pattern
                
                // Set dimensions for RGGB (2x2)
                metadata["CFAAXIS"] = "2x2";
            }
            else
            {
                metadata["BAYERPAT"] = standardizedPattern;
                
                // Determine pattern dimensions for standard Bayer
                var patternWidth = processed.PatternWidth > 0 ? processed.PatternWidth : 2;
                var patternHeight = processed.PatternHeight > 0 ? processed.PatternHeight : 2;
                metadata["CFAAXIS"] = $"{patternWidth}x{patternHeight}";
            }

            metadata["CFA"] = "1"; // Indicates color filter array present
        }

        // Standard astrophotography keywords
        metadata["XBINNING"] = "1";
        metadata["YBINNING"] = "1";
        metadata["XBAYROFF"] = "0";
        metadata["YBAYROFF"] = "0";

        // Camera model information
        if (!string.IsNullOrEmpty(capabilities.Metadata.ProductName))
        {
            metadata["INSTRUME"] = capabilities.Metadata.ProductName;
            metadata["CAMERA"] = capabilities.Metadata.ProductName; // Duplicate for stacking software compatibility
        }

        // Sensor type identifier
        if (!string.IsNullOrEmpty(pattern))
        {
            var sensorType = pattern.StartsWith("XTRANS", StringComparison.OrdinalIgnoreCase) ? "X-Trans CMOS" : "Bayer CMOS";
            metadata["SENSOR"] = sensorType;
        }

        // Pixel size (from config, in microns)
        if (config != null)
        {
            if (config.PixelSizeX > 0)
            {
                metadata["XPIXSZ"] = config.PixelSizeX.ToString("0.##", CultureInfo.InvariantCulture);
            }
            if (config.PixelSizeY > 0)
            {
                metadata["YPIXSZ"] = config.PixelSizeY.ToString("0.##", CultureInfo.InvariantCulture);
            }
        }

        if (_settingsProvider.Settings.EnableExtendedFitsMetadata)
        {
            metadata["FUJIMODE"] = capabilities.Metadata.ProductName;
            metadata["FUJIISO"] = raw.Iso.ToString(CultureInfo.InvariantCulture);
            metadata["FUJISHUT"] = raw.ShutterCode.ToString(CultureInfo.InvariantCulture);
            metadata["FUJILENS"] = capabilities.Metadata.LensProductName;
            metadata["FUJILENSSN"] = capabilities.Metadata.LensSerialNumber;
            metadata["XTNSPAT"] = pattern;
            metadata["BLACKLVL"] = processed.BlackLevel.ToString(CultureInfo.InvariantCulture);
            metadata["WHITELVL"] = processed.WhiteLevel.ToString(CultureInfo.InvariantCulture);
            metadata["FUJIDR"] = capabilities.Metadata.DynamicRangeCode.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(rafPath))
            {
                metadata["RAF_PATH"] = rafPath;
            }
        }

        var bayerPattern = metadata.GetValueOrDefault("BAYERPAT", null);
        _diagnostics.RecordFitsKeywordGeneration(metadata.Count, bayerPattern);

        return metadata;
    }
}

public sealed record FujiImagePackage(
    ushort[] Pixels,
    int Width,
    int Height,
    string ColorFilterPattern,
    int PatternWidth,
    int PatternHeight,
    IReadOnlyDictionary<string, string> FitsKeywords,
    string? RafSidecarPath,
    ushort[]? DebayeredRgb = null,
    double[]? CameraMultipliers = null,
    int PreviewBitDepth = 16)
{
    public static FujiImagePackage Empty { get; } = new(
        Array.Empty<ushort>(),
        0,
        0,
        string.Empty,
        0,
        0,
        new Dictionary<string, string>(),
        null,
        null);
    
    /// <summary>
    /// Gets debayered RGB data if available (for X-Trans display in NINA).
    /// Returns null if not available. Data is in RGBRGB... format, length = Width * Height * 3.
    /// </summary>
    public ushort[]? GetDebayeredRgb() => DebayeredRgb;

    public double[]? GetPreviewCameraMultipliers() =>
        CameraMultipliers != null && CameraMultipliers.Length > 0 ? CameraMultipliers : null;

    public int GetPreviewBitDepth() => PreviewBitDepth;

}
