using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NINA.Plugins.Fujifilm.Interop;

internal static class FujifilmSdkWrapper
{
    private const string SdkDllName = "XAPI.dll";

    public const int XSDK_COMPLETE = 0;
    public const int XSDK_ERROR = -1;

    public const int XSDK_DSC_IF_USB = 1;

    public const int XSDK_PRIORITY_PC = 0x0002;

    // Error Codes (SDK §5)
    public const int XSDK_ERRCODE_NOERR = 0x0000;
    public const int XSDK_ERRCODE_SEQUENCE = 0x1001;
    public const int XSDK_ERRCODE_PARAM = 0x1002;
    public const int XSDK_ERRCODE_INVALID_CAMERA = 0x1003;
    public const int XSDK_ERRCODE_LOADLIB = 0x1004;
    public const int XSDK_ERRCODE_UNSUPPORTED = 0x1005;
    public const int XSDK_ERRCODE_BUSY = 0x1006;
    public const int XSDK_ERRCODE_AF_TIMEOUT = 0x1007;
    public const int XSDK_ERRCODE_SHOOT_ERROR = 0x1008;
    public const int XSDK_ERRCODE_FRAME_FULL = 0x1009;
    public const int XSDK_ERRCODE_STANDBY = 0x1010;
    public const int XSDK_ERRCODE_NODRIVER = 0x1011;
    public const int XSDK_ERRCODE_NO_MODEL_MODULE = 0x1012;
    public const int XSDK_ERRCODE_API_NOTFOUND = 0x1013;
    public const int XSDK_ERRCODE_API_MISMATCH = 0x1014;
    public const int XSDK_ERRCODE_INVALID_USBMODE = 0x1015;
    public const int XSDK_ERRCODE_FORCEMODE_BUSY = 0x1016;
    public const int XSDK_ERRCODE_RUNNING_OTHER_FUNCTION = 0x1017;
    public const int XSDK_ERRCODE_COMMUNICATION = 0x2001;
    public const int XSDK_ERRCODE_TIMEOUT = 0x2002;
    public const int XSDK_ERRCODE_COMBINATION = 0x2003;
    public const int XSDK_ERRCODE_WRITEERROR = 0x2004;
    public const int XSDK_ERRCODE_CARDFULL = 0x2005;
    public const int XSDK_ERRCODE_HARDWARE = 0x3001;
    public const int XSDK_ERRCODE_INTERNAL = 0x9001;
    public const int XSDK_ERRCODE_MEMFULL = 0x9002;
    public const int XSDK_ERRCODE_UNKNOWN = 0x9100;

    public const int XSDK_RELEASE_SHOOT = 0x0100;
    public const int XSDK_RELEASE_N_S1OFF = 0x0004;
    public const int XSDK_RELEASE_SHOOT_S1OFF = XSDK_RELEASE_SHOOT | XSDK_RELEASE_N_S1OFF;
    public const int XSDK_RELEASE_S1ON = 0x0200;
    public const int XSDK_RELEASE_BULBS2_ON = 0x0500;
    public const int XSDK_RELEASE_N_BULBS2OFF = 0x0008;
    public const int XSDK_RELEASE_N_BULBS1OFF = XSDK_RELEASE_N_BULBS2OFF | XSDK_RELEASE_N_S1OFF;
    public const int XSDK_SHUTTER_BULB = -1;

    public const int XSDK_DRANGE_100 = 0x0064;
    
    // AE Mode constants
    public const int XSDK_AE_OFF = 0x0001;  // Manual exposure mode
    public const int XSDK_AE_PROGRAM = 0x0006;  // Program AE
    public const int XSDK_AE_APERTURE_PRIORITY = 0x0003;  // Aperture priority
    public const int XSDK_AE_SHUTTER_PRIORITY = 0x0004;  // Shutter priority
    public const int XSDK_DRANGE_200 = 200;
    public const int XSDK_DRANGE_400 = 400;
    public const int XSDK_DRANGE_800 = 800;
    public const int XSDK_DRANGE_AUTO = 0xffff;
    

