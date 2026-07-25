using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NINA.Plugins.Fujifilm.Diagnostics;
using NINA.Plugins.Fujifilm.Interop;

namespace NINA.Plugins.Fujifilm.Devices.LiveView;

/// <summary>
/// Implements live view streaming for Fujifilm cameras.
/// </summary>
[Export(typeof(ILiveViewService))]
[PartCreationPolicy(CreationPolicy.NonShared)]
public sealed class LiveViewService : ILiveViewService, IDisposable
{
    private readonly IFujifilmDiagnosticsService _diagnostics;

    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;
    private readonly Stopwatch _fpsStopwatch = new();
    private long _frameCount;
    private double _currentFps;
    private bool _disposed;
    private IntPtr _activeHandle;

    /// <inheritdoc/>
    public event EventHandler<LiveViewFrame>? FrameReceived;

    /// <inheritdoc/>
    public bool IsStreaming => _streamTask != null && !_streamTask.IsCompleted;

    /// <inheritdoc/>
    public double CurrentFps => _currentFps;

    /// <inheritdoc/>
    public long FrameCount => _frameCount;

    [ImportingConstructor]
    public LiveViewService(IFujifilmDiagnosticsService diagnostics)
    {
        _diagnostics = diagnostics;
    }

    /// <inheritdoc/>
    public async Task StartAsync(IntPtr handle, LiveViewQuality quality, LiveViewSize size, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (handle == IntPtr.Zero)
        {
            throw new ArgumentException("Invalid camera handle", nameof(handle));
        }

        if (_activeHandle != IntPtr.Zero)
        {
            _diagnostics.RecordEvent("LiveView", "Live view already streaming, stopping first...");
            await StopAsync(_activeHandle).ConfigureAwait(false);
        }

        _diagnostics.RecordEvent("LiveView", $"Starting live view: Quality={quality.GetDisplayName()}, Size={size.GetDisplayName()}");

        var cameraLiveViewStarted = false;
        try
        {
            // Configure quality before starting
            var qualityResult = FujifilmSdkWrapper.XSDK_SetProp(
                handle,
                FujifilmSdkWrapper.API_CODE_SetLiveViewImageQuality,
                FujifilmSdkWrapper.API_PARAM_LiveView,
                (int)quality);

            if (qualityResult != FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                var error = FujifilmSdkWrapper.GetLastError(handle);
                _diagnostics.RecordEvent("LiveView", $"Could not set live view quality {quality} (result={qualityResult}, ErrCode=0x{error.ErrorCode:X})");

                // Not every body accepts every value the headers define: a GFX100S II rejects
                // Normal outright. Rather than leaving the camera on whatever quality it happened to
                // be using - which is what produced unpredictable preview quality - fall back to
                // Fine, which the SDK reference documents for every supported model.
                if (quality != LiveViewQuality.Fine)
                {
                    var fallback = FujifilmSdkWrapper.XSDK_SetProp(
                        handle,
                        FujifilmSdkWrapper.API_CODE_SetLiveViewImageQuality,
                        FujifilmSdkWrapper.API_PARAM_LiveView,
                        (int)LiveViewQuality.Fine);

                    _diagnostics.RecordEvent("LiveView", fallback == FujifilmSdkWrapper.XSDK_COMPLETE
                        ? $"This camera does not accept live view quality {quality}; using Fine instead."
                        : $"This camera accepted neither {quality} nor Fine for live view quality (result={fallback}).");
                }
            }

            // Configure size
            var sizeResult = FujifilmSdkWrapper.XSDK_SetProp(
                handle,
                FujifilmSdkWrapper.API_CODE_SetLiveViewImageSize,
                FujifilmSdkWrapper.API_PARAM_LiveView,
                (int)size);

            if (sizeResult != FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                var error = FujifilmSdkWrapper.GetLastError(handle);
                _diagnostics.RecordEvent("LiveView", $"Warning: Failed to set size (result={sizeResult}, ErrCode=0x{error.ErrorCode:X})");
            }

            // Start live view
            var startResult = FujifilmSdkWrapper.XSDK_SetProp(
                handle,
                FujifilmSdkWrapper.API_CODE_StartLiveView,
                0,
                0);

            if (startResult != FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                var error = FujifilmSdkWrapper.GetLastError(handle);
                throw new InvalidOperationException($"Failed to start live view (result={startResult}, ErrCode=0x{error.ErrorCode:X})");
            }
            cameraLiveViewStarted = true;

            // Initialize streaming state
            _frameCount = 0;
            _currentFps = 0;
            _fpsStopwatch.Restart();
            _activeHandle = handle;
            _streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            _streamTask = StreamFramesAsync(handle, _streamCts.Token);

            _diagnostics.RecordEvent("LiveView", "Live view started successfully");
        }
        catch (Exception ex)
        {
            if (cameraLiveViewStarted)
            {
                var stopResult = FujifilmSdkWrapper.XSDK_SetProp(
                    handle,
                    FujifilmSdkWrapper.API_CODE_StopLiveView,
                    0,
                    0);
                _diagnostics.RecordEvent("LiveView", $"Stopped camera live view after failed stream startup (result={stopResult}).");
            }

            _diagnostics.RecordEvent("LiveView", $"Failed to start live view: {ex.Message}");
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync(IntPtr handle)
    {
        if (!IsStreaming && _activeHandle == IntPtr.Zero)
        {
            return;
        }

        if (handle == IntPtr.Zero)
        {
            handle = _activeHandle;
        }

        _diagnostics.RecordEvent("LiveView", "Stopping live view...");

        try
        {
            // Cancel the streaming task
            _streamCts?.Cancel();

            // Wait for the streaming task to complete
            if (_streamTask != null)
            {
                try
                {
                    await _streamTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancelling
                }
                catch (Exception ex)
                {
                    // A failed reader must not prevent the camera-side live-view mode
                    // from being stopped below.
                    _diagnostics.RecordEvent("LiveView", $"Frame stream ended with an error: {ex.Message}");
                }
            }

            // Stop live view on the camera
            if (handle != IntPtr.Zero)
            {
                var stopResult = FujifilmSdkWrapper.XSDK_SetProp(
                    handle,
                    FujifilmSdkWrapper.API_CODE_StopLiveView,
                    0,
                    0);

                if (stopResult != FujifilmSdkWrapper.XSDK_COMPLETE)
                {
                    var error = FujifilmSdkWrapper.GetLastError(handle);
                    _diagnostics.RecordEvent("LiveView", $"Warning: StopLiveView returned {stopResult}, ErrCode=0x{error.ErrorCode:X}");
                }

            }

            _diagnostics.RecordEvent("LiveView", $"Live view stopped. Total frames: {_frameCount}, Avg FPS: {_currentFps:F1}");
        }
        finally
        {
            _streamCts?.Dispose();
            _streamCts = null;
            _streamTask = null;
            _activeHandle = IntPtr.Zero;
            _fpsStopwatch.Stop();
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<double> GetAvailableZoomLevels(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return Array.Empty<double>();
        }

        return LiveViewZoomLevels
            .DescribeAvailable(QueryAdvertisedZoomCodes(handle))
            .Select(entry => entry.Magnification)
            .ToArray();
    }

    /// <inheritdoc/>
    public double? SetZoom(IntPtr handle, double magnification)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        // SetThroughImageZoom takes an SDK_THROUGH_ZOOM_* code, not a zoom factor, and the codes are
        // not ordered by magnification. Ask the camera which it offers and pick the closest, so a
        // body with three zoom steps and one with sixteen are both handled without a model table.
        var advertised = QueryAdvertisedZoomCodes(handle);
        var code = LiveViewZoomLevels.SelectCodeFor(advertised, magnification);
        if (code == null)
        {
            _diagnostics.RecordEvent("LiveView", "This camera did not advertise any live view zoom levels.");
            return null;
        }

        var result = FujifilmSdkWrapper.XSDK_SetProp(
            handle,
            FujifilmSdkWrapper.API_CODE_SetThroughImageZoom,
            FujifilmSdkWrapper.API_PARAM_LiveView,
            code.Value);

        if (result == FujifilmSdkWrapper.XSDK_COMPLETE)
        {
            var applied = LiveViewZoomLevels.GetMagnification(code.Value);
            _diagnostics.RecordEvent("LiveView",
                $"Live view zoom set to {LiveViewZoomLevels.Describe(code.Value)} (asked for x{magnification:0.#}).");
            return applied;
        }

        var error = FujifilmSdkWrapper.GetLastError(handle);
        _diagnostics.RecordEvent("LiveView",
            $"Camera refused live view zoom {LiveViewZoomLevels.Describe(code.Value)} (result={result}, ErrCode=0x{error.ErrorCode:X}).");
        return null;
    }

    /// <summary>Reads the zoom codes this body advertises via CapThroughImageZoom.</summary>
    private IReadOnlyList<int> QueryAdvertisedZoomCodes(IntPtr handle)
    {
        if (FujifilmSdkWrapper.XSDK_CapProp(handle, FujifilmSdkWrapper.API_CODE_CapThroughImageZoom,
                FujifilmSdkWrapper.API_PARAM_CapThroughImageZoom, out var count, IntPtr.Zero) != FujifilmSdkWrapper.XSDK_COMPLETE
            || count <= 0)
        {
            return Array.Empty<int>();
        }

        var buffer = Marshal.AllocHGlobal(count * sizeof(int));
        try
        {
            if (FujifilmSdkWrapper.XSDK_CapProp(handle, FujifilmSdkWrapper.API_CODE_CapThroughImageZoom,
                    FujifilmSdkWrapper.API_PARAM_CapThroughImageZoom, out count, buffer) != FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                return Array.Empty<int>();
            }

            var codes = new int[count];
            for (var i = 0; i < count; i++)
            {
                codes[i] = Marshal.ReadInt32(buffer, i * sizeof(int));
            }

            return codes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private async Task StreamFramesAsync(IntPtr handle, CancellationToken cancellationToken)
    {
        var lastFpsUpdate = DateTime.UtcNow;
        long framesSinceLastUpdate = 0;

        _diagnostics.RecordEvent("LiveView", "Frame streaming loop started");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Read image info
                var infoResult = FujifilmSdkWrapper.XSDK_ReadImageInfo(handle, out var imageInfo);

                if (infoResult == FujifilmSdkWrapper.XSDK_COMPLETE && imageInfo.lDataSize > 0)
                {
                    // Check if this is a live view frame (format = LIVE)
                    if ((imageInfo.lFormat & 0xFF) == FujifilmSdkWrapper.XSDK_IMAGEFORMAT_LIVE)
                    {
                        // Allocate buffer and read the image
                        var buffer = new byte[imageInfo.lDataSize];
                        var gcHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                        try
                        {
                            var readResult = FujifilmSdkWrapper.XSDK_ReadImage(
                                handle,
                                gcHandle.AddrOfPinnedObject(),
                                (uint)buffer.Length);

                            if (readResult == FujifilmSdkWrapper.XSDK_COMPLETE)
                            {
                                // Create frame and raise event
                                var frame = new LiveViewFrame(
                                    buffer,
                                    imageInfo.lImagePixWidth,
                                    imageInfo.lImagePixHeight,
                                    imageInfo.lFormat,
                                    DateTime.UtcNow.Ticks);

                                _frameCount++;
                                framesSinceLastUpdate++;

                                if (_frameCount <= 5)
                                {
                                    _diagnostics.RecordEvent("LiveView", $"Frame #{_frameCount}: {imageInfo.lImagePixWidth}x{imageInfo.lImagePixHeight}, {buffer.Length} bytes");
                                }

                                FrameReceived?.Invoke(this, frame);
                            }
                        }
                        finally
                        {
                            gcHandle.Free();
                        }

                        var deleteResult = FujifilmSdkWrapper.XSDK_DeleteImage(handle);
                        if (deleteResult != FujifilmSdkWrapper.XSDK_COMPLETE)
                        {
                            _diagnostics.RecordEvent("LiveView", $"Delete live-view frame returned {deleteResult}");
                        }
                    }
                }

                // Update FPS calculation every second
                var now = DateTime.UtcNow;
                if ((now - lastFpsUpdate).TotalSeconds >= 1.0)
                {
                    _currentFps = framesSinceLastUpdate / (now - lastFpsUpdate).TotalSeconds;
                    framesSinceLastUpdate = 0;
                    lastFpsUpdate = now;
                }

                // Small delay to prevent tight polling (~60fps max)
                await Task.Delay(16, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _diagnostics.RecordEvent("LiveView", $"Frame read error: {ex.Message}");
                // Small delay before retry
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        _diagnostics.RecordEvent("LiveView", "Frame streaming loop ended");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_activeHandle != IntPtr.Zero)
        {
            StopAsync(_activeHandle).GetAwaiter().GetResult();
        }

        _disposed = true;
    }
}
