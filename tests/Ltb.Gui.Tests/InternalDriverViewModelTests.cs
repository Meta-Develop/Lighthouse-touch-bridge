using Ltb.App;
using Ltb.Driver;
using Ltb.Gui;
using Ltb.Gui.Controls;
using Ltb.Gui.ViewModels;
using Ltb.MetaLink;
using Ltb.Protocol;

namespace Ltb.Gui.Tests;

public sealed class InternalDriverViewModelTests
{
    private const string BuildIdentity = "ltb-driver-test-build";

    [Fact]
    public async Task TypedSnapshotRendersExactDriverHmdCalibrationCaptureAndFeedEvidence()
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
        var leftCalibration = CalibrationEvidence(
            InternalDriverCalibrationMode.RotationOnly,
            "Rotation-only validation retained.",
            lagMilliseconds: -2.5d,
            rotationRmsDegrees: 1.25d,
            positionRmsMillimeters: null,
            translationConditionNumber: null,
            inlierRatio: 0.9d,
            createdUtc: new DateTimeOffset(2026, 7, 20, 10, 11, 12, TimeSpan.Zero));
        var rightCalibration = CalibrationEvidence(
            InternalDriverCalibrationMode.FullSixDof,
            "Translation validation passed.",
            lagMilliseconds: 3.5d,
            rotationRmsDegrees: 0.75d,
            positionRmsMillimeters: 4.5d,
            translationConditionNumber: 12d,
            inlierRatio: 0.95d,
            createdUtc: new DateTimeOffset(2026, 7, 20, 10, 12, 13, TimeSpan.Zero));
        session.Publish(Snapshot(
            InternalDriverSessionState.Recording,
            allReady: false,
            leftProfile: InternalDriverProfileReadiness.Reused,
            rightProfile: InternalDriverProfileReadiness.Calibrated,
            feedReadiness: DriverFeedReadiness.Reconnecting,
            reconnectAttempts: 3,
            feedError: "pipe unavailable",
            driverRegistered: true,
            driverLoaded: true,
            driver: LoadedDriverEvidence(),
            hmd: HmdEvidence(),
            leftCalibration: leftCalibration,
            rightCalibration: rightCalibration,
            leftCapture: CaptureEvidence(
                sampleCount: 40,
                rotationProgress: 0.4d,
                positionProgress: 0.2d),
            rightCapture: CaptureEvidence(
                sampleCount: 75,
                rotationProgress: 1d,
                positionProgress: 0.75d)));

        Assert.Equal(12, rows.Length);
        Assert.Equal(rows, viewModel.ReadinessRows);
        Assert.Equal(InternalDriverSessionState.Recording, viewModel.CurrentPhase);
        Assert.Equal("Recording", viewModel.PhaseText);
        Assert.Equal("LHR-LEFT", viewModel.LeftHand.TrackerSerial);
        Assert.Equal("LHR-RIGHT", viewModel.RightHand.TrackerSerial);
        Assert.Equal("Tracked", viewModel.LeftHand.TrackerStatus);
        Assert.Equal("Connected / not tracked", viewModel.RightHand.TrackerStatus);
        Assert.Equal("Publishing", viewModel.LeftHand.PublishingStatus);
        Assert.Equal("Neutral", viewModel.RightHand.PublishingStatus);
        Assert.Equal("None", viewModel.LeftHand.NeutralReason);
        Assert.Equal("Tracker Pose Invalid", viewModel.RightHand.NeutralReason);
        var loadedRow = Assert.Single(viewModel.ReadinessRows, row => row.Key == "loaded-driver");
        Assert.Equal("Ready", loadedRow.Status);
        Assert.Contains(BuildIdentity, loadedRow.Detail, StringComparison.Ordinal);
        Assert.Contains("LTB-TOUCH-LEFT", loadedRow.Detail, StringComparison.Ordinal);
        Assert.Contains("LTB-TOUCH-RIGHT", loadedRow.Detail, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(loadedRow.Detail, "runtime build: " + BuildIdentity));
        var registrationRow = Assert.Single(
            viewModel.ReadinessRows,
            row => row.Key == "driver-registration");
        Assert.Contains(BuildIdentity, registrationRow.Detail, StringComparison.Ordinal);
        var hmdRow = Assert.Single(viewModel.ReadinessRows, row => row.Key == "lighthouse-hmd");
        Assert.Equal("Ready", hmdRow.Status);
        Assert.Contains("hmd-stable-id", hmdRow.Detail, StringComparison.Ordinal);
        Assert.Contains("/devices/lighthouse/hmd", hmdRow.Detail, StringComparison.Ordinal);
        Assert.Contains("lighthouse", hmdRow.Detail, StringComparison.Ordinal);
        Assert.Contains("HTC", hmdRow.Detail, StringComparison.Ordinal);
        Assert.Contains("Vive Pro", hmdRow.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Bigscreen Beyond", hmdRow.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("device index", hmdRow.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Rotation only", viewModel.LeftHand.CalibrationMode);
        Assert.Equal("Rotation-only validation retained.", viewModel.LeftHand.CalibrationReason);
        Assert.Equal("-2.5 ms", viewModel.LeftHand.CalibrationLag);
        Assert.Contains("rotation RMS 1.25 deg", viewModel.LeftHand.CalibrationQuality, StringComparison.Ordinal);
        Assert.Contains("position RMS unavailable", viewModel.LeftHand.CalibrationQuality, StringComparison.Ordinal);
        Assert.Equal("2026-07-20 10:11:12 UTC", viewModel.LeftHand.CalibrationCreated);
        Assert.Equal("Full 6DoF", viewModel.RightHand.CalibrationMode);
        Assert.Contains("position RMS 4.50 mm", viewModel.RightHand.CalibrationQuality, StringComparison.Ordinal);
        Assert.Equal("40", viewModel.LeftHand.CaptureSamples);
        Assert.Contains("tracking 90.0%", viewModel.LeftHand.CaptureValidity, StringComparison.Ordinal);
        Assert.Contains("axis coverage 80.0%", viewModel.LeftHand.CaptureMotion, StringComparison.Ordinal);
        Assert.Equal(0.4d, viewModel.LeftHand.RotationProgress);
        Assert.Equal("40.0% - collecting", viewModel.LeftHand.RotationProgressStatus);
        Assert.Equal(0.2d, viewModel.LeftHand.PositionProgress);
        Assert.Equal(
            "20.0% observed - optional; rotation-only remains normal",
            viewModel.LeftHand.PositionProgressStatus);
        Assert.Equal(1d, viewModel.RightHand.RotationProgress);
        Assert.Equal("100.0% - ready", viewModel.RightHand.RotationProgressStatus);
        Assert.Equal(0.75d, viewModel.RightHand.PositionProgress);
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
    public async Task AbsentAndTerminalEvidenceRenderUnavailableAndClearPriorRunDetails()
    {
        var active = Snapshot(
            InternalDriverSessionState.Active,
            allReady: true,
            feedReadiness: DriverFeedReadiness.Ready,
            driver: LoadedDriverEvidence(),
            hmd: HmdEvidence(),
            leftCalibration: CalibrationEvidence(
                InternalDriverCalibrationMode.RotationOnly,
                "Retained.",
                0d,
                1d,
                null,
                null,
                0.9d,
                new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero)),
            leftCapture: CaptureEvidence(20, 1d, 0.5d));
        var session = new ControlledSession(active);
        await using var viewModel = NewViewModel(session);

