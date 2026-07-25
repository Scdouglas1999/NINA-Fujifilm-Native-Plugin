using System;
using System.Collections.Generic;

namespace NINA.Plugins.Fujifilm.Devices;

/// <summary>
/// Turns the raw sensitivity list a camera reports into the fixed ISO values N.I.N.A. can use.
/// </summary>
/// <remarks>
/// <c>XSDK_CapSensitivity</c> returns auto-ISO modes alongside real sensitivities, encoded as
/// non-positive values: -1 to -4 are AUTO 1-4, -10 is plain AUTO, and -400 onwards are the capped
/// auto modes. They are legitimate arguments to <c>XSDK_SetSensitivity</c>, but they are modes
/// rather than sensitivities, and letting a sequence select one would hand exposure control back to
/// the camera - never what an astrophotography sub-exposure wants.
/// </remarks>
public static class FujifilmSensitivityCatalog
{
    /// <summary>True when the SDK value is a fixed ISO rather than an auto-ISO mode.</summary>
    public static bool IsFixedSensitivity(int sdkValue) => sdkValue > 0;

    /// <summary>
    /// Filters a reported sensitivity list down to the fixed ISO values, preserving order.
    /// </summary>
    /// <param name="reported">Values as returned by the SDK.</param>
    /// <param name="autoModesIgnored">How many auto-ISO modes were dropped.</param>
    public static IReadOnlyList<int> SelectFixedSensitivities(
        IEnumerable<int> reported, out int autoModesIgnored)
    {
        ArgumentNullException.ThrowIfNull(reported);

        var fixedValues = new List<int>();
        autoModesIgnored = 0;

        foreach (var value in reported)
        {
            if (IsFixedSensitivity(value))
            {
                fixedValues.Add(value);
            }
            else
            {
                autoModesIgnored++;
            }
        }

        return fixedValues;
    }
}
