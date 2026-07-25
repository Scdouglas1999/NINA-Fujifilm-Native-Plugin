using NINA.Plugins.Fujifilm.Devices;

namespace NINA.Plugins.Fujifilm.Tests;

/// <summary>
/// Regression coverage for the focuser position mapping.
///
/// The reference values (INF=254, MOD=395, step=1) are the real XSDK_CapFocusPos readings captured
/// in the plugin diagnostics log from a Fujifilm body, so the "old behaviour" assertions below
/// reproduce the exact arithmetic that shipped through 3.0.2.0.
/// </summary>
public sealed class FocusTravelMapTests
{
    private const int RecordedInf = 254;
    private const int RecordedMod = 395;

    /// <summary>The pre-fix mapping: range built from the nominal marks only.</summary>
    private static int LegacyPosition(int pulse) => pulse - Math.Min(RecordedInf, RecordedMod);

    [Fact]
    public void PulsePastInfinity_UsedToReportNegative_AndIsNowPositive()
    {
        // A full-spectrum body focuses beyond the visible-light infinity mark, so the lens parks
        // below lFocusPlsINF. That is what produced the reported "negative position" readings.
        const int parkedPastInfinity = 240;

        Assert.True(LegacyPosition(parkedPastInfinity) < 0);

        var map = new FocusTravelMap(RecordedInf, RecordedMod, overSearchInfinity: 30, overSearchMod: 20, minDriveStep: 1);
        var position = map.ToPosition(parkedPastInfinity, out var clamped);

        Assert.False(clamped);
        Assert.Equal(16, position);
        Assert.InRange(position, 0, map.Range);
    }

    [Fact]
    public void InfinityMark_IsNotPinnedToZero_WhenOverSearchExists()
    {
        var map = new FocusTravelMap(RecordedInf, RecordedMod, overSearchInfinity: 30, overSearchMod: 20, minDriveStep: 1);

        // Previously infinity mapped to 0, leaving an autofocus run no room on the infinity side.
        Assert.Equal(0, LegacyPosition(RecordedInf));
        Assert.Equal(30, map.InfinityPosition);
        Assert.Equal(30, map.OverSearchInfinity);
        Assert.False(map.HasNoPastInfinityTravel);
    }

    [Fact]
    public void Range_CoversBothOverSearchRegions()
    {
        var map = new FocusTravelMap(RecordedInf, RecordedMod, overSearchInfinity: 30, overSearchMod: 20, minDriveStep: 1);

        Assert.Equal(224, map.TravelMin);
        Assert.Equal(415, map.TravelMax);
        Assert.Equal(191, map.Range);

        // The legacy range omitted all 50 pulses of over-search travel.
        Assert.Equal(141, RecordedMod - RecordedInf);
    }

    [Fact]
    public void AutofocusSweepAroundInfinity_StaysInRange()
    {
        // Reproduces the user's failure: sharpest focus lands a few steps from the infinity mark
        // and Hocus Focus samples symmetrically either side of it while building the curve.
        var map = new FocusTravelMap(RecordedInf, RecordedMod, overSearchInfinity: 30, overSearchMod: 20, minDriveStep: 1);
        var focusPoint = map.InfinityPosition + 5;

        for (var offset = -5; offset <= 5; offset++)
        {
            var requested = focusPoint + (offset * 4);
            map.ToPulse(requested, out var clamp);
            Assert.Equal(FocusClamp.None, clamp);
        }

        // The same sweep under the old mapping ran off the bottom of the range.
        Assert.True(LegacyPosition(RecordedInf) + 5 - 20 < 0);
    }

    [Fact]
    public void ToPulse_ClampsAtBothHardStops()
    {
        var map = new FocusTravelMap(RecordedInf, RecordedMod, overSearchInfinity: 30, overSearchMod: 20, minDriveStep: 1);

        Assert.Equal(map.TravelMin, map.ToPulse(-25, out var low));
        Assert.Equal(FocusClamp.BelowInfinityStop, low);

        Assert.Equal(map.TravelMax, map.ToPulse(map.Range + 25, out var high));
        Assert.Equal(FocusClamp.BeyondClosestStop, high);
    }

    [Fact]
    public void PositionAndPulse_RoundTrip()
    {
        var map = new FocusTravelMap(RecordedInf, RecordedMod, overSearchInfinity: 30, overSearchMod: 20, minDriveStep: 1);

        for (var position = 0; position <= map.Range; position++)
        {
            Assert.Equal(position, map.ToPosition(map.ToPulse(position)));
        }
    }

