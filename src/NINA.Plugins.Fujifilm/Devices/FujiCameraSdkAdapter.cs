using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Image.Interfaces;
using NINA.Plugin.Interfaces;
using NINA.Plugins.Fujifilm.Configuration;
using NINA.Plugins.Fujifilm.Devices.LiveView;
using NINA.Plugins.Fujifilm.Diagnostics;
using NINA.Plugins.Fujifilm.Imaging;
using NINA.Plugins.Fujifilm.Interop;
using NINA.Plugins.Fujifilm.Interop.Native;
using NINA.Plugins.Fujifilm.Settings;
using NINA.Profile.Interfaces;

namespace NINA.Plugins.Fujifilm.Devices;

#nullable enable
internal sealed class FujiCameraSdkAdapter : IGenericCameraSDK, IDisposable
{
    private readonly FujiCamera _camera;
    private readonly FujifilmCameraDescriptor _descriptor;
    private readonly IFujifilmDiagnosticsService _diagnostics;
    private readonly ILibRawAdapter _libRawAdapter;
    private readonly IFujiSettingsProvider _settingsProvider;
    private readonly CameraImageBuilder _imageBuilder;
    private readonly IDisposable _cameraLifetime;
    private readonly IDisposable _libRawLifetime;

    private readonly object _sync = new();

    private CameraConfig? _config;
    private int[] _isoValues = Array.Empty<int>();
    private int _currentIso;
    private bool _connected;

    private Task<RawCaptureResult>? _captureTask;
    private CancellationTokenSource? _captureCts;
    private double _lastExposureSeconds;
    private bool _imageReady;
    private FujiCameraExposureState _cameraState = FujiCameraExposureState.Idle;

    private int _roiX;
    private int _roiY;
    private int _roiWidth;
    private int _roiHeight;
    private int _roiBin = 1;
    private bool _disposed;
    private DateTime _lastExposureStartUtc = DateTime.MinValue;
    private DateTime _lastStatusRefreshUtc = DateTime.MinValue;
    private FujiCameraCapabilities _capabilities = FujiCameraCapabilities.Empty;
    private FujiImagePackage? _lastImagePackage;
    private string? _lastExposureError;

    private readonly IProfileService _profileService;

    // Live view support
    private readonly ILiveViewService _liveViewService;
    private readonly BlockingCollection<LiveViewFrame> _liveViewFrameQueue = new(boundedCapacity: 2); // Keep small to minimize latency
    private bool _liveViewActive;
    private int _liveViewWidth;
    private int _liveViewHeight;
    private volatile LiveViewFrame? _latestFrame; // Always keep the most recent frame for lowest latency

    /// <summary>
    /// Gets whether live view is currently active.
    /// </summary>
    public bool IsLiveViewActive => _liveViewActive;

    /// <summary>
    /// Gets the current live view width, or 0 if not active.
    /// </summary>
    public int LiveViewWidth => _liveViewActive ? _liveViewWidth : 0;

    /// <summary>
    /// Gets the current live view height, or 0 if not active.
    /// </summary>
    public int LiveViewHeight => _liveViewActive ? _liveViewHeight : 0;

    public FujiCameraSdkAdapter(
        FujiCamera camera,
        FujifilmCameraDescriptor descriptor,
        IFujifilmDiagnosticsService diagnostics,
        ILibRawAdapter libRawAdapter,
        IFujiSettingsProvider settingsProvider,
        IProfileService profileService,
        ILiveViewService liveViewService,
        IDisposable cameraLifetime,
        IDisposable libRawLifetime)
    {
        _camera = camera;
        _descriptor = descriptor;
        _diagnostics = diagnostics;
        _libRawAdapter = libRawAdapter;
        _settingsProvider = settingsProvider;
        _profileService = profileService;
        _liveViewService = liveViewService;
        _imageBuilder = new CameraImageBuilder(settingsProvider, diagnostics, profileService);
        _cameraLifetime = cameraLifetime;
        _libRawLifetime = libRawLifetime;

        _liveViewService.FrameReceived += OnLiveViewFrameReceived;
    }

    private int _frameReceivedCount = 0;

