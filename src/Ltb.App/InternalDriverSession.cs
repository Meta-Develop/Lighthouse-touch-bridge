using Ltb.Core;
using Ltb.Driver;
using Ltb.MetaLink;
using Ltb.OpenVr;
using Ltb.Protocol;

namespace Ltb.App;

/// <summary>
/// Owns one complete first-party run. The session never consumes SteamVR
/// controller poses: Meta supplies inputs/readiness and the exact associated
/// raw tracker serial supplies pose and protocol sample time.
/// </summary>
internal sealed class InternalDriverSession : IInternalDriverSession
{
    private readonly IInternalDriverSessionRuntime _runtime;
    private readonly IInternalDriverSessionOutput _output;
    private readonly InternalDriverSessionOptions _options;
    private readonly InternalDriverInputMapper _inputMapper = new();
    private readonly object _lifecycleSync = new();
    private readonly object _publicationSync = new();
    private readonly object _snapshotSync = new();
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private IDriverFeed? _feed;
    private InternalDriverRegistration? _registration;
    private InternalDriverPlatformProbe? _platformProbe;
    private InternalDriverProfilePair? _profiles;
    private InternalDriverRuntimeObservation? _lastObservation;
    private InternalDriverCaptureEvidence? _leftCapture;
    private InternalDriverCaptureEvidence? _rightCapture;
    private ulong _lastLeftTimestamp;
    private ulong _lastRightTimestamp;
    private long _publicationGeneration;
    private bool _publicationFinalized = true;
    private bool _disposed;
    private InternalDriverSessionSnapshot _snapshot = InternalDriverSessionSnapshot.Initial;

    internal InternalDriverSession(
        IInternalDriverSessionRuntime runtime,
        InternalDriverSessionOptions? options = null,
        IInternalDriverSessionOutput? output = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _options = options ?? new InternalDriverSessionOptions();
        _options.Validate();
        _output = output ?? NullInternalDriverSessionOutput.Instance;
    }

    public event EventHandler<InternalDriverSessionSnapshot>? SnapshotChanged;

