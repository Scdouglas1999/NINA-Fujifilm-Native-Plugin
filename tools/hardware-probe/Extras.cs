using System;
using System.Runtime.InteropServices;
using static Probe.Sdk;

namespace Probe;

// Covers the parts of the plugin that the first probe pass did not reach: lens info, battery,
// live view, a bulb exposure, and cancelling an exposure that is already running.
internal static class Extras
{
    const int API_CODE_CheckBatteryInfo = 0x4055;
    const int API_CODE_StartLiveView = 0x3301, API_CODE_StopLiveView = 0x3302;
    const int RELEASE_S1ON = 0x0200, RELEASE_BULBS2_ON = 0x0500;
    const int RELEASE_N_S1OFF = 0x0004, RELEASE_N_BULBS2OFF = 0x0008;
    const int RELEASE_N_BULBS1OFF = RELEASE_N_BULBS2OFF | RELEASE_N_S1OFF;
    const int RELEASE_SHOOT_S1OFF = 0x0100 | 0x0004;
    const int RELEASE_CANCEL = 0x000F;
    const int IMAGEFORMAT_LIVE = 4;

    static void Err(IntPtr h, string what, long r)
    {
        XSDK_GetErrorNumber(h, out var a, out var e);
        Console.WriteLine($"      {what}: result={r} apiCode=0x{a:X} errCode=0x{e:X}");
    }

    public static void LensInfo(IntPtr h)
    {
        Console.WriteLine("\n  -- lens info --");
        var r = XSDK_GetLensInfo(h, out var li);
        if (r != 0) { Err(h, "GetLensInfo", r); return; }
        Console.WriteLine($"    struct size={Marshal.SizeOf<LensInformation>()}");
        Console.WriteLine($"    model='{li.strModel?.Trim()}' product='{li.strProductName?.Trim()}' serial='{li.strSerialNo?.Trim()}'");
        Console.WriteLine($"    IS={li.lISCapability} MF={li.lMFCapability} ZoomPos={li.lZoomPosCapability}");
    }

    public static void Battery(IntPtr h)
    {
        // Mirrors the plugin: probe the candidate layouts largest-first, always supplying storage
        // for the largest, and let the camera decide. No model name is consulted.
        Console.WriteLine("\n  -- battery (adaptive probe, no model list) --");
        int? accepted = null;
        foreach (var candidate in new[] { 8, 6 })
        {
            var r = XSDK_GetProp_Battery8(h, API_CODE_CheckBatteryInfo, candidate,
                out var b1, out var b2, out var b3, out var b4, out var b5, out var b6, out var b7, out var b8);
            Console.WriteLine($"    probe {candidate} output values -> result={r}" +
                (r == 0 ? $"  ACCEPTED: bodyInfo=0x{b1:X} bodyRatio={b4} grip={b2}/{b5}" : "  (rejected)"));
            if (r == 0) { accepted = candidate; break; }
        }
        Console.WriteLine(accepted is null
            ? "    => no known layout accepted; the plugin reports battery unavailable"
            : $"    => VERIFIED: camera accepts the {accepted}-value layout, discovered without a model list");
    }

