using NINA.Plugins.Fujifilm.Devices;
using NINA.Plugins.Fujifilm.Settings;

namespace NINA.Plugins.Fujifilm.Tests;

public sealed class FujiApiCapabilitiesTests
{
    // API codes verified against XAPIOpt.h.
    private const int SetRawOutputDepth = 0x2160;
    private const int SetRawCompression = 0x2150;
    private const int SetLongExposureNR = 0x2145;

    [Fact]
    public void Supports_IsTrueOnlyForAdvertisedCodes()
    {
        var capabilities = new FujiApiCapabilities(new[] { SetRawCompression, SetLongExposureNR });

        Assert.True(capabilities.Supports(SetRawCompression));
        Assert.True(capabilities.Supports(SetLongExposureNR));
        Assert.False(capabilities.Supports(SetRawOutputDepth));
        Assert.Equal(2, capabilities.Count);
    }

    [Fact]
    public void UnknownCapabilities_AllowEveryCallRatherThanBlockingEverything()
    {
        // A camera that did not report a usable list must not have every optional feature disabled;
        // the call itself decides instead.
        var capabilities = FujiApiCapabilities.Unknown;

        Assert.True(capabilities.IsEmpty);
        Assert.True(capabilities.Supports(SetRawOutputDepth));
        Assert.False(capabilities.Confirms(SetRawOutputDepth));
    }

    [Fact]
    public void SupportsAll_RequiresEveryCode()
    {
        var capabilities = new FujiApiCapabilities(new[] { SetRawCompression });

        Assert.True(capabilities.SupportsAll(SetRawCompression));
        Assert.False(capabilities.SupportsAll(SetRawCompression, SetRawOutputDepth));
    }
}

public sealed class FocusDistanceFormatterTests
{
    // The GFX100S II reported limiter ranges of 0, 2000 and 5000 with a far endpoint of 16777215.
    [Theory]
    [InlineData(2000, "2m")]
    [InlineData(5000, "5m")]
    [InlineData(1500, "1.5m")]
    [InlineData(12500, "12.5m")]
    [InlineData(16777215, "infinity")]
    [InlineData(0, "unknown")]
    public void Format_RendersMetres(int raw, string expected)
    {
        Assert.Equal(expected, FocusDistanceFormatter.Format(raw, FocusDistanceUnit.Meters));
    }

    [Fact]
    public void Format_UsesFeetSuffixWhenAsked()
    {
        Assert.Equal("5ft", FocusDistanceFormatter.Format(5000, FocusDistanceUnit.Feet));
    }

    [Fact]
    public void Format_TreatsTheSaturatedInfinityValueAsInfinity()
    {
        // 0xFFFFFF is what the hardware returns for the far end of an "x to infinity" limiter.
        Assert.Equal("infinity", FocusDistanceFormatter.Format(0xFFFFFF, FocusDistanceUnit.Meters));
    }
}

public sealed class FocusLimiterStateTests
{
    // Positions are normalised 0-1024 with 0 = closest focus and 1024 = infinity.
    private const int Infinity = FocusLimiterState.NormalizedInfinity;

    [Fact]
    public void MatchesTheReadingCapturedFromAGfx100SII()
    {
        // current=133 dofNear=122 dofFar=143 A=0 B=0 status=0
        var state = new FocusLimiterState(current: 133, dofNear: 122, dofFar: 143, posA: 0, posB: 0, status: 0);

        Assert.False(state.IsRangeValid);
        Assert.Equal(13, state.PercentTowardInfinity, 0);
        Assert.False(state.ExcludesInfinity);
        Assert.Contains("13% toward infinity", state.Describe());
        Assert.Contains("not reported", state.Describe());
    }

    [Fact]
    public void FullRangeLimiterIsReportedAsUnrestricted()
    {
        var state = new FocusLimiterState(current: 512, dofNear: 500, dofFar: 520, posA: 0, posB: Infinity, status: 1);

        Assert.True(state.IsRangeValid);
        Assert.True(state.IsFullRange);
        Assert.False(state.ExcludesInfinity);
        Assert.Contains("covers the full range", state.Describe());
    }

    [Fact]
    public void LimiterThatStopsShortOfInfinityIsFlagged()
    {
        // The failure that silently breaks astrophotography: AF may not search where the stars are.
        var state = new FocusLimiterState(current: 300, dofNear: 290, dofFar: 310, posA: 0, posB: 600, status: 1);

        Assert.True(state.ExcludesInfinity);
        Assert.False(state.IsFullRange);
        Assert.Contains("excludes infinity", state.Describe());
    }

    [Fact]
    public void LimiterReachingInfinityIsNotFlagged()
    {
        // A "5m to infinity" limiter restricts the near end but still reaches the stars.
        var state = new FocusLimiterState(current: 900, dofNear: 880, dofFar: 920, posA: 400, posB: Infinity, status: 1);

        Assert.False(state.ExcludesInfinity);
        Assert.False(state.IsFullRange);
        Assert.Contains("restricts AF", state.Describe());
        Assert.DoesNotContain("excludes infinity", state.Describe());
    }

    [Fact]
    public void EndpointsAreOrderedRegardlessOfHowTheyAreReported()
    {
        var forward = new FocusLimiterState(0, 0, 0, posA: 200, posB: 800, status: 1);
        var reversed = new FocusLimiterState(0, 0, 0, posA: 800, posB: 200, status: 1);

        Assert.Equal(forward.RangeNear, reversed.RangeNear);
        Assert.Equal(forward.RangeFar, reversed.RangeFar);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(512, 50)]
    [InlineData(1024, 100)]
    [InlineData(2048, 100)]   // clamped
    [InlineData(-50, 0)]      // clamped
    public void PercentTowardInfinityIsClampedToTheNormalisedRange(int current, double expected)
    {
        var state = new FocusLimiterState(current, 0, 0, 0, 0, 0);
        Assert.Equal(expected, state.PercentTowardInfinity, 0);
    }
}