    private void OnLiveViewFrameReceived(object? sender, LiveViewFrame frame)
    {
        _frameReceivedCount++;

        // Log first frame and every 100th frame
        if (_frameReceivedCount == 1 || _frameReceivedCount % 100 == 0)
        {
            _diagnostics.RecordEvent("Adapter", $"Frame received #{_frameReceivedCount}: {frame.Width}x{frame.Height}, {frame.JpegData.Length} bytes");
        }

        // Always store the latest frame for minimum latency
        _latestFrame = frame;

        // Also add to queue for GetVideoCapture, dropping oldest if full
        if (!_liveViewFrameQueue.TryAdd(frame))
        {
            // Queue is full, remove oldest and try again
            _liveViewFrameQueue.TryTake(out _);
            _liveViewFrameQueue.TryAdd(frame);
        }
    }

    public bool Connected => _connected;

    public void Connect()
    {
        if (_connected)
        {
            return;
        }

        _camera.ConnectAsync(_descriptor, CancellationToken.None).GetAwaiter().GetResult();
        _config = _camera.Configuration;
        _capabilities = _camera.GetCapabilitiesSnapshot();
        _cameraState = FujiCameraExposureState.Idle;
        _imageReady = false;
        _lastImagePackage = null;
        _isoValues = _capabilities.IsoValues.Count > 0 ? CopyIsoValues(_capabilities.IsoValues) : Array.Empty<int>();
        _currentIso = _camera.SelectClosestIso(_capabilities.DefaultIso > 0 ? _capabilities.DefaultIso : (_isoValues.Length > 0 ? _isoValues[0] : 200));
        _roiX = 0;
        _roiY = 0;
        _roiWidth = _capabilities.SensorWidth > 0 ? _capabilities.SensorWidth : (_config?.CameraXSize ?? 0);
        _roiHeight = _capabilities.SensorHeight > 0 ? _capabilities.SensorHeight : (_config?.CameraYSize ?? 0);
        _roiBin = 1;
        _connected = true;
        _diagnostics.RecordEvent("Adapter", $"Connected to {_descriptor.DisplayName}");
        _diagnostics.RecordEvent("Adapter", $"Available ISO values: [{string.Join(", ", _isoValues)}]");
        _diagnostics.RecordEvent("Adapter", $"Initial ISO set to: {_currentIso}");
        _diagnostics.RecordEvent("Adapter", $"Buffer capacity: {_capabilities.BufferShootCapacity}/{_capabilities.BufferTotalCapacity}");
        _diagnostics.RecordEvent("Adapter", $"State Mode={_capabilities.ModeCode}, AE={_capabilities.AEModeCode}, DR={_capabilities.DynamicRangeCode}, LastError={_capabilities.LastSdkErrorCode} (API {_capabilities.LastApiErrorCode})");
    }

    public void Disconnect()
    {
        if (!_connected)
        {
            return;
        }

        // Stop live view if active
        if (_liveViewActive)
        {
            StopVideoCapture();
        }

        CancelCapture();
        _camera.DisconnectAsync().GetAwaiter().GetResult();
        _connected = false;
        _cameraState = FujiCameraExposureState.Idle;
        _imageReady = false;
        _lastImagePackage = null;
        _diagnostics.RecordEvent("Adapter", $"Disconnected {_descriptor.DisplayName}");
    }

    /// <summary>
    /// Gets supported binning modes.
    /// Note: Fujifilm cameras do not support binning via the SDK.
    /// Only 1x1 (no binning) is available.
    /// </summary>
    public int[] GetBinningInfo()
    {
        // Binning is not supported by Fujifilm cameras
        return new[] { 1 };
    }

    public (int, int) GetDimensions()
    {
        // When live view is active, return the live view dimensions
        if (_liveViewActive && _liveViewWidth > 0 && _liveViewHeight > 0)
        {
            return (_liveViewWidth, _liveViewHeight);
        }
        var width = _roiWidth > 0 ? _roiWidth : (_config?.CameraXSize ?? 0);
        var height = _roiHeight > 0 ? _roiHeight : (_config?.CameraYSize ?? 0);
        return (width, height);
    }

    public int GetGain() => _currentIso;

