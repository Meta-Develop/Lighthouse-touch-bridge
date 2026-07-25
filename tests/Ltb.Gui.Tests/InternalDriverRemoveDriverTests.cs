using Ltb.App;
using Ltb.Gui;
using Ltb.Gui.ViewModels;

namespace Ltb.Gui.Tests;

/// <summary>
/// Secondary driver-removal action: it reports the transactional removal
/// outcome and is mutually exclusive with the primary Start/Stop session flow
/// without altering that flow.
/// </summary>
public sealed class InternalDriverRemoveDriverTests
{
    [Fact]
    public async Task RemoveDriverRunsRemoverAndPresentsTheRemovalDiagnostic()
    {
        var remover = new GatedRemover();
        await using var viewModel = new InternalDriverViewModel(
            new UnusedSessionFactory(),
            action => action(),
            () => remover);

        Assert.True(viewModel.CanRemoveDriver);
        var removal = viewModel.RemoveDriverAsync();
        await remover.Entered;

        Assert.Equal("Removing the driver_ltb registration...", viewModel.RemoveDriverStatus);
        Assert.False(viewModel.CanRemoveDriver);

        remover.Complete(new InternalDriverRemovalResult(
            Changed: true,
            RestartRequired: true,
            "driver_ltb was removed without changing unrelated drivers; restart SteamVR."));
        await removal;

        Assert.Equal(
            "driver_ltb was removed without changing unrelated drivers; restart SteamVR.",
            viewModel.RemoveDriverStatus);
        Assert.True(viewModel.CanRemoveDriver);
        Assert.Equal(1, remover.RemoveCount);
        Assert.True(remover.Disposed);
    }

    [Fact]
    public async Task RemoveDriverFailurePresentsTheErrorAndReenablesTheAction()
    {
        var remover = new GatedRemover();
        await using var viewModel = new InternalDriverViewModel(
            new UnusedSessionFactory(),
            action => action(),
            () => remover);

        var removal = viewModel.RemoveDriverAsync();
        await remover.Entered;
        remover.Fail(new InvalidOperationException("ownership is lost"));
        await removal;

        Assert.Equal(
            "Driver removal failed: ownership is lost",
            viewModel.RemoveDriverStatus);
        Assert.True(viewModel.CanRemoveDriver);
        Assert.True(remover.Disposed);
    }

    [Fact]
    public async Task RemoveDriverIsBlockedWhileTheSessionRuns()
    {
        var session = new BlockingSession();
        var remover = new GatedRemover();
        await using var viewModel = new InternalDriverViewModel(
            new SingleSessionFactory(session),
            action => action(),
            () => remover);

        var run = viewModel.StartAsync();
        await session.Started;

        Assert.False(viewModel.CanRemoveDriver);
        Assert.False(viewModel.CanCalibrate);
        await viewModel.RemoveDriverAsync();
        await viewModel.CalibrateAsync();
        Assert.Equal(0, remover.RemoveCount);

        await viewModel.StopAsync();
        await run;

        Assert.True(viewModel.CanRemoveDriver);
        var removal = viewModel.RemoveDriverAsync();
        await remover.Entered;
        remover.Complete(new InternalDriverRemovalResult(
            Changed: false,
            RestartRequired: false,
            "driver_ltb is already removed and its owned settings state is restored."));
        await removal;

        Assert.Equal(1, remover.RemoveCount);
    }

    [Fact]
    public async Task StartIsBlockedWhileRemovalIsActive()
    {
        var factory = new SingleSessionFactory(new BlockingSession());
        var remover = new GatedRemover();
        await using var viewModel = new InternalDriverViewModel(
            factory,
            action => action(),
            () => remover);

        var removal = viewModel.RemoveDriverAsync();
        await remover.Entered;

        await viewModel.StartAsync();
        await viewModel.CalibrateAsync();

        Assert.Equal(0, factory.CreateCount);
        Assert.False(viewModel.IsRunning);
        Assert.False(viewModel.CanCalibrate);
        Assert.False(viewModel.CanToggle);

        remover.Complete(new InternalDriverRemovalResult(
            Changed: false,
            RestartRequired: false,
            "nothing to remove"));
        await removal;
    }

    private sealed class UnusedSessionFactory : IInternalDriverSessionFactory
    {
        public IInternalDriverSession Create(InternalDriverSessionIntent intent) =>
            throw new InvalidOperationException(
                "Driver removal must not create an internal-driver session.");
    }

    private sealed class SingleSessionFactory : IInternalDriverSessionFactory
    {
        private readonly IInternalDriverSession _session;

        public SingleSessionFactory(IInternalDriverSession session)
        {
            _session = session;
        }

        public int CreateCount { get; private set; }

        public IInternalDriverSession Create(InternalDriverSessionIntent intent)
        {
            CreateCount++;
            return _session;
        }
    }

    private sealed class BlockingSession : IInternalDriverSession
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _runExit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<InternalDriverSessionSnapshot>? SnapshotChanged
        {
            add
            {
            }
            remove
            {
            }
        }

        public InternalDriverSessionSnapshot CurrentSnapshot { get; } =
            InternalDriverSessionSnapshot.Initial;

        public Task Started => _started.Task;

        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await _runExit.Task.ConfigureAwait(false);
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            _runExit.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class GatedRemover : IInternalDriverRemover
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<InternalDriverRemovalResult> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public int RemoveCount { get; private set; }

        public bool Disposed { get; private set; }

        public ValueTask<InternalDriverRemovalResult> RemoveAsync(
            CancellationToken cancellationToken = default)
        {
            RemoveCount++;
            _entered.TrySetResult();
            return new ValueTask<InternalDriverRemovalResult>(_result.Task);
        }

        public void Complete(InternalDriverRemovalResult result) =>
            _result.TrySetResult(result);

        public void Fail(Exception failure) => _result.TrySetException(failure);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
