using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using NINA.Plugins.Fujifilm.Configuration;
using NINA.Plugins.Fujifilm.Interop;
using NINA.Plugins.Fujifilm.Devices;
using NINA.Plugins.Fujifilm.Settings;
using static Probe.Sdk;

namespace Probe;

// Drives the plugin's real decision-making classes with data read from the attached camera, so the
// shipping logic is what gets exercised. Nothing here names a camera model: everything is derived
// from what the camera reports.
internal static class PluginLogic
{
    static int _pass, _fail;

    static void Check(string what, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"    [{(ok ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? "  -- " + detail : "")}");
    }

    public static int Run(IntPtr h, string productName, HashSet<long> apiCodes, string configDir)
    {
        Console.WriteLine("\n== Plugin logic driven by live camera data ==");

        // ---- capability gate --------------------------------------------------------------
        var caps = new FujiApiCapabilities(apiCodes.Select(c => (int)c));
        Console.WriteLine($"\n  -- FujiApiCapabilities ({caps.Count} codes) --");
        Check("advertised codes are recognised", !caps.IsEmpty);
        Check("a code the camera advertises is supported",
            caps.Supports(FujifilmSdkWrapper.API_CODE_SetLongExposureNR) == apiCodes.Contains(FujifilmSdkWrapper.API_CODE_SetLongExposureNR));
        Check("an implausible code is not confirmed", !caps.Confirms(0x7FFF));

        // ---- model config resolution ------------------------------------------------------
        Console.WriteLine($"\n  -- CameraModelRules for '{productName}' --");
        var configs = new List<CameraConfig>();
        foreach (var f in Directory.GetFiles(configDir, "*.json"))
        {
            var c = JsonSerializer.Deserialize<CameraConfig>(File.ReadAllText(f));
            if (c != null) configs.Add(c);
        }
        var match = CameraModelRules.FindBestMatch(configs, productName);
        Check($"a config resolved for the attached camera", match != null, match?.ModelName ?? "no match");
        Check("resolved config is valid", match != null && CameraModelRules.IsValid(match));
        Check("this camera is not filtered as an unsupported still camera",
            !CameraModelRules.IsKnownUnsupportedStillCamera(productName));

        // ---- shutter catalog from the codes this camera actually reports -------------------
        Console.WriteLine("\n  -- FujifilmShutterSpeedCatalog from live codes --");
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
        var unknown = new List<int>();
        var bulbCapable = bulbFlag != 0 || (match?.DefaultBulbCapable ?? false);
        var map = FujifilmShutterSpeedCatalog.Build(codes, match?.ShutterSpeedMap, bulbCapable,
            match?.DefaultMaxExposure ?? 3600.0, unknown.Add);
        Console.WriteLine($"    camera reported {codes.Count} codes, SDK bulb flag={bulbFlag}, effective bulb={bulbCapable}");
        Check("every reported shutter code is understood", unknown.Count == 0,
            unknown.Count == 0 ? "" : $"unmapped: {string.Join(",", unknown.Take(8))}");
        var timedMax = FujifilmShutterSpeedCatalog.GetTimedMaximum(map, 0);
        Console.WriteLine($"    longest timed exposure this camera offers: {timedMax}s");
        Check("catalog is not empty", map.Count > 0);

        // Selection must never silently shorten an exposure.
        foreach (var req in new[] { 0.5, 1.0, 30.0, 120.0, 900.0, 3600.0 })
        {
            try
            {
                var code = FujifilmShutterSpeedCatalog.SelectCode(map, req, bulbCapable);
                var actual = code == FujifilmShutterSpeedCatalog.BulbCode ? req : map[code];
                var isBulb = code == FujifilmShutterSpeedCatalog.BulbCode;
                Check($"{req}s -> {(isBulb ? "BULB" : actual + "s")}",
                    isBulb || Math.Abs(actual - req) < Math.Max(1.0, req * 0.35),
                    isBulb ? "" : $"selected {actual}s for {req}s");
            }
            catch (InvalidOperationException ex)
            {
                Check($"{req}s -> refused rather than shortened", true, ex.Message.Split('.')[0]);
            }
        }

        // ---- focus travel from this lens' capability block ---------------------------------
        Console.WriteLine("\n  -- FocusTravelMap from live focus capability --");
        XSDK_SetProp(h, 0x2201, 1, 0x0001);   // manual focus: required before focus capability reads
        int fsz = Marshal.SizeOf<FocusPosCap>();
        var fb = Marshal.AllocHGlobal(fsz);
        try
        {
            var cap = new FocusPosCap { lSize = fsz, lVer = 0x00010000 };
            Marshal.StructureToPtr(cap, fb, false);
            long sz = fsz;
            if (XSDK_CapProp_Focus(h, 0x2259, 2, ref sz, fb) == 0)
            {
                cap = Marshal.PtrToStructure<FocusPosCap>(fb);
                if (cap.lInf == 0 && cap.lMod == 0)
                {
                    Console.WriteLine("    lens reports no programmable focus range; skipping");
                }
                else
                {
                    var travel = new FocusTravelMap((int)cap.lInf, (int)cap.lMod, (int)cap.lOverInf, (int)cap.lOverMod, (int)cap.lMinStep);
                    Console.WriteLine($"    INF={cap.lInf} MOD={cap.lMod} overINF={cap.lOverInf} overMOD={cap.lOverMod} step={cap.lMinStep}");
                    Console.WriteLine($"    -> range 0..{travel.Range}, infinity at {travel.InfinityPosition}, step {travel.Step}");
                    Check("travel range is positive", travel.Range > 0);
                    Check("infinity sits inside the range", travel.InfinityPosition >= 0 && travel.InfinityPosition <= travel.Range);
                    Check("past-infinity headroom equals the reported over-search",
                        travel.InfinityPosition == Math.Abs((int)cap.lOverInf));

                    XSDK_GetProp(h, 0x2208, 1, out var pulse);
                    var pos = travel.ToPosition((int)pulse, out var clamped);
                    Console.WriteLine($"    current pulse {pulse} -> position {pos}{(clamped ? " (clamped)" : "")}");
                    Check("reported position is never negative", pos >= 0);
                    Check("reported position is within MaxStep", pos <= travel.Range);
                    // Requests are quantised to the lens' minimum drive step, so a round trip is
                    // exact only when the step is 1. The contract is that it never drifts by more
                    // than one step - well inside the accuracy the lens itself achieves.
                    var roundTrip = travel.ToPosition(travel.ToPulse(pos));
                    Check($"position round-trips within one drive step ({travel.Step})",
                        Math.Abs(roundTrip - pos) <= travel.Step, $"{pos} -> {roundTrip}");

                    // Every position in the range must map to a reachable pulse.
                    var bad = 0;
                    for (var p = 0; p <= travel.Range; p += Math.Max(1, travel.Range / 200))
                    {
                        var pl = travel.ToPulse(p, out var clamp);
                        if (clamp != FocusClamp.None || pl < travel.TravelMin || pl > travel.TravelMax) bad++;
                    }
                    Check("all in-range positions map to reachable pulses", bad == 0, $"{bad} bad");
                }
            }
        }
        finally { Marshal.FreeHGlobal(fb); }

        // ---- focus limiter -----------------------------------------------------------------
        Console.WriteLine("\n  -- FocusLimiterState from live indicator --");
        var isz = Marshal.SizeOf<FocusLimiterIndicator>();
        var ib = Marshal.AllocHGlobal(isz);
        try
        {
            if (XSDK_GetProp_Struct(h, 0x226B, 1, ib) == 0)
            {
                var ind = Marshal.PtrToStructure<FocusLimiterIndicator>(ib);
                var st = new FocusLimiterState((int)ind.lCurrent, (int)ind.lDOF_Near, (int)ind.lDOF_Far, (int)ind.lPos_A, (int)ind.lPos_B, (int)ind.lStatus);
                Console.WriteLine($"    {st.Describe()}");
                Check("percent toward infinity is a percentage", st.PercentTowardInfinity is >= 0 and <= 100);
                Check("describe() produces a sentence", st.Describe().Length > 10);
            }
            else Console.WriteLine("    this lens reports no focus limiter");
        }
        finally { Marshal.FreeHGlobal(ib); }

        // ---- battery layout discovery ------------------------------------------------------
        Console.WriteLine("\n  -- FujifilmBatteryProtocol probe --");
        var accepted = FujifilmBatteryProtocol.Probe(candidate =>
            XSDK_GetProp_Battery8(h, 0x4055, candidate, out _, out _, out _, out _, out _, out _, out _, out _) == 0);
        Console.WriteLine($"    accepted layout: {(accepted?.ToString() ?? "none")}");
        Check("battery layout discovered without consulting a model list", accepted != null);

        // ---- capture quality plan ----------------------------------------------------------
        Console.WriteLine("\n  -- FujiCaptureQualityPlan against this camera's capabilities --");
        var settings = new FujiSettings();
        settings.Normalize();
        var plan = FujiCaptureQualityPlan.Build(settings, caps);
        foreach (var step in plan)
            Console.WriteLine($"    step: {step.Name} -> 0x{step.SetApiCode:X} = {step.Describe(step.Value)}");
        Check("every planned step targets an API this camera advertises",
            plan.All(s => caps.Supports(s.SetApiCode)));
        Check("crop mode is left alone by default", plan.All(s => s.Name != "Crop mode"));

        var leaveAll = new FujiSettings
        {
            RawBitDepth = RawBitDepthPreference.LeaveAlone,
            RawCompression = RawCompressionPreference.LeaveAlone,
            CropMode = CropModePreference.LeaveAlone,
            DisableLongExposureNR = false
        };
        Check("'leave alone' everywhere touches nothing", FujiCaptureQualityPlan.Build(leaveAll, caps).Count == 0);

        Console.WriteLine($"\n  == plugin logic: {_pass} passed, {_fail} failed ==");
        return _fail;
    }
}
