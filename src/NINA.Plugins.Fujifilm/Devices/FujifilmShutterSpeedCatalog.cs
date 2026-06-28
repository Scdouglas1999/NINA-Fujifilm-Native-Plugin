using System;
using System.Collections.Generic;
using System.Linq;
using NINA.Plugins.Fujifilm.Configuration;

namespace NINA.Plugins.Fujifilm.Devices;

internal static class FujifilmShutterSpeedCatalog
{
    public const int BulbCode = -1;
    private static readonly IReadOnlyDictionary<int, double> Universal = CreateUniversalMap();

    public static IReadOnlyDictionary<int, double> Build(
        IReadOnlyList<int> supportedCodes,
        IReadOnlyList<ShutterSpeedMapping>? modelMappings,
        bool bulbCapable,
        double bulbMaxSeconds,
        Action<int>? unknownCode = null)
    {
        var modelMap = (modelMappings ?? Array.Empty<ShutterSpeedMapping>())
            .Where(mapping => mapping.Duration > 0)
            .GroupBy(mapping => mapping.SdkCode)
            .ToDictionary(group => group.Key, group => group.Last().Duration);
        var result = new Dictionary<int, double>();

        foreach (var code in supportedCodes.Distinct())
        {
            if (code == BulbCode)
            {
                continue;
            }

            if (modelMap.TryGetValue(code, out var modelDuration))
            {
                result[code] = modelDuration;
            }
            else if (Universal.TryGetValue(code, out var universalDuration))
            {
                result[code] = universalDuration;
            }
            else
            {
                unknownCode?.Invoke(code);
            }
        }

        if (bulbCapable)
        {
            result[BulbCode] = bulbMaxSeconds > 0 ? bulbMaxSeconds : 3600.0;
        }

        return result;
    }

    public static int SelectCode(IReadOnlyDictionary<int, double> map, double requestedSeconds, bool bulbCapable)
    {
        if (requestedSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedSeconds));
        }

        var timed = map
            .Where(pair => pair.Key != BulbCode && pair.Value > 0)
            .ToArray();

        if (timed.Length == 0)
        {
            if (bulbCapable)
            {
                return BulbCode;
            }

            throw new InvalidOperationException("The camera did not report any usable shutter speeds or bulb support.");
        }

        var maxTimed = timed.Max(pair => pair.Value);
        if (requestedSeconds > maxTimed && bulbCapable)
        {
            return BulbCode;
        }

        return timed
            .OrderBy(pair => Math.Abs(pair.Value - requestedSeconds))
            .ThenBy(pair => pair.Value > requestedSeconds ? 1 : 0)
            .First()
            .Key;
    }

    public static double GetTimedMaximum(IReadOnlyDictionary<int, double> map, double fallback)
    {
        var values = map
            .Where(pair => pair.Key != BulbCode && pair.Value > 0)
            .Select(pair => pair.Value)
            .ToArray();
        return values.Length == 0 ? fallback : values.Max();
    }

    private static IReadOnlyDictionary<int, double> CreateUniversalMap()
    {
        return new Dictionary<int, double>
        {
            [5] = 1.0 / 180000.0, [6] = 1.0 / 160000.0, [7] = 1.0 / 128000.0,
            [9] = 1.0 / 102400.0, [12] = 1.0 / 80000.0, [15] = 1.0 / 64000.0,
            [19] = 1.0 / 51200.0, [24] = 1.0 / 40000.0, [30] = 1.0 / 32000.0,
            [38] = 1.0 / 25600.0, [43] = 1.0 / 24000.0, [48] = 1.0 / 20000.0,
            [61] = 1.0 / 16000.0, [76] = 1.0 / 12800.0, [86] = 1.0 / 12000.0,
            [96] = 1.0 / 10000.0, [122] = 1.0 / 8000.0, [153] = 1.0 / 6400.0,
            [172] = 1.0 / 6000.0, [193] = 1.0 / 5000.0, [244] = 1.0 / 4000.0,
            [307] = 1.0 / 3200.0, [345] = 1.0 / 3000.0, [387] = 1.0 / 2500.0,
            [488] = 1.0 / 2000.0, [615] = 1.0 / 1600.0, [690] = 1.0 / 1500.0,
            [775] = 1.0 / 1250.0, [976] = 1.0 / 1000.0, [1230] = 1.0 / 800.0,
            [1381] = 1.0 / 750.0, [1550] = 1.0 / 640.0, [1953] = 1.0 / 500.0,
            [2460] = 1.0 / 400.0, [2762] = 1.0 / 350.0, [3100] = 1.0 / 320.0,
            [3906] = 1.0 / 250.0, [4921] = 1.0 / 200.0, [5524] = 1.0 / 180.0,
            [6200] = 1.0 / 160.0, [7812] = 1.0 / 125.0, [9843] = 1.0 / 100.0,
            [11048] = 1.0 / 90.0, [12401] = 1.0 / 80.0, [15625] = 1.0 / 60.0,
            [19686] = 1.0 / 50.0, [22097] = 1.0 / 45.0, [24803] = 1.0 / 40.0,
            [31250] = 1.0 / 30.0, [39372] = 1.0 / 25.0, [49606] = 1.0 / 20.0,
            [62500] = 1.0 / 15.0, [78745] = 1.0 / 13.0, [99212] = 1.0 / 10.0,
            [125000] = 1.0 / 8.0, [157490] = 1.0 / 6.0, [198425] = 1.0 / 5.0,
            [250000] = 1.0 / 4.0, [314980] = 1.0 / 3.0, [396850] = 1.0 / 2.5,
            [500000] = 1.0 / 2.0, [629960] = 1.0 / 1.6, [707106] = 1.0 / 1.5,
            [793700] = 1.0 / 1.3, [1000000] = 1.0, [1259921] = 1.3,
            [1414213] = 1.5, [1587401] = 1.6, [2000000] = 2.0,
            [2519842] = 2.5, [3174802] = 3.0, [4000000] = 4.0,
            [5039684] = 5.0, [6349604] = 6.0, [8000000] = 8.0,
            [10079368] = 10.0, [12699208] = 13.0, [16000000] = 15.0,
            [20158736] = 20.0, [25398416] = 25.0, [32000000] = 30.0,
            [64000000] = 60.0
        };
    }
}
