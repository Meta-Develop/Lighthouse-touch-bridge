using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
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
    }

    [Fact]
    public async Task RuntimeFaultAfterActiveRebuildsFinalSnapshotWithoutRetiredFeedEvidence()
    {
        var runtime = new FakeRuntime(
            ReadyObservation(sampleTimeSeconds: 10d),
            ReadyObservation(sampleTimeSeconds: 11d))
        {
            ObserveFailureAtCall = 3,
        };
        var output = new RecordingOutput();
        await using var session = Session(runtime, output);

        await session.RunAsync();

        Assert.Contains(output.Snapshots, snapshot =>
            snapshot.State == InternalDriverSessionState.Active &&
            snapshot.Readiness.CanPublish);
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
        string? omitTracker = null)
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
            ReadyDevices(extraTracker),
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

    private static IReadOnlyList<SteamVrDeviceDescriptor> ReadyDevices(bool extraTracker = false)
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
                    progress(state, $"{state} test progress", "none");
                }
            }

            return ValueTask.FromResult(new InternalDriverProfilePair(
                new InternalDriverHandProfile(
                    ProtocolHand.Left,
                    "TRACKER-LEFT",
                    new RigidTransform(Quaternion.Identity, new Vector3(0.1f, 0f, 0f)),
                    readiness,
                    "left profile"),
                new InternalDriverHandProfile(
                    ProtocolHand.Right,
                    "TRACKER-RIGHT",
                    new RigidTransform(Quaternion.Identity, new Vector3(0f, 0.1f, 0f)),
                    readiness,
                    "right profile")));
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