    public InternalDriverSessionSnapshot CurrentSnapshot
    {
        get
        {
            lock (_snapshotSync)
            {
                return _snapshot;
            }
        }
    }

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runTask is { IsCompleted: false } running)
            {
                return running;
            }

            _runCancellation?.Dispose();
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runTask = RunAndReleaseAsync(_runCancellation);
            return _runTask;
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        Task? runTask;
        lock (_lifecycleSync)
        {
            runTask = _runTask;
            _runCancellation?.Cancel();
        }

        if (runTask is null)
        {
            return;
        }

        try
        {
            await runTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lifecycleSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await StopAsync().ConfigureAwait(false);
        await _runtime.DisposeAsync().ConfigureAwait(false);
        _output.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RunAndReleaseAsync(CancellationTokenSource runCancellation)
    {
        try
        {
            await RunCoreAsync(runCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_lifecycleSync)
            {
                if (ReferenceEquals(_runCancellation, runCancellation))
                {
                    _runCancellation = null;
                    _runTask = null;
                }
            }

            runCancellation.Dispose();
        }
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        ResetRunState();
        PublishState(
            InternalDriverSessionState.DependencyCheck,
            "Checking the Windows, SteamVR, Meta Link, OpenVR, and staged driver dependencies.",
            "Install or repair any dependency named by the next diagnostic.");

        try
        {
            var probe = _runtime.Probe();
            _platformProbe = probe;
            if (!probe.IsSupported)
            {
                PublishState(
                    InternalDriverSessionState.Faulted,
                    probe.Diagnostic,
                    probe.Remediation,
                    leftReason: InternalDriverNeutralReason.DependencyUnavailable,
                    rightReason: InternalDriverNeutralReason.DependencyUnavailable);
                return;
            }

            _registration = await _runtime.EnsureDriverAsync(cancellationToken).ConfigureAwait(false);
            if (!_registration.IsRegistered)
            {
                PublishState(
                    InternalDriverSessionState.Faulted,
                    _registration.Diagnostic,
                    "Repair the staged driver and OpenVR registration, then run the session again.",
                    leftReason: InternalDriverNeutralReason.DriverNotReady,
                    rightReason: InternalDriverNeutralReason.DriverNotReady);
                return;
            }

            if (_registration.RestartRequired)
            {
                PublishState(
                    InternalDriverSessionState.WaitingForSteamVR,
                    _registration.Diagnostic,
                    "Restart SteamVR once so it loads the newly registered staged build.",
                    restartRequired: true,
                    leftReason: InternalDriverNeutralReason.DriverNotReady,
                    rightReason: InternalDriverNeutralReason.DriverNotReady);
            }

            var readyObservation = await WaitForInitialReadinessAsync(cancellationToken)
                .ConfigureAwait(false);
            PublishState(
                InternalDriverSessionState.Ready,
                "SteamVR, Meta Link, controller-source tracker candidates, and the loaded first-party driver are ready.",
                "Keep both Touch controllers awake and move only the requested hand if calibration is required.",
                observation: readyObservation);

            _profiles = await _runtime.ResolveProfilesAsync(
                readyObservation,
                ReportCalibrationProgress,
                cancellationToken).ConfigureAwait(false);
            if (!_profiles.IsValid)
            {
                throw new InvalidOperationException(
                    "Calibration returned an invalid or non-distinct tracker/profile pair.");
            }

            EnsureProfileTrackersWereObserved(readyObservation, _profiles);
            await StartFreshFeedAsync(readyObservation, cancellationToken).ConfigureAwait(false);
            await MonitorActiveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var restartRequired = CurrentSnapshot.RestartRequired;
            PublishState(
                InternalDriverSessionState.Stopped,
                "Internal-driver session cancellation was requested.",
                "Run the session again when the controllers are needed.",
                restartRequired: restartRequired,
                leftReason: InternalDriverNeutralReason.Stopping,
                rightReason: InternalDriverNeutralReason.Stopping);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var restartRequired = CurrentSnapshot.RestartRequired;
            PublishState(
                InternalDriverSessionState.Faulted,
                $"Internal-driver session failed: {exception.Message}",
                "Correct the reported dependency, topology, calibration, or feed failure, then run the session again.",
                restartRequired: restartRequired,
                leftReason: InternalDriverNeutralReason.Faulted,
                rightReason: InternalDriverNeutralReason.Faulted);
        }
        finally
        {
            var faultSnapshot = CurrentSnapshot.State == InternalDriverSessionState.Faulted
                ? CurrentSnapshot
                : null;
            var stoppedSnapshot = CurrentSnapshot.State == InternalDriverSessionState.Stopped
                ? CurrentSnapshot
                : null;
            await CleanupRunAsync().ConfigureAwait(false);
            faultSnapshot ??= CurrentSnapshot.State == InternalDriverSessionState.Faulted
                ? CurrentSnapshot
                : null;
            stoppedSnapshot ??= CurrentSnapshot.State == InternalDriverSessionState.Stopped
                ? CurrentSnapshot
                : null;
            ClearRunEvidence();
            var finalSnapshot = faultSnapshot is { } fault
                ? CreateFinalFaultSnapshot(fault)
                : stoppedSnapshot is { } stopped
                    ? CreateFinalStoppedSnapshot(stopped)
                    : CreateStateSnapshot(
                        InternalDriverSessionState.Stopped,
                        "Internal-driver session stopped; both hands are neutral and runtime resources are retired.",
                        "Run the session again to start a new feed session.",
                        leftReason: InternalDriverNeutralReason.SessionStopped,
                        rightReason: InternalDriverNeutralReason.SessionStopped,
                        retainRunEvidence: false);
            PublishFinalSnapshot(finalSnapshot);
        }
    }

    private InternalDriverSessionSnapshot CreateFinalFaultSnapshot(
        InternalDriverSessionSnapshot faultSnapshot)
    {
        return CreateFinalTerminalSnapshot(faultSnapshot);
    }

    private InternalDriverSessionSnapshot CreateFinalStoppedSnapshot(
        InternalDriverSessionSnapshot stoppedSnapshot)
    {
        return CreateFinalTerminalSnapshot(stoppedSnapshot);
    }

    private InternalDriverSessionSnapshot CreateFinalTerminalSnapshot(
        InternalDriverSessionSnapshot terminalSnapshot)
    {
        var rebuilt = CreateStateSnapshot(
            terminalSnapshot.State,
            terminalSnapshot.Diagnostic,
            terminalSnapshot.Remediation,
            restartRequired: terminalSnapshot.RestartRequired,
            leftReason: terminalSnapshot.Left.NeutralReason,
            rightReason: terminalSnapshot.Right.NeutralReason,
            retainRunEvidence: false);
        return rebuilt with
        {
            Left = rebuilt.Left with { Diagnostic = terminalSnapshot.Left.Diagnostic },
            Right = rebuilt.Right with { Diagnostic = terminalSnapshot.Right.Diagnostic },
        };
    }

    private async ValueTask<InternalDriverRuntimeObservation> WaitForInitialReadinessAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = _runtime.Observe();
            _lastObservation = observation;
            var waiting = InitialWaitingState(observation, out var diagnostic, out var remediation);
            if (waiting is null)
            {
                return observation;
            }

            PublishState(
                waiting.Value,
                diagnostic,
                remediation,
                observation,
                restartRequired: _registration?.RestartRequired == true,
                leftReason: WaitingNeutralReason(waiting.Value),
                rightReason: WaitingNeutralReason(waiting.Value));
            await _runtime.DelayAsync(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private InternalDriverSessionState? InitialWaitingState(
        InternalDriverRuntimeObservation observation,
        out string diagnostic,
        out string remediation)
    {
        if (!observation.SteamVrRunning)
        {
            diagnostic = observation.SteamVrDiagnostic;
            remediation = "Start SteamVR with the intended Lighthouse HMD as the sole HMD.";
            return InternalDriverSessionState.WaitingForSteamVR;
        }

        if (!MetaBothReady(observation.Meta))
        {
            diagnostic = MetaDiagnostic(observation.Meta);
            remediation = "Start Quest Link or Air Link and wake both Touch controllers.";
            return InternalDriverSessionState.WaitingForMetaLink;
        }

        if (!AtLeastTwoReadyTrackerCandidates(observation))
        {
            diagnostic = TrackerDiagnostic(observation);
            remediation = "Connect at least two distinct physical Lighthouse trackers and restore valid raw poses.";
            return InternalDriverSessionState.WaitingForTrackers;
        }

        var loaded = LoadedReadiness(observation);
        if (!loaded.IsReady)
        {
            diagnostic = loaded.Diagnostic;
            remediation = "Remove disallowed SteamVR devices, verify the staged driver registration, and restart SteamVR.";
            return InternalDriverSessionState.WaitingForDriver;
        }

        diagnostic = loaded.Diagnostic;
        remediation = "No remediation is required.";
        return null;
    }

    private async ValueTask StartFreshFeedAsync(
        InternalDriverRuntimeObservation observation,
        CancellationToken cancellationToken)
    {
        await RetireFeedAsync(bestEffortNeutralize: false).ConfigureAwait(false);
        _inputMapper.Reset();
        _lastLeftTimestamp = 0;
        _lastRightTimestamp = 0;
        _feed = _runtime.CreateFeed() ?? throw new InvalidOperationException(
            "The driver feed factory returned null.");
        PublishState(
            InternalDriverSessionState.StartingFeed,
            "Starting a fresh unpredictable IPC session at global sequence zero.",
            "Keep SteamVR running while the first heartbeat and both hand states are published.",
            observation,
            leftReason: InternalDriverNeutralReason.FeedUnavailable,
            rightReason: InternalDriverNeutralReason.FeedUnavailable);
        await StartFeedWithReconnectMonitoringAsync(_feed, observation, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask MonitorActiveAsync(CancellationToken cancellationToken)
    {
        var metaWasLost = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = _runtime.Observe();
            _lastObservation = observation;
            if (!observation.SteamVrRunning)
            {
                await NeutralizeBothAsync(
                    InternalDriverNeutralReason.SteamVrStopped).ConfigureAwait(false);
                await RetireFeedAsync(bestEffortNeutralize: false).ConfigureAwait(false);
                PublishState(
                    InternalDriverSessionState.Stopped,
                    observation.SteamVrDiagnostic,
                    "Restart SteamVR explicitly, then run a new internal-driver session.",
                    observation,
                    leftReason: InternalDriverNeutralReason.SteamVrStopped,
                    rightReason: InternalDriverNeutralReason.SteamVrStopped);
                return;
            }

            var loaded = LoadedReadiness(observation);
            if (!loaded.IsReady)
            {
                await NeutralizeBothAsync(
                    InternalDriverNeutralReason.DriverNotReady).ConfigureAwait(false);
                await RetireFeedAsync(bestEffortNeutralize: false).ConfigureAwait(false);
                PublishState(
                    InternalDriverSessionState.WaitingForDriver,
                    loaded.Diagnostic,
                    "Restore the exact loaded driver build/topology; a fresh feed will start after recovery.",
                    observation,
                    leftReason: InternalDriverNeutralReason.DriverNotReady,
                    rightReason: InternalDriverNeutralReason.DriverNotReady);
                await _runtime.DelayAsync(_options.PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!MetaBothReady(observation.Meta))
            {
                if (!metaWasLost)
                {
                    await NeutralizeBothAsync(
                        InternalDriverNeutralReason.MetaNotReady).ConfigureAwait(false);
                    await RetireFeedAsync(bestEffortNeutralize: false).ConfigureAwait(false);
                    _inputMapper.Reset();
                    _runtime.ResetMeta();
                    metaWasLost = true;
                }

                PublishState(
                    InternalDriverSessionState.WaitingForMetaLink,
                    MetaDiagnostic(observation.Meta),
                    "Reconnect Quest Link or Air Link and wake both controllers; the retired feed session will not be reused.",
                    observation,
                    leftReason: InternalDriverNeutralReason.MetaNotReady,
                    rightReason: InternalDriverNeutralReason.MetaNotReady);
                await _runtime.DelayAsync(_options.PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            metaWasLost = false;
            if (_feed is null)
            {
                await StartFreshFeedAsync(observation, cancellationToken).ConfigureAwait(false);
            }

            var feedBefore = _feed!.Health;
            if (feedBefore.Readiness is DriverFeedReadiness.Reconnecting or DriverFeedReadiness.Faulted ||
                feedBefore.IsStale)
            {
                PublishState(
                    InternalDriverSessionState.Reconnecting,
                    feedBefore.LastError ?? "Driver feed is reconnecting with a fresh IPC session.",
                    "Keep SteamVR running; DriverFeed will create a new random session and reset sequence to zero.",
                    observation,
                    leftReason: InternalDriverNeutralReason.FeedReconnecting,
                    rightReason: InternalDriverNeutralReason.FeedReconnecting);
            }

            var left = await PublishHandAsync(
                _profiles!.Left,
                observation.Meta.Left,
                observation,
                cancellationToken).ConfigureAwait(false);
            var right = await PublishHandAsync(
                _profiles.Right,
                observation.Meta.Right,
                observation,
                cancellationToken).ConfigureAwait(false);
            var health = _feed.Health;
            var state = health.Readiness is DriverFeedReadiness.Reconnecting or DriverFeedReadiness.Faulted ||
                health.IsStale
                    ? InternalDriverSessionState.Reconnecting
                    : InternalDriverSessionState.Active;
            PublishSnapshot(new InternalDriverSessionSnapshot(
                state,
                BuildReadiness(
                    observation,
                    feedReady: health.Readiness == DriverFeedReadiness.Ready && !health.IsStale),
                left,
                right,
                ToFeedSnapshot(health),
                RestartRequired: false,
                state == InternalDriverSessionState.Active
                    ? ActiveDiagnostic(left, right)
                    : health.LastError ?? "The driver feed is reconnecting.",
                state == InternalDriverSessionState.Active
                    ? "No remediation is required."
                    : "Keep SteamVR running while DriverFeed establishes a fresh IPC session.")
            {
                Driver = DriverEvidence(observation),
                LighthouseHmd = LighthouseHmdEvidence(observation),
            });

            await _runtime.DelayAsync(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<InternalDriverHandSnapshot> PublishHandAsync(
        InternalDriverHandProfile profile,
        MetaLinkHandSnapshot meta,
        InternalDriverRuntimeObservation observation,
        CancellationToken cancellationToken)
    {
        if (!observation.TrackerSamples.TryGetValue(profile.TrackerSerial, out var tracker))
        {
            return await PublishNeutralHandAsync(
                profile,
                meta,
                tracker: null,
                InternalDriverNeutralReason.TrackerMissing,
                $"Exact tracker '{profile.TrackerSerial}' is absent; no other tracker will be substituted.",
                cancellationToken).ConfigureAwait(false);
        }

        if (!tracker.IsConnected)
        {
            return await PublishNeutralHandAsync(
                profile,
                meta,
                tracker,
                InternalDriverNeutralReason.TrackerDisconnected,
                $"Exact tracker '{profile.TrackerSerial}' is disconnected; waiting for the same stable serial.",
                cancellationToken).ConfigureAwait(false);
        }

        if (!IsTrackerPublishable(tracker))
        {
            return await PublishNeutralHandAsync(
                profile,
                meta,
                tracker,
                InternalDriverNeutralReason.TrackerPoseInvalid,
                $"Exact tracker '{profile.TrackerSerial}' has no current fully tracked raw pose.",
                cancellationToken).ConfigureAwait(false);
        }

        if (meta.Readiness != MetaLinkReadiness.Ready ||
            meta.Controller is not { } controller ||
            !controller.Analog.IsValid)
        {
            return await PublishNeutralHandAsync(
                profile,
                meta,
                tracker,
                InternalDriverNeutralReason.MetaNotReady,
                meta.Diagnostic,
                cancellationToken).ConfigureAwait(false);
        }

        var input = _inputMapper.Map(controller);
        var state = InternalDriverHandStateComposer.Compose(
            profile.Hand,
            tracker,
            profile.TrackerFromController,
            input,
            inputsValid: true);
        await PublishWithReconnectMonitoringAsync(state, cancellationToken).ConfigureAwait(false);
        RecordTimestamp(profile.Hand, state.SampleMonotonicNanoseconds);
        return HandSnapshot(
            profile,
            meta,
            tracker,
            isPublishing: true,
            InternalDriverNeutralReason.None,
            $"Publishing from exact tracker '{profile.TrackerSerial}' and live {meta.Hand} Meta inputs.");
    }

    private async ValueTask<InternalDriverHandSnapshot> PublishNeutralHandAsync(
        InternalDriverHandProfile profile,
        MetaLinkHandSnapshot meta,
        PoseSourceSample? tracker,
        InternalDriverNeutralReason reason,
        string diagnostic,
        CancellationToken cancellationToken)
    {
        _inputMapper.Neutralize(ToMetaHand(profile.Hand));
        if (_feed is not null)
        {
            var state = DriverHandState.Neutral(
                profile.Hand,
                NextNeutralTimestamp(profile.Hand),
                connected: tracker?.IsConnected == true);
            var published = await TryPublishWithReconnectMonitoringAsync(
                state,
                _options.ShutdownOperationTimeout).ConfigureAwait(false);
            if (published)
            {
                RecordTimestamp(profile.Hand, state.SampleMonotonicNanoseconds);
            }
        }

        return HandSnapshot(profile, meta, tracker, isPublishing: false, reason, diagnostic);
    }

    private async ValueTask NeutralizeBothAsync(InternalDriverNeutralReason reason)
    {
        if (_profiles is null || _feed is null)
        {
            return;
        }

        _inputMapper.Reset();
        foreach (var profile in new[] { _profiles.Left, _profiles.Right })
        {
            try
            {
                var state = DriverHandState.Neutral(
                    profile.Hand,
                    NextNeutralTimestamp(profile.Hand),
                    connected: false);
                var published = await TryPublishWithReconnectMonitoringAsync(
                    state,
                    _options.ShutdownOperationTimeout).ConfigureAwait(false);
                if (published)
                {
                    RecordTimestamp(profile.Hand, state.SampleMonotonicNanoseconds);
                }
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException &&
                exception is not OutOfMemoryException)
            {
                PublishState(
                    InternalDriverSessionState.Reconnecting,
                    $"Best-effort {reason} neutralization could not reach the driver: {exception.Message}",
                    "The driver watchdog will neutralize stale state; restore the pipe before resuming.",
                    _lastObservation,
                    leftReason: reason,
                    rightReason: reason);
            }
        }
    }

    private async ValueTask RetireFeedAsync(bool bestEffortNeutralize)
    {
        var feed = _feed;
        if (feed is null)
        {
            return;
        }

        if (bestEffortNeutralize)
        {
            await NeutralizeBothAsync(
                InternalDriverNeutralReason.Stopping).ConfigureAwait(false);
        }

        try
        {
            var stopped = await TryBoundedFeedOperationAsync(
                token => feed.StopAsync(token),
                _options.ShutdownOperationTimeout).ConfigureAwait(false);
            if (!stopped)
            {
                PublishState(
                    InternalDriverSessionState.Reconnecting,
                    "Driver feed stop exceeded the bounded shutdown window; retirement is continuing.",
                    "The driver watchdog will neutralize the abandoned producer session.",
                    _lastObservation,
                    leftReason: InternalDriverNeutralReason.FeedUnavailable,
                    rightReason: InternalDriverNeutralReason.FeedUnavailable);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            PublishState(
                InternalDriverSessionState.Reconnecting,
                $"Driver feed retirement reported: {exception.Message}",
                "The driver watchdog will neutralize a retired or disconnected producer.",
                _lastObservation,
                leftReason: InternalDriverNeutralReason.FeedUnavailable,
                rightReason: InternalDriverNeutralReason.FeedUnavailable);
        }

        try
        {
            var disposed = await TryBoundedOperationAsync(
                () => feed.DisposeAsync(),
                _options.ShutdownOperationTimeout).ConfigureAwait(false);
            if (!disposed)
            {
                PublishState(
                    InternalDriverSessionState.Reconnecting,
                    "Driver feed disposal exceeded the bounded shutdown window; the session will not wait indefinitely.",
                    "Close the process if the abandoned transport does not retire under its watchdog.",
                    _lastObservation,
                    leftReason: InternalDriverNeutralReason.FeedUnavailable,
                    rightReason: InternalDriverNeutralReason.FeedUnavailable);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            PublishState(
                InternalDriverSessionState.Reconnecting,
                $"Driver feed disposal reported: {exception.Message}",
                "The retired session will not be reused.",
                _lastObservation,
                leftReason: InternalDriverNeutralReason.FeedUnavailable,
                rightReason: InternalDriverNeutralReason.FeedUnavailable);
        }

        if (ReferenceEquals(_feed, feed))
        {
            _feed = null;
        }
    }

    private async ValueTask CleanupRunAsync()
    {
        await RetireFeedAsync(bestEffortNeutralize: true).ConfigureAwait(false);
        try
        {
            var stopped = await TryBoundedFeedOperationAsync(
                token => _runtime.StopRunAsync(token),
                _options.ShutdownOperationTimeout).ConfigureAwait(false);
            if (!stopped)
            {
                PublishState(
                    InternalDriverSessionState.Faulted,
                    "Meta/OpenVR runtime disposal exceeded the bounded shutdown window.",
                    "Close the process before restarting SteamVR or Meta Link.",
                    _lastObservation,
                    leftReason: InternalDriverNeutralReason.Faulted,
                    rightReason: InternalDriverNeutralReason.Faulted);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            PublishState(
                InternalDriverSessionState.Faulted,
                $"Runtime resource disposal failed: {exception.Message}",
                "Close the process before restarting SteamVR or Meta Link.",
                _lastObservation,
                leftReason: InternalDriverNeutralReason.Faulted,
                rightReason: InternalDriverNeutralReason.Faulted);
        }
    }

    private async ValueTask PublishWithReconnectMonitoringAsync(
        DriverHandState state,
        CancellationToken cancellationToken)
    {
        var publicationGeneration = GetPublicationGeneration();
        var feed = _feed ?? throw new InvalidOperationException("The driver feed is unavailable.");
        var publishTask = feed.PublishAsync(state, cancellationToken).AsTask();
        try
        {
            while (!publishTask.IsCompleted)
            {
                var delayTask = _runtime.DelayAsync(_options.PollInterval, cancellationToken).AsTask();
                var completed = await Task.WhenAny(publishTask, delayTask).ConfigureAwait(false);
                if (completed == publishTask)
                {
                    break;
                }

                await delayTask.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var health = feed.Health;
                if (health.Readiness is DriverFeedReadiness.Reconnecting or DriverFeedReadiness.Faulted ||
                    health.IsStale)
                {
                    PublishState(
                        InternalDriverSessionState.Reconnecting,
                        health.LastError ?? "DriverFeed is reconnecting a pending publication.",
                        "Keep SteamVR running; the pending publication will use a fresh random session at sequence zero.",
                        _lastObservation,
                        leftReason: InternalDriverNeutralReason.FeedReconnecting,
                        rightReason: InternalDriverNeutralReason.FeedReconnecting,
                        expectedPublicationGeneration: publicationGeneration);
                }
            }

            await publishTask.ConfigureAwait(false);
        }
        catch
        {
            ObserveAbandonedTask(publishTask);
            throw;
        }
    }

    private async ValueTask<bool> TryPublishWithReconnectMonitoringAsync(
        DriverHandState state,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        Task task;
        try
        {
            task = PublishWithReconnectMonitoringAsync(state, cancellation.Token).AsTask();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }

        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ObserveAbandonedTask(task);
            return false;
        }
    }

    private async ValueTask StartFeedWithReconnectMonitoringAsync(
        IDriverFeed feed,
        InternalDriverRuntimeObservation observation,
        CancellationToken cancellationToken)
    {
        var publicationGeneration = GetPublicationGeneration();
        var startTask = feed.StartAsync(cancellationToken).AsTask();
        try
        {
            while (!startTask.IsCompleted)
            {
                var delayTask = _runtime.DelayAsync(_options.PollInterval, cancellationToken).AsTask();
                var completed = await Task.WhenAny(startTask, delayTask).ConfigureAwait(false);
                if (completed == startTask)
                {
                    break;
                }

                await delayTask.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var health = feed.Health;
                if (health.Readiness is DriverFeedReadiness.Reconnecting or DriverFeedReadiness.Faulted ||
                    health.IsStale)
                {
                    PublishState(
                        InternalDriverSessionState.Reconnecting,
                        health.LastError ?? "DriverFeed is reconnecting during feed startup.",
                        "Keep SteamVR running; feed startup will use a fresh random IPC session at sequence zero.",
                        observation,
                        leftReason: InternalDriverNeutralReason.FeedReconnecting,
                        rightReason: InternalDriverNeutralReason.FeedReconnecting,
                        expectedPublicationGeneration: publicationGeneration);
                }
            }

            await startTask.ConfigureAwait(false);
        }
        catch
        {
            ObserveAbandonedTask(startTask);
            throw;
        }
    }

    private static async ValueTask<bool> TryBoundedFeedOperationAsync(
        Func<CancellationToken, ValueTask> operation,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        Task task;
        try
        {
            task = operation(cancellation.Token).AsTask();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }

        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ObserveAbandonedTask(task);
            return false;
        }
    }

    private static async ValueTask<bool> TryBoundedOperationAsync(
        Func<ValueTask> operation,
        TimeSpan timeout)
    {
        Task task;
        try
        {
            task = operation().AsTask();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }

        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ObserveAbandonedTask(task);
            return false;
        }
    }

    private static void ObserveAbandonedTask(Task task) =>
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private void ReportCalibrationProgress(
        InternalDriverSessionState state,
        string diagnostic,
        string remediation,
        InternalDriverCaptureEvidence? leftCapture = null,
        InternalDriverCaptureEvidence? rightCapture = null,
        InternalDriverRuntimeObservation? observation = null)
    {
        if (state is < InternalDriverSessionState.Recording or > InternalDriverSessionState.SaveProfile)
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (leftCapture is not null)
        {
            _leftCapture = leftCapture;
        }

        if (rightCapture is not null)
        {
            _rightCapture = rightCapture;
        }

        if (observation is not null)
        {
            _lastObservation = observation;
        }

        PublishState(
            state,
            diagnostic,
            remediation,
            _lastObservation,
            leftReason: InternalDriverNeutralReason.ProfileUnavailable,
            rightReason: InternalDriverNeutralReason.ProfileUnavailable);
    }

    private void PublishState(
        InternalDriverSessionState state,
        string diagnostic,
        string remediation,
        InternalDriverRuntimeObservation? observation = null,
        bool restartRequired = false,
        InternalDriverNeutralReason leftReason = InternalDriverNeutralReason.ProfileUnavailable,
        InternalDriverNeutralReason rightReason = InternalDriverNeutralReason.ProfileUnavailable,
        long? expectedPublicationGeneration = null)
    {
        var isTerminal = state is InternalDriverSessionState.Stopped or
            InternalDriverSessionState.Faulted;
        PublishSnapshot(
            CreateStateSnapshot(
                state,
                diagnostic,
                remediation,
                observation,
                restartRequired,
                leftReason,
                rightReason,
                retainRunEvidence: !isTerminal),
            expectedPublicationGeneration);
    }

    private InternalDriverSessionSnapshot CreateStateSnapshot(
        InternalDriverSessionState state,
        string diagnostic,
        string remediation,
        InternalDriverRuntimeObservation? observation = null,
        bool restartRequired = false,
        InternalDriverNeutralReason leftReason = InternalDriverNeutralReason.ProfileUnavailable,
        InternalDriverNeutralReason rightReason = InternalDriverNeutralReason.ProfileUnavailable,
        bool retainRunEvidence = true)
    {
        var left = retainRunEvidence
            ? StateHandSnapshot(ProtocolHand.Left, observation, leftReason)
            : ClearedHandSnapshot(ProtocolHand.Left, leftReason);
        var right = retainRunEvidence
            ? StateHandSnapshot(ProtocolHand.Right, observation, rightReason)
            : ClearedHandSnapshot(ProtocolHand.Right, rightReason);
        var health = _feed?.Health;
        return new InternalDriverSessionSnapshot(
            state,
            retainRunEvidence
                ? BuildReadiness(
                    observation,
                    health?.Readiness == DriverFeedReadiness.Ready && health.Value.IsStale == false)
                : InternalDriverSessionReadiness.Empty,
            left,
            right,
            retainRunEvidence && health is { } present
                ? ToFeedSnapshot(present)
                : InternalDriverFeedSnapshot.Stopped,
            restartRequired,
            diagnostic,
            remediation)
        {
            Driver = retainRunEvidence ? DriverEvidence(observation) : null,
            LighthouseHmd = retainRunEvidence ? LighthouseHmdEvidence(observation) : null,
        };
    }

    private InternalDriverHandSnapshot StateHandSnapshot(
        ProtocolHand hand,
        InternalDriverRuntimeObservation? observation,
        InternalDriverNeutralReason reason)
    {
        var profile = hand == ProtocolHand.Left ? _profiles?.Left : _profiles?.Right;
        var meta = observation is null
            ? null
            : hand == ProtocolHand.Left
                ? observation.Meta.Left
                : observation.Meta.Right;
        PoseSourceSample? tracker = null;
        if (profile is not null &&
            observation?.TrackerSamples.TryGetValue(profile.TrackerSerial, out var sample) == true)
        {
            tracker = sample;
        }

        return new InternalDriverHandSnapshot(
            hand,
            profile?.TrackerSerial,
            tracker?.IsConnected == true,
            tracker is { } tracked && IsTrackerPublishable(tracked),
            meta?.Readiness ?? MetaLinkReadiness.RuntimeStopped,
            meta is { Readiness: MetaLinkReadiness.Ready, Controller: { } controller } &&
                controller.Analog.IsValid,
            profile?.Readiness ?? InternalDriverProfileReadiness.Missing,
            tracker is { } present ? PoseAge(present) : null,
            IsPublishing: false,
            reason,
            meta?.Diagnostic ?? "No active runtime observation.")
        {
            Calibration = profile?.Calibration,
            Capture = hand == ProtocolHand.Left ? _leftCapture : _rightCapture,
        };
    }

    private static InternalDriverHandSnapshot ClearedHandSnapshot(
        ProtocolHand hand,
        InternalDriverNeutralReason reason) => new(
        hand,
        TrackerSerial: null,
        TrackerConnected: false,
        TrackerTracked: false,
        MetaLinkReadiness.RuntimeStopped,
        MetaInputsValid: false,
        InternalDriverProfileReadiness.Missing,
        PoseAge: null,
        IsPublishing: false,
        reason,
        "No active runtime observation.");

    private InternalDriverSessionReadiness BuildReadiness(
        InternalDriverRuntimeObservation? observation,
        bool feedReady)
    {
        var platform = _platformProbe?.IsSupported == true;
        var driverRegistered = _registration?.IsRegistered == true;
        return new InternalDriverSessionReadiness(
            platform,
            observation?.SteamVrRunning == true,
            observation is not null && MetaBothReady(observation.Meta),
            observation is not null && RequiredTrackersReady(observation),
            _profiles?.IsValid == true,
            driverRegistered,
            observation is not null && LoadedReadiness(observation).IsReady,
            feedReady);
    }

    private InternalDriverHandSnapshot HandSnapshot(
        InternalDriverHandProfile profile,
        MetaLinkHandSnapshot meta,
        PoseSourceSample? tracker,
        bool isPublishing,
        InternalDriverNeutralReason reason,
        string diagnostic) => new(
        profile.Hand,
        profile.TrackerSerial,
        tracker?.IsConnected == true,
        tracker is { } value && IsTrackerPublishable(value),
        meta.Readiness,
        meta.Readiness == MetaLinkReadiness.Ready &&
            meta.Controller is { } controller &&
            controller.Analog.IsValid,
        profile.Readiness,
        tracker is { } sample ? PoseAge(sample) : null,
        isPublishing,
        reason,
        diagnostic)
    {
        Calibration = profile.Calibration,
        Capture = profile.Hand == ProtocolHand.Left ? _leftCapture : _rightCapture,
    };

    private TimeSpan? PoseAge(PoseSourceSample sample)
    {
        if (sample.SampleAgeSeconds is { } age && double.IsFinite(age) && age >= 0d)
        {
            return TimeSpan.FromSeconds(age);
        }

        var now = _runtime.GetMonotonicNanoseconds() / 1_000_000_000d;
        var elapsed = Math.Max(0d, now - sample.MonotonicHostTimeSeconds);
        return double.IsFinite(elapsed) ? TimeSpan.FromSeconds(elapsed) : null;
    }

    private InternalDriverFeedSnapshot ToFeedSnapshot(DriverFeedHealth health)
    {
        var hasSuccessfulTimestamp =
            health.LastSuccessfulSendNanoseconds is not null ||
            health.LastSuccessfulHeartbeatNanoseconds is not null;
        var now = hasSuccessfulTimestamp ? _runtime.GetMonotonicNanoseconds() : 0UL;

        return new InternalDriverFeedSnapshot(
            health.Readiness,
            health.SessionId,
            health.LastSuccessfulSequence,
            SuccessfulAge(now, health.LastSuccessfulSendNanoseconds),
            SuccessfulAge(now, health.LastSuccessfulHeartbeatNanoseconds),
            health.ConsecutiveReconnectAttempts,
            health.LastError);
    }

    private static TimeSpan? SuccessfulAge(ulong now, ulong? successfulTimestamp)
    {
        if (successfulTimestamp is not { } last)
        {
            return null;
        }

        var elapsedNanoseconds = now >= last ? now - last : 0UL;
        var elapsedTicks = Math.Min((ulong)long.MaxValue, elapsedNanoseconds / 100UL);
        return TimeSpan.FromTicks((long)elapsedTicks);
    }

    private void PublishSnapshot(
        InternalDriverSessionSnapshot snapshot,
        long? expectedPublicationGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_publicationSync)
        {
            if (_publicationFinalized ||
                expectedPublicationGeneration is { } expected && expected != _publicationGeneration)
            {
                return;
            }

            PublishSnapshotCore(snapshot);
        }
    }

    private void PublishFinalSnapshot(InternalDriverSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_publicationSync)
        {
            _publicationFinalized = true;
            PublishSnapshotCore(snapshot);
        }
    }

    private void PublishSnapshotCore(InternalDriverSessionSnapshot snapshot)
    {
        lock (_snapshotSync)
        {
            _snapshot = snapshot;
        }

        try
        {
            _output.Write(snapshot);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A diagnostic sink cannot own controller safety or session lifecycle.
        }

        var handlers = SnapshotChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<InternalDriverSessionSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, snapshot);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Presentation listeners are isolated from the production state machine.
            }
        }
    }

    private long GetPublicationGeneration()
    {
        lock (_publicationSync)
        {
            return _publicationGeneration;
        }
    }

    private InternalDriverLoadedReadinessResult LoadedReadiness(
        InternalDriverRuntimeObservation observation) =>
        InternalDriverLoadedReadiness.Evaluate(
            observation.Devices,
            _registration?.StagedBuildIdentity);

    private InternalDriverDriverEvidence? DriverEvidence(
        InternalDriverRuntimeObservation? observation)
    {
        if (_registration is not { IsRegistered: true } registration ||
            string.IsNullOrWhiteSpace(registration.StagedBuildIdentity))
        {
            return null;
        }

        if (observation is null || !LoadedReadiness(observation).IsReady)
        {
            return new InternalDriverDriverEvidence(registration.StagedBuildIdentity);
        }

        var left = LoadedControllerEvidence(
                observation,
                InternalDriverLoadedReadiness.LeftControllerSerial,
                SteamVrControllerRole.LeftHand,
                registration.StagedBuildIdentity)
            ?? throw new InvalidDataException(
                "Validated loaded topology did not retain exact left controller evidence.");
        var right = LoadedControllerEvidence(
                observation,
                InternalDriverLoadedReadiness.RightControllerSerial,
                SteamVrControllerRole.RightHand,
                registration.StagedBuildIdentity)
            ?? throw new InvalidDataException(
                "Validated loaded topology did not retain exact right controller evidence.");
        return new InternalDriverDriverEvidence(
            registration.StagedBuildIdentity,
            left,
            right);
    }

    private static InternalDriverLoadedControllerEvidence? LoadedControllerEvidence(
        InternalDriverRuntimeObservation observation,
        string serial,
        SteamVrControllerRole role,
        string stagedBuildIdentity)
    {
        var matches = observation.Devices.Where(device =>
            string.Equals(device.Identity.SerialNumber, serial, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 ||
            matches[0] is not { IsConnected: true, Category: SteamVrDeviceCategory.InputController } device ||
            device.ControllerRole != role ||
            device.Metadata is not { } metadata ||
            !string.Equals(metadata.DriverId, InternalDriverLoadedReadiness.DriverId, StringComparison.Ordinal) ||
            !string.Equals(
                metadata.TrackingSystemName,
                InternalDriverLoadedReadiness.TrackingSystemName,
                StringComparison.Ordinal) ||
            !string.Equals(
                metadata.ControllerType,
                InternalDriverLoadedReadiness.ControllerType,
                StringComparison.Ordinal) ||
            !string.Equals(
                metadata.InputProfilePath,
                InternalDriverLoadedReadiness.InputProfilePath,
                StringComparison.Ordinal) ||
            !string.Equals(metadata.DriverVersion, stagedBuildIdentity, StringComparison.Ordinal))
        {
            return null;
        }

        return new InternalDriverLoadedControllerEvidence(serial, metadata.DriverVersion!);
    }

    private static InternalDriverLighthouseHmdEvidence? LighthouseHmdEvidence(
        InternalDriverRuntimeObservation? observation)
    {
        if (observation is null || !ActiveHmdReadiness.Evaluate(observation.Devices).IsReady)
        {
            return null;
        }

        var hmds = observation.Devices
            .Where(device => device.Category == SteamVrDeviceCategory.HeadMountedDisplay)
            .ToArray();
        if (hmds.Length != 1 || hmds[0].Metadata is not { } metadata)
        {
            return null;
        }

        var hmd = hmds[0];
        return new InternalDriverLighthouseHmdEvidence(
            hmd.StableDeviceId,
            hmd.Identity.DevicePath,
            metadata.DriverId,
            metadata.TrackingSystemName,
            metadata.ActualTrackingSystemName,
            metadata.ManufacturerName,
            metadata.ModelNumber);
    }

    private static bool MetaBothReady(MetaLinkRuntimeSnapshot meta) =>
        MetaHandReady(meta.Left) && MetaHandReady(meta.Right);

    private static bool MetaHandReady(MetaLinkHandSnapshot hand) =>
        hand.Readiness == MetaLinkReadiness.Ready &&
        hand.Controller is { } controller &&
        controller.Analog.IsValid &&
        !controller.Battery.IsAvailable;

    private static bool AtLeastTwoReadyTrackerCandidates(
        InternalDriverRuntimeObservation observation) =>
        observation.TrackerSamples
            .Where(pair => IsTrackerPublishable(pair.Value))
            .Select(pair => pair.Key)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Count() == 2;

    private static bool ExactlyTwoReadyTrackerCandidates(
        InternalDriverRuntimeObservation observation) =>
        observation.TrackerSamples.Count == 2 &&
        observation.TrackerSamples.Values.All(IsTrackerPublishable);

    private bool RequiredTrackersReady(InternalDriverRuntimeObservation observation)
    {
        if (_profiles is not { IsValid: true } profiles)
        {
            return ExactlyTwoReadyTrackerCandidates(observation);
        }

        return observation.TrackerSamples.TryGetValue(
                   profiles.Left.TrackerSerial,
                   out var left) &&
               observation.TrackerSamples.TryGetValue(
                   profiles.Right.TrackerSerial,
                   out var right) &&
               IsTrackerPublishable(left) &&
               IsTrackerPublishable(right);
    }

    private static bool IsTrackerPublishable(PoseSourceSample sample) =>
        sample.IsConnected &&
        sample.PoseSample.HasValidOrientation &&
        sample.PoseSample.HasValidPosition &&
        sample.Validity.HasFlag(PoseValidity.TrackingValid) &&
        sample.TrackingResult == PoseTrackingResult.RunningOk;

    private static string MetaDiagnostic(MetaLinkRuntimeSnapshot meta) =>
        $"Meta Link is not ready for both hands: left={meta.Left.Readiness} ({meta.Left.Diagnostic}); " +
        $"right={meta.Right.Readiness} ({meta.Right.Diagnostic}).";

    private static string TrackerDiagnostic(InternalDriverRuntimeObservation observation) =>
        $"Expected at least two distinct connected, fully tracked raw tracker poses; observed " +
        $"{observation.TrackerSamples.Count} stable serial(s).";

    private static string ActiveDiagnostic(
        InternalDriverHandSnapshot left,
        InternalDriverHandSnapshot right)
    {
        if (left.IsPublishing && right.IsPublishing)
        {
            return "Publishing both first-party controller hands from Meta inputs and exact raw tracker poses.";
        }

        return $"Per-hand publication is active: left={(left.IsPublishing ? "publishing" : $"neutral ({left.NeutralReason})")}; " +
            $"right={(right.IsPublishing ? "publishing" : $"neutral ({right.NeutralReason})")}.";
    }

    private static InternalDriverNeutralReason WaitingNeutralReason(InternalDriverSessionState state) =>
        state switch
        {
            InternalDriverSessionState.WaitingForSteamVR => InternalDriverNeutralReason.SteamVrStopped,
            InternalDriverSessionState.WaitingForMetaLink => InternalDriverNeutralReason.MetaNotReady,
            InternalDriverSessionState.WaitingForTrackers => InternalDriverNeutralReason.TrackerMissing,
            InternalDriverSessionState.WaitingForDriver => InternalDriverNeutralReason.DriverNotReady,
            _ => InternalDriverNeutralReason.ProfileUnavailable,
        };

    private static void EnsureProfileTrackersWereObserved(
        InternalDriverRuntimeObservation observation,
        InternalDriverProfilePair profiles)
    {
        if (!observation.TrackerSamples.ContainsKey(profiles.Left.TrackerSerial) ||
            !observation.TrackerSamples.ContainsKey(profiles.Right.TrackerSerial))
        {
            throw new InvalidOperationException(
                "Profile resolution selected a controller-source tracker serial that was not observed.");
        }
    }

    private ulong NextNeutralTimestamp(ProtocolHand hand)
    {
        var now = Math.Max(1UL, _runtime.GetMonotonicNanoseconds());
        var previous = hand == ProtocolHand.Left ? _lastLeftTimestamp : _lastRightTimestamp;
        return previous == ulong.MaxValue ? previous : Math.Max(now, previous + 1UL);
    }

    private void RecordTimestamp(ProtocolHand hand, ulong timestamp)
    {
        if (hand == ProtocolHand.Left)
        {
            _lastLeftTimestamp = timestamp;
        }
        else if (hand == ProtocolHand.Right)
        {
            _lastRightTimestamp = timestamp;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(hand));
        }
    }

    private void ResetRunState()
    {
        lock (_publicationSync)
        {
            _publicationGeneration = unchecked(_publicationGeneration + 1);
            _publicationFinalized = false;
        }

        _registration = null;
        _platformProbe = null;
        _profiles = null;
        _lastObservation = null;
        _leftCapture = null;
        _rightCapture = null;
        _lastLeftTimestamp = 0;
        _lastRightTimestamp = 0;
        _inputMapper.Reset();
    }

    private void ClearRunEvidence()
    {
        _registration = null;
        _platformProbe = null;
        _profiles = null;
        _lastObservation = null;
        _leftCapture = null;
        _rightCapture = null;
        _lastLeftTimestamp = 0;
        _lastRightTimestamp = 0;
    }

    private static MetaLinkHand ToMetaHand(ProtocolHand hand) => hand switch
    {
        ProtocolHand.Left => MetaLinkHand.Left,
        ProtocolHand.Right => MetaLinkHand.Right,
        _ => throw new ArgumentOutOfRangeException(nameof(hand)),
    };
}
