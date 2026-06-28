using System.Collections.Generic;
using System.Linq;
using NINA.Image.ImageData;

namespace NINA.Plugins.Fujifilm.Imaging;

internal static class FujiMetadataHeaderWriter
{
    public static void Apply(ImageMetaData metadata, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var header in headers)
        {
            var existing = metadata.GenericHeaders.FirstOrDefault(item => item.Key == header.Key);
            if (existing != null)
            {
                metadata.GenericHeaders.Remove(existing);
            }

            var value = FujiMetadataValueParser.Parse(header.Key, header.Value);
            if (value.Kind == FujiMetadataValueKind.Double)
            {
                metadata.GenericHeaders.Add(new DoubleMetaDataHeader(header.Key, (double)value.Value));
            }
            else if (value.Kind == FujiMetadataValueKind.Integer)
            {
                metadata.GenericHeaders.Add(new IntMetaDataHeader(header.Key, (int)value.Value));
            }
            else if (value.Kind == FujiMetadataValueKind.Boolean)
            {
                metadata.GenericHeaders.Add(new BoolMetaDataHeader(header.Key, (bool)value.Value));
            }
            else
            {
                metadata.GenericHeaders.Add(new StringMetaDataHeader(header.Key, (string)value.Value));
            }
        }
    }
}
