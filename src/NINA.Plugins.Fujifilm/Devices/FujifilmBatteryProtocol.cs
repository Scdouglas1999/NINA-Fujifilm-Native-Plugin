using System;
using System.Linq;
using NINA.Plugins.Fujifilm.Configuration;

namespace NINA.Plugins.Fujifilm.Devices;

/// <summary>
/// Selects the only battery-query signatures verified in Fujifilm's model headers.
/// Calling the variadic native API with the wrong argument count is unsafe, so unknown
/// and legacy models deliberately return <see langword="null"/>.
/// </summary>
internal static class FujifilmBatteryProtocol
{
    internal const int OldModelParameterCount = 6;
    internal const int NewModelParameterCount = 8;

    private static readonly string[] EightParameterModels =
    {
        "GFX100SII", "GFX100II", "GFX100S", "GFX100",
        "X-H2S", "X-H2", "X-T5", "X-S20", "X-M5"
    };

    private static readonly string[] SixParameterModels =
    {
        "GFX50SII", "GFX50S", "GFX50R",
        "X-PRO3", "X-T4", "X-T3", "X-S10"
    };

    internal static int? GetParameterCount(string? modelName)
    {
        var normalized = CameraModelRules.NormalizeName(modelName ?? string.Empty);

        if (EightParameterModels.Any(model => IsModelMatch(normalized, model)))
        {
            return NewModelParameterCount;
        }

        if (SixParameterModels.Any(model => IsModelMatch(normalized, model)))
        {
            return OldModelParameterCount;
        }

        return null;
    }

    private static bool IsModelMatch(string normalizedProductName, string knownModel)
    {
        var normalizedKnownModel = CameraModelRules.NormalizeName(knownModel);
        return normalizedProductName.Equals(normalizedKnownModel, StringComparison.OrdinalIgnoreCase) ||
               normalizedProductName.EndsWith(normalizedKnownModel, StringComparison.OrdinalIgnoreCase);
    }
}
