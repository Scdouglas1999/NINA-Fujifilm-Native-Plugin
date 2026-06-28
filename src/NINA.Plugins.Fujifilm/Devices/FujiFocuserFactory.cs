using System;
using System.ComponentModel.Composition;
using NINA.Plugins.Fujifilm.Configuration.Loading;
using NINA.Plugins.Fujifilm.Diagnostics;
using NINA.Plugins.Fujifilm.Interop;

namespace NINA.Plugins.Fujifilm.Devices;

public interface IFujiFocuserFactory
{
    bool SupportsFocus(FujifilmCameraDescriptor descriptor);
    FujiFocuser Create(FujifilmCameraDescriptor descriptor);
}

[Export(typeof(IFujiFocuserFactory))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class FujiFocuserFactory : IFujiFocuserFactory
{
    private readonly ICameraModelCatalog _catalog;
    private readonly IFujifilmDiagnosticsService _diagnostics;
    private readonly ExportFactory<FujiFocuser> _focuserFactory;

    [ImportingConstructor]
    public FujiFocuserFactory(ICameraModelCatalog catalog, IFujifilmDiagnosticsService diagnostics, ExportFactory<FujiFocuser> focuserFactory)
    {
        _catalog = catalog;
        _diagnostics = diagnostics;
        _focuserFactory = focuserFactory;
    }

    public bool SupportsFocus(FujifilmCameraDescriptor descriptor)
    {
        var config = _catalog.TryGetByProductName(descriptor.DisplayName);
        return config?.SdkConstants.FocusModeManual != 0;
    }

    public FujiFocuser Create(FujifilmCameraDescriptor descriptor)
    {
        _diagnostics.RecordEvent("Focuser", $"Creating focuser for {descriptor.DisplayName}");
        var export = _focuserFactory.CreateExport();
        var focuser = export.Value;
        focuser.Initialize(descriptor);
        return focuser;
    }
}
