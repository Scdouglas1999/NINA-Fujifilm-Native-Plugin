using NINA.Plugins.Fujifilm.Devices;
using NINA.Plugins.Fujifilm.Interop;

namespace NINA.Plugins.Fujifilm.Tests;

/// <summary>
/// The battery layout is discovered by asking the camera rather than by looking the model up in a
/// table, so a body the plugin has never seen still reports its battery. The previous table-based
/// version silently disabled battery reporting on any model missing from it.
///
/// These tests deliberately name no camera model.
/// </summary>
public sealed class FujifilmBatteryProtocolTests
{
    [Fact]
    public void ChargingStatePreservesSdkMeaning()
    {
        Assert.True(FujifilmBatteryProtocol.GetChargingState(FujifilmSdkWrapper.SDK_POWERCAPACITY_DC_CHARGE));
        Assert.False(FujifilmBatteryProtocol.GetChargingState(FujifilmSdkWrapper.SDK_POWERCAPACITY_DC));
        Assert.Null(FujifilmBatteryProtocol.GetChargingState(0x7FFFFFFF));
    }

    [Fact]
    public void LargestLayoutIsTriedFirst()
    {
        var attempted = new List<int>();

        FujifilmBatteryProtocol.Probe(candidate =>
        {
            attempted.Add(candidate);
            return false;
        });

        Assert.Equal(new[] { 8, 6 }, attempted);
    }

    [Fact]
    public void ACameraAcceptingTheLargestLayoutStopsThere()
    {
        var attempted = new List<int>();

        var result = FujifilmBatteryProtocol.Probe(candidate =>
        {
            attempted.Add(candidate);
            return candidate == FujifilmBatteryProtocol.NewModelParameterCount;
        });

        Assert.Equal(FujifilmBatteryProtocol.NewModelParameterCount, result);
        Assert.Equal(new[] { 8 }, attempted);
    }

    [Fact]
    public void ACameraNeedingTheOlderLayoutFallsThroughToIt()
    {
        var result = FujifilmBatteryProtocol.Probe(
            candidate => candidate == FujifilmBatteryProtocol.OldModelParameterCount);

        Assert.Equal(FujifilmBatteryProtocol.OldModelParameterCount, result);
    }

    [Fact]
    public void ACameraImplementingNeitherReportsUnavailableRatherThanGuessing()
    {
        Assert.Null(FujifilmBatteryProtocol.Probe(_ => false));
    }

    [Fact]
    public void CandidatesAreOrderedLargestFirstSoStorageIsNeverUndersupplied()
    {
        // The hazard with the variadic call is supplying too few output pointers. Callers allocate
        // storage for the largest candidate, so the largest must also be tried first.
        var candidates = FujifilmBatteryProtocol.CandidateParameterCounts;

        Assert.NotEmpty(candidates);
        Assert.Equal(candidates.Max(), candidates[0]);
        Assert.Equal(candidates.OrderByDescending(value => value), candidates);
    }
}
