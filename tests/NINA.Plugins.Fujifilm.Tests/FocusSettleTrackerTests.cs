using NINA.Plugins.Fujifilm.Devices;

namespace NINA.Plugins.Fujifilm.Tests;

/// <summary>
/// The sample sequences here are the positions a real GFX100S II reported while moving, measured
/// over USB: the lens settles in about 550ms but reads back 29-38 pulses below the commanded
/// position, in both directions, on a lens whose minimum drive step is 3.
/// </summary>
public sealed class FocusSettleTrackerTests
{
    private const int MinStep = 3;

    [Fact]
    public void ExactArrivalIsReportedImmediately()
    {
        var tracker = new FocusSettleTracker(target: 2665, tolerance: MinStep);

        Assert.Equal(FocusSettleResult.Arrived, tracker.Observe(2664));
    }

    [Fact]
    public void LensThatStopsShortOfTargetSettlesRatherThanTimingOut()
    {
        // Commanded 2361; the hardware stopped at 2323 and stayed there.
        var tracker = new FocusSettleTracker(target: 2361, tolerance: MinStep);

        Assert.Equal(FocusSettleResult.Moving, tracker.Observe(2500));
        Assert.Equal(FocusSettleResult.Moving, tracker.Observe(2400));
        Assert.Equal(FocusSettleResult.Moving, tracker.Observe(2323));

        // Now stationary. After the required run of unchanged samples the move is done.
        Assert.Equal(FocusSettleResult.Moving, tracker.Observe(2323));
        Assert.Equal(FocusSettleResult.Moving, tracker.Observe(2323));
        Assert.Equal(FocusSettleResult.Moving, tracker.Observe(2323));
        Assert.Equal(FocusSettleResult.Settled, tracker.Observe(2323));

        Assert.Equal(2323, tracker.LastPosition);
        Assert.Equal(-38, tracker.Residual);
    }

    [Fact]
    public void SettlesInTheOtherDirectionToo()
    {
        // Commanded 2961; the hardware stopped at 2932.
        var tracker = new FocusSettleTracker(target: 2961, tolerance: MinStep);

        tracker.Observe(2800);

        // The sample that first reaches 2932 is a move, then four unchanged samples are needed.
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(FocusSettleResult.Moving, tracker.Observe(2932));
        }

        Assert.Equal(FocusSettleResult.Settled, tracker.Observe(2932));
        Assert.Equal(-29, tracker.Residual);
    }

    [Fact]
    public void AStillMovingLensIsNeverReportedAsSettled()
    {
        var tracker = new FocusSettleTracker(target: 5000, tolerance: MinStep);

        foreach (var position in new[] { 100, 200, 300, 400, 500, 600, 700, 800 })
        {
            Assert.Equal(FocusSettleResult.Moving, tracker.Observe(position));
        }
    }

    [Fact]
    public void CreepWithinOneDriveStepCountsAsStopped()
    {
        // A lens that jitters by a single pulse is not moving in any useful sense.
        var tracker = new FocusSettleTracker(target: 1000, tolerance: MinStep);

        tracker.Observe(900);
        Assert.Equal(FocusSettleResult.Moving, tracker.Observe(901));
        Assert.Equal(FocusSettleResult.Moving, tracker.Observe(900));
        Assert.Equal(FocusSettleResult.Moving, tracker.Observe(902));
        Assert.Equal(FocusSettleResult.Settled, tracker.Observe(901));
    }

    [Fact]
    public void ResumedMovementResetsTheStableRun()
    {
        var tracker = new FocusSettleTracker(target: 1000, tolerance: MinStep);

        tracker.Observe(500);
        tracker.Observe(500);
        tracker.Observe(500);
        Assert.Equal(FocusSettleResult.Moving, tracker.Observe(600));   // moved again
        Assert.Equal(FocusSettleResult.Moving, tracker.Observe(600));
        Assert.Equal(FocusSettleResult.Moving, tracker.Observe(600));
        Assert.Equal(FocusSettleResult.Moving, tracker.Observe(600));
        Assert.Equal(FocusSettleResult.Settled, tracker.Observe(600));
    }

    [Fact]
    public void ArrivalWinsOverSettling()
    {
        var tracker = new FocusSettleTracker(target: 1000, tolerance: MinStep);

        tracker.Observe(1500);
        Assert.Equal(FocusSettleResult.Arrived, tracker.Observe(1000));
    }
}
