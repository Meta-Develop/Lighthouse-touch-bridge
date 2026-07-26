using Ltb.App;
using Ltb.Driver;
using Ltb.Gui.ViewModels;

namespace Ltb.Gui.Tests;

public sealed class TrackerBindingViewModelTests
{
    [Fact]
    public async Task RefreshPresentsCanonicalOptionsAndRequiresDistinctSelection()
    {
        var control = new FakePreSessionControl(Snapshot(
            left: "LHR-LEFT",
            right: "LHR-RIGHT"));
        await using var viewModel = new TrackerBindingViewModel(
            control,
            action => action());

        await viewModel.InitializeAsync();

        Assert.Equal(2, viewModel.Trackers.Count);
        Assert.Equal("LHR-LEFT", viewModel.SelectedLeftTracker?.Serial);
        Assert.Equal("LHR-RIGHT", viewModel.SelectedRightTracker?.Serial);
        Assert.True(viewModel.CanSaveBinding);
        viewModel.SelectedRightTracker = viewModel.SelectedLeftTracker;
        Assert.False(viewModel.CanSaveBinding);
    }

    [Fact]
    public async Task MismatchExposesExplicitRetainAndAcceptActions()
    {
        var control = new FakePreSessionControl(Snapshot(
            left: "LHR-LEFT",
            right: "LHR-RIGHT"));
        await using var viewModel = new TrackerBindingViewModel(
            control,
            action => action());
        viewModel.ApplyVerification(new InternalDriverManualBindingVerificationEvidence(
            InternalDriverManualBindingVerificationState.MismatchCorrectionCandidate,
            "LHR-LEFT",
            "LHR-RIGHT",
            "Motion correlation suggests a swap.",
            "LHR-RIGHT",
            "LHR-LEFT"));

        Assert.True(viewModel.HasCorrectionChoice);
        Assert.True(viewModel.RetainManualBindingCommand.CanExecute(null));
        Assert.True(viewModel.AcceptCorrectionCommand.CanExecute(null));
        Assert.Contains("Choose Retain", viewModel.VerificationStatusText);

        await viewModel.ApplyVerificationDecisionAsync(
            InternalDriverManualBindingDecision.RetainManualBinding);

        Assert.Equal(
            InternalDriverManualBindingDecision.RetainManualBinding,
            Assert.Single(control.Decisions));
        Assert.False(viewModel.HasCorrectionChoice);
        Assert.Contains("retained explicitly", viewModel.VerificationStatusText);
    }

    [Fact]
    public async Task AcceptCorrectionCallsTypedAppDecisionAndClearsPendingChoice()
    {
        var control = new FakePreSessionControl(Snapshot(
            left: "LHR-LEFT",
            right: "LHR-RIGHT"));
        await using var viewModel = new TrackerBindingViewModel(
            control,
            action => action());
        viewModel.ApplyVerification(new InternalDriverManualBindingVerificationEvidence(
            InternalDriverManualBindingVerificationState.MismatchCorrectionCandidate,
            "LHR-LEFT",
            "LHR-RIGHT",
            "Motion correlation suggests a swap.",
            "LHR-RIGHT",
            "LHR-LEFT"));

        await viewModel.ApplyVerificationDecisionAsync(
            InternalDriverManualBindingDecision.AcceptCorrectionCandidate);

        Assert.Equal(
            InternalDriverManualBindingDecision.AcceptCorrectionCandidate,
            Assert.Single(control.Decisions));
        Assert.False(viewModel.HasCorrectionChoice);
        Assert.Contains("accepted explicitly", viewModel.VerificationStatusText);
    }

