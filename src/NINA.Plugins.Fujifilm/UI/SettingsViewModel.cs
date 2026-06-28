using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NINA.Plugins.Fujifilm.Diagnostics;
using NINA.Plugins.Fujifilm.Devices;
using NINA.Plugins.Fujifilm.Settings;

namespace NINA.Plugins.Fujifilm.UI;

[Export(typeof(SettingsViewModel))]
[PartCreationPolicy(CreationPolicy.NonShared)]
public partial class SettingsViewModel : ObservableObject
{
    private readonly IFujiSettingsProvider _settingsProvider;
    private readonly IFujifilmDiagnosticsService _diagnostics;
    private readonly IFujiCameraFactory _cameraFactory;
    private CancellationTokenSource? _capabilitiesCts;

    [ObservableProperty]
    private IReadOnlyList<FujifilmCameraDescriptor> _availableCameras = Array.Empty<FujifilmCameraDescriptor>();

    [ObservableProperty]
    private FujifilmCameraDescriptor? _selectedCamera;

    [ObservableProperty]
    private FujiCameraCapabilities? _cameraCapabilities;

    [ObservableProperty]
    private bool _isLoadingCapabilities;

    [ObservableProperty]
    private string _capabilitiesErrorMessage = string.Empty;

    [ImportingConstructor]
    public SettingsViewModel(IFujiSettingsProvider settingsProvider, IFujifilmDiagnosticsService diagnostics, IFujiCameraFactory cameraFactory)
    {
        _settingsProvider = settingsProvider;
        _diagnostics = diagnostics;
        _cameraFactory = cameraFactory;
        Settings = settingsProvider.Settings;
        _ = RefreshCamerasAsync();
    }

    public FujiSettings Settings { get; }

    /// <summary>
    /// Battery status text for the status panel.
    /// </summary>
    public string BatteryStatusText
    {
        get
        {
            if (CameraCapabilities == null)
                return "Not connected";

            var level = CameraCapabilities.Metadata.BatteryLevel;
            var status = CameraCapabilities.Metadata.BatteryStatus;

            if (level >= 0)
                return $"{level}% ({status})";

            return string.IsNullOrEmpty(status) ? "Unknown" : status;
        }
    }

    /// <summary>
    /// Lens status text for the status panel.
    /// </summary>
    public string LensStatusText
    {
        get
        {
            if (CameraCapabilities == null)
                return "Not connected";

            var lens = CameraCapabilities.Metadata.LensProductName;
            if (string.IsNullOrWhiteSpace(lens))
                return "No lens detected";

            var ois = CameraCapabilities.Metadata.HasImageStabilization ? " [OIS]" : "";
            return $"{lens}{ois}";
        }
    }

    /// <summary>
    /// Whether status information is available (camera connected and capabilities loaded).
    /// </summary>
    public bool IsStatusAvailable => CameraCapabilities != null;

