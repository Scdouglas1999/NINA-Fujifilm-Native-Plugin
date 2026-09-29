using System;
using System.Threading;
using System.Threading.Tasks;
using NINA.Plugins.Fujifilm.Interop;

namespace NINA.Plugins.Fujifilm.Devices;

internal static class FujifilmTimedRelease
{
    internal static async Task TriggerAsync(Action<int, string> release, CancellationToken token,
        Action<string> log, Func<TimeSpan, CancellationToken, Task>? settle = null)
    {
        // SDK 1.34.1.0 p.126 requires half-press before full-press. The Windows
        // ReleaseButton sample uses S1ON followed by S2_S1OFF in PC priority mode.
        token.ThrowIfCancellationRequested();
        release(FujifilmSdkWrapper.XSDK_RELEASE_S1ON, "Timed exposure half-press");
        try
        {
            // X-T4 + Viltrox 27/1.2: immediate S2 failed at 1/5 s; a 500 ms S1
            // hold passed the paired isolated SDK test (2026-09-12), then NINA.
            // This is a measured workaround, not an SDK-specified minimum delay.
            log("Waiting 500 ms after timed exposure half-press before full-press.");
            await (settle ?? Task.Delay)(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            // Cancellation during preparation must not leave the half-press held.
            try { release(FujifilmSdkWrapper.XSDK_RELEASE_N_S1OFF, "Cancel timed exposure half-press"); }
            catch (Exception ex) { log($"Could not release cancelled half-press: {ex.Message}"); }
            throw;
        }

        // Do not retry a failed full-press: it may already have started an exposure.
        // Leave native-error recovery to the caller rather than issuing more commands
        // on a possibly disconnected or failed session.
        release(FujifilmSdkWrapper.XSDK_RELEASE_S2_S1OFF, "Timed exposure full-press");
    }
}
