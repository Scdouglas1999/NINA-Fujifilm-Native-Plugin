using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NINA.Plugins.Fujifilm.Devices;
using NINA.Plugins.Fujifilm.Devices.LiveView;
using NINA.Plugins.Fujifilm.Interop;
using static Probe.Sdk;

namespace Probe;

// Runs the pre-fix logic and the shipped logic side by side against the attached camera, so each
// historical defect is demonstrated rather than asserted. The "before" column is the arithmetic or
// the call as it actually shipped.
internal static class Regressions
{
    static int _shown, _fixed, _notReproduced;

    static void Verdict(string title, string before, string after, bool isFixed, string note = "")
    {
        _shown++;
        if (isFixed) _fixed++; else _notReproduced++;
        Console.WriteLine($"\n  {title}");
        Console.WriteLine($"    before : {before}");
        Console.WriteLine($"    after  : {after}");
        Console.WriteLine($"    verdict: {(isFixed ? "FIXED - demonstrated on this camera" : "NOT REPRODUCED on this camera")}");
        if (note.Length > 0) Console.WriteLine($"    note   : {note}");
    }

    // ---- the 37 shutter codes added in 3.0.4.0; the shipped map before that had 82 entries ----
    static readonly int[] AddedShutterCodes =
    {
        44194, 88388, 176776, 353553, 2828427, 5656854, 11313708, 22627416,
        35000000, 40000000, 40317473, 45000000, 50000000, 50796833, 55000000, 60000000,
        80634947, 101593667, 128000000, 161269894, 203187334, 256000000, 322539788,
        406374669, 512000000, 645079577, 812749338, 1024000000, 1290159155, 1625498677,
        2048000000, 64000030, 64000060, 64000090, 64000120, 64000150, 64000180
    };