    [Fact]
    public void ToPosition_ClampsReadingsOutsideAdvertisedTravel()
    {
        // The SDK warns the pulse counter drifts with temperature, so a reading can fall outside
        // the advertised travel. NINA requires 0 <= Position <= MaxStep.
        var map = new FocusTravelMap(RecordedInf, RecordedMod, overSearchInfinity: 30, overSearchMod: 20, minDriveStep: 1);

        Assert.Equal(0, map.ToPosition(map.TravelMin - 10, out var lowClamped));
        Assert.True(lowClamped);

        Assert.Equal(map.Range, map.ToPosition(map.TravelMax + 10, out var highClamped));
        Assert.True(highClamped);
    }

    [Fact]
    public void ZeroOverSearch_LeavesInfinityAtTheHardStop()
    {
        var map = new FocusTravelMap(RecordedInf, RecordedMod, overSearchInfinity: 0, overSearchMod: 0, minDriveStep: 1);

        Assert.True(map.HasNoPastInfinityTravel);
        Assert.Equal(0, map.InfinityPosition);
        Assert.Equal(141, map.Range);
    }

    [Fact]
    public void DescendingPulseOrdering_IsHandled()
    {
        // The SDK does not guarantee INF < MOD, so the map anchors on the lower mark.
        var map = new FocusTravelMap(focusPlsInfinity: 395, focusPlsMod: 254, overSearchInfinity: 30, overSearchMod: 20, minDriveStep: 1);

        Assert.Equal(234, map.TravelMin);
        Assert.Equal(425, map.TravelMax);
        Assert.Equal(191, map.Range);
        Assert.Equal(161, map.InfinityPosition);
        Assert.Equal(0, map.ToPosition(map.TravelMin));
    }

    [Fact]
    public void MinimumDriveStep_QuantisesRequests()
    {
        var map = new FocusTravelMap(RecordedInf, RecordedMod, overSearchInfinity: 30, overSearchMod: 20, minDriveStep: 4);

        Assert.Equal(4, map.Step);
        Assert.Equal(map.TravelMin + 8, map.ToPulse(9));
        Assert.Equal(map.TravelMin + 8, map.ToPulse(10));
    }

    [Fact]
    public void ZeroMinimumDriveStep_FallsBackToSingleStep()
    {
        var map = new FocusTravelMap(RecordedInf, RecordedMod, overSearchInfinity: 30, overSearchMod: 20, minDriveStep: 0);

        Assert.Equal(1, map.Step);
        Assert.Equal(map.TravelMin + 7, map.ToPulse(7));
    }

    [Fact]
    public void NegativeOverSearch_IsTreatedAsMagnitude()
    {
        var map = new FocusTravelMap(RecordedInf, RecordedMod, overSearchInfinity: -30, overSearchMod: -20, minDriveStep: 1);

        Assert.Equal(224, map.TravelMin);
        Assert.Equal(415, map.TravelMax);
        Assert.Equal(30, map.InfinityPosition);
    }

    /// <summary>
    /// Requests are quantised to the lens' minimum drive step, so a position round trip is exact
    /// only when that step is 1. The contract is that it never drifts by more than one step.
    /// Values here are a real lens' capability block: INF=-1004, MOD=6995, over-search 333/365,
    /// minimum drive step 3 - note the negative infinity pulse, which is entirely normal.
    /// </summary>
    [Fact]
    public void RoundTripStaysWithinOneDriveStep()
    {
        var map = new FocusTravelMap(-1004, 6995, 333, 365, minDriveStep: 3);

        Assert.Equal(8697, map.Range);
        Assert.Equal(333, map.InfinityPosition);

        for (var position = 0; position <= map.Range; position++)
        {
            var roundTripped = map.ToPosition(map.ToPulse(position));
            Assert.True(Math.Abs(roundTripped - position) <= map.Step,
                $"position {position} round-tripped to {roundTripped}, more than {map.Step} away");
        }
    }

    [Fact]
    public void RoundTripIsExactWhenTheLensCanDriveSinglePulses()
    {
        var map = new FocusTravelMap(-1004, 6995, 333, 365, minDriveStep: 1);

        for (var position = 0; position <= map.Range; position += 7)
        {
            Assert.Equal(position, map.ToPosition(map.ToPulse(position)));
        }
    }

    [Fact]
    public void NegativeInfinityPulseIsHandled()
    {
        // Nothing requires the SDK's infinity pulse to be positive, and on real hardware it is not.
        var map = new FocusTravelMap(-1004, 6995, 333, 365, 3);

        Assert.Equal(-1337, map.TravelMin);
        Assert.Equal(7360, map.TravelMax);
        Assert.Equal(0, map.ToPosition(-1337));
        Assert.Equal(map.Range, map.ToPosition(7360));
    }
}

