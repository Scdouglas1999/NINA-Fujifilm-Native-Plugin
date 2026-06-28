using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NINA.Plugins.Fujifilm.Configuration;
using NINA.Plugins.Fujifilm.Configuration.Loading;
using NINA.Plugins.Fujifilm.Diagnostics;
using NINA.Plugins.Fujifilm.Interop;
using NINA.Plugins.Fujifilm.Interop.Native;
using NINA.Plugins.Fujifilm.Settings;

namespace NINA.Plugins.Fujifilm.Devices;

[Export(typeof(FujiCamera))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class FujiCamera : IAsyncDisposable, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    private void RaisePropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private readonly IFujifilmInterop _interop;
    private readonly ICameraModelCatalog _catalog;
    private readonly IFujiSettingsProvider _settingsProvider;
    private readonly IFujifilmDiagnosticsService _diagnostics;

    private FujifilmCameraSession? _session;
    private CameraConfig? _config;
    private IReadOnlyList<int> _supportedSensitivities = Array.Empty<int>();
    private IReadOnlyDictionary<int, double> _shutterCodeToDuration = new Dictionary<int, double>();
    private IReadOnlyList<int> _supportedShutterCodes = Array.Empty<int>(); // Store originally queried codes for validation
    private bool _bulbCapable;
    private const double DefaultMinExposureSeconds = 0.001;
    private int _bufferShootCapacity;
    private int _bufferTotalCapacity;
    private int _lastModeCode;
    private int _lastAEModeCode;
    private int _lastDynamicRangeCode;
    private int _lastApiErrorCode;
    private int _lastSdkErrorCode;
    private int _bulbReleaseHeld;
    private string? _connectedDeviceId;
    private FujiCameraMetadata _metadata = FujiCameraMetadata.Empty;

    public bool SupportsBulb => _bulbCapable;

    public FujiCameraCapabilities GetCapabilitiesSnapshot()
    {
        var isoValues = GetAvailableIsoValues();
        var sensorWidth = _config?.CameraXSize ?? 0;
        var sensorHeight = _config?.CameraYSize ?? 0;
        _diagnostics.RecordEvent("Camera", $"GetCapabilitiesSnapshot: Config={(_config != null ? "Present" : "Null")} Width={sensorWidth} Height={sensorHeight}");
        var minExposure = GetMinExposureSecondsInternal();
        var maxExposure = GetMaxExposureSecondsInternal();
        var timedMaxExposure = FujifilmShutterSpeedCatalog.GetTimedMaximum(
            _shutterCodeToDuration,
            _config?.DefaultMinExposure ?? DefaultMinExposureSeconds);
        var bulbMaxExposure = _bulbCapable
            ? _config?.DefaultMaxExposure ?? 3600.0
            : timedMaxExposure;
        var defaultIso = SelectClosestIsoInternal(_config?.DefaultMinSensitivity ?? (isoValues.Length > 0 ? isoValues[0] : 200));

        return new FujiCameraCapabilities(
            Array.AsReadOnly(isoValues),
            defaultIso,
            minExposure,
            maxExposure,
            _bulbCapable,
            sensorWidth,
            sensorHeight,
            _bufferShootCapacity,
            _bufferTotalCapacity,
            _lastModeCode,
            _lastAEModeCode,
            _lastDynamicRangeCode,
            _lastApiErrorCode,
            _lastSdkErrorCode,
            _metadata,
            timedMaxExposure,
            bulbMaxExposure);
    }

    public int[] GetAvailableIsoValues()
    {
        if (_supportedSensitivities.Count > 0)
        {
            return _supportedSensitivities.ToArray();
        }

        return BuildFallbackIsoArray();
    }

    public int SelectClosestIso(int iso)
    {
        return SelectClosestIsoInternal(iso);
    }

    public double GetMinExposureSeconds()
    {
        return GetMinExposureSecondsInternal();
    }

    public double GetMaxExposureSeconds()
    {
        return GetMaxExposureSecondsInternal();
    }

    public (int shoot, int total) GetBufferCapacity()
    {
        return (_bufferShootCapacity, _bufferTotalCapacity);
    }

    public FujiCameraCapabilities RefreshCapabilitiesSnapshot()
    {
        if (!IsConnected)
        {
            return GetCapabilitiesSnapshot();
        }

        RefreshBufferCapacity();
        RefreshOperatingState();
        RefreshLensMetadata();
        return GetCapabilitiesSnapshot();
    }

    private int SelectClosestIsoInternal(int iso)
    {
        var isoValues = GetAvailableIsoValues();
        if (isoValues.Length == 0)
        {
            return iso;
        }

        var closest = isoValues[0];
        var delta = Math.Abs(iso - closest);
        foreach (var candidate in isoValues)
        {
            var currentDelta = Math.Abs(iso - candidate);
            if (currentDelta < delta)
            {
                closest = candidate;
                delta = currentDelta;
            }
        }

        return closest;
    }

    private double GetMinExposureSecondsInternal()
    {
        var timed = _shutterCodeToDuration
            .Where(pair => pair.Key != FujifilmSdkWrapper.XSDK_SHUTTER_BULB && pair.Value > 0)
            .Select(pair => pair.Value)
            .ToList();

        if (timed.Count > 0)
        {
            return timed.Min();
        }

        return _config?.DefaultMinExposure ?? DefaultMinExposureSeconds;
    }

    private double GetMaxExposureSecondsInternal()
    {
        var timed = _shutterCodeToDuration
            .Where(pair => pair.Key != FujifilmSdkWrapper.XSDK_SHUTTER_BULB && pair.Value > 0)
            .Select(pair => pair.Value)
            .ToList();

        var timedMax = timed.Count > 0 ? timed.Max() : (_config?.DefaultMaxExposure ?? 600.0);
        if (_bulbCapable)
        {
            var bulbDefault = _config?.DefaultMaxExposure ?? 3600.0;
            var bulbConfigured = _shutterCodeToDuration.TryGetValue(FujifilmSdkWrapper.XSDK_SHUTTER_BULB, out var bulbValue)
                ? bulbValue
                : bulbDefault;
            return Math.Max(timedMax, bulbConfigured);
        }

        return timedMax;
    }

    private int[] BuildFallbackIsoArray()
    {
        if (_config == null)
        {
            _diagnostics.RecordEvent("Camera", "BuildFallbackIsoArray: No config available, returning empty array.");
            return Array.Empty<int>();
        }

        var minIso = _config.DefaultMinSensitivity > 0 ? _config.DefaultMinSensitivity : 160;
        var maxIso = _config.DefaultMaxSensitivity > 0 ? _config.DefaultMaxSensitivity : 12800;
        
        if (minIso >= maxIso)
        {
            _diagnostics.RecordEvent("Camera", $"BuildFallbackIsoArray: Returning single value [{minIso}]");
            return new[] { minIso };
        }

        // Generate common ISO values between min and max
        var commonIsoValues = new[] { 100, 125, 160, 200, 250, 320, 400, 500, 640, 800, 1000, 1250, 1600, 2000, 2500, 3200, 4000, 5000, 6400, 8000, 10000, 12800 };
        var isoList = new System.Collections.Generic.List<int>();
        
        foreach (var iso in commonIsoValues)
        {
            if (iso >= minIso && iso <= maxIso)
            {
                isoList.Add(iso);
            }
        }
        
        // Ensure min and max are included
        if (!isoList.Contains(minIso)) isoList.Insert(0, minIso);
        if (!isoList.Contains(maxIso)) isoList.Add(maxIso);
        
        _diagnostics.RecordEvent("Camera", $"BuildFallbackIsoArray: Returning {isoList.Count} values from {isoList[0]} to {isoList[isoList.Count - 1]}");
        return isoList.ToArray();
    }

    [ImportingConstructor]
    public FujiCamera(
        IFujifilmInterop interop,
        ICameraModelCatalog catalog,
        IFujiSettingsProvider settingsProvider,
        IFujifilmDiagnosticsService diagnostics)
    {
        _interop = interop;
        _catalog = catalog;
        _settingsProvider = settingsProvider;
        _diagnostics = diagnostics;
    }

    public bool IsConnected => _session != null && _session.Handle != IntPtr.Zero;
    internal string? ConnectedDeviceId => IsConnected ? _connectedDeviceId : null;

    /// <summary>
    /// Gets the native SDK session handle. Returns IntPtr.Zero if not connected.
    /// Used internally for SDK operations that require the camera handle (e.g., live view).
    /// </summary>
    internal IntPtr SessionHandle => _session?.Handle ?? IntPtr.Zero;

    public CameraConfig? Configuration => _config;
    public IReadOnlyList<int> SupportedIsoValues => _supportedSensitivities;
    public IReadOnlyDictionary<int, double> ShutterCodeToDuration => _shutterCodeToDuration;

    private async Task ExecuteWithRetryAsync(Func<int> sdkCall, string operationName, CancellationToken cancellationToken = default)
    {
        int retryCount = 0;
        const int maxRetries = 5;
        const int delayMs = 500;

        if (_session == null)
        {
            throw new InvalidOperationException("Camera session is not initialized.");
        }

        while (true)
        {
            int result = sdkCall();
            if (result == FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                _diagnostics.RecordEvent("Camera", $"{operationName} succeeded.");
                return;
            }

            var error = FujifilmSdkWrapper.GetLastError(_session.Handle);
            if (error.ErrorCode == FujifilmSdkWrapper.XSDK_ERRCODE_BUSY && retryCount < maxRetries)
            {
                retryCount++;
                _diagnostics.RecordEvent("Camera", $"{operationName} failed with BUSY. Retrying ({retryCount}/{maxRetries}) in {delayMs}ms...");
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new FujifilmSdkException(operationName, result, error.ApiCode, error.ErrorCode);
            }
        }
    }

    public async Task ConnectAsync(FujifilmCameraDescriptor descriptor, CancellationToken cancellationToken)
    {
        await _interop.InitializeAsync(cancellationToken).ConfigureAwait(false);

        if (IsConnected)
        {
            _diagnostics.RecordEvent("Camera", "Camera already connected. Disconnecting before reconnecting.");
            await DisconnectAsync().ConfigureAwait(false);
        }

        _session = await _interop.OpenCameraAsync(descriptor.DeviceId, cancellationToken).ConfigureAwait(false);
        _connectedDeviceId = descriptor.DeviceId;
        _diagnostics.RecordEvent("Camera", $"Opened handle {_session.Handle} for {descriptor.DeviceId}");

        try
        {

        // Give the camera a moment to settle after opening connection
        await Task.Delay(500, cancellationToken).ConfigureAwait(false);

        try
        {
            await ExecuteWithRetryAsync(() => 
                FujifilmSdkWrapper.XSDK_SetPriorityMode(_session.Handle, FujifilmSdkWrapper.XSDK_PRIORITY_PC), 
                nameof(FujifilmSdkWrapper.XSDK_SetPriorityMode), 
                cancellationToken).ConfigureAwait(false);
            _diagnostics.RecordEvent("Camera", "Set Priority Mode to PC (matching ASCOM driver behavior).");
        }
        catch (Exception ex)
        {
             _diagnostics.RecordEvent("Camera", $"Failed to set Priority Mode: {ex.Message}");
             // Proceeding, as sometimes it might already be set or non-fatal
        }

        _config = ResolveConfiguration(descriptor.DisplayName);
        if (_config == null)
        {
            _diagnostics.RecordEvent("Camera", $"No configuration found for camera '{descriptor.DisplayName}'. Using defaults.");
        }
        else if (_config.ModelName.Equals("X-T2", StringComparison.OrdinalIgnoreCase))
        {
            _diagnostics.RecordEvent("Camera", "X-T2 selected: using the legacy FF0002API model module. This path is experimental and battery reporting is intentionally unavailable.");
        }

        if (_config != null)
        {
            await ApplyConfigurationAsync(_config, cancellationToken).ConfigureAwait(false);
        }

        // Set Dynamic Range to 100 before querying capabilities (required by SDK)
        // CapSensitivity requires DR to be set first, and supported ISO values depend on DR
        // This matches the ASCOM driver's behavior
        _diagnostics.RecordEvent("Camera", "Setting Dynamic Range to 100 before querying capabilities...");
        try
        {
            // Use numeric value 100 (XSDK_DRANGE_100 = 0x0064 = 100)
            var setDrResult = FujifilmSdkWrapper.XSDK_SetDynamicRange(_session.Handle, 100);
            if (setDrResult == FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                _diagnostics.RecordEvent("Camera", "Dynamic Range set to 100 successfully.");
                // Small delay after setting DR to allow camera to process (matching ASCOM driver)
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var error = FujifilmSdkWrapper.GetLastError(_session.Handle);
                _diagnostics.RecordEvent("Camera", $"Warning: Failed to set Dynamic Range to 100 (result={setDrResult}, ApiCode=0x{error.ApiCode:X}, ErrCode=0x{error.ErrorCode:X}). Capability queries may fail.");
            }
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Camera", $"Warning: Exception setting Dynamic Range: {ex.Message}. Proceeding with capability queries.");
        }

        // Cache capabilities (ISO, shutter speeds) - must be done after DR is set
        CacheCapabilities();
        RefreshBufferCapacity();

        // Initialize metadata with device info
        InitializeMetadata();

        // Refresh operating state (includes battery)
        RefreshOperatingState();

        // Refresh lens metadata (model, aperture, focal length, capabilities)
        RefreshLensMetadata();

        _diagnostics.RecordEvent("Camera", "For reliable sequencing, disable Long Exposure Noise Reduction in the camera menu.");

        _diagnostics.RecordEvent("Camera", $"Fujifilm camera {descriptor.DisplayName} connected. ISO count={_supportedSensitivities.Count}, shutter codes={_shutterCodeToDuration.Count}, Battery={_metadata.BatteryLevel}%");
        RaisePropertyChanged(nameof(IsConnected));
        }
        catch
        {
            var failedSession = _session;
            _session = null;
            _connectedDeviceId = null;
            _config = null;
            if (failedSession != null)
            {
                await _interop.CloseCameraAsync(failedSession).ConfigureAwait(false);
            }
            RaisePropertyChanged(nameof(IsConnected));
            throw;
        }
    }

    private async Task ApplyConfigurationAsync(CameraConfig config, CancellationToken cancellationToken)
    {
        if (_session == null || _session.Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Camera session not available.");
        }

        _diagnostics.RecordEvent("Camera", $"ApplyConfiguration called for {config.ModelName}");

        // Check if camera is in Manual mode (mode dial must be set to M physically)
        try
        {
            var modeResult = FujifilmSdkWrapper.XSDK_GetMode(_session.Handle, out var currentMode);
            if (modeResult == FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                _diagnostics.RecordEvent("Camera", $"Current camera mode: {currentMode} (0x{currentMode:X})");
                // Note: Mode codes are model-specific. For GFX100S, Manual is 1 (not 0x1101)
                // The physical mode dial must be set to M for full manual control
            }
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Camera", $"Failed to get current mode: {ex.Message}");
        }
        
        // NOTE: We do not set AE Mode programmatically here.
        // The ASCOM driver does not do this, and it seems to rely on the user's physical camera settings.
        // Explicitly setting AE Mode caused COMBINATION errors in previous attempts.
        _diagnostics.RecordEvent("Camera", "Skipping programmatic AE mode setting (relying on physical camera state).");

        _diagnostics.RecordEvent("Camera", "Setting Media Record Mode to OFF (0) to prevent SD card conflicts...");
        try
        {
            await ExecuteWithRetryAsync(() => 
                FujifilmSdkWrapper.XSDK_SetMediaRecord(_session.Handle, FujifilmSdkWrapper.XSDK_MEDIAREC_OFF),
                nameof(FujifilmSdkWrapper.XSDK_SetMediaRecord),
                cancellationToken).ConfigureAwait(false);
            _diagnostics.RecordEvent("Camera", "Media Record Mode set to OFF.");
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Camera", $"Failed to set Media Record Mode: {ex.Message}. Exposure might fail if SD card is full or slow.");
        }
    }

    private void CacheCapabilities()
    {
        if (_session == null || _session.Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Camera session not available.");
        }

        _supportedSensitivities = QuerySensitivityValues();
        var shutterCodes = QueryShutterCodes();
        _supportedShutterCodes = shutterCodes; // Store for validation
        _shutterCodeToDuration = BuildShutterSpeedDictionary(shutterCodes);
    }

    /// <summary>
    /// Initializes camera metadata from device info.
    /// </summary>
    private void InitializeMetadata()
    {
        if (_session == null || _session.Handle == IntPtr.Zero)
        {
            _metadata = new FujiCameraMetadata();
            return;
        }

        // Create new metadata instance
        _metadata = new FujiCameraMetadata();

        try
        {
            // Get device info from SDK
            var result = FujifilmSdkWrapper.XSDK_GetDeviceInfoEx(
                _session.Handle,
                out var deviceInfo,
                out int apiCount,
                IntPtr.Zero);

            if (result == FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                _metadata.ProductName = deviceInfo.strProduct?.Trim() ?? string.Empty;
                _metadata.FirmwareVersion = deviceInfo.strFirmware?.Trim() ?? string.Empty;

                _diagnostics.RecordEvent("Camera", $"Device info: Product='{_metadata.ProductName}', Firmware='{_metadata.FirmwareVersion}', API count={apiCount}");
            }
            else
            {
                _diagnostics.RecordEvent("Camera", $"Failed to get device info (result={result})");
            }

            // Get initial dynamic range
            if (FujifilmSdkWrapper.XSDK_GetDynamicRange(_session.Handle, out var dRange) == FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                _metadata.DynamicRangeCode = dRange;
            }
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Camera", $"Error initializing metadata: {ex.Message}");
        }
    }

    private void RefreshBufferCapacity()
    {
        if (_session == null)
        {
            _bufferShootCapacity = 0;
            _bufferTotalCapacity = 0;
            return;
        }

        var result = FujifilmSdkWrapper.XSDK_GetBufferCapacity(_session.Handle, out var shootFrames, out var totalFrames);
        if (result != FujifilmSdkWrapper.XSDK_COMPLETE)
        {
            _diagnostics.RecordEvent("Camera", $"XSDK_GetBufferCapacity failed with code {result}");
            _bufferShootCapacity = 0;
            _bufferTotalCapacity = 0;
            return;
        }

        _bufferShootCapacity = shootFrames;
        _bufferTotalCapacity = totalFrames;
    }

    private void RefreshOperatingState()
    {
        if (_session == null)
        {
            _lastModeCode = 0;
            _lastAEModeCode = 0;
            _lastDynamicRangeCode = 0;
            _lastApiErrorCode = 0;
            _lastSdkErrorCode = 0;
            return;
        }

        if (FujifilmSdkWrapper.XSDK_GetMode(_session.Handle, out var mode) == FujifilmSdkWrapper.XSDK_COMPLETE)
        {
            _lastModeCode = mode;
        }

        if (FujifilmSdkWrapper.XSDK_GetAEMode(_session.Handle, out var aeMode) == FujifilmSdkWrapper.XSDK_COMPLETE)
        {
            _lastAEModeCode = aeMode;
        }

        if (FujifilmSdkWrapper.XSDK_GetDynamicRange(_session.Handle, out var dRange) == FujifilmSdkWrapper.XSDK_COMPLETE)
        {
            _lastDynamicRangeCode = dRange;
        }

        if (FujifilmSdkWrapper.XSDK_GetErrorNumber(_session.Handle, out var apiCode, out var errCode) == FujifilmSdkWrapper.XSDK_COMPLETE)
        {
            _lastApiErrorCode = apiCode;
            _lastSdkErrorCode = errCode;
        }

        // Refresh battery status on every state refresh
        RefreshBatteryStatus();
    }

    /// <summary>
    /// Refreshes the battery level from the camera.
    /// The battery API is variadic and model-dependent. Only call signatures confirmed by
    /// Fujifilm model headers are used; an incorrect arity can corrupt the native call frame.
    /// </summary>
    private void RefreshBatteryStatus()
    {
        if (_session == null || _session.Handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var modelName = _config?.ModelName ?? _metadata.ProductName ?? string.Empty;
            var parameterCount = FujifilmBatteryProtocol.GetParameterCount(modelName);
            if (parameterCount == null)
            {
                _metadata.BatteryLevel = -1;
                _metadata.BatteryStatus = "Unavailable";
                _diagnostics.RecordEvent("Camera", $"Battery query skipped for '{modelName}': the SDK argument layout has not been verified for this model.");
                return;
            }

            int result;
            int bodyBatteryInfo, gripBatteryInfo, gripBattery2Info;
            int bodyBatteryRatio, gripBatteryRatio, gripBattery2Ratio;

            if (parameterCount == FujifilmBatteryProtocol.NewModelParameterCount)
            {
                result = FujifilmSdkWrapper.XSDK_GetProp_Battery8(
                    _session.Handle,
                    FujifilmSdkWrapper.API_CODE_CheckBatteryInfo,
                    FujifilmSdkWrapper.API_PARAM_CheckBatteryInfo_NewModels,
                    out bodyBatteryInfo,
                    out gripBatteryInfo,
                    out gripBattery2Info,
                    out bodyBatteryRatio,
                    out gripBatteryRatio,
                    out gripBattery2Ratio,
                    out _,  // plBodyBattery2Info
                    out _); // plBodyBattery2Ratio2

                _diagnostics.RecordEvent("Camera", $"Battery8 call: result={result}, bodyInfo=0x{bodyBatteryInfo:X}, bodyRatio={bodyBatteryRatio}");
            }
            else
            {
                result = FujifilmSdkWrapper.XSDK_GetProp_Battery6(
                    _session.Handle,
                    FujifilmSdkWrapper.API_CODE_CheckBatteryInfo,
                    FujifilmSdkWrapper.API_PARAM_CheckBatteryInfo_OldModels,
                    out bodyBatteryInfo,
                    out gripBatteryInfo,
                    out gripBattery2Info,
                    out bodyBatteryRatio,
                    out gripBatteryRatio,
                    out gripBattery2Ratio);

                _diagnostics.RecordEvent("Camera", $"Battery6 call: result={result}, bodyInfo=0x{bodyBatteryInfo:X}, bodyRatio={bodyBatteryRatio}");
            }

            if (result == FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                // Prefer the ratio (0-100%) if available, otherwise use the status code
                int batteryLevel;
                if (bodyBatteryRatio >= 0 && bodyBatteryRatio <= 100)
                {
                    batteryLevel = (int)bodyBatteryRatio;
                }
                else
                {
                    batteryLevel = MapBatteryStatusToPercent((int)bodyBatteryInfo);
                }

                _metadata.BatteryLevel = Math.Clamp(batteryLevel, 0, 100);
                _metadata.BatteryStatus = _metadata.BatteryLevel switch
                {
                    > 50 => "OK",
                    > 20 => "Low",
                    _ => "Critical"
                };

                _diagnostics.RecordEvent("Camera", $"Battery: {_metadata.BatteryLevel}% ({_metadata.BatteryStatus})");
            }
            else
            {
                var error = FujifilmSdkWrapper.GetLastError(_session.Handle);
                _diagnostics.RecordEvent("Camera", $"Battery check failed: result={result}, ApiCode=0x{error.ApiCode:X}, ErrCode=0x{error.ErrorCode:X}");
            }
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Camera", $"Battery status refresh error: {ex.Message}");
        }
    }

    /// <summary>
    /// Maps SDK_POWERCAPACITY status codes to percentage values.
    /// </summary>
    private static int MapBatteryStatusToPercent(int statusCode)
    {
        return statusCode switch
        {
            FujifilmSdkWrapper.SDK_POWERCAPACITY_EMPTY => 0,
            FujifilmSdkWrapper.SDK_POWERCAPACITY_END => 5,
            FujifilmSdkWrapper.SDK_POWERCAPACITY_PREEND => 10,
            FujifilmSdkWrapper.SDK_POWERCAPACITY_HALF => 50,
            FujifilmSdkWrapper.SDK_POWERCAPACITY_FULL => 100,
            FujifilmSdkWrapper.SDK_POWERCAPACITY_HIGH => 80,
            FujifilmSdkWrapper.SDK_POWERCAPACITY_PREEND5 => 15,
            FujifilmSdkWrapper.SDK_POWERCAPACITY_20 => 20,
            FujifilmSdkWrapper.SDK_POWERCAPACITY_40 => 40,
            FujifilmSdkWrapper.SDK_POWERCAPACITY_60 => 60,
            FujifilmSdkWrapper.SDK_POWERCAPACITY_80 => 80,
            FujifilmSdkWrapper.SDK_POWERCAPACITY_100 => 100,
            FujifilmSdkWrapper.SDK_POWERCAPACITY_DC_CHARGE => 100, // Charging
            FujifilmSdkWrapper.SDK_POWERCAPACITY_DC => 100,        // DC powered
            _ => statusCode >= 0 && statusCode <= 100 ? statusCode : 0
        };
    }

    /// <summary>
    /// Refreshes lens metadata including focal length and aperture.
    /// </summary>
    private void RefreshLensMetadata()
    {
        if (_session == null || _session.Handle == IntPtr.Zero)
        {
            _diagnostics.RecordEvent("Camera", "RefreshLensMetadata: No session available");
            return;
        }

        try
        {
            _diagnostics.RecordEvent("Camera", "RefreshLensMetadata: Calling XSDK_GetLensInfo...");

            // Get basic lens info (model, serial, capabilities)
            var lensInfoResult = FujifilmSdkWrapper.XSDK_GetLensInfo(_session.Handle, out var lensInfo);
            _diagnostics.RecordEvent("Camera", $"XSDK_GetLensInfo result: {lensInfoResult} (COMPLETE={FujifilmSdkWrapper.XSDK_COMPLETE})");

            if (lensInfoResult == FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                _metadata.LensProductName = lensInfo.strProductName?.Trim() ?? string.Empty;
                _metadata.LensSerialNumber = lensInfo.strSerialNo?.Trim() ?? string.Empty;
                _metadata.LensModel = lensInfo.strModel?.Trim() ?? string.Empty;
                _metadata.HasImageStabilization = lensInfo.lISCapability != 0;
                _metadata.HasManualFocus = lensInfo.lMFCapability != 0;
                _metadata.IsZoomLens = lensInfo.lZoomPosCapability != 0;

                _diagnostics.RecordEvent("Camera", $"Lens detected: Model='{_metadata.LensModel}' Product='{_metadata.LensProductName}' SN='{_metadata.LensSerialNumber}' IS={_metadata.HasImageStabilization} MF={_metadata.HasManualFocus} Zoom={_metadata.IsZoomLens}");
            }
            else
            {
                var error = FujifilmSdkWrapper.GetLastError(_session.Handle);
                _diagnostics.RecordEvent("Camera", $"XSDK_GetLensInfo FAILED: result={lensInfoResult}, ApiCode=0x{error.ApiCode:X}, ErrCode=0x{error.ErrorCode:X}. No lens detected or lens detection not supported.");
            }

            // Get current aperture (f-number * 100)
            var apertureResult = FujifilmSdkWrapper.XSDK_GetProp(
                _session.Handle,
                FujifilmSdkWrapper.API_CODE_GetAperture,
                FujifilmSdkWrapper.API_PARAM_Aperture,
                out int apertureValue);

            if (apertureResult == FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                // SDK returns f-number * 100 (e.g., 280 for f/2.8)
                _metadata.CurrentAperture = apertureValue / 100.0;
                _diagnostics.RecordEvent("Camera", $"Aperture: f/{_metadata.CurrentAperture:F1}");
            }
            else
            {
                _diagnostics.RecordEvent("Camera", $"GetAperture failed: result={apertureResult}");
            }

            // Get zoom position for zoom lenses
            if (_metadata.IsZoomLens)
            {
                RefreshZoomPosition();
            }
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Camera", $"Lens metadata refresh error: {ex.Message}");
        }
    }

    /// <summary>
    /// Refreshes zoom position and maps it to focal length for zoom lenses.
    /// </summary>
    private void RefreshZoomPosition()
    {
        if (_session == null || _session.Handle == IntPtr.Zero || !_metadata.IsZoomLens)
        {
            return;
        }

        try
        {
            // Get current zoom position
            var zoomResult = FujifilmSdkWrapper.XSDK_GetProp(
                _session.Handle,
                FujifilmSdkWrapper.API_CODE_GetLensZoomPos,
                FujifilmSdkWrapper.API_PARAM_LensZoomPos,
                out int zoomPos);

            if (zoomResult != FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                return;
            }

            _metadata.CurrentZoomPosition = (int)zoomPos;

            // Get zoom position to focal length mapping
            // Note: XSDK_CapLensZoomPos requires model-specific API codes
            // For now, we store the zoom position and estimate focal length if possible
            // A proper implementation would query CapLensZoomPos for the mapping table

            // Try to get capabilities for zoom position mapping
            var capResult = FujifilmSdkWrapper.XSDK_CapProp(
                _session.Handle,
                FujifilmSdkWrapper.API_CODE_CapLensZoomPos,
                1,  // API param for count
                out int numPositions,
                IntPtr.Zero);

            if (capResult == FujifilmSdkWrapper.XSDK_COMPLETE && numPositions > 0)
            {
                // Query the actual position-to-focal-length mappings
                // Each entry contains: position, focal length, 35mm equivalent
                var bufferSize = numPositions * 3 * sizeof(int);  // 3 ints per position
                var buffer = Marshal.AllocHGlobal(bufferSize);
                try
                {
                    var dataResult = FujifilmSdkWrapper.XSDK_CapProp(
                        _session.Handle,
                        FujifilmSdkWrapper.API_CODE_CapLensZoomPos,
                        2,  // API param for data
                        out numPositions,
                        buffer);

                    if (dataResult == FujifilmSdkWrapper.XSDK_COMPLETE)
                    {
                        // Find the focal length for current zoom position
                        for (int i = 0; i < numPositions; i++)
                        {
                            int pos = Marshal.ReadInt32(buffer, i * 3 * sizeof(int));
                            int focal = Marshal.ReadInt32(buffer, i * 3 * sizeof(int) + sizeof(int));
                            int focal35 = Marshal.ReadInt32(buffer, i * 3 * sizeof(int) + 2 * sizeof(int));

                            if (pos == _metadata.CurrentZoomPosition)
                            {
                                _metadata.CurrentFocalLength = focal;
                                _metadata.FocalLength35mmEquiv = focal35;
                                _diagnostics.RecordEvent("Camera", $"Zoom: pos={pos}, focal={focal}mm (35mm equiv: {focal35}mm)");
                                break;
                            }
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Camera", $"Zoom position refresh error: {ex.Message}");
        }
    }

    private IReadOnlyList<int> QuerySensitivityValues()
    {
        if (_session == null)
        {
            return Array.Empty<int>();
        }

        // Use DR=100 explicitly (should have been set during connection, but use explicit value for safety)
        // CapSensitivity requires DR to be set, and we set it to 100 during ConnectAsync
        int drToQuery = 100; // XSDK_DRANGE_100
        
        int count = 0;
        try
        {
            // Step 1: Get count
            var countResult = FujifilmSdkWrapper.XSDK_CapSensitivity(_session.Handle, ref drToQuery, ref count, IntPtr.Zero);
            
            if (countResult != FujifilmSdkWrapper.XSDK_COMPLETE || count <= 0)
            {
                _diagnostics.RecordEvent("Camera", $"QuerySensitivityValues: CapSensitivity query failed or returned 0 values (Result={countResult}, Count={count}). Using fallback ISO values.");
                return BuildFallbackIsoArray();
            }

            // Step 2: Get data
            var bufferSize = count * sizeof(int);
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                var dataResult = FujifilmSdkWrapper.XSDK_CapSensitivity(_session.Handle, ref drToQuery, ref count, buffer);
                
                if (dataResult != FujifilmSdkWrapper.XSDK_COMPLETE)
                {
                    _diagnostics.RecordEvent("Camera", $"QuerySensitivityValues: Failed to get sensitivity data (Result={dataResult}). Using fallback ISO values.");
                    return BuildFallbackIsoArray();
                }

                var sensitivities = new List<int>(count);
                for (int i = 0; i < count; i++)
                {
                    var val = Marshal.ReadInt32(buffer, i * sizeof(int));
                    sensitivities.Add(val);
                }
                
                _diagnostics.RecordEvent("Camera", $"QuerySensitivityValues: Successfully queried {sensitivities.Count} ISO values from camera.");
                return sensitivities;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Camera", $"QuerySensitivityValues: Exception during ISO query: {ex.Message}. Using fallback ISO values.");
            return BuildFallbackIsoArray();
        }
    }

    private IReadOnlyList<int> QueryShutterCodes()
    {
        if (_session == null)
        {
            return Array.Empty<int>();
        }

        int count = 0;
        int bulbCapable;
        // Step 1: Get count
        var countResult = FujifilmSdkWrapper.XSDK_CapShutterSpeed(_session.Handle, ref count, IntPtr.Zero, out bulbCapable);
        FujifilmSdkWrapper.CheckResult(_session.Handle, countResult, nameof(FujifilmSdkWrapper.XSDK_CapShutterSpeed));
        bool sdkBulbCapable = bulbCapable != 0; // Store SDK result temporarily

        _bulbCapable = sdkBulbCapable;
        _diagnostics.RecordEvent("Camera", $"Bulb capability: SDK={sdkBulbCapable}, Config={_config?.DefaultBulbCapable}, Final={_bulbCapable}");

        if (count == 0)
        {
            // If the SDK returns no timed shutter codes at all, keep the conservative
            // model default as a last-resort bulb hint. An explicit SDK "not bulb
            // capable" result above is not overridden.
            if (_config?.DefaultBulbCapable == true)
            {
                _bulbCapable = true;
                _diagnostics.RecordEvent("Camera", "SDK returned 0 shutter codes, using config DefaultBulbCapable=true");
            }
            return Array.Empty<int>();
        }

        // Step 2: Get data
        var bufferSize = count * sizeof(int);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            // Note: bulbCapable is an output, but for the second call we just pass a dummy variable or the same one.
            var dataResult = FujifilmSdkWrapper.XSDK_CapShutterSpeed(_session.Handle, ref count, buffer, out bulbCapable);
            FujifilmSdkWrapper.CheckResult(_session.Handle, dataResult, nameof(FujifilmSdkWrapper.XSDK_CapShutterSpeed));

            var shutterCodes = new List<int>(count);
            for (int i = 0; i < count; i++)
            {
                var val = Marshal.ReadInt32(buffer, i * sizeof(int));
                shutterCodes.Add(val);
            }
            return shutterCodes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private IReadOnlyDictionary<int, double> BuildShutterSpeedDictionary(IReadOnlyList<int> shutterCodes)
    {
        return FujifilmShutterSpeedCatalog.Build(
            shutterCodes,
            _config?.ShutterSpeedMap,
            _bulbCapable,
            _config?.DefaultMaxExposure ?? 3600.0,
            code => _diagnostics.RecordEvent("Camera", $"SDK reported undocumented shutter code {code}; ignoring it instead of guessing its duration."));
    }

    public async Task<RawCaptureResult> CaptureRawAsync(double exposureSeconds, int iso, CancellationToken cancellationToken)
    {
        if (_session == null || _session.Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Camera is not connected.");
        }

        if (exposureSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exposureSeconds), "Exposure must be positive.");
        }

        if (_supportedSensitivities.Count > 0 && !_supportedSensitivities.Contains(iso))
        {
            _diagnostics.RecordEvent("Camera", $"Requested ISO {iso} not in supported list; using closest.");
            iso = _supportedSensitivities.OrderBy(value => Math.Abs(value - iso)).First();
        }

        var shutterCode = ResolveShutterCode(exposureSeconds);
        if (shutterCode == FujifilmSdkWrapper.XSDK_SHUTTER_BULB && !_bulbCapable)
        {
            throw new InvalidOperationException("Camera does not report bulb capability; exposure exceeds timed range.");
        }

        _diagnostics.RecordEvent("Camera", $"Starting exposure. Duration={exposureSeconds}s ISO={iso} ShutterCode={shutterCode}");

        // Set Sensitivity (ISO)
        _diagnostics.RecordEvent("Camera", $"Setting ISO to {iso}...");
        var setIsoResult = FujifilmSdkWrapper.XSDK_SetSensitivity(_session.Handle, iso);
        FujifilmSdkWrapper.CheckResult(_session.Handle, setIsoResult, nameof(FujifilmSdkWrapper.XSDK_SetSensitivity));
        _diagnostics.RecordEvent("Camera", $"ISO set successfully to {iso}");

        // Add delay to allow camera to process ISO change and update internal state
        // This is critical as shutter speed support may vary with ISO/Dynamic Range combination
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);

        // Re-query shutter codes after ISO change to ensure we have valid codes for current state
        var currentShutterCodes = QueryShutterCodes();
        if (currentShutterCodes.Count > 0)
        {
            _diagnostics.RecordEvent("Camera", $"Re-queried shutter codes after ISO change: {currentShutterCodes.Count} codes available");
            _supportedShutterCodes = currentShutterCodes;
            _shutterCodeToDuration = BuildShutterSpeedDictionary(currentShutterCodes);
            shutterCode = ResolveShutterCode(exposureSeconds);
        }

        // Set Shutter Speed
        var bulbVal = (shutterCode == FujifilmSdkWrapper.XSDK_SHUTTER_BULB) ? 1 : 0;
        _diagnostics.RecordEvent("Camera", $"Setting Shutter Speed to {shutterCode} (Bulb={bulbVal})...");
        
        int retryCount = 0;
        const int maxRetries = 3;
        bool shutterSet = false;

        while (!shutterSet && retryCount <= maxRetries)
        {
            var setSpeedResult = FujifilmSdkWrapper.XSDK_SetShutterSpeed(_session.Handle, shutterCode, bulbVal);
            
            if (setSpeedResult == FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                shutterSet = true;
                _diagnostics.RecordEvent("Camera", $"Shutter Speed set successfully to {shutterCode}");
            }
            else
            {
                var error = FujifilmSdkWrapper.GetLastError(_session.Handle);
                
                // Handle BUSY state
                if (error.ErrorCode == FujifilmSdkWrapper.XSDK_ERRCODE_BUSY && retryCount < maxRetries)
                {
                    retryCount++;
                    _diagnostics.RecordEvent("Camera", $"SetShutterSpeed failed with BUSY. Retrying ({retryCount}/{maxRetries}) in 250ms...");
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
                // Handle COMBINATION error (function call combination error - invalid code for current state)
                else if (error.ErrorCode == FujifilmSdkWrapper.XSDK_ERRCODE_COMBINATION)
                {
                    // Log current camera state for diagnostics
                    RefreshOperatingState();
                    var currentMode = _lastModeCode;
                    var currentAEMode = _lastAEModeCode;
                    var currentDR = _lastDynamicRangeCode;
                    var currentIso = -1;
                    if (FujifilmSdkWrapper.XSDK_GetSensitivity(_session.Handle, out var currentIsoValue) == FujifilmSdkWrapper.XSDK_COMPLETE)
                    {
                        currentIso = currentIsoValue;
                    }
                    
                    _diagnostics.RecordEvent("Camera", "ERROR: Failed to set shutter speed due to COMBINATION error (0x2003).");
                    _diagnostics.RecordEvent("Camera", $"Current camera state: Mode={currentMode}, AE={currentAEMode}, DR={currentDR}, ISO={currentIso}");
                    _diagnostics.RecordEvent("Camera", $"Attempted shutter code: {shutterCode} (Bulb={bulbVal})");
                    
                    // Re-query supported shutter codes to find a valid alternative
                    _diagnostics.RecordEvent("Camera", "Re-querying supported shutter codes to find valid alternative...");
                    var validCodes = QueryShutterCodes();
                    if (validCodes.Count > 0)
                    {
                        _supportedShutterCodes = validCodes;
                        _diagnostics.RecordEvent("Camera", $"Found {validCodes.Count} supported shutter codes for current state");
                        
                        // Rebuild duration map with newly queried codes
                        var newDurationMap = BuildShutterSpeedDictionary(validCodes);
                        _shutterCodeToDuration = newDurationMap;
                        _diagnostics.RecordEvent("Camera", $"Rebuilt duration map with {newDurationMap.Count} entries");
                        
                        // First, try to find the closest code to the requested duration (prefer codes close to requested)
                        // Use a tolerance of 20% to prefer reasonably close matches
                        var tolerance = exposureSeconds * 0.2;
                        var alternativeCode = newDurationMap
                            .Where(pair => pair.Key > 0 && Math.Abs(pair.Value - exposureSeconds) <= tolerance)
                            .OrderBy(pair => Math.Abs(pair.Value - exposureSeconds))
                            .FirstOrDefault();
                        
                        // If no close match, try codes <= requested (prefer not to over-expose)
                        if (alternativeCode.Key == 0)
                        {
                            alternativeCode = newDurationMap
                                .Where(pair => pair.Value <= exposureSeconds + 1e-6 && pair.Key > 0)
                                .OrderByDescending(pair => pair.Value)
                                .FirstOrDefault();
                        }
                        
                        // If still no match, find closest overall (may over-expose)
                        if (alternativeCode.Key == 0)
                        {
                            _diagnostics.RecordEvent("Camera", $"No code found <= {exposureSeconds}s. Searching for closest code overall...");
                            alternativeCode = newDurationMap
                                .Where(pair => pair.Key > 0)
                                .OrderBy(pair => Math.Abs(pair.Value - exposureSeconds))
                                .FirstOrDefault();
                        }
                        
                        if (alternativeCode.Key != 0 && alternativeCode.Key != shutterCode)
                        {
                            _diagnostics.RecordEvent("Camera", $"Attempting alternative shutter code {alternativeCode.Key} (duration={alternativeCode.Value}s)");
                            shutterCode = alternativeCode.Key;
                            bulbVal = (shutterCode == FujifilmSdkWrapper.XSDK_SHUTTER_BULB) ? 1 : 0;
                            
                            // Retry with alternative code
                            var retryResult = FujifilmSdkWrapper.XSDK_SetShutterSpeed(_session.Handle, shutterCode, bulbVal);
                            if (retryResult == FujifilmSdkWrapper.XSDK_COMPLETE)
                            {
                                _diagnostics.RecordEvent("Camera", $"Successfully set alternative shutter code {shutterCode}");
                                shutterSet = true;
                                break;
                            }
                            else
                            {
                                var retryError = FujifilmSdkWrapper.GetLastError(_session.Handle);
                                _diagnostics.RecordEvent("Camera", $"Alternative code {shutterCode} also failed (result={retryResult}, errCode=0x{retryError.ErrorCode:X})");
                            }
                        }
                        else if (alternativeCode.Key == 0)
                        {
                            _diagnostics.RecordEvent("Camera", "No alternative code could be found in the queried codes");
                        }
                    }
                    
                    _diagnostics.RecordEvent("Camera", "CRITICAL TIP: Ensure the physical Shutter Speed dial is set to 'T' (Time) or 'A' (Auto) to allow software control.");
                    _diagnostics.RecordEvent("Camera", "Also ensure camera is in Manual (M) mode and that the requested shutter speed is valid for current ISO/Dynamic Range combination.");
                    _diagnostics.RecordEvent("Camera", "The exposure will be aborted because the requested shutter setting could not be applied.");
                    break; 
                }
                else
                {
                    // Throw for other errors
                    throw new FujifilmSdkException(nameof(FujifilmSdkWrapper.XSDK_SetShutterSpeed), setSpeedResult, error.ApiCode, error.ErrorCode);
                }
            }
        }

        if (!shutterSet)
        {
            throw new InvalidOperationException(
                $"The camera rejected shutter code {shutterCode} for {exposureSeconds:0.###} seconds. " +
                "Set the physical shutter dial to T, use Manual exposure mode, and retry. The exposure was not triggered.");
        }

        if (shutterCode != FujifilmSdkWrapper.XSDK_SHUTTER_BULB)
        {
            await ExecuteTimedExposureAsync(exposureSeconds, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ExecuteBulbExposureAsync(exposureSeconds, cancellationToken).ConfigureAwait(false);
        }

        var raw = await DownloadImageAsync(cancellationToken).ConfigureAwait(false);
        var finalized = raw with { ExposureSeconds = exposureSeconds, Iso = iso, ShutterCode = shutterCode, TimestampTicks = DateTime.UtcNow.Ticks };
        RefreshBufferCapacity();
        RefreshOperatingState();
        return finalized;
    }

    private int ResolveShutterCode(double exposureSeconds)
    {
        _diagnostics.RecordEvent("Camera", $"ResolveShutterCode: Requested duration={exposureSeconds}s, ShutterCodeToDuration has {_shutterCodeToDuration.Count} entries");
        var code = FujifilmShutterSpeedCatalog.SelectCode(_shutterCodeToDuration, exposureSeconds, _bulbCapable);
        var selectedDuration = _shutterCodeToDuration.GetValueOrDefault(code, exposureSeconds);
        _diagnostics.RecordEvent("Camera", $"ResolveShutterCode: Selected code={code} for duration={selectedDuration}s (requested {exposureSeconds}s)");
        return code;
    }

    public async Task StopExposureAsync()
    {
        if (_session == null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _bulbReleaseHeld, 0) == 1)
        {
            IssueReleaseCommand(FujifilmSdkWrapper.XSDK_RELEASE_N_BULBS1OFF, "Stop active bulb exposure");
        }
        else
        {
            _diagnostics.RecordEvent("Camera", "StopExposure requested with no active bulb release. Timed exposures cannot be interrupted by this SDK.");
        }

        RefreshBufferCapacity();
        RefreshOperatingState();
        await Task.CompletedTask;
    }

    private async Task ExecuteTimedExposureAsync(double exposureSeconds, CancellationToken cancellationToken)
    {
        if (_session == null)
        {
            return;
        }

        IssueReleaseCommand(FujifilmSdkWrapper.XSDK_RELEASE_SHOOT_S1OFF, "Timed exposure trigger");
        var extra = TimeSpan.FromSeconds(Math.Max(1.0, Math.Min(5.0, exposureSeconds * 0.2)));
        await Task.Delay(TimeSpan.FromSeconds(exposureSeconds) + extra, cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteBulbExposureAsync(double exposureSeconds, CancellationToken cancellationToken)
    {
        if (_session == null)
        {
            return;
        }

        _diagnostics.RecordEvent("Camera", "Starting bulb exposure sequence: S1ON");
        IssueReleaseCommand(FujifilmSdkWrapper.XSDK_RELEASE_S1ON, "Bulb S1ON");
        Interlocked.Exchange(ref _bulbReleaseHeld, 1);

        try
        {
            var releaseDelay = Math.Clamp(_settingsProvider.Settings.BulbReleaseDelayMs, 0, 5000);
            _diagnostics.RecordEvent("Camera", $"Delay between S1ON and BULBS2_ON: {releaseDelay}ms");
            await Task.Delay(TimeSpan.FromMilliseconds(releaseDelay), cancellationToken).ConfigureAwait(false);

            _diagnostics.RecordEvent("Camera", "Starting bulb exposure: BULBS2_ON");
            IssueReleaseCommand(FujifilmSdkWrapper.XSDK_RELEASE_BULBS2_ON, "Bulb start");

            _diagnostics.RecordEvent("Camera", $"Waiting for bulb exposure duration: {exposureSeconds}s");
            await Task.Delay(TimeSpan.FromSeconds(exposureSeconds), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (Interlocked.Exchange(ref _bulbReleaseHeld, 0) == 1)
            {
                _diagnostics.RecordEvent("Camera", "Stopping bulb exposure: BULBS1OFF");
                IssueReleaseCommand(FujifilmSdkWrapper.XSDK_RELEASE_N_BULBS1OFF, "Bulb stop");
            }
        }
        
        // Add delay after stop command to allow camera to process
        // The camera needs time to finalize the exposure and prepare image data
        _diagnostics.RecordEvent("Camera", "Adding delay after bulb stop to allow camera processing...");
        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RawCaptureResult> DownloadImageAsync(CancellationToken cancellationToken)
    {
        if (_session == null)
        {
            throw new InvalidOperationException("Camera session not available.");
        }

        // For bulb exposures, the camera needs more time to process after stopping
        // Increase timeout and polling interval for bulb exposures
        const int maxAttempts = 30; // Increased from 10 to allow more time for bulb processing
        const int pollIntervalMs = 500; // Increased from 200ms to match ASCOM driver polling
        
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var infoResult = FujifilmSdkWrapper.XSDK_ReadImageInfo(_session.Handle, out var info);
            
            // Don't throw on error, just log and continue polling
            if (infoResult != FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                _diagnostics.RecordEvent("Camera", $"ReadImageInfo failed (attempt {attempt + 1}): result={infoResult}. Continuing to poll...");
                await Task.Delay(TimeSpan.FromMilliseconds(pollIntervalMs), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (info.lDataSize > 0)
            {
                if (info.lFormat != FujifilmSdkWrapper.XSDK_IMAGEFORMAT_RAW)
                {
                    _diagnostics.RecordEvent("Camera", $"Discarding non-RAW image from camera (format={info.lFormat}, bytes={info.lDataSize}). Set IMAGE QUALITY to RAW or RAW+JPEG.");
                    var deleteNonRawResult = FujifilmSdkWrapper.XSDK_DeleteImage(_session.Handle);
                    if (deleteNonRawResult != FujifilmSdkWrapper.XSDK_COMPLETE)
                    {
                        _diagnostics.RecordEvent("Camera", $"XSDK_DeleteImage for non-RAW frame returned {deleteNonRawResult}");
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(pollIntervalMs), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var buffer = new byte[info.lDataSize];
                var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    var readResult = FujifilmSdkWrapper.XSDK_ReadImage(_session.Handle, handle.AddrOfPinnedObject(), (uint)buffer.Length);
                    FujifilmSdkWrapper.CheckResult(_session.Handle, readResult, nameof(FujifilmSdkWrapper.XSDK_ReadImage));

                    _diagnostics.RecordEvent("Camera", $"Downloaded RAW frame {info.lImagePixWidth}x{info.lImagePixHeight} bytes={buffer.Length}");
                    return new RawCaptureResult(buffer, info.lImagePixWidth, info.lImagePixHeight, info.lFormat, info.lImageBitDepth, 0, 0, 0.0, 0);
                }
                finally
                {
                    handle.Free();
                    var deleteResult = FujifilmSdkWrapper.XSDK_DeleteImage(_session.Handle);
                    if (deleteResult != FujifilmSdkWrapper.XSDK_COMPLETE)
                    {
                        _diagnostics.RecordEvent("Camera", $"XSDK_DeleteImage returned {deleteResult}");
                    }
                }
            }

            _diagnostics.RecordEvent("Camera", $"Image not ready yet (attempt {attempt + 1}/{maxAttempts}). Waiting {pollIntervalMs}ms...");
            await Task.Delay(TimeSpan.FromMilliseconds(pollIntervalMs), cancellationToken).ConfigureAwait(false);
            RefreshBufferCapacity();
            RefreshOperatingState();
        }

        throw new TimeoutException($"Timed out waiting for Fujifilm RAW image data after exposure. Ensure IMAGE QUALITY is set to RAW or RAW+JPEG. Polled for {maxAttempts * pollIntervalMs / 1000.0}s.");
    }

    private void IssueReleaseCommand(int releaseMode, string context)
    {
        if (_session == null)
        {
            return;
        }

        IntPtr shotOptPtr = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(shotOptPtr, 0);
            var releaseResult = FujifilmSdkWrapper.XSDK_Release(_session.Handle, releaseMode, shotOptPtr, out var status);
            if (releaseResult != FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                _diagnostics.RecordEvent("Camera", $"{context} failed (result={releaseResult}, status={status})");
                var error = FujifilmSdkWrapper.GetLastError(_session.Handle);
                throw new FujifilmSdkException(nameof(FujifilmSdkWrapper.XSDK_Release), releaseResult, error.ApiCode, error.ErrorCode);
            }
            else
            {
                _diagnostics.RecordEvent("Camera", $"{context} succeeded (status={status})");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(shotOptPtr);
        }
    }

    public async Task DisconnectAsync()
    {
        if (_session != null && _session.Handle != IntPtr.Zero)
        {
            _diagnostics.RecordEvent("Camera", $"Closing camera session {_session.Handle}");
            await _interop.CloseCameraAsync(_session).ConfigureAwait(false);
            _session = null;
            _connectedDeviceId = null;
            _config = null;
            _supportedSensitivities = Array.Empty<int>();
            _shutterCodeToDuration = new Dictionary<int, double>();
            _supportedShutterCodes = Array.Empty<int>();
            _bufferShootCapacity = 0;
            _bufferTotalCapacity = 0;
            RaisePropertyChanged(nameof(IsConnected));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
    }

    private CameraConfig? ResolveConfiguration(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            _diagnostics.RecordEvent("Camera", "ResolveConfiguration: DisplayName is null or empty");
            return null;
        }

        var config = _catalog.TryGetByProductName(displayName);
        if (config != null)
        {
            _diagnostics.RecordEvent("Camera", $"ResolveConfiguration: Found config for '{displayName}'");
        }
        else
        {
            _diagnostics.RecordEvent("Camera", $"ResolveConfiguration: No config found for '{displayName}'");
        }

        return config;
    }
}

public sealed record RawCaptureResult(
    byte[] RawBuffer,
    int Width,
    int Height,
    int Format,
    int BitDepth,
    int Iso,
    int ShutterCode,
    double ExposureSeconds,
    long TimestampTicks);

public sealed record FujiCameraCapabilities(
    IReadOnlyList<int> IsoValues,
    int DefaultIso,
    double MinExposureSeconds,
    double MaxExposureSeconds,
    bool SupportsBulb,
    int SensorWidth,
    int SensorHeight,
    int BufferShootCapacity,
    int BufferTotalCapacity,
    int ModeCode,
    int AEModeCode,
    int DynamicRangeCode,
    int LastApiErrorCode,
    int LastSdkErrorCode,
    FujiCameraMetadata Metadata,
    double TimedExposureMaxSeconds,
    double BulbExposureMaxSeconds)
{
    public static FujiCameraCapabilities Empty { get; } = new(
        Array.Empty<int>(),
        0,
        0,
        0,
        false,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        FujiCameraMetadata.Empty,
        0,
        0);
}
