using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Plugins.Fujifilm.Devices.LiveView;

/// <summary>
/// Maps between live view magnifications and the SDK codes that select them.
/// </summary>
/// <remarks>
/// <para>
/// <c>SetThroughImageZoom</c> takes one of the <c>SDK_THROUGH_ZOOM_*</c> codes, not a magnification.
/// The codes are not ordered by magnification - 0x0003 is x6.0 while 0x0004 is x4.0 - so treating
/// the argument as a zoom factor selects the wrong magnification, and anything above 0x0011 is not
/// a code at all.
/// </para>
/// <para>
/// Which codes exist varies by body, so the set to choose from comes from the camera's own
/// <c>CapThroughImageZoom</c> list rather than from anything hardcoded here. This table only records
/// what each code means, which is fixed by the SDK across every model.
/// </para>
/// </remarks>
public static class LiveViewZoomLevels
{
    /// <summary>SDK code to magnification, from the SDK_THROUGH_ZOOM_* constants.</summary>
    private static readonly IReadOnlyDictionary<int, double> Magnifications = new Dictionary<int, double>
    {
        [0x0001] = 1.0,
        [0x0002] = 2.5,
        [0x0003] = 6.0,
        [0x0004] = 4.0,
        [0x0005] = 8.0,
        [0x0006] = 16.0,
        [0x0007] = 2.0,
        [0x0008] = 3.3,
        [0x0009] = 6.6,
        [0x000A] = 13.1,
        [0x000B] = 24.0,
        [0x000C] = 19.7,
        [0x000D] = 8.3,
        [0x000E] = 17.0,
        [0x000F] = 6.8,
        [0x0010] = 14.0,
        [0x0011] = 12.0
    };

    /// <summary>Lowest and highest SDK codes that mean anything.</summary>
    public const int MinimumCode = 0x0001;
    public const int MaximumCode = 0x0011;

    /// <summary>Whether the value is a zoom code the SDK defines.</summary>
    public static bool IsKnownCode(int sdkCode) => Magnifications.ContainsKey(sdkCode);

    /// <summary>The magnification a code selects, or null when the code is not one the SDK defines.</summary>
    public static double? GetMagnification(int sdkCode) =>
        Magnifications.TryGetValue(sdkCode, out var magnification) ? magnification : null;

    /// <summary>Renders a code for display, e.g. "x2.5".</summary>
    public static string Describe(int sdkCode) =>
        GetMagnification(sdkCode) is { } magnification
            ? $"x{magnification:0.#}"
            : $"unknown(0x{sdkCode:X})";

    /// <summary>
    /// Picks the code closest to the requested magnification from the ones a camera advertises.
    /// </summary>
    /// <param name="advertisedCodes">Codes from the camera's CapThroughImageZoom list.</param>
    /// <param name="requestedMagnification">Desired magnification, e.g. 4.0 for x4.</param>
    /// <returns>The chosen code, or null when the camera advertised nothing usable.</returns>
    public static int? SelectCodeFor(IEnumerable<int> advertisedCodes, double requestedMagnification)
    {
        ArgumentNullException.ThrowIfNull(advertisedCodes);

        var usable = advertisedCodes
            .Where(IsKnownCode)
            .Select(code => (Code: code, Magnification: Magnifications[code]))
            .ToArray();

        if (usable.Length == 0)
        {
            return null;
        }

        return usable
            .OrderBy(entry => Math.Abs(entry.Magnification - requestedMagnification))
            .ThenBy(entry => entry.Magnification)
            .First()
            .Code;
    }

    /// <summary>
    /// The magnifications a camera offers, ascending, for presenting a choice to the user.
    /// </summary>
    public static IReadOnlyList<(int Code, double Magnification)> DescribeAvailable(IEnumerable<int> advertisedCodes)
    {
        ArgumentNullException.ThrowIfNull(advertisedCodes);

        return advertisedCodes
            .Where(IsKnownCode)
            .Select(code => (Code: code, Magnification: Magnifications[code]))
            .OrderBy(entry => entry.Magnification)
            .ToArray();
    }
}