    public int GetMaxGain() => (_isoValues.Length > 0) ? _isoValues[_isoValues.Length - 1] : 0;

    public int GetMaxOffset() => 0;

    public int GetMaxUSBLimit() => 0;

    public int GetMinGain() => (_isoValues.Length > 0) ? _isoValues[0] : 0;

    public int GetMinOffset() => 0;

    public int GetMinUSBLimit() => 0;

    public int GetOffset() => 0;

    public double GetPixelSize() => _config?.PixelSizeX ?? double.NaN;

    public int GetUSBLimit() => 0;

    public SensorType GetSensorInfo()
    {
        // For GFX cameras (Bayer), RGGB is the standard.
        // For X-Trans, NINA doesn't have an XTrans enum, so we use RGGB as a placeholder.
        // Live view also uses RGGB synthetic Bayer pattern.
        return SensorType.RGGB;
    }

    public bool SetGain(int value)
    {
        var previousIso = _currentIso;
        _currentIso = _camera.SelectClosestIso(value);
        _diagnostics.RecordEvent("Adapter", $"SetGain called: requested={value}, selected={_currentIso} (was {previousIso})");
        return true;
    }

    public bool SetOffset(int value) => false;

    public bool SetUSBLimit(int value) => false;

    public double GetMaxExposureTime() => _capabilities.MaxExposureSeconds > 0 ? _capabilities.MaxExposureSeconds : (_config?.DefaultMaxExposure ?? 600.0);

    public double GetMinExposureTime() => _capabilities.MinExposureSeconds > 0 ? _capabilities.MinExposureSeconds : (_config?.DefaultMinExposure ?? 0.001);