        var run = viewModel.StartAsync();
        await session.Started;
        Assert.Equal("Rotation only", viewModel.LeftHand.CalibrationMode);
        Assert.Equal("20", viewModel.LeftHand.CaptureSamples);
        Assert.Equal("Ready", Assert.Single(
            viewModel.ReadinessRows,
            row => row.Key == "loaded-driver").Status);

        session.Publish(CreateStoppedSnapshot(active));

        Assert.Equal("Unavailable", viewModel.LeftHand.CalibrationMode);
        Assert.Equal("Unavailable", viewModel.LeftHand.CalibrationReason);
        Assert.Equal("Unavailable", viewModel.LeftHand.CalibrationLag);
        Assert.Equal("Unavailable", viewModel.LeftHand.CalibrationQuality);
        Assert.Equal("Unavailable", viewModel.LeftHand.CalibrationCreated);
        Assert.Equal("Unavailable", viewModel.LeftHand.CaptureSamples);
        Assert.Equal("Unavailable", viewModel.LeftHand.CaptureValidity);
        Assert.Equal("Unavailable", viewModel.LeftHand.CaptureMotion);
        Assert.Equal(0d, viewModel.LeftHand.RotationProgress);
        Assert.Equal("Unavailable", viewModel.LeftHand.RotationProgressStatus);
        Assert.Equal(0d, viewModel.LeftHand.PositionProgress);
        Assert.Equal("Unavailable", viewModel.LeftHand.PositionProgressStatus);
        var loaded = Assert.Single(viewModel.ReadinessRows, row => row.Key == "loaded-driver");
        Assert.Equal("Waiting", loaded.Status);
        Assert.Contains("No staged or loaded", loaded.Detail, StringComparison.Ordinal);
        var hmd = Assert.Single(viewModel.ReadinessRows, row => row.Key == "lighthouse-hmd");
        Assert.Equal("Waiting", hmd.Status);
        Assert.Contains("No validated Lighthouse HMD evidence", hmd.Detail, StringComparison.Ordinal);

