namespace NINA.Plugins.Fujifilm.Devices;

/// <summary>
/// Metadata about the connected Fujifilm camera and lens.
/// </summary>
public sealed class FujiCameraMetadata
{
    // Camera body info
    public string ProductName { get; set; } = string.Empty;
    public string FirmwareVersion { get; set; } = string.Empty;
    public int DynamicRangeCode { get; set; }

    // Battery info
    public int BatteryLevel { get; set; }  // 0-100 percentage
    public string BatteryStatus { get; set; } = string.Empty;  // "OK", "Low", "Critical"
    public bool? IsCharging { get; set; }

    // Lens info (basic - from XSDK_GetLensInfo)
    public string LensProductName { get; set; } = string.Empty;
    public string LensSerialNumber { get; set; } = string.Empty;
    public string LensModel { get; set; } = string.Empty;
    public string LensVendor { get; set; } = string.Empty;

    // Lens capabilities (from XSDK_LensInformation)
    public bool HasImageStabilization { get; set; }
    public bool HasManualFocus { get; set; }
    public bool IsZoomLens { get; set; }

    // Current lens state (from GetLensZoomPos, GetAperture)
    public int CurrentFocalLength { get; set; }       // mm
    public int FocalLength35mmEquiv { get; set; }     // 35mm equivalent
    public double CurrentAperture { get; set; }       // f-number (e.g., 2.8)
    public int CurrentZoomPosition { get; set; }      // SDK zoom position code

    public static FujiCameraMetadata Empty { get; } = new();

    /// <summary>
    /// Creates a copy of this metadata with specified overrides.
    /// </summary>
    public FujiCameraMetadata Clone()
    {
        return new FujiCameraMetadata
        {
            ProductName = ProductName,
            FirmwareVersion = FirmwareVersion,
            DynamicRangeCode = DynamicRangeCode,
            BatteryLevel = BatteryLevel,
            BatteryStatus = BatteryStatus,
            IsCharging = IsCharging,
            LensProductName = LensProductName,
            LensSerialNumber = LensSerialNumber,
            LensModel = LensModel,
            LensVendor = LensVendor,
            HasImageStabilization = HasImageStabilization,
            HasManualFocus = HasManualFocus,
            IsZoomLens = IsZoomLens,
            CurrentFocalLength = CurrentFocalLength,
            FocalLength35mmEquiv = FocalLength35mmEquiv,
            CurrentAperture = CurrentAperture,
            CurrentZoomPosition = CurrentZoomPosition
        };
    }

    /// <summary>
    /// Creates metadata from legacy record parameters for backwards compatibility.
    /// </summary>
    public static FujiCameraMetadata FromLegacy(
        string productName,
        string firmwareVersion,
        string lensProductName,
        string lensSerialNumber,
        int dynamicRangeCode)
    {
        return new FujiCameraMetadata
        {
            ProductName = productName,
            FirmwareVersion = firmwareVersion,
            LensProductName = lensProductName,
            LensSerialNumber = lensSerialNumber,
            DynamicRangeCode = dynamicRangeCode
        };
    }
}
