using System;
using System.ComponentModel.Composition;

namespace NINA.Plugins.Fujifilm.Devices;

/// <summary>
/// Tracks the Fujifilm devices that are currently connected.
/// </summary>
/// <remarks>
/// N.I.N.A. hands sequence instructions its own device mediators, which expose the generic
/// <c>ICamera</c>/<c>IFocuser</c> surface rather than the Fujifilm-specific one. The Fujifilm
/// sequence instructions need the concrete devices to reach settings the generic interfaces do not
/// model, such as RAW bit depth or the lens' infinity position, so the adapters publish themselves
/// here while they are connected.
/// </remarks>
public interface IFujiEquipmentRegistry
{
    FujiCamera? ConnectedCamera { get; }
    FujiFocuser? ConnectedFocuser { get; }

    void RegisterCamera(FujiCamera? camera);
    void RegisterFocuser(FujiFocuser? focuser);
}

[Export(typeof(IFujiEquipmentRegistry))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class FujiEquipmentRegistry : IFujiEquipmentRegistry
{
    private readonly object _sync = new();
    private FujiCamera? _camera;
    private FujiFocuser? _focuser;

    public FujiCamera? ConnectedCamera
    {
        get { lock (_sync) { return _camera; } }
    }

    public FujiFocuser? ConnectedFocuser
    {
        get { lock (_sync) { return _focuser; } }
    }

    public void RegisterCamera(FujiCamera? camera)
    {
        lock (_sync) { _camera = camera; }
    }

    public void RegisterFocuser(FujiFocuser? focuser)
    {
        lock (_sync) { _focuser = focuser; }
    }
}