    // Media Record Constants (XAPI.h lines 2237-2240). OFF was previously defined as 0, which is not
    // a valid value: the SDK rejected it with XSDK_ERRCODE_COMBINATION, so card recording was never
    // actually disabled.
    public const int XSDK_MEDIAREC_RAWJPEG = 0x0001;
    public const int XSDK_MEDIAREC_RAW = 0x0002;
    public const int XSDK_MEDIAREC_JPEG = 0x0003;
    public const int XSDK_MEDIAREC_OFF = 0x0004;

    // ========== Battery Info API (from XAPIOpt.h) ==========
    public const int API_CODE_CheckBatteryInfo = 0x4055;

    // The API parameter is the number of output values the call produces: current bodies return 8
    // and older ones 6. Which applies is discovered by asking the camera - see
    // FujifilmBatteryProtocol - rather than from a list of model names, so a model the plugin has
    // never seen still reports its battery.

    // Power Capacity Status Codes (from XAPIOpt.h lines 1234-1247)
    public const int SDK_POWERCAPACITY_EMPTY = 0x0000;      // Empty
    public const int SDK_POWERCAPACITY_END = 0x0001;        // End (about to die)
    public const int SDK_POWERCAPACITY_PREEND = 0x0002;     // Pre-end (very low)
    public const int SDK_POWERCAPACITY_HALF = 0x0003;       // Half
    public const int SDK_POWERCAPACITY_FULL = 0x0004;       // Full
    public const int SDK_POWERCAPACITY_HIGH = 0x0005;       // High
    public const int SDK_POWERCAPACITY_PREEND5 = 0x0007;    // Less than 20%
    public const int SDK_POWERCAPACITY_20 = 0x0008;         // 20%
    public const int SDK_POWERCAPACITY_40 = 0x0009;         // 40%
    public const int SDK_POWERCAPACITY_60 = 0x000A;         // 60%
    public const int SDK_POWERCAPACITY_80 = 0x000B;         // 80%
    public const int SDK_POWERCAPACITY_100 = 0x000C;        // 100%
    public const int SDK_POWERCAPACITY_DC_CHARGE = 0x000D;  // Charging via DC
    public const int SDK_POWERCAPACITY_FULL_CHARGE = 0x000E;      // Fully charged
    public const int SDK_POWERCAPACITY_CHARGING_ERROR = 0x000F;   // Charging error
    public const int SDK_POWERCAPACITY_CAPACITY_UNKNOWN = 0x0010; // Capacity unknown
    public const int SDK_POWERCAPACITY_DC = 0x00FF;         // Powered by DC adapter

    // ========== Lens Position/Zoom API Codes (from XAPI.h) ==========
    public const int API_CODE_CapLensZoomPos = 0x1321;
    public const int API_CODE_SetLensZoomPos = 0x1322;
    public const int API_CODE_GetLensZoomPos = 0x1323;
    public const int API_PARAM_LensZoomPos = 1;

    // ========== Aperture API Codes (from XAPI.h) ==========
    public const int API_CODE_CapAperture = 0x1324;
    public const int API_CODE_SetAperture = 0x1325;
    public const int API_CODE_GetAperture = 0x1326;
    public const int API_PARAM_Aperture = 1;

    // ========== Live View API Codes (from XAPI.h) ==========
    public const int API_CODE_StartLiveView = 0x3301;
    public const int API_CODE_StopLiveView = 0x3302;
    public const int API_CODE_SetLiveViewImageQuality = 0x3323;
    public const int API_CODE_SetLiveViewImageSize = 0x3325;
    public const int API_CODE_SetThroughImageZoom = 0x3327;
    public const int API_PARAM_LiveView = 1;

    // ========== Live View Quality Constants ==========
    public const int XSDK_LIVEVIEW_QUALITY_FINE = 0x0001;
    public const int XSDK_LIVEVIEW_QUALITY_NORMAL = 0x0002;
    public const int XSDK_LIVEVIEW_QUALITY_BASIC = 0x0003;

    // ========== Live View Size Constants ==========
    // Large/Medium/Small. The pixel dimensions differ by model and sensor aspect ratio, so they
    // are read from a decoded frame rather than assumed.
    public const int XSDK_LIVEVIEW_SIZE_L = 0x0001;
    public const int XSDK_LIVEVIEW_SIZE_M = 0x0002;
    public const int XSDK_LIVEVIEW_SIZE_S = 0x0003;

