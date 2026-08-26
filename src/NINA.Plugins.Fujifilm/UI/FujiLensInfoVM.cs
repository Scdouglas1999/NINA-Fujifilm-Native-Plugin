using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Collections.Generic;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Plugins.Fujifilm.Devices;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;

namespace NINA.Plugins.Fujifilm.UI;

/// <summary>
/// Minimal dockable panel that displays Fujifilm lens info in the imaging tab.
/// Battery is shown in NINA's built-in camera panel; this shows the lens.
/// </summary>
[Export(typeof(IDockableVM))]
public class FujiLensInfoVM : DockableVM
{
    private readonly FujiCamera _camera;
    private string _lensText = "Not connected";
    private bool _isConnected;
    private IReadOnlyList<double> _availableApertures = Array.Empty<double>();
    private double _selectedAperture;
    private bool _canControlAperture;
    private string _apertureError = string.Empty;
    private bool _refreshing;

    public string LensText
    {
        get => _lensText;
        set { _lensText = value; RaisePropertyChanged(); }
    }

    public bool IsConnected
    {
        get => _isConnected;
        set { _isConnected = value; RaisePropertyChanged(); }
    }

    public IReadOnlyList<double> AvailableApertures
    {
        get => _availableApertures;
        private set { _availableApertures = value; RaisePropertyChanged(); }
    }

    public bool CanControlAperture
    {
        get => _canControlAperture;
        private set { _canControlAperture = value; RaisePropertyChanged(); }
    }

    public string ApertureError
    {
        get => _apertureError;
        private set { _apertureError = value; RaisePropertyChanged(); }
    }

    public double SelectedAperture
    {
        get => _selectedAperture;
        set
        {
            if (_refreshing || Math.Abs(_selectedAperture - value) < 0.0001)
            {
                return;
            }

            if (_camera.TrySetAperture(value, out var error))
            {
                _selectedAperture = _camera.CurrentAperture;
                ApertureError = string.Empty;
            }
            else
            {
                ApertureError = error;
            }

            RaisePropertyChanged();
        }
    }

    [ImportingConstructor]
    public FujiLensInfoVM(IProfileService profileService, FujiCamera camera) : base(profileService)
    {
        _camera = camera;
        Title = "Fuji Lens";

        // Subscribe to camera connection changes
        _camera.PropertyChanged += OnCameraPropertyChanged;

        // Initial refresh
        RefreshLensInfo();
    }

    public override bool IsTool => true;  // Shows in tool pane (right side)

    private void OnCameraPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FujiCamera.IsConnected) ||
            e.PropertyName == nameof(FujiCamera.AvailableApertures) ||
            e.PropertyName == nameof(FujiCamera.CurrentAperture) ||
            e.PropertyName == nameof(FujiCamera.SupportsApertureControl))
        {
            RefreshLensInfo();
        }
    }

    private void RefreshLensInfo()
    {
        try
        {
            if (!_camera.IsConnected)
            {
                IsConnected = false;
                LensText = "Not connected";
                AvailableApertures = Array.Empty<double>();
                CanControlAperture = false;
                ApertureError = string.Empty;
                return;
            }

            IsConnected = true;

            var caps = _camera.GetCapabilitiesSnapshot();
            var meta = caps.Metadata;

            if (!string.IsNullOrWhiteSpace(meta.LensProductName))
            {
                var ois = meta.HasImageStabilization ? " [OIS]" : "";
                LensText = $"{meta.LensProductName}{ois}";
            }
            else
            {
                LensText = "No lens detected";
            }

            _refreshing = true;
            try
            {
                AvailableApertures = _camera.AvailableApertures;
                CanControlAperture = _camera.SupportsApertureControl;
                _selectedAperture = _camera.CurrentAperture;
                RaisePropertyChanged(nameof(SelectedAperture));
                ApertureError = CanControlAperture
                    ? string.Empty
                    : "Aperture control is unavailable. For an electronic lens, set its aperture ring to A.";
            }
            finally
            {
                _refreshing = false;
            }
        }
        catch (Exception)
        {
            LensText = "Error";
        }
    }
}
