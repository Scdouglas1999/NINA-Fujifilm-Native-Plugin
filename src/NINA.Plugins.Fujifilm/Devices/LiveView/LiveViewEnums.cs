namespace NINA.Plugins.Fujifilm.Devices.LiveView;

/// <summary>
/// Live view image quality settings.
/// Values correspond to SDK constants.
/// </summary>
public enum LiveViewQuality
{
    /// <summary>
    /// Fine quality - best image quality.
    /// SDK: XSDK_LIVEVIEW_QUALITY_FINE = 0x0001
    /// </summary>
    Fine = 0x0001,

    /// <summary>
    /// Normal quality - balanced.
    /// SDK: XSDK_LIVEVIEW_QUALITY_NORMAL = 0x0002
    /// </summary>
    Normal = 0x0002,

    /// <summary>
    /// Basic quality - fastest streaming.
    /// SDK: XSDK_LIVEVIEW_QUALITY_BASIC = 0x0003
    /// </summary>
    Basic = 0x0003
}

/// <summary>
/// Live view image size settings.
/// Values correspond to SDK constants.
/// </summary>
public enum LiveViewSize
{
    /// <summary>
    /// Large size - 1280 pixels wide.
    /// SDK: XSDK_LIVEVIEW_SIZE_L = 0x0001
    /// </summary>
    Large = 0x0001,

    /// <summary>
    /// Medium size - 800 pixels wide.
    /// SDK: XSDK_LIVEVIEW_SIZE_M = 0x0002
    /// </summary>
    Medium = 0x0002,

    /// <summary>
    /// Small size - 640 pixels wide.
    /// SDK: XSDK_LIVEVIEW_SIZE_S = 0x0003
    /// </summary>
    Small = 0x0003
}

/// <summary>
/// Extension methods for Live View enums.
/// </summary>
public static class LiveViewEnumExtensions
{
    /// <summary>
    /// A short label for this size setting.
    /// </summary>
    /// <remarks>
    /// Deliberately relative rather than a pixel count. The frame size depends on the model and its
    /// sensor aspect ratio - a GFX100S II streams 1024x768 at Large, and a 3:2 body will not - so
    /// the only trustworthy dimensions are the ones read back from a decoded frame.
    /// </remarks>
    public static string GetSizeLabel(this LiveViewSize size)
    {
        return size switch
        {
            LiveViewSize.Large => "Large",
            LiveViewSize.Medium => "Medium",
            LiveViewSize.Small => "Small",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Gets a human-readable display name.
    /// </summary>
    public static string GetDisplayName(this LiveViewQuality quality)
    {
        return quality switch
        {
            LiveViewQuality.Fine => "Fine",
            LiveViewQuality.Normal => "Normal",
            LiveViewQuality.Basic => "Basic (Fastest)",
            _ => quality.ToString()
        };
    }

    /// <summary>
    /// Gets a human-readable display name.
    /// </summary>
    public static string GetDisplayName(this LiveViewSize size)
    {
        return size switch
        {
            LiveViewSize.Large => "Large (1280px)",
            LiveViewSize.Medium => "Medium (800px)",
            LiveViewSize.Small => "Small (640px)",
            _ => size.ToString()
        };
    }
}
