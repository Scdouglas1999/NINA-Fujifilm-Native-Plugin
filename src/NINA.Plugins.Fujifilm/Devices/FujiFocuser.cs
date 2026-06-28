using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using NINA.Plugins.Fujifilm.Diagnostics;
using NINA.Plugins.Fujifilm.Interop;
using NINA.Plugins.Fujifilm.Interop.Native;

namespace NINA.Plugins.Fujifilm.Devices;

[Export(typeof(FujiFocuser))]
[PartCreationPolicy(CreationPolicy.NonShared)]
public sealed class FujiFocuser : IAsyncDisposable
{
    private readonly IFujifilmInterop _interop;
    private readonly IFujifilmDiagnosticsService _diagnostics;

    private FujifilmCameraDescriptor? _descriptor;
    private FujifilmCameraSession? _session;
    private int _focusMin;
    private int _focusMax;
    private int _focusStep;
    private string _lensProductName = string.Empty;

    public int FocusMin => _focusMin;
    public int FocusMax => _focusMax;
    public int FocusRange => _focusMax - _focusMin;
    public string LensProductName => _lensProductName;

    [ImportingConstructor]
    public FujiFocuser(IFujifilmInterop interop, IFujifilmDiagnosticsService diagnostics)
    {
        _interop = interop;
        _diagnostics = diagnostics;
    }

    public void Initialize(FujifilmCameraDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    public async Task MoveAsync(int position, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        var absolute = position + _focusMin;
        if (absolute < _focusMin)
        {
            absolute = _focusMin;
        }
        else if (absolute > _focusMax)
        {
            absolute = _focusMax;
        }

        if (_focusStep > 1)
        {
            absolute = _focusMin + (int)Math.Round((absolute - _focusMin) / (double)_focusStep) * _focusStep;
            absolute = Math.Clamp(absolute, _focusMin, _focusMax);
        }
        
        _diagnostics.RecordEvent("Focuser", $"MoveAsync: requested position={position}, absolute={absolute}, min={_focusMin}, max={_focusMax}");
        
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
            if (Math.Abs(actualPos - absolute) <= Math.Max(1, _focusStep))
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
        
        var relative = pos - _focusMin;
        _diagnostics.RecordEvent("Focuser", $"GetPositionAsync: absolute={pos}, relative={relative}, min={_focusMin}");
        
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

        QueryFocusLimits();
        QueryLensInfo();
        return _session;
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

        _focusMin = Math.Min(cap.lFocusPlsINF, cap.lFocusPlsMOD);
        _focusMax = Math.Max(cap.lFocusPlsINF, cap.lFocusPlsMOD);
        _focusStep = cap.lMinDriveStepMFDriveEndThresh > 0 ? cap.lMinDriveStepMFDriveEndThresh : 1;

        _diagnostics.RecordEvent("Focuser", $"Focus range min={_focusMin} max={_focusMax} step={_focusStep} (INF={cap.lFocusPlsINF}, MOD={cap.lFocusPlsMOD})");
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
            await _interop.CloseCameraAsync(_session).ConfigureAwait(false);
            await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }
    }
}
