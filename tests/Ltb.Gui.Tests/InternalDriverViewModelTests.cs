using Ltb.App;
using Ltb.Driver;
using Ltb.Gui;
using Ltb.Gui.ViewModels;
using Ltb.MetaLink;
using Ltb.Protocol;

namespace Ltb.Gui.Tests;

public sealed class InternalDriverViewModelTests
{
    [Fact]
    public async Task TypedSnapshotUpdatesStableRowsHandsGlobalPhaseEstimateAndFeedHealth()
    {
        var initial = Snapshot(
            InternalDriverSessionState.DependencyCheck,
            allReady: false,
            leftProfile: InternalDriverProfileReadiness.Missing,
            rightProfile: InternalDriverProfileReadiness.Missing);
        var session = new ControlledSession(initial);
        await using var viewModel = NewViewModel(session);
        var rows = viewModel.ReadinessRows.ToArray();

        var run = viewModel.StartAsync();
        await session.Started;
        session.Publish(Snapshot(
            InternalDriverSessionState.RotationSolve,
            allReady: false,
            leftProfile: InternalDriverProfileReadiness.Missing,
            rightProfile: InternalDriverProfileReadiness.Incompatible,
            feedReadiness: DriverFeedReadiness.Reconnecting,
            reconnectAttempts: 3,
            feedError: "pipe unavailable"));

        Assert.Equal(12, rows.Length);
        Assert.Equal(rows, viewModel.ReadinessRows);
        Assert.Equal(InternalDriverSessionState.RotationSolve, viewModel.CurrentPhase);
        Assert.Equal("Rotation Solve", viewModel.PhaseText);
        Assert.Equal("LHR-LEFT", viewModel.LeftHand.TrackerSerial);
        Assert.Equal("LHR-RIGHT", viewModel.RightHand.TrackerSerial);
        Assert.Equal("Tracked", viewModel.LeftHand.TrackerStatus);
        Assert.Equal("Connected / not tracked", viewModel.RightHand.TrackerStatus);
        Assert.Equal("Publishing", viewModel.LeftHand.PublishingStatus);
        Assert.Equal("Neutral", viewModel.RightHand.PublishingStatus);
        Assert.Equal("None", viewModel.LeftHand.NeutralReason);
        Assert.Equal("Tracker Pose Invalid", viewModel.RightHand.NeutralReason);
        Assert.Equal(0.60d, viewModel.LeftHand.GlobalCalibrationPhaseEstimate);
        Assert.Equal(
            viewModel.LeftHand.GlobalCalibrationPhaseEstimate,
            viewModel.RightHand.GlobalCalibrationPhaseEstimate);
        Assert.Contains("Not exposed", viewModel.LeftHand.CalibrationMode, StringComparison.Ordinal);
        Assert.Contains("Not exposed", viewModel.RightHand.CalibrationQuality, StringComparison.Ordinal);
        Assert.Equal("Reconnecting", viewModel.FeedState);
        Assert.Equal("0123456789ABCDEF0FEDCBA987654321", viewModel.FeedSession);
        Assert.Equal("42", viewModel.FeedSequence);
        Assert.Equal("25.0 ms", viewModel.FeedHeartbeatAge);
        Assert.Equal("12.0 ms", viewModel.FeedSendAge);
        Assert.Equal(3, viewModel.FeedReconnectAttempts);
        Assert.Equal("pipe unavailable", viewModel.FeedError);

        session.AllowStop();
        await viewModel.StopAsync();
        await run;
    }

