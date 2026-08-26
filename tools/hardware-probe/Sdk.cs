using System;
using System.Runtime.InteropServices;

namespace Probe;

// Mirrors the plugin's P/Invoke declarations exactly. On Linux the SDK ships as XAPI.so; the
// signatures are identical, so this exercises the same marshalling the plugin uses on Windows.
internal static class Sdk
{
    const string L = "XAPI";

    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_Init(IntPtr hLib);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_Exit();
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_Detect(long lInterface, IntPtr pInterface, IntPtr pDeviceName, out long plCount);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_OpenEx([MarshalAs(UnmanagedType.LPStr)] string pDevice, out IntPtr phCamera, out long plCameraMode, IntPtr pOption);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_Close(IntPtr h);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_SetPriorityMode(IntPtr h, long mode);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_GetErrorNumber(IntPtr h, out long plAPICode, out long plERRCode);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_GetDeviceInfoEx(IntPtr h, out XSDK_DeviceInformation info, out long plNumAPICode, IntPtr plAPICode);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_GetProp(IntPtr h, long code, long param, out long val);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_SetProp(IntPtr h, long code, long param, long val);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_GetProp")] public static extern int XSDK_GetProp2(IntPtr h, long code, long param, out long v1, out long v2);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_GetProp")] public static extern int XSDK_GetProp_Struct(IntPtr h, long code, long param, IntPtr pData);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_GetProp")] public static extern int XSDK_GetProp_Count(IntPtr h, long code, long param, out long plNum, IntPtr pData);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_CapProp")] public static extern int XSDK_CapProp_Count(IntPtr h, long code, long param, out long plNum, IntPtr pData);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_CapProp")] public static extern int XSDK_CapProp_Focus(IntPtr h, long code, long param, ref long plSize, IntPtr pData);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_CapShutterSpeed(IntPtr h, ref long plNum, IntPtr pl, out long plBulb);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_SetShutterSpeed(IntPtr h, long code, long bulb);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_SetMediaRecord(IntPtr h, long v);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_GetMediaRecord(IntPtr h, out long v);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_Release(IntPtr h, long mode, IntPtr opt, out long status);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_ReadImageInfo(IntPtr h, out ImageInformation info);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_ReadImage(IntPtr h, IntPtr data, ulong size);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_DeleteImage(IntPtr h);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_GetBufferCapacity(IntPtr h, out long shoot, out long total);

    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_GetLensInfo(IntPtr h, out LensInformation info);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_GetProp")] public static extern int XSDK_GetProp_Battery8(IntPtr h, long code, long param, out long a, out long b, out long c, out long d, out long e, out long f, out long g, out long i);

    // The four-parameter CapSensitivity as it shipped before 3.0.4.0.
    [DllImport(L, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_CapSensitivity")]
    public static extern int XSDK_CapSensitivity_FourArg(IntPtr h, ref long lDR, ref long plNum, IntPtr pl);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_GetProp")]
    public static extern int XSDK_GetProp_Buffer(IntPtr h, long code, long param, IntPtr pData);
    // ref, not out, so both slots can be seeded with a sentinel and checked afterwards.
    [DllImport(L, CallingConvention = CallingConvention.Cdecl, EntryPoint = "XSDK_GetProp")]
    public static extern int XSDK_GetProp_TwoRef(IntPtr h, long code, long param, ref long v1, ref long v2);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XSDK_GetImageSize(IntPtr h, out long size);

    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_GetMode(IntPtr h, out long m);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_GetAEMode(IntPtr h, out long m);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_SetAEMode(IntPtr h, long m);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_SetSensitivity(IntPtr h, long v);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_GetSensitivity(IntPtr h, out long v);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_CapSensitivity(IntPtr h, ref long plNum, IntPtr pl);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_GetLensZoomPos(IntPtr h, out long v);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_CapAperture(IntPtr h, long zoomPos, ref long plNum, IntPtr pl);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_SetAperture(IntPtr h, long fNumber);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int XSDK_GetAperture(IntPtr h, out long fNumber);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct XSDK_DeviceInformation
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strVendor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strManufacturer;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strProduct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strFirmware;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strDeviceType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strSerialNo;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strFramework;
        public byte bDeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string strDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string strYNo;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FocusLimiterIndicator { public long lCurrent, lDOF_Near, lDOF_Far, lPos_A, lPos_B, lStatus; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FocusLimiter { public long lPos_A, lPos_B; }

    // XAPI.H XSDK_ImageInformation, pragma pack(1): char[32] + 6 longs + XSDK_HANDLE.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct ImageInformation
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string strInternalName;
        public long lFormat, lDataSize, lImagePixHeight, lImagePixWidth, lImageBitDepth, lPreviewSize;
        public IntPtr hImage;
    }

    // XAPI.H XSDK_LensInformation, pragma pack(1)
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct LensInformation
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 20)] public string strModel;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 100)] public string strProductName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 20)] public string strSerialNo;
        public long lISCapability, lMFCapability, lZoomPosCapability;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct FocusPosCap { public long lSize, lVer, lInf, lMod, lOverInf, lOverMod, lDof, lMinStep; }
}