    [Fact]
    public async Task StartPreflightFailurePreventsSessionFactoryCreation()
    {
        var control = new FakePreSessionControl(Snapshot(
            left: "LHR-LEFT",
            right: "LHR-RIGHT") with
        {
            State = InternalDriverPreSessionState.RegisteredDevicePathUnresolved,
            Diagnostic = "Registered device path unresolved; no write attempted.",
            Remediation = "Collect Windows path provenance.",
        });
        var factory = new CountingSessionFactory();
        await using var viewModel = new InternalDriverViewModel(
            factory,
            action => action(),
            preSessionControl: control);

        await viewModel.StartAsync();

        Assert.Equal(0, factory.CreateCalls);
        Assert.Equal(InternalDriverSessionState.Stopped, viewModel.CurrentPhase);
        Assert.Equal("Action required", viewModel.OverallStatus);
        Assert.Contains("no write", viewModel.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionSnapshotMismatchReachesTheExplicitGuiDecisionSurface()
    {
        var control = new FakePreSessionControl(Snapshot());
        var verification = new InternalDriverManualBindingVerificationEvidence(
            InternalDriverManualBindingVerificationState.MismatchCorrectionCandidate,
            "LHR-LEFT",
            "LHR-RIGHT",
            "Motion correlation suggests a swap.",
            "LHR-RIGHT",
            "LHR-LEFT");
        var session = new ImmediateSession(
            InternalDriverSessionSnapshot.Initial with
            {
                ManualBindingVerification = verification,
            });
        await using var viewModel = new InternalDriverViewModel(
            new SingleSessionFactory(session),
            action => action(),
            preSessionControl: control);

        await viewModel.StartAsync();

        Assert.True(viewModel.TrackerBinding.HasCorrectionChoice);
        Assert.Contains(
            "Motion correlation suggests a swap",
            viewModel.TrackerBinding.VerificationStatusText);
        Assert.True(viewModel.TrackerBinding.AcceptCorrectionCommand.CanExecute(null));
        Assert.True(viewModel.TrackerBinding.RetainManualBindingCommand.CanExecute(null));
    }

    [Fact]
    public async Task PassthroughControlAllowsRestartAfterReportingSkippedCleanup()
    {
        await using var control = new PassthroughInternalDriverPreSessionControl();

        var cleanup = await control.CompleteControlledStopAsync();
        var nextStart = await control.PrepareStartAsync();

        Assert.Equal(InternalDriverPreSessionState.CleanupSkipped, cleanup.State);
        Assert.Equal(InternalDriverPreSessionState.Ready, nextStart.State);
        Assert.True(nextStart.CanStart);
    }

    [Fact]
    public async Task ControlledStopQueuesBehindBusyRefreshAndRunsCleanupExactlyOnce()
    {
        var control = new SerializedBlockingPreSessionControl(Snapshot());
        await using var viewModel = new TrackerBindingViewModel(
            control,
            action => action());

        var refresh = viewModel.RefreshAsync();
        await control.RefreshEntered;
        Assert.True(viewModel.IsBusy);

        var cleanup = viewModel.CompleteControlledStopAsync();
        Assert.False(cleanup.IsCompleted);
        Assert.Equal(0, control.CleanupCalls);
        Assert.True(viewModel.IsBusy);

        control.ReleaseRefresh();
        await control.CleanupEntered;
        await refresh;
        Assert.False(cleanup.IsCompleted);
        Assert.Equal(1, control.CleanupCalls);
        Assert.True(viewModel.IsBusy);

        control.ReleaseCleanup();
        _ = await cleanup;

        Assert.Equal(1, control.CleanupCalls);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.CanEdit);
    }

    [Fact]
    public async Task ControlledStopAfterDisposalDoesNotReachAppControl()
    {
        var control = new FakePreSessionControl(Snapshot());
        var viewModel = new TrackerBindingViewModel(
            control,
            action => action());

        await viewModel.DisposeAsync();
        _ = await viewModel.CompleteControlledStopAsync();

        Assert.Equal(0, control.CleanupCalls);
        Assert.False(viewModel.CanEdit);
    }

    private static InternalDriverPreSessionSnapshot Snapshot(
        string? left = null,
        string? right = null) => new(
        InternalDriverPreSessionState.Ready,
        new[]
        {
            new InternalDriverPairedTrackerOption("LHR-LEFT", "Vive Tracker"),
            new InternalDriverPairedTrackerOption("LHR-RIGHT", "Vive Tracker"),
        },
        left,
        right,
        UnregisterOnExit: true,
        SteamVrDriverStartupState.NoLtbRegistration,
        new InternalDriverSteamVrProcessSnapshot(false, false),
        "Ready.",
        "No remediation.");

    private sealed class FakePreSessionControl(
        InternalDriverPreSessionSnapshot snapshot) :
        IInternalDriverPreSessionControl
    {
        public List<InternalDriverManualBindingDecision> Decisions { get; } = [];

        public int CleanupCalls { get; private set; }

        public InternalDriverPreSessionSnapshot CurrentSnapshot { get; private set; } = snapshot;

        public ValueTask<InternalDriverPreSessionSnapshot> RefreshAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CurrentSnapshot);

        public ValueTask<InternalDriverPreSessionSnapshot> SaveManualBindingAsync(
            string leftTrackerSerial,
            string rightTrackerSerial,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CurrentSnapshot);

        public ValueTask<InternalDriverPreSessionSnapshot> ClearManualBindingAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CurrentSnapshot);

