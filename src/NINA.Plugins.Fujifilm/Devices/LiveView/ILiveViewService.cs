using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.Fujifilm.Devices.LiveView;

/// <summary>
/// Interface for live view streaming operations on Fujifilm cameras.
/// </summary>
public interface ILiveViewService
{
    /// <summary>
    /// Event raised when a new frame is received from the camera.
    /// </summary>
    event EventHandler<LiveViewFrame>? FrameReceived;

    /// <summary>
    /// Returns true if live view streaming is currently active.
    /// </summary>
    bool IsStreaming { get; }

    /// <summary>
    /// Gets the current frame rate in frames per second.
    /// </summary>
    double CurrentFps { get; }

    /// <summary>
    /// Gets the total number of frames received since streaming started.
    /// </summary>
    long FrameCount { get; }

    /// <summary>
    /// Starts live view streaming.
    /// </summary>
    /// <param name="handle">The camera handle.</param>
    /// <param name="quality">The image quality setting.</param>
    /// <param name="size">The image size setting.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task that completes when streaming has started.</returns>
    Task StartAsync(IntPtr handle, LiveViewQuality quality, LiveViewSize size, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops live view streaming.
    /// </summary>
    /// <param name="handle">The camera handle.</param>
    /// <returns>Task that completes when streaming has stopped.</returns>
    Task StopAsync(IntPtr handle);

    /// <summary>
    /// Sets the digital zoom level during live view.
    /// </summary>
    /// <param name="handle">The camera handle.</param>
    /// <param name="zoomLevel">Zoom level from 1 to 24 (1x to 24x magnification).</param>
    /// <summary>
    /// Sets the live view magnification, choosing the closest level this camera offers.
    /// </summary>
    /// <returns>The magnification actually applied, or null if the camera has none or refused.</returns>
    double? SetZoom(IntPtr handle, double magnification);

    /// <summary>The live view magnifications this camera offers, ascending.</summary>
    IReadOnlyList<double> GetAvailableZoomLevels(IntPtr handle);
}