    // ========== Capture Quality API Codes (XAPIOpt.h) ==========
    public const int API_CODE_SetLongExposureNR = 0x2145;
    public const int API_CODE_GetLongExposureNR = 0x2146;
    public const int API_CODE_CapLongExposureNR = 0x218A;
    public const int API_CODE_SetRAWCompression = 0x2150;
    public const int API_CODE_GetRAWCompression = 0x2151;
    public const int API_CODE_CapRAWCompression = 0x218F;
    public const int API_CODE_SetRAWOutputDepth = 0x2160;
    public const int API_CODE_GetRAWOutputDepth = 0x2161;
    public const int API_CODE_CapRAWOutputDepth = 0x2193;
    public const int API_CODE_SetCropMode = 0x2267;
    public const int API_CODE_GetCropMode = 0x2268;
    public const int API_CODE_CapCropMode = 0x2242;

    // ========== Focus Limiter / Scale API Codes (XAPIOpt.h) ==========
    public const int API_CODE_CapFocusLimiterMode = 0x2244;
    public const int API_CODE_GetFocusLimiterIndicator = 0x226B;
    public const int API_CODE_GetFocusLimiterRange = 0x226C;
    public const int API_CODE_SetFocusLimiterMode = 0x226D;
    public const int API_CODE_GetFocusLimiterMode = 0x226E;
    public const int API_CODE_SetFocusScaleUnit = 0x4215;
    public const int API_CODE_GetFocusScaleUnit = 0x4216;
    public const int API_CODE_CapFocusScaleUnit = 0x4235;
    public const int API_CODE_GetThroughImageZoom = 0x3328;
    public const int API_CODE_CapThroughImageZoom = 0x332B;

    // API parameters. Verified identical across every model header that supports the call;
    // build/verify-sdk-interop.py re-checks this against the SDK headers.
    public const int API_PARAM_SetLongExposureNR = 1;
    public const int API_PARAM_GetLongExposureNR = 1;
    public const int API_PARAM_CapLongExposureNR = 2;
    public const int API_PARAM_SetRAWCompression = 1;
    public const int API_PARAM_GetRAWCompression = 1;
    public const int API_PARAM_CapRAWCompression = 2;
    public const int API_PARAM_SetRAWOutputDepth = 1;
    public const int API_PARAM_GetRAWOutputDepth = 1;
    public const int API_PARAM_CapRAWOutputDepth = 2;
    public const int API_PARAM_SetCropMode = 1;
    public const int API_PARAM_GetCropMode = 2;   // note: differs from the setter
    public const int API_PARAM_CapCropMode = 2;
    public const int API_PARAM_GetFocusLimiterRange = 2;
    public const int API_PARAM_GetFocusLimiterMode = 1;
    public const int API_PARAM_SetFocusLimiterMode = 1;
    public const int API_PARAM_GetFocusLimiterIndicator = 1;
    public const int API_PARAM_SetFocusScaleUnit = 1;
    public const int API_PARAM_GetFocusScaleUnit = 1;
    public const int API_PARAM_CapFocusScaleUnit = 2;
    public const int API_PARAM_SetThroughImageZoom = 1;
    public const int API_PARAM_GetThroughImageZoom = 1;
    public const int API_PARAM_CapThroughImageZoom = 2;

    // ========== Generic ON/OFF (XAPIOpt.h). OFF is 2, not 0. ==========
    public const int SDK_ON = 0x0001;
    public const int SDK_OFF = 0x0002;

    // ========== RAW Output Depth (XAPIOpt.h) ==========
    public const int SDK_RAWOUTPUTDEPTH_14BIT = 0x0001;
    public const int SDK_RAWOUTPUTDEPTH_16BIT = 0x0002;

    // ========== RAW Compression (XAPIOpt.h) ==========
    public const int SDK_RAW_COMPRESSION_OFF = 0x0001;       // Uncompressed
    public const int SDK_RAW_COMPRESSION_LOSSLESS = 0x0002;  // Lossless compression
    public const int SDK_RAW_COMPRESSION_LOSSY = 0x0003;

    // ========== Crop Mode (XAPIOpt.h) ==========
    public const int SDK_CROPMODE_OFF = 0x0000;
    public const int SDK_CROPMODE_35MM = 0x0001;
    public const int SDK_CROPMODE_SPORTSFINDER_125 = 0x0002;
    public const int SDK_CROPMODE_AUTO = 0x8001;