    public DateTime StartExposure(double exposureTime, int width, int height)
    {
        EnsureConnected();

        // Still capture and live view use the same camera-side image queue. Leaving live
        // view active can consume or block the RAF produced by the exposure.
        if (_liveViewActive)
        {
            StopVideoCapture();
        }

        lock (_sync)
        {
            if (_captureTask != null && !_captureTask.IsCompleted)
            {
                throw new InvalidOperationException("Exposure already in progress.");
            }

            _captureCts = new CancellationTokenSource();
            _lastExposureSeconds = exposureTime;
            _cameraState = FujiCameraExposureState.Exposing;
            _imageReady = false;
            _lastImagePackage = null;
            _lastExposureError = null;

            _captureTask = Task.Run(() =>
                _camera.CaptureRawAsync(exposureTime, _currentIso, _captureCts.Token),
                CancellationToken.None);
            _captureTask.ContinueWith(task =>
            {
                if (task.IsCanceled)
                {
                    _cameraState = FujiCameraExposureState.Error;
                    _imageReady = false;
                    _lastExposureError = "The exposure was cancelled.";
                    return;
                }

                if (task.IsFaulted)
                {
                    _cameraState = FujiCameraExposureState.Error;
                    _imageReady = false;
                    _lastExposureError = task.Exception?.GetBaseException().Message ?? "The exposure failed.";
                    _diagnostics.RecordEvent("Adapter", $"Exposure task faulted: {_lastExposureError}");
                    return;
                }

                _imageReady = true;
                _cameraState = FujiCameraExposureState.Ready;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        _lastExposureStartUtc = DateTime.UtcNow;
        return _lastExposureStartUtc;
    }

    public void StopExposure()
    {
        CancelCapture();
    }

    /// <summary>
    /// Sets the Region of Interest (ROI) for image capture.
    /// Note: Fujifilm cameras do not support arbitrary ROI or binning via the SDK.
    /// Only full-frame capture is supported. This method validates that the requested
    /// ROI matches the full sensor dimensions.
    /// </summary>
    public bool SetROI(int startX, int startY, int width, int height, int binning)
    {
        var fullWidth = _capabilities.SensorWidth > 0 ? _capabilities.SensorWidth : (_config?.CameraXSize ?? width);
        var fullHeight = _capabilities.SensorHeight > 0 ? _capabilities.SensorHeight : (_config?.CameraYSize ?? height);
        
        // Fujifilm cameras don't support arbitrary ROI or binning - only full frame
        if (startX != 0 || startY != 0 || width != fullWidth || height != fullHeight || binning != 1)
        {
            _diagnostics.RecordEvent("Adapter", $"ROI/Binning not supported by Fujifilm SDK. Requested: ({startX},{startY}) {width}x{height} bin{binning}, Required: (0,0) {fullWidth}x{fullHeight} bin1");
            return false;
        }

        // Store the full-frame ROI for GetROI() compatibility
        _roiX = startX;
        _roiY = startY;
        _roiWidth = width;
        _roiHeight = height;
        _roiBin = 1; // Binning is not supported
        return true;
    }

    public int GetBitDepth() => 16;

    public (int, int, int, int, int) GetROI()
    {
        // When live view is active, return the live view dimensions
        if (_liveViewActive && _liveViewWidth > 0 && _liveViewHeight > 0)
        {
            return (0, 0, _liveViewWidth, _liveViewHeight, 1);
        }
        return (_roiX, _roiY, _roiWidth, _roiHeight, _roiBin);
    }

    public bool HasTemperatureReadout() => false;

    public bool HasTemperatureControl() => false;

    public bool SetCooler(bool onOff) => false;

    public bool GetCoolerOnOff() => false;

    public bool SetTargetTemperature(double temperature) => false;

    public double GetTargetTemperature() => double.NaN;

    public double GetTemperature() => double.NaN;

    public double GetCoolerPower() => double.NaN;

    public FujiImagePackage? LastImagePackage => _lastImagePackage;

    public string? LastExposureError => _lastExposureError;

    public FujiCameraCapabilities CurrentCapabilities => _capabilities;

    public FujiCameraExposureState GetCameraState() => _cameraState;

    public double GetExposureProgress()
    {
        if (_cameraState == FujiCameraExposureState.Exposing && _lastExposureSeconds > 0)
        {
            var elapsed = (DateTime.UtcNow - _lastExposureStartUtc).TotalSeconds;
            return Math.Clamp((elapsed / _lastExposureSeconds) * 100.0, 0.0, 100.0);
        }

        return _cameraState switch
        {
            FujiCameraExposureState.Downloading => 90.0,
            FujiCameraExposureState.Ready => 100.0,
            _ => 0.0
        };
    }

    public async Task<ushort[]> GetExposure(double exposureTime, int width, int height, CancellationToken ct)
    {
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var stepStopwatch = System.Diagnostics.Stopwatch.StartNew();

        Task<RawCaptureResult>? captureTask;
        lock (_sync)
        {
            captureTask = _captureTask;
        }

        if (captureTask == null)
        {
            throw new InvalidOperationException("No exposure has been started.");
        }

        _cameraState = FujiCameraExposureState.Downloading;

        RawCaptureResult raw;
        try
        {
            raw = await captureTask.ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _captureTask = null;
                _captureCts?.Dispose();
                _captureCts = null;
            }
        }

        var usbTransferMs = stepStopwatch.ElapsedMilliseconds;
        stepStopwatch.Restart();

        Logger.Info($"[TIMING] USB transfer complete: {usbTransferMs}ms, buffer size: {raw.RawBuffer.Length} bytes");

        var libRaw = await _libRawAdapter.ProcessRawAsync(raw.RawBuffer, ct).ConfigureAwait(false);

        var libRawMs = stepStopwatch.ElapsedMilliseconds;
        stepStopwatch.Restart();

        Logger.Info($"[TIMING] LibRaw processing: {libRawMs}ms, Success={libRaw.Success}, {libRaw.Width}x{libRaw.Height}, BayerLen={libRaw.BayerData.Length}, RgbLen={libRaw.DebayeredRgb?.Length ?? 0}");

        if (!libRaw.Success)
        {
            _cameraState = FujiCameraExposureState.Error;
            _lastExposureError = $"LibRaw could not decode the RAF frame (status: {libRaw.Status})." +
                (string.IsNullOrWhiteSpace(libRaw.RafSidecarPath) ? string.Empty : $" Recovery RAF: {libRaw.RafSidecarPath}");
            throw new InvalidOperationException(_lastExposureError);
        }

        var package = _imageBuilder.Build(raw, libRaw, _capabilities, _config);

        var buildMs = stepStopwatch.ElapsedMilliseconds;
        stepStopwatch.Restart();

        Logger.Info($"[TIMING] Image build: {buildMs}ms, {package.Width}x{package.Height}");
        
        if (package.Pixels.Length > 0)
        {
            ushort min = ushort.MaxValue;
            ushort max = ushort.MinValue;
            long sum = 0;
            var sampleCount = 0;
            
            for (var i = 0; i < package.Pixels.Length; i += 100)
            {
                var val = package.Pixels[i];
                if (val < min) min = val;
                if (val > max) max = val;
                sum += val;
                sampleCount++;
            }
            
            var avg = sampleCount > 0 ? (double)sum / sampleCount : 0.0;
            _diagnostics.RecordEvent("Adapter", $"Image stats (sampled): Min={min}, Max={max}, Avg={avg:F2}");
        }

        _roiWidth = package.Width;
        _roiHeight = package.Height;
        _lastImagePackage = package;
        _imageReady = false;
        _cameraState = FujiCameraExposureState.Idle;
        
        // NINA's GenericCamera path expects a single-channel frame. LibRaw gives us
        // debayered RGB for X-Trans RAFs, so expose that as synthetic RGGB for preview
        // while keeping XTNSPAT metadata to record the real sensor pattern.
        var debayeredRgb = package.GetDebayeredRgb();
        if (debayeredRgb != null && package.ColorFilterPattern.StartsWith("XTRANS", StringComparison.OrdinalIgnoreCase))
        {
            // Try returning RGB data for color preview
            try
            {
                var syntheticBayer = SyntheticBayerConverter.FromRgb(debayeredRgb, package.Width, package.Height);

                var convertMs = stepStopwatch.ElapsedMilliseconds;
                var totalMs = totalStopwatch.ElapsedMilliseconds;

                Logger.Info($"[TIMING] RGB->Bayer conversion: {convertMs}ms");
                Logger.Info($"[TIMING] TOTAL GetExposure: {totalMs}ms (USB:{usbTransferMs} + LibRaw:{libRawMs} + Build:{buildMs} + Convert:{convertMs})");

                return syntheticBayer;
            }
            catch (Exception ex)
            {
                Logger.Error($"[TIMING] Synthetic Bayer conversion failed: {ex.Message}");
                _cameraState = FujiCameraExposureState.Error;
                _lastExposureError = $"X-Trans preview conversion failed: {ex.Message}";
                throw new InvalidOperationException(_lastExposureError, ex);
            }
        }

        // For Bayer cameras or when debayered RGB is not available, return raw bayer data
        var totalMsBayer = totalStopwatch.ElapsedMilliseconds;
        Logger.Info($"[TIMING] TOTAL GetExposure (Bayer path): {totalMsBayer}ms");

        return package.Pixels;
    }
    
    public bool IsExposureReady()
    {
        lock (_sync)
        {
            return _imageReady;
        }
    }

    public bool HasDewHeater() => false;

    public bool SetDewHeater(int power) => false;

    public bool IsDewHeaterOn() => false;

    /// <summary>
    /// Starts live view video capture.
    /// Uses the LiveViewService to stream JPEG frames from the camera.
    /// </summary>
    public void StartVideoCapture(double exposureTime, int width, int height)
    {
        EnsureConnected();

        lock (_sync)
        {
            if (_captureTask != null && !_captureTask.IsCompleted)
            {
                throw new InvalidOperationException("Live view cannot start while an exposure is in progress.");
            }
        }

        if (_liveViewActive)
        {
            _diagnostics.RecordEvent("Adapter", "Live view already active, ignoring StartVideoCapture");
            return;
        }

        var handle = _camera.SessionHandle;
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Camera session handle is not available");
        }

        // Get user settings for live view quality and size
        var settings = _settingsProvider.Settings;
        var liveViewQuality = settings.LiveViewQuality;
        var liveViewSize = settings.LiveViewSize;

        // Set initial live view dimensions based on the selected size
        // Aspect ratio is approximately 3:2 for Fuji sensors
        _liveViewWidth = liveViewSize.GetApproximateWidth();
        _liveViewHeight = (int)(_liveViewWidth / 1.5); // 3:2 aspect ratio

        // Clear any old frames from the queue and latest frame
        while (_liveViewFrameQueue.TryTake(out _)) { }
        _latestFrame = null;

        _liveViewService.StartAsync(handle, liveViewQuality, liveViewSize, CancellationToken.None).GetAwaiter().GetResult();
        _liveViewActive = _liveViewService.IsStreaming;
        _diagnostics.RecordEvent("Adapter", $"Live view started: {_liveViewWidth}x{_liveViewHeight} ({liveViewSize}/{liveViewQuality})");
    }

    /// <summary>
    /// Stops live view video capture.
    /// </summary>
    public void StopVideoCapture()
    {
        if (!_liveViewActive)
        {
            return;
        }

        var handle = _camera.SessionHandle;
        _liveViewService.StopAsync(handle).GetAwaiter().GetResult();

        // Clear the frame queue
        while (_liveViewFrameQueue.TryTake(out _)) { }

        _liveViewActive = false;
        // Reset live view dimensions so GetDimensions/GetROI returns sensor size for normal exposures
        _liveViewWidth = 0;
        _liveViewHeight = 0;
        _latestFrame = null;
        _frameReceivedCount = 0;
        _getVideoCaptureCount = 0;
        _diagnostics.RecordEvent("Adapter", "Live view stopped");
    }

    private int _getVideoCaptureCount = 0;

    /// <summary>
    /// Gets a live view frame as ushort[] image data.
    /// Uses the queue for frame delivery to ensure we get a complete frame.
    /// </summary>
    public async Task<ushort[]> GetVideoCapture(double exposureTime, int width, int height, CancellationToken ct)
    {
        _getVideoCaptureCount++;
        if (_getVideoCaptureCount == 1 || _getVideoCaptureCount % 100 == 0)
        {
            _diagnostics.RecordEvent("Adapter", $"GetVideoCapture #{_getVideoCaptureCount}: requested {width}x{height}, active={_liveViewActive}, latestFrame={_latestFrame != null}");
        }

        if (!_liveViewActive)
        {
            throw new InvalidOperationException("Live view is not active");
        }

        // Try to get a frame from the queue - this ensures we get a complete frame
        // that won't be modified during processing
        LiveViewFrame? frame = null;
        var startTime = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            // Try to take from queue with short timeout
            if (_liveViewFrameQueue.TryTake(out frame, 50))
            {
                break;
            }

            // Fall back to latest frame if queue is empty but we have a frame available
            var latestFrame = _latestFrame;
            if (latestFrame != null)
            {
                frame = latestFrame;
                break;
            }

            // Check overall timeout
            if ((DateTime.UtcNow - startTime).TotalMilliseconds > 2000)
            {
                throw new TimeoutException("Timeout waiting for live view frame");
            }

            await Task.Delay(10, ct).ConfigureAwait(false);
        }

        if (frame == null)
        {
            throw new OperationCanceledException("Live view frame retrieval was cancelled");
        }

        // Make a local copy of the JPEG data to ensure thread safety during processing
        var jpegData = frame.JpegData;

        // Convert JPEG to ushort[] image data
        return ConvertJpegToImageData(jpegData, width, height);
    }

