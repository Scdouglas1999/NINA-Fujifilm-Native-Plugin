using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Plugins.Fujifilm.Devices;

/// <summary>
/// Converts the Shooting SDK's aperture representation (f-number multiplied by 100) into the
/// values shown to users. The camera remains authoritative: callers pass the values returned by
/// <c>XSDK_CapAperture</c>, so native and third-party electronic lenses follow the same path.
/// </summary>
public static class FujifilmApertureCatalog
{
    public const int None = 0;
    public const int Auto = 0xFFFF;

    public static IReadOnlyList<int> SelectManualValues(IEnumerable<int> reportedValues)
    {
        return (reportedValues ?? Array.Empty<int>())
            .Where(value => value > None && value != Auto)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
    }

    public static double ToFNumber(int sdkValue) => sdkValue / 100.0;

    public static int ToSdkValue(double fNumber) => checked((int)Math.Round(
        fNumber * 100.0,
        MidpointRounding.AwayFromZero));

    public static string Describe(int sdkValue) => $"f/{ToFNumber(sdkValue):0.0#}";
}
