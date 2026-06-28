using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Plugins.Fujifilm.Configuration;

internal static class CameraModelRules
{
    private static readonly string[] UnsupportedStillCameraModels =
    {
        "GFX ETERNA 55",
        "GFX ETERNA55",
        "ETERNA 55",
        "ETERNA55"
    };

    public static string NormalizeName(string name)
    {
        return new string((name ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
    }

    public static CameraConfig? FindBestMatch(IEnumerable<CameraConfig> configs, string productName)
    {
        var normalizedProduct = NormalizeName(productName);
        return configs
            .Select(config => new { Config = config, Key = NormalizeName(config.ModelName) })
            .Where(candidate => candidate.Key.Length > 0 && normalizedProduct.EndsWith(candidate.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.Key.Length)
            .Select(candidate => candidate.Config)
            .FirstOrDefault();
    }

    public static bool IsValid(CameraConfig config)
    {
        return !string.IsNullOrWhiteSpace(config.ModelName) &&
               config.CameraXSize > 0 && config.CameraYSize > 0 &&
               config.PixelSizeX > 0 && config.PixelSizeY > 0 &&
               config.DefaultMinSensitivity > 0 &&
               config.DefaultMaxSensitivity >= config.DefaultMinSensitivity &&
               config.DefaultMinExposure > 0 &&
               config.DefaultMaxExposure >= config.DefaultMinExposure;
    }

    public static bool IsKnownUnsupportedStillCamera(string productName)
    {
        var normalizedProduct = NormalizeName(productName);
        return UnsupportedStillCameraModels.Any(model =>
        {
            var normalizedModel = NormalizeName(model);
            return normalizedProduct.Equals(normalizedModel, StringComparison.OrdinalIgnoreCase) ||
                   normalizedProduct.EndsWith(normalizedModel, StringComparison.OrdinalIgnoreCase);
        });
    }
}
