namespace NINA.Plugins.Fujifilm.Devices;

internal static class FujiExposureCompletion
{
    public static bool IsTerminal(bool imageReady, FujiCameraExposureState state) =>
        imageReady || state == FujiCameraExposureState.Error;

    /// <summary>
    /// True when no exposure is in flight and no image is waiting to be downloaded, so it is safe
    /// to talk to the camera for something other than the exposure: a status refresh or an
    /// aperture change. Error counts as quiescent - it is the resting state after a failed
    /// exposure, not a busy one.
    /// </summary>
    public static bool IsQuiescent(FujiCameraExposureState state) =>
        state is FujiCameraExposureState.Idle or FujiCameraExposureState.Error;
}

internal enum FujiCameraExposureState
{
    Idle,
    Exposing,
    Downloading,
    Ready,
    Error
}
