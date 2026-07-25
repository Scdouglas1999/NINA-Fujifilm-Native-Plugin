using System;
using System.Runtime.InteropServices;
using static Probe.Sdk;

namespace Probe;

// Watches how a Fujifilm lens actually converges on a commanded focus pulse, so the plugin's
// move-verification tolerance and timeout can be based on measured behaviour rather than a guess.
internal static class Settle
{
    const int SetFocusPos = 0x2207, GetFocusPos = 0x2208, CapFocusPos = 0x2259;

    public static void Run(IntPtr h)
    {
        int fsz = Marshal.SizeOf<FocusPosCap>();
        var fb = Marshal.AllocHGlobal(fsz);
        try
        {
            var cap = new FocusPosCap { lSize = fsz, lVer = 0x00010000 };
            Marshal.StructureToPtr(cap, fb, false);
            long sz = fsz;
            if (XSDK_CapProp_Focus(h, CapFocusPos, 2, ref sz, fb) != 0) { Console.WriteLine("  CapFocusPos failed"); return; }
            cap = Marshal.PtrToStructure<FocusPosCap>(fb);
            long step = cap.lMinStep > 0 ? cap.lMinStep : 1;

            XSDK_GetProp(h, GetFocusPos, 1, out var origin);
            Console.WriteLine($"  minStep={step}, origin pulse={origin}");

            foreach (var delta in new long[] { -300, 300 })
            {
                var target = origin + delta;
                target = (target / step) * step;
                Console.WriteLine($"\n  --- commanding {target} (delta {delta}) ---");
                var t0 = DateTime.UtcNow;
                if (XSDK_SetProp(h, SetFocusPos, 1, target) != 0) { Console.WriteLine("   SetFocusPos failed"); continue; }

                long last = long.MinValue; int stable = 0;
                for (int i = 0; i < 120; i++)
                {
                    System.Threading.Thread.Sleep(50);
                    if (XSDK_GetProp(h, GetFocusPos, 1, out var pos) != 0) break;
                    var ms = (DateTime.UtcNow - t0).TotalMilliseconds;
                    if (pos == last) { stable++; } else { stable = 0; Console.WriteLine($"    t={ms,6:0}ms pos={pos,6} err={pos - target,5}"); }
                    last = pos;
                    if (stable >= 8) { Console.WriteLine($"    settled at {pos} after {ms:0}ms, final error {pos - target} pulses ({Math.Abs(pos - target) / (double)step:0.#} x minStep)"); break; }
                }
            }

            Console.WriteLine($"\n  --- restoring to {origin} ---");
            XSDK_SetProp(h, SetFocusPos, 1, origin);
            for (int i = 0; i < 120; i++) { System.Threading.Thread.Sleep(50); XSDK_GetProp(h, GetFocusPos, 1, out var p); if (Math.Abs(p - origin) <= step) { Console.WriteLine($"    back at {p}"); break; } }
            XSDK_GetProp(h, GetFocusPos, 1, out var fin);
            Console.WriteLine($"    final pulse={fin} (origin {origin}, error {fin - origin})");
        }
        finally { Marshal.FreeHGlobal(fb); }
    }
}
