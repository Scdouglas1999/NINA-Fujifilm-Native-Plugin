using System;
using System.Threading.Tasks;

namespace NINA.Plugins.Fujifilm.Imaging;

internal static class SyntheticBayerConverter
{
    public static ushort[] FromRgb(ushort[] rgbData, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        }

        var requiredLength = checked(width * height * 3);
        if (rgbData == null || rgbData.Length != requiredLength)
        {
            throw new ArgumentException($"RGB data length must be exactly {requiredLength} for a {width}x{height} image.", nameof(rgbData));
        }

        var bayer = new ushort[width * height];
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var rgbIndex = (y * width + x) * 3;
                var isEvenRow = (y & 1) == 0;
                var isEvenColumn = (x & 1) == 0;
                bayer[y * width + x] = isEvenRow
                    ? isEvenColumn ? rgbData[rgbIndex] : rgbData[rgbIndex + 1]
                    : isEvenColumn ? rgbData[rgbIndex + 1] : rgbData[rgbIndex + 2];
            }
        });

        return bayer;
    }
}
