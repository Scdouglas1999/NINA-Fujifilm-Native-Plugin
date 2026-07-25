using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using NINA.Plugins.Fujifilm.Diagnostics;
using NINA.Plugins.Fujifilm.Interop;
using NINA.Plugins.Fujifilm.Interop.Native;
using NINA.Plugins.Fujifilm.Settings;

namespace NINA.Plugins.Fujifilm.Devices;

[Export(typeof(FujiFocuser))]
[PartCreationPolicy(CreationPolicy.NonShared)]
public sealed class FujiFocuser : IAsyncDisposable
{
    private readonly IFujifilmInterop _interop;
    private readonly IFujifilmDiagnosticsService _diagnostics;
    private readonly IFujiSettingsProvider _settingsProvider;

    private FujifilmCameraDescriptor? _descriptor;
    private FujifilmCameraSession? _session;

    private FocusTravelMap? _travel;
    private int? _originalFocusMode;
    private string _lensProductName = string.Empty;

    private FocusTravelMap Travel => _travel ?? throw new InvalidOperationException("Focus limits have not been queried yet.");

    public int FocusMin => _travel?.TravelMin ?? 0;
    public int FocusMax => _travel?.TravelMax ?? 0;
    public int FocusRange => _travel?.Range ?? 0;

    /// <summary>
    /// NINA-space position corresponding to the lens' nominal infinity mark. Focus for
    /// astronomical targets sits at or slightly beyond this point, and everything below it
    /// is the SDK's "over search" travel past infinity.
    /// </summary>
    public int InfinityPosition => _travel?.InfinityPosition ?? 0;

    /// <summary>Usable travel available on the past-infinity side of the infinity mark.</summary>
    public int OverSearchInfinity => _travel?.OverSearchInfinity ?? 0;

    public string LensProductName => _lensProductName;

    [ImportingConstructor]
    public FujiFocuser(IFujifilmInterop interop, IFujifilmDiagnosticsService diagnostics, IFujiSettingsProvider settingsProvider)
    {
        _interop = interop;
        _diagnostics = diagnostics;
        _settingsProvider = settingsProvider;
    }

    public void Initialize(FujifilmCameraDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    public async Task MoveAsync(int position, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        var travel = Travel;
        var absolute = travel.ToPulse(position, out var clamp);

        if (clamp == FocusClamp.BelowInfinityStop)
        {
            _diagnostics.RecordEvent("Focuser",
                $"WARNING: requested position {position} is below the infinity hard stop; clamping to 0. " +
                "An autofocus run that hits this limit will produce an incomplete curve - reduce the step size or number of steps.");
        }
        else if (clamp == FocusClamp.BeyondClosestStop)
        {
            _diagnostics.RecordEvent("Focuser",
                $"WARNING: requested position {position} is beyond the closest-focus hard stop; clamping to {travel.Range}.");
        }

        _diagnostics.RecordEvent("Focuser", $"MoveAsync: requested position={position}, absolute={absolute}, min={travel.TravelMin}, max={travel.TravelMax}");
        
        var result = FujifilmSdkWrapper.XSDK_SetFocusPos(session.Handle, absolute);
        _diagnostics.RecordEvent("Focuser", $"XSDK_SetFocusPos returned: {result} (0x{result:X})");
        
        FujifilmSdkWrapper.CheckResult(session.Handle, result, nameof(FujifilmSdkWrapper.XSDK_SetFocusPos));
        
        var effectiveTimeout = timeout is { } requested && requested > TimeSpan.Zero
            ? requested
            : TimeSpan.FromSeconds(5);
        var deadline = DateTime.UtcNow + effectiveTimeout;
        while (true)
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            var verifyResult = FujifilmSdkWrapper.XSDK_GetFocusPos(session.Handle, out var actualPos);
            FujifilmSdkWrapper.CheckResult(session.Handle, verifyResult, nameof(FujifilmSdkWrapper.XSDK_GetFocusPos));
            if (Math.Abs(actualPos - absolute) <= Math.Max(1, travel.Step))
            {
                _diagnostics.RecordEvent("Focuser", $"Focus move complete: requested={absolute}, actual={actualPos}");
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Lens did not reach focus position {absolute}; last reported position was {actualPos}.");
            }
        }
    }