    // ========== Focus Distance Scale Unit (XAPIOpt.h) ==========
    public const int SDK_SCALEUNIT_M = 0x0001;
    public const int SDK_SCALEUNIT_FT = 0x0002;

    // ========== Focus Limiter (XAPIOpt.h) ==========
    public const int SDK_FOCUS_LIMITER_OFF = 0x0001;      // aka FULL
    public const int SDK_FOCUS_LIMITER_MOD_MID = 0x0002;
    public const int SDK_FOCUS_LIMITER_MID_INF = 0x0003;
    public const int SDK_FOCUS_LIMITER_STATUS_VALID = 0x0001;
    public const int SDK_FOCUS_LIMITER_STATUS_INVALID = 0x0000;

    // ========== Release / drive modes for cancel and pixel shift (XAPI.h) ==========
    public const int XSDK_RELEASE_CANCEL = 0x000F;
    public const int XSDK_RELEASE_PIXELSHIFT = 0x4000;
    public const int XSDK_DRIVE_MODE_PIXELSHIFTMULTISHOT = 0x0010;

    // ========== Image Format Constants ==========
    // XAPI.h lines 378-382. Only the low byte of lFormat carries the format; bits 0x0F00 encode
    // the camera rotation, so always mask with 0xFF before comparing.
    public const int XSDK_IMAGEFORMAT_RAW = 1;
    public const int XSDK_IMAGEFORMAT_LIVE = 4;  // Live View JPEG
    public const int XSDK_IMAGEFORMAT_NONE = 5;
    public const int XSDK_IMAGEFORMAT_JPEG = 7;
    public const int XSDK_IMAGEFORMAT_HEIF = 0x0012;

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_Init")]
    public static extern int XSDK_Init(IntPtr hLib);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_Exit")]
    public static extern int XSDK_Exit();

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_Detect")]
    public static extern int XSDK_Detect(int lInterface, IntPtr pInterface, IntPtr pDeviceName, out int plCount);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_OpenEx")]
    public static extern int XSDK_OpenEx([MarshalAs(UnmanagedType.LPStr)] string pDevice, out IntPtr phCamera, out int plCameraMode, IntPtr pOption);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_Close")]
    public static extern int XSDK_Close(IntPtr hCamera);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_SetPriorityMode")]
    public static extern int XSDK_SetPriorityMode(IntPtr hCamera, int lPriorityMode);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_GetDeviceInfoEx")]
    public static extern int XSDK_GetDeviceInfoEx(IntPtr hCamera, out XSDK_DeviceInformation pDevInfo, out int plNumAPICode, IntPtr plAPICode);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_CapShutterSpeed")]
    public static extern int XSDK_CapShutterSpeed(IntPtr hCamera, ref int plNumShutterSpeed, IntPtr plShutterSpeed, out int plBulbCapable);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_SetShutterSpeed")]
    public static extern int XSDK_SetShutterSpeed(IntPtr hCamera, int lShutterSpeed, int lBulb);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_CapSensitivity")]
    // XAPI.H: XSDK_CapSensitivity( XSDK_HANDLE, long* plNumSensitivity, long* plSensitivity ) - three
    // parameters. The previous four-parameter declaration shifted every argument by one slot, so the
    // count was written into the caller's dynamic-range variable and the query always returned zero.
    public static extern int XSDK_CapSensitivity(IntPtr hCamera, ref int plNumSensitivity, IntPtr plSensitivity);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_SetSensitivity")]
    public static extern int XSDK_SetSensitivity(IntPtr hCamera, int lSensitivity);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_GetSensitivity")]
    public static extern int XSDK_GetSensitivity(IntPtr hCamera, out int plSensitivity);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_GetLensZoomPos")]
    public static extern int XSDK_GetLensZoomPos(IntPtr hCamera, out int plZoomPos);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_CapAperture")]
    public static extern int XSDK_CapAperture(IntPtr hCamera, int lZoomPos, ref int plNumAperture, IntPtr plFNumber);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_SetAperture")]
    public static extern int XSDK_SetAperture(IntPtr hCamera, int lFNumber);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_GetAperture")]
    public static extern int XSDK_GetAperture(IntPtr hCamera, out int plFNumber);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_SetMode")]
    public static extern int XSDK_SetMode(IntPtr hCamera, int lMode);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_GetMode")]
    public static extern int XSDK_GetMode(IntPtr hCamera, out int plMode);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_SetAEMode")]
    public static extern int XSDK_SetAEMode(IntPtr hCamera, int lAEMode);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_GetAEMode")]
    public static extern int XSDK_GetAEMode(IntPtr hCamera, out int plAEMode);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_SetMediaRecord")]
    public static extern int XSDK_SetMediaRecord(IntPtr hCamera, int lMediaRecord);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_GetMediaRecord")]
    public static extern int XSDK_GetMediaRecord(IntPtr hCamera, out int plMediaRecord);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_GetLensInfo")]
    public static extern int XSDK_GetLensInfo(IntPtr hCamera, out XSDK_LensInformation pLensInfo);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_GetLensVersion")]
    public static extern int XSDK_GetLensVersion(IntPtr hCamera, [MarshalAs(UnmanagedType.LPStr)] StringBuilder pLensVersion);