        session.AllowStop();
        await viewModel.StopAsync();
        await run;
    }

    [Fact]
    public async Task EachHandRendersItsOwnIndependentCaptureProgress()
    {
        var session = new ControlledSession(Snapshot(
            InternalDriverSessionState.Recording,
            allReady: false,
            leftCapture: CaptureEvidence(10, 0.25d, 0.5d),
            rightCapture: CaptureEvidence(30, 0.75d, 1d)));
        await using var viewModel = NewViewModel(session);

        var run = viewModel.StartAsync();
        await session.Started;

        Assert.Equal("10", viewModel.LeftHand.CaptureSamples);
        Assert.Equal(0.25d, viewModel.LeftHand.RotationProgress);
        Assert.Equal(0.5d, viewModel.LeftHand.PositionProgress);
        Assert.Equal("30", viewModel.RightHand.CaptureSamples);
        Assert.Equal(0.75d, viewModel.RightHand.RotationProgress);
        Assert.Equal(1d, viewModel.RightHand.PositionProgress);
        Assert.Equal(
            "100.0% observed - available for optional translation evaluation",
            viewModel.RightHand.PositionProgressStatus);

        session.AllowStop();
        await viewModel.StopAsync();
        await run;
    }

    [Fact]
    public async Task HmdRowRequiresTypedHmdEvidenceInsteadOfGenericLoadedReadiness()
    {
        var session = new ControlledSession(Snapshot(
            InternalDriverSessionState.Active,
            allReady: true,
            feedReadiness: DriverFeedReadiness.Ready,
            driver: LoadedDriverEvidence(),
            hmd: null));
        await using var viewModel = NewViewModel(session);

        var run = viewModel.StartAsync();
        await session.Started;

        Assert.Equal("Ready", Assert.Single(
            viewModel.ReadinessRows,
            row => row.Key == "loaded-driver").Status);
        var hmd = Assert.Single(viewModel.ReadinessRows, row => row.Key == "lighthouse-hmd");
        Assert.Equal("Waiting", hmd.Status);
        Assert.Contains("No validated Lighthouse HMD evidence", hmd.Detail, StringComparison.Ordinal);

        session.AllowStop();
        await viewModel.StopAsync();
        await run;
    }

    [Fact]
    public async Task HmdRowRendersUnavailableDriverWithValidatedTrackingEvidence()
    {
        var session = new ControlledSession(Snapshot(
            InternalDriverSessionState.Active,
            allReady: true,
            feedReadiness: DriverFeedReadiness.Ready,
            driver: LoadedDriverEvidence(),
            hmd: new InternalDriverLighthouseHmdEvidence(
                "hmd-stable-id",
                "openvr://device/hmd-stable-id",
                driverId: null,
                trackingSystemName: "vendor_tracking",
                actualTrackingSystemName: "lighthouse",
                manufacturerName: "Bigscreen",
                modelNumber: "Beyond 2e")));
        await using var viewModel = NewViewModel(session);

        var run = viewModel.StartAsync();
        await session.Started;

        var hmd = Assert.Single(viewModel.ReadinessRows, row => row.Key == "lighthouse-hmd");
        Assert.Equal("Ready", hmd.Status);
        Assert.Contains("driver: unavailable", hmd.Detail, StringComparison.Ordinal);
        Assert.Contains("tracking system: vendor_tracking", hmd.Detail, StringComparison.Ordinal);
        Assert.Contains("actual tracking system: lighthouse", hmd.Detail, StringComparison.Ordinal);

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
        Assert.Equal(
            "Restart required",
            Assert.Single(
                viewModel.ReadinessRows,
                row => row.Key == "lighthouse-hmd").Status);
        Assert.Equal("Unavailable", viewModel.LeftHand.CalibrationMode);

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
    public async Task ConcurrentStopCloseAndDisposeShareOneFailSafeStopAndOneDisposal()
    {
        var session = new ControlledSession(Snapshot(
            InternalDriverSessionState.Reconnecting,
            allReady: false,
            feedReadiness: DriverFeedReadiness.Reconnecting));
        var viewModel = NewViewModel(session);

        var run = viewModel.StartAsync();
        await session.Started;
        var firstStop = viewModel.StopAsync();
        var repeatedStop = viewModel.StopAsync();
        var close = viewModel.CloseAsync();
        var dispose = viewModel.DisposeAsync().AsTask();

        Assert.Same(firstStop, repeatedStop);
        await session.StopEntered;
        await session.CancellationObserved;
        Assert.Equal(1, session.StopCallCount);
        Assert.False(firstStop.IsCompleted);

        session.AllowStop();
        await Task.WhenAll(firstStop, repeatedStop, close, dispose, run);

        Assert.Equal(1, session.StopCallCount);
        Assert.Equal(1, session.DisposeCallCount);
        Assert.False(viewModel.CanToggle);
        Assert.False(viewModel.IsRunning);
    }

    [Fact]
    public async Task FailSafeStopErrorIsSurfacedAfterRunDisposalCompletes()
    {
        var session = new ControlledSession(
            Snapshot(InternalDriverSessionState.Active, allReady: true),
            new InvalidOperationException("stop exploded"));
        await using var viewModel = NewViewModel(session);

        var run = viewModel.StartAsync();
        await session.Started;
        var stop = viewModel.StopAsync();
        await session.StopEntered;
        session.AllowStop();
        await stop;
        await run;

        Assert.Equal(1, session.StopCallCount);
        Assert.Equal(1, session.DisposeCallCount);
        Assert.Equal(
            "Internal-driver fail-safe stop failed: stop exploded",
            viewModel.LastError);
        Assert.Equal("Action required", viewModel.OverallStatus);
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
        Assert.Equal(
            [
                InternalDriverSessionIntent.NormalStart,
                InternalDriverSessionIntent.NormalStart,
            ],
            factory.Intents);
        Assert.True(first.DisposeCompleted);
        Assert.Equal(InternalDriverSessionState.Active, viewModel.CurrentPhase);
        Assert.True(viewModel.IsReady);

        second.AllowStop();
        await viewModel.StopAsync();
        await secondRun;
    }

    [Fact]
    public async Task CalibrationUsesExplicitIntentAndIsDisabledDuringTheSharedSessionLifecycle()
    {
        var session = new ControlledSession(Snapshot(
            InternalDriverSessionState.Recording,
            allReady: false));
        var factory = new QueueSessionFactory(session);
        await using var viewModel = new InternalDriverViewModel(factory, action => action());

        Assert.True(viewModel.CanCalibrate);
        Assert.True(viewModel.CalibrationCommand.CanExecute(null));

        var run = viewModel.CalibrateAsync();
        await session.Started;

        Assert.Equal([InternalDriverSessionIntent.Calibrate], factory.Intents);
        Assert.False(viewModel.CanCalibrate);
        Assert.False(viewModel.CalibrationCommand.CanExecute(null));
        Assert.False(viewModel.CanRemoveDriver);

        await viewModel.StartAsync();
        await viewModel.CalibrateAsync();
        Assert.Equal(1, factory.CreateCount);

        session.AllowStop();
        await viewModel.StopAsync();
        await run;

        Assert.True(viewModel.CanCalibrate);
        Assert.True(viewModel.CalibrationCommand.CanExecute(null));
    }

    [Fact]
    public void GuidedCalibrationInfersActiveHandAndKeepsProcessingStepperVisible()
    {
        var time = new ManualGuiTimeSource();
        var guide = new CalibrationGuideViewModel(time);
        var leftRecording = Snapshot(
            InternalDriverSessionState.Recording,
            allReady: false,
            leftCapture: CaptureEvidence(12, 0.25d, 0.5d));

        guide.Update(leftRecording);

        Assert.True(guide.IsVisible);
        Assert.Equal("Move the left hand only", guide.ActiveHandText);
        Assert.False(guide.IsRightHand);
        Assert.Equal(MotionGuideCue.Pitch, guide.Cue);
        Assert.Equal("Current", guide.Steps.Single(step => step.Title == "Left capture").Status);
        Assert.Contains("Actual analyzer", guide.EvidenceText, StringComparison.Ordinal);

        time.Advance(TimeSpan.FromSeconds(2));
        guide.Update(leftRecording);
        Assert.Equal(MotionGuideCue.Yaw, guide.Cue);

        var rightRecording = leftRecording with
        {
            Right = leftRecording.Right with
            {
                Capture = CaptureEvidence(3, 0.1d, 0.2d),
            },
        };
        guide.Update(rightRecording);
        Assert.Equal("Move the right hand only", guide.ActiveHandText);
        Assert.True(guide.IsRightHand);
        Assert.Equal(MotionGuideCue.Pitch, guide.Cue);
        Assert.Equal("Complete", guide.Steps.Single(step => step.Title == "Left capture").Status);
        Assert.Equal("Current", guide.Steps.Single(step => step.Title == "Right capture").Status);

        guide.Update(rightRecording with { State = InternalDriverSessionState.Association });
        Assert.True(guide.IsVisible);
        Assert.Equal(MotionGuideCue.Processing, guide.Cue);
        Assert.Equal("Current", guide.Steps.Single(step => step.Title == "Associate").Status);
        Assert.Contains("No user motion", guide.CueInstruction, StringComparison.Ordinal);

        guide.Update(rightRecording with { State = InternalDriverSessionState.SaveProfile });
        Assert.True(guide.IsVisible);
        Assert.Equal("Current", guide.Steps.Single(step => step.Title == "Save").Status);

        guide.Update(rightRecording with { State = InternalDriverSessionState.Active });
        Assert.False(guide.IsVisible);
    }

    [Fact]
    public void DebugHistoryIsOptInFixedSizeGapPreservingAndClearedAtBoundaries()
    {
        var time = new ManualGuiTimeSource();
        var diagnostics = new DebugDiagnosticsViewModel(time);
        var snapshot = Snapshot(
            InternalDriverSessionState.Active,
            allReady: true,
            leftCalibration: CalibrationEvidence(
                InternalDriverCalibrationMode.RotationOnly,
                "Retained.",
                -2.5d,
                1d,
                null,
                null,
                0.9d,
                DateTimeOffset.UnixEpoch));

        Assert.False(diagnostics.TrySample(snapshot, force: true));
        Assert.Equal(0, diagnostics.RetainedSampleCount);

        diagnostics.IsEnabled = true;
        Assert.True(diagnostics.TrySample(snapshot, force: true));
        time.Advance(DebugDiagnosticsViewModel.SampleInterval);
        Assert.True(diagnostics.TrySample(
            snapshot with { Left = snapshot.Left with { PoseAge = null } },
            force: true));
        Assert.Null(diagnostics.LeftTrackerAge[^1].Value);
        Assert.Contains("frozen", diagnostics.FrozenLagSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a live estimator", diagnostics.FrozenLagSummary, StringComparison.Ordinal);

        for (var index = 0; index < DebugDiagnosticsViewModel.MaximumSamples + 8; index++)
        {
            time.Advance(DebugDiagnosticsViewModel.SampleInterval);
            Assert.True(diagnostics.TrySample(snapshot, force: true));
        }

        Assert.Equal(DebugDiagnosticsViewModel.MaximumSamples, diagnostics.RetainedSampleCount);
        Assert.Equal(
            DebugDiagnosticsViewModel.MaximumSamples,
            diagnostics.RightTrackerAge.Count);
        Assert.Equal(
            DebugDiagnosticsViewModel.MaximumSamples,
            diagnostics.IterationInterval.Count);
        Assert.True(diagnostics.LeftTrackerAge[0].ElapsedSeconds > 0d);

        diagnostics.ResetForRun();
        Assert.Equal(0, diagnostics.RetainedSampleCount);
        diagnostics.TrySample(snapshot, force: true);
        Assert.Equal(1, diagnostics.RetainedSampleCount);

        diagnostics.IsEnabled = false;
        Assert.Equal(0, diagnostics.RetainedSampleCount);
        Assert.Empty(diagnostics.LeftFrozenLag);
        Assert.Empty(diagnostics.LeftTrackerHostIngressAge);
    }

    [Fact]
    public void TimingHistoryPreservesLowerBoundValuesNullGapsAndTenHertzLimit()
    {
        var time = new ManualGuiTimeSource();
        var diagnostics = new DebugDiagnosticsViewModel(time)
        {
            IsEnabled = true,
        };
        var timing = TimingSnapshot(
            iterationMilliseconds: 11d,
            observeMilliseconds: 2d,
            publicationMilliseconds: 3d,
            leftIngressMilliseconds: 4d,
            rightIngressMilliseconds: null);
        var snapshot = Snapshot(
            InternalDriverSessionState.Active,
            allReady: true,
            timing: timing);

        Assert.True(diagnostics.TrySample(snapshot, force: true));
        Assert.Equal(11d, diagnostics.IterationInterval[^1].Value);
        Assert.Equal(2d, diagnostics.ObserveDuration[^1].Value);
        Assert.Equal(3d, diagnostics.PairPublicationDuration[^1].Value);
        Assert.Equal(4d, diagnostics.LeftTrackerHostIngressAge[^1].Value);
        Assert.Null(diagnostics.RightTrackerHostIngressAge[^1].Value);

        time.Advance(TimeSpan.FromMilliseconds(99));
        Assert.False(diagnostics.TrySample(snapshot with
        {
            Timing = TimingSnapshot(12d, 5d, 6d, 7d, 8d),
        }));
        Assert.Single(diagnostics.IterationInterval);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(diagnostics.TrySample(snapshot with
        {
            Timing = TimingSnapshot(12d, 5d, 6d, 7d, 8d),
        }));
        Assert.Equal(12d, diagnostics.IterationInterval[^1].Value);
        Assert.Equal(8d, diagnostics.RightTrackerHostIngressAge[^1].Value);

        time.Advance(DebugDiagnosticsViewModel.SampleInterval);
        Assert.True(diagnostics.TrySample(snapshot with { Timing = null }));
        Assert.Null(diagnostics.IterationInterval[^1].Value);
        Assert.Null(diagnostics.ObserveDuration[^1].Value);
        Assert.Null(diagnostics.PairPublicationDuration[^1].Value);
        Assert.Null(diagnostics.LeftTrackerHostIngressAge[^1].Value);
        Assert.Null(diagnostics.RightTrackerHostIngressAge[^1].Value);

        diagnostics.ResetForRun();
        Assert.Empty(diagnostics.IterationInterval);
        Assert.Empty(diagnostics.ObserveDuration);
        Assert.Empty(diagnostics.PairPublicationDuration);
        Assert.Empty(diagnostics.LeftTrackerHostIngressAge);
        Assert.Empty(diagnostics.RightTrackerHostIngressAge);
    }

    [Fact]
    public void ActiveSnapshotPresentationIsCappedButMeaningfulChangesAreImmediate()
    {
        var time = new ManualGuiTimeSource();
        var scheduler = new ManualGuiDelayScheduler(time);
        var trailing = new List<(long Generation, long Sequence, InternalDriverSessionSnapshot Snapshot)>();
        using var coalescer = new SnapshotPresentationCoalescer(
            time,
            scheduler,
            (generation, sequence, snapshot) =>
                trailing.Add((generation, sequence, snapshot)));
        var active = Snapshot(
            InternalDriverSessionState.Active,
            allReady: true,
            feedReadiness: DriverFeedReadiness.Ready);
        coalescer.Reset(7, active);

        var fastTelemetryOnly = active with
        {
            Left = active.Left with { PoseAge = TimeSpan.FromMilliseconds(8) },
            Feed = active.Feed with
            {
                LastSuccessfulSequence = 43,
                LastSuccessfulSendAge = TimeSpan.FromMilliseconds(13),
            },
            Timing = TimingSnapshot(11d, 2d, 3d, 4d, 5d),
        };
        Assert.False(coalescer.ShouldPresent(7, 1, fastTelemetryOnly));
        Assert.Equal(1, scheduler.ActiveCount);
        Assert.Empty(trailing);

        time.Advance(SnapshotPresentationCoalescer.ActivePresentationInterval);
        scheduler.RunDue();
        var flush = Assert.Single(trailing);
        Assert.Equal(7, flush.Generation);
        Assert.Equal(1, flush.Sequence);
        Assert.Same(fastTelemetryOnly, flush.Snapshot);
        Assert.Equal(0, scheduler.ActiveCount);

        Assert.True(coalescer.ShouldPresent(
            7,
            2,
            fastTelemetryOnly with { Diagnostic = "A changed diagnostic." }));
        Assert.True(coalescer.ShouldPresent(
            7,
            3,
            fastTelemetryOnly with { Feed = fastTelemetryOnly.Feed with { LastError = "pipe" } }));
        Assert.True(coalescer.ShouldPresent(
            7,
            4,
            fastTelemetryOnly with { State = InternalDriverSessionState.Faulted }));
        Assert.True(coalescer.ShouldPresent(8, 5, fastTelemetryOnly));
    }

    [Fact]
    public void ActiveTrailingFlushKeepsLatestTelemetryAndCannotOutliveStopOrDispose()
    {
        var time = new ManualGuiTimeSource();
        var scheduler = new ManualGuiDelayScheduler(time);
        var trailing = new List<(long Sequence, InternalDriverSessionSnapshot Snapshot)>();
        var active = Snapshot(
            InternalDriverSessionState.Active,
            allReady: true,
            feedReadiness: DriverFeedReadiness.Ready);
        var coalescer = new SnapshotPresentationCoalescer(
            time,
            scheduler,
            (_, sequence, snapshot) => trailing.Add((sequence, snapshot)));
        coalescer.Reset(1, active);

        var first = active with
        {
            Feed = active.Feed with { LastSuccessfulSequence = 43 },
        };
        var latest = active with
        {
            Feed = active.Feed with { LastSuccessfulSequence = 44 },
            Timing = TimingSnapshot(12d, 3d, 4d, 5d, 6d),
        };
        Assert.False(coalescer.ShouldPresent(1, 10, first));
        time.Advance(TimeSpan.FromMilliseconds(40));
        Assert.False(coalescer.ShouldPresent(1, 11, latest));
        Assert.Equal(1, scheduler.ActiveCount);
        Assert.False(coalescer.ShouldPresent(
            0,
            12,
            active with { Diagnostic = "Stale generation diagnostic." }));
        Assert.Equal(1, scheduler.ActiveCount);

        time.Advance(TimeSpan.FromMilliseconds(60));
        scheduler.RunDue();
        var latestFlush = Assert.Single(trailing);
        Assert.Equal(11, latestFlush.Sequence);
        Assert.Same(latest, latestFlush.Snapshot);
        Assert.Equal(
            12d,
            latestFlush.Snapshot.Timing?.IterationInterval?.TotalMilliseconds);

        Assert.False(coalescer.ShouldPresent(1, 13, first));
        var canceledByStop = scheduler.SingleActive;
        Assert.True(coalescer.ShouldPresent(
            1,
            14,
            active with { State = InternalDriverSessionState.Stopped }));
        canceledByStop.InvokeEvenIfDisposed();
        Assert.Single(trailing);

        coalescer.Reset(2, active);
        Assert.False(coalescer.ShouldPresent(2, 15, latest));
        var canceledByDispose = scheduler.SingleActive;
        coalescer.Dispose();
        canceledByDispose.InvokeEvenIfDisposed();
        Assert.Single(trailing);
    }

    [Fact]
    public async Task ClosingCancelsTrailingFlushAndStillPresentsFinalStoppedSnapshot()
    {
        var time = new ManualGuiTimeSource();
        var scheduler = new ManualGuiDelayScheduler(time);
        var active = Snapshot(
            InternalDriverSessionState.Active,
            allReady: true,
            feedReadiness: DriverFeedReadiness.Ready);
        var session = new ControlledSession(active);
        await using var viewModel = new InternalDriverViewModel(
            new QueueSessionFactory(session),
            action => action(),
            timeSource: time,
            delayScheduler: scheduler);

        var run = viewModel.StartAsync();
        await session.Started;
        session.Publish(active with
        {
            Feed = active.Feed with { LastSuccessfulSequence = 43 },
        });
        Assert.Equal(1, scheduler.ActiveCount);

        var closing = viewModel.CloseAsync();
        await session.StopEntered;
        Assert.Equal(0, scheduler.ActiveCount);
        session.AllowStop();
        await closing;
        await run;
        scheduler.RunAllEvenIfDisposed();

        Assert.Equal(InternalDriverSessionState.Stopped, viewModel.CurrentPhase);
        Assert.False(viewModel.IsRunning);
    }

    [Fact]
    public void DiagnosticSeriesOffsetsKeepSimultaneousBinaryStatesInDistinctBands()
    {
        Assert.Equal(7d, DiagnosticPlotMath.ApplySeriesOffset(1d, 6d));
        Assert.Equal(5d, DiagnosticPlotMath.ApplySeriesOffset(1d, 4d));
        Assert.Equal(3d, DiagnosticPlotMath.ApplySeriesOffset(1d, 2d));
        Assert.Equal(1d, DiagnosticPlotMath.ApplySeriesOffset(1d, 0d));
        Assert.Equal(6d, DiagnosticPlotMath.ApplySeriesOffset(0d, 6d));
        Assert.Equal(4d, DiagnosticPlotMath.ApplySeriesOffset(0d, 4d));
    }

    [Theory]
    [InlineData(GuiEvidenceOrigin.LiveRuntime, "LIVE SESSION DATA")]
    [InlineData(GuiEvidenceOrigin.ScriptedSimulation, "SIMULATED DATA")]
    [InlineData(GuiEvidenceOrigin.OfflineReplay, "OFFLINE REPLAY")]
    public async Task EvidenceOriginIsExplicitlyLabeled(
        GuiEvidenceOrigin origin,
        string expectedLabel)
    {
        await using var viewModel = new InternalDriverViewModel(
            new QueueSessionFactory(),
            action => action(),
            timeSource: new ManualGuiTimeSource(),
            evidenceOrigin: origin);

        Assert.Equal(expectedLabel, viewModel.EvidenceOriginLabel);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.EvidenceOriginDetail));
    }

    private static InternalDriverViewModel NewViewModel(IInternalDriverSession session) =>
        new(new QueueSessionFactory(session), action => action());

    private static InternalDriverDriverEvidence LoadedDriverEvidence() => new(
        BuildIdentity,
        new InternalDriverLoadedControllerEvidence("LTB-TOUCH-LEFT", BuildIdentity),
        new InternalDriverLoadedControllerEvidence("LTB-TOUCH-RIGHT", BuildIdentity));

    private static InternalDriverLighthouseHmdEvidence HmdEvidence() => new(
        "hmd-stable-id",
        "/devices/lighthouse/hmd",
        "lighthouse",
        "lighthouse",
        "HTC",
        "Vive Pro");

    private static InternalDriverCalibrationEvidence CalibrationEvidence(
        InternalDriverCalibrationMode mode,
        string reason,
        double lagMilliseconds,
        double rotationRmsDegrees,
        double? positionRmsMillimeters,
        double? translationConditionNumber,
        double inlierRatio,
        DateTimeOffset createdUtc) => new(
            2,
            mode,
            reason,
            lagMilliseconds,
            new InternalDriverCalibrationQualityEvidence(
                rotationRmsDegrees,
                positionRmsMillimeters,
                translationConditionNumber,
                inlierRatio),
            createdUtc);

    private static InternalDriverCaptureEvidence CaptureEvidence(
        int sampleCount,
        double rotationProgress,
        double positionProgress) => new(
            sampleCount,
            trackingValidityFraction: 0.9d,
            orientationValidityFraction: 0.85d,
            positionValidityFraction: 0.75d,
            motionAxisCoverage: 0.8d,
            totalRotationDegrees: 180d,
            rotationProgress,
            positionProgress,
            rotationReady: rotationProgress == 1d,
            positionReady: positionProgress == 1d);

    private static InternalDriverTimingSnapshot TimingSnapshot(
        double? iterationMilliseconds,
        double observeMilliseconds,
        double publicationMilliseconds,
        double? leftIngressMilliseconds,
        double? rightIngressMilliseconds) => new(
            iterationMilliseconds is { } iteration
                ? TimeSpan.FromMilliseconds(iteration)
                : null,
            TimeSpan.FromMilliseconds(observeMilliseconds),
            TimeSpan.FromMilliseconds(publicationMilliseconds),
            leftIngressMilliseconds is { } leftIngress
                ? TimeSpan.FromMilliseconds(leftIngress)
                : null,
            rightIngressMilliseconds is { } rightIngress
                ? TimeSpan.FromMilliseconds(rightIngress)
                : null,
            observedTrackerCount: 2);

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }

        return count;
    }

    private static InternalDriverSessionSnapshot Snapshot(
        InternalDriverSessionState state,
        bool allReady,
        bool restartRequired = false,
        InternalDriverProfileReadiness leftProfile = InternalDriverProfileReadiness.Calibrated,
        InternalDriverProfileReadiness rightProfile = InternalDriverProfileReadiness.Calibrated,
        DriverFeedReadiness feedReadiness = DriverFeedReadiness.Stopped,
        int reconnectAttempts = 0,
        string? feedError = null,
        bool? driverRegistered = null,
        bool? driverLoaded = null,
        InternalDriverDriverEvidence? driver = null,
        InternalDriverLighthouseHmdEvidence? hmd = null,
        InternalDriverCalibrationEvidence? leftCalibration = null,
        InternalDriverCalibrationEvidence? rightCalibration = null,
        InternalDriverCaptureEvidence? leftCapture = null,
        InternalDriverCaptureEvidence? rightCapture = null,
        InternalDriverTimingSnapshot? timing = null)
    {
        var readiness = new InternalDriverSessionReadiness(
            PlatformSupported: allReady,
            SteamVrRunning: allReady,
            MetaBothHandsReady: allReady,
            TwoDistinctTrackersReady: allReady,
            ProfilesReady: allReady,
            DriverRegistered: driverRegistered ?? allReady,
            DriverLoaded: driverLoaded ?? allReady,
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
            "Left typed diagnostic.")
        {
            Calibration = leftCalibration,
            Capture = leftCapture,
        };
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
            "Right typed diagnostic.")
        {
            Calibration = rightCalibration,
            Capture = rightCapture,
        };
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
            "Typed remediation.")
        {
            Driver = driver,
            LighthouseHmd = hmd,
            Timing = timing,
        };
    }

    private sealed class QueueSessionFactory : IInternalDriverSessionFactory
    {
        private readonly Queue<IInternalDriverSession> _sessions;
        private readonly List<InternalDriverSessionIntent> _intents = [];

        public QueueSessionFactory(params IInternalDriverSession[] sessions)
        {
            _sessions = new Queue<IInternalDriverSession>(sessions);
        }

        public int CreateCount { get; private set; }

        public IReadOnlyList<InternalDriverSessionIntent> Intents => _intents;

        public IInternalDriverSession Create(InternalDriverSessionIntent intent)
        {
            CreateCount++;
            _intents.Add(intent);
            return _sessions.Dequeue();
        }
    }

    private sealed class ControlledSession : IInternalDriverSession
    {
        private readonly Exception? _stopException;
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

        public ControlledSession(
            InternalDriverSessionSnapshot snapshot,
            Exception? stopException = null)
        {
            CurrentSnapshot = snapshot;
            _stopException = stopException;
        }

        public event EventHandler<InternalDriverSessionSnapshot>? SnapshotChanged;

        public InternalDriverSessionSnapshot CurrentSnapshot { get; private set; }

        public Task Started => _started.Task;

        public Task StopEntered => _stopEntered.Task;

        public Task CancellationObserved => _cancellationObserved.Task;

        public int StopCallCount { get; private set; }

        public int DisposeCallCount { get; private set; }

        public bool DisposeCompleted => DisposeCallCount > 0;

        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(
                () => _cancellationObserved.TrySetResult());
            _started.TrySetResult();
            await _runExit.Task.ConfigureAwait(false);
        }

        public async ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            StopCallCount++;
            _stopEntered.TrySetResult();
            await _allowStop.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            CurrentSnapshot = CreateStoppedSnapshot(CurrentSnapshot);
            SnapshotChanged?.Invoke(this, CurrentSnapshot);
            _runExit.TrySetResult();
            if (_stopException is not null)
            {
                throw _stopException;
            }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
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
            Calibration = null,
            Capture = null,
        },
        Right = current.Right with
        {
            IsPublishing = false,
            NeutralReason = InternalDriverNeutralReason.SessionStopped,
            Calibration = null,
            Capture = null,
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
        Driver = null,
        LighthouseHmd = null,
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

    private sealed class ManualGuiTimeSource : IGuiTimeSource
    {
        private long _timestamp;

        public long GetTimestamp() => _timestamp;

        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
            TimeSpan.FromTicks(endingTimestamp - startingTimestamp);

        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }

    private sealed class ManualGuiDelayScheduler : IGuiDelayScheduler
    {
        private readonly ManualGuiTimeSource _timeSource;
        private readonly List<ScheduledCallback> _callbacks = [];

        public ManualGuiDelayScheduler(ManualGuiTimeSource timeSource)
        {
            _timeSource = timeSource;
        }

        public int ActiveCount => _callbacks.Count(callback => !callback.IsDisposed);

        public ScheduledCallback SingleActive =>
            Assert.Single(_callbacks.Where(callback => !callback.IsDisposed));

        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            var scheduled = new ScheduledCallback(
                _timeSource.GetTimestamp() + delay.Ticks,
                callback);
            _callbacks.Add(scheduled);
            return scheduled;
        }

        public void RunDue()
        {
            foreach (var callback in _callbacks
                         .Where(callback =>
                             !callback.IsDisposed &&
                             callback.DueTimestamp <= _timeSource.GetTimestamp())
                         .ToArray())
            {
                callback.Invoke();
            }
        }

        public void RunAllEvenIfDisposed()
        {
            foreach (var callback in _callbacks.ToArray())
            {
                callback.InvokeEvenIfDisposed();
            }
        }

        public sealed class ScheduledCallback : IDisposable
        {
            private readonly Action _callback;

            public ScheduledCallback(long dueTimestamp, Action callback)
            {
                DueTimestamp = dueTimestamp;
                _callback = callback;
            }

            public long DueTimestamp { get; }

            public bool IsDisposed { get; private set; }

            public void Dispose() => IsDisposed = true;

            public void Invoke()
            {
                if (IsDisposed)
                {
                    return;
                }

                IsDisposed = true;
                _callback();
            }

            public void InvokeEvenIfDisposed() => _callback();
        }
    }
}