    [Fact]
    public async Task DispatcherOwnsInitialRowsAndEverySnapshotPresentation()
    {
        var dispatcher = new QueuedDispatcher();
        var session = new ControlledSession(Snapshot(
            InternalDriverSessionState.DependencyCheck,
            allReady: false));
        await using var viewModel = new InternalDriverViewModel(
            new QueueSessionFactory(session),
            dispatcher.Post);

        Assert.Empty(viewModel.ReadinessRows);
        dispatcher.Drain();
        Assert.Equal(12, viewModel.ReadinessRows.Count);

        var run = viewModel.StartAsync();
        await session.Started;
        Assert.False(viewModel.IsRunning);
        Assert.Equal(InternalDriverSessionState.Stopped, viewModel.CurrentPhase);

        dispatcher.Drain();
        Assert.True(viewModel.IsRunning);
        Assert.Equal(InternalDriverSessionState.DependencyCheck, viewModel.CurrentPhase);

        session.Publish(Snapshot(
            InternalDriverSessionState.Validation,
            allReady: false));
        Assert.Equal(InternalDriverSessionState.DependencyCheck, viewModel.CurrentPhase);
        dispatcher.Drain();
        Assert.Equal(InternalDriverSessionState.Validation, viewModel.CurrentPhase);

        session.AllowStop();
        await viewModel.StopAsync();
        await run;
        dispatcher.Drain();
        Assert.False(viewModel.IsRunning);
        Assert.True(dispatcher.PostCount >= 4);
    }

    [Fact]
    public async Task RestartRequiredCanNeverRenderReady()
    {
        var session = new ControlledSession(Snapshot(
            InternalDriverSessionState.Active,
            allReady: true,
            restartRequired: true,
            leftProfile: InternalDriverProfileReadiness.Calibrated,
            rightProfile: InternalDriverProfileReadiness.Reused,
            feedReadiness: DriverFeedReadiness.Ready));
        await using var viewModel = NewViewModel(session);

        var run = viewModel.StartAsync();
        await session.Started;

        Assert.False(viewModel.IsReady);
        Assert.True(viewModel.RestartRequired);
        Assert.Equal("Restart required", viewModel.OverallStatus);
        Assert.Equal(
            "Restart required",
            Assert.Single(
                viewModel.ReadinessRows,
                row => row.Key == "driver-registration").Status);
        Assert.Equal(1d, viewModel.LeftHand.GlobalCalibrationPhaseEstimate);

        session.AllowStop();
        await viewModel.StopAsync();
        await run;
    }

    [Theory]
    [InlineData(InternalDriverSessionState.DependencyCheck)]
    [InlineData(InternalDriverSessionState.RotationSolve)]
    [InlineData(InternalDriverSessionState.StartingFeed)]
    [InlineData(InternalDriverSessionState.Active)]
    [InlineData(InternalDriverSessionState.Reconnecting)]
    public async Task StopCancelsAndAwaitsFailSafeStopAndDisposal(
        InternalDriverSessionState phase)
    {
        var session = new ControlledSession(Snapshot(
            phase,
            allReady: phase == InternalDriverSessionState.Active,
            feedReadiness: phase == InternalDriverSessionState.Reconnecting
                ? DriverFeedReadiness.Reconnecting
                : DriverFeedReadiness.Ready));
        await using var viewModel = NewViewModel(session);

        var run = viewModel.StartAsync();
        await session.Started;
        Assert.Equal("Stop", viewModel.ActionButtonText);

        var stop = viewModel.StopAsync();
        await session.StopEntered;
        await session.CancellationObserved;
        Assert.False(stop.IsCompleted);
        Assert.False(session.DisposeCompleted);

        session.AllowStop();
        await stop;
        await run;

        Assert.True(session.DisposeCompleted);
        Assert.False(viewModel.IsRunning);
        Assert.Equal("Start", viewModel.ActionButtonText);
        Assert.Equal(InternalDriverSessionState.Stopped, viewModel.CurrentPhase);
        Assert.Equal("Stopped", viewModel.OverallStatus);
    }

    [Fact]
    public async Task CloseCancelsAndAwaitsSessionDisposal()
    {
        var session = new ControlledSession(Snapshot(
            InternalDriverSessionState.Reconnecting,
            allReady: false,
            feedReadiness: DriverFeedReadiness.Reconnecting));
        var viewModel = NewViewModel(session);

        var run = viewModel.StartAsync();
        await session.Started;
        var close = viewModel.CloseAsync();
        await session.StopEntered;
        Assert.False(close.IsCompleted);

        session.AllowStop();
        await close;
        await run;

        Assert.True(session.DisposeCompleted);
        Assert.False(viewModel.CanToggle);
        Assert.False(viewModel.ActionCommand.CanExecute(null));
    }

