using NINA.Plugins.Fujifilm.Imaging;

namespace NINA.Plugins.Fujifilm.Tests;

public sealed class BayerPaddingCropperTests
{
    [Fact]
    public void Crop_ReturnsActiveAreaWhenBoundsAreValid()
    {
        ushort[] source =
        {
            00, 01, 02, 03,
            10, 11, 12, 13,
            20, 21, 22, 23
        };

        var result = BayerPaddingCropper.Crop(source, 4, 3, 1, 1, 2, 2);

        Assert.True(result.Cropped);
        Assert.Equal(2, result.Width);
        Assert.Equal(2, result.Height);
        Assert.Equal(new ushort[] { 11, 12, 21, 22 }, result.Data);
    }

    [Fact]
    public void Crop_LeavesDataUnchangedWhenBufferLengthDoesNotMatchDimensions()
    {
        var source = new ushort[] { 1, 2, 3 };

        var result = BayerPaddingCropper.Crop(source, 4, 3, 1, 1, 2, 2);

        Assert.False(result.Cropped);
        Assert.Same(source, result.Data);
        Assert.Equal(4, result.Width);
        Assert.Equal(3, result.Height);
    }

    [Fact]
    public void Crop_LeavesDataUnchangedWhenActiveAreaIsOutOfBounds()
    {
        var source = Enumerable.Range(0, 12).Select(value => (ushort)value).ToArray();

        var result = BayerPaddingCropper.Crop(source, 4, 3, 3, 1, 2, 2);

        Assert.False(result.Cropped);
        Assert.Same(source, result.Data);
        Assert.Equal(4, result.Width);
        Assert.Equal(3, result.Height);
    }
}
