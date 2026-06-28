using System.Collections.Generic;
using System.Globalization;

namespace NINA.Plugins.Fujifilm.Imaging;

internal enum FujiMetadataValueKind
{
    String,
    Integer,
    Double,
    Boolean
}

internal readonly record struct FujiMetadataValue(FujiMetadataValueKind Kind, object Value);

internal static class FujiMetadataValueParser
{
    private static readonly HashSet<string> DoubleKeys = new() { "EXPTIME", "XPIXSZ", "YPIXSZ", "EGAIN" };
    private static readonly HashSet<string> IntegerKeys = new()
    {
        "ISO", "XBINNING", "YBINNING", "XBAYROFF", "YBAYROFF", "BLACKLVL",
        "WHITELVL", "FUJIISO", "FUJISHUT", "FUJIDR"
    };

    public static FujiMetadataValue Parse(string key, string value)
    {
        if (DoubleKeys.Contains(key) &&
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            return new FujiMetadataValue(FujiMetadataValueKind.Double, doubleValue);
        }

        if (IntegerKeys.Contains(key) &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integerValue))
        {
            return new FujiMetadataValue(FujiMetadataValueKind.Integer, integerValue);
        }

        if (key == "CFA")
        {
            if (bool.TryParse(value, out var booleanValue))
            {
                return new FujiMetadataValue(FujiMetadataValueKind.Boolean, booleanValue);
            }
            if (value == "1" || value == "0")
            {
                return new FujiMetadataValue(FujiMetadataValueKind.Boolean, value == "1");
            }
        }

        return new FujiMetadataValue(FujiMetadataValueKind.String, value);
    }
}