    [Fact]
    public async Task FailedRunRendersActionableErrorAndStillDisposes()
    {
        var session = new ThrowingSession(Snapshot(
            InternalDriverSessionState.DependencyCheck,
            allReady: false));
        await using var viewModel = NewViewModel(session);

        await viewModel.StartAsync();

        Assert.Equal("Action required", viewModel.OverallStatus);
        Assert.Equal(
            "Internal-driver session failed: session exploded",
            viewModel.LastError);
        Assert.True(session.DisposeCompleted);
        Assert.False(viewModel.IsRunning);
    }

    [Fact]
    public async Task SecondStartRequestsFreshSessionAfterFirstIsDisposed()
    {
        var first = new ControlledSession(Snapshot(
            InternalDriverSessionState.DependencyCheck,
            allReady: false));
        var second = new ControlledSession(Snapshot(
            InternalDriverSessionState.Active,
            allReady: true,
            feedReadiness: DriverFeedReadiness.Ready));
        var factory = new QueueSessionFactory(first, second);
        await using var viewModel = new InternalDriverViewModel(factory, action => action());

        var firstRun = viewModel.StartAsync();
        await first.Started;
        first.AllowStop();
        await viewModel.StopAsync();
        await firstRun;

        var secondRun = viewModel.StartAsync();
        await second.Started;

        Assert.Equal(2, factory.CreateCount);
        Assert.True(first.DisposeCompleted);
        Assert.Equal(InternalDriverSessionState.Active, viewModel.CurrentPhase);
        Assert.True(viewModel.IsReady);

        second.AllowStop();
        await viewModel.StopAsync();
        await secondRun;
    }

    private static InternalDriverViewModel NewViewModel(IInternalDriverSession session) =>
        new(new QueueSessionFactory(session), action => action());

    private static InternalDriverSessionSnapshot Snapshot(
        InternalDriverSessionState state,
        bool allReady,
        bool restartRequired = false,
        InternalDriverProfileReadiness leftProfile = InternalDriverProfileReadiness.Calibrated,
        InternalDriverProfileReadiness rightProfile = InternalDriverProfileReadiness.Calibrated,
        DriverFeedReadiness feedReadiness = DriverFeedReadiness.Stopped,
        int reconnectAttempts = 0,
        string? feedError = null)
    {
        var readiness = new InternalDriverSessionReadiness(
            PlatformSupported: allReady,
            SteamVrRunning: allReady,
            MetaBothHandsReady: allReady,
            TwoDistinctTrackersReady: allReady,
            ProfilesReady: allReady,
            DriverRegistered: allReady,
            DriverLoaded: allReady,
            FeedReady: allReady);
        var left = new InternalDriverHandSnapshot(
            ProtocolHand.Left,
            "LHR-LEFT",
            TrackerConnected: true,
            TrackerTracked: true,
            MetaLinkReadiness.Ready,
            MetaInputsValid: true,
            leftProfile,
            PoseAge: TimeSpan.FromMilliseconds(7),
            IsPublishing: true,
            InternalDriverNeutralReason.None,
            "Left typed diagnostic.");
        var right = new InternalDriverHandSnapshot(
            ProtocolHand.Right,
            "LHR-RIGHT",
            TrackerConnected: true,
            TrackerTracked: false,
            MetaLinkReadiness.ControllersUnavailable,
            MetaInputsValid: false,
            rightProfile,
            PoseAge: TimeSpan.FromMilliseconds(19),
            IsPublishing: false,
            InternalDriverNeutralReason.TrackerPoseInvalid,
            "Right typed diagnostic.");
        var feed = new InternalDriverFeedSnapshot(
            feedReadiness,
            new ProtocolSessionId(0x0123456789ABCDEF, 0x0FEDCBA987654321),
            LastSuccessfulSequence: 42,
            LastSuccessfulSendAge: TimeSpan.FromMilliseconds(12),
            LastSuccessfulHeartbeatAge: TimeSpan.FromMilliseconds(25),
            reconnectAttempts,
            feedError);
        return new InternalDriverSessionSnapshot(
            state,
            readiness,
            left,
            right,
            feed,
            restartRequired,
            "Typed session diagnostic.",
            "Typed remediation.");
    }

