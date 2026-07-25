using System.Numerics;
using Ltb.App;
using Ltb.Core;
using Ltb.Driver;
using Ltb.MetaLink;
using Ltb.OpenVr;

namespace Ltb.Integration.Tests;

public sealed class InternalDriverPrerequisiteProbeTests
{
    private const string BuildIdentity = "driver_ltb-preflight-test";

    [Fact]
    public async Task ReadyProbeRequiresAllProbeableFactsAndDefersSessionOnlyFacts()
    {
        var runtime = new FakePrerequisiteRuntime(
            RegisteredInspection(),
            ReadyObservation());
        await using var probe = new InternalDriverPrerequisiteProbe(runtime);

        var snapshot = await probe.ProbeAsync();

        Assert.True(snapshot.ProbeCompleted);
        Assert.True(snapshot.CanStart);
        Assert.True(snapshot.CanCalibrate);
        Assert.Equal(InternalDriverPrerequisiteStatus.Ready, snapshot.Platform.Status);
        Assert.Equal(InternalDriverPrerequisiteStatus.Ready, snapshot.MetaLink.Status);
        Assert.Equal(InternalDriverPrerequisiteStatus.Ready, snapshot.Controllers.Status);
        Assert.Equal(InternalDriverPrerequisiteStatus.Ready, snapshot.SteamVr.Status);
        Assert.Equal(InternalDriverPrerequisiteStatus.Ready, snapshot.Trackers.Status);
        Assert.Equal(InternalDriverPrerequisiteStatus.Ready, snapshot.Driver.Status);
        Assert.Equal(
            InternalDriverPrerequisiteStatus.DeferredUntilStart,
            snapshot.Profiles.Status);
        Assert.Equal(
            InternalDriverPrerequisiteStatus.DeferredUntilStart,
            snapshot.Feed.Status);
        Assert.Contains("deferred checks", snapshot.StartGateReason, StringComparison.Ordinal);
        Assert.Equal(1, runtime.InspectCount);
        Assert.Equal(1, runtime.ObserveCount);
    }

