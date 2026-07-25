using System;
using System.Runtime.InteropServices;
using static Probe.Sdk;

namespace Probe;

// Exercises the capture and download path end to end: the media-record fix, the
// XSDK_ImageInformation layout, the lFormat rotation mask, and whether XSDK_ReadImage really
// removes the frame from the in-camera buffer (which decides if the extra DeleteImage is correct).
internal static class Capture
{
    const int MEDIAREC_OFF = 0x0004;
    const int RELEASE_SHOOT_S1OFF = 0x0100 | 0x0004;
    const int IMAGEFORMAT_RAW = 1;

    public static void Run(IntPtr h, long shutterCode, string label)
    {
        Console.WriteLine($"\n  --- {label} (shutter code {shutterCode}) ---");

        var mr = XSDK_SetMediaRecord(h, MEDIAREC_OFF);
        XSDK_GetMediaRecord(h, out var mrNow);
        Console.WriteLine($"    SetMediaRecord(OFF=0x4) -> {mr}, reads back {mrNow}  {(mr == 0 && mrNow == MEDIAREC_OFF ? "VERIFIED (card writing disabled)" : "not applied")}");

        var ss = XSDK_SetShutterSpeed(h, shutterCode, 0);
        if (ss != 0) { Console.WriteLine($"    SetShutterSpeed failed ({ss})"); return; }

        XSDK_GetBufferCapacity(h, out var shootBefore, out _);
        Console.WriteLine($"    buffer before release: {shootBefore} frame(s) pending");

        // plShotOpt is IN/OUT: "the number of pictures to be taken per burst ... returns the number
        // actually taken". The plugin passes a pointer to 0; try 1 as well to settle which the SDK
        // actually accepts.
        var t0 = DateTime.UtcNow;
        long rel = -1, status = 0;
        long usedShotOpt = -1;
        foreach (long shotOpt in new long[] { 1, 0 })
        {
            var opt = Marshal.AllocHGlobal(sizeof(long));
            try
            {
                Marshal.WriteInt64(opt, shotOpt);
                rel = XSDK_Release(h, RELEASE_SHOOT_S1OFF, opt, out status);
                var returned = Marshal.ReadInt64(opt);
                Console.WriteLine($"    XSDK_Release(plShotOpt={shotOpt}) -> {rel}, status={status}, plShotOpt returned {returned}");
                if (rel == 0) { usedShotOpt = shotOpt; break; }
                XSDK_GetErrorNumber(h, out var a, out var e);
                Console.WriteLine($"      apiCode=0x{a:X} errCode=0x{e:X}");
            }
            finally { Marshal.FreeHGlobal(opt); }
        }
        if (rel != 0) { Console.WriteLine("    release failed with both shot-option values"); return; }
        Console.WriteLine($"    => plShotOpt={usedShotOpt} accepted");

        ImageInformation info = default;
        bool ready = false;
        for (int i = 0; i < 60; i++)
        {
            System.Threading.Thread.Sleep(250);
            if (XSDK_ReadImageInfo(h, out info) == 0 && info.lDataSize > 0) { ready = true; break; }
        }
        var elapsed = (DateTime.UtcNow - t0).TotalSeconds;
        if (!ready) { Console.WriteLine($"    no image after {elapsed:0.0}s"); return; }

        Console.WriteLine($"    ReadImageInfo after {elapsed:0.0}s: format=0x{info.lFormat:X} masked=0x{info.lFormat & 0xFF:X} " +
                          $"{info.lImagePixWidth}x{info.lImagePixHeight} depth={info.lImageBitDepth} bytes={info.lDataSize}");
        Console.WriteLine($"      struct size={Marshal.SizeOf<ImageInformation>()} internalName='{info.strInternalName?.Trim()}'");
        Console.WriteLine($"      rotation bits=0x{info.lFormat & 0x0F00:X}  isRAW(masked)={(info.lFormat & 0xFF) == IMAGEFORMAT_RAW}" +
                          $"  isRAW(unmasked, the old check)={info.lFormat == IMAGEFORMAT_RAW}");

        var buf = Marshal.AllocHGlobal((int)info.lDataSize);
        try
        {
            var rd = XSDK_ReadImage(h, buf, (ulong)info.lDataSize);
            Console.WriteLine($"    XSDK_ReadImage -> {rd}");
            if (rd == 0)
            {
                var head = new byte[4];
                Marshal.Copy(buf, head, 0, 4);
                Console.WriteLine($"      first bytes: {BitConverter.ToString(head)}  ({System.Text.Encoding.ASCII.GetString(head)})");
            }
        }
        finally { Marshal.FreeHGlobal(buf); }

        // Does ReadImage already remove the frame? This is what decides whether the extra
        // DeleteImage on the success path is correct or harmful.
        XSDK_GetBufferCapacity(h, out var shootAfterRead, out _);
        var del = XSDK_DeleteImage(h);
        XSDK_GetBufferCapacity(h, out var shootAfterDelete, out _);
        Console.WriteLine($"    buffer after ReadImage: {shootAfterRead}; DeleteImage -> {del}; after delete: {shootAfterDelete}");
        Console.WriteLine($"      => ReadImage {(shootAfterRead == 0 ? "DID" : "did NOT")} clear the buffer; extra DeleteImage was {(del == 0 ? "accepted" : "rejected (expected if buffer already empty)")}");
    }
}