    public static void Run(IntPtr h, IReadOnlyCollection<long> apiCodes)
    {
        Console.WriteLine("\n================ BEFORE / AFTER on the attached camera ================");

        // 1 ---- XSDK_CapSensitivity declared with four parameters instead of three -------------
        {
            long badDr = 100, badCount = 0;
            var oldResult = XSDK_CapSensitivity_FourArg(h, ref badDr, ref badCount, IntPtr.Zero);
            long newCount = 0;
            var newResult = XSDK_CapSensitivity(h, ref newCount, IntPtr.Zero);
            Verdict("ISO enumeration (3.0.4.0)",
                $"4-parameter call -> result={oldResult}, count={badCount}",
                $"3-parameter call -> result={newResult}, count={newCount}",
                badCount == 0 && newCount > 0,
                "the extra leading argument shifted every parameter, so the count landed in the caller's own variable");
        }

        // 2 ---- XSDK_MEDIAREC_OFF was 0, which is not a defined value --------------------------
        {
            XSDK_GetMediaRecord(h, out var original);
            var oldSet = XSDK_SetMediaRecord(h, 0);
            XSDK_GetErrorNumber(h, out _, out var oldErr);
            var newSet = XSDK_SetMediaRecord(h, FujifilmSdkWrapper.XSDK_MEDIAREC_OFF);
            XSDK_GetMediaRecord(h, out var nowValue);
            XSDK_SetMediaRecord(h, original);
            Verdict("Disabling card recording (3.0.4.0)",
                $"SetMediaRecord(0) -> result={oldSet}, errCode=0x{oldErr:X}",
                $"SetMediaRecord(0x4) -> result={newSet}, reads back {nowValue}",
                oldSet != 0 && newSet == 0 && nowValue == FujifilmSdkWrapper.XSDK_MEDIAREC_OFF,
                "card writes were never actually disabled, despite the code intending to");
        }

        // 3 ---- focus travel: nominal marks only, versus including the over-search -------------
        XSDK_SetProp(h, FujifilmSdkWrapper.XSDK_API_CODE_SetFocusMode, 1, FujifilmSdkWrapper.XSDK_FOCUS_MANUAL);
        int fsz = Marshal.SizeOf<FocusPosCap>();
        var fb = Marshal.AllocHGlobal(fsz);
        FocusPosCap cap = default;
        var haveFocus = false;
        try
        {
            var seed = new FocusPosCap { lSize = fsz, lVer = 0x00010000 };
            Marshal.StructureToPtr(seed, fb, false);
            long sz = fsz;
            if (XSDK_CapProp_Focus(h, FujifilmSdkWrapper.XSDK_API_CODE_CapFocusPos, 2, ref sz, fb) == 0)
            {
                cap = Marshal.PtrToStructure<FocusPosCap>(fb);
                haveFocus = cap.lInf != 0 || cap.lMod != 0;
            }
        }
        finally { Marshal.FreeHGlobal(fb); }

        if (haveFocus)
        {
            var travel = new FocusTravelMap((int)cap.lInf, (int)cap.lMod, (int)cap.lOverInf, (int)cap.lOverMod, (int)cap.lMinStep);
            var oldMin = (int)Math.Min(cap.lInf, cap.lMod);
            var oldMax = (int)Math.Max(cap.lInf, cap.lMod);

            Verdict("Focuser travel and infinity (3.0.3.0)",
                $"range 0-{oldMax - oldMin}, infinity at position {(int)cap.lInf - oldMin}, no travel beneath it",
                $"range 0-{travel.Range}, infinity at position {travel.InfinityPosition}, {travel.OverSearchInfinity} steps beneath it",
                travel.InfinityPosition > 0 && travel.Range > oldMax - oldMin,
                $"this lens reports {Math.Abs(cap.lOverInf)} pulses of past-infinity travel that used to be discarded");

            XSDK_GetProp(h, FujifilmSdkWrapper.XSDK_API_CODE_GetFocusPos, 1, out var pulse);
            var oldPos = (int)pulse - oldMin;
            var newPos = travel.ToPosition((int)pulse);
            Verdict("Focuser position sign (3.0.3.0)",
                $"pulse {pulse} -> position {oldPos}",
                $"pulse {pulse} -> position {newPos}",
                oldPos < 0 && newPos >= 0,
                oldPos < 0 ? "the old mapping reported a negative position, which aborts an autofocus run"
                           : "this lens is not currently parked past infinity, so the old mapping happens to be positive here too");

            // 4 ---- requests snapped to the minimum drive step --------------------------------
            var step = travel.Step;
            var oldReachable = new HashSet<int>();
            var newReachable = new HashSet<int>();
            for (var p = 0; p <= travel.Range; p++)
            {
                oldReachable.Add(step > 1 ? (int)Math.Round(p / (double)step) * step : p);
                newReachable.Add(travel.ToPosition(travel.ToPulse(p)));
            }
            Verdict("Focuser positions that do not exist (3.1.1.0)",
                $"{oldReachable.Count} of {travel.Range + 1} positions reachable (snapped to multiples of {step})",
                $"{newReachable.Count} of {travel.Range + 1} positions reachable",
                newReachable.Count == travel.Range + 1 && oldReachable.Count < travel.Range + 1,
                $"asking for a position the old code could not produce left N.I.N.A. waiting forever; drive step here is {step}");

            // 5 ---- move verification demanded exact arrival ----------------------------------
            XSDK_GetProp(h, FujifilmSdkWrapper.XSDK_API_CODE_GetFocusPos, 1, out var start);
            var targetPos = Math.Clamp(travel.ToPosition((int)start) - 200, 0, travel.Range);
            var targetPulse = travel.ToPulse(targetPos);
            if (XSDK_SetProp(h, FujifilmSdkWrapper.XSDK_API_CODE_SetFocusPos, 1, targetPulse) == 0)
            {
                long last = long.MinValue; var stable = 0; long settled = start;
                for (var i = 0; i < 60; i++)
                {
                    System.Threading.Thread.Sleep(50);
                    if (XSDK_GetProp(h, FujifilmSdkWrapper.XSDK_API_CODE_GetFocusPos, 1, out settled) != 0) break;
                    if (Math.Abs(settled - last) <= step) { if (++stable >= 4) break; } else stable = 0;
                    last = settled;
                }
                var residual = settled - targetPulse;
                var oldWouldAccept = Math.Abs(residual) <= Math.Max(1, step);
                Verdict("Focus move verification (3.1.0.0)",
                    oldWouldAccept
                        ? $"required |{residual}| <= {Math.Max(1, step)} -> satisfied here, so the move would have completed"
                        : $"required |{residual}| <= {Math.Max(1, step)} -> never satisfied, so the move would have timed out",
                    $"settled at pulse {settled}, {residual} from the commanded {targetPulse}; accepted once the lens stopped moving",
                    !oldWouldAccept,
                    oldWouldAccept
                        ? "this lens landed exactly on target for this move, so the old check passes too - the failure needs a lens that reports an offset"
                        : "the lens had already arrived; the old check simply could not tell");
                XSDK_SetProp(h, FujifilmSdkWrapper.XSDK_API_CODE_SetFocusPos, 1, start);
                System.Threading.Thread.Sleep(800);
            }
        }
        else Console.WriteLine("\n  (focus checks skipped: no lens reporting a programmable focus range)");

        // 6 ---- shutter catalogue and the 60 second ceiling ------------------------------------
        {
            long n = 0;
            XSDK_CapShutterSpeed(h, ref n, IntPtr.Zero, out var bulbFlag);
            var codes = new List<int>();
            if (n > 0)
            {
                var buf = Marshal.AllocHGlobal((int)n * 8);
                try
                {
                    var nn = n;
                    if (XSDK_CapShutterSpeed(h, ref nn, buf, out _) == 0)
                        for (long i = 0; i < nn; i++) codes.Add((int)Marshal.ReadInt64(buf, (int)i * 8));
                }
                finally { Marshal.FreeHGlobal(buf); }
            }

            if (codes.Count(c => c != -1) < 10)
            {
                Console.WriteLine($"\n  Shutter checks skipped: the camera offered only {codes.Count(c => c != -1)} timed code(s).");
                Console.WriteLine("    Set the exposure mode to Manual to enumerate the full list.");
            }
            else
            {
                var newMap = FujifilmShutterSpeedCatalog.Build(codes, null, true, 3600.0);
                var oldMap = newMap.Where(kv => !AddedShutterCodes.Contains(kv.Key) && kv.Key != FujifilmShutterSpeedCatalog.BulbCode)
                                   .ToDictionary(kv => kv.Key, kv => kv.Value);
                var rejected = codes.Count(c => c != -1 && !oldMap.ContainsKey(c));
                var oldTimedMax = oldMap.Count > 0 ? oldMap.Values.Max() : 0;
                var newTimedMax = FujifilmShutterSpeedCatalog.GetTimedMaximum(newMap, 0);

                Verdict("Shutter codes the plugin understood (3.0.4.0)",
                    $"{rejected} of the camera's {codes.Count(c => c != -1)} timed codes rejected as undocumented",
                    $"0 rejected; longest timed exposure {newTimedMax}s (was {oldTimedMax}s)",
                    rejected > 0 && newTimedMax > oldTimedMax);

                var oldBulb = bulbFlag != 0;                                   // 3.0.2.0: SDK flag alone
                var newBulb = FujifilmBulbCapability.Resolve(bulbFlag != 0, true);
                var oldCeiling = oldBulb ? 3600.0 : oldTimedMax;
                var newCeiling = FujifilmBulbCapability.ResolveMaximumExposureSeconds(newBulb, 3600.0, newTimedMax);
                Verdict("Maximum exposure offered (3.1.0.0)",
                    $"SDK bulb flag={oldBulb} taken as authoritative -> ceiling {oldCeiling}s",
                    $"model configuration honoured -> bulb={newBulb}, ceiling {newCeiling}s",
                    newCeiling >= 3600.0 && oldCeiling < 3600.0,
                    "this is the regression that made maximum exposure 60 seconds from 3.0.2.0");
            }
        }

        // 7 ---- auto-ISO modes leaking into the ISO list ---------------------------------------
        {
            long isoCount = 0;
            XSDK_CapSensitivity(h, ref isoCount, IntPtr.Zero);
            if (isoCount > 0)
            {
                var buf = Marshal.AllocHGlobal((int)isoCount * 8);
                try
                {
                    var n2 = isoCount;
                    XSDK_CapSensitivity(h, ref n2, buf);
                    var raw = new List<int>();
                    for (var i = 0; i < n2; i++) raw.Add((int)Marshal.ReadInt64(buf, i * 8));
                    var kept = FujifilmSensitivityCatalog.SelectFixedSensitivities(raw, out var dropped);
                    var autos = raw.Where(v => v <= 0).ToArray();
                    Verdict("Auto-ISO modes offered as ISO values (3.1.0.0)",
                        $"all {raw.Count} entries offered, including {autos.Length} auto mode(s) [{string.Join(",", autos)}]",
                        $"{kept.Count} fixed sensitivities, {dropped} auto mode(s) filtered out",
                        autos.Length > 0 && kept.All(v => v > 0));
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }

        // 8 ---- GetCropMode writes two values -------------------------------------------------
        {
            // XSDK_GetProp is variadic: it writes through N pointer *arguments*, not into one
            // buffer. Seed two separate outputs with a sentinel and see how many get overwritten.
            const long sentinel = 0x5A5A5A5A;
            long slot1 = sentinel, slot2 = sentinel;
            var r = XSDK_GetProp_TwoRef(h, FujifilmSdkWrapper.API_CODE_GetCropMode, 2, ref slot1, ref slot2);
            var wroteTwo = r == 0 && slot1 != sentinel && slot2 != sentinel;
            Verdict("Reading crop mode (3.1.0.0)",
                wroteTwo
                    ? "one output pointer was supplied, so the second value was written through an unrelated argument slot"
                    : "one output pointer was supplied",
                $"two output pointers supplied; the SDK wrote {(wroteTwo ? "both" : slot1 != sentinel ? "one" : "neither")} (mode={slot1}, status={slot2})",
                wroteTwo,
                wroteTwo
                    ? "supplying a single output previously corrupted the heap; the call reports the number of outputs in its API parameter"
                    : "this camera returned fewer values than the reference documents, so the overrun is not reproducible here");
        }

        // 9 ---- live view zoom took a magnification instead of a code ---------------------------
        {
            var zoomCodes = CapValues(h, FujifilmSdkWrapper.API_CODE_CapThroughImageZoom, 2);
            if (zoomCodes.Length > 0)
            {
                const double wanted = 24.0;
                var oldSent = (int)wanted;                                   // clamped 1-24, sent raw
                var newCode = LiveViewZoomLevels.SelectCodeFor(zoomCodes, wanted);
                var oldAccepted = XSDK_SetProp(h, FujifilmSdkWrapper.API_CODE_SetThroughImageZoom, 1, oldSent) == 0;
                var newAccepted = XSDK_SetProp(h, FujifilmSdkWrapper.API_CODE_SetThroughImageZoom, 1, newCode ?? 1) == 0;
                XSDK_SetProp(h, FujifilmSdkWrapper.API_CODE_SetThroughImageZoom, 1, zoomCodes[0]);
                Verdict("Live view zoom (3.1.0.0)",
                    $"asking for x{wanted:0} sent the value {oldSent} -> {(oldAccepted ? "accepted" : "refused by camera")}",
                    $"asking for x{wanted:0} sends code 0x{newCode:X} = {LiveViewZoomLevels.Describe(newCode ?? 0)} -> {(newAccepted ? "accepted" : "refused")}",
                    !oldAccepted && newAccepted,
                    $"valid codes are 0x01-0x11; this camera offers [{string.Join(", ", LiveViewZoomLevels.DescribeAvailable(zoomCodes).Select(a => "x" + a.Magnification))}]");
            }
        }

        // 10 ---- live view default quality the camera rejects ------------------------------------
        {
            var normal = XSDK_SetProp(h, FujifilmSdkWrapper.API_CODE_SetLiveViewImageQuality, 1, (int)NINA.Plugins.Fujifilm.Devices.LiveView.LiveViewQuality.Normal);
            var fine = XSDK_SetProp(h, FujifilmSdkWrapper.API_CODE_SetLiveViewImageQuality, 1, (int)NINA.Plugins.Fujifilm.Devices.LiveView.LiveViewQuality.Fine);
            Verdict("Live view default quality (3.1.0.0)",
                $"default was Normal -> result={normal}",
                $"default is Fine -> result={fine}, and a refused value falls back to Fine",
                normal != 0 && fine == 0,
                "a rejected quality left the camera on whatever it happened to be using");
        }

        // 11 ---- XSDK_GetImageSize was never an export -------------------------------------------
        {
            var threw = false;
            try { XSDK_GetImageSize(h, out _); }
            catch (EntryPointNotFoundException) { threw = true; }
            catch (Exception) { threw = true; }
            Verdict("XSDK_GetImageSize (3.1.0.0)",
                threw ? "calling it throws EntryPointNotFoundException; it is not an export" : "resolved unexpectedly",
                "the declaration was removed",
                threw);
        }

        Console.WriteLine($"\n================ {_fixed} of {_shown} demonstrated fixed on this camera, {_notReproduced} not reproduced ================");
    }

    static int[] CapValues(IntPtr h, int code, int param)
    {
        if (XSDK_CapProp_Count(h, code, param, out var n, IntPtr.Zero) != 0 || n <= 0) return Array.Empty<int>();
        var buf = Marshal.AllocHGlobal((int)n * 8);
        try
        {
            if (XSDK_CapProp_Count(h, code, param, out n, buf) != 0) return Array.Empty<int>();
            var v = new int[n];
            for (var i = 0; i < n; i++) v[i] = (int)Marshal.ReadInt64(buf, i * 8);
            return v;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
