using NINA.Plugins.Fujifilm.Devices;
using NINA.Plugins.Fujifilm.Diagnostics;
using NINA.Plugins.Fujifilm.Interop;
using NINA.Plugins.Fujifilm.Interop.Native;
using Xunit;

namespace NINA.Plugins.Fujifilm.Windows.Tests;

// Exercise the actual camera and factory through a fake session. Cancel at the first
// connection diagnostic, before the settle delay, so no proprietary SDK calls are made.
public sealed class EquipmentRefreshTests : IDisposable
{
    private readonly string _originalProductName = FujiCameraMetadata.Empty.ProductName;

    public void Dispose() => FujiCameraMetadata.Empty.ProductName = _originalProductName;

    [Theory]
    [InlineData("")]
    [InlineData("X-T2")]
    public async Task RefreshDuringConnection_PreservesDiscoveredIdentity(string metadataProductName)
    {
        var interop = new FakeInterop();
        var diagnostics = new TestDiagnostics();
        var camera = new FujiCamera(interop, null!, null!, diagnostics, new FujiEquipmentRegistry());
        var factory = CreateFactory(camera, interop, diagnostics);
        camera.GetCapabilitiesSnapshot().Metadata.ProductName = metadataProductName;

        // Reconnecting another body must replace the saved identity even if metadata is stale.
        foreach (var model in new[] { "X-T4", "X-T5" })
        {
            using var cancellation = new CancellationTokenSource();
            var descriptor = new FujifilmCameraDescriptor(model, "ENUM:0");
            IReadOnlyList<FujifilmCameraDescriptor>? refreshed = null;
            diagnostics.OnOpened = () =>
            {
                refreshed = factory.GetAvailableCamerasAsync(CancellationToken.None).GetAwaiter().GetResult();
                cancellation.Cancel();
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => camera.ConnectAsync(descriptor, cancellation.Token));

            Assert.Equal(descriptor, Assert.Single(refreshed!));
            Assert.Equal(0, interop.DetectionCount);
            Assert.False(camera.IsConnected);
            Assert.Null(camera.ConnectedDescriptor);
        }

        Assert.Equal(2, interop.CloseCount);
        // Failed initialization must release the shortcut so a subsequent scan can discover a new body.
        Assert.Equal("X-H2", Assert.Single(await factory.GetAvailableCamerasAsync(CancellationToken.None)).DisplayName);
        Assert.Equal(1, interop.DetectionCount);
    }

    [Fact]
    public async Task Disconnect_ReleasesIdentityAndResumesDiscovery()
    {
        var interop = new FakeInterop();
        var diagnostics = new TestDiagnostics();
        var camera = new FujiCamera(interop, null!, null!, diagnostics, new FujiEquipmentRegistry());
        var factory = CreateFactory(camera, interop, diagnostics);
        using var cancellation = new CancellationTokenSource();
        IReadOnlyList<FujifilmCameraDescriptor>? refreshed = null;
        diagnostics.OnOpened = () =>
        {
            camera.DisconnectAsync().GetAwaiter().GetResult();
            refreshed = factory.GetAvailableCamerasAsync(CancellationToken.None).GetAwaiter().GetResult();
            cancellation.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => camera.ConnectAsync(
            new FujifilmCameraDescriptor("X-T4", "ENUM:0"), cancellation.Token));

        Assert.Equal("X-H2", Assert.Single(refreshed!).DisplayName);
        Assert.Equal(1, interop.DetectionCount);
        Assert.Equal(1, interop.CloseCount);
        Assert.Null(camera.ConnectedDescriptor);
    }

    private static FujiCameraFactory CreateFactory(
        FujiCamera camera, IFujifilmInterop interop, IFujifilmDiagnosticsService diagnostics) =>
        new(interop, diagnostics, camera, null!, null!, null!, null!, null!);

    private sealed class FakeInterop : IFujifilmInterop
    {
        public int DetectionCount { get; private set; }
        public int CloseCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<FujifilmCameraSession> OpenCameraAsync(string deviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new FujifilmCameraSession(new IntPtr(1), deviceId));

        public Task CloseCameraAsync(FujifilmCameraSession session)
        {
            CloseCount++;
            session.Handle = IntPtr.Zero;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<FujifilmCameraInfo>> DetectCamerasAsync(CancellationToken cancellationToken)
        {
            DetectionCount++;
            return Task.FromResult<IReadOnlyList<FujifilmCameraInfo>>(
                new[] { new FujifilmCameraInfo("X-H2", "test-camera", "ENUM:0") });
        }

        public Task<(int Width, int Height)> GetImageInfoAsync(FujifilmCameraSession session) =>
            throw new NotSupportedException();

        public Task<int> GetSensitivityAsync(FujifilmCameraSession session) =>
            throw new NotSupportedException();
    }

    private sealed class TestDiagnostics : IFujifilmDiagnosticsService
    {
        public Action? OnOpened { get; set; }

        public void RecordEvent(string category, string message)
        {
            if (category == "Camera" && message.StartsWith("Opened handle ", StringComparison.Ordinal))
                OnOpened?.Invoke();
        }

        public void RecordSdkCall(string apiName, int result, int apiCode, int errorCode) { }
        public void RecordBayerPatternDetection(string pattern, int width, int height, bool isXTrans, int? isBayerFlag = null) { }
        public void RecordFitsKeywordGeneration(int keywordCount, string? bayerPattern = null) { }
        public void RecordSdkConstantValidation(string constantName, int expectedValue, int actualValue, bool isValid) { }
        public Task<string> ExportDiagnosticsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