    // Focus Control API Codes (XAPIOpt.h: API_CODE_*)
    public const int XSDK_API_CODE_SetFocusMode = 0x2201;
    public const int XSDK_API_CODE_GetFocusMode = 0x2202;
    public const int XSDK_API_CODE_SetFocusPos = 0x2207;
    public const int XSDK_API_CODE_GetFocusPos = 0x2208;
    public const int XSDK_API_CODE_CapFocusMode = 0x2209;
    public const int XSDK_API_CODE_CapFocusPos = 0x2259;

    // Focus Control API Parameters (verified identical across all 18 model headers)
    public const int XSDK_API_PARAM_CapFocusPos = 2;
    public const int XSDK_API_PARAM_SetFocusPos = 1;
    public const int XSDK_API_PARAM_GetFocusPos = 1;
    public const int XSDK_API_PARAM_SetFocusMode = 1;
    public const int XSDK_API_PARAM_GetFocusMode = 1;
    public const int XSDK_API_PARAM_CapFocusMode = 2;

    // Focus Mode constants (XAPIOpt.h: SDK_FOCUS_*)
    public const int XSDK_FOCUS_MANUAL = 0x0001;
    public const int XSDK_FOCUS_AFS = 0x8001;
    public const int XSDK_FOCUS_AFC = 0x8002;

    public static string DescribeFocusMode(int mode) => mode switch
    {
        XSDK_FOCUS_MANUAL => "MANUAL",
        XSDK_FOCUS_AFS => "AF-S",
        XSDK_FOCUS_AFC => "AF-C",
        _ => $"Unknown(0x{mode:X})"
    };

    public static int XSDK_GetFocusMode(IntPtr hCamera, out int plFocusMode)
    {
        return XSDK_GetProp(hCamera, XSDK_API_CODE_GetFocusMode, XSDK_API_PARAM_GetFocusMode, out plFocusMode);
    }

    public static int XSDK_SetFocusMode(IntPtr hCamera, int lFocusMode)
    {
        return XSDK_SetProp(hCamera, XSDK_API_CODE_SetFocusMode, XSDK_API_PARAM_SetFocusMode, lFocusMode);
    }

    // ---- Capture quality ----------------------------------------------------------------

    public static int XSDK_GetLongExposureNR(IntPtr hCamera, out int plSetting)
        => XSDK_GetProp(hCamera, API_CODE_GetLongExposureNR, API_PARAM_GetLongExposureNR, out plSetting);

    public static int XSDK_SetLongExposureNR(IntPtr hCamera, int lSetting)
        => XSDK_SetProp(hCamera, API_CODE_SetLongExposureNR, API_PARAM_SetLongExposureNR, lSetting);

    public static int XSDK_GetRAWCompression(IntPtr hCamera, out int plSetting)
        => XSDK_GetProp(hCamera, API_CODE_GetRAWCompression, API_PARAM_GetRAWCompression, out plSetting);

    public static int XSDK_SetRAWCompression(IntPtr hCamera, int lSetting)
        => XSDK_SetProp(hCamera, API_CODE_SetRAWCompression, API_PARAM_SetRAWCompression, lSetting);

