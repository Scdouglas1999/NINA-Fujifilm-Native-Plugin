using System;
using System.Diagnostics;
using static Probe.Sdk;

namespace Probe;

// Reproduces the reported "cannot connect until I pull the battery" failure: rapid
// close-then-reopen cycles, and opening a camera that is already open. Both are what the plugin
// used to do, and both answer with XSDK_ERRCODE_SEQUENCE (0x1001).
internal static class SessionCycle
{
    const int ERRCODE_SEQUENCE = 0x1001;

    static long LastErr(IntPtr h)
    {
        XSDK_GetErrorNumber(h, out _, out var err);
        return err;
    }

    /// <summary>Close, wait the given settle, reopen. Returns true if the reopen succeeded.</summary>
    static bool CloseReopen(ref IntPtr h, int settleMs, out long errCode)
    {
        XSDK_Close(h);
        if (settleMs > 0) System.Threading.Thread.Sleep(settleMs);
        var r = XSDK_OpenEx("ENUM:0", out h, out _, IntPtr.Zero);
        errCode = r == 0 ? 0 : LastErr(IntPtr.Zero);
        return r == 0;
    }

    public static int Run(ref IntPtr handle)
    {
        Console.WriteLine("\n== Connect / disconnect cycling ==");
        var failures = 0;
        var h = handle;

        // 1. Opening a camera that is already open - what detection used to do on every rescan.
        Console.WriteLine("\n  -- opening an already-open camera --");
        var second = XSDK_OpenEx("ENUM:0", out var dup, out _, IntPtr.Zero);
        if (second == 0)
        {
            Console.WriteLine($"    this camera allowed a second handle (0x{dup.ToInt64():X}); closing it again");
            XSDK_Close(dup);
            System.Threading.Thread.Sleep(600);
        }
        else
        {
            var err = LastErr(IntPtr.Zero);
            Console.WriteLine($"    refused with errCode=0x{err:X}{(err == ERRCODE_SEQUENCE ? "  <- SEQUENCE: exactly the reported 'cannot connect'" : "")}");
            Console.WriteLine("    the plugin no longer does this: detection reuses the open session");
        }

        // 2. Close/reopen with no settle - what the plugin used to do.
        Console.WriteLine("\n  -- close then reopen with NO settle (previous behaviour) --");
        var noSettleFails = 0;
        for (var i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            var ok = CloseReopen(ref h, 0, out var err);
            Console.WriteLine($"    cycle {i + 1}: {(ok ? "opened" : $"FAILED errCode=0x{err:X}")} in {sw.ElapsedMilliseconds}ms");
            if (!ok)
            {
                noSettleFails++;
                System.Threading.Thread.Sleep(1500);
                if (XSDK_OpenEx("ENUM:0", out h, out _, IntPtr.Zero) != 0)
                {
                    Console.WriteLine("      could not recover; aborting this phase");
                    handle = h;
                    return failures + 1;
                }
            }
        }

        // 3. Close/reopen honouring the SDK's mandated 600ms - the new behaviour.
        Console.WriteLine("\n  -- close then reopen WITH the SDK's 600ms settle (new behaviour) --");
        var settleFails = 0;
        for (var i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            var ok = CloseReopen(ref h, 600, out var err);
            Console.WriteLine($"    cycle {i + 1}: {(ok ? "opened" : $"FAILED errCode=0x{err:X}")} in {sw.ElapsedMilliseconds}ms");
            if (!ok)
            {
                settleFails++;
                System.Threading.Thread.Sleep(1500);
                if (XSDK_OpenEx("ENUM:0", out h, out _, IntPtr.Zero) != 0)
                {
                    Console.WriteLine("      could not recover; aborting this phase");
                    handle = h;
                    return failures + 1;
                }
            }
        }

        Console.WriteLine($"\n    without settle: {noSettleFails}/5 reopens failed");
        Console.WriteLine($"    with 600ms settle: {settleFails}/5 reopens failed");
        if (settleFails > 0) failures++;

        handle = h;   // the cycling reopened the camera, so hand the live handle back
        Console.WriteLine($"\n  == session cycling: {(failures == 0 ? "PASS" : "FAIL")} ==");
        return failures;
    }
}
