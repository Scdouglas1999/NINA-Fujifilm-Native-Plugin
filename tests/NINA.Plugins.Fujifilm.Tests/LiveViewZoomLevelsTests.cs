using NINA.Plugins.Fujifilm.Devices.LiveView;

namespace NINA.Plugins.Fujifilm.Tests;

/// <summary>
/// SetThroughImageZoom takes an SDK_THROUGH_ZOOM_* code, not a magnification, and the codes are not
/// ordered by magnification. The previous implementation clamped its argument to 1-24 and sent it
/// raw, which selected the wrong magnification for most values and sent a value that is not a code
/// at all for anything above 0x11.
/// </summary>
public sealed class LiveViewZoomLevelsTests
{
    [Theory]
    [InlineData(0x0001, 1.0)]
    [InlineData(0x0002, 2.5)]
    [InlineData(0x0003, 6.0)]
    [InlineData(0x0004, 4.0)]   // note: 0x03 is x6 and 0x04 is x4, so codes are not ordered
    [InlineData(0x000B, 24.0)]
    [InlineData(0x0011, 12.0)]
    public void CodesMapToTheirDocumentedMagnification(int code, double expected)
        => Assert.Equal(expected, LiveViewZoomLevels.GetMagnification(code));

    [Theory]
    [InlineData(0x0000)]
    [InlineData(0x0012)]
    [InlineData(24)]     // what the old code would have sent when asked for x24
    public void ValuesOutsideTheCodeSpaceAreRejected(int code)
    {
        Assert.False(LiveViewZoomLevels.IsKnownCode(code));
        Assert.Null(LiveViewZoomLevels.GetMagnification(code));
    }

    [Fact]
    public void TheOldClampWouldHaveSelectedTheWrongMagnification()
    {
        // Asking for x4 used to send the value 4, which is the code for... x4 by luck.
        // Asking for x6 sent 6, which is the code for x16.
        Assert.Equal(16.0, LiveViewZoomLevels.GetMagnification(6));
        Assert.Equal(2.5, LiveViewZoomLevels.GetMagnification(2));
    }

    [Fact]
    public void ClosestAvailableMagnificationIsChosen()
    {
        // A camera advertising six levels: x1, x2.5, x4, x8, x16, x24.
        var advertised = new[] { 0x01, 0x02, 0x04, 0x05, 0x06, 0x0B };

        Assert.Equal(0x01, LiveViewZoomLevels.SelectCodeFor(advertised, 1.0));
        Assert.Equal(0x02, LiveViewZoomLevels.SelectCodeFor(advertised, 2.4));
        Assert.Equal(0x04, LiveViewZoomLevels.SelectCodeFor(advertised, 4.2));
        Assert.Equal(0x06, LiveViewZoomLevels.SelectCodeFor(advertised, 15.0));
        Assert.Equal(0x0B, LiveViewZoomLevels.SelectCodeFor(advertised, 100.0));   // beyond the top
        Assert.Equal(0x01, LiveViewZoomLevels.SelectCodeFor(advertised, 0.1));     // below the bottom
    }

    [Fact]
    public void ACameraWithFewerLevelsIsHandledWithoutAModelTable()
    {
        // A body offering only x1, x2.5 and x6.
        var advertised = new[] { 0x01, 0x02, 0x03 };

        Assert.Equal(0x03, LiveViewZoomLevels.SelectCodeFor(advertised, 16.0));
        Assert.Equal(0x02, LiveViewZoomLevels.SelectCodeFor(advertised, 3.0));
    }

    [Fact]
    public void UnknownCodesFromACameraAreIgnored()
    {
        var advertised = new[] { 0x01, 0x99, 0x02 };

        Assert.Equal(new[] { 1.0, 2.5 }, LiveViewZoomLevels.DescribeAvailable(advertised).Select(e => e.Magnification));
    }

    [Fact]
    public void ACameraOfferingNothingUsableReturnsNull()
    {
        Assert.Null(LiveViewZoomLevels.SelectCodeFor(Array.Empty<int>(), 4.0));
        Assert.Null(LiveViewZoomLevels.SelectCodeFor(new[] { 0x77 }, 4.0));
    }

    [Fact]
    public void AvailableLevelsAreOrderedByMagnificationNotByCode()
    {
        var advertised = new[] { 0x03, 0x04, 0x01 };   // x6, x4, x1

        Assert.Equal(new[] { 1.0, 4.0, 6.0 }, LiveViewZoomLevels.DescribeAvailable(advertised).Select(e => e.Magnification));
    }

    [Fact]
    public void DescribeRendersAMagnification()
    {
        Assert.Equal("x2.5", LiveViewZoomLevels.Describe(0x02));
        Assert.Equal("x16", LiveViewZoomLevels.Describe(0x06));
        Assert.Contains("unknown", LiveViewZoomLevels.Describe(0x99));
    }
}
