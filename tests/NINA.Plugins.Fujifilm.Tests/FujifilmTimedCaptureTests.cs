using System.Collections.Concurrent;
using NINA.Plugins.Fujifilm.Devices;
using NINA.Plugins.Fujifilm.Interop;

namespace NINA.Plugins.Fujifilm.Tests;

public sealed class FujifilmTimedCaptureTests
{
    private static Task NoSettle(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    [Fact]
    public async Task SuccessfulCaptureReturnsImageWithoutStopping()
    {
        var commands = new List<int>();
        var image = new object();
        var result = await FujifilmTimedRelease.CaptureAsync((mode, _) => commands.Add(mode),
            () => Task.FromResult(image),
            () => throw new Xunit.Sdk.XunitException("Unexpected stop"), default, _ => { }, NoSettle);
        Assert.Same(image, result);
        Assert.Equal(new[] { 0x0200, 0x0304 }, commands);
    }

    [Fact]
    public async Task AlreadyCancelledNeverTriggersCapturesOrStops()
    {
        int stops = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FujifilmTimedRelease.CaptureAsync<int>(
            (_, _) => throw new Xunit.Sdk.XunitException("Unexpected release"),
            () => throw new Xunit.Sdk.XunitException("Unexpected capture"),
            () => { stops++; return Task.CompletedTask; },
            new CancellationToken(true), _ => { }, NoSettle));
        Assert.Equal(0, stops);
    }

    [Fact]
    public async Task AbortDuringPreparationOnlyReleasesHalfPress()
    {
        using var cancellation = new CancellationTokenSource();
        var commands = new List<int>();
        int stops = 0;
        var capture = FujifilmTimedRelease.CaptureAsync<int>((mode, _) => commands.Add(mode),
            () => throw new Xunit.Sdk.XunitException("Unexpected capture"),
            () => { stops++; return Task.CompletedTask; },
            cancellation.Token, _ => { }, (_, token) => Task.Delay(Timeout.Infinite, token));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        Assert.Equal(new[] { 0x0200, 0x0004 }, commands);
        Assert.Equal(0, stops);
    }

    [Fact]
    public async Task AbortAfterFullPressStopsWithoutWaitingForExposureDuration()
    {
        using var cancellation = new CancellationTokenSource();
        var commands = new List<int>();
        var capture = FujifilmTimedRelease.CaptureAsync((mode, _) => commands.Add(mode), async () =>
        {
            await Task.Delay(Timeout.Infinite, cancellation.Token);
            return 1;
        }, () => { commands.Add(0x000F); return Task.CompletedTask; },
            cancellation.Token, _ => { }, NoSettle);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { 0x0200, 0x0304, 0x000F }, commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbortDuringFullPressWaitsForNativeReturnAndDoesNotStopAfterFailure(bool failRelease)
    {
        using var cancellation = new CancellationTokenSource();
        using var finishRelease = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new ConcurrentQueue<int>();
        var failure = new FujifilmSdkException("release", -1, 0x1112, 0x1008);
        int nativeCalls = 0, overlap = 0;
        var capture = Task.Run(() => FujifilmTimedRelease.CaptureAsync<int>((mode, _) =>
        {
            if (Interlocked.Increment(ref nativeCalls) != 1) Interlocked.Exchange(ref overlap, 1);
            try
            {
                commands.Enqueue(mode);
                if (mode != 0x0304) return;
                entered.SetResult();
                if (!finishRelease.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                if (failRelease) throw failure;
            }
            finally { Interlocked.Decrement(ref nativeCalls); }
        }, () => throw new Xunit.Sdk.XunitException("Cancelled before download"), () =>
        {
            if (Interlocked.Increment(ref nativeCalls) != 1) Interlocked.Exchange(ref overlap, 1);
            commands.Enqueue(0x000F);
            Interlocked.Decrement(ref nativeCalls);
            return Task.CompletedTask;
        }, cancellation.Token, _ => { }, NoSettle));

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(capture.IsCompleted);
            Assert.Equal(new[] { 0x0200, 0x0304 }, commands);
        }
        finally { finishRelease.Set(); }

        if (failRelease)
        {
            var actual = await Assert.ThrowsAsync<FujifilmSdkException>(() => capture.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(failure, actual);
            Assert.Equal(new[] { 0x0200, 0x0304 }, commands);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(new[] { 0x0200, 0x0304, 0x000F }, commands);
        }
        Assert.Equal(0, overlap);
    }

    [Fact]
    public async Task AbortDuringDownloadWaitsForWorkerAndDoesNotReturnCancelledImage()
    {
        using var cancellation = new CancellationTokenSource();
        var finishDownload = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new List<int>();
        var capture = FujifilmTimedRelease.CaptureAsync((mode, _) => commands.Add(mode),
            () => finishDownload.Task, () => { commands.Add(0x000F); return Task.CompletedTask; },
            cancellation.Token, _ => { }, NoSettle);
        cancellation.Cancel();
        Assert.False(capture.IsCompleted);
        Assert.Equal(new[] { 0x0200, 0x0304 }, commands);
        finishDownload.SetResult(123);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        Assert.Equal(new[] { 0x0200, 0x0304, 0x000F }, commands);
    }

    [Fact]
    public async Task FailedStopPreservesCancellationAndDoesNotRetry()
    {
        using var cancellation = new CancellationTokenSource();
        int stops = 0;
        var messages = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FujifilmTimedRelease.CaptureAsync(
            (_, _) => { }, () => { cancellation.Cancel(); return Task.FromResult(1); }, () =>
            {
                stops++;
                throw new FujifilmSdkException("cancel", -1, 0x1112, 0x1008);
            }, cancellation.Token, messages.Add, NoSettle));
        Assert.Equal(1, stops);
        Assert.Contains(messages, message => message.StartsWith("StopExposure failed during cancel:"));
    }

    [Fact]
    public async Task DownloadSdkFailureWithCancellationDoesNotIssueStop()
    {
        using var cancellation = new CancellationTokenSource();
        int stops = 0;
        var failure = new FujifilmSdkException("read", -1, 0x1203, 0x2001);
        var actual = await Assert.ThrowsAsync<FujifilmSdkException>(() => FujifilmTimedRelease.CaptureAsync<int>(
            (_, _) => { }, () => { cancellation.Cancel(); throw failure; },
            () => { stops++; return Task.CompletedTask; },
            cancellation.Token, _ => { }, NoSettle));
        Assert.Same(failure, actual);
        Assert.Equal(0, stops);
    }

    [Fact]
    public async Task CaptureDoesNotFinishUntilCancellationCleanupFinishes()
    {
        using var cancellation = new CancellationTokenSource();
        var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int stops = 0;
        var capture = FujifilmTimedRelease.CaptureAsync((_, _) => { }, async () =>
        {
            await Task.Delay(Timeout.Infinite, cancellation.Token);
            return 1;
        }, () =>
        {
            stops++;
            stopStarted.SetResult();
            return stopFinished.Task;
        }, cancellation.Token, _ => { }, NoSettle);
        cancellation.Cancel();
        try
        {
            await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(capture.IsCompleted);
            Assert.Equal(1, stops);
        }
        finally { stopFinished.SetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        Assert.Equal(1, stops);
    }
}
