using NINA.Plugins.Fujifilm.Devices;

namespace NINA.Plugins.Fujifilm.Tests;

public sealed class FujiExposureCompletionTests
{
    [Fact]
    public void IsTerminal_WithoutImageOrError_ReturnsFalse()
    {
        var nonTerminalStates = new[]
        {
            FujiCameraExposureState.Idle,
            FujiCameraExposureState.Exposing,
            FujiCameraExposureState.Downloading,
            FujiCameraExposureState.Ready
        };

        foreach (var state in nonTerminalStates)
        {
            Assert.False(FujiExposureCompletion.IsTerminal(imageReady: false, state));
        }
    }

    [Fact]
    public void IsTerminal_WithReadyImage_ReturnsTrue()
    {
        Assert.True(FujiExposureCompletion.IsTerminal(
            imageReady: true,
            FujiCameraExposureState.Ready));
    }

    [Fact]
    public void IsTerminal_WithCaptureError_ReturnsTrue()
    {
        Assert.True(FujiExposureCompletion.IsTerminal(
            imageReady: false,
            FujiCameraExposureState.Error));
    }
}
