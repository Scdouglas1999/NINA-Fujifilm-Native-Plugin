using System.Globalization;
using System.Text.Json;
using NINA.Plugins.Fujifilm.Devices;

namespace NINA.Plugins.Fujifilm.Tests;

public class FujifilmCameraActionsTests
{
    [Fact]
    public void SupportedActions_AdvertisesCanonicalNameOnly()
    {
        Assert.Equal(new[] { "Camera:SetAperture" }, FujifilmCameraActions.SupportedActions);
    }

    [Theory]
    [InlineData("Camera:SetAperture")]
    [InlineData("camera:setaperture")]
    [InlineData("CAMERA:SETAPERTURE")]
    public void Execute_MatchesActionNameCaseInsensitively(string actionName)
    {
        var response = FujifilmCameraActions.Execute(
            actionName,
            "{\"fNumber\":2.8}",
            true,
            requested => new ApertureSetResult(true, string.Empty, requested));

        using var json = JsonDocument.Parse(response);
        Assert.Equal(2.8, json.RootElement.GetProperty("requestedFNumber").GetDouble(), 3);
        Assert.Equal(2.8, json.RootElement.GetProperty("appliedFNumber").GetDouble(), 3);
    }

    [Fact]
    public void Execute_UsesInvariantJsonNumbers()
    {
        using var culture = new TemporaryCulture("fr-FR");

        var response = FujifilmCameraActions.Execute(
            FujifilmCameraActions.SetAperture,
            "{\"fNumber\":1.2}",
            true,
            requested => new ApertureSetResult(true, string.Empty, requested));

        Assert.Contains("1.2", response, StringComparison.Ordinal);
        Assert.DoesNotContain("1,2", response, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"fNumber\":null}")]
    [InlineData("{\"fNumber\":\"2.8\"}")]
    [InlineData("{\"fNumber\":0}")]
    [InlineData("{\"fNumber\":-2.8}")]
    [InlineData("{\"fNumber\":2.8,\"FNUMBER\":4}")]
    public void Execute_RejectsInvalidParameters(string? parameters)
    {
        Assert.Throws<ArgumentException>(() => FujifilmCameraActions.Execute(
            FujifilmCameraActions.SetAperture,
            parameters!,
            true,
            requested => new ApertureSetResult(true, string.Empty, requested)));
    }

    [Fact]
    public void Execute_IgnoresUnknownPropertiesForForwardCompatibility()
    {
        var response = FujifilmCameraActions.Execute(
            FujifilmCameraActions.SetAperture,
            "{\"fNumber\":4,\"requestId\":\"test\"}",
            true,
            requested => new ApertureSetResult(true, string.Empty, requested));

        using var json = JsonDocument.Parse(response);
        Assert.Equal(4, json.RootElement.GetProperty("appliedFNumber").GetDouble());
    }

    [Fact]
    public void Execute_RejectsUnknownActionBeforeConnectionCheck()
    {
        Assert.Throws<NotSupportedException>(() => FujifilmCameraActions.Execute(
            "Camera:Unknown",
            "{}",
            false,
            requested => new ApertureSetResult(true, string.Empty, requested)));
    }

    [Fact]
    public void Execute_RejectsDisconnectedCameraWithoutInvokingSetter()
    {
        var invoked = false;

        var error = Assert.Throws<InvalidOperationException>(() => FujifilmCameraActions.Execute(
            FujifilmCameraActions.SetAperture,
            "{\"fNumber\":2.8}",
            false,
            requested =>
            {
                invoked = true;
                return new ApertureSetResult(true, string.Empty, requested);
            }));

        Assert.False(invoked);
        Assert.Contains("not connected", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Execute_PropagatesApertureFailureAsException()
    {
        var error = Assert.Throws<InvalidOperationException>(() => FujifilmCameraActions.Execute(
            FujifilmCameraActions.SetAperture,
            "{\"fNumber\":16}",
            true,
            requested => new ApertureSetResult(false, "Lens rejected the aperture.", 0)));

        Assert.Equal("Lens rejected the aperture.", error.Message);
    }

    [Fact]
    public void Execute_ReturnsVerifiedValueRatherThanRequestedValue()
    {
        var response = FujifilmCameraActions.Execute(
            FujifilmCameraActions.SetAperture,
            "{\"fNumber\":2.8}",
            true,
            requested => new ApertureSetResult(true, string.Empty, 3.2));

        using var json = JsonDocument.Parse(response);
        Assert.Equal(2.8, json.RootElement.GetProperty("requestedFNumber").GetDouble(), 3);
        Assert.Equal(3.2, json.RootElement.GetProperty("appliedFNumber").GetDouble(), 3);
    }

    private sealed class TemporaryCulture : IDisposable
    {
        private readonly CultureInfo originalCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        public TemporaryCulture(string name)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