    public static int XSDK_GetRAWOutputDepth(IntPtr hCamera, out int plSetting)
        => XSDK_GetProp(hCamera, API_CODE_GetRAWOutputDepth, API_PARAM_GetRAWOutputDepth, out plSetting);

    public static int XSDK_SetRAWOutputDepth(IntPtr hCamera, int lSetting)
        => XSDK_SetProp(hCamera, API_CODE_SetRAWOutputDepth, API_PARAM_SetRAWOutputDepth, lSetting);

    /// <summary>
    /// Reads the crop mode. Unlike the other getters here this one writes <b>two</b> values:
    /// API_PARAM is the number of output values the call produces, and GetCropMode's parameter is
    /// 2 because it returns the mode and a status alongside it. Passing a single output pointer
    /// lets the SDK write past the end of the caller's storage.
    /// </summary>
    public static int XSDK_GetCropMode(IntPtr hCamera, out int plSetting, out int plStatus)
        => XSDK_GetProp2(hCamera, API_CODE_GetCropMode, API_PARAM_GetCropMode, out plSetting, out plStatus);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_GetProp")]
    private static extern int XSDK_GetProp2(IntPtr hCamera, int lAPICode, int lAPIParam, out int plValue1, out int plValue2);

    public static int XSDK_SetCropMode(IntPtr hCamera, int lSetting)
        => XSDK_SetProp(hCamera, API_CODE_SetCropMode, API_PARAM_SetCropMode, lSetting);

    // ---- Focus limiter and distance scale ------------------------------------------------

    public static int XSDK_GetFocusLimiterMode(IntPtr hCamera, out int plMode)
        => XSDK_GetProp(hCamera, API_CODE_GetFocusLimiterMode, API_PARAM_GetFocusLimiterMode, out plMode);

    public static int XSDK_GetFocusScaleUnit(IntPtr hCamera, out int plUnit)
        => XSDK_GetProp(hCamera, API_CODE_GetFocusScaleUnit, API_PARAM_GetFocusScaleUnit, out plUnit);

    public static int XSDK_GetThroughImageZoom(IntPtr hCamera, out int plZoom)
        => XSDK_GetProp(hCamera, API_CODE_GetThroughImageZoom, API_PARAM_GetThroughImageZoom, out plZoom);