    /// <summary>
    /// Converts JPEG data to ushort[] image data for NINA.
    /// Creates a synthetic RGGB Bayer pattern from the JPEG.
    /// Uses native JPEG dimensions for best quality.
    /// </summary>
    private ushort[] ConvertJpegToImageData(byte[] jpegData, int targetWidth, int targetHeight)
    {
        using var ms = new MemoryStream(jpegData);
        using var bitmap = new Bitmap(ms);

        int width = bitmap.Width;
        int height = bitmap.Height;

        // Log dimension info for debugging (only on first frame or size change)
        if (_liveViewWidth != width || _liveViewHeight != height)
        {
            _diagnostics.RecordEvent("Adapter", $"Live view: JPEG {width}x{height}, target {targetWidth}x{targetHeight}");
        }

        // Update live view dimensions - these will be used by GetDimensions/GetROI
        _liveViewWidth = width;
        _liveViewHeight = height;

        // Create output array
        var result = new ushort[width * height];

        // Lock bitmap for fast pixel access
        var rect = new Rectangle(0, 0, width, height);
        var bitmapData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        try
        {
            int stride = bitmapData.Stride;
            byte[] pixels = new byte[stride * height];
            System.Runtime.InteropServices.Marshal.Copy(bitmapData.Scan0, pixels, 0, pixels.Length);

            // Create synthetic RGGB Bayer pattern
            Parallel.For(0, height, y =>
            {
                int rowOffset = y * stride;
                bool isEvenRow = (y % 2 == 0);

                for (int x = 0; x < width; x++)
                {
                    int pixelOffset = rowOffset + x * 3;

                    // BGR format in bitmap
                    byte b = pixels[pixelOffset];
                    byte g = pixels[pixelOffset + 1];
                    byte r = pixels[pixelOffset + 2];

                    bool isEvenCol = (x % 2 == 0);

                    // RGGB Bayer Pattern - scale 8-bit to 16-bit
                    ushort value;
                    if (isEvenRow)
                    {
                        value = isEvenCol ? (ushort)(r * 257) : (ushort)(g * 257);
                    }
                    else
                    {
                        value = isEvenCol ? (ushort)(g * 257) : (ushort)(b * 257);
                    }

                    result[y * width + x] = value;
                }
            });

            return result;
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
    }

    public List<string> GetReadoutModes() => new() { "Default" };

    public int GetReadoutMode() => 0;

    public void SetReadoutMode(int modeIndex)
    {
        if (modeIndex != 0)
        {
            _diagnostics.RecordEvent("Adapter", "Readout mode selection is not supported; defaulting to mode 0.");
        }
    }

    public bool HasAdjustableFan() => false;

    public bool SetFanPercentage(int fanPercentage) => false;

    public int GetFanPercentage() => 0;

    /// <summary>
    /// Gets the current battery level (0-100) from the camera.
    /// Returns -1 if battery level is not available.
    /// </summary>
    public int GetBatteryLevel()
    {
        if (!_connected)
            return -1;

        lock (_sync)
        {
            if (_cameraState == FujiCameraExposureState.Idle && !_liveViewActive &&
                DateTime.UtcNow - _lastStatusRefreshUtc >= TimeSpan.FromSeconds(15))
            {
                _capabilities = _camera.RefreshCapabilitiesSnapshot();
                _lastStatusRefreshUtc = DateTime.UtcNow;
            }
        }

        var level = _capabilities.Metadata.BatteryLevel;
        return level >= 0 ? level : -1;
    }

    private void EnsureConnected()
    {
        if (!_connected)
        {
            throw new InvalidOperationException("Camera is not connected.");
        }
    }

    private void CancelCapture()
    {
        Task<RawCaptureResult>? captureTask;
        CancellationTokenSource? captureCts;
        lock (_sync)
        {
            captureTask = _captureTask;
            captureCts = _captureCts;
            if (captureTask == null)
            {
                return;
            }
        }

        captureCts?.Cancel();
        try
        {
            _camera.StopExposureAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Adapter", $"StopExposure failed during cancel: {ex.Message}");
        }

        try
        {
            captureTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Adapter", $"Exposure ended with an error during cancel: {ex.GetBaseException().Message}");
        }

        lock (_sync)
        {
            if (ReferenceEquals(_captureTask, captureTask))
            {
                _captureTask = null;
                _captureCts?.Dispose();
                _captureCts = null;
            }
            _cameraState = FujiCameraExposureState.Idle;
            _imageReady = false;
            _lastImagePackage = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_connected)
        {
            Disconnect();
        }

        _liveViewService.FrameReceived -= OnLiveViewFrameReceived;
        _liveViewFrameQueue.Dispose();

        _cameraLifetime.Dispose();
        _libRawLifetime.Dispose();
    }

    private static int[] CopyIsoValues(IReadOnlyList<int> isoValues)
    {
        var result = new int[isoValues.Count];
        for (var i = 0; i < isoValues.Count; i++)
        {
            result[i] = isoValues[i];
        }

        return result;
    }
}

internal enum FujiCameraExposureState
{
    Idle,
    Exposing,
    Downloading,
    Ready,
    Error
}