    public static void LiveView(IntPtr h)
    {
        Console.WriteLine("\n  -- live view --");
        var start = XSDK_SetProp(h, API_CODE_StartLiveView, 0, 0);
        Console.WriteLine($"    StartLiveView -> {start}");
        if (start != 0) { Err(h, "StartLiveView", start); return; }
        try
        {
            int frames = 0; long bytes = 0;
            for (int i = 0; i < 25; i++)
            {
                System.Threading.Thread.Sleep(100);
                if (XSDK_ReadImageInfo(h, out var info) != 0 || info.lDataSize <= 0) continue;
                if ((info.lFormat & 0xFF) != IMAGEFORMAT_LIVE) { Console.WriteLine($"      unexpected format 0x{info.lFormat:X}"); continue; }
                var buf = Marshal.AllocHGlobal((int)info.lDataSize);
                try
                {
                    if (XSDK_ReadImage(h, buf, (ulong)info.lDataSize) == 0)
                    {
                        frames++; bytes += info.lDataSize;
                        if (frames == 1)
                        {
                            var head = new byte[3]; Marshal.Copy(buf, head, 0, 3);
                            Console.WriteLine($"      first frame {info.lImagePixWidth}x{info.lImagePixHeight} {info.lDataSize} bytes, header {BitConverter.ToString(head)} ({(head[0]==0xFF&&head[1]==0xD8?"JPEG":"?")})");
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
                if (frames >= 5) break;
            }
            Console.WriteLine($"    captured {frames} live-view frame(s), {bytes} bytes total  {(frames > 0 ? "VERIFIED" : "no frames")}");
        }
        finally
        {
            var stop = XSDK_SetProp(h, API_CODE_StopLiveView, 0, 0);
            Console.WriteLine($"    StopLiveView -> {stop}");
        }
    }

    public static void BulbExposure(IntPtr h)
    {
        Console.WriteLine("\n  -- bulb exposure (about 2s) --");
        var ss = XSDK_SetShutterSpeed(h, -1, 1);   // XSDK_SHUTTER_BULB
        Console.WriteLine($"    SetShutterSpeed(BULB) -> {ss}");
        if (ss != 0) { Err(h, "SetShutterSpeed(BULB)", ss); return; }

        var opt = Marshal.AllocHGlobal(sizeof(long));
        try
        {
            Marshal.WriteInt64(opt, 1);
            var s1 = XSDK_Release(h, RELEASE_S1ON, opt, out var st1);
            Console.WriteLine($"    S1ON -> {s1}, status={st1}");
            if (s1 != 0) { Err(h, "S1ON", s1); return; }
            System.Threading.Thread.Sleep(500);

            var s2 = XSDK_Release(h, RELEASE_BULBS2_ON, opt, out var st2);
            Console.WriteLine($"    BULBS2_ON -> {s2}, status={st2}");
            if (s2 != 0) { Err(h, "BULBS2_ON", s2); XSDK_Release(h, RELEASE_N_BULBS1OFF, opt, out _); return; }

            System.Threading.Thread.Sleep(2000);
            var off = XSDK_Release(h, RELEASE_N_BULBS1OFF, opt, out var st3);
            Console.WriteLine($"    N_BULBS1OFF -> {off}, status={st3}");

            for (int i = 0; i < 40; i++)
            {
                System.Threading.Thread.Sleep(250);
                if (XSDK_ReadImageInfo(h, out var info) == 0 && info.lDataSize > 0)
                {
                    Console.WriteLine($"    bulb frame ready: format=0x{info.lFormat:X} bytes={info.lDataSize} name='{info.strInternalName?.Trim()}'  VERIFIED");
                    var b = Marshal.AllocHGlobal((int)info.lDataSize);
                    try { XSDK_ReadImage(h, b, (ulong)info.lDataSize); } finally { Marshal.FreeHGlobal(b); }
                    return;
                }
            }
            Console.WriteLine("    no bulb frame arrived");
        }
        finally { Marshal.FreeHGlobal(opt); }
    }

    public static void CancelExposure(IntPtr h)
    {
        Console.WriteLine("\n  -- cancel an exposure in progress (new in 3.1.0.0) --");
        var ss = XSDK_SetShutterSpeed(h, 16000000, 0);   // 15 seconds
        Console.WriteLine($"    SetShutterSpeed(15s) -> {ss}");
        if (ss != 0) { Err(h, "SetShutterSpeed(15s)", ss); return; }

        var opt = Marshal.AllocHGlobal(sizeof(long));
        try
        {
            Marshal.WriteInt64(opt, 1);
            var rel = XSDK_Release(h, RELEASE_SHOOT_S1OFF, opt, out var st);
            Console.WriteLine($"    release 15s exposure -> {rel}, status={st}");
            if (rel != 0) { Err(h, "release", rel); return; }

            System.Threading.Thread.Sleep(2000);
            var t0 = DateTime.UtcNow;
            var cancel = XSDK_Release(h, RELEASE_CANCEL, opt, out var cs);
            var dt = (DateTime.UtcNow - t0).TotalMilliseconds;
            Console.WriteLine($"    XSDK_RELEASE_CANCEL after 2s -> {cancel}, status={cs} ({dt:0}ms)");
            if (cancel != 0) { Err(h, "RELEASE_CANCEL", cancel); Console.WriteLine("    => this body does not support cancelling; plugin falls back to waiting"); }
            else Console.WriteLine("    => VERIFIED: exposure cancellable");

            for (int i = 0; i < 60; i++)
            {
                System.Threading.Thread.Sleep(250);
                if (XSDK_ReadImageInfo(h, out var info) == 0 && info.lDataSize > 0)
                {
                    Console.WriteLine($"    a frame still arrived ({info.lDataSize} bytes) after {(DateTime.UtcNow - t0).TotalSeconds:0.0}s; draining it");
                    var b = Marshal.AllocHGlobal((int)info.lDataSize);
                    try { XSDK_ReadImage(h, b, (ulong)info.lDataSize); } finally { Marshal.FreeHGlobal(b); }
                    return;
                }
            }
            Console.WriteLine("    no frame produced after cancel (exposure abandoned)");
        }
        finally { Marshal.FreeHGlobal(opt); }
    }
}
