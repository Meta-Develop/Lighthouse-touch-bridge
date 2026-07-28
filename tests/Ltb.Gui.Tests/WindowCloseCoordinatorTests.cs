namespace Ltb.Gui.Tests;

public sealed class WindowCloseCoordinatorTests
{
    [Fact]
    public async Task CleanupFailureStillSavesAndDispatchesOneFinalClose()
    {
        var calls = new List<string>();
        var coordinator = new WindowCloseCoordinator(
            () =>
            {
                calls.Add("cleanup");
                throw new InvalidOperationException("injected cleanup failure");
            },
            () =>
            {
                calls.Add("save");
                return Task.CompletedTask;
            },
            () => calls.Add("close"));

        await coordinator.RequestCloseAsync();

        Assert.Equal(["cleanup", "save", "close"], calls);
        Assert.True(coordinator.AllowFinalClose);
    }

    [Fact]
    public async Task PlacementFailureStillDispatchesOneFinalClose()
    {
        var cleanupCalls = 0;
        var saveCalls = 0;
        var closeCalls = 0;
        var coordinator = new WindowCloseCoordinator(
            () =>
            {
                cleanupCalls++;
                return Task.CompletedTask;
            },
            () =>
            {
                saveCalls++;
                throw new IOException("injected placement failure");
            },
            () => closeCalls++);

        await coordinator.RequestCloseAsync();

        Assert.Equal(1, cleanupCalls);
        Assert.Equal(1, saveCalls);
        Assert.Equal(1, closeCalls);
        Assert.True(coordinator.AllowFinalClose);
    }

    [Fact]
    public async Task RepeatedRequestsShareCleanupAndOneFinalCloseDispatch()
    {
        var cleanupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupCalls = 0;
        var saveCalls = 0;
        var closeCalls = 0;
        var coordinator = new WindowCloseCoordinator(
            async () =>
            {
                cleanupCalls++;
                cleanupEntered.TrySetResult();
                await releaseCleanup.Task;
            },
            () =>
            {
                saveCalls++;
                return Task.CompletedTask;
            },
            () => closeCalls++);

        var first = coordinator.RequestCloseAsync();
        await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.RequestCloseAsync();
        var third = coordinator.RequestCloseAsync();

        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.False(coordinator.AllowFinalClose);
        Assert.Equal(1, cleanupCalls);

        releaseCleanup.TrySetResult();
        await Task.WhenAll(first, second, third);

        Assert.Equal(1, cleanupCalls);
        Assert.Equal(1, saveCalls);
        Assert.Equal(1, closeCalls);
        Assert.True(coordinator.AllowFinalClose);
    }

    [Fact]
    public async Task FinalCloseReentryDoesNotStartAnotherOperation()
    {
        var cleanupCalls = 0;
        var saveCalls = 0;
        var closeCalls = 0;
        WindowCloseCoordinator? coordinator = null;
        coordinator = new WindowCloseCoordinator(
            () =>
            {
                cleanupCalls++;
                return Task.CompletedTask;
            },
            () =>
            {
                saveCalls++;
                return Task.CompletedTask;
            },
            () =>
            {
                closeCalls++;
                Assert.True(coordinator!.AllowFinalClose);
            });

        var first = coordinator.RequestCloseAsync();
        await first;
        var afterFinalClose = coordinator.RequestCloseAsync();

        Assert.Same(first, afterFinalClose);
        Assert.Equal(1, cleanupCalls);
        Assert.Equal(1, saveCalls);
        Assert.Equal(1, closeCalls);
    }
}