    /// <summary>
    /// Reads the focus limiter indicator: the current focus position, the depth-of-field bounds
    /// around it, and the limiter's A/B endpoints, all in focus pulses.
    /// </summary>
    public static int XSDK_GetFocusLimiterIndicator(IntPtr hCamera, out XSDK_FOCUS_LIMITER_INDICATOR indicator)
    {
        int size = Marshal.SizeOf<XSDK_FOCUS_LIMITER_INDICATOR>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            var result = XSDK_GetProp_Struct(
                hCamera,
                API_CODE_GetFocusLimiterIndicator,
                API_PARAM_GetFocusLimiterIndicator,
                buffer);
            indicator = Marshal.PtrToStructure<XSDK_FOCUS_LIMITER_INDICATOR>(buffer);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Reads the endpoints of every focus limiter range the lens offers. Pass a null buffer to
    /// learn the count first, as documented for GetFocusLimiterRange.
    /// </summary>
    public static int XSDK_GetFocusLimiterRange(IntPtr hCamera, out XSDK_FOCUS_LIMITER[] ranges)
    {
        ranges = Array.Empty<XSDK_FOCUS_LIMITER>();

        var countResult = XSDK_GetProp_Count(
            hCamera, API_CODE_GetFocusLimiterRange, API_PARAM_GetFocusLimiterRange, out var count, IntPtr.Zero);
        if (countResult != XSDK_COMPLETE || count <= 0)
        {
            return countResult;
        }

        int entrySize = Marshal.SizeOf<XSDK_FOCUS_LIMITER>();
        IntPtr buffer = Marshal.AllocHGlobal(entrySize * count);
        try
        {
            var result = XSDK_GetProp_Count(
                hCamera, API_CODE_GetFocusLimiterRange, API_PARAM_GetFocusLimiterRange, out count, buffer);
            if (result != XSDK_COMPLETE)
            {
                return result;
            }

            var parsed = new XSDK_FOCUS_LIMITER[count];
            for (int i = 0; i < count; i++)
            {
                parsed[i] = Marshal.PtrToStructure<XSDK_FOCUS_LIMITER>(buffer + (i * entrySize));
            }

            ranges = parsed;
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_GetProp")]
    private static extern int XSDK_GetProp_Struct(IntPtr hCamera, int lAPICode, int lAPIParam, IntPtr pData);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_GetProp")]
    private static extern int XSDK_GetProp_Count(IntPtr hCamera, int lAPICode, int lAPIParam, out int plNum, IntPtr pData);

    // Helper methods wrapping generic property functions
    public static int XSDK_CapFocusPos(IntPtr hCamera, out XSDK_FOCUS_POS_CAP focusPosCap)
    {
        int size = Marshal.SizeOf<XSDK_FOCUS_POS_CAP>();
        IntPtr pFocusPosCap = Marshal.AllocHGlobal(size);
        
        // Initialize struct size and version as per SDK docs
        var cap = new XSDK_FOCUS_POS_CAP();
        cap.lSizeFocusPosCap = size;
        cap.lStructVer = 0x00010000;
        Marshal.StructureToPtr(cap, pFocusPosCap, false);

        System.Diagnostics.Debug.WriteLine($"[FujiSDK] XSDK_CapFocusPos: struct size={size}, API_CODE=0x{XSDK_API_CODE_CapFocusPos:X}, API_PARAM={XSDK_API_PARAM_CapFocusPos}");

        try
        {
            int result = XSDK_CapProp_Focus(hCamera, XSDK_API_CODE_CapFocusPos, XSDK_API_PARAM_CapFocusPos, ref size, pFocusPosCap);
            
            System.Diagnostics.Debug.WriteLine($"[FujiSDK] XSDK_CapFocusPos: result={result}, size after call={size}");
            
            focusPosCap = Marshal.PtrToStructure<XSDK_FOCUS_POS_CAP>(pFocusPosCap);
            
            System.Diagnostics.Debug.WriteLine($"[FujiSDK] XSDK_CapFocusPos: lSizeFocusPosCap={focusPosCap.lSizeFocusPosCap}, lStructVer=0x{focusPosCap.lStructVer:X}, lFocusPlsINF={focusPosCap.lFocusPlsINF}, lFocusPlsMOD={focusPosCap.lFocusPlsMOD}, lFocusPlsFCSDepthCap={focusPosCap.lFocusPlsFCSDepthCap}");
            
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(pFocusPosCap);
        }
    }

    public static int XSDK_GetFocusPos(IntPtr hCamera, out int plFocusPos)
    {
        return XSDK_GetProp(hCamera, XSDK_API_CODE_GetFocusPos, XSDK_API_PARAM_GetFocusPos, out plFocusPos);
    }

    public static int XSDK_SetFocusPos(IntPtr hCamera, int lFocusPos)
    {
        return XSDK_SetProp(hCamera, XSDK_API_CODE_SetFocusPos, XSDK_API_PARAM_SetFocusPos, lFocusPos);
    }

    // Specific P/Invoke for CapFocusPos which uses IN/OUT for the size parameter
    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_CapProp")]
    private static extern int XSDK_CapProp_Focus(IntPtr hCamera, int lAPICode, int lAPIParam, ref int plSize, IntPtr pData);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_Release")]
    public static extern int XSDK_Release(IntPtr hCamera, int lReleaseMode, IntPtr plShotOpt, out int pStatus);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_ReadImageInfo")]
    public static extern int XSDK_ReadImageInfo(IntPtr hCamera, out XSDK_ImageInformation pImgInfo);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_ReadImage")]
    public static extern int XSDK_ReadImage(IntPtr hCamera, IntPtr pData, uint ulDataSize);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_DeleteImage")]
    public static extern int XSDK_DeleteImage(IntPtr hCamera);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_GetBufferCapacity")]
    public static extern int XSDK_GetBufferCapacity(IntPtr hCamera, out int plShootFrameNum, out int plTotalFrameNum);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_SetDynamicRange")]
    public static extern int XSDK_SetDynamicRange(IntPtr hCamera, int lDRange);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_GetDynamicRange")]
    public static extern int XSDK_GetDynamicRange(IntPtr hCamera, out int plDRange);


    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_GetErrorNumber")]
    public static extern int XSDK_GetErrorNumber(IntPtr hCamera, out int plAPICode, out int plERRCode);

    // Model-dependent property functions (for Long Exposure NR, Noise Reduction, etc.)
    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_CapProp")]
    public static extern int XSDK_CapProp(IntPtr hCamera, int lAPICode, int lAPIParam, out int plNum, IntPtr plValues);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_SetProp")]
    public static extern int XSDK_SetProp(IntPtr hCamera, int lAPICode, int lAPIParam, int lValue);

    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_GetProp")]
    public static extern int XSDK_GetProp(IntPtr hCamera, int lAPICode, int lAPIParam, out int plValue);

    // Battery Info overload for models whose headers specify 8 output parameters.
    [DllImport(SdkDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_GetProp")]
    public static extern int XSDK_GetProp_Battery8(
        IntPtr hCamera,
        int lAPICode,
        int lAPIParam,
        out int plBodyBatteryInfo,
        out int plGripBatteryInfo,
        out int plGripBattery2Info,
        out int plBodyBatteryRatio,
        out int plGripBatteryRatio,
        out int plGripBattery2Ratio,
        out int plBodyBattery2Info,
        out int plBodyBattery2Ratio2);


    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct XSDK_DeviceInformation
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strVendor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strManufacturer;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strProduct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strFirmware;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strDeviceType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strSerialNo;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strFramework;
        public byte bDeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string strDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string strYNo;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct XSDK_ImageInformation
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string strInternalName;
        public int lFormat;
        public int lDataSize;
        public int lImagePixHeight;
        public int lImagePixWidth;
        public int lImageBitDepth;
        public int lPreviewSize;

        // XAPI.H declares a trailing XSDK_HANDLE hImage. Omitting it made the marshaller allocate
        // 56 bytes for a struct the SDK writes 64 bytes into, overrunning the buffer on every
        // XSDK_ReadImageInfo call.
        public IntPtr hImage;
    }

    /// <summary>XAPIOpt.h SDK_FOCUS_LIMITER_INDICATOR, #pragma pack(1). All values are focus pulses.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct XSDK_FOCUS_LIMITER_INDICATOR
    {
        public int lCurrent;
        public int lDOF_Near;
        public int lDOF_Far;
        public int lPos_A;
        public int lPos_B;
        public int lStatus;
    }

    /// <summary>XAPIOpt.h SDK_FOCUS_LIMITER, #pragma pack(1). One selectable limiter range.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct XSDK_FOCUS_LIMITER
    {
        public int lPos_A;
        public int lPos_B;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 4)]
    public struct XSDK_FOCUS_POS_CAP
    {
        public int lSizeFocusPosCap;
        public int lStructVer;
        public int lFocusPlsINF;
        public int lFocusPlsMOD;
        public int lFocusOverSearchPlsINF;
        public int lFocusOverSearchPlsMOD;
        public int lFocusPlsFCSDepthCap;
        public int lMinDriveStepMFDriveEndThresh;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct XSDK_LensInformation
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 20)]
        public string strModel;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 100)]
        public string strProductName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 20)]
        public string strSerialNo;
        public int lISCapability;
        public int lMFCapability;
        public int lZoomPosCapability;
    }

    public static void CheckResult(IntPtr cameraHandle, int result, string operation)
    {
        if (result == XSDK_COMPLETE)
        {
            return;
        }

        var error = GetLastError(cameraHandle);
        throw new FujifilmSdkException(operation, result, error.ApiCode, error.ErrorCode);
    }

    public static FujifilmSdkError GetLastError(IntPtr cameraHandle)
    {
        int apiCode;
        int errCode;
        var result = XSDK_GetErrorNumber(cameraHandle, out apiCode, out errCode);
        return new FujifilmSdkError(result, apiCode, errCode);
    }
}

internal readonly record struct FujifilmSdkError(int Result, int ApiCode, int ErrorCode);

internal sealed class FujifilmSdkException : Exception
{
    public FujifilmSdkException(string operation, int result, int apiCode, int errorCode)
        : base($"Fujifilm SDK call '{operation}' failed (Result={result}, ApiCode=0x{apiCode:X}, ErrCode=0x{errorCode:X})")
    {
        Result = result;
        ApiCode = apiCode;
        ErrorCode = errorCode;
    }

    public int Result { get; }
    public int ApiCode { get; }
    public int ErrorCode { get; }
}
