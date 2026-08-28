namespace NINA.Plugins.Fujifilm.Devices;

internal static class FujiExposureCompletion
{
    public static bool IsTerminal(bool imageReady, FujiCameraExposureState state) =>
        imageReady || state == FujiCameraExposureState.Error;
}

internal enum FujiCameraExposureState
{
    Idle,
    Exposing,
    Downloading,
    Ready,
    Error
}
