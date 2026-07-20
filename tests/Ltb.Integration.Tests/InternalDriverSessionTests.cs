using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Ltb.App;
using Ltb.Core;
using Ltb.Driver;
using Ltb.MetaLink;
using Ltb.OpenVr;
using Ltb.Protocol;

namespace Ltb.Integration.Tests;

public sealed class InternalDriverSessionTests
{
    private const string BuildIdentity = "driver_ltb-1.2.3-ipc-1.0";

    [Fact]
    public async Task RegistrationMutationRequestsOneRestartWhileUnchangedRegistrationDoesNot()
    {
        var changed = new FakeRuntime(ReadyObservation(), StoppedObservation())
        {
            Registration = Registration(changed: true),
        };
        var changedOutput = new RecordingOutput();
        await using (var session = Session(changed, changedOutput))
        {
            await session.RunAsync();
        }

        Assert.Contains(changedOutput.Snapshots, snapshot =>
            snapshot.State == InternalDriverSessionState.WaitingForSteamVR &&
            snapshot.RestartRequired);

        var unchanged = new FakeRuntime(ReadyObservation(), StoppedObservation())
        {
            Registration = Registration(changed: false),
        };
        var unchangedOutput = new RecordingOutput();
        await using (var session = Session(unchanged, unchangedOutput))
        {
            await session.RunAsync();
        }

        Assert.DoesNotContain(unchangedOutput.Snapshots, snapshot => snapshot.RestartRequired);
    }

    [Fact]
    public async Task CancellationTerminalPreservesDiagnosticRemediationAndPendingRestartFlag()
    {
        var runtime = new FakeRuntime(MetaLostObservation())
        {
            Registration = Registration(changed: true),
        };
        var output = new RecordingOutput();
        await using var session = Session(runtime, output);

        var run = session.RunAsync();
        await output.WaitForStateAsync(
            InternalDriverSessionState.WaitingForMetaLink,
            TimeSpan.FromSeconds(2));
        await session.StopAsync();
        await run;

        var terminal = output.Snapshots.Where(snapshot =>
            snapshot.State == InternalDriverSessionState.Stopped).ToArray();
        Assert.NotEmpty(terminal);
        Assert.All(terminal, snapshot =>
        {
            Assert.True(snapshot.RestartRequired);
            Assert.Contains("cancellation", snapshot.Diagnostic, StringComparison.Ordinal);
            Assert.Contains("Run the session again", snapshot.Remediation, StringComparison.Ordinal);
        });
        AssertAllTerminalSnapshotsCleared(output.Snapshots);
    }

    [Fact]
    public async Task ExactTwoTrackerTopologyGatesCalibrationAndPublication()
    {
        var runtime = new FakeRuntime(
            ReadyObservation(extraTracker: true),
            ReadyObservation(),
            ReadyObservation(sampleTimeSeconds: 11d),
            StoppedObservation());
        var output = new RecordingOutput();
        await using var session = Session(runtime, output);

        await session.RunAsync();

        Assert.Contains(output.Snapshots, snapshot =>
            snapshot.State == InternalDriverSessionState.WaitingForTrackers &&
            !snapshot.Readiness.TwoDistinctTrackersReady);
        Assert.Equal(1, runtime.ResolveProfilesCount);
        Assert.Contains(output.Snapshots, snapshot => snapshot.State == InternalDriverSessionState.Active);
    }

    [Fact]
    public async Task ExtraHmdWithValidControllersWithholdsExactLoadedBuildEvidence()
    {
        var runtime = new FakeRuntime(
            ReadyObservation(extraHmd: true),
            ReadyObservation(),
            ReadyObservation(sampleTimeSeconds: 11d),
            StoppedObservation());
        var output = new RecordingOutput();
        await using var session = Session(runtime, output);

        await session.RunAsync();

        var waiting = Assert.Single(output.Snapshots.Where(snapshot =>
            snapshot.State == InternalDriverSessionState.WaitingForDriver));
        Assert.False(waiting.Readiness.DriverLoaded);
        Assert.Equal(BuildIdentity, waiting.Driver!.StagedBuildIdentity);
        Assert.False(waiting.Driver.ExactLoadedBuildReady);
        Assert.Null(waiting.Driver.LeftController);
        Assert.Null(waiting.Driver.RightController);
        Assert.Null(waiting.LighthouseHmd);
    }

    [Fact]
    public async Task ReusedProfilesSkipCalibrationStatesAndCalibratedProfilesExposeEveryStage()
    {
        var reused = new FakeRuntime(
            ReadyObservation(),
            ReadyObservation(sampleTimeSeconds: 11d),
            StoppedObservation());
        var reusedOutput = new RecordingOutput();
        await using (var session = Session(reused, reusedOutput))
        {
            await session.RunAsync();
        }

        Assert.DoesNotContain(reusedOutput.Snapshots, snapshot =>
            snapshot.State is >= InternalDriverSessionState.Recording and
                <= InternalDriverSessionState.SaveProfile);
        Assert.Contains(reusedOutput.Snapshots, snapshot =>
            snapshot.State == InternalDriverSessionState.Active &&
            snapshot.Left.ProfileReadiness == InternalDriverProfileReadiness.Reused);
        var reusedActive = Assert.Single(reusedOutput.Snapshots.Where(snapshot =>
            snapshot.State == InternalDriverSessionState.Active));
        Assert.Equal(InternalDriverCalibrationMode.RotationOnly, reusedActive.Left.Calibration!.SelectedMode);
        Assert.Equal(InternalDriverCalibrationMode.FullSixDof, reusedActive.Right.Calibration!.SelectedMode);
        Assert.Contains("reused", reusedActive.Left.Calibration.SelectionReason, StringComparison.Ordinal);

        var calibrated = new FakeRuntime(
            ReadyObservation(),
            ReadyObservation(sampleTimeSeconds: 11d),
            StoppedObservation())
        {
            ResolveAsCalibrated = true,
        };
        var calibratedOutput = new RecordingOutput();
        await using (var session = Session(calibrated, calibratedOutput))
        {
            await session.RunAsync();
        }

        foreach (var state in new[]
                 {
                     InternalDriverSessionState.Recording,
                     InternalDriverSessionState.Association,
                     InternalDriverSessionState.TimeAlignment,
                     InternalDriverSessionState.RotationSolve,
                     InternalDriverSessionState.TranslationAttempt,
                     InternalDriverSessionState.Validation,
                     InternalDriverSessionState.SaveProfile,
                 })
        {
            Assert.Contains(calibratedOutput.Snapshots, snapshot => snapshot.State == state);
        }

        var calibratedActive = Assert.Single(calibratedOutput.Snapshots.Where(snapshot =>
            snapshot.State == InternalDriverSessionState.Active));
        Assert.Equal(InternalDriverProfileReadiness.Calibrated, calibratedActive.Left.ProfileReadiness);
        Assert.Contains("fresh", calibratedActive.Left.Calibration!.SelectionReason, StringComparison.Ordinal);
        Assert.Equal(1.25d, calibratedActive.Left.Calibration.Quality.RotationRmsDegrees);
        Assert.Equal(4.5d, calibratedActive.Right.Calibration!.Quality.PositionRmsMillimeters);
    }

