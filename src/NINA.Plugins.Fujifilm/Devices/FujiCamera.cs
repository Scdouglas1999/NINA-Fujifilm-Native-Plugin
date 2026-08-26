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
    private readonly IFujiEquipmentRegistry _registry;
    private readonly IFujifilmDiagnosticsService _diagnostics;

    private FujifilmCameraSession? _session;
    private CameraConfig? _config;
    private IReadOnlyList<int> _supportedSensitivities = Array.Empty<int>();
    private IReadOnlyDictionary<int, double> _shutterCodeToDuration = new Dictionary<int, double>();
    private IReadOnlyList<int> _supportedShutterCodes = Array.Empty<int>(); // Store originally queried codes for validation
    private IReadOnlyList<int> _supportedApertureValues = Array.Empty<int>();
    private bool _bulbCapable;
    private FujiApiCapabilities _apiCapabilities = FujiApiCapabilities.Unknown;
    private bool _longExposureNoiseReductionOn;
    private int? _batteryParameterCount;
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

    public bool SupportsApertureControl =>
        IsConnected &&
        _supportedApertureValues.Count > 0;

    public IReadOnlyList<double> AvailableApertures =>
        _supportedApertureValues.Select(FujifilmApertureCatalog.ToFNumber).ToArray();

    public double CurrentAperture => _metadata.CurrentAperture;

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
        var bulbMaxExposure = FujifilmBulbCapability.ResolveMaximumExposureSeconds(
            _bulbCapable, _config?.DefaultMaxExposure, timedMaxExposure);
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
        IFujifilmDiagnosticsService diagnostics,
        IFujiEquipmentRegistry registry)
    {
        _interop = interop;
        _catalog = catalog;
        _settingsProvider = settingsProvider;
        _diagnostics = diagnostics;
        _registry = registry;
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

            // The SDK documents three recoverable busy states. FORCEMODE_BUSY and
            // RUNNING_OTHER_FUNCTION are both described as transient conditions that clear once the
            // camera finishes what it is doing, so they are worth the same retry as plain BUSY.
            var isBusy = error.ErrorCode is FujifilmSdkWrapper.XSDK_ERRCODE_BUSY
                or FujifilmSdkWrapper.XSDK_ERRCODE_FORCEMODE_BUSY
                or FujifilmSdkWrapper.XSDK_ERRCODE_RUNNING_OTHER_FUNCTION;

            if (isBusy && retryCount < maxRetries)
            {
                retryCount++;
                _diagnostics.RecordEvent("Camera", $"{operationName} failed with BUSY (0x{error.ErrorCode:X}). Retrying ({retryCount}/{maxRetries}) in {delayMs}ms...");
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

        // RAW depth/compression, Long Exposure NR and crop mode. Runs after InitializeMetadata so
        // the advertised API code list is available to gate each step.
        ApplyCaptureQualitySettings();

        _diagnostics.RecordEvent("Camera", $"Fujifilm camera {descriptor.DisplayName} connected. ISO count={_supportedSensitivities.Count}, shutter codes={_shutterCodeToDuration.Count}, Battery={_metadata.BatteryLevel}%");
        _registry.RegisterCamera(this);
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

        if (!_settingsProvider.Settings.DisableCameraCardRecording)
        {
            _diagnostics.RecordEvent("Camera", "Leaving camera card recording enabled (DisableCameraCardRecording is off).");
            return;
        }

        _diagnostics.RecordEvent("Camera", $"Setting Media Record Mode to OFF (0x{FujifilmSdkWrapper.XSDK_MEDIAREC_OFF:X}) to prevent SD card conflicts...");
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

                _apiCapabilities = ReadApiCapabilities(apiCount);
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

    /// <summary>
    /// Reads the list of model-dependent API codes the connected body advertises. Optional features
    /// are gated on this rather than on a per-model table, because the headers disagree between
    /// models and firmware moves the line.
    /// </summary>
    private FujiApiCapabilities ReadApiCapabilities(int apiCount)
    {
        if (_session == null || apiCount <= 0)
        {
            return FujiApiCapabilities.Unknown;
        }

        var buffer = Marshal.AllocHGlobal(apiCount * sizeof(int));
        try
        {
            var result = FujifilmSdkWrapper.XSDK_GetDeviceInfoEx(
                _session.Handle, out _, out var confirmedCount, buffer);
            if (result != FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                _diagnostics.RecordEvent("Camera", $"Could not read the supported API code list (result={result}); optional features will be attempted rather than skipped.");
                return FujiApiCapabilities.Unknown;
            }

            var count = Math.Min(apiCount, Math.Max(confirmedCount, 0));
            var codes = new int[count];
            for (int i = 0; i < count; i++)
            {
                codes[i] = Marshal.ReadInt32(buffer, i * sizeof(int));
            }

            var capabilities = new FujiApiCapabilities(codes);
            _diagnostics.RecordEvent("Camera", $"Camera advertises {capabilities.Count} API codes.");
            return capabilities;
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Camera", $"Error reading API code list: {ex.Message}");
            return FujiApiCapabilities.Unknown;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Applies the user's capture-quality preferences, skipping anything this body does not
    /// advertise. Every step reads the value back so the log records what the camera actually did.
    /// </summary>
    private void ApplyCaptureQualitySettings()
    {
        if (_session == null)
        {
            return;
        }

        ReportLongExposureNoiseReduction();

        var steps = FujiCaptureQualityPlan.Build(_settingsProvider.Settings, _apiCapabilities);
        if (steps.Count == 0)
        {
            _diagnostics.RecordEvent("Camera", "No capture-quality changes to apply.");
            return;
        }

        foreach (var step in steps)
        {
            try
            {
                var setResult = FujifilmSdkWrapper.XSDK_SetProp(_session.Handle, step.SetApiCode, 1, step.Value);
                if (setResult != FujifilmSdkWrapper.XSDK_COMPLETE)
                {
                    var error = FujifilmSdkWrapper.GetLastError(_session.Handle);
                    _diagnostics.RecordEvent("Camera",
                        $"{step.Name}: could not set {step.Describe(step.Value)} (result={setResult}, error=0x{error.ErrorCode:X}). Leaving the camera setting unchanged.");
                    continue;
                }

                _diagnostics.RecordEvent("Camera", $"{step.Name}: set to {step.Describe(step.Value)}.");
            }
            catch (Exception ex)
            {
                _diagnostics.RecordEvent("Camera", $"{step.Name}: error applying setting: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Sets a single capture property, reporting failure rather than throwing so sequence
    /// instructions can turn it into a message the user can act on.
    /// </summary>
    public bool TrySetCaptureProperty(int setApiCode, int value, string description, out string error)
    {
        error = string.Empty;

        if (_session == null)
        {
            error = $"Cannot set {description}: the camera is not connected.";
            return false;
        }

        if (!_apiCapabilities.Supports(setApiCode))
        {
            error = $"This camera does not support setting {description}.";
            return false;
        }

        try
        {
            var result = FujifilmSdkWrapper.XSDK_SetProp(_session.Handle, setApiCode, 1, value);
            if (result == FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                _diagnostics.RecordEvent("Camera", $"{description} set to 0x{value:X} by a sequence instruction.");
                return true;
            }

            var sdkError = FujifilmSdkWrapper.GetLastError(_session.Handle);
            error = $"The camera refused to set {description} (result={result}, error=0x{sdkError.ErrorCode:X}).";
            _diagnostics.RecordEvent("Camera", error);
            return false;
        }
        catch (Exception ex)
        {
            error = $"Error setting {description}: {ex.Message}";
            _diagnostics.RecordEvent("Camera", error);
            return false;
        }
    }

    /// <summary>
    /// Reads Long Exposure NR and warns when it is on. With LENR enabled the body shoots a matching
    /// dark after every long sub and subtracts it internally, doubling the frame time and applying
    /// calibration the user did not choose.
    /// </summary>
    private void ReportLongExposureNoiseReduction()
    {
        if (_session == null || !_apiCapabilities.Supports(FujifilmSdkWrapper.API_CODE_GetLongExposureNR))
        {
            return;
        }

        try
        {
            var result = FujifilmSdkWrapper.XSDK_GetLongExposureNR(_session.Handle, out var setting);
            if (result != FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                _diagnostics.RecordEvent("Camera", $"Could not read the Long Exposure NR setting (result={result}).");
                return;
            }

            _longExposureNoiseReductionOn = setting == FujifilmSdkWrapper.SDK_ON;
            _diagnostics.RecordEvent("Camera", $"Long Exposure NR is {FujiCaptureQualityPlan.DescribeOnOff(setting)}.");

            if (_longExposureNoiseReductionOn && !_settingsProvider.Settings.DisableLongExposureNR)
            {
                _diagnostics.RecordEvent("Camera",
                    "WARNING: Long Exposure NR is enabled. The camera will shoot a matching dark frame after every long exposure, " +
                    "roughly doubling the time per sub-exposure and subtracting a dark you did not choose. " +
                    "Enable 'Turn off the camera's Long Exposure NR' in the plugin options, or switch it off on the camera.");
            }
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Camera", $"Error reading Long Exposure NR: {ex.Message}");
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
            int bodyBatteryInfo = 0, gripBatteryInfo = 0, gripBattery2Info = 0;
            int bodyBatteryRatio = -1, gripBatteryRatio = 0, gripBattery2Ratio = 0;
            var result = FujifilmSdkWrapper.XSDK_ERROR;

            // Ask the camera which battery layout it implements rather than looking the model up in
            // a table. Always hand the SDK storage for the largest layout: supplying too few output
            // pointers to a variadic call is what would be unsafe, and varying only the declared
            // count never does that.
            _batteryParameterCount ??= FujifilmBatteryProtocol.Probe(candidate =>
            {
                var probeResult = FujifilmSdkWrapper.XSDK_GetProp_Battery8(
                    _session.Handle,
                    FujifilmSdkWrapper.API_CODE_CheckBatteryInfo,
                    candidate,
                    out var info, out var grip, out var grip2,
                    out var ratio, out var gripRatio, out var grip2Ratio,
                    out _, out _);

                if (probeResult != FujifilmSdkWrapper.XSDK_COMPLETE)
                {
                    return false;
                }

                bodyBatteryInfo = info;
                gripBatteryInfo = grip;
                gripBattery2Info = grip2;
                bodyBatteryRatio = ratio;
                gripBatteryRatio = gripRatio;
                gripBattery2Ratio = grip2Ratio;
                result = probeResult;
                _diagnostics.RecordEvent("Camera", $"Battery query accepted with {candidate} output values: bodyInfo=0x{info:X}, bodyRatio={ratio}");
                return true;
            });

            if (_batteryParameterCount == null)
            {
                _metadata.BatteryLevel = -1;
                _metadata.BatteryStatus = "Unavailable";
                _diagnostics.RecordEvent("Camera", "This camera did not accept any known battery query layout; battery reporting is unavailable.");
                return;
            }

            // Subsequent refreshes reuse the layout the camera already accepted.
            if (result != FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                result = FujifilmSdkWrapper.XSDK_GetProp_Battery8(
                    _session.Handle,
                    FujifilmSdkWrapper.API_CODE_CheckBatteryInfo,
                    _batteryParameterCount.Value,
                    out bodyBatteryInfo, out gripBatteryInfo, out gripBattery2Info,
                    out bodyBatteryRatio, out gripBatteryRatio, out gripBattery2Ratio,
                    out _, out _);
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
            FujifilmSdkWrapper.SDK_POWERCAPACITY_FULL_CHARGE => 100,
            FujifilmSdkWrapper.SDK_POWERCAPACITY_DC => 100,        // DC powered

            // Not battery levels: report unknown rather than letting the numeric fallback below
            // turn status code 0x0F into "15%".
            FujifilmSdkWrapper.SDK_POWERCAPACITY_CHARGING_ERROR => -1,
            FujifilmSdkWrapper.SDK_POWERCAPACITY_CAPACITY_UNKNOWN => -1,
            _ => -1
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

            // Aperture choices can depend on zoom position, so refresh zoom before asking the lens.
            if (_metadata.IsZoomLens)
            {
                RefreshZoomPosition();
            }

            _supportedApertureValues = QueryApertureValues(_metadata.CurrentZoomPosition);

            // Get current aperture (f-number * 100).
            var apertureResult = FujifilmSdkWrapper.XSDK_GetAperture(
                _session.Handle,
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

            RaisePropertyChanged(nameof(SupportsApertureControl));
            RaisePropertyChanged(nameof(AvailableApertures));
            RaisePropertyChanged(nameof(CurrentAperture));
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Camera", $"Lens metadata refresh error: {ex.Message}");
        }
    }

    private IReadOnlyList<int> QueryApertureValues(int zoomPosition)
    {
        if (_session == null)
        {
            return Array.Empty<int>();
        }

        var count = 0;
        try
        {
            var countResult = FujifilmSdkWrapper.XSDK_CapAperture(
                _session.Handle, zoomPosition, ref count, IntPtr.Zero);
            if (countResult != FujifilmSdkWrapper.XSDK_COMPLETE || count <= 0)
            {
                _diagnostics.RecordEvent("Camera",
                    $"CapAperture returned no values for zoom position {zoomPosition} (result={countResult}, count={count}).");
                return Array.Empty<int>();
            }

            var capacity = count;
            var buffer = Marshal.AllocHGlobal(checked(capacity * sizeof(int)));
            try
            {
                var dataResult = FujifilmSdkWrapper.XSDK_CapAperture(
                    _session.Handle, zoomPosition, ref count, buffer);
                if (dataResult != FujifilmSdkWrapper.XSDK_COMPLETE || count < 0 || count > capacity)
                {
                    _diagnostics.RecordEvent("Camera",
                        $"CapAperture value query failed (result={dataResult}, count={count}, capacity={capacity}).");
                    return Array.Empty<int>();
                }

                var reported = new int[count];
                for (var i = 0; i < count; i++)
                {
                    reported[i] = Marshal.ReadInt32(buffer, i * sizeof(int));
                }

                var manual = FujifilmApertureCatalog.SelectManualValues(reported);
                _diagnostics.RecordEvent("Camera",
                    $"Lens advertises {manual.Count} manual aperture value(s) at zoom position {zoomPosition}: " +
                    string.Join(", ", manual.Select(FujifilmApertureCatalog.Describe)));
                return manual;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Camera", $"Aperture capability query failed: {ex.Message}");
            return Array.Empty<int>();
        }
    }

    public bool TrySetAperture(double fNumber, out string error)
    {
        error = string.Empty;
        if (_session == null)
        {
            error = "Cannot set aperture: the camera is not connected.";
            return false;
        }

        // The aperture range can change with zoom position, and an electronic lens can be swapped
        // while the camera session remains open. Treat the lens as authoritative at command time.
        var zoomPosition = _metadata.CurrentZoomPosition;
        if (FujifilmSdkWrapper.XSDK_GetLensZoomPos(_session.Handle, out var currentZoomPosition) ==
            FujifilmSdkWrapper.XSDK_COMPLETE)
        {
            zoomPosition = currentZoomPosition;
            _metadata.CurrentZoomPosition = currentZoomPosition;
        }

        var currentApertureValues = QueryApertureValues(zoomPosition);
        if (!_supportedApertureValues.SequenceEqual(currentApertureValues))
        {
            _supportedApertureValues = currentApertureValues;
            RaisePropertyChanged(nameof(SupportsApertureControl));
            RaisePropertyChanged(nameof(AvailableApertures));
        }

        if (_supportedApertureValues.Count == 0)
        {
            error = "The attached lens reports no manually selectable apertures. Check that its aperture ring is set to A.";
            return false;
        }

        int requested;
        try
        {
            requested = FujifilmApertureCatalog.ToSdkValue(fNumber);
        }
        catch (Exception)
        {
            error = $"Aperture f/{fNumber:0.0#} is invalid.";
            return false;
        }

        if (!_supportedApertureValues.Contains(requested))
        {
            error = $"Aperture f/{fNumber:0.0#} is not advertised by the connected lens.";
            return false;
        }

        var originalAEMode = FujifilmSdkWrapper.XSDK_AE_OFF;
        var originalAperture = 0;
        var haveOriginalAperture = false;
        var changedAEMode = false;
        var apertureWriteCompleted = false;
        var apertureSet = false;
        try
        {
            haveOriginalAperture = FujifilmSdkWrapper.XSDK_GetAperture(
                _session.Handle, out originalAperture) == FujifilmSdkWrapper.XSDK_COMPLETE;

            var getAEModeResult = FujifilmSdkWrapper.XSDK_GetAEMode(_session.Handle, out originalAEMode);
            if (getAEModeResult != FujifilmSdkWrapper.XSDK_COMPLETE ||
                originalAEMode != FujifilmSdkWrapper.XSDK_AE_OFF)
            {
                var setAEModeResult = FujifilmSdkWrapper.XSDK_SetAEMode(
                    _session.Handle,
                    FujifilmSdkWrapper.XSDK_AE_OFF);
                if (setAEModeResult != FujifilmSdkWrapper.XSDK_COMPLETE)
                {
                    var sdkError = FujifilmSdkWrapper.GetLastError(_session.Handle);
                    error = $"Cannot enable Manual exposure mode for aperture control " +
                            $"(result={setAEModeResult}, error=0x{sdkError.ErrorCode:X}).";
                    _diagnostics.RecordEvent("Camera", error);
                    return false;
                }

                changedAEMode = getAEModeResult == FujifilmSdkWrapper.XSDK_COMPLETE;
                _lastAEModeCode = FujifilmSdkWrapper.XSDK_AE_OFF;
                _diagnostics.RecordEvent(
                    "Camera",
                    $"Changed AE mode from 0x{originalAEMode:X} to Manual for aperture control.");
            }

            var setResult = FujifilmSdkWrapper.XSDK_SetAperture(_session.Handle, requested);
            if (setResult != FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                var sdkError = FujifilmSdkWrapper.GetLastError(_session.Handle);
                error = $"The camera refused aperture {FujifilmApertureCatalog.Describe(requested)} " +
                        $"(result={setResult}, error=0x{sdkError.ErrorCode:X}). Check the lens aperture ring and exposure mode.";
                _diagnostics.RecordEvent("Camera", error);
                return false;
            }
            apertureWriteCompleted = true;

            var getResult = FujifilmSdkWrapper.XSDK_GetAperture(_session.Handle, out var actual);
            if (getResult != FujifilmSdkWrapper.XSDK_COMPLETE || actual != requested)
            {
                error = getResult != FujifilmSdkWrapper.XSDK_COMPLETE
                    ? $"Aperture was written, but the camera did not return a value for verification (result={getResult})."
                    : $"Requested {FujifilmApertureCatalog.Describe(requested)}, but the camera reports {FujifilmApertureCatalog.Describe(actual)}.";
                _diagnostics.RecordEvent("Camera", error);
                return false;
            }

            _metadata.CurrentAperture = FujifilmApertureCatalog.ToFNumber(actual);
            _diagnostics.RecordEvent("Camera", $"Aperture set and verified at {FujifilmApertureCatalog.Describe(actual)}.");
            RaisePropertyChanged(nameof(CurrentAperture));
            apertureSet = true;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Error setting aperture: {ex.Message}";
            _diagnostics.RecordEvent("Camera", error);
            return false;
        }
        finally
        {
            if (apertureWriteCompleted && !apertureSet && haveOriginalAperture && _session != null)
            {
                var restoreApertureResult = FujifilmSdkWrapper.XSDK_SetAperture(
                    _session.Handle, originalAperture);
                var verifyApertureResult = FujifilmSdkWrapper.XSDK_GetAperture(
                    _session.Handle, out var restoredAperture);
                var apertureRestored = restoreApertureResult == FujifilmSdkWrapper.XSDK_COMPLETE &&
                                       verifyApertureResult == FujifilmSdkWrapper.XSDK_COMPLETE &&
                                       restoredAperture == originalAperture;
                _diagnostics.RecordEvent(
                    "Camera",
                    apertureRestored
                        ? $"Restored aperture to {FujifilmApertureCatalog.Describe(originalAperture)} after verification failure."
                        : $"Failed to restore aperture {FujifilmApertureCatalog.Describe(originalAperture)} after verification failure " +
                          $"(set={restoreApertureResult}, get={verifyApertureResult}, actual={restoredAperture}).");
                if (!apertureRestored)
                {
                    error += " The previous aperture could not be restored; reconnect the camera and verify its settings.";
                }
            }

            // A successful aperture command must remain in Manual mode; restoring Program or a
            // priority mode would immediately hand aperture selection back to the camera. If the
            // write failed, undo the mode change so a failed command has no unrelated side effect.
            if (changedAEMode && !apertureSet && _session != null)
            {
                var restoreResult = FujifilmSdkWrapper.XSDK_SetAEMode(_session.Handle, originalAEMode);
                var verifyModeResult = FujifilmSdkWrapper.XSDK_GetAEMode(
                    _session.Handle, out var restoredAEMode);
                if (restoreResult == FujifilmSdkWrapper.XSDK_COMPLETE &&
                    verifyModeResult == FujifilmSdkWrapper.XSDK_COMPLETE &&
                    restoredAEMode == originalAEMode)
                {
                    _lastAEModeCode = originalAEMode;
                    _diagnostics.RecordEvent("Camera", $"Restored AE mode to 0x{originalAEMode:X} after aperture failure.");
                }
                else
                {
                    _diagnostics.RecordEvent("Camera",
                        $"Failed to restore AE mode 0x{originalAEMode:X} (set={restoreResult}, get={verifyModeResult}, actual=0x{restoredAEMode:X}).");
                    error += " The previous exposure mode could not be restored; reconnect the camera and verify its settings.";
                }
            }
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

        // XSDK_CapSensitivity reports the sensitivities available for the dynamic range currently set
        // on the camera; it takes no dynamic-range argument. DR is set to 100 during ConnectAsync.
        int count = 0;
        try
        {
            // Step 1: Get count
            var countResult = FujifilmSdkWrapper.XSDK_CapSensitivity(_session.Handle, ref count, IntPtr.Zero);
            
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
                var dataResult = FujifilmSdkWrapper.XSDK_CapSensitivity(_session.Handle, ref count, buffer);
                
                if (dataResult != FujifilmSdkWrapper.XSDK_COMPLETE)
                {
                    _diagnostics.RecordEvent("Camera", $"QuerySensitivityValues: Failed to get sensitivity data (Result={dataResult}). Using fallback ISO values.");
                    return BuildFallbackIsoArray();
                }

                var reported = new List<int>(count);
                for (int i = 0; i < count; i++)
                {
                    reported.Add(Marshal.ReadInt32(buffer, i * sizeof(int)));
                }

                // The list mixes real sensitivities with auto-ISO modes; keep only the former.
                var sensitivities = FujifilmSensitivityCatalog.SelectFixedSensitivities(reported, out var autoModes);

                if (sensitivities.Count == 0)
                {
                    _diagnostics.RecordEvent("Camera", $"QuerySensitivityValues: the camera reported {count} entries but none were fixed sensitivities. Using fallback ISO values.");
                    return BuildFallbackIsoArray();
                }

                _diagnostics.RecordEvent("Camera",
                    $"QuerySensitivityValues: {sensitivities.Count} fixed ISO values from the camera ({sensitivities.Min()}-{sensitivities.Max()})" +
                    (autoModes > 0 ? $"; ignored {autoModes} auto-ISO mode(s)." : "."));
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

        // XSDK_CapShutterSpeed's bulb flag is not trustworthy: it came back "not capable" on every
        // probe of every camera in the diagnostics logs, including sessions that went on to run a
        // successful bulb exposure moments later. Every model this plugin supports has a mechanical
        // bulb mode, which is what DefaultBulbCapable records, so treat the model configuration as
        // authoritative when the SDK denies bulb support.
        _bulbCapable = FujifilmBulbCapability.Resolve(sdkBulbCapable, _config?.DefaultBulbCapable);
        _diagnostics.RecordEvent("Camera", $"Bulb capability: SDK={sdkBulbCapable}, Config={_config?.DefaultBulbCapable}, Final={_bulbCapable}");

        if (count == 0)
        {
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
            // A timed exposure is running. XSDK_RELEASE_CANCEL is documented as "Long time-exposure
            // cancelled while in progress", which is what lets a sequence abort a long sub for
            // clouds or a meridian flip instead of waiting it out.
            TryCancelTimedExposure();
        }

        RefreshBufferCapacity();
        RefreshOperatingState();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Asks the camera to abandon a timed exposure that is still running. Not every body supports
    /// the release mode, so a refusal is logged and the caller simply waits the exposure out as
    /// before rather than failing.
    /// </summary>
    private void TryCancelTimedExposure()
    {
        if (_session == null)
        {
            return;
        }

        try
        {
            var result = FujifilmSdkWrapper.XSDK_Release(
                _session.Handle, FujifilmSdkWrapper.XSDK_RELEASE_CANCEL, IntPtr.Zero, out var status);

            if (result == FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                _diagnostics.RecordEvent("Camera", $"Cancelled the in-progress timed exposure (status={status}).");

                // A cancelled exposure still lands a frame in the camera buffer: the body finalises
                // whatever it had already collected. Leaving it there would hand it to the next
                // exposure as if it were that exposure's frame, so discard it now.
                DrainCameraBuffer("after cancelling an exposure");
                return;
            }

            var error = FujifilmSdkWrapper.GetLastError(_session.Handle);
            _diagnostics.RecordEvent("Camera",
                $"This camera refused XSDK_RELEASE_CANCEL (result={result}, error=0x{error.ErrorCode:X}); the exposure will run to completion.");
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Camera", $"Error cancelling the timed exposure: {ex.Message}");
        }
    }

    /// <summary>
    /// Discards any frames sitting in the camera's buffer.
    /// </summary>
    /// <remarks>
    /// A frame left behind by an aborted or timed-out exposure is handed to the next download as if
    /// it belonged to that exposure, so every subsequent sub-exposure in the sequence is off by one.
    /// Cancelling a timed exposure was measured to leave exactly one such frame behind.
    /// </remarks>
    private void DrainCameraBuffer(string reason)
    {
        if (_session == null)
        {
            return;
        }

        const int maxFramesToDiscard = 8;
        var discarded = 0;

        try
        {
            for (var attempt = 0; attempt < maxFramesToDiscard; attempt++)
            {
                var capacityResult = FujifilmSdkWrapper.XSDK_GetBufferCapacity(
                    _session.Handle, out var pendingFrames, out _);
                if (capacityResult != FujifilmSdkWrapper.XSDK_COMPLETE || pendingFrames <= 0)
                {
                    break;
                }

                if (FujifilmSdkWrapper.XSDK_DeleteImage(_session.Handle) != FujifilmSdkWrapper.XSDK_COMPLETE)
                {
                    break;
                }

                discarded++;
            }

            if (discarded > 0)
            {
                _diagnostics.RecordEvent("Camera", $"Discarded {discarded} stale frame(s) from the camera buffer {reason}.");
            }
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Camera", $"Error draining the camera buffer {reason}: {ex.Message}");
        }
        finally
        {
            RefreshBufferCapacity();
        }
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
                // Only the low byte of lFormat is the image format; bits 0x0F00 carry the camera's
                // rotation (RAW_90 = 0x0601, RAW_180 = 0x0301, RAW_270 = 0x0801). Comparing the raw
                // value discarded and deleted any frame captured with the body rotated.
                if ((info.lFormat & 0xFF) != FujifilmSdkWrapper.XSDK_IMAGEFORMAT_RAW)
                {
                    _diagnostics.RecordEvent("Camera", $"Discarding non-RAW image from camera (format=0x{info.lFormat:X}, bytes={info.lDataSize}). Set IMAGE QUALITY to RAW or RAW+JPEG.");
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
                var readSucceeded = false;
                try
                {
                    var readResult = FujifilmSdkWrapper.XSDK_ReadImage(_session.Handle, handle.AddrOfPinnedObject(), (uint)buffer.Length);
                    FujifilmSdkWrapper.CheckResult(_session.Handle, readResult, nameof(FujifilmSdkWrapper.XSDK_ReadImage));
                    readSucceeded = true;

                    _diagnostics.RecordEvent("Camera", $"Downloaded RAW frame {info.lImagePixWidth}x{info.lImagePixHeight} bytes={buffer.Length}");
                    return new RawCaptureResult(buffer, info.lImagePixWidth, info.lImagePixHeight, info.lFormat, info.lImageBitDepth, 0, 0, 0.0, 0);
                }
                finally
                {
                    handle.Free();

                    // XSDK_ReadImage takes the frame from the top of the buffer and deletes it, so
                    // deleting again after a successful read either fails harmlessly or removes the
                    // next frame. Only clean up when the read did not complete.
                    if (!readSucceeded)
                    {
                        var deleteResult = FujifilmSdkWrapper.XSDK_DeleteImage(_session.Handle);
                        if (deleteResult != FujifilmSdkWrapper.XSDK_COMPLETE)
                        {
                            _diagnostics.RecordEvent("Camera", $"XSDK_DeleteImage after failed read returned {deleteResult}");
                        }
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
        _registry.RegisterCamera(null);
        _batteryParameterCount = null;

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
            _supportedApertureValues = Array.Empty<int>();
            _bufferShootCapacity = 0;
            _bufferTotalCapacity = 0;
            RaisePropertyChanged(nameof(IsConnected));
            RaisePropertyChanged(nameof(SupportsApertureControl));
            RaisePropertyChanged(nameof(AvailableApertures));
            RaisePropertyChanged(nameof(CurrentAperture));
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
