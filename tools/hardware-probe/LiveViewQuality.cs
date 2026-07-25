using System;
using System.IO;
using System.Runtime.InteropServices;
using static Probe.Sdk;

namespace Probe;

// Measures what live view actually delivers at each quality and size setting: real JPEG
// dimensions parsed from the frame, byte size, and achieved frame rate.
internal static class LiveViewQuality
{
    const int API_CODE_StartLiveView = 0x3301, API_CODE_StopLiveView = 0x3302;
    const int API_CODE_SetLiveViewImageQuality = 0x3323, API_CODE_SetLiveViewImageSize = 0x3325;
    const int API_CODE_CapThroughImageZoom = 0x332B, API_CODE_GetThroughImageZoom = 0x3328;
    const int IMAGEFORMAT_LIVE = 4;

    // Reads width/height from a JPEG's SOF marker; the SDK leaves the info struct's dimensions at 0.
    static (int w, int h) JpegSize(byte[] d)
    {
        for (int i = 2; i + 9 < d.Length;)
        {
            if (d[i] != 0xFF) { i++; continue; }
            var marker = d[i + 1];
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                return (d[i + 7] << 8 | d[i + 8], d[i + 5] << 8 | d[i + 6]);
            int len = d[i + 2] << 8 | d[i + 3];
            i += 2 + len;
        }
        return (0, 0);
    }

    public static void Run(IntPtr h, string outDir)
    {
        Directory.CreateDirectory(outDir);
        Console.WriteLine("\n  -- live view quality matrix --");

        // What zoom levels does this body offer? SetZoom expects one of these enum codes.
        var cz = XSDK_CapProp_Count(h, API_CODE_CapThroughImageZoom, 2, out var nz, IntPtr.Zero);
        if (cz == 0 && nz > 0)
        {
            var zb = Marshal.AllocHGlobal((int)nz * 8);
            try
            {
                if (XSDK_CapProp_Count(h, API_CODE_CapThroughImageZoom, 2, out nz, zb) == 0)
                {
                    var zs = new long[nz];
                    for (int i = 0; i < nz; i++) zs[i] = Marshal.ReadInt64(zb, i * 8);
                    Console.WriteLine($"    CapThroughImageZoom: {nz} levels {string.Join(",", zs)}");
                }
            }
            finally { Marshal.FreeHGlobal(zb); }
        }
        else Console.WriteLine($"    CapThroughImageZoom -> result={cz}, n={nz}");

        // Prove the plugin's fallback: ask for Normal, watch it be rejected, then set Fine.
        Console.WriteLine("    -- fallback check --");
        var qn = XSDK_SetProp(h, API_CODE_SetLiveViewImageQuality, 1, 2L);
        Console.WriteLine($"      request Normal -> {qn} {(qn != 0 ? "(rejected, as the plugin now expects)" : "(accepted)")}");
        if (qn != 0)
        {
            var qf = XSDK_SetProp(h, API_CODE_SetLiveViewImageQuality, 1, 1L);
            Console.WriteLine($"      fall back to Fine -> {qf} {(qf == 0 ? "VERIFIED" : "also rejected")}");
        }

        foreach (var (sizeName, sizeVal) in new[] { ("Large", 1L) })
        foreach (var (qName, qVal) in new[] { ("Fine", 1L), ("Basic", 3L) })
        {
            var sq = XSDK_SetProp(h, API_CODE_SetLiveViewImageQuality, 1, qVal);
            var ssz = XSDK_SetProp(h, API_CODE_SetLiveViewImageSize, 1, sizeVal);
            var start = XSDK_SetProp(h, API_CODE_StartLiveView, 0, 0);
            if (start != 0) { Console.WriteLine($"    {sizeName,-6}/{qName,-6}: StartLiveView failed ({start})"); continue; }

            try
            {
                int frames = 0; long bytes = 0; int w = 0, hgt = 0; byte[] sample = null;
                var t0 = DateTime.UtcNow;
                for (int i = 0; i < 60 && frames < 10; i++)
                {
                    System.Threading.Thread.Sleep(40);
                    if (XSDK_ReadImageInfo(h, out var info) != 0 || info.lDataSize <= 0) continue;
                    if ((info.lFormat & 0xFF) != IMAGEFORMAT_LIVE) continue;
                    var buf = Marshal.AllocHGlobal((int)info.lDataSize);
                    try
                    {
                        if (XSDK_ReadImage(h, buf, (ulong)info.lDataSize) == 0)
                        {
                            var arr = new byte[info.lDataSize];
                            Marshal.Copy(buf, arr, 0, (int)info.lDataSize);
                            if (frames == 0) { (w, hgt) = JpegSize(arr); sample = arr; }
                            frames++; bytes += info.lDataSize;
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                var secs = (DateTime.UtcNow - t0).TotalSeconds;
                var avg = frames > 0 ? bytes / frames : 0;
                Console.WriteLine($"    {sizeName,-6}/{qName,-6}: {w}x{hgt}  avg {avg,7} bytes/frame  {frames} frames in {secs:0.0}s ({(frames > 0 ? frames / secs : 0):0.0} fps)   set q={sq} sz={ssz}");
                if (sample != null) File.WriteAllBytes(Path.Combine(outDir, $"liveview-{sizeName}-{qName}.jpg"), sample);
            }
            finally { XSDK_SetProp(h, API_CODE_StopLiveView, 0, 0); System.Threading.Thread.Sleep(200); }
        }
        Console.WriteLine($"    sample frames written to {outDir}");
    }
}
