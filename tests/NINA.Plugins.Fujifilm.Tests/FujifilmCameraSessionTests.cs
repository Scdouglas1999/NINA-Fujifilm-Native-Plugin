using NINA.Plugins.Fujifilm.Interop.Native;

namespace NINA.Plugins.Fujifilm.Tests;

public sealed class FujifilmCameraSessionTests
{
    [Fact]
    public async Task Dispose_DoesNotInvalidateSharedNativeHandle()
    {
        var session = new FujifilmCameraSession(new IntPtr(42), "ENUM:0");

        await session.DisposeAsync();

        Assert.Equal(new IntPtr(42), session.Handle);
    }
}
