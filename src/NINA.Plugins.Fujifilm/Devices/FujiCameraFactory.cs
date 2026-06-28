using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces;
using NINA.Image.Interfaces;
using NINA.Plugins.Fujifilm.Configuration;
using NINA.Plugins.Fujifilm.Configuration.Loading;
using NINA.Plugins.Fujifilm.Devices.LiveView;
using NINA.Plugins.Fujifilm.Diagnostics;
using NINA.Plugins.Fujifilm.Interop;
using NINA.Profile.Interfaces;
using NINA.Plugins.Fujifilm.Settings;

namespace NINA.Plugins.Fujifilm.Devices;

[Export(typeof(IFujiCameraFactory))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class FujiCameraFactory : IFujiCameraFactory
{
    private readonly IFujifilmInterop _interop;
    private readonly IFujifilmDiagnosticsService _diagnostics;
    private readonly FujiCamera _camera;
    private readonly ILibRawAdapter _libRaw;
    private readonly IProfileService _profileService;
    private readonly IExposureDataFactory _exposureDataFactory;
    private readonly IFujiSettingsProvider _settingsProvider;
    private readonly ExportFactory<ILiveViewService> _liveViewServiceFactory;

    [ImportingConstructor]
    public FujiCameraFactory(
        IFujifilmInterop interop,
        IFujifilmDiagnosticsService diagnostics,
        FujiCamera camera,
        ILibRawAdapter libRaw,
        IFujiSettingsProvider settingsProvider,
        IProfileService profileService,
        IExposureDataFactory exposureDataFactory,
        ExportFactory<ILiveViewService> liveViewServiceFactory
        )
    {
        _interop = interop;
        _diagnostics = diagnostics;
        _camera = camera;
        _libRaw = libRaw;
        _settingsProvider = settingsProvider;
        _profileService = profileService;
        _exposureDataFactory = exposureDataFactory;
        _liveViewServiceFactory = liveViewServiceFactory;
    }

    public async Task<IReadOnlyList<FujifilmCameraDescriptor>> GetAvailableCamerasAsync(CancellationToken cancellationToken)
    {
        var cameras = await _interop.DetectCamerasAsync(cancellationToken).ConfigureAwait(false);
        var descriptors = new List<FujifilmCameraDescriptor>();

        foreach (var info in cameras)
        {
            if (CameraModelRules.IsKnownUnsupportedStillCamera(info.ProductName))
            {
                _diagnostics.RecordEvent("Factory", $"Skipping unsupported Fujifilm SDK device '{info.ProductName}'. The plugin only supports still-camera RAF capture workflows.");
                continue;
            }

            descriptors.Add(new FujifilmCameraDescriptor(info.ProductName, info.DeviceId));
        }

        return descriptors;
    }

    public FujiCamera CreateCamera()
    {
        return _camera;
    }

    public ICamera CreateGenericCamera(FujifilmCameraDescriptor descriptor)
    {
        // The Fujifilm SDK is process-global and the plugin supports one active camera session.
        // Each NINA adapter gets an independent live-view service while sharing that session.
        var liveViewExport = _liveViewServiceFactory.CreateExport();
        var liveViewService = liveViewExport.Value;

        var sdkAdapter = new FujiCameraSdkAdapter(
            _camera,
            descriptor,
            _diagnostics,
            _libRaw,
            _settingsProvider,
            _profileService,
            liveViewService,
            Disposable.Empty,
            Disposable.Empty);

        // Wrap the IGenericCameraSDK adapter in NINA's GenericCamera to implement ICamera
        var genericCamera = new GenericCamera(
            descriptor.DisplayName,  // Camera name
            descriptor.DisplayName,  // Camera ID/description
            "Fujifilm Camera Plugin",
            "3.0.2",
            true,  // hasBattery = true so NINA shows battery in equipment panel
            sdkAdapter,
            _profileService,
            _exposureDataFactory);

        // Wrap in our custom camera that provides dynamic lens info, auto-refresh, and proper live view
        return new FujiGenericCamera(genericCamera, sdkAdapter);
    }

    public async Task<FujiCameraCapabilities> GetCapabilitiesAsync(FujifilmCameraDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (_camera.IsConnected)
        {
            if (!string.Equals(_camera.ConnectedDeviceId, descriptor.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Capabilities cannot be loaded for {descriptor.DisplayName} while another Fujifilm camera is connected.");
            }

            return _camera.GetCapabilitiesSnapshot();
        }

        try
        {
            await _camera.ConnectAsync(descriptor, cancellationToken).ConfigureAwait(false);
            return _camera.GetCapabilitiesSnapshot();
        }
        finally
        {
            if (_camera.IsConnected)
            {
                await _camera.DisconnectAsync().ConfigureAwait(false);
            }
        }
    }

    private class Disposable : IDisposable
    {
        public static readonly IDisposable Empty = new Disposable();
        public void Dispose() { }
    }
}
