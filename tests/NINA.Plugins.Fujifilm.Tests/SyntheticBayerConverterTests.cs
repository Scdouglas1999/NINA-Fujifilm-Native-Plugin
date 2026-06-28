using NINA.Plugins.Fujifilm.Imaging;

namespace NINA.Plugins.Fujifilm.Tests;

public sealed class SyntheticBayerConverterTests
{
    [Fact]
    public void FromRgb_ProducesRggbSamples()
    {
        ushort[] rgb =
        {
            10, 11, 12, 20, 21, 22,
            30, 31, 32, 40, 41, 42
        };

        var result = SyntheticBayerConverter.FromRgb(rgb, 2, 2);

        Assert.Equal(new ushort[] { 10, 21, 31, 42 }, result);
    }

    [Fact]
    public void FromRgb_RejectsMismatchedBufferLength()
    {
        Assert.Throws<ArgumentException>(() => SyntheticBayerConverter.FromRgb(new ushort[5], 2, 2));
    }
}
