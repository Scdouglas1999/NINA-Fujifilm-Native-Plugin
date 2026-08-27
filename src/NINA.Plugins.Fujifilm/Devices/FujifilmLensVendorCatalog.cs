using System;

namespace NINA.Plugins.Fujifilm.Devices;

/// <summary>
/// Resolves lens makers from identifiers returned by XSDK_GetLensInfo. The SDK lens structure has
/// no manufacturer field, so third-party identifiers must be learned explicitly.
/// </summary>
internal static class FujifilmLensVendorCatalog
{
    internal static string Resolve(string? model, string? productName)
    {
        if (string.Equals(model?.Trim(), "LX202A", StringComparison.OrdinalIgnoreCase))
        {
            return "Viltrox";
        }

        var product = productName?.Trim() ?? string.Empty;
        if (product.StartsWith("XF", StringComparison.OrdinalIgnoreCase) ||
            product.StartsWith("XC", StringComparison.OrdinalIgnoreCase) ||
            product.StartsWith("GF", StringComparison.OrdinalIgnoreCase))
        {
            return "Fujifilm";
        }

        return string.Empty;
    }

    internal static string FormatDisplayName(FujiCameraMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var vendor = string.IsNullOrWhiteSpace(metadata.LensVendor)
            ? string.Empty
            : $"{metadata.LensVendor.Trim()} ";
        var ois = metadata.HasImageStabilization ? " [OIS]" : string.Empty;
        return $"{vendor}{metadata.LensProductName.Trim()}{ois}";
    }
}
