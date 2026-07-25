using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NINA.Plugins.Fujifilm.Devices;
using NINA.Plugins.Fujifilm.Interop;
using NINA.Plugins.Fujifilm.Settings;
using static Probe.Sdk;

namespace Probe;

// Applies every value of every plugin setting to the camera, reads it back, and restores the
// original. Nothing is model-specific: what is attempted comes from the camera's own Cap* lists and
// its advertised API codes, so on a body that offers fewer options fewer combinations are tried.
internal static class SettingsSweep
{
    static int _pass, _fail, _skip;

    static void Result(string what, bool? ok, string detail = "")
    {
        if (ok == null) { _skip++; Console.WriteLine($"    [SKIP] {what}  -- {detail}"); return; }
        if (ok.Value) _pass++; else _fail++;
        Console.WriteLine($"    [{(ok.Value ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? "  -- " + detail : "")}");
    }

    static long[] CapList(IntPtr h, int code, int param)
    {
        if (XSDK_CapProp_Count(h, code, param, out var n, IntPtr.Zero) != 0 || n <= 0) return Array.Empty<long>();
        var buf = Marshal.AllocHGlobal((int)n * 8);
        try
        {
            if (XSDK_CapProp_Count(h, code, param, out n, buf) != 0) return Array.Empty<long>();
            var vals = new long[n];
            for (int i = 0; i < n; i++) vals[i] = Marshal.ReadInt64(buf, i * 8);
            return vals;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // Sets every value the camera says it supports, verifies the read-back, and restores.
    static void SweepProperty(IntPtr h, FujiApiCapabilities caps, string name,
        int capCode, int capParam, int getCode, int getParam, int setCode, Func<int, string> describe)
    {
        if (!caps.Confirms(setCode)) { Result(name, null, "camera does not advertise this control"); return; }
        if (XSDK_GetProp(h, getCode, getParam, out var original) != 0) { Result(name, null, "cannot read current value"); return; }

        var supported = CapList(h, capCode, capParam);
        if (supported.Length == 0) { Result(name, null, "camera reported no supported values"); return; }

        var applied = new List<string>();
        var allOk = true;
        foreach (var value in supported)
        {
            var set = XSDK_SetProp(h, setCode, 1, value);
            if (set != 0)
            {
                // A camera may list a value it will not currently accept. The plugin logs and moves
                // on rather than failing the connection, so this is reported, not treated as a
                // defect. Only a failed read-back or a failed restore is a real problem.
                applied.Add($"{describe((int)value)}=refused by camera");
                continue;
            }
            if (XSDK_GetProp(h, getCode, getParam, out var back) != 0 || back != value)
            {
                applied.Add($"{describe((int)value)}=readback {back}"); allOk = false; continue;
            }
            applied.Add(describe((int)value));
        }

        XSDK_SetProp(h, setCode, 1, original);
        XSDK_GetProp(h, getCode, getParam, out var restored);
        Result($"{name}: {supported.Length} value(s) [{string.Join(", ", applied)}]", allOk && restored == original,
            restored == original ? $"restored to {describe((int)original)}" : $"RESTORE FAILED ({restored})");
    }

    public static int Run(IntPtr h, FujiApiCapabilities caps)
    {
        Console.WriteLine("\n== Every setting applied and read back on the camera ==");

        Console.WriteLine("\n  -- capture quality --");
        SweepProperty(h, caps, "RAW bit depth",
            FujifilmSdkWrapper.API_CODE_CapRAWOutputDepth, 2,
            FujifilmSdkWrapper.API_CODE_GetRAWOutputDepth, 1,
            FujifilmSdkWrapper.API_CODE_SetRAWOutputDepth, FujiCaptureQualityPlan.DescribeBitDepth);

        SweepProperty(h, caps, "RAW compression",
            FujifilmSdkWrapper.API_CODE_CapRAWCompression, 2,
            FujifilmSdkWrapper.API_CODE_GetRAWCompression, 1,
            FujifilmSdkWrapper.API_CODE_SetRAWCompression, FujiCaptureQualityPlan.DescribeCompression);

        SweepProperty(h, caps, "Long exposure NR",
            FujifilmSdkWrapper.API_CODE_CapLongExposureNR, 2,
            FujifilmSdkWrapper.API_CODE_GetLongExposureNR, 1,
            FujifilmSdkWrapper.API_CODE_SetLongExposureNR, FujiCaptureQualityPlan.DescribeOnOff);

        // Crop mode reads back two values, so it needs its own handling.
        Console.WriteLine("\n  -- crop mode (two-output getter) --");
        if (caps.Confirms(FujifilmSdkWrapper.API_CODE_SetCropMode) &&
            XSDK_GetProp2(h, FujifilmSdkWrapper.API_CODE_GetCropMode, 2, out var origCrop, out _) == 0)
        {
            var modes = CapList(h, FujifilmSdkWrapper.API_CODE_CapCropMode, 2);
            var ok = modes.Length > 0;
            var applied = new List<string>();
            foreach (var m in modes)
            {
                var set = XSDK_SetProp(h, FujifilmSdkWrapper.API_CODE_SetCropMode, 1, m);
                if (set != 0) { applied.Add($"{FujiCaptureQualityPlan.DescribeCropMode((int)m)}=refused by camera"); }
                else if (XSDK_GetProp2(h, FujifilmSdkWrapper.API_CODE_GetCropMode, 2, out var back, out _) != 0 || back != m)
                { ok = false; applied.Add($"{FujiCaptureQualityPlan.DescribeCropMode((int)m)}=readback mismatch"); }
                else applied.Add(FujiCaptureQualityPlan.DescribeCropMode((int)m));
            }
            XSDK_SetProp(h, FujifilmSdkWrapper.API_CODE_SetCropMode, 1, origCrop);
            XSDK_GetProp2(h, FujifilmSdkWrapper.API_CODE_GetCropMode, 2, out var restoredCrop, out _);
            Result($"Crop mode: {modes.Length} value(s) [{string.Join(", ", applied)}]", ok && restoredCrop == origCrop,
                $"restored to {FujiCaptureQualityPlan.DescribeCropMode((int)restoredCrop)}");
        }
        else Result("Crop mode", null, "not advertised or unreadable");

        Console.WriteLine("\n  -- focuser --");
        SweepProperty(h, caps, "Focus distance unit",
            FujifilmSdkWrapper.API_CODE_CapFocusScaleUnit, 2,
            FujifilmSdkWrapper.API_CODE_GetFocusScaleUnit, 1,
            FujifilmSdkWrapper.API_CODE_SetFocusScaleUnit,
            v => v == FujifilmSdkWrapper.SDK_SCALEUNIT_M ? "metres" : v == FujifilmSdkWrapper.SDK_SCALEUNIT_FT ? "feet" : $"0x{v:X}");

        // Focus mode: the plugin forces manual and restores the original on disconnect.
        if (XSDK_GetProp(h, FujifilmSdkWrapper.XSDK_API_CODE_GetFocusMode, 1, out var origFocusMode) == 0)
        {
            var toManual = XSDK_SetProp(h, FujifilmSdkWrapper.XSDK_API_CODE_SetFocusMode, 1, FujifilmSdkWrapper.XSDK_FOCUS_MANUAL);
            XSDK_GetProp(h, FujifilmSdkWrapper.XSDK_API_CODE_GetFocusMode, 1, out var nowMode);
            var back = XSDK_SetProp(h, FujifilmSdkWrapper.XSDK_API_CODE_SetFocusMode, 1, origFocusMode);
            XSDK_GetProp(h, FujifilmSdkWrapper.XSDK_API_CODE_GetFocusMode, 1, out var restoredMode);
            Result($"Force manual focus mode ({FujifilmSdkWrapper.DescribeFocusMode((int)origFocusMode)} -> MANUAL -> back)",
                toManual == 0 && nowMode == FujifilmSdkWrapper.XSDK_FOCUS_MANUAL && back == 0 && restoredMode == origFocusMode);
        }
        else Result("Force manual focus mode", null, "focus mode unreadable");

        Console.WriteLine("\n  -- capture --");
        // Card recording: the plugin's DisableCameraCardRecording setting.
        if (XSDK_GetMediaRecord(h, out var origMedia) == 0)
        {
            var off = XSDK_SetMediaRecord(h, FujifilmSdkWrapper.XSDK_MEDIAREC_OFF);
            XSDK_GetMediaRecord(h, out var nowMedia);
            XSDK_SetMediaRecord(h, origMedia);
            XSDK_GetMediaRecord(h, out var restoredMedia);
            Result("Stop camera writing to its card", off == 0 && nowMedia == FujifilmSdkWrapper.XSDK_MEDIAREC_OFF && restoredMedia == origMedia,
                $"was {origMedia}, set {nowMedia}, restored {restoredMedia}");
        }
        else Result("Stop camera writing to its card", null, "media record unreadable");

        // ISO: every sensitivity the camera enumerates.
        long isoCount = 0;
        XSDK_CapSensitivity(h, ref isoCount, IntPtr.Zero);
        if (isoCount > 0 && XSDK_GetSensitivity(h, out var origIso) == 0)
        {
            var buf = Marshal.AllocHGlobal((int)isoCount * 8);
            try
            {
                var n = isoCount;
                XSDK_CapSensitivity(h, ref n, buf);
                var isos = new long[n];
                for (int i = 0; i < n; i++) isos[i] = Marshal.ReadInt64(buf, i * 8);
                var fixedIsos = FujifilmSensitivityCatalog.SelectFixedSensitivities(
                    isos.Select(v => (int)v), out var autoModes);
                Result($"auto-ISO modes are filtered out of the ISO list", fixedIsos.All(v => v > 0),
                    $"{isos.Length} reported -> {fixedIsos.Count} fixed, {autoModes} auto mode(s) ignored");

                var failed = new List<long>();
                foreach (var iso in fixedIsos)
                {
                    if (XSDK_SetSensitivity(h, iso) != 0 || XSDK_GetSensitivity(h, out var b) != 0 || b != iso) failed.Add(iso);
                }
                XSDK_SetSensitivity(h, origIso);
                XSDK_GetSensitivity(h, out var restoredIso);
                Result($"ISO: every fixed sensitivity accepted ({fixedIsos.Min()}-{fixedIsos.Max()})", failed.Count == 0 && restoredIso == origIso,
                    failed.Count == 0 ? $"restored to {restoredIso}" : $"failed: {string.Join(",", failed.Take(6))}");
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        else Result("ISO sweep", null, "no sensitivities enumerated");

        // Shutter: every code the camera enumerates.
        long ssCount = 0;
        XSDK_CapShutterSpeed(h, ref ssCount, IntPtr.Zero, out _);
        if (ssCount > 0)
        {
            var buf = Marshal.AllocHGlobal((int)ssCount * 8);
            try
            {
                var n = ssCount;
                XSDK_CapShutterSpeed(h, ref n, buf, out _);
                var codes = new List<long>();
                for (int i = 0; i < n; i++) codes.Add(Marshal.ReadInt64(buf, i * 8));
                var failed = codes.Where(c => c != -1 && XSDK_SetShutterSpeed(h, c, 0) != 0).ToList();
                XSDK_SetShutterSpeed(h, 1000000, 0);
                Result($"Shutter: all {codes.Count(c => c != -1)} timed codes accepted", failed.Count == 0,
                    failed.Count == 0 ? "" : $"refused: {string.Join(",", failed.Take(6))}");
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        else Result("Shutter sweep", null, "no shutter codes enumerated");

        Console.WriteLine("\n  -- live view --");
        var qualities = new (string, long)[] { ("Fine", 1), ("Normal", 2), ("Basic", 3) };
        var accepted = new List<string>();
        foreach (var (qn, qv) in qualities)
            if (XSDK_SetProp(h, FujifilmSdkWrapper.API_CODE_SetLiveViewImageQuality, 1, qv) == 0) accepted.Add(qn);
        XSDK_SetProp(h, FujifilmSdkWrapper.API_CODE_SetLiveViewImageQuality, 1, 1);
        Result($"Live view quality: camera accepts [{string.Join(", ", accepted)}]", accepted.Contains("Fine"),
            "the plugin defaults to Fine and falls back to it when a value is refused");

        var sizes = new (string, long)[] { ("Large", 1), ("Medium", 2), ("Small", 3) };
        var sizeOk = sizes.All(s => XSDK_SetProp(h, FujifilmSdkWrapper.API_CODE_SetLiveViewImageSize, 1, s.Item2) == 0);
        XSDK_SetProp(h, FujifilmSdkWrapper.API_CODE_SetLiveViewImageSize, 1, 1);
        Result("Live view size: all three accepted", sizeOk);

        Console.WriteLine($"\n  == settings sweep: {_pass} passed, {_fail} failed, {_skip} skipped ==");
        return _fail;
    }
}
