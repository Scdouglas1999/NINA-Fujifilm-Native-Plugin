using System;
using System.Threading;
using System.Threading.Tasks;
using NINA.Plugins.Fujifilm.Interop;

namespace NINA.Plugins.Fujifilm.Devices;

internal static class FujifilmTimedRelease
{
    internal static async Task<T> CaptureAsync<T>(Action<int, string> release,
        Func<Task<T>> capture, Func<Task> stop, CancellationToken token,
        Action<string> log, Func<TimeSpan, CancellationToken, Task>? settle = null)
    {
        await TriggerAsync(release, token, log, settle).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await capture().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Only the capture worker issues native cancellation commands.
            try { await stop().ConfigureAwait(false); }
            catch (Exception ex) { log($"StopExposure failed during cancel: {ex.Message}"); }
            throw;
        }
    }

    internal static async Task TriggerAsync(Action<int, string> release, CancellationToken token,
        Action<string> log, Func<TimeSpan, CancellationToken, Task>? settle = null)
    {
        // SDK 1.34.1.0 p.126 requires half-press before full-press.
        token.ThrowIfCancellationRequested();
        release(FujifilmSdkWrapper.XSDK_RELEASE_S1ON, "Timed exposure half-press");
        try
        {
            // Empirical settling allowance, not an SDK-specified minimum.
            log("Waiting 500 ms after timed exposure half-press before full-press.");
            await (settle ?? Task.Delay)(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            try { release(FujifilmSdkWrapper.XSDK_RELEASE_N_S1OFF, "Cancel timed exposure half-press"); }
            catch (Exception ex) { log($"Could not release cancelled half-press: {ex.Message}"); }
            throw;
        }

        // A failed full-press must not be retried.
        release(FujifilmSdkWrapper.XSDK_RELEASE_S2_S1OFF, "Timed exposure full-press");
    }
}
