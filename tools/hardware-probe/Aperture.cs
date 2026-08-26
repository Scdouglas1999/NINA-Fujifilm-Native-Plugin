using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using static Probe.Sdk;

namespace Probe;

internal static class Aperture
{
    private static string F(long raw) => $"f/{raw / 100.0:0.0#}";

    internal static int Run(IntPtr camera, IReadOnlySet<long> advertisedCodes)
    {
        const long capCode = 0x1324;
        const long setCode = 0x1325;
        const long getCode = 0x1326;

        Console.WriteLine("\n== Aperture capability and write/restore sweep ==");
        if (advertisedCodes.Count > 0 &&
            (!advertisedCodes.Contains(capCode) || !advertisedCodes.Contains(setCode) || !advertisedCodes.Contains(getCode)))
        {
            Console.WriteLine("  NOTE: camera does not advertise the complete Cap/Set/Get aperture API; probing the direct standard calls.");
        }

        var zoomPosition = 0L;
        var zoomResult = XSDK_GetLensZoomPos(camera, out zoomPosition);
        if (zoomResult != 0)
        {
            // Prime lenses commonly use position zero; CapAperture accepts it even when a body does
            // not expose GetLensZoomPos for the attached lens.
            zoomPosition = 0;
        }

        long count = 0;
        var result = XSDK_CapAperture(camera, zoomPosition, ref count, IntPtr.Zero);
        if (result != 0 || count <= 0)
        {
            Console.WriteLine($"  FAIL: CapAperture count returned result={result}, count={count}, zoom={zoomPosition}");
            return 1;
        }

        // C long is 32-bit on Windows and 64-bit on the Linux SDK build.
        var nativeLongSize = OperatingSystem.IsWindows() ? sizeof(int) : sizeof(long);
        var buffer = Marshal.AllocHGlobal(checked((int)count * nativeLongSize));
        var values = new List<long>();
        try
        {
            result = XSDK_CapAperture(camera, zoomPosition, ref count, buffer);
            if (result != 0)
            {
                Console.WriteLine($"  FAIL: CapAperture values returned result={result}");
                return 1;
            }

            for (var i = 0; i < count; i++)
            {
                var offset = checked((int)i * nativeLongSize);
                var value = nativeLongSize == sizeof(int)
                    ? Marshal.ReadInt32(buffer, offset)
                    : Marshal.ReadInt64(buffer, offset);
                if (value > 0 && value != 0xFFFF)
                {
                    values.Add(value);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        values = values.Distinct().OrderBy(value => value).ToList();
        Console.WriteLine($"  zoom position={zoomPosition}; advertised values ({values.Count}): [{string.Join(", ", values.Select(F))}]");
        if (values.Count == 0 || XSDK_GetAperture(camera, out var original) != 0)
        {
            Console.WriteLine("  FAIL: no manual values or current aperture could not be read.");
            return 1;
        }

        var failures = 0;
        Console.WriteLine($"  original={F(original)} ({original})");
        try
        {
            foreach (var value in values)
            {
                var setResult = XSDK_SetAperture(camera, value);
                long errorApi = 0, errorCode = 0;
                if (setResult != 0)
                {
                    XSDK_GetErrorNumber(camera, out errorApi, out errorCode);
                }
                var getResult = XSDK_GetAperture(camera, out var actual);
                var passed = setResult == 0 && getResult == 0 && actual == value;
                Console.WriteLine($"  {(passed ? "PASS" : "FAIL")}: set {F(value)} ({value}), set={setResult}, errorApi=0x{errorApi:X}, error=0x{errorCode:X}, get={getResult}, read={F(actual)} ({actual})");
                if (!passed) failures++;
            }
        }
        finally
        {
            var restoreResult = XSDK_SetAperture(camera, original);
            var readResult = XSDK_GetAperture(camera, out var restored);
            var restoredOk = restoreResult == 0 && readResult == 0 && restored == original;
            Console.WriteLine($"  {(restoredOk ? "PASS" : "FAIL")}: restore {F(original)}, read={F(restored)} ({restored})");
            if (!restoredOk) failures++;
        }

        return failures;
    }
}
