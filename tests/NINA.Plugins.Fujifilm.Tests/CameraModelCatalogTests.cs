using System.Text.Json;
using NINA.Plugins.Fujifilm.Configuration;

namespace NINA.Plugins.Fujifilm.Tests;

public sealed class CameraModelCatalogTests
{
    [Fact]
    public void ProductLookup_HandlesVendorPrefixAndChoosesLongestSuffix()
    {
        var configs = new[]
        {
            CreateConfig("X-H2", 7728, 5152),
            CreateConfig("X-H2S", 6240, 4160),
            CreateConfig("X-T2", 6000, 4000),
            CreateConfig("GFX100", 11648, 8736),
            CreateConfig("GFX100RF", 11648, 8736)
        };

        Assert.Equal("X-T2", CameraModelRules.FindBestMatch(configs, "FUJIFILM X-T2")?.ModelName);
        Assert.Equal("X-H2S", CameraModelRules.FindBestMatch(configs, "FUJIFILM X-H2S")?.ModelName);
        Assert.Equal("GFX100RF", CameraModelRules.FindBestMatch(configs, "FUJIFILM GFX100RF")?.ModelName);
    }

    [Fact]
    public void Validation_RejectsInvalidConfiguration()
    {
        Assert.False(CameraModelRules.IsValid(CreateConfig("Invalid", 0, 0)));
        Assert.True(CameraModelRules.IsValid(CreateConfig("X-T2", 6000, 4000)));
    }

    [Theory]
    [InlineData("FUJIFILM GFX ETERNA 55")]
    [InlineData("GFX ETERNA55")]
    public void KnownUnsupportedStillCameraFilter_MatchesEternaCinemaCamera(string productName)
    {
        Assert.True(CameraModelRules.IsKnownUnsupportedStillCamera(productName));
    }

    [Theory]
    [InlineData("FUJIFILM GFX100RF")]
    [InlineData("FUJIFILM X-T5")]
    public void KnownUnsupportedStillCameraFilter_AllowsStillCameras(string productName)
    {
        Assert.False(CameraModelRules.IsKnownUnsupportedStillCamera(productName));
    }

    [Fact]
    public void ShippedConfigurations_AllPassValidation()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "CameraConfigs");
        var configs = Directory.GetFiles(path, "*.json")
            .Select(file => JsonSerializer.Deserialize<CameraConfig>(File.ReadAllText(file)))
            .Where(config => config != null)
            .Cast<CameraConfig>()
            .ToArray();

        Assert.Equal(18, configs.Length);
        Assert.All(configs, config => Assert.True(CameraModelRules.IsValid(config), config.ModelName));
        Assert.Equal(3600, configs.Single(config => config.ModelName == "X-T2").DefaultMaxExposure);
        Assert.Equal(11648, configs.Single(config => config.ModelName == "GFX100RF").CameraXSize);
    }

    private static CameraConfig CreateConfig(string model, int width, int height)
    {
        return new CameraConfig
        {
            ModelName = model,
            CameraXSize = width,
            CameraYSize = height,
            PixelSizeX = 3.76,
            PixelSizeY = 3.76,
            DefaultMinExposure = 0.001,
            DefaultMaxExposure = 60,
            DefaultMinSensitivity = 100,
            DefaultMaxSensitivity = 12800
        };
    }
}
