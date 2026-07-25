using System;
using NINA.Plugins.Fujifilm.Devices.LiveView;

namespace NINA.Plugins.Fujifilm.Settings;

/// <summary>
/// Demosaicing algorithm quality levels for RAW preview processing.
/// Higher quality = better image but slower processing.
/// </summary>
public enum DemosaicQuality
{
    /// <summary>
    /// Linear interpolation - fastest (~1s), slight softness on edges.
    /// Best for astrophotography where speed matters and subjects are soft.
    /// </summary>
    Fast = 0,

    /// <summary>
    /// VNG (Variable Number of Gradients) - balanced (~3-4s), good edge handling.
    /// Good compromise between speed and quality.
    /// </summary>
    Balanced = 1,

    /// <summary>
    /// AHD (Adaptive Homogeneity-Directed) - highest quality (~15s), best edges.
    /// Use when preview quality is more important than download speed.
    /// </summary>
    HighQuality = 3
}

public sealed class FujiSettings
{
    public int BulbReleaseDelayMs { get; set; } = 500;
    
    /// <summary>
    /// Whether to save native RAF files alongside NINA's image files.
    /// IMPORTANT: For X-Trans cameras (X-T2, X-T3, X-T4, X-T5, X-H2, X-S10, etc.), 
    /// RAF files are REQUIRED for proper processing in PixInsight and other stacking software.
    /// The FITS/XISF files contain a synthetic RGGB conversion suitable only for NINA's preview.
    /// RAF files preserve the true X-Trans sensor data for accurate debayering and calibration.
    /// For GFX cameras (standard Bayer), RAF is optional but still recommended for maximum flexibility.
    /// Default is TRUE to ensure proper astrophotography workflow support.
    /// </summary>
    public bool SaveNativeRafSidecar { get; set; } = true;
    
    public bool EnableExtendedFitsMetadata { get; set; } = true;

    /// <summary>
    /// Demosaicing quality for preview images. Higher quality = slower downloads.
    /// Default is Fast for quick ~1s downloads. Does not affect saved RAW files.
    /// </summary>
    public DemosaicQuality PreviewDemosaicQuality { get; set; } = DemosaicQuality.Fast;

    /// <summary>
    /// Live view image quality setting.
    /// Default is Normal for balanced speed and quality.
    /// </summary>
    public LiveViewQuality LiveViewQuality { get; set; } = LiveViewQuality.Normal;

    /// <summary>
    /// Live view image size setting.
    /// Default is Large (1280px) for best preview detail.
    /// </summary>
    public LiveViewSize LiveViewSize { get; set; } = LiveViewSize.Large;

    /// <summary>
    /// Switch the camera into manual focus mode while the focuser is connected.
    /// XSDK_SetFocusPos targets manual focus mode, and a body left in AF-S/AF-C refocuses by
    /// itself whenever the shutter is half-pressed to start an exposure, which moves the lens
    /// away from the position NINA commanded. The previous mode is restored on disconnect.
    /// Disable this only if a body refuses to focus with the setting enabled.
    /// </summary>
    public bool ForceManualFocusMode { get; set; } = true;

    /// <summary>
    /// Ask the camera to stop writing captures to its own memory card while N.I.N.A. is connected.
    /// The plugin always intended to do this - a card write competes with the USB download and can
    /// stall a sequence - but it sent an invalid value that the SDK rejected, so card recording
    /// stayed on. Now that the value is correct, this is a setting: turn it off to keep an in-camera
    /// backup copy of every frame.
    /// </summary>
    public bool DisableCameraCardRecording { get; set; } = true;

    public void Normalize()
    {
        BulbReleaseDelayMs = Math.Clamp(BulbReleaseDelayMs, 0, 5000);
        if (!Enum.IsDefined(typeof(DemosaicQuality), PreviewDemosaicQuality))
        {
            PreviewDemosaicQuality = DemosaicQuality.Fast;
        }
        if (!Enum.IsDefined(typeof(global::NINA.Plugins.Fujifilm.Devices.LiveView.LiveViewQuality), LiveViewQuality))
        {
            LiveViewQuality = global::NINA.Plugins.Fujifilm.Devices.LiveView.LiveViewQuality.Normal;
        }
        if (!Enum.IsDefined(typeof(global::NINA.Plugins.Fujifilm.Devices.LiveView.LiveViewSize), LiveViewSize))
        {
            LiveViewSize = global::NINA.Plugins.Fujifilm.Devices.LiveView.LiveViewSize.Large;
        }
    }
}
