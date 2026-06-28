using System;

namespace NINA.Plugins.Fujifilm.Imaging;

internal static class BayerPaddingCropper
{
    public static (ushort[] Data, int Width, int Height, bool Cropped) Crop(
        ushort[]? bayerData,
        int sourceWidth,
        int sourceHeight,
        int activeLeft,
        int activeTop,
        int activeWidth,
        int activeHeight)
    {
        var data = bayerData ?? Array.Empty<ushort>();
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return (data, sourceWidth, sourceHeight, false);
        }

        var expectedLength = checked(sourceWidth * sourceHeight);
        if (data.Length != expectedLength)
        {
            return (data, sourceWidth, sourceHeight, false);
        }

        if (activeWidth <= 0 || activeHeight <= 0 ||
            (activeWidth == sourceWidth && activeHeight == sourceHeight))
        {
            return (data, sourceWidth, sourceHeight, false);
        }

        if (activeLeft < 0 || activeTop < 0 ||
            activeLeft >= sourceWidth || activeTop >= sourceHeight ||
            activeLeft + activeWidth > sourceWidth ||
            activeTop + activeHeight > sourceHeight)
        {
            return (data, sourceWidth, sourceHeight, false);
        }

        var croppedData = new ushort[activeWidth * activeHeight];
        for (var y = 0; y < activeHeight; y++)
        {
            var sourceOffset = (activeTop + y) * sourceWidth + activeLeft;
            var destinationOffset = y * activeWidth;
            Array.Copy(data, sourceOffset, croppedData, destinationOffset, activeWidth);
        }

        return (croppedData, activeWidth, activeHeight, true);
    }
}
