using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using NINA.Core.Enum;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Plugins.Fujifilm.Imaging;
using NINA.Profile.Interfaces;

namespace NINA.Plugins.Fujifilm.Devices;

/// <summary>
/// Custom camera wrapper that provides dynamic battery/lens info display in NINA's camera panel.
/// Wraps GenericCamera but overrides Description to include real-time lens info.
/// </summary>
internal sealed class FujiGenericCamera : ICamera
{
    private readonly GenericCamera _innerCamera;
    private readonly FujiCameraSdkAdapter _sdkAdapter;
    private readonly System.Timers.Timer _refreshTimer;
    private string _lensInfo = string.Empty;
    private int _lastBatteryLevel = -1;
    private bool _lastHasBattery;

    public FujiGenericCamera(
        GenericCamera innerCamera,
        FujiCameraSdkAdapter sdkAdapter)
    {
        _innerCamera = innerCamera;
        _sdkAdapter = sdkAdapter;

        // Subscribe to inner camera's property changes
        _innerCamera.PropertyChanged += OnInnerPropertyChanged;

        // Set up refresh timer for battery/lens updates (every 30 seconds)
        _refreshTimer = new System.Timers.Timer(30000);
        _refreshTimer.Elapsed += OnRefreshTimerElapsed;
        _refreshTimer.AutoReset = true;
    }

    private void OnInnerPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        // Forward property changes and add our own
        PropertyChanged?.Invoke(this, e);

