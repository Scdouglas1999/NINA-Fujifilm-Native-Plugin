using System;
using System.Threading.Tasks;

namespace NINA.Plugins.Fujifilm.Interop.Native;

public sealed class FujifilmCameraSession : IAsyncDisposable
{
    internal FujifilmCameraSession(IntPtr handle, string deviceId)
    {
        Handle = handle;
        DeviceId = deviceId;
    }

    public IntPtr Handle { get; internal set; }
    public string DeviceId { get; }

    public ValueTask DisposeAsync()
    {
        // FujifilmInterop owns and reference-counts the native handle. A camera and its
        // lens focuser can share this object, so disposing one consumer must not invalidate it.
        return ValueTask.CompletedTask;
    }
}
