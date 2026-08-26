using System;
using System.Linq;
using System.Runtime.InteropServices;
using static Probe.Sdk;

namespace Probe;

internal static class Program
{
    // Verified API codes / params from XAPIOpt.h
    const int SetLENR=0x2145, GetLENR=0x2146, CapLENR=0x218A;
    const int SetRawComp=0x2150, GetRawComp=0x2151, CapRawComp=0x218F;
    const int SetRawDepth=0x2160, GetRawDepth=0x2161, CapRawDepth=0x2193;
    const int SetCrop=0x2267, GetCrop=0x2268, CapCrop=0x2242;
    const int GetFLIndicator=0x226B, GetFLRange=0x226C, GetFLMode=0x226E;
    const int SetScaleUnit=0x4215, GetScaleUnit=0x4216, CapScaleUnit=0x4235;
    const int CapFocusPos=0x2259, GetFocusPos=0x2208;
    const int ON=0x0001, OFF=0x0002;

    static IntPtr h;
    static long origFocusMode;
    static int failures;

    static void Err(string what, int r)
    {
        XSDK_GetErrorNumber(h, out var api, out var err);
        Console.WriteLine($"    {what}: result={r} apiCode=0x{api:X} errCode=0x{err:X}");
    }

    static bool GetInt(string name, int code, int param, out long val)
    {
        var r = XSDK_GetProp(h, code, param, out val);
        if (r == 0) { Console.WriteLine($"  {name,-24} = {val} (0x{val:X})"); return true; }
        Err(name, r); return false;
    }

    static void CapList(string name, int code, int param)
    {
        var r = XSDK_CapProp_Count(h, code, param, out var n, IntPtr.Zero);
        if (r != 0 || n <= 0) { Err($"{name} (count)", r); return; }
        var buf = Marshal.AllocHGlobal((int)n * 8);
        try
        {
            r = XSDK_CapProp_Count(h, code, param, out n, buf);
            if (r != 0) { Err($"{name} (values)", r); return; }
            var vals = new long[n];
            for (int i = 0; i < n; i++) vals[i] = Marshal.ReadInt64(buf, i * 8);
            Console.WriteLine($"  {name,-24} supports {n}: [{string.Join(", ", vals)}]");
        }
        finally { Marshal.FreeHGlobal(buf); }
    }


    // Reads a property, writes a different value, reads it back to prove the write took effect,
    // then restores the original. Leaves the camera exactly as it was found.
    static void RoundTrip(string name, int getCode, int setCode, long testValue)
    {
        if (XSDK_GetProp(h, getCode, 1, out var original) != 0) { Console.WriteLine($"  {name}: cannot read, skipping"); return; }

        if (original == testValue)
        {
            Console.WriteLine($"  {name}: already {testValue}; nothing to prove without changing it, skipping");
            return;
        }

        var wr = XSDK_SetProp(h, setCode, 1, testValue);
        if (wr != 0) { Err($"{name} set {testValue}", wr); return; }

        if (XSDK_GetProp(h, getCode, 1, out var after) != 0) { Console.WriteLine($"  {name}: write ok but read-back failed"); return; }

        var ok = after == testValue;
        Console.WriteLine($"  {name}: {original} -> wrote {testValue} -> read {after}  {(ok ? "VERIFIED" : "MISMATCH")}");

        var rb = XSDK_SetProp(h, setCode, 1, original);
        XSDK_GetProp(h, getCode, 1, out var restored);
        Console.WriteLine($"    restored to {restored} (result={rb})");
    }

