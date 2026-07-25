using System;
using System.Globalization;
using NINA.Plugins.Fujifilm.Settings;

namespace NINA.Plugins.Fujifilm.Devices;

/// <summary>
/// Formats the raw distances the SDK reports for focus limiter endpoints.
/// </summary>
/// <remarks>
/// GetFocusLimiterRange returns "the absolute distance for the endpoint in mm or 1/1000 ft",
/// with the unit chosen by SetFocusScaleUnit. Both are thousandths of a base unit, so the raw
/// value is scaled the same way either way; only the label differs.
/// </remarks>
public static class FocusDistanceFormatter
{
    /// <summary>
    /// Renders a raw limiter distance. Values at or beyond the lens' infinity stop are reported as
    /// infinity rather than as a misleadingly precise number.
    /// </summary>
    public static string Format(int raw, FocusDistanceUnit unit)
    {
        if (raw <= 0)
        {
            return "unknown";
        }

        // The SDK uses a saturated value for "infinity"; anything past a kilometre is infinity as
        // far as any lens this plugin drives is concerned.
        if (raw >= 1_000_000)
        {
            return "infinity";
        }

        var value = raw / 1000.0;
        var suffix = unit == FocusDistanceUnit.Feet ? "ft" : "m";
        var format = value < 10 ? "0.##" : "0.#";
        return value.ToString(format, CultureInfo.InvariantCulture) + suffix;
    }
}

/// <summary>
/// Interprets the focus limiter indicator the camera reports.
/// </summary>
/// <remarks>
/// Per SDK_ProgrammingReference §4.2.1.17 every position in the indicator is normalised to
/// 0-1024, where <b>0 is the minimum object distance and 1024 is infinity</b> — the opposite
/// direction to the raw focus pulse counter, where infinity is the numerically lower end.
/// </remarks>
public sealed class FocusLimiterState
{
    /// <summary>Normalised value representing the closest focus position.</summary>
    public const int NormalizedMod = 0;

    /// <summary>Normalised value representing infinity.</summary>
    public const int NormalizedInfinity = 1024;

    /// <summary>
    /// How near the far endpoint may fall short of 1024 and still count as reaching infinity.
    /// Hardware does not always report exactly 1024.
    /// </summary>
    public const int InfinityTolerance = 16;

    public FocusLimiterState(int current, int dofNear, int dofFar, int posA, int posB, int status)
    {
        Current = current;
        DofNear = dofNear;
        DofFar = dofFar;
        RangeNear = Math.Min(posA, posB);
        RangeFar = Math.Max(posA, posB);
        IsRangeValid = status == 1;
    }

    /// <summary>Current focus position, 0 (closest) to 1024 (infinity).</summary>
    public int Current { get; }

    /// <summary>Depth-of-field endpoint on the close-focus side.</summary>
    public int DofNear { get; }

    /// <summary>Depth-of-field endpoint on the infinity side.</summary>
    public int DofFar { get; }

    /// <summary>Close end of the AF search range the limiter allows.</summary>
    public int RangeNear { get; }

    /// <summary>Far end of the AF search range the limiter allows.</summary>
    public int RangeFar { get; }

    /// <summary>Whether the camera reported the search range as valid.</summary>
    public bool IsRangeValid { get; }

    /// <summary>Current focus position as a percentage of the way from closest focus to infinity.</summary>
    public double PercentTowardInfinity =>
        Math.Clamp(Current, NormalizedMod, NormalizedInfinity) * 100.0 / NormalizedInfinity;

    /// <summary>
    /// True when a valid limiter range stops short of infinity. This is the case that silently
    /// breaks astrophotography: the lens physically refuses to focus where the stars are.
    /// </summary>
    public bool ExcludesInfinity => IsRangeValid && RangeFar < NormalizedInfinity - InfinityTolerance;

    /// <summary>
    /// True when a valid limiter range covers essentially the whole travel, i.e. the limiter is
    /// effectively off.
    /// </summary>
    public bool IsFullRange =>
        IsRangeValid &&
        RangeNear <= InfinityTolerance &&
        RangeFar >= NormalizedInfinity - InfinityTolerance;


    /// <summary>A one-line summary suitable for the focuser description and the diagnostics log.</summary>
    public string Describe()
    {
        var position = $"focus at {PercentTowardInfinity:0}% toward infinity";

        if (!IsRangeValid)
        {
            return $"{position}; focus limiter range not reported";
        }

        if (IsFullRange)
        {
            return $"{position}; focus limiter covers the full range";
        }

        var near = RangeNear * 100.0 / NormalizedInfinity;
        var far = RangeFar * 100.0 / NormalizedInfinity;
        var summary = $"{position}; focus limiter restricts AF to {near:0}%-{far:0}% of travel";

        return ExcludesInfinity
            ? summary + " and excludes infinity"
            : summary;
    }
}