    [Fact]
    public async Task UnregisteredReadableStageIsExplicitlyDeferredAndPermitsFirstStart()
    {
        var runtime = new FakePrerequisiteRuntime(
            RegisteredInspection() with { IsRegistered = false },
            ReadyObservation());
        await using var probe = new InternalDriverPrerequisiteProbe(runtime);

        var snapshot = await probe.ProbeAsync();

        Assert.True(snapshot.CanStart);
        Assert.Equal(
            InternalDriverPrerequisiteStatus.DeferredUntilStart,
            snapshot.Driver.Status);
        Assert.Contains("not registered", snapshot.Driver.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("Press Start once", snapshot.Driver.Remediation, StringComparison.Ordinal);
        Assert.Contains(snapshot.Driver.Diagnostic, snapshot.StartGateReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisteredButNotLoadedDriverFailsClosedWithSessionRemediation()
    {
        var observation = ReadyObservation() with
        {
            Devices = ReadyObservation().Devices
                .Where(device =>
                    device.Category != SteamVrDeviceCategory.InputController)
                .ToArray(),
        };
        var runtime = new FakePrerequisiteRuntime(
            RegisteredInspection(),
            observation);
        await using var probe = new InternalDriverPrerequisiteProbe(runtime);

        var snapshot = await probe.ProbeAsync();

        Assert.False(snapshot.CanStart);
        Assert.False(snapshot.CanCalibrate);
        Assert.Equal(
            InternalDriverPrerequisiteStatus.ActionRequired,
            snapshot.Driver.Status);
        Assert.Contains("LTB-TOUCH-LEFT", snapshot.Driver.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("restart SteamVR", snapshot.Driver.Remediation, StringComparison.Ordinal);
        Assert.Contains(snapshot.Driver.Diagnostic, snapshot.StartGateReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdditionalLighthouseHmdFailsSoleHmdGateBeforeStart()
    {
        var observation = ReadyObservation();
        var runtime = new FakePrerequisiteRuntime(
            RegisteredInspection(),
            observation with
            {
                Devices =
                [
                    .. observation.Devices,
                    Descriptor(
                        "HMD-EXTRA",
                        8,
                        SteamVrDeviceCategory.HeadMountedDisplay,
                        SteamVrControllerRole.None,
                        new SteamVrDeviceMetadata(
                            "lighthouse",
                            "lighthouse",
                            "Example",
                            "Extra HMD",
                            controllerType: null)),
                ],
            });
        await using var probe = new InternalDriverPrerequisiteProbe(runtime);

        var snapshot = await probe.ProbeAsync();

        Assert.False(snapshot.CanStart);
        Assert.Equal(
            InternalDriverPrerequisiteStatus.ActionRequired,
            snapshot.SteamVr.Status);
        Assert.Contains("sole HMD", snapshot.SteamVr.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationStopsProbeBeforeRuntimeObservation()
    {
        var runtime = new FakePrerequisiteRuntime(
            RegisteredInspection(),
            ReadyObservation())
        {
            BlockInspection = true,
        };
        await using var probe = new InternalDriverPrerequisiteProbe(runtime);
        using var cancellation = new CancellationTokenSource();

        var probing = probe.ProbeAsync(cancellation.Token).AsTask();
        await runtime.InspectStarted;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probing);
        Assert.Equal(0, runtime.ObserveCount);
    }

    [Fact]
    public async Task ProductionProbeFailsClosedBeforeWindowsOnlyIoOnUnsupportedHost()
    {
        if (OperatingSystem.IsWindows() && Environment.Is64BitProcess)
        {
            return;
        }

        await using var probe = InternalDriverSessionFactory.CreatePrerequisiteProbe();

        var snapshot = await probe.ProbeAsync();

        Assert.True(snapshot.ProbeCompleted);
        Assert.False(snapshot.CanStart);
        Assert.Equal(
            InternalDriverPrerequisiteStatus.ActionRequired,
            snapshot.Platform.Status);
        Assert.Contains("Windows x64", snapshot.Platform.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("win-x64", snapshot.StartGateReason, StringComparison.Ordinal);
    }

    private static SteamVrDriverInspection RegisteredInspection() => new(
        new SteamVrPaths(
            "openvrpaths.vrpath",
            "runtime",
            "config",
            "vrpathreg",
            "steamvr.vrsettings"),
        "driver_ltb",
        BuildIdentity,
        IsRegistered: true,
        SteamVrActivateMultipleDriversState.Enabled);

    private static InternalDriverRuntimeObservation ReadyObservation()
    {
        var trackers = new Dictionary<string, PoseSourceSample>(StringComparer.Ordinal)
        {
            ["TRACKER-LEFT"] = TrackerSample(10d, Vector3.UnitX),
            ["TRACKER-RIGHT"] = TrackerSample(10.001d, Vector3.UnitY),
        };
        return new InternalDriverRuntimeObservation(
            SteamVrRunning: true,
            "SteamVR runtime is running.",
            ReadyMeta(10d),
            ReadyDevices(),
            trackers);
    }

    private static MetaLinkRuntimeSnapshot ReadyMeta(double time)
    {
        var left = Controller(MetaLinkHand.Left, time);
        var right = Controller(MetaLinkHand.Right, time + 0.001d);
        return new MetaLinkRuntimeSnapshot(
            1,
            time,
            new MetaLinkHandSnapshot(
                MetaLinkHand.Left,
                MetaLinkReadiness.Ready,
                "left ready",
                left),
            new MetaLinkHandSnapshot(
                MetaLinkHand.Right,
                MetaLinkReadiness.Ready,
                "right ready",
                right));
    }

    private static MetaLinkControllerSnapshot Controller(
        MetaLinkHand hand,
        double time) => new(
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
            appMonotonicTimeNanoseconds: checked((long)(time * 1_000_000_000d)),
            clockUncertaintySeconds: 0.0001d),
        new MetaLinkButtons(false, false, false, false, false, false, 0),
        new MetaLinkTouches(
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            0),
        new MetaLinkAnalogState(0.5f, 0.5f, Vector2.Zero),
        MetaLinkBatteryState.Unavailable);

    private static IReadOnlyList<SteamVrDeviceDescriptor> ReadyDevices() =>
    [
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
                controllerType: null)
            {
                ActualTrackingSystemName = "lighthouse",
            }),
        Descriptor(
            InternalDriverLoadedReadiness.LeftControllerSerial,
            1,
            SteamVrDeviceCategory.InputController,
            SteamVrControllerRole.LeftHand,
            LtbMetadata()),
        Descriptor(
            InternalDriverLoadedReadiness.RightControllerSerial,
            2,
            SteamVrDeviceCategory.InputController,
            SteamVrControllerRole.RightHand,
            LtbMetadata()),
        TrackerDescriptor("TRACKER-LEFT", 3),
        TrackerDescriptor("TRACKER-RIGHT", 4),
    ];

    private static SteamVrDeviceMetadata LtbMetadata() => new(
        InternalDriverLoadedReadiness.DriverId,
        InternalDriverLoadedReadiness.TrackingSystemName,
        "Meta-Develop",
        "LTB Touch",
        InternalDriverLoadedReadiness.ControllerType,
        InternalDriverLoadedReadiness.InputProfilePath,
        BuildIdentity);

    private static SteamVrDeviceDescriptor TrackerDescriptor(string serial, uint index) =>
        Descriptor(
            serial,
            index,
            SteamVrDeviceCategory.GenericTracker,
            SteamVrControllerRole.None,
            new SteamVrDeviceMetadata(
                "lighthouse",
                "lighthouse",
                "HTC",
                "Tracker",
                controllerType: null));

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

    private static PoseSourceSample TrackerSample(
        double time,
        Vector3 position) => new(
        new TimestampedPoseSample(
            time,
            new RigidTransform(Quaternion.Identity, position),
            PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid),
        isConnected: true,
        PoseTrackingResult.RunningOk,
        runtimeTimeSeconds: time,
        predictionOffsetSeconds: 0d,
        sampleAgeSeconds: 0.002d,
        linearVelocityMetersPerSecond: Vector3.Zero,
        angularVelocityRadiansPerSecond: Vector3.Zero);

    private sealed class FakePrerequisiteRuntime(
        SteamVrDriverInspection inspection,
        InternalDriverRuntimeObservation observation)
        : IInternalDriverPrerequisiteRuntime
    {
        private readonly TaskCompletionSource _inspectStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool BlockInspection { get; init; }

        public Task InspectStarted => _inspectStarted.Task;

        public int InspectCount { get; private set; }

        public int ObserveCount { get; private set; }

        public InternalDriverPlatformProbe ProbePlatform() => new(
            true,
            "Windows x64 is supported.",
            "No remediation is required.");

        public async ValueTask<SteamVrDriverInspection> InspectDriverAsync(
            CancellationToken cancellationToken)
        {
            InspectCount++;
            _inspectStarted.TrySetResult();
            if (BlockInspection)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return inspection;
        }

        public InternalDriverRuntimeObservation Observe()
        {
            ObserveCount++;
            return observation;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