    [Fact]
    public async Task FreshCaptureReportsRealPerHandEvidenceAndPreservesCompletedLeftWhileRightAdvances()
    {
        var runtime = new FakeRuntime(
            ReadyObservation(),
            ReadyObservation(sampleTimeSeconds: 11d),
            StoppedObservation())
        {
            ResolveAsCalibrated = true,
        };
        var output = new RecordingOutput();
        await using var session = Session(runtime, output);

        await session.RunAsync();

        var rightCapture = Assert.Single(output.Snapshots.Where(snapshot =>
            snapshot.State == InternalDriverSessionState.Recording &&
            snapshot.Left.Capture?.SampleCount == 48 &&
            snapshot.Right.Capture?.SampleCount == 13));
        Assert.True(rightCapture.Left.Capture!.RotationReady);
        Assert.False(rightCapture.Right.Capture!.RotationReady);
        Assert.Equal(0.82d, rightCapture.Left.Capture.MotionAxisCoverage);
        Assert.Equal(0.31d, rightCapture.Right.Capture.MotionAxisCoverage);
        Assert.False(rightCapture.Readiness.TwoDistinctTrackersReady);

        var active = Assert.Single(output.Snapshots.Where(snapshot =>
            snapshot.State == InternalDriverSessionState.Active));
        Assert.Equal(48, active.Left.Capture!.SampleCount);
        Assert.Equal(52, active.Right.Capture!.SampleCount);
        Assert.True(active.Left.Capture.RotationReady);
        Assert.True(active.Right.Capture.RotationReady);
    }

    [Fact]
    public void MappedCaptureDeduplicatesRepeatedOrRegressingRealMetaTimestamps()
    {
        var filter = new InternalDriverMappedMetaSampleFilter(MetaLinkHand.Left);

        Assert.True(filter.TryAppend(Controller(MetaLinkHand.Left, 10d, trigger: 0.1f)));
        Assert.False(filter.TryAppend(Controller(MetaLinkHand.Left, 10d, trigger: 0.2f)));
        Assert.False(filter.TryAppend(Controller(MetaLinkHand.Left, 9d, trigger: 0.3f)));
        Assert.True(filter.TryAppend(Controller(MetaLinkHand.Left, 10.01d, trigger: 0.4f)));

        Assert.Equal(2, filter.Samples.Count);
        Assert.Equal(10d, filter.Samples[0].Pose.RawMetaTimeSeconds);
        Assert.Equal(10.01d, filter.Samples[1].Pose.RawMetaTimeSeconds);
    }

    [Fact]
    public void CaptureCoverageRetainsUnavailableRuntimeTicksAsInvalidEvidence()
    {
        var tracker = new InternalDriverCaptureEvidenceTracker(MetaLinkHand.Left);
        var ready = ReadyMeta(10d);
        var dropout = new MetaLinkRuntimeSnapshot(
            sequence: 2,
            observedAtMonotonicSeconds: 10.01d,
            new MetaLinkHandSnapshot(
                MetaLinkHand.Left,
                MetaLinkReadiness.ControllersUnavailable,
                "left tracking unavailable"),
            ready.Right);

        Assert.True(tracker.TryAppend(ready));
        Assert.True(tracker.TryAppend(dropout));
        Assert.True(tracker.TryAppend(ReadyMeta(10.02d)));
        var evidence = tracker.Evaluate();
        Assert.Equal(3, evidence.SampleCount);
        Assert.Equal(2d / 3d, evidence.TrackingValidityFraction, 12);
        Assert.Equal(2d / 3d, evidence.OrientationValidityFraction, 12);
        Assert.Equal(2d / 3d, evidence.PositionValidityFraction, 12);
    }

    [Fact]
    public void CaptureReportCadenceBoundsPeriodicCallbacksAndRotationGateIsActionable()
    {
        var cadence = new InternalDriverCaptureReportCadence(
            TimeSpan.FromMilliseconds(250),
            startedNanoseconds: 1_000_000_000UL);
        var coverage = new InternalDriverCaptureEvidenceTracker(MetaLinkHand.Left);
        var callbacks = 1; // Initial capture-state publication.
        for (var milliseconds = 10; milliseconds <= 1_000; milliseconds += 10)
        {
            Assert.True(coverage.TryAppend(ReadyMeta(10d + (milliseconds / 1000d))));
            if (cadence.ShouldReport(1_000_000_000UL + ((ulong)milliseconds * 1_000_000UL)))
            {
                _ = coverage.Evaluate();
                callbacks++;
            }
        }

        _ = coverage.Evaluate();
        callbacks++; // CaptureHandAsync always emits one final evidence report.
        Assert.Equal(6, callbacks);
        Assert.Equal(5, coverage.EvaluationCount);

        var evidence = new InternalDriverCaptureEvidence(
            40,
            0.96d,
            0.5d,
            0.88d,
            0.01d,
            35d,
            0.42d,
            1d,
            rotationReady: false,
            positionReady: true);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionInternalDriverSessionRuntime.EnsureRotationReady(
                MetaLinkHand.Left,
                evidence));
        Assert.Contains("lacks rotation coverage", exception.Message, StringComparison.Ordinal);
        Assert.Contains("pitch, yaw, and roll", exception.Message, StringComparison.Ordinal);
        Assert.Contains("35.0 deg", exception.Message, StringComparison.Ordinal);