    public string CapabilitiesSummary
    {
        get
        {
            if (IsLoadingCapabilities)
            {
                return "Loading capabilities...";
            }

            if (!string.IsNullOrEmpty(CapabilitiesErrorMessage))
            {
                return $"Error: {CapabilitiesErrorMessage}";
            }

            if (CameraCapabilities == null)
            {
                return "Select a camera and click 'Load Capabilities' to view camera info.";
            }

            var caps = CameraCapabilities;
            var metadata = caps.Metadata;
            var isoSummary = caps.IsoValues.Count switch
            {
                0 => "n/a",
                1 => caps.IsoValues[0].ToString(),
                _ => $"{caps.IsoValues[0]} – {caps.IsoValues[caps.IsoValues.Count - 1]}"
            };

            var exposureSummary = caps.MaxExposureSeconds > 0
                ? $"{caps.MinExposureSeconds:0.###} s – {caps.MaxExposureSeconds:0.###} s"
                : "n/a";
            var timedExposureSummary = caps.TimedExposureMaxSeconds > 0
                ? $"{caps.MinExposureSeconds:0.###} s – {caps.TimedExposureMaxSeconds:0.###} s"
                : exposureSummary;
            var bulbExposureSummary = caps.BulbExposureMaxSeconds > 0
                ? $"{caps.TimedExposureMaxSeconds:0.###} s – {caps.BulbExposureMaxSeconds:0.###} s"
                : "n/a";

            var bufferSummary = caps.BufferTotalCapacity > 0
                ? $"{caps.BufferShootCapacity}/{caps.BufferTotalCapacity}"
                : "n/a";

            var resolutionSummary = caps.SensorWidth > 0 && caps.SensorHeight > 0
                ? $"{caps.SensorWidth} x {caps.SensorHeight}"
                : "n/a";

            var bulbSummary = caps.SupportsBulb ? "Yes" : "No";

            var stateSummary = $"Mode: {caps.ModeCode}  AE: {caps.AEModeCode}  DR: {caps.DynamicRangeCode}";
            var errorSummary = (caps.LastSdkErrorCode != 0 || caps.LastApiErrorCode != 0)
                ? $"{caps.LastSdkErrorCode} (API {caps.LastApiErrorCode})"
                : "None";

            var bodySummary = string.IsNullOrWhiteSpace(metadata.ProductName)
                ? "Body: (unknown)"
                : $"Body: {metadata.ProductName}  FW: {metadata.FirmwareVersion}";

            var identifiedModel = $"{metadata.ProductName} {SelectedCamera?.DisplayName}";
            var compatibilitySummary = identifiedModel.Contains("X-T2", StringComparison.OrdinalIgnoreCase)
                ? "Compatibility: Legacy/experimental (FF0002API); hardware validation required"
                : "Compatibility: Configured Shooting SDK model";

            // Enhanced lens info
            string lensSummary;
            if (string.IsNullOrWhiteSpace(metadata.LensProductName))
            {
                lensSummary = "Lens: (none detected)";
            }
            else
            {
                lensSummary = $"Lens: {metadata.LensProductName}";
                if (!string.IsNullOrWhiteSpace(metadata.LensSerialNumber))
                {
                    lensSummary += $"  SN: {metadata.LensSerialNumber}";
                }
                if (metadata.HasImageStabilization)
                {
                    lensSummary += " [OIS]";
                }
            }

            // Aperture info
            var apertureSummary = metadata.CurrentAperture > 0
                ? $"Aperture: f/{metadata.CurrentAperture:F1}"
                : "Aperture: n/a";

            // Battery info
            var batterySummary = metadata.BatteryLevel >= 0
                ? $"Battery: {metadata.BatteryLevel}% ({metadata.BatteryStatus})"
                : "Battery: n/a";

            return $"{compatibilitySummary}\n" +
                   $"Resolution: {resolutionSummary}\n" +
                   $"ISO Range: {isoSummary}\n" +
                   $"Exposure (Timed): {timedExposureSummary}\n" +
                   $"Exposure (Bulb): {bulbExposureSummary}\n" +
                   $"Supports Bulb: {bulbSummary}\n" +
                   $"Buffer Capacity: {bufferSummary}\n" +
                   $"{stateSummary}\n" +
                   $"Last Error: {errorSummary}\n" +
                   $"{bodySummary}\n" +
                   $"{lensSummary}\n" +
                   $"{apertureSummary}\n" +
                   $"{batterySummary}";
        }
    }

    [RelayCommand]
    private void Save()
    {
        _settingsProvider.Save();
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        await _diagnostics.ExportDiagnosticsAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task RefreshCamerasAsync()
    {
        try
        {
            var cameras = await _cameraFactory.GetAvailableCamerasAsync(CancellationToken.None).ConfigureAwait(false);
            AvailableCameras = cameras;
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Settings", $"Refresh failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task LoadCapabilitiesAsync()
    {
        await LoadCapabilitiesInternalAsync().ConfigureAwait(false);
    }

    partial void OnSelectedCameraChanged(FujifilmCameraDescriptor? value)
    {
        CapabilitiesErrorMessage = string.Empty;
        _ = LoadCapabilitiesInternalAsync();
    }

    partial void OnCameraCapabilitiesChanged(FujiCameraCapabilities? value)
    {
        OnPropertyChanged(nameof(CapabilitiesSummary));
        OnPropertyChanged(nameof(BatteryStatusText));
        OnPropertyChanged(nameof(LensStatusText));
        OnPropertyChanged(nameof(IsStatusAvailable));
    }

    partial void OnIsLoadingCapabilitiesChanged(bool value)
    {
        OnPropertyChanged(nameof(CapabilitiesSummary));
    }

    partial void OnCapabilitiesErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(CapabilitiesSummary));
    }

    private async Task LoadCapabilitiesInternalAsync()
    {
        var descriptor = SelectedCamera;
        if (descriptor == null)
        {
            CameraCapabilities = null;
            CapabilitiesErrorMessage = string.Empty;
            return;
        }

        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _capabilitiesCts, cts);
        previous?.Cancel();
        previous?.Dispose();

        IsLoadingCapabilities = true;
        CapabilitiesErrorMessage = string.Empty;

        try
        {
            var capabilities = await _cameraFactory.GetCapabilitiesAsync(descriptor, cts.Token).ConfigureAwait(false);
            if (ReferenceEquals(_capabilitiesCts, cts))
            {
                CameraCapabilities = capabilities;
            }
        }
        catch (OperationCanceledException)
        {
            // Swallow cancellations triggered by selection changes.
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_capabilitiesCts, cts))
            {
                _diagnostics.RecordEvent("Settings", $"Capabilities load failed: {ex.Message}");
                CapabilitiesErrorMessage = ex.Message;
                CameraCapabilities = null;
            }
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _capabilitiesCts, null, cts) == cts)
            {
                IsLoadingCapabilities = false;
            }

            cts.Dispose();
        }
    }
}