    public async Task<int> GetPositionAsync(CancellationToken cancellationToken)
    {
        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        var result = FujifilmSdkWrapper.XSDK_GetFocusPos(session.Handle, out var pos);
        
        if (result != FujifilmSdkWrapper.XSDK_COMPLETE)
        {
            _diagnostics.RecordEvent("Focuser", $"XSDK_GetFocusPos failed: result={result} (0x{result:X})");
        }
        
        FujifilmSdkWrapper.CheckResult(session.Handle, result, nameof(FujifilmSdkWrapper.XSDK_GetFocusPos));

        var travel = Travel;

        // The SDK warns that the pulse count "is not absolute, but fluctuates with temperature
        // and a variety of other conditions", so the lens can briefly sit outside the range it
        // advertised. NINA requires 0 <= Position <= MaxStep, so clamp rather than hand it a
        // negative value that would abort an autofocus run.
        var relative = travel.ToPosition(pos, out var clamped);
        if (clamped)
        {
            _diagnostics.RecordEvent("Focuser",
                $"Reported pulse {pos} falls outside the advertised travel [{travel.TravelMin}, {travel.TravelMax}]; clamped position to {relative}.");
        }

        _diagnostics.RecordEvent("Focuser", $"GetPositionAsync: absolute={pos}, relative={relative}, min={travel.TravelMin}");

        return relative;
    }

    private async Task<FujifilmCameraSession> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        if (_descriptor == null)
        {
            throw new InvalidOperationException("Focuser has not been initialized.");
        }

        if (_session != null && _session.Handle != IntPtr.Zero)
        {
            return _session;
        }

        _session = await _interop.OpenCameraAsync(_descriptor.DeviceId, cancellationToken).ConfigureAwait(false);
        _diagnostics.RecordEvent("Focuser", $"Opened focuser session for {_descriptor.DisplayName}");