    static int Main(string[] args)
    {
        Console.WriteLine("== XSDK_Init ==");
        var r = XSDK_Init(IntPtr.Zero);
        Console.WriteLine($"  XSDK_Init -> {r}");
        if (r != 0) return 1;

        try
        {
            r = XSDK_Detect(1 /*USB*/, IntPtr.Zero, IntPtr.Zero, out var count);
            Console.WriteLine($"  XSDK_Detect -> {r}, cameras={count}");
            if (r != 0 || count <= 0) return 1;

            r = XSDK_OpenEx("ENUM:0", out h, out var mode, IntPtr.Zero);
            Console.WriteLine($"  XSDK_OpenEx -> {r}, handle=0x{h.ToInt64():X}, mode={mode}");
            if (r != 0) { Err("OpenEx", r); return 1; }

            try
            {
                var priorityResult = XSDK_SetPriorityMode(h, 0x0002); // PC priority

                if (args.Any(arg => string.Equals(arg, "--aperture-only", StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine("\n== Aperture-only hardware check ==");
                    Console.WriteLine($"  SetPriorityMode(PC) -> {priorityResult}");
                    if (XSDK_GetMode(h, out var apertureMode) == 0)
                        Console.WriteLine($"  Camera mode: 0x{apertureMode:X}");
                    var haveOriginalAeMode = XSDK_GetAEMode(h, out var apertureAeMode) == 0;
                    if (haveOriginalAeMode)
                        Console.WriteLine($"  AE mode: 0x{apertureAeMode:X}");
                    var apertureCodes = new System.Collections.Generic.HashSet<long>();
                    if (XSDK_GetDeviceInfoEx(h, out var apertureInfo, out var apertureApiCount, IntPtr.Zero) == 0)
                    {
                        Console.WriteLine($"  Camera: {apertureInfo.strProduct?.Trim()} firmware {apertureInfo.strFirmware?.Trim()}");
                        if (apertureApiCount > 0)
                        {
                            var nativeLongSize = OperatingSystem.IsWindows() ? sizeof(int) : sizeof(long);
                            var apiBuffer = Marshal.AllocHGlobal(checked((int)apertureApiCount * nativeLongSize));
                            try
                            {
                                var returnedCount = apertureApiCount;
                                if (XSDK_GetDeviceInfoEx(h, out _, out returnedCount, apiBuffer) == 0 &&
                                    returnedCount >= 0 && returnedCount <= apertureApiCount)
                                {
                                    for (var i = 0; i < returnedCount; i++)
                                    {
                                        var offset = checked((int)i * nativeLongSize);
                                        apertureCodes.Add(nativeLongSize == sizeof(int)
                                            ? Marshal.ReadInt32(apiBuffer, offset)
                                            : Marshal.ReadInt64(apiBuffer, offset));
                                    }
                                }
                            }
                            finally { Marshal.FreeHGlobal(apiBuffer); }
                        }
                    }
                    if (XSDK_GetLensInfo(h, out var apertureLens) == 0)
                        Console.WriteLine($"  Lens: {apertureLens.strProductName?.Trim()} ({apertureLens.strModel?.Trim()})");

                    var setManualResult = XSDK_SetAEMode(h, 0x0001);
                    if (setManualResult == 0)
                    {
                        Console.WriteLine("  Temporarily set AE mode Manual -> 0");
                    }
                    else
                    {
                        XSDK_GetErrorNumber(h, out var modeErrorApi, out var modeErrorCode);
                        Console.WriteLine($"  Temporarily set AE mode Manual -> {setManualResult}, API=0x{modeErrorApi:X}, error=0x{modeErrorCode:X}");
                    }
                    try
                    {
                        if (setManualResult == 0)
                            failures += Aperture.Run(h, apertureCodes);
                        else
                            failures++;
                    }
                    finally
                    {
                        if (haveOriginalAeMode)
                        {
                            var restoreAeResult = XSDK_SetAEMode(h, apertureAeMode);
                            Console.WriteLine($"  Restore AE mode 0x{apertureAeMode:X} -> {restoreAeResult}");
                        }
                    }
                    return failures == 0 ? 0 : 1;
                }

                Console.WriteLine("\n== Device ==");
                r = XSDK_GetDeviceInfoEx(h, out var info, out var apiCount, IntPtr.Zero);
                Console.WriteLine($"  GetDeviceInfoEx -> {r}, apiCount={apiCount}");
                Console.WriteLine($"  Product='{info.strProduct?.Trim()}' Firmware='{info.strFirmware?.Trim()}' Serial='{info.strSerialNo?.Trim()}' Framework='{info.strFramework?.Trim()}' size={Marshal.SizeOf<XSDK_DeviceInformation>()}");

                // --- the capability list the plugin now gates every optional feature on ---
                var codes = new System.Collections.Generic.HashSet<long>();
                if (apiCount > 0)
                {
                    var buf = Marshal.AllocHGlobal((int)apiCount * 8);
                    try
                    {
                        r = XSDK_GetDeviceInfoEx(h, out _, out var n2, buf);
                        Console.WriteLine($"  GetDeviceInfoEx(list) -> {r}, returned={n2}");
                        if (r == 0)
                            for (long i = 0; i < Math.Min(apiCount, n2); i++) codes.Add(Marshal.ReadInt64(buf, (int)i * 8));
                        Console.WriteLine($"  advertised API codes: {codes.Count}");
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                bool Adv(int c) => codes.Count == 0 || codes.Contains(c);
                Console.WriteLine($"  advertises SetRAWOutputDepth(0x2160)={Adv(SetRawDepth)}  SetRAWCompression(0x2150)={Adv(SetRawComp)}");
                Console.WriteLine($"  advertises SetLongExposureNR(0x2145)={Adv(SetLENR)}  SetCropMode(0x2267)={Adv(SetCrop)}");
                Console.WriteLine($"  advertises GetFocusLimiterIndicator(0x226B)={Adv(GetFLIndicator)}  GetFocusScaleUnit(0x4216)={Adv(GetScaleUnit)}");

                Console.WriteLine("\n== Tier 1 reads ==");
                GetInt("RAWOutputDepth", GetRawDepth, 1, out _);
                CapList("CapRAWOutputDepth", CapRawDepth, 2);
                GetInt("RAWCompression", GetRawComp, 1, out _);
                CapList("CapRAWCompression", CapRawComp, 2);
                GetInt("LongExposureNR", GetLENR, 1, out _);
                CapList("CapLongExposureNR", CapLENR, 2);

                Console.WriteLine("\n== Crop mode ==");
                {
                    var rc = XSDK_GetProp2(h, GetCrop, 2, out var cm, out var cs);
                    if (rc == 0) Console.WriteLine($"  CropMode                  = {cm} (0x{cm:X}), status={cs}");
                    else Err("GetCropMode", rc);
                }
                CapList("CapCropMode", CapCrop, 2);

                Console.WriteLine("\n== Focus limiter / scale ==");
                GetInt("FocusScaleUnit", GetScaleUnit, 1, out _);
                CapList("CapFocusScaleUnit", CapScaleUnit, 2);
                GetInt("FocusLimiterMode", GetFLMode, 1, out _);

                var isz = Marshal.SizeOf<FocusLimiterIndicator>();
                var ibuf = Marshal.AllocHGlobal(isz);
                try
                {
                    r = XSDK_GetProp_Struct(h, GetFLIndicator, 1, ibuf);
                    if (r == 0)
                    {
                        var ind = Marshal.PtrToStructure<FocusLimiterIndicator>(ibuf);
                        Console.WriteLine($"  FocusLimiterIndicator: current={ind.lCurrent} dofNear={ind.lDOF_Near} dofFar={ind.lDOF_Far} A={ind.lPos_A} B={ind.lPos_B} status={ind.lStatus}");
                    }
                    else Err("GetFocusLimiterIndicator", r);
                }
                finally { Marshal.FreeHGlobal(ibuf); }

                r = XSDK_GetProp_Count(h, GetFLRange, 2, out var nRanges, IntPtr.Zero);
                Console.WriteLine($"  FocusLimiterRange count -> result={r}, n={nRanges}");
                if (r == 0 && nRanges > 0)
                {
                    var esz = Marshal.SizeOf<FocusLimiter>();
                    var rbuf = Marshal.AllocHGlobal(esz * (int)nRanges);
                    try
                    {
                        r = XSDK_GetProp_Count(h, GetFLRange, 2, out nRanges, rbuf);
                        if (r == 0)
                            for (int i = 0; i < (int)nRanges; i++)
                            {
                                var fl = Marshal.PtrToStructure<FocusLimiter>(rbuf + i * esz);
                                Console.WriteLine($"    range[{i}]: A={fl.lPos_A} B={fl.lPos_B} (mm or 1/1000ft)");
                            }
                        else Err("GetFocusLimiterRange(values)", r);
                    }
                    finally { Marshal.FreeHGlobal(rbuf); }
                }

                Console.WriteLine("\n== Focus position (3.0.3.0 fix) ==");
                // The plugin forces manual focus mode before reading focus capability; do the same.
                if (XSDK_GetProp(h, 0x2202, 1, out var fmBefore) == 0)
                    { origFocusMode = fmBefore; }
                if (fmBefore != 0)
                    Console.WriteLine($"  focus mode before = 0x{fmBefore:X} ({(fmBefore==1?"MANUAL":fmBefore==0x8001?"AF-S":fmBefore==0x8002?"AF-C":"?")})");
                var setMf = XSDK_SetProp(h, 0x2201, 1, 0x0001);
                Console.WriteLine($"  set focus mode MANUAL -> {setMf}");
                if (setMf != 0) Err("SetFocusMode(MANUAL)", setMf);
                if (XSDK_GetProp(h, 0x2202, 1, out var fmAfter) == 0)
                    Console.WriteLine($"  focus mode after  = 0x{fmAfter:X}");
                int fsz = Marshal.SizeOf<FocusPosCap>();
                var fbuf = Marshal.AllocHGlobal(fsz);
                try
                {
                    var cap = new FocusPosCap { lSize = fsz, lVer = 0x00010000 };
                    Marshal.StructureToPtr(cap, fbuf, false);
                    long sz = fsz;
                    r = XSDK_CapProp_Focus(h, CapFocusPos, 2, ref sz, fbuf);
                    if (r == 0)
                    {
                        cap = Marshal.PtrToStructure<FocusPosCap>(fbuf);
                        Console.WriteLine($"  CapFocusPos: INF={cap.lInf} MOD={cap.lMod} overINF={cap.lOverInf} overMOD={cap.lOverMod} dof={cap.lDof} minStep={cap.lMinStep} (size={cap.lSize})");
                        if (XSDK_GetProp(h, GetFocusPos, 1, out var pos) == 0)
                        {
                            long lo = Math.Min(cap.lInf, cap.lMod) - Math.Abs(cap.lOverInf);
                            long hi = Math.Max(cap.lInf, cap.lMod) + Math.Abs(cap.lOverMod);
                            Console.WriteLine($"  current pulse={pos}  OLD position (pos-INF)={pos - cap.lInf}  NEW position (pos-travelMin)={pos - lo}  range=0..{hi - lo}  infinityAt={cap.lInf - lo}");
                        }
                    }
                    else Err("CapFocusPos", r);
                }
                finally { Marshal.FreeHGlobal(fbuf); }

                Console.WriteLine("\n== Focus MOVE round-trip (restores original position) ==");
                {
                    int fsz2 = Marshal.SizeOf<FocusPosCap>();
                    var fb2 = Marshal.AllocHGlobal(fsz2);
                    try
                    {
                        var c2 = new FocusPosCap { lSize = fsz2, lVer = 0x00010000 };
                        Marshal.StructureToPtr(c2, fb2, false);
                        long sz2 = fsz2;
                        if (XSDK_CapProp_Focus(h, CapFocusPos, 2, ref sz2, fb2) == 0)
                        {
                            c2 = Marshal.PtrToStructure<FocusPosCap>(fb2);
                            long lo = Math.Min(c2.lInf, c2.lMod) - Math.Abs(c2.lOverInf);
                            long hi = Math.Max(c2.lInf, c2.lMod) + Math.Abs(c2.lOverMod);
                            long step = c2.lMinStep > 0 ? c2.lMinStep : 1;
                            if (XSDK_GetProp(h, GetFocusPos, 1, out var start) == 0)
                            {
                                long startPos = start - lo;
                                // Move a small, safe amount well inside the travel, then come back.
                                long targetPos = Math.Clamp(startPos - 60, 0, hi - lo);
                                targetPos = (targetPos / step) * step;
                                long targetPulse = lo + targetPos;
                                Console.WriteLine($"  start pulse={start} (position {startPos}); moving to position {targetPos} (pulse {targetPulse})");
                                var mv = XSDK_SetProp(h, 0x2207, 1, targetPulse);
                                if (mv == 0)
                                {
                                    long actual = start;
                                    for (int i = 0; i < 40; i++)
                                    {
                                        System.Threading.Thread.Sleep(50);
                                        if (XSDK_GetProp(h, GetFocusPos, 1, out actual) == 0 && Math.Abs(actual - targetPulse) <= step) break;
                                    }
                                    Console.WriteLine($"  after move: pulse={actual} position={actual - lo}  {(Math.Abs(actual - targetPulse) <= step ? "VERIFIED" : "did not reach target")}");
                                    XSDK_SetProp(h, 0x2207, 1, start);
                                    for (int i = 0; i < 40; i++) { System.Threading.Thread.Sleep(50); if (XSDK_GetProp(h, GetFocusPos, 1, out actual) == 0 && Math.Abs(actual - start) <= step) break; }
                                    Console.WriteLine($"  restored to pulse={actual}");
                                }
                                else Err("SetFocusPos", mv);
                            }
                        }
                    }
                    finally { Marshal.FreeHGlobal(fb2); }
                }

                Console.WriteLine("\n== Focus settle characterisation ==");
                Settle.Run(h);

                Console.WriteLine("\n== WRITE round-trip (restores original values) ==");
                failures += Aperture.Run(h, codes);
                RoundTrip("RAWOutputDepth", GetRawDepth, SetRawDepth, 1);   // 16-bit -> 14-bit -> back
                RoundTrip("RAWCompression", GetRawComp, SetRawComp, 2);     // uncompressed -> lossless -> back
                RoundTrip("LongExposureNR", GetLENR, SetLENR, OFF);         // on -> off -> back
                {
                    // Crop mode reads back through the two-output getter (API_PARAM = 2).
                    if (XSDK_GetProp2(h, GetCrop, 2, out var orig, out var st0) == 0)
                    {
                        long target = orig == 0 ? 1 : 0;
                        var wr = XSDK_SetProp(h, SetCrop, 1, target);
                        if (wr == 0 && XSDK_GetProp2(h, GetCrop, 2, out var after, out var st1) == 0)
                        {
                            Console.WriteLine($"  CropMode: {orig} -> wrote {target} -> read {after}  {(after == target ? "VERIFIED" : "MISMATCH")}");
                            XSDK_SetProp(h, SetCrop, 1, orig);
                            XSDK_GetProp2(h, GetCrop, 2, out var restored, out _);
                            Console.WriteLine($"    restored to {restored}");
                        }
                        else Err("CropMode write", wr);
                    }
                }

                Console.WriteLine("\n== Shutter / ISO capability (3.0.4.0 fixes) ==");
                if (XSDK_GetMode(h, out var camMode) == 0) Console.WriteLine($"  camera Mode   = 0x{camMode:X}");
                if (XSDK_GetAEMode(h, out var aeMode) == 0) Console.WriteLine($"  camera AEMode = 0x{aeMode:X} ({(aeMode==1?"Manual":aeMode==3?"AperturePriority":aeMode==4?"ShutterPriority":aeMode==6?"Program":"?")})");
                if (XSDK_GetSensitivity(h, out var iso0) == 0) Console.WriteLine($"  current ISO   = {iso0}");
                long scount = 0;
                r = XSDK_CapShutterSpeed(h, ref scount, IntPtr.Zero, out var bulb);
                Console.WriteLine($"  CapShutterSpeed -> result={r}, codes={scount}, bulbCapable={bulb}");
                long icount = 0;
                r = XSDK_CapSensitivity(h, ref icount, IntPtr.Zero);
                Console.WriteLine($"  CapSensitivity(3-arg) -> result={r}, count={icount}");
                if (r == 0 && icount > 0)
                {
                    var ibuf2 = Marshal.AllocHGlobal((int)icount * 8);
                    try
                    {
                        r = XSDK_CapSensitivity(h, ref icount, ibuf2);
                        if (r == 0)
                        {
                            var iso = new long[Math.Min(icount, 8)];
                            for (int i = 0; i < iso.Length; i++) iso[i] = Marshal.ReadInt64(ibuf2, i * 8);
                            Console.WriteLine($"    first ISO values: [{string.Join(", ", iso)}] ...");
                        }
                    }
                    finally { Marshal.FreeHGlobal(ibuf2); }
                }
                if (scount > 0)
                {
                    var sbuf = Marshal.AllocHGlobal((int)scount * 8);
                    try
                    {
                        long n = scount;
                        r = XSDK_CapShutterSpeed(h, ref n, sbuf, out _);
                        if (r == 0)
                        {
                            var all = new System.Collections.Generic.List<long>();
                            for (long i = 0; i < n; i++) all.Add(Marshal.ReadInt64(sbuf, (int)i * 8));
                            all.Sort();
                            var longCodes = all.FindAll(c => c >= 64000000);
                            Console.WriteLine($"    total codes={all.Count}, fastest={all[0]}, slowest={all[all.Count-1]}");
                            Console.WriteLine($"    codes >= 60s: [{string.Join(", ", longCodes)}]");
                        }
                    }
                    finally { Marshal.FreeHGlobal(sbuf); }
                }

                Console.WriteLine("\n== CAPTURE round-trip (nothing written to the card) ==");

                Console.WriteLine("\n== Remaining plugin features ==");
                Regressions.Run(h, codes);
                failures += SessionCycle.Run(ref h);
                Extras.LensInfo(h);
                Extras.Battery(h);
                failures += PluginLogic.Run(h, info.strProduct?.Trim() ?? "", codes,
                    System.IO.Path.Combine(AppContext.BaseDirectory, "CameraConfigs"));
                failures += SettingsSweep.Run(h, new NINA.Plugins.Fujifilm.Devices.FujiApiCapabilities(codes.Select(c => (int)c)));
            }
            finally { Console.WriteLine("\n== closing =="); if (origFocusMode != 0) { var fr = XSDK_SetProp(h, 0x2201, 1, origFocusMode); Console.WriteLine($"  restored focus mode to 0x{origFocusMode:X} (result={fr})"); } XSDK_Close(h); System.Threading.Thread.Sleep(700); }
        }
        finally { XSDK_Exit(); }
        Console.WriteLine($"\n==== {(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED")} ====");
        return failures == 0 ? 0 : 1;
    }
}
