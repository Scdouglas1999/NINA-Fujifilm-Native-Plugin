using NINA.Plugins.Fujifilm.Devices;
using NINA.Plugins.Fujifilm.Interop;

namespace NINA.Plugins.Fujifilm.Tests;

public sealed class FujifilmTimedReleaseTests
{
    [Fact]
    public async Task HalfPressThen500MillisecondSettleThenFullPress()
    {
        var commands = new List<int>();
        using var cancellation = new CancellationTokenSource();
        bool settled = false;
        await FujifilmTimedRelease.TriggerAsync((mode, _) =>
        {
            if (mode == 0x0304) Assert.True(settled);
            commands.Add(mode);
        }, cancellation.Token, _ => { }, (duration, token) =>
        {
            Assert.Equal(new[] { 0x0200 }, commands);
            Assert.Equal(TimeSpan.FromMilliseconds(500), duration);
            Assert.Equal(cancellation.Token, token);
            settled = true;
            return Task.CompletedTask;
        });
        Assert.Equal(new[] { 0x0200, 0x0304 }, commands);
    }

    [Fact]
    public async Task FailedHalfPressNeverWaitsOrAttemptsAnExposure()
    {
        int calls = 0;
        var failure = new FujifilmSdkException("release", -1, 0x1112, 0x1006);
        var actual = await Assert.ThrowsAsync<FujifilmSdkException>(() =>
            FujifilmTimedRelease.TriggerAsync((mode, _) =>
            {
                calls++;
                Assert.Equal(0x0200, mode);
                throw failure;
            }, default, _ => { }, (_, _) => throw new Xunit.Sdk.XunitException("Unexpected wait")));
        Assert.Same(failure, actual);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FailedFullPressIsNotRetriedOrFollowedByNativeCalls()
    {
        var commands = new List<int>();
        var failure = new FujifilmSdkException("release", -1, 0x1112, 0x1008);
        var actual = await Assert.ThrowsAsync<FujifilmSdkException>(() =>
            FujifilmTimedRelease.TriggerAsync((mode, _) =>
            {
                commands.Add(mode);
                if (mode == 0x0304) throw failure;
            }, default, _ => { }, (_, _) => Task.CompletedTask));
        Assert.Same(failure, actual);
        Assert.Equal(new[] { 0x0200, 0x0304 }, commands);
    }

    [Fact]
    public async Task CancellationAfterHalfPressReleasesWithoutTakingPicture()
    {
        using var cancellation = new CancellationTokenSource();
        var commands = new List<int>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FujifilmTimedRelease.TriggerAsync((mode, _) =>
        {
            commands.Add(mode);
            cancellation.Cancel();
        }, cancellation.Token, _ => { }, (_, _) => Task.CompletedTask));
        Assert.Equal(new[] { 0x0200, 0x0004 }, commands);
    }

    [Fact]
    public async Task AlreadyCancelledMakesNoNativeCalls()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FujifilmTimedRelease.TriggerAsync(
            (_, _) => throw new Xunit.Sdk.XunitException("Unexpected native call"),
            new CancellationToken(true), _ => { }));
    }

    [Fact]
    public async Task FullPressWaitsForHalfPressSettleToComplete()
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new List<int>();
        var capture = FujifilmTimedRelease.TriggerAsync((mode, _) => commands.Add(mode),
            default, _ => { }, (_, _) => settled.Task);
        try
        {
            Assert.False(capture.IsCompleted);
            Assert.Equal(new[] { 0x0200 }, commands);
        }
        finally { settled.SetResult(); }
        await capture;
        Assert.Equal(new[] { 0x0200, 0x0304 }, commands);
    }

    [Fact]
    public async Task CancellationDuringSettleReleasesHalfPressWithoutFullPress()
    {
        using var cancellation = new CancellationTokenSource();
        var commands = new List<int>();
        var capture = FujifilmTimedRelease.TriggerAsync((mode, _) => commands.Add(mode),
            cancellation.Token, _ => { }, (_, token) => Task.Delay(Timeout.Infinite, token));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        Assert.Equal(new[] { 0x0200, 0x0004 }, commands);
    }

    [Fact]
    public async Task FailedCancellationCleanupPreservesCancellationAndNeverFullPresses()
    {
        using var cancellation = new CancellationTokenSource();
        var commands = new List<int>();
        var messages = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FujifilmTimedRelease.TriggerAsync((mode, _) =>
        {
            commands.Add(mode);
            if (mode == 0x0200) cancellation.Cancel();
            else throw new FujifilmSdkException("release", -1, 0x1112, 0x1006);
        }, cancellation.Token, messages.Add, (_, _) => Task.CompletedTask));
        Assert.Equal(new[] { 0x0200, 0x0004 }, commands);
        Assert.Contains(messages, message => message.StartsWith("Could not release cancelled half-press:"));
    }
}