        ApplyManualFocusMode();
        QueryFocusLimits();
        QueryLensInfo();
        return _session;
    }

    /// <summary>
    /// Puts the body into manual focus mode for the duration of the session. XSDK_SetFocusPos is
    /// documented as setting "the focus position for manual focus mode"; while the body is left in
    /// AF-S/AF-C the camera re-focuses on its own whenever the shutter is half-pressed (the plugin
    /// issues XSDK_RELEASE_S1ON to start a bulb exposure), which silently drags the lens away from
    /// the position NINA last commanded and makes the reported position differ on every reconnect.
    /// </summary>
    private void ApplyManualFocusMode()
    {
        if (_session == null)
        {
            return;
        }

        var readResult = FujifilmSdkWrapper.XSDK_GetFocusMode(_session.Handle, out var currentMode);
        if (readResult == FujifilmSdkWrapper.XSDK_COMPLETE)
        {
            _originalFocusMode = currentMode;
            _diagnostics.RecordEvent("Focuser", $"Camera focus mode on connect: {FujifilmSdkWrapper.DescribeFocusMode(currentMode)}");
        }
        else
        {
            _diagnostics.RecordEvent("Focuser", $"XSDK_GetFocusMode failed: result={readResult}");
        }

        if (!_settingsProvider.Settings.ForceManualFocusMode)
        {
            _diagnostics.RecordEvent("Focuser", "ForceManualFocusMode is disabled; leaving the camera focus mode untouched.");
            return;
        }

        if (readResult == FujifilmSdkWrapper.XSDK_COMPLETE && currentMode == FujifilmSdkWrapper.XSDK_FOCUS_MANUAL)
        {
            _diagnostics.RecordEvent("Focuser", "Camera is already in manual focus mode.");
            return;
        }

        // Best effort: some bodies refuse the change while the physical focus selector overrides it.
        var setResult = FujifilmSdkWrapper.XSDK_SetFocusMode(_session.Handle, FujifilmSdkWrapper.XSDK_FOCUS_MANUAL);
        if (setResult == FujifilmSdkWrapper.XSDK_COMPLETE)
        {
            _diagnostics.RecordEvent("Focuser", "Switched camera to manual focus mode so the body cannot refocus on its own.");
        }
        else
        {
            var error = FujifilmSdkWrapper.GetLastError(_session.Handle);
            _diagnostics.RecordEvent("Focuser",
                $"Could not switch to manual focus mode (result={setResult}, error=0x{error.ErrorCode:X}). " +
                "Set the camera's focus selector to M if positions drift between exposures.");
        }
    }

    private void RestoreFocusMode()
    {
        if (_session == null || _session.Handle == IntPtr.Zero || _originalFocusMode is not { } mode)
        {
            return;
        }

        if (mode == FujifilmSdkWrapper.XSDK_FOCUS_MANUAL || !_settingsProvider.Settings.ForceManualFocusMode)
        {
            return;
        }

        try
        {
            var result = FujifilmSdkWrapper.XSDK_SetFocusMode(_session.Handle, mode);
            _diagnostics.RecordEvent("Focuser",
                $"Restored camera focus mode to {FujifilmSdkWrapper.DescribeFocusMode(mode)} (result={result}).");
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Focuser", $"Failed to restore focus mode: {ex.Message}");
        }
        finally
        {
            _originalFocusMode = null;
        }
    }

    private void QueryFocusLimits()
    {
        if (_session == null)
        {
            return;
        }

        _diagnostics.RecordEvent("Focuser", $"Querying focus limits with API_CODE=0x{FujifilmSdkWrapper.XSDK_API_CODE_CapFocusPos:X}, API_PARAM={FujifilmSdkWrapper.XSDK_API_PARAM_CapFocusPos}");

        var result = FujifilmSdkWrapper.XSDK_CapFocusPos(_session.Handle, out var cap);
        
        _diagnostics.RecordEvent("Focuser", $"XSDK_CapFocusPos returned: result={result} (0x{result:X})");
        _diagnostics.RecordEvent("Focuser", $"Struct values: lSizeFocusPosCap={cap.lSizeFocusPosCap}, lStructVer=0x{cap.lStructVer:X}");
        _diagnostics.RecordEvent("Focuser", $"Focus positions: lFocusPlsINF={cap.lFocusPlsINF}, lFocusPlsMOD={cap.lFocusPlsMOD}");
        _diagnostics.RecordEvent("Focuser", $"Over search: lFocusOverSearchPlsINF={cap.lFocusOverSearchPlsINF}, lFocusOverSearchPlsMOD={cap.lFocusOverSearchPlsMOD}");
        _diagnostics.RecordEvent("Focuser", $"DOF capability: lFocusPlsFCSDepthCap={cap.lFocusPlsFCSDepthCap}, lMinDriveStep={cap.lMinDriveStepMFDriveEndThresh}");

        if (result != FujifilmSdkWrapper.XSDK_COMPLETE)
        {
            var error = FujifilmSdkWrapper.GetLastError(_session.Handle);
            throw new NotSupportedException(
                $"The attached lens did not provide focus-position capabilities (SDK result {result}, error 0x{error.ErrorCode:X}).");
        }

        // Check if lens supports focus position control
        if (cap.lFocusPlsINF == 0 && cap.lFocusPlsMOD == 0)
        {
            var message = "This lens did not report a programmable focus range. Use an electronic AF lens and set the camera focus selector to S or C.";
            _diagnostics.RecordEvent("Focuser", $"ERROR: {message}");
            throw new NotSupportedException(message);
        }

        _travel = new FocusTravelMap(
            cap.lFocusPlsINF,
            cap.lFocusPlsMOD,
            cap.lFocusOverSearchPlsINF,
            cap.lFocusOverSearchPlsMOD,
            cap.lMinDriveStepMFDriveEndThresh);

        _diagnostics.RecordEvent("Focuser",
            $"Focus travel min={_travel.TravelMin} max={_travel.TravelMax} range={_travel.Range} step={_travel.Step} " +
            $"(INF={_travel.FocusPlsInfinity}, MOD={_travel.FocusPlsMod}, overSearchINF={_travel.OverSearchInfinity}, overSearchMOD={_travel.OverSearchMod})");
        _diagnostics.RecordEvent("Focuser",
            $"Infinity mark sits at NINA position {_travel.InfinityPosition}; {_travel.OverSearchInfinity} steps of past-infinity travel are available below it.");

        if (_travel.HasNoPastInfinityTravel)
        {
            _diagnostics.RecordEvent("Focuser",
                "WARNING: this lens reports no past-infinity over-search travel, so position 0 is the infinity hard stop. " +
                "Keep autofocus step size small enough that the run stays above 0.");
        }
    }

    private void QueryLensInfo()
    {
        if (_session == null)
        {
            return;
        }

        try
        {
            var result = FujifilmSdkWrapper.XSDK_GetLensInfo(_session.Handle, out var lensInfo);
            if (result == FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                _lensProductName = lensInfo.strProductName?.Trim() ?? string.Empty;
                _diagnostics.RecordEvent("Focuser", $"Lens detected: {_lensProductName}");
            }
            else
            {
                _diagnostics.RecordEvent("Focuser", $"XSDK_GetLensInfo failed: result={result}");
            }
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Focuser", $"Lens info query error: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session != null)
        {
            RestoreFocusMode();
            await _interop.CloseCameraAsync(_session).ConfigureAwait(false);
            await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }
    }
}
