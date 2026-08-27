using System;
using System.Collections.Generic;
using NINA.Plugins.Fujifilm.Interop;

namespace NINA.Plugins.Fujifilm.Devices;

/// <summary>
/// Works out how many values a camera's battery query returns, by asking the camera rather than by
/// consulting a list of model names.
/// </summary>
/// <remarks>
/// <para>
/// <c>CheckBatteryInfo</c>'s API parameter is the number of output values the call produces: 8 on
/// current bodies and 6 on older ones. A hardcoded model list gets this wrong for every model it has
/// not been taught about - it silently disabled battery reporting on the GFX100RF, for instance -
/// and needs editing for every camera Fujifilm releases.
/// </para>
/// <para>
/// Probing is safe as long as storage for the largest layout is always supplied. The hazard with a
/// variadic call is providing <i>too few</i> output pointers, because the SDK then writes through
/// whatever happens to be in the remaining argument slots. Passing the full eight and varying only
/// the declared count never does that: a camera that fills six simply leaves the last two alone.
/// The SDK validates the count and refuses one it does not implement, which is what makes the probe
/// conclusive rather than a guess.
/// </para>
/// </remarks>
internal static class FujifilmBatteryProtocol
{
    internal const int OldModelParameterCount = 6;
    internal const int NewModelParameterCount = 8;

    internal static bool? GetChargingState(int statusCode)
    {
        if (statusCode == FujifilmSdkWrapper.SDK_POWERCAPACITY_DC_CHARGE)
        {
            return true;
        }

        return statusCode switch
        {
            FujifilmSdkWrapper.SDK_POWERCAPACITY_EMPTY or
            FujifilmSdkWrapper.SDK_POWERCAPACITY_END or
            FujifilmSdkWrapper.SDK_POWERCAPACITY_PREEND or
            FujifilmSdkWrapper.SDK_POWERCAPACITY_HALF or
            FujifilmSdkWrapper.SDK_POWERCAPACITY_FULL or
            FujifilmSdkWrapper.SDK_POWERCAPACITY_HIGH or
            FujifilmSdkWrapper.SDK_POWERCAPACITY_PREEND5 or
            FujifilmSdkWrapper.SDK_POWERCAPACITY_20 or
            FujifilmSdkWrapper.SDK_POWERCAPACITY_40 or
            FujifilmSdkWrapper.SDK_POWERCAPACITY_60 or
            FujifilmSdkWrapper.SDK_POWERCAPACITY_80 or
            FujifilmSdkWrapper.SDK_POWERCAPACITY_100 or
            FujifilmSdkWrapper.SDK_POWERCAPACITY_FULL_CHARGE or
            FujifilmSdkWrapper.SDK_POWERCAPACITY_DC => false,
            _ => null
        };
    }

    /// <summary>Counts to try, largest first.</summary>
    internal static readonly IReadOnlyList<int> CandidateParameterCounts =
        new[] { NewModelParameterCount, OldModelParameterCount };

    /// <summary>
    /// Interprets the result of probing each candidate count.
    /// </summary>
    /// <param name="probe">
    /// Called with a candidate count; returns true when the camera accepted it.
    /// </param>
    /// <returns>The accepted count, or null when the camera implements neither.</returns>
    internal static int? Probe(Func<int, bool> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        foreach (var candidate in CandidateParameterCounts)
        {
            if (probe(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