        public ValueTask<InternalDriverPreSessionSnapshot> SetUnregisterOnExitAsync(
            bool enabled,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CurrentSnapshot);

        public ValueTask<InternalDriverPreSessionSnapshot> ApplyManualBindingDecisionAsync(
            InternalDriverManualBindingVerificationEvidence verification,
            InternalDriverManualBindingDecision decision,
            CancellationToken cancellationToken = default)
        {
            Decisions.Add(decision);
            CurrentSnapshot = CurrentSnapshot with
            {
                Diagnostic = decision ==
                    InternalDriverManualBindingDecision.AcceptCorrectionCandidate
                        ? "Accepted correction."
                        : "Retained manual binding.",
            };
            return ValueTask.FromResult(CurrentSnapshot);
        }

        public ValueTask<InternalDriverPreSessionSnapshot> PrepareStartAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CurrentSnapshot);

        public ValueTask<InternalDriverPreSessionSnapshot> CompleteControlledStopAsync(
            CancellationToken cancellationToken = default)
        {
            CleanupCalls++;
            return ValueTask.FromResult(CurrentSnapshot);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SerializedBlockingPreSessionControl(
        InternalDriverPreSessionSnapshot snapshot) :
        IInternalDriverPreSessionControl
    {
        private readonly SemaphoreSlim _serialization = new(1, 1);
        private readonly TaskCompletionSource _refreshEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseRefresh =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cleanupEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseCleanup =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _cleanupCalls;

        public InternalDriverPreSessionSnapshot CurrentSnapshot { get; } = snapshot;

        public Task RefreshEntered => _refreshEntered.Task;

        public Task CleanupEntered => _cleanupEntered.Task;

        public int CleanupCalls => Volatile.Read(ref _cleanupCalls);

        public async ValueTask<InternalDriverPreSessionSnapshot> RefreshAsync(
            CancellationToken cancellationToken = default)
        {
            await _serialization.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _refreshEntered.TrySetResult();
                await _releaseRefresh.Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                return CurrentSnapshot;
            }
            finally
            {
                _serialization.Release();
            }
        }

        public ValueTask<InternalDriverPreSessionSnapshot> SaveManualBindingAsync(
            string leftTrackerSerial,
            string rightTrackerSerial,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CurrentSnapshot);

        public ValueTask<InternalDriverPreSessionSnapshot> ClearManualBindingAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CurrentSnapshot);

        public ValueTask<InternalDriverPreSessionSnapshot> SetUnregisterOnExitAsync(
            bool enabled,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CurrentSnapshot);

        public ValueTask<InternalDriverPreSessionSnapshot> ApplyManualBindingDecisionAsync(
            InternalDriverManualBindingVerificationEvidence verification,
            InternalDriverManualBindingDecision decision,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CurrentSnapshot);

        public ValueTask<InternalDriverPreSessionSnapshot> PrepareStartAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CurrentSnapshot);

        public async ValueTask<InternalDriverPreSessionSnapshot> CompleteControlledStopAsync(
            CancellationToken cancellationToken = default)
        {
            await _serialization.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Interlocked.Increment(ref _cleanupCalls);
                _cleanupEntered.TrySetResult();
                await _releaseCleanup.Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                return CurrentSnapshot;
            }
            finally
            {
                _serialization.Release();
            }
        }

        public void ReleaseRefresh() => _releaseRefresh.TrySetResult();

        public void ReleaseCleanup() => _releaseCleanup.TrySetResult();

        public ValueTask DisposeAsync()
        {
            _serialization.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingSessionFactory : IInternalDriverSessionFactory
    {
        public int CreateCalls { get; private set; }

        public IInternalDriverSession Create(InternalDriverSessionIntent intent)
        {
            CreateCalls++;
            throw new InvalidOperationException("Preflight should prevent session creation.");
        }
    }

    private sealed class SingleSessionFactory(IInternalDriverSession session) :
        IInternalDriverSessionFactory
    {
        public IInternalDriverSession Create(InternalDriverSessionIntent intent) => session;
    }

    private sealed class ImmediateSession(InternalDriverSessionSnapshot snapshot) :
        IInternalDriverSession
    {
        public event EventHandler<InternalDriverSessionSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public InternalDriverSessionSnapshot CurrentSnapshot { get; } = snapshot;

        public Task RunAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