        // When connected state changes, update our info
        if (e.PropertyName == nameof(Connected) && Connected)
        {
            RefreshCameraInfo();
            _refreshTimer.Start();
        }
        else if (e.PropertyName == nameof(Connected) && !Connected)
        {
            _refreshTimer.Stop();
            _lensInfo = string.Empty;
            _lastBatteryLevel = -1;
            _lastHasBattery = false;
            RaisePropertyChanged(nameof(Description));
            RaisePropertyChanged(nameof(BatteryLevel));
            RaisePropertyChanged(nameof(HasBattery));
        }
    }

    private void OnRefreshTimerElapsed(object sender, ElapsedEventArgs e)
    {
        RefreshCameraInfo();
    }

    private void RefreshCameraInfo()
    {
        if (!Connected) return;

        try
        {
            // Get current battery level
            var battery = _sdkAdapter.GetBatteryLevel();
            if (battery != _lastBatteryLevel)
            {
                _lastBatteryLevel = battery;
                RaisePropertyChanged(nameof(BatteryLevel));
            }

            var hasBattery = battery >= 0;
            if (hasBattery != _lastHasBattery)
            {
                _lastHasBattery = hasBattery;
                RaisePropertyChanged(nameof(HasBattery));
            }

            // Get lens info from the camera's capabilities
            var caps = GetCapabilitiesSnapshot();
            var newLensInfo = caps?.Metadata?.LensProductName ?? string.Empty;
            if (newLensInfo != _lensInfo)
            {
                _lensInfo = newLensInfo;
                RaisePropertyChanged(nameof(Description));
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"Fujifilm camera status refresh failed: {ex.Message}");
        }
    }

    private FujiCameraCapabilities GetCapabilitiesSnapshot()
    {
        return _sdkAdapter.CurrentCapabilities;
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void RaisePropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    // Override Description to include lens info
    public string Description
    {
        get
        {
            var baseDesc = _innerCamera.Description;
            if (!string.IsNullOrWhiteSpace(_lensInfo))
            {
                return $"{baseDesc} | Lens: {_lensInfo}";
            }
            return baseDesc;
        }
    }

    // Battery level - delegate to SDK adapter for fresh value
    public int BatteryLevel => Connected ? _sdkAdapter.GetBatteryLevel() : -1;
    public bool HasBattery => Connected && BatteryLevel >= 0;

    // Delegate all other ICamera members to inner camera
    public string Id => _innerCamera.Id;
    public string Name => _innerCamera.Name;
    public string DisplayName => _innerCamera.DisplayName;
    public string Category => _innerCamera.Category;
    public bool Connected => _innerCamera.Connected;
    public bool HasShutter => _innerCamera.HasShutter;
    public double Temperature => _innerCamera.Temperature;
    public double TemperatureSetPoint { get => _innerCamera.TemperatureSetPoint; set => _innerCamera.TemperatureSetPoint = value; }
    public short BinX { get => _innerCamera.BinX; set => _innerCamera.BinX = value; }
    public short BinY { get => _innerCamera.BinY; set => _innerCamera.BinY = value; }
    public string SensorName => _innerCamera.SensorName;
    public SensorType SensorType => _innerCamera.SensorType;
    public short BayerOffsetX => _innerCamera.BayerOffsetX;
    public short BayerOffsetY => _innerCamera.BayerOffsetY;
    // Override CameraXSize/CameraYSize during live view to return live view dimensions
    public int CameraXSize => _sdkAdapter.IsLiveViewActive && _sdkAdapter.LiveViewWidth > 0
        ? _sdkAdapter.LiveViewWidth
        : _innerCamera.CameraXSize;
    public int CameraYSize => _sdkAdapter.IsLiveViewActive && _sdkAdapter.LiveViewHeight > 0
        ? _sdkAdapter.LiveViewHeight
        : _innerCamera.CameraYSize;
    public double ExposureMin => _innerCamera.ExposureMin;
    public double ExposureMax => _innerCamera.ExposureMax;
    public short MaxBinX => _innerCamera.MaxBinX;
    public short MaxBinY => _innerCamera.MaxBinY;
    public double PixelSizeX => _innerCamera.PixelSizeX;
    public double PixelSizeY => _innerCamera.PixelSizeY;
    public bool CanSetTemperature => _innerCamera.CanSetTemperature;
    public bool CoolerOn { get => _innerCamera.CoolerOn; set => _innerCamera.CoolerOn = value; }
    public double CoolerPower => _innerCamera.CoolerPower;
    public bool HasDewHeater => _innerCamera.HasDewHeater;
    public bool DewHeaterOn { get => _innerCamera.DewHeaterOn; set => _innerCamera.DewHeaterOn = value; }
    public CameraStates CameraState => _innerCamera.CameraState;
    public int Offset { get => _innerCamera.Offset; set => _innerCamera.Offset = value; }
    public int USBLimit { get => _innerCamera.USBLimit; set => _innerCamera.USBLimit = value; }
    public int USBLimitMax => _innerCamera.USBLimitMax;
    public int USBLimitMin => _innerCamera.USBLimitMin;
    public int USBLimitStep => _innerCamera.USBLimitStep;
    public bool CanSetOffset => _innerCamera.CanSetOffset;
    public int OffsetMin => _innerCamera.OffsetMin;
    public int OffsetMax => _innerCamera.OffsetMax;
    public bool CanSetUSBLimit => _innerCamera.CanSetUSBLimit;
    public bool CanGetGain => _innerCamera.CanGetGain;
    public bool CanSetGain => _innerCamera.CanSetGain;
    public int GainMax => _innerCamera.GainMax;
    public int GainMin => _innerCamera.GainMin;
    public int Gain { get => _innerCamera.Gain; set => _innerCamera.Gain = value; }
    public IList<int> Gains => _innerCamera.Gains;
    public AsyncObservableCollection<BinningMode> BinningModes => _innerCamera.BinningModes;
    public bool HasSetupDialog => _innerCamera.HasSetupDialog;
    public IList<string> SupportedActions => _innerCamera.SupportedActions;
    public bool CanSubSample => _innerCamera.CanSubSample;
    public bool EnableSubSample { get => _innerCamera.EnableSubSample; set => _innerCamera.EnableSubSample = value; }
    public int SubSampleX { get => _innerCamera.SubSampleX; set => _innerCamera.SubSampleX = value; }
    public int SubSampleY { get => _innerCamera.SubSampleY; set => _innerCamera.SubSampleY = value; }
    public int SubSampleWidth { get => _innerCamera.SubSampleWidth; set => _innerCamera.SubSampleWidth = value; }
    public int SubSampleHeight { get => _innerCamera.SubSampleHeight; set => _innerCamera.SubSampleHeight = value; }
    // Override to enable live view - our SDK adapter supports it
    public bool CanShowLiveView => true;
    public bool LiveViewEnabled { get => _innerCamera.LiveViewEnabled; set => _innerCamera.LiveViewEnabled = value; }
    public int BitDepth => _innerCamera.BitDepth;
    public IList<string> ReadoutModes => _innerCamera.ReadoutModes;
    public short ReadoutMode { get => _innerCamera.ReadoutMode; set => _innerCamera.ReadoutMode = value; }
    public short ReadoutModeForSnapImages { get => _innerCamera.ReadoutModeForSnapImages; set => _innerCamera.ReadoutModeForSnapImages = value; }
    public short ReadoutModeForNormalImages { get => _innerCamera.ReadoutModeForNormalImages; set => _innerCamera.ReadoutModeForNormalImages = value; }
    public double ElectronsPerADU => _innerCamera.ElectronsPerADU;
    private double _liveViewExposureTime = 1.0;
    public double LiveViewExposureTime { get => _liveViewExposureTime; set => _liveViewExposureTime = value; }
    public string Action(string actionName, string actionParameters) => _innerCamera.Action(actionName, actionParameters);
    public string SendCommandString(string command, bool raw = true) => _innerCamera.SendCommandString(command, raw);
    public bool SendCommandBool(string command, bool raw = true) => _innerCamera.SendCommandBool(command, raw);
    public void SendCommandBlind(string command, bool raw = true) => _innerCamera.SendCommandBlind(command, raw);
    public void SetupDialog() => _innerCamera.SetupDialog();
    public Task<bool> Connect(CancellationToken token) => _innerCamera.Connect(token);
    public void Disconnect() => _innerCamera.Disconnect();
    public Task WaitUntilExposureIsReady(CancellationToken token) => _innerCamera.WaitUntilExposureIsReady(token);
    public void StopExposure() => _innerCamera.StopExposure();
    public void AbortExposure() => _innerCamera.AbortExposure();
    public void StartExposure(CaptureSequence sequence) => _innerCamera.StartExposure(sequence);
    public async Task<IExposureData> DownloadExposure(CancellationToken token)
    {
        var exposure = await _innerCamera.DownloadExposure(token).ConfigureAwait(false);
        if (exposure == null)
        {
            throw new InvalidOperationException(_sdkAdapter.LastExposureError ?? "N.I.N.A. did not receive image data from the Fujifilm camera.");
        }

        var package = _sdkAdapter.LastImagePackage;
        if (package != null)
        {
            FujiMetadataHeaderWriter.Apply(exposure.MetaData, package.FitsKeywords);
        }

        return exposure;
    }
    public void StartLiveView(CaptureSequence sequence) => _innerCamera.StartLiveView(sequence);
    public Task<IExposureData> DownloadLiveView(CancellationToken token) => _innerCamera.DownloadLiveView(token);
    public void StopLiveView() => _innerCamera.StopLiveView();
    public void SetBinning(short x, short y) => _innerCamera.SetBinning(x, y);
    public void UpdateSubSampleArea() => _innerCamera.UpdateSubSampleArea();
    public string DriverInfo => _innerCamera.DriverInfo;
    public string DriverVersion => _innerCamera.DriverVersion;
}
