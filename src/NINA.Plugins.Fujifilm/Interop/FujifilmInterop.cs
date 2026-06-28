using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NINA.Plugins.Fujifilm.Diagnostics;
using NINA.Plugins.Fujifilm.Interop.Native;

namespace NINA.Plugins.Fujifilm.Interop;

[Export(typeof(IFujifilmInterop))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class FujifilmInterop : IFujifilmInterop
{
    private const int DetectionAttempts = 4;
    private const int DetectionRetryDelayMilliseconds = 500;

    private readonly IFujifilmDiagnosticsService _diagnostics;
    private static readonly SemaphoreSlim _globalLock = new(1, 1);
    private static readonly SemaphoreSlim _detectionLock = new(1, 1); // Serialize detection operations
    private static bool _isSdkInitializedGlobally;

    [ImportingConstructor]
    public FujifilmInterop(IFujifilmDiagnosticsService diagnostics)
    {
        _diagnostics = diagnostics;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _globalLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isSdkInitializedGlobally)
            {
                return;
            }

            _diagnostics.RecordEvent("Interop", "Initializing Fujifilm SDK runtime");
            LogRuntimeInventory();
            try 
            {
                var initResult = FujifilmSdkWrapper.XSDK_Init(IntPtr.Zero);
                FujifilmSdkWrapper.CheckResult(IntPtr.Zero, initResult, nameof(FujifilmSdkWrapper.XSDK_Init));
                _isSdkInitializedGlobally = true;
            }
            catch (FujifilmSdkException ex) when (ex.ErrorCode == 0x1004)
            {
                _diagnostics.RecordEvent("Interop", "SDK returned 0x1004 (Already Initialized) during Init. Treating as success.");
                _isSdkInitializedGlobally = true;
            }
            catch (DllNotFoundException ex)
            {
                throw new InvalidOperationException(
                    "Fujifilm Shooting SDK runtime not found. Reinstall the plugin and verify that XAPI.dll, XSDK.DAT, FTLPTP.dll, and the FF####API.dll model modules are beside the plugin DLL.",
                    ex);
            }
            catch (BadImageFormatException ex)
            {
                throw new InvalidOperationException(
                    "The Fujifilm Shooting SDK runtime has the wrong architecture. This plugin requires the 64-bit SDK files.",
                    ex);
            }
        }
        finally
        {
            _globalLock.Release();
        }
    }

    private void LogRuntimeInventory()
    {
        var pluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
        _diagnostics.RecordEvent("Interop", $"Plugin runtime directory: {pluginDirectory}");

        foreach (var fileName in new[] { "XAPI.dll", "XSDK.DAT", "FTLPTP.dll", "FTLPTPIP.dll" })
        {
            var path = Path.Combine(pluginDirectory, fileName);
            _diagnostics.RecordEvent("Interop", $"Runtime file {fileName}: {(File.Exists(path) ? "present" : "missing")}");
        }

        var modelModules = Directory.Exists(pluginDirectory)
            ? Directory.GetFiles(pluginDirectory, "FF????API.dll").Select(Path.GetFileName).OrderBy(name => name).ToArray()
            : Array.Empty<string>();
        _diagnostics.RecordEvent("Interop", modelModules.Length == 0
            ? "No Fujifilm camera model modules were found. X-T2 requires FF0002API.dll."
            : $"Fujifilm camera model modules ({modelModules.Length}): {string.Join(", ", modelModules)}");
    }

    public async Task ShutdownAsync()
    {
        await _globalLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_isSdkInitializedGlobally)
            {
                return;
            }

            _diagnostics.RecordEvent("Interop", "Shutting down Fujifilm SDK runtime");
            var exitResult = FujifilmSdkWrapper.XSDK_Exit();
            if (exitResult != FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                _diagnostics.RecordEvent("Interop", $"XSDK_Exit returned {exitResult}");
            }

            _isSdkInitializedGlobally = false;
        }
        finally
        {
            _globalLock.Release();
        }
    }

    public async Task<IReadOnlyList<FujifilmCameraInfo>> DetectCamerasAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        // Serialize detection operations to prevent concurrent access issues
        await _detectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _diagnostics.RecordEvent("Interop", "Detecting Fujifilm cameras via USB");
            
            // Add a small delay to allow any previous operations to complete
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        
        var count = 0;
        for (var attempt = 1; attempt <= DetectionAttempts; attempt++)
        {
            var detectResult = FujifilmSdkWrapper.XSDK_Detect(
                FujifilmSdkWrapper.XSDK_DSC_IF_USB,
                IntPtr.Zero,
                IntPtr.Zero,
                out count);

            FujifilmSdkWrapper.CheckResult(
                IntPtr.Zero,
                detectResult,
                nameof(FujifilmSdkWrapper.XSDK_Detect));

            if (count > 0 || attempt == DetectionAttempts)
            {
                break;
            }

            var retryDelay = DetectionRetryDelayMilliseconds * attempt;
            _diagnostics.RecordEvent(
                "Interop",
                $"No cameras found on detection attempt {attempt}/{DetectionAttempts}; retrying in {retryDelay}ms");
            await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
        }

        if (count <= 0)
        {
            _diagnostics.RecordEvent("Interop", "No Fujifilm cameras detected by SDK");
            return Array.Empty<FujifilmCameraInfo>();
        }

        _diagnostics.RecordEvent("Interop", $"SDK detected {count} camera(s)");
        
        var cameras = new List<FujifilmCameraInfo>(count);
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            // Add delay between detection attempts to allow camera to settle
            if (index > 0)
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
            
            var deviceId = $"ENUM:{index}";
            IntPtr cameraHandle = IntPtr.Zero;
            bool handleOpened = false;
            
            try
            {
                // Retry logic for opening camera (handles transient SEQUENCE errors)
                const int maxRetries = 3;
                int retryCount = 0;
                int openResult = FujifilmSdkWrapper.XSDK_ERROR;
                
                while (retryCount < maxRetries && openResult != FujifilmSdkWrapper.XSDK_COMPLETE)
                {
                    if (retryCount > 0)
                    {
                        _diagnostics.RecordEvent("Interop", $"Retrying camera open for {deviceId} (attempt {retryCount + 1}/{maxRetries})...");
                        await Task.Delay(300 * retryCount, cancellationToken).ConfigureAwait(false); // Exponential backoff
                    }
                    
                    openResult = FujifilmSdkWrapper.XSDK_OpenEx(deviceId, out cameraHandle, out var mode, IntPtr.Zero);
                    
                    if (openResult == FujifilmSdkWrapper.XSDK_COMPLETE)
                    {
                        handleOpened = true;
                        _diagnostics.RecordEvent("Interop", $"Opened camera handle {cameraHandle} in mode {mode} for detection");
                        
                        // Small delay after opening to allow camera to initialize
                        await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                        
                        FujifilmSdkWrapper.XSDK_DeviceInformation deviceInfo;
                        int apiCount;
                        var infoResult = FujifilmSdkWrapper.XSDK_GetDeviceInfoEx(cameraHandle, out deviceInfo, out apiCount, IntPtr.Zero);
                        
                        if (infoResult != FujifilmSdkWrapper.XSDK_COMPLETE)
                        {
                            var error = FujifilmSdkWrapper.GetLastError(cameraHandle);
                            _diagnostics.RecordEvent("Interop", $"GetDeviceInfoEx failed (result={infoResult}, ApiCode=0x{error.ApiCode:X}, ErrCode=0x{error.ErrorCode:X}). Retrying...");
                            // Close and retry
                            SafeCloseCamera(cameraHandle, "GetDeviceInfoEx failure");
                            cameraHandle = IntPtr.Zero;
                            handleOpened = false;
                            retryCount++;
                            continue;
                        }

                        var productName = deviceInfo.strProduct?.Trim() ?? string.Empty;
                        var serialNo = deviceInfo.strSerialNo?.Trim() ?? string.Empty;
                        _diagnostics.RecordEvent("Interop", $"Device info: Product='{productName}', Serial='{serialNo}'");
                        
                        var displayName = string.IsNullOrWhiteSpace(productName) ? $"Fujifilm Camera {index}" : productName;
                        _diagnostics.RecordEvent("Interop", $"Creating camera descriptor: DisplayName='{displayName}', DeviceId='{deviceId}'");
                        
                        cameras.Add(new FujifilmCameraInfo(
                            displayName,
                            serialNo,
                            deviceId));
                        break; // Success, exit retry loop
                    }
                    else
                    {
                        var error = FujifilmSdkWrapper.GetLastError(IntPtr.Zero);
                        if (error.ErrorCode == FujifilmSdkWrapper.XSDK_ERRCODE_SEQUENCE)
                        {
                            _diagnostics.RecordEvent("Interop", $"SEQUENCE error (0x1001) opening {deviceId}. Camera may be in use or in bad state. Retrying...");
                            retryCount++;
                        }
                        else
                        {
                            // Non-retryable error
                            FujifilmSdkWrapper.CheckResult(IntPtr.Zero, openResult, nameof(FujifilmSdkWrapper.XSDK_OpenEx));
                        }
                    }
                }
                
                if (openResult != FujifilmSdkWrapper.XSDK_COMPLETE)
                {
                    var error = FujifilmSdkWrapper.GetLastError(IntPtr.Zero);
                    _diagnostics.RecordEvent("Interop", $"Failed to open camera {deviceId} after {maxRetries} attempts (result={openResult}, ApiCode=0x{error.ApiCode:X}, ErrCode=0x{error.ErrorCode:X})");
                }
            }
            catch (FujifilmSdkException ex)
            {
                _diagnostics.RecordEvent("Interop", $"Detection failed at index {index}: {ex.Message}");
            }
            catch (Exception ex)
            {
                _diagnostics.RecordEvent("Interop", $"Unexpected error during detection at index {index}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (handleOpened && cameraHandle != IntPtr.Zero)
                {
                    SafeCloseCamera(cameraHandle, "detection phase");
                }
            }
        }

            _diagnostics.RecordEvent("Interop", $"Detection complete: {cameras.Count} camera(s) successfully detected");
            return cameras;
        }
        finally
        {
            _detectionLock.Release();
        }
    }

    private void SafeCloseCamera(IntPtr cameraHandle, string context)
    {
        if (cameraHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var closeResult = FujifilmSdkWrapper.XSDK_Close(cameraHandle);
            if (closeResult != FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                var error = FujifilmSdkWrapper.GetLastError(cameraHandle);
                _diagnostics.RecordEvent("Interop", $"XSDK_Close returned {closeResult} during {context} (ApiCode=0x{error.ApiCode:X}, ErrCode=0x{error.ErrorCode:X})");
            }
        }
        catch (System.Runtime.InteropServices.SEHException ex)
        {
            // Handle SEH exceptions that can occur when closing invalid handles
            _diagnostics.RecordEvent("Interop", $"SEHException closing camera handle during {context}: {ex.Message}. Handle may have been invalid.");
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Interop", $"Exception closing camera handle during {context}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Dictionary to track open sessions and their reference counts
    // Key: DeviceId, Value: (Session, RefCount)
    private readonly Dictionary<string, (FujifilmCameraSession Session, int RefCount)> _openSessions = new();

    public async Task<FujifilmCameraSession> OpenCameraAsync(string deviceId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        
        await _globalLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Check if we already have an open session for this device
            if (_openSessions.TryGetValue(deviceId, out var entry))
            {
                // Increment reference count
                entry.RefCount++;
                _openSessions[deviceId] = entry; // Update struct in dictionary
                
                _diagnostics.RecordEvent("Interop", $"Reusing existing session for {deviceId} (RefCount: {entry.RefCount})");
                return entry.Session;
            }

            // No existing session, open a new one
            _diagnostics.RecordEvent("Interop", $"Opening Fujifilm camera {deviceId}");
            int openResult = FujifilmSdkWrapper.XSDK_OpenEx(deviceId, out var handle, out var mode, IntPtr.Zero);
            FujifilmSdkWrapper.CheckResult(IntPtr.Zero, openResult, nameof(FujifilmSdkWrapper.XSDK_OpenEx));
            
            _diagnostics.RecordEvent("Interop", $"Camera handle {handle} opened in mode {mode}");
            
            var session = new FujifilmCameraSession(handle, deviceId);
            _openSessions[deviceId] = (session, 1);
            
            return session;
        }
        finally
        {
            _globalLock.Release();
        }
    }

    public async Task CloseCameraAsync(FujifilmCameraSession session)
    {
        if (session == null || session.Handle == IntPtr.Zero)
        {
            return;
        }

        await _globalLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_openSessions.TryGetValue(session.DeviceId, out var entry))
            {
                entry.RefCount--;
                
                if (entry.RefCount > 0)
                {
                    _diagnostics.RecordEvent("Interop", $"Decremented RefCount for {session.DeviceId} to {entry.RefCount}. Keeping session open.");
                    _openSessions[session.DeviceId] = entry; // Update struct
                    return;
                }
                
                // RefCount is 0, close the session
                _openSessions.Remove(session.DeviceId);
            }
            
            _diagnostics.RecordEvent("Interop", $"Closing Fujifilm camera {session.DeviceId} (RefCount 0)");
            SafeCloseCamera(session.Handle, $"closing session for {session.DeviceId}");
            session.Handle = IntPtr.Zero;
        }
        finally
        {
            _globalLock.Release();
        }
    }

    public Task<(int Width, int Height)> GetImageInfoAsync(FujifilmCameraSession session)
    {
        if (session == null || session.Handle == IntPtr.Zero)
        {
            throw new ArgumentException("Invalid session", nameof(session));
        }

        FujifilmSdkWrapper.XSDK_ImageInformation info;
        var result = FujifilmSdkWrapper.XSDK_ReadImageInfo(session.Handle, out info);
        
        // Don't throw if it fails, just return empty info or log
        if (result != FujifilmSdkWrapper.XSDK_COMPLETE)
        {
            _diagnostics.RecordEvent("Interop", $"XSDK_ReadImageInfo returned {result}");
            // Return empty tuple if failed
            return Task.FromResult((0, 0));
        }

        return Task.FromResult((info.lImagePixWidth, info.lImagePixHeight));
    }

    public Task<int> GetImageSizeAsync(FujifilmCameraSession session)
    {
        if (session == null || session.Handle == IntPtr.Zero)
        {
            throw new ArgumentException("Invalid session", nameof(session));
        }

        int imageSize;
        var result = FujifilmSdkWrapper.XSDK_GetImageSize(session.Handle, out imageSize);
        
        if (result != FujifilmSdkWrapper.XSDK_COMPLETE)
        {
            _diagnostics.RecordEvent("Interop", $"XSDK_GetImageSize returned {result}");
            return Task.FromResult(-1);
        }

        return Task.FromResult(imageSize);
    }

    public Task<int> GetSensitivityAsync(FujifilmCameraSession session)
    {
        if (session == null || session.Handle == IntPtr.Zero)
        {
            throw new ArgumentException("Invalid session", nameof(session));
        }

        int sensitivity;
        var result = FujifilmSdkWrapper.XSDK_GetSensitivity(session.Handle, out sensitivity);
        
        if (result != FujifilmSdkWrapper.XSDK_COMPLETE)
        {
            _diagnostics.RecordEvent("Interop", $"XSDK_GetSensitivity returned {result}");
            return Task.FromResult(-1);
        }

        return Task.FromResult(sensitivity);
    }

    // The SDK lifetime is process-global. Explicit sessions close their camera handles;
    // shutting the runtime down here could invalidate another exported adapter.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