    private sealed class QueueSessionFactory : IInternalDriverSessionFactory
    {
        private readonly Queue<IInternalDriverSession> _sessions;

        public QueueSessionFactory(params IInternalDriverSession[] sessions)
        {
            _sessions = new Queue<IInternalDriverSession>(sessions);
        }

        public int CreateCount { get; private set; }

        public IInternalDriverSession Create()
        {
            CreateCount++;
            return _sessions.Dequeue();
        }
    }

    private sealed class ControlledSession : IInternalDriverSession
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowStop =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _runExit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ControlledSession(InternalDriverSessionSnapshot snapshot)
        {
            CurrentSnapshot = snapshot;
        }

        public event EventHandler<InternalDriverSessionSnapshot>? SnapshotChanged;

        public InternalDriverSessionSnapshot CurrentSnapshot { get; private set; }

        public Task Started => _started.Task;

        public Task StopEntered => _stopEntered.Task;

        public Task CancellationObserved => _cancellationObserved.Task;

        public bool DisposeCompleted { get; private set; }

        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(
                () => _cancellationObserved.TrySetResult());
            _started.TrySetResult();
            await _runExit.Task.ConfigureAwait(false);
        }

        public async ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            _stopEntered.TrySetResult();
            await _allowStop.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            CurrentSnapshot = CreateStoppedSnapshot(CurrentSnapshot);
            SnapshotChanged?.Invoke(this, CurrentSnapshot);
            _runExit.TrySetResult();
        }

        public ValueTask DisposeAsync()
        {
            DisposeCompleted = true;
            return ValueTask.CompletedTask;
        }

        public void Publish(InternalDriverSessionSnapshot snapshot)
        {
            CurrentSnapshot = snapshot;
            SnapshotChanged?.Invoke(this, snapshot);
        }

        public void AllowStop() => _allowStop.TrySetResult();
    }

    private static InternalDriverSessionSnapshot CreateStoppedSnapshot(
        InternalDriverSessionSnapshot current) => current with
    {
        State = InternalDriverSessionState.Stopped,
        Readiness = new InternalDriverSessionReadiness(
            PlatformSupported: false,
            SteamVrRunning: false,
            MetaBothHandsReady: false,
            TwoDistinctTrackersReady: false,
            ProfilesReady: false,
            DriverRegistered: false,
            DriverLoaded: false,
            FeedReady: false),
        Left = current.Left with
        {
            IsPublishing = false,
            NeutralReason = InternalDriverNeutralReason.SessionStopped,
        },
        Right = current.Right with
        {
            IsPublishing = false,
            NeutralReason = InternalDriverNeutralReason.SessionStopped,
        },
        Feed = new InternalDriverFeedSnapshot(
            DriverFeedReadiness.Stopped,
            SessionId: null,
            LastSuccessfulSequence: null,
            LastSuccessfulSendAge: null,
            LastSuccessfulHeartbeatAge: null,
            ReconnectAttempts: 0,
            LastError: null),
        RestartRequired = false,
        Diagnostic = "Internal-driver session stopped.",
        Remediation = "Press Start to run a new session.",
    };

    private sealed class ThrowingSession : IInternalDriverSession
    {
        public ThrowingSession(InternalDriverSessionSnapshot snapshot)
        {
            CurrentSnapshot = snapshot;
        }

        public event EventHandler<InternalDriverSessionSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public InternalDriverSessionSnapshot CurrentSnapshot { get; }

        public bool DisposeCompleted { get; private set; }

        public Task RunAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("session exploded");

        public ValueTask StopAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCompleted = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class QueuedDispatcher
    {
        private readonly Queue<Action> _actions = new();

        public int PostCount { get; private set; }

        public void Post(Action action)
        {
            PostCount++;
            _actions.Enqueue(action);
        }

        public void Drain()
        {
            while (_actions.TryDequeue(out var action))
            {
                action();
            }
        }
    }
}
