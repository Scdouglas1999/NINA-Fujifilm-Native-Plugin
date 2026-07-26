using System;

namespace NINA.Plugins.Fujifilm.Devices;

/// <summary>Describes whether a requested position had to be pulled back to a hard stop.</summary>
public enum FocusClamp
{
    None = 0,

    /// <summary>The request was past the infinity end of the travel.</summary>
    BelowInfinityStop,

    /// <summary>The request was past the close-focus end of the travel.</summary>
    BeyondClosestStop
}

/// <summary>
/// Converts between the Fujifilm SDK's focus pulse counter and the zero-based, always-positive
/// position space that NINA and its autofocus routines expect.
/// </summary>
/// <remarks>
/// <para>
/// SDK_ProgrammingReference §4.2.1.12 (CapFocusPos) lays the focus axis out as:
/// </para>
/// <code>
/// INF |&lt;- lFocusOverSearchPlsINF ->| lFocusPlsINF ... lFocusPlsMOD |&lt;- lFocusOverSearchPlsMOD ->| MOD
/// </code>
/// <para>
/// The nominal INF and MOD marks are therefore *interior* points: the lens can be driven past
/// both of them into the over-search regions. Releases up to 3.0.2.0 built the usable range from
/// <c>[lFocusPlsINF, lFocusPlsMOD]</c> alone, which made the past-infinity travel unreachable and
/// produced negative positions whenever the lens was parked in it — the common resting place for a
/// full-spectrum body, whose focus falls beyond the visible-light infinity mark.
/// </para>
/// <para>
/// The SDK also warns that the pulse counter "is not absolute, but fluctuates with temperature and
/// a variety of other conditions", so callers must re-read the capability block each session and
/// treat positions as relative to it rather than as durable absolute coordinates.
/// </para>
/// </remarks>
public sealed class FocusTravelMap
{
    public FocusTravelMap(int focusPlsInfinity, int focusPlsMod, int overSearchInfinity, int overSearchMod, int minDriveStep)
    {
        FocusPlsInfinity = focusPlsInfinity;
        FocusPlsMod = focusPlsMod;

        // The SDK reports over-search as a magnitude of travel, not a signed pulse coordinate.
        OverSearchInfinity = Math.Abs(overSearchInfinity);
        OverSearchMod = Math.Abs(overSearchMod);

        // Pulse values ascend from INF to MOD on every lens observed so far, but the ordering is
        // not guaranteed by the SDK, so anchor on whichever mark is numerically lower.
        if (focusPlsInfinity <= focusPlsMod)
        {
            TravelMin = focusPlsInfinity - OverSearchInfinity;
            TravelMax = focusPlsMod + OverSearchMod;
        }
        else
        {
            TravelMin = focusPlsMod - OverSearchMod;
            TravelMax = focusPlsInfinity + OverSearchInfinity;
        }

        Step = minDriveStep > 0 ? minDriveStep : 1;
    }

    public int FocusPlsInfinity { get; }
    public int FocusPlsMod { get; }
    public int OverSearchInfinity { get; }
    public int OverSearchMod { get; }

    /// <summary>Lowest pulse value the lens can be driven to.</summary>
    public int TravelMin { get; }

    /// <summary>Highest pulse value the lens can be driven to.</summary>
    public int TravelMax { get; }

    /// <summary>Smallest movement the lens will honour, from lMinDriveStepMFDriveEndThresh.</summary>
    public int Step { get; }

    /// <summary>Total travel in NINA position units; this is what NINA reports as MaxStep.</summary>
    public int Range => TravelMax - TravelMin;

    /// <summary>
    /// NINA position of the nominal infinity mark. Astronomical focus lands at or just past this
    /// point, so it is also the amount of headroom an autofocus run has on the infinity side.
    /// </summary>
    public int InfinityPosition => FocusPlsInfinity - TravelMin;

    /// <summary>True when the lens reports no travel past the infinity mark.</summary>
    public bool HasNoPastInfinityTravel => OverSearchInfinity == 0;

    /// <summary>Converts an SDK pulse reading into a NINA position, clamping into [0, Range].</summary>
    public int ToPosition(int pulse, out bool clamped)
    {
        var raw = pulse - TravelMin;
        var bounded = Math.Clamp(raw, 0, Range);
        clamped = bounded != raw;
        return bounded;
    }

    public int ToPosition(int pulse) => ToPosition(pulse, out _);

    /// <summary>
    /// Converts a NINA position into an SDK pulse value, clamping to the hard stops.
    /// </summary>
    /// <remarks>
    /// Deliberately not rounded to <see cref="Step"/>. Snapping requests to a multiple of the
    /// minimum drive step makes every other position unreachable: on a lens reporting a step of 3,
    /// two thirds of the range simply did not exist, and asking for one of them moved the lens to a
    /// neighbouring position instead. N.I.N.A. then waited forever for a position that could never
    /// be reported, and the move had to be cancelled by hand.
    ///
    /// The minimum drive step describes the smallest movement the lens will make, not a grid it
    /// requires requests to sit on, so <c>XSDK_SetFocusPos</c> accepts any value in range and the
    /// lens goes as close as it can. It is still reported to N.I.N.A. as the focuser's step size.
    /// </remarks>
    public int ToPulse(int position, out FocusClamp clamp)
    {
        clamp = FocusClamp.None;

        if (position < 0)
        {
            clamp = FocusClamp.BelowInfinityStop;
            position = 0;
        }
        else if (position > Range)
        {
            clamp = FocusClamp.BeyondClosestStop;
            position = Range;
        }

        return TravelMin + position;
    }

    public int ToPulse(int position) => ToPulse(position, out _);
}
