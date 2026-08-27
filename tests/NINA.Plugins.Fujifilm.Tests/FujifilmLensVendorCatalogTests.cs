using NINA.Plugins.Fujifilm.Devices;

namespace NINA.Plugins.Fujifilm.Tests;

public sealed class FujifilmLensVendorCatalogTests
{
    [Fact]
    public void RecognizesObservedViltroxIdentifier()
    {
        Assert.Equal("Viltrox", FujifilmLensVendorCatalog.Resolve("LX202A", "AF 27/1.2 XF"));
    }

    [Theory]
    [InlineData("XF27mmF2.8 R WR")]
    [InlineData("XC15-45mmF3.5-5.6 OIS PZ")]
    [InlineData("GF45mmF2.8 R WR")]
    public void RecognizesNativeLensFamilies(string productName)
    {
        Assert.Equal("Fujifilm", FujifilmLensVendorCatalog.Resolve(string.Empty, productName));
    }

    [Fact]
    public void DoesNotGuessUnknownThirdPartyLens()
    {
        Assert.Empty(FujifilmLensVendorCatalog.Resolve("UNKNOWN", "AF 35/1.4 XF"));
    }

    [Fact]
    public void FormatsOneConsistentVendorQualifiedDisplayName()
    {
        var metadata = new FujiCameraMetadata
        {
            LensVendor = "Viltrox",
            LensProductName = "AF 27/1.2 XF",
            HasImageStabilization = true
        };

        Assert.Equal("Viltrox AF 27/1.2 XF [OIS]", FujifilmLensVendorCatalog.FormatDisplayName(metadata));
    }
}