        ProductionInternalDriverSessionRuntime.EnsureRotationReady(
            MetaLinkHand.Right,
            new InternalDriverCaptureEvidence(
                48,
                0.96d,
                0.95d,
                0.4d,
                0.8d,
                220d,
                1d,
                0.5d,
                rotationReady: true,
                positionReady: false));
    }

    [Fact]
    public void PublicEvidenceDtosRejectInvalidOrIncoherentValues()
    {
        var left = new InternalDriverLoadedControllerEvidence(
            InternalDriverLoadedReadiness.LeftControllerSerial,
            BuildIdentity);
        var right = new InternalDriverLoadedControllerEvidence(
            InternalDriverLoadedReadiness.RightControllerSerial,
            BuildIdentity);
        var quality = new InternalDriverCalibrationQualityEvidence(1d, null, null, 0.9d);
        var created = new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);

        Assert.ThrowsAny<ArgumentException>(() =>
            new InternalDriverLoadedControllerEvidence(" ", BuildIdentity));
        Assert.ThrowsAny<ArgumentException>(() =>
            new InternalDriverLoadedControllerEvidence("LTB-TOUCH-LEFT", ""));
        Assert.ThrowsAny<ArgumentException>(() => new InternalDriverDriverEvidence(""));
        Assert.ThrowsAny<ArgumentException>(() =>
            new InternalDriverDriverEvidence(BuildIdentity, left, rightController: null));
        Assert.ThrowsAny<ArgumentException>(() =>
            new InternalDriverDriverEvidence(BuildIdentity, right, left));
        Assert.ThrowsAny<ArgumentException>(() =>
            new InternalDriverDriverEvidence(
                BuildIdentity,
                new InternalDriverLoadedControllerEvidence(
                    InternalDriverLoadedReadiness.LeftControllerSerial,
                    "other-build"),
                right));

        Assert.ThrowsAny<ArgumentException>(() => new InternalDriverLighthouseHmdEvidence(
            " ", "/devices/hmd", "lighthouse", null, null, null));
        Assert.ThrowsAny<ArgumentException>(() => new InternalDriverLighthouseHmdEvidence(
            "HMD", "", "lighthouse", null, null, null));
        Assert.ThrowsAny<ArgumentException>(() => new InternalDriverLighthouseHmdEvidence(
            "HMD", "/devices/hmd", "", null, null, null));
        Assert.ThrowsAny<ArgumentException>(() => new InternalDriverLighthouseHmdEvidence(
            "HMD", "/devices/hmd", "lighthouse", " ", null, null));

        Assert.ThrowsAny<ArgumentException>(() =>
            new InternalDriverCalibrationQualityEvidence(-1d, null, null, 0.9d));
        Assert.ThrowsAny<ArgumentException>(() =>
            new InternalDriverCalibrationQualityEvidence(1d, double.NaN, null, 0.9d));
        Assert.ThrowsAny<ArgumentException>(() =>
            new InternalDriverCalibrationQualityEvidence(1d, null, double.PositiveInfinity, 0.9d));
        Assert.ThrowsAny<ArgumentException>(() =>
            new InternalDriverCalibrationQualityEvidence(1d, null, null, 1.1d));
        Assert.ThrowsAny<ArgumentException>(() => new InternalDriverCalibrationEvidence(
            1, InternalDriverCalibrationMode.RotationOnly, "reason", 0d, quality, created));
        Assert.ThrowsAny<ArgumentException>(() => new InternalDriverCalibrationEvidence(
            2, (InternalDriverCalibrationMode)99, "reason", 0d, quality, created));
        Assert.ThrowsAny<ArgumentException>(() => new InternalDriverCalibrationEvidence(
            2, InternalDriverCalibrationMode.RotationOnly, " ", 0d, quality, created));
        Assert.ThrowsAny<ArgumentException>(() => new InternalDriverCalibrationEvidence(
            2, InternalDriverCalibrationMode.RotationOnly, "reason", double.NaN, quality, created));
        Assert.ThrowsAny<ArgumentException>(() => new InternalDriverCalibrationEvidence(
            2, InternalDriverCalibrationMode.RotationOnly, "reason", 0d, null!, created));
        Assert.ThrowsAny<ArgumentException>(() => new InternalDriverCalibrationEvidence(
            2, InternalDriverCalibrationMode.FullSixDof, "reason", 0d, quality, created));
        Assert.ThrowsAny<ArgumentException>(() => new InternalDriverCalibrationEvidence(
            2, InternalDriverCalibrationMode.RotationOnly, "reason", 0d, quality, default));

        Assert.ThrowsAny<ArgumentException>(() => CaptureEvidenceWith(sampleCount: -1));
        Assert.ThrowsAny<ArgumentException>(() => CaptureEvidenceWith(
            trackingValidityFraction: double.NaN));
        Assert.ThrowsAny<ArgumentException>(() => CaptureEvidenceWith(
            orientationValidityFraction: 1.1d));
        Assert.ThrowsAny<ArgumentException>(() => CaptureEvidenceWith(
            motionAxisCoverage: -0.1d));
        Assert.ThrowsAny<ArgumentException>(() => CaptureEvidenceWith(
            totalRotationDegrees: double.PositiveInfinity));
        Assert.ThrowsAny<ArgumentException>(() => CaptureEvidenceWith(
            rotationProgress: 1d,
            rotationReady: false));
        Assert.ThrowsAny<ArgumentException>(() => CaptureEvidenceWith(
            positionProgress: 0.5d,
            positionReady: true));
        Assert.ThrowsAny<ArgumentException>(() => CaptureEvidenceWith(
            sampleCount: 0,
            trackingValidityFraction: 0.1d,
            rotationProgress: 0d,
            positionProgress: 0d,
            positionReady: false));
    }

    [Fact]
    public async Task OneTrackerLossNeutralizesOnlyThatHandAndReacquiresExactSerial()
    {
        var first = ReadyObservation(sampleTimeSeconds: 10d);
        var lost = ReadyObservation(sampleTimeSeconds: 11d, omitTracker: "TRACKER-LEFT");
        var recovered = ReadyObservation(sampleTimeSeconds: 12d);
        var feed = new FakeFeed();
        var runtime = new FakeRuntime(first, lost, recovered, StoppedObservation())
        {
            Feeds = new Queue<FakeFeed>([feed]),
        };
        var output = new RecordingOutput();
        await using var session = Session(runtime, output);

        await session.RunAsync();

        var lostSnapshot = Assert.Single(output.Snapshots.Where(snapshot =>
            snapshot.State == InternalDriverSessionState.Active &&
            snapshot.Left.NeutralReason == InternalDriverNeutralReason.TrackerMissing));
        Assert.False(lostSnapshot.Left.IsPublishing);
        Assert.True(lostSnapshot.Right.IsPublishing);
        Assert.Equal("TRACKER-LEFT", lostSnapshot.Left.TrackerSerial);
        Assert.Contains(output.Snapshots, snapshot =>
            snapshot.State == InternalDriverSessionState.Active &&
            snapshot.Left.IsPublishing &&
            snapshot.Left.TrackerSerial == "TRACKER-LEFT");

        var afterLoss = feed.Published
            .Where(state => state.SampleMonotonicNanoseconds >= 11_000_000_000UL)
            .ToArray();
        Assert.Contains(afterLoss, state =>
            state.Hand == ProtocolHand.Left &&
            !state.Presence.HasFlag(ProtocolPresence.Tracked) &&
            state.Input == ProtocolInputState.Neutral);
        Assert.Contains(afterLoss, state =>
            state.Hand == ProtocolHand.Right &&
            state.Presence.HasFlag(ProtocolPresence.Tracked));
    }

    [Fact]
    public async Task UnexpectedThirdTrackerNeutralizesBothUntilExactTopologyRecovers()
    {
        var feed = new FakeFeed();
        var runtime = new FakeRuntime(
            ReadyObservation(sampleTimeSeconds: 10d),
            ReadyObservation(sampleTimeSeconds: 11d),
            ReadyObservation(sampleTimeSeconds: 12d, extraTracker: true),
            ReadyObservation(sampleTimeSeconds: 13d),
            StoppedObservation())
        {
            Feeds = new Queue<FakeFeed>([feed]),
        };
        var output = new RecordingOutput();
        await using var session = Session(runtime, output);

        await session.RunAsync();

        var snapshots = output.Snapshots.ToArray();
        var topologyIndex = Array.FindIndex(snapshots, snapshot =>
            snapshot.State == InternalDriverSessionState.WaitingForTrackers &&
            snapshot.Left.NeutralReason == InternalDriverNeutralReason.TrackerTopologyInvalid &&
            snapshot.Right.NeutralReason == InternalDriverNeutralReason.TrackerTopologyInvalid);
        Assert.True(topologyIndex > 0);
        var topologySnapshot = snapshots[topologyIndex];
        Assert.False(topologySnapshot.Readiness.TwoDistinctTrackersReady);
        Assert.False(topologySnapshot.Readiness.CanPublish);
        Assert.False(topologySnapshot.Left.IsPublishing);
        Assert.False(topologySnapshot.Right.IsPublishing);
        Assert.Contains(snapshots[..topologyIndex], snapshot =>
            snapshot.State == InternalDriverSessionState.Active &&
            snapshot.Left.IsPublishing &&
            snapshot.Right.IsPublishing);
        Assert.Contains(snapshots[(topologyIndex + 1)..], snapshot =>
            snapshot.State == InternalDriverSessionState.Active &&
            snapshot.Readiness.CanPublish &&
            snapshot.Left.IsPublishing &&
            snapshot.Right.IsPublishing);

        var published = feed.Published.ToArray();
        Assert.True(published.Length >= 6);
        Assert.All(published[..2], state =>
            Assert.True(state.Presence.HasFlag(ProtocolPresence.Tracked)));
        Assert.All(published[2..4], state =>
        {
            Assert.False(state.Presence.HasFlag(ProtocolPresence.Tracked));
            Assert.Equal(ProtocolInputState.Neutral, state.Input);
        });
        Assert.All(published[4..6], state =>
            Assert.True(state.Presence.HasFlag(ProtocolPresence.Tracked)));
    }

    [Fact]
    public async Task MetaLossNeutralizesBothResetsRuntimeAndStartsFreshFeedAfterBothHandsRecover()
    {
        var oldFeed = new FakeFeed(new ProtocolSessionId(1, 1));
        var freshFeed = new FakeFeed(new ProtocolSessionId(2, 2));
        var runtime = new FakeRuntime(
            ReadyObservation(sampleTimeSeconds: 10d),
            MetaLostObservation(),
            ReadyObservation(sampleTimeSeconds: 12d),
            StoppedObservation())
        {
            Feeds = new Queue<FakeFeed>([oldFeed, freshFeed]),
        };
        var output = new RecordingOutput();
        await using var session = Session(runtime, output);

        await session.RunAsync();

        Assert.Equal(1, runtime.ResetMetaCount);
        Assert.Equal(2, runtime.CreatedFeedCount);
        Assert.True(oldFeed.StopCount >= 1);
        Assert.Contains(output.Snapshots, snapshot =>
            snapshot.State == InternalDriverSessionState.WaitingForMetaLink &&
            snapshot.Left.NeutralReason == InternalDriverNeutralReason.MetaNotReady &&
            snapshot.Right.NeutralReason == InternalDriverNeutralReason.MetaNotReady);
        Assert.Contains(output.Snapshots, snapshot =>
            snapshot.State == InternalDriverSessionState.Active &&
            snapshot.Feed.SessionId == new ProtocolSessionId(2, 2));
    }

    [Fact]
    public async Task PendingReconnectPublicationSurfacesReconnectingBeforeItCompletes()
    {
        var feed = new FakeFeed { BlockNextPublish = true };
        var runtime = new FakeRuntime(
            ReadyObservation(sampleTimeSeconds: 10d),
            ReadyObservation(sampleTimeSeconds: 11d),
            StoppedObservation())
        {
            Feeds = new Queue<FakeFeed>([feed]),
        };
        var output = new RecordingOutput();
        await using var session = Session(runtime, output);

        var run = session.RunAsync();
        await feed.PublishBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await output.WaitForStateAsync(
            InternalDriverSessionState.Reconnecting,
            TimeSpan.FromSeconds(2));
        Assert.False(run.IsCompleted);

        feed.ReleaseBlockedPublish();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(output.Snapshots, snapshot =>
            snapshot.State == InternalDriverSessionState.Reconnecting &&
            snapshot.Feed.Readiness == DriverFeedReadiness.Reconnecting);
    }

    [Fact]
    public async Task ActiveSnapshotExposesExactSuccessfulHeartbeatAgeAndSequence()
    {
        var runtime = new FakeRuntime(
            ReadyObservation(sampleTimeSeconds: 10d),
            ReadyObservation(sampleTimeSeconds: 11d),
            StoppedObservation());
        var output = new RecordingOutput();
        await using var session = Session(runtime, output);

        await session.RunAsync();

        var active = Assert.Single(output.Snapshots.Where(snapshot =>
            snapshot.State == InternalDriverSessionState.Active));
        Assert.Equal(2UL, active.Feed.LastSuccessfulSequence);
        Assert.Equal(TimeSpan.FromMilliseconds(1), active.Feed.LastSuccessfulHeartbeatAge);
        Assert.Equal(BuildIdentity, active.Driver!.StagedBuildIdentity);
        Assert.True(active.Driver.ExactLoadedBuildReady);
        Assert.Equal(
            InternalDriverLoadedReadiness.LeftControllerSerial,
            active.Driver.LeftController!.SerialNumber);
        Assert.Equal(BuildIdentity, active.Driver.LeftController.RuntimeBuildIdentity);
        Assert.Equal(
            InternalDriverLoadedReadiness.RightControllerSerial,
            active.Driver.RightController!.SerialNumber);
        Assert.Equal(BuildIdentity, active.Driver.RightController.RuntimeBuildIdentity);
        Assert.Equal("HMD-LIGHTHOUSE", active.LighthouseHmd!.StableDeviceId);
        Assert.Equal("/devices/HMD-LIGHTHOUSE", active.LighthouseHmd.DevicePath);
        Assert.Equal("lighthouse", active.LighthouseHmd.DriverId);
        Assert.Equal("lighthouse", active.LighthouseHmd.TrackingSystemName);
        Assert.Equal("Bigscreen", active.LighthouseHmd.ManufacturerName);
        Assert.Equal("Beyond", active.LighthouseHmd.ModelNumber);

        var dependencyCheck = Assert.Single(output.Snapshots.Where(snapshot =>
            snapshot.State == InternalDriverSessionState.DependencyCheck));
        Assert.Null(dependencyCheck.Driver);
        Assert.Null(dependencyCheck.LighthouseHmd);
    }

    [Fact]
    public async Task RuntimeFaultAfterActiveRebuildsFinalSnapshotWithoutRetiredFeedEvidence()
    {
        var runtime = new FakeRuntime(
            ReadyObservation(sampleTimeSeconds: 10d),
            ReadyObservation(sampleTimeSeconds: 11d))
        {
            ObserveFailureAtCall = 3,
            ResolveAsCalibrated = true,
        };
        var output = new RecordingOutput();
        await using var session = Session(runtime, output);

        await session.RunAsync();

        Assert.Contains(output.Snapshots, snapshot =>
            snapshot.State == InternalDriverSessionState.Active &&
            snapshot.Readiness.CanPublish &&
            snapshot.Left.Calibration is not null &&
            snapshot.Left.Capture is not null &&
            snapshot.Driver is not null &&
            snapshot.LighthouseHmd is not null);
        var fault = session.CurrentSnapshot;
        Assert.Same(fault, output.Snapshots[^1]);
        Assert.Equal(InternalDriverSessionState.Faulted, fault.State);
        Assert.Contains("scripted runtime observation failure", fault.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(InternalDriverNeutralReason.Faulted, fault.Left.NeutralReason);
        Assert.Equal(InternalDriverNeutralReason.Faulted, fault.Right.NeutralReason);
        Assert.False(fault.Readiness.FeedReady);
        Assert.Equal(DriverFeedReadiness.Stopped, fault.Feed.Readiness);
        Assert.Null(fault.Feed.SessionId);
        Assert.Null(fault.Feed.LastSuccessfulSequence);
        Assert.Null(fault.Feed.LastSuccessfulSendAge);
        Assert.Null(fault.Feed.LastSuccessfulHeartbeatAge);
        Assert.Equal(0, fault.Feed.ReconnectAttempts);
        Assert.Null(fault.Feed.LastError);
        AssertFinalRuntimeEvidenceCleared(fault);
        AssertAllTerminalSnapshotsCleared(output.Snapshots);
    }

    [Fact]
    public async Task BlockingDisconnectedFeedCannotPreventStopRetirementOrRuntimeDisposal()
    {
        var feed = new FakeFeed();
        var runtime = new FakeRuntime(ReadyObservation())
        {
            Feeds = new Queue<FakeFeed>([feed]),
        };
        var output = new RecordingOutput();
        await using var session = Session(
            runtime,
            output,
            new InternalDriverSessionOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(1),
                ShutdownOperationTimeout = TimeSpan.FromMilliseconds(20),
            });
        var run = session.RunAsync();
        await output.WaitForStateAsync(InternalDriverSessionState.Active, TimeSpan.FromSeconds(2));
        feed.BlockNeutralAndRetirement = true;

        var stopwatch = Stopwatch.StartNew();
        await session.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), stopwatch.Elapsed.ToString());
        Assert.Equal(1, runtime.StopRunCount);
        Assert.Equal(InternalDriverSessionState.Stopped, session.CurrentSnapshot.State);
        AssertFinalRuntimeEvidenceCleared(session.CurrentSnapshot);
        AssertAllTerminalSnapshotsCleared(output.Snapshots);
        Assert.True(run.IsCompleted);
        Assert.Contains(output.Snapshots, snapshot =>
            snapshot.State == InternalDriverSessionState.Reconnecting &&
            snapshot.Feed.LastError == "test neutral pipe disconnected");
    }

    [Fact]
    public async Task SteamVrExitNeutralizesAndStopsWithoutReopeningCurrentRun()
    {
        var runtime = new FakeRuntime(
            ReadyObservation(sampleTimeSeconds: 10d),
            StoppedObservation());
        var output = new RecordingOutput();
        await using var session = Session(runtime, output);

        await session.RunAsync();

        Assert.Equal(2, runtime.ObserveCount);
        Assert.Equal(1, runtime.StopRunCount);
        Assert.Contains(output.Snapshots, snapshot =>
            snapshot.Diagnostic.Contains("SteamVR", StringComparison.Ordinal) &&
            snapshot.Left.NeutralReason == InternalDriverNeutralReason.SteamVrStopped);
        Assert.Equal(InternalDriverSessionState.Stopped, session.CurrentSnapshot.State);
        AssertAllTerminalSnapshotsCleared(output.Snapshots);
    }

    [Fact]
    public async Task JsonLinesNeverPersistsStaleTerminalSessionEvidence()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ltb-terminal-json-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "session.jsonl");
        try
        {
            var runtime = new FakeRuntime(
                ReadyObservation(sampleTimeSeconds: 10d),
                ReadyObservation(sampleTimeSeconds: 11d))
            {
                ObserveFailureAtCall = 3,
                ResolveAsCalibrated = true,
            };
            await using (var session = new InternalDriverSession(
                runtime,
                new InternalDriverSessionOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(1),
                    ShutdownOperationTimeout = TimeSpan.FromMilliseconds(50),
                },
                new JsonLinesInternalDriverSessionOutput(path)))
            {
                await session.RunAsync();
            }

            var terminalCount = 0;
            foreach (var line in File.ReadLines(path))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var state = root.GetProperty("state").GetString();
                if (state is not ("Stopped" or "Faulted"))
                {
                    continue;
                }

                terminalCount++;
                Assert.False(root.GetProperty("readiness").GetProperty("can_publish").GetBoolean());
                Assert.Equal(JsonValueKind.Null, root.GetProperty("driver").ValueKind);
                Assert.Equal(JsonValueKind.Null, root.GetProperty("lighthouse_hmd").ValueKind);
                Assert.Equal(
                    "Stopped",
                    root.GetProperty("feed").GetProperty("readiness").GetString());
                foreach (var handName in new[] { "left", "right" })
                {
                    var hand = root.GetProperty(handName);
                    Assert.Equal(JsonValueKind.Null, hand.GetProperty("tracker_serial").ValueKind);
                    Assert.Equal("Missing", hand.GetProperty("profile_readiness").GetString());
                    Assert.Equal(JsonValueKind.Null, hand.GetProperty("calibration").ValueKind);
                    Assert.Equal(JsonValueKind.Null, hand.GetProperty("capture").ValueKind);
                }
            }

            Assert.True(terminalCount >= 1, "No terminal JSONL record was emitted.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StopAndDisposalAreIdempotentAndSequentialRunsCreateNewFeeds()
    {
        var first = new FakeFeed(new ProtocolSessionId(11, 1));
        var second = new FakeFeed(new ProtocolSessionId(22, 2));
        var runtime = new FakeRuntime(
            ReadyObservation(),
            StoppedObservation(),
            ReadyObservation(sampleTimeSeconds: 20d),
            StoppedObservation())
        {
            Feeds = new Queue<FakeFeed>([first, second]),
        };
        var output = new RecordingOutput();
        var session = Session(runtime, output);

        await session.RunAsync();
        await session.RunAsync();
        await session.StopAsync();
        await session.StopAsync();
        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(2, runtime.CreatedFeedCount);
        Assert.Equal(2, runtime.StopRunCount);
        Assert.Equal(1, runtime.DisposeCount);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public void FactoryDefaultsUseLocalApplicationDataAndStagedBaseDirectoryWithoutManualSteamVrPath()
    {
        var localRoot = Path.Combine(Path.GetTempPath(), "ltb-session-paths", Guid.NewGuid().ToString("N"));
        var paths = InternalDriverSessionFactory.ResolvePaths(new InternalDriverSessionOptions
        {
            LocalApplicationDataRoot = localRoot,
        });

        Assert.StartsWith(Path.GetFullPath(localRoot), paths.SettingsPath, StringComparison.Ordinal);
        Assert.StartsWith(Path.GetFullPath(localRoot), paths.CalibrationProfileStorePath, StringComparison.Ordinal);
        Assert.StartsWith(Path.GetFullPath(localRoot), paths.StructuredLogPath, StringComparison.Ordinal);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "driver_ltb"))),
            paths.StagedDriverRoot);
        Assert.DoesNotContain("steamvr.vrsettings", paths.SettingsPath, StringComparison.OrdinalIgnoreCase);
    }

    private static InternalDriverSession Session(
        FakeRuntime runtime,
        RecordingOutput output,
        InternalDriverSessionOptions? options = null) =>
        new(runtime, options ?? new InternalDriverSessionOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(1),
            ShutdownOperationTimeout = TimeSpan.FromMilliseconds(50),
        }, output);

    private static void AssertFinalRuntimeEvidenceCleared(InternalDriverSessionSnapshot snapshot)
    {
        Assert.Equal(InternalDriverSessionReadiness.Empty, snapshot.Readiness);
        Assert.Null(snapshot.Driver);
        Assert.Null(snapshot.LighthouseHmd);
        Assert.Equal(DriverFeedReadiness.Stopped, snapshot.Feed.Readiness);
        Assert.Null(snapshot.Feed.SessionId);
        Assert.Null(snapshot.Feed.LastSuccessfulSequence);
        Assert.Null(snapshot.Feed.LastSuccessfulSendAge);
        Assert.Null(snapshot.Feed.LastSuccessfulHeartbeatAge);
        Assert.Equal(0, snapshot.Feed.ReconnectAttempts);
        Assert.Null(snapshot.Feed.LastError);
        foreach (var hand in new[] { snapshot.Left, snapshot.Right })
        {
            Assert.Null(hand.TrackerSerial);
            Assert.False(hand.TrackerConnected);
            Assert.False(hand.TrackerTracked);
            Assert.Equal(MetaLinkReadiness.RuntimeStopped, hand.MetaReadiness);
            Assert.False(hand.MetaInputsValid);
            Assert.Equal(InternalDriverProfileReadiness.Missing, hand.ProfileReadiness);
            Assert.Null(hand.PoseAge);
            Assert.False(hand.IsPublishing);
            Assert.Null(hand.Calibration);
            Assert.Null(hand.Capture);
        }
    }

    private static void AssertAllTerminalSnapshotsCleared(
        IEnumerable<InternalDriverSessionSnapshot> snapshots)
    {
        var terminal = snapshots.Where(snapshot => snapshot.State is
            InternalDriverSessionState.Stopped or InternalDriverSessionState.Faulted).ToArray();
        Assert.NotEmpty(terminal);
        Assert.All(terminal, AssertFinalRuntimeEvidenceCleared);
    }

    private static InternalDriverCaptureEvidence CaptureEvidenceWith(
        int sampleCount = 1,
        double trackingValidityFraction = 1d,
        double orientationValidityFraction = 1d,
        double positionValidityFraction = 1d,
        double motionAxisCoverage = 0d,
        double totalRotationDegrees = 0d,
        double rotationProgress = 0d,
        double positionProgress = 1d,
        bool rotationReady = false,
        bool positionReady = true) => new(
        sampleCount,
        trackingValidityFraction,
        orientationValidityFraction,
        positionValidityFraction,
        motionAxisCoverage,
        totalRotationDegrees,
        rotationProgress,
        positionProgress,
        rotationReady,
        positionReady);

    private static InternalDriverCalibrationEvidence CalibrationEvidence(
        ProtocolHand hand,
        bool calibrated)
    {
        var fullSixDof = hand == ProtocolHand.Right;
        return new InternalDriverCalibrationEvidence(
            2,
            fullSixDof
                ? InternalDriverCalibrationMode.FullSixDof
                : InternalDriverCalibrationMode.RotationOnly,
            $"{(calibrated ? "fresh" : "reused")} {hand} profile evidence",
            fullSixDof ? 12.5d : 9.75d,
            new InternalDriverCalibrationQualityEvidence(
                1.25d,
                fullSixDof ? 4.5d : null,
                fullSixDof ? 17d : null,
                fullSixDof ? 0.93d : 0.91d),
            new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero));
    }

    private static InternalDriverCaptureEvidence CaptureEvidence(
        int sampleCount,
        double motionAxisCoverage,
        bool rotationReady) => new(
        sampleCount,
        0.96d,
        0.95d,
        0.88d,
        motionAxisCoverage,
        rotationReady ? 220d : 70d,
        rotationReady ? 1d : 0.42d,
        1d,
        rotationReady,
        true);

    private static InternalDriverRegistration Registration(bool changed) => new(
        IsRegistered: true,
        Changed: changed,
        RestartRequired: changed,
        BuildIdentity,
        changed
            ? "driver registration changed; restart SteamVR"
            : "driver registration is unchanged");

    private static InternalDriverRuntimeObservation ReadyObservation(
        double sampleTimeSeconds = 10d,
        bool extraTracker = false,
        string? omitTracker = null,
        bool extraHmd = false)
    {
        var trackers = new Dictionary<string, PoseSourceSample>(StringComparer.Ordinal);
        AddTracker("TRACKER-LEFT", sampleTimeSeconds, Vector3.UnitX);
        AddTracker("TRACKER-RIGHT", sampleTimeSeconds + 0.001d, Vector3.UnitY);
        if (extraTracker)
        {
            AddTracker("TRACKER-EXTRA", sampleTimeSeconds + 0.002d, Vector3.UnitZ);
        }

        return new InternalDriverRuntimeObservation(
            SteamVrRunning: true,
            "SteamVR runtime is running.",
            ReadyMeta(sampleTimeSeconds),
            ReadyDevices(extraTracker, extraHmd),
            trackers);

        void AddTracker(string serial, double time, Vector3 position)
        {
            if (!string.Equals(serial, omitTracker, StringComparison.Ordinal))
            {
                trackers.Add(serial, TrackerSample(time, position));
            }
        }
    }

    private static InternalDriverRuntimeObservation MetaLostObservation() => new(
        SteamVrRunning: true,
        "SteamVR runtime is running.",
        new MetaLinkRuntimeSnapshot(
            99,
            11d,
            new MetaLinkHandSnapshot(
                MetaLinkHand.Left,
                MetaLinkReadiness.HeadsetDisconnected,
                "left unavailable"),
            new MetaLinkHandSnapshot(
                MetaLinkHand.Right,
                MetaLinkReadiness.HeadsetDisconnected,
                "right unavailable")),
        ReadyDevices(),
        new Dictionary<string, PoseSourceSample>(StringComparer.Ordinal)
        {
            ["TRACKER-LEFT"] = TrackerSample(11d, Vector3.UnitX),
            ["TRACKER-RIGHT"] = TrackerSample(11.001d, Vector3.UnitY),
        });

    private static InternalDriverRuntimeObservation StoppedObservation() => new(
        SteamVrRunning: false,
        "SteamVR stopped and requested shutdown.",
        ReadyMeta(20d),
        [],
        new Dictionary<string, PoseSourceSample>(StringComparer.Ordinal));

    private static MetaLinkRuntimeSnapshot ReadyMeta(double time)
    {
        var left = Controller(MetaLinkHand.Left, time, trigger: 0.7f);
        var right = Controller(MetaLinkHand.Right, time + 0.001d, trigger: 0.2f);
        return new MetaLinkRuntimeSnapshot(
            (long)(time * 1000d),
            time,
            new MetaLinkHandSnapshot(MetaLinkHand.Left, MetaLinkReadiness.Ready, "left ready", left),
            new MetaLinkHandSnapshot(MetaLinkHand.Right, MetaLinkReadiness.Ready, "right ready", right));
    }

    private static MetaLinkControllerSnapshot Controller(
        MetaLinkHand hand,
        double time,
        float trigger) => new(
        hand,
        new MetaLinkPoseSnapshot(
            RigidTransform.Identity,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            isOrientationTracked: true,
            isPositionTracked: true,
            hasValidOrientation: true,
            hasValidPosition: true,
            rawMetaTimeSeconds: time,
            appMonotonicTimeSeconds: time,
            appMonotonicTimeNanoseconds: (long)(time * 1_000_000_000d),
            clockUncertaintySeconds: 0.0001d),
        hand == MetaLinkHand.Left
            ? new MetaLinkButtons(false, false, true, false, false, true, 0)
            : new MetaLinkButtons(true, false, false, false, false, false, 0),
        new MetaLinkTouches(
            A: hand == MetaLinkHand.Right,
            B: false,
            X: hand == MetaLinkHand.Left,
            Y: false,
            Thumbstick: true,
            ThumbRest: true,
            IndexTrigger: true,
            IndexPointing: false,
            ThumbUp: false,
            RawMask: 0),
        new MetaLinkAnalogState(trigger, 0.3f, new Vector2(0.2f, -0.4f)),
        MetaLinkBatteryState.Unavailable);

    private static PoseSourceSample TrackerSample(double time, Vector3 position) => new(
        new TimestampedPoseSample(
            time,
            new RigidTransform(Quaternion.Identity, position),
            PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid),
        isConnected: true,
        PoseTrackingResult.RunningOk,
        runtimeTimeSeconds: time,
        predictionOffsetSeconds: 0d,
        sampleAgeSeconds: 0.002d,
        linearVelocityMetersPerSecond: new Vector3(1f, 2f, 3f),
        angularVelocityRadiansPerSecond: new Vector3(0f, 1f, 0f));

    private static IReadOnlyList<SteamVrDeviceDescriptor> ReadyDevices(
        bool extraTracker = false,
        bool extraHmd = false)
    {
        var devices = new List<SteamVrDeviceDescriptor>
        {
            Descriptor(
                "HMD-LIGHTHOUSE",
                0,
                SteamVrDeviceCategory.HeadMountedDisplay,
                SteamVrControllerRole.None,
                new SteamVrDeviceMetadata(
                    "lighthouse",
                    "lighthouse",
                    "Bigscreen",
                    "Beyond",
                    null,
                    null,
                    "hmd-build")),
            LtbController(
                InternalDriverLoadedReadiness.LeftControllerSerial,
                1,
                SteamVrControllerRole.LeftHand),
            LtbController(
                InternalDriverLoadedReadiness.RightControllerSerial,
                2,
                SteamVrControllerRole.RightHand),
            TrackerDescriptor("TRACKER-LEFT", 3),
            TrackerDescriptor("TRACKER-RIGHT", 4),
        };
        if (extraTracker)
        {
            devices.Add(TrackerDescriptor("TRACKER-EXTRA", 5));
        }

        if (extraHmd)
        {
            devices.Add(Descriptor(
                "HMD-LIGHTHOUSE-EXTRA",
                6,
                SteamVrDeviceCategory.HeadMountedDisplay,
                SteamVrControllerRole.None,
                new SteamVrDeviceMetadata(
                    "lighthouse",
                    "lighthouse",
                    "Example",
                    "Extra HMD",
                    null)));
        }

        return devices;
    }

    private static SteamVrDeviceDescriptor LtbController(
        string serial,
        uint index,
        SteamVrControllerRole role) => Descriptor(
        serial,
        index,
        SteamVrDeviceCategory.InputController,
        role,
        new SteamVrDeviceMetadata(
            InternalDriverLoadedReadiness.DriverId,
            InternalDriverLoadedReadiness.TrackingSystemName,
            "LTB",
            "LTB Touch",
            InternalDriverLoadedReadiness.ControllerType,
            InternalDriverLoadedReadiness.InputProfilePath,
            BuildIdentity));

    private static SteamVrDeviceDescriptor TrackerDescriptor(string serial, uint index) =>
        Descriptor(
            serial,
            index,
            SteamVrDeviceCategory.GenericTracker,
            SteamVrControllerRole.None,
            new SteamVrDeviceMetadata("lighthouse", "lighthouse", "HTC", "Tracker", null));

    private static SteamVrDeviceDescriptor Descriptor(
        string serial,
        uint index,
        SteamVrDeviceCategory category,
        SteamVrControllerRole role,
        SteamVrDeviceMetadata metadata) => new(
        new SteamVrDeviceIdentity(serial, $"/devices/{serial}"),
        index,
        category,
        role,
        isConnected: true,
        metadata);

    private sealed class FakeRuntime : IInternalDriverSessionRuntime
    {
        private readonly Queue<InternalDriverRuntimeObservation> _observations;
        private InternalDriverRuntimeObservation _current;
        private ulong _now = 100_000_000_000UL;

        public FakeRuntime(params InternalDriverRuntimeObservation[] observations)
        {
            if (observations.Length == 0)
            {
                throw new ArgumentException("At least one observation is required.", nameof(observations));
            }

            _observations = new Queue<InternalDriverRuntimeObservation>(observations);
            _current = observations[0];
        }

        public InternalDriverRegistration Registration { get; set; } =
            InternalDriverSessionTests.Registration(changed: false);

        public Queue<FakeFeed> Feeds { get; set; } = new();

        public bool ResolveAsCalibrated { get; set; }

        public int? ObserveFailureAtCall { get; set; }

        public int ObserveCount { get; private set; }

        public int ResolveProfilesCount { get; private set; }

        public int ResetMetaCount { get; private set; }

        public int CreatedFeedCount { get; private set; }

        public int StopRunCount { get; private set; }

        public int DisposeCount { get; private set; }

        public InternalDriverPlatformProbe Probe() => new(
            true,
            "test platform ready",
            "none");

        public ValueTask<InternalDriverRegistration> EnsureDriverAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Registration);
        }

        public InternalDriverRuntimeObservation Observe()
        {
            ObserveCount++;
            if (ObserveCount == ObserveFailureAtCall)
            {
                throw new InvalidOperationException("scripted runtime observation failure");
            }

            if (_observations.Count > 0)
            {
                _current = _observations.Dequeue();
            }

            return _current;
        }

        public ValueTask<InternalDriverProfilePair> ResolveProfilesAsync(
            InternalDriverRuntimeObservation observation,
            InternalDriverProgress progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolveProfilesCount++;
            var readiness = ResolveAsCalibrated
                ? InternalDriverProfileReadiness.Calibrated
                : InternalDriverProfileReadiness.Reused;
            if (ResolveAsCalibrated)
            {
                var captureObservation = ReadyObservation(
                    sampleTimeSeconds: 42d,
                    extraTracker: true);
                var leftPartial = CaptureEvidence(12, 0.22d, rotationReady: false);
                var leftComplete = CaptureEvidence(48, 0.82d, rotationReady: true);
                var rightPartial = CaptureEvidence(13, 0.31d, rotationReady: false);
                var rightComplete = CaptureEvidence(52, 0.79d, rotationReady: true);
                progress(
                    InternalDriverSessionState.Recording,
                    "left capture started",
                    "none",
                    leftPartial,
                    rightCapture: null,
                    observation: captureObservation);
                progress(
                    InternalDriverSessionState.Recording,
                    "left capture complete",
                    "none",
                    leftComplete,
                    rightCapture: null,
                    observation: captureObservation);
                progress(
                    InternalDriverSessionState.Recording,
                    "right capture started",
                    "none",
                    leftComplete,
                    rightPartial,
                    captureObservation);
                progress(
                    InternalDriverSessionState.Recording,
                    "right capture complete",
                    "none",
                    leftComplete,
                    rightComplete,
                    captureObservation);
                foreach (var state in new[]
                         {
                             InternalDriverSessionState.Association,
                             InternalDriverSessionState.TimeAlignment,
                             InternalDriverSessionState.RotationSolve,
                             InternalDriverSessionState.TranslationAttempt,
                             InternalDriverSessionState.Validation,
                             InternalDriverSessionState.SaveProfile,
                         })
                {
                    progress(state, $"{state} test progress", "none");
                }
            }

            return ValueTask.FromResult(new InternalDriverProfilePair(
                new InternalDriverHandProfile(
                    ProtocolHand.Left,
                    "TRACKER-LEFT",
                    new RigidTransform(Quaternion.Identity, new Vector3(0.1f, 0f, 0f)),
                    readiness,
                    "left profile")
                {
                    Calibration = CalibrationEvidence(
                        ProtocolHand.Left,
                        ResolveAsCalibrated),
                },
                new InternalDriverHandProfile(
                    ProtocolHand.Right,
                    "TRACKER-RIGHT",
                    new RigidTransform(Quaternion.Identity, new Vector3(0f, 0.1f, 0f)),
                    readiness,
                    "right profile")
                {
                    Calibration = CalibrationEvidence(
                        ProtocolHand.Right,
                        ResolveAsCalibrated),
                }));
        }

        public IDriverFeed CreateFeed()
        {
            CreatedFeedCount++;
            return Feeds.Count > 0 ? Feeds.Dequeue() : new FakeFeed();
        }

        public void ResetMeta() => ResetMetaCount++;

        public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        public ulong GetMonotonicNanoseconds()
        {
            _now += 1_000_000UL;
            return _now;
        }

        public ValueTask StopRunAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopRunCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeFeed : IDriverFeed
    {
        private readonly object _sync = new();
        private readonly ProtocolSessionId _sessionId;
        private TaskCompletionSource? _blockedPublish;
        private ulong _sequence;

        public FakeFeed()
            : this(new ProtocolSessionId(100, 200))
        {
        }

        public FakeFeed(ProtocolSessionId sessionId)
        {
            _sessionId = sessionId;
        }

        public ConcurrentQueue<DriverHandState> Published { get; } = new();

        public TaskCompletionSource PublishBlocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool BlockNextPublish { get; set; }

        public bool BlockNeutralAndRetirement { get; set; }

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public DriverFeedHealth Health { get; private set; } = new(
            DriverFeedReadiness.Stopped,
            IsStale: false,
            SessionId: null,
            LastSuccessfulSequence: null,
            LastSuccessfulSendNanoseconds: null,
            LastSuccessfulHeartbeatNanoseconds: null,
            ConsecutiveReconnectAttempts: 0,
            LastError: null);

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Health = new DriverFeedHealth(
                DriverFeedReadiness.Ready,
                IsStale: false,
                _sessionId,
                LastSuccessfulSequence: 0,
                LastSuccessfulSendNanoseconds: 100_000_000_000UL,
                LastSuccessfulHeartbeatNanoseconds: 100_000_000_000UL,
                ConsecutiveReconnectAttempts: 0,
                LastError: null);
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishAsync(
            DriverHandState state,
            CancellationToken cancellationToken = default)
        {
            Published.Enqueue(state);
            if (BlockNeutralAndRetirement &&
                !state.Presence.HasFlag(ProtocolPresence.Tracked))
            {
                Health = Health with
                {
                    Readiness = DriverFeedReadiness.Reconnecting,
                    ConsecutiveReconnectAttempts = 1,
                    LastError = "test neutral pipe disconnected",
                };
                return new ValueTask(new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously).Task);
            }

            lock (_sync)
            {
                if (BlockNextPublish)
                {
                    BlockNextPublish = false;
                    _blockedPublish = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    Health = Health with
                    {
                        Readiness = DriverFeedReadiness.Reconnecting,
                        ConsecutiveReconnectAttempts = 1,
                        LastError = "test pipe disconnected",
                    };
                    PublishBlocked.TrySetResult();
                    return new ValueTask(_blockedPublish.Task);
                }
            }

            _sequence++;
            Health = Health with
            {
                Readiness = DriverFeedReadiness.Ready,
                SessionId = _sessionId,
                LastSuccessfulSequence = _sequence,
                LastSuccessfulSendNanoseconds = state.SampleMonotonicNanoseconds,
                ConsecutiveReconnectAttempts = 0,
                LastError = null,
            };
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            if (BlockNeutralAndRetirement)
            {
                return new ValueTask(new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously).Task);
            }

            Health = Health with { Readiness = DriverFeedReadiness.Stopped, SessionId = null };
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (BlockNeutralAndRetirement)
            {
                return new ValueTask(new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously).Task);
            }

            Health = Health with { Readiness = DriverFeedReadiness.Disposed, SessionId = null };
            return ValueTask.CompletedTask;
        }

        public void ReleaseBlockedPublish()
        {
            lock (_sync)
            {
                Health = Health with
                {
                    Readiness = DriverFeedReadiness.Ready,
                    SessionId = new ProtocolSessionId(_sessionId.Word0 + 1, _sessionId.Word1 + 1),
                    LastSuccessfulSequence = 0,
                    ConsecutiveReconnectAttempts = 0,
                    LastError = null,
                };
                _blockedPublish?.TrySetResult();
            }
        }
    }

    private sealed class RecordingOutput : IInternalDriverSessionOutput
    {
        private readonly ConcurrentQueue<InternalDriverSessionSnapshot> _snapshots = new();

        public IReadOnlyList<InternalDriverSessionSnapshot> Snapshots => _snapshots.ToArray();

        public void Write(InternalDriverSessionSnapshot snapshot) => _snapshots.Enqueue(snapshot);

        public async Task WaitForStateAsync(InternalDriverSessionState state, TimeSpan timeout)
        {
            var started = Stopwatch.StartNew();
            while (started.Elapsed < timeout)
            {
                if (_snapshots.Any(snapshot => snapshot.State == state))
                {
                    return;
                }

                await Task.Yield();
            }

            throw new TimeoutException($"Session did not report {state} within {timeout}.");
        }

        public void Dispose()
        {
        }
    }
}
