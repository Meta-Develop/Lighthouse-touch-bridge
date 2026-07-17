using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Ltb.App;
using Ltb.Core;
using Ltb.OpenVr;
using Ltb.Vmt;

namespace Ltb.Integration.Tests;

public sealed class OneHandBridgeCoordinatorTests
{
    private static readonly RigidTransform Mount = new(
        Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.25f),
        new Vector3(0.02f, -0.04f, 0.03f));

    [Fact]
    public async Task HealthyFreshSlotIsDiscoveredOnlyAfterActivationAndUsesActualPath()
    {
        var events = new List<string>();
        var initialPose = new RigidTransform(
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f),
            new Vector3(1f, 2f, 3f));
        using var cancel = new CancellationTokenSource();
        var context = CreateContext(
            events,
            [
                HealthySample(1d, initialPose),
                HealthySample(2d, initialPose),
                HealthySample(3d, initialPose),
            ],
            delayAction: () => cancel.Cancel());

        var result = await context.Coordinator.RunAsync(
            Profile(),
            new VmtDeviceAddress(7),
            TimeSpan.FromSeconds(0.5),
            monitorRateHz: 50,
            cancel.Token);

        Assert.Equal(OneHandBridgeStopReason.Cancellation, result.StopReason);
        Assert.DoesNotContain(
            context.Runtime.EnumerationHistory[0],
            device => VmtDeviceAddress.TryParse(device.Identity.DevicePath, out _));
        Assert.Empty(result.SafeDisableFailures);
        Assert.Equal(VmtDescriptor().Identity.DevicePath, result.VmtDevice!.Identity.DevicePath);
        Assert.NotNull(context.Verifier.LastRequest);
        Assert.Equal(TouchDescriptor().StableDeviceId,
            context.Verifier.LastRequest!.ExpectedInputControllerSerial);
        Assert.Equal(VmtDescriptor().Identity.DevicePath,
            context.Verifier.LastRequest.ExpectedPoseSourceDevicePath);
        AssertTransformClose(initialPose * Mount, context.Verifier.LastRequest.ExpectedOutputPose);
        Assert.Equal(
            [
                "vmt.start",
                "vmt.deactivate:LHR-TEST0001:7",
                "override.prepare:/devices/vmt/VMT_7",
                "vmt.wait:5",
                "vmt.activate:LHR-TEST0001:7",
                "override.enable:/devices/vmt/VMT_7",
                "verify",
                "delay",
                "vmt.deactivate:LHR-TEST0001:7",
                "override.release:/devices/vmt/VMT_7",
            ],
            events);
    }

    [Fact]
    public async Task StationaryTrackerPoseIsHealthyAndDoesNotCountAsStale()
    {
        var pose = new RigidTransform(Quaternion.Identity, new Vector3(0.4f, 0.5f, 0.6f));
        using var cancel = new CancellationTokenSource();
        var context = CreateContext(
            [],
            [
                HealthySample(10d, pose),
                HealthySample(10d, pose),
                HealthySample(10d, pose),
            ],
            delayAction: () => cancel.Cancel());

        var result = await context.Coordinator.RunAsync(
            Profile(),
            new VmtDeviceAddress(7),
            TimeSpan.FromSeconds(0.5),
            20,
            cancel.Token);

        Assert.Equal(OneHandBridgeStopReason.Cancellation, result.StopReason);
    }

    [Fact]
    public async Task TrackerDisconnectTriggersOneShotSafeDisable()
    {
        var context = CreateContext(
            [],
            [HealthySample(1d), HealthySample(2d), HealthySample(3d)]);
        context.Runtime.ReplaceTrackerSamples(
            [
                HealthySample(1d),
                HealthySample(2d),
                HealthySample(3d),
                Sample(4d, connected: false, ageSeconds: 0.01),
            ]);

        var result = await RunHealthFailureAsync(context);

        Assert.Contains("disconnected", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, context.Vmt.DeactivateCalls);
        Assert.Equal(1, context.Overrides.ReleaseCalls);
    }

    [Fact]
    public async Task TrackerLossDuringHeartbeatWaitCannotActivateOverride()
    {
        var context = CreateContext(
            [],
            [
                HealthySample(1d),
                Sample(2d, connected: false, ageSeconds: 0.01),
            ]);

        var result = await RunHealthFailureAsync(context);

        Assert.Contains("disconnected", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, context.Vmt.ActivateCalls);
        Assert.Equal(0, context.Overrides.EnableCalls);
        Assert.Equal(2, context.Vmt.DeactivateCalls);
        Assert.Equal(1, context.Overrides.ReleaseCalls);
    }

    [Fact]
    public async Task TrackerSampleAgeTriggersSafeDisableWithoutComparingUtc()
    {
        var context = CreateContext(
            [],
            [
                HealthySample(1d),
                HealthySample(2d),
                HealthySample(3d),
                Sample(4d, ageSeconds: 0.5),
            ]);

        var result = await RunHealthFailureAsync(context);

        Assert.Contains("sample is stale", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, context.Vmt.DeactivateCalls);
        Assert.Equal(1, context.Overrides.ReleaseCalls);
    }

    [Fact]
    public async Task MissingRuntimeSampleAgeAcceptsFreshSynchronousFullValidReads()
    {
        using var cancel = new CancellationTokenSource();
        var context = CreateContext(
            [],
            [
                Sample(1d, ageSeconds: null),
                Sample(2d, ageSeconds: null),
                Sample(3d, ageSeconds: null),
            ],
            delayAction: () => cancel.Cancel());

        var result = await context.Coordinator.RunAsync(
            Profile(),
            new VmtDeviceAddress(7),
            TimeSpan.FromSeconds(0.5),
            20,
            cancel.Token);

        Assert.Equal(OneHandBridgeStopReason.Cancellation, result.StopReason);
        Assert.Equal(1, context.Vmt.ActivateCalls);
        Assert.Equal(1, context.Overrides.EnableCalls);
    }

    [Fact]
    public async Task InvalidVmtPoseBeforeEnableNeverCreatesMapping()
    {
        var context = CreateContext(
            [],
            [HealthySample(1d), HealthySample(2d), HealthySample(3d)]);
        context.Runtime.ReplaceVmtSamples([InvalidTrackingSample(3d)]);

        var result = await RunHealthFailureAsync(context);

        Assert.Contains("VMT pose source", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, context.Overrides.EnableCalls);
        Assert.Equal(2, context.Vmt.DeactivateCalls);
        Assert.Equal(1, context.Overrides.ReleaseCalls);
    }

    [Fact]
    public async Task InvalidActiveVmtPoseTriggersSafeDisable()
    {
        var trackerSamples = new[]
        {
            HealthySample(1d),
            HealthySample(2d),
            HealthySample(3d),
            HealthySample(4d),
        };
        var context = CreateContext([], trackerSamples);
        context.Runtime.ReplaceVmtSamples(
            [VmtOutputSample(trackerSamples[2]), InvalidTrackingSample(4d)]);

        var result = await RunHealthFailureAsync(context);

        Assert.Contains("VMT pose source", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, context.Overrides.EnableCalls);
        Assert.Equal(2, context.Vmt.DeactivateCalls);
        Assert.Equal(1, context.Overrides.ReleaseCalls);
    }

    [Fact]
    public async Task VmtHeartbeatStalenessTriggersSafeDisableIndependentlyOfTrackerAge()
    {
        FakeVmtController? vmt = null;
        var context = CreateContext(
            [],
            [
                HealthySample(1d),
                HealthySample(2d),
                HealthySample(3d),
                HealthySample(4d),
            ],
            delayAction: () => vmt!.Alive = false);
        vmt = context.Vmt;

        var result = await RunHealthFailureAsync(context);

        Assert.Contains("VMT heartbeat", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OneHandBridgeCoordinator.VmtHeartbeatTimeout, context.Vmt.WaitTimeout);
    }

    [Fact]
    public async Task HeartbeatLossDuringActivationNeverEnablesOverride()
    {
        var context = CreateContext(
            [],
            [HealthySample(1d), HealthySample(2d), HealthySample(3d)]);
        context.Vmt.ActivateAction = () => context.Vmt.Alive = false;

        var result = await RunHealthFailureAsync(context);

        Assert.Contains("heartbeat", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, context.Vmt.ActivateCalls);
        Assert.Equal(0, context.Overrides.EnableCalls);
        Assert.Equal(2, context.Vmt.DeactivateCalls);
        Assert.Equal(1, context.Overrides.ReleaseCalls);
    }

    [Fact]
    public async Task PostActivationWaitAllowsDisconnectedSlotToBecomeConnectedBeforeOverride()
    {
        using var cancel = new CancellationTokenSource();
        var delayCalls = 0;
        var context = CreateContext(
            [],
            [HealthySample(1d), HealthySample(2d), HealthySample(3d)],
            delayAction: () =>
            {
                delayCalls++;
                if (delayCalls == 2)
                {
                    cancel.Cancel();
                }
            });
        var initial = new[] { TrackerDescriptor(), TouchDescriptor() };
        var active = new[]
        {
            TrackerDescriptor(),
            TouchDescriptor(),
            VmtDescriptor(isConnected: true),
        };
        context.Runtime.EnumerationFactory = callIndex => callIndex < 3 ? initial : active;

        var result = await context.Coordinator.RunAsync(
            Profile(),
            new VmtDeviceAddress(7),
            TimeSpan.FromSeconds(0.5),
            20,
            cancel.Token);

        Assert.Equal(OneHandBridgeStopReason.Cancellation, result.StopReason);
        Assert.True(result.VmtDevice!.IsConnected);
        Assert.Equal(1, context.Vmt.ActivateCalls);
        Assert.Equal(1, context.Overrides.EnableCalls);
        Assert.Equal(2, delayCalls);
    }

    [Fact]
    public async Task PostActivationConnectionTimeoutNeverEnablesOverride()
    {
        var disconnected = new[]
        {
            TrackerDescriptor(),
            TouchDescriptor(),
            VmtDescriptor(),
        };
        var context = CreateContext(
            [],
            [HealthySample(1d), HealthySample(2d)],
            monitorDevices: disconnected);

        var result = await context.Coordinator.RunAsync(
            Profile(),
            new VmtDeviceAddress(7),
            TimeSpan.FromSeconds(0.5),
            20,
            CancellationToken.None);

        Assert.Equal(OneHandBridgeStopReason.HealthFailure, result.StopReason);
        Assert.Contains("not discovered as a connected", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, context.Vmt.ActivateCalls);
        Assert.Equal(0, context.Overrides.EnableCalls);
        Assert.Equal(2, context.Vmt.DeactivateCalls);
        Assert.Equal(1, context.Overrides.ReleaseCalls);
        Assert.Equal(
            TimeSpan.FromSeconds(5),
            context.Delay.Delays.Aggregate(TimeSpan.Zero, (total, delay) => total + delay));
    }

    [Fact]
    public async Task TrackerInvalidDuringVmtConnectionWaitNeverEnablesOverride()
    {
        var context = CreateContext(
            [],
            [HealthySample(1d), HealthySample(2d), InvalidTrackingSample(3d)]);
        var initial = new[] { TrackerDescriptor(), TouchDescriptor() };
        var active = new[]
        {
            TrackerDescriptor(),
            TouchDescriptor(),
            VmtDescriptor(isConnected: true),
        };
        context.Runtime.EnumerationFactory = callIndex => callIndex < 3 ? initial : active;

        var result = await context.Coordinator.RunAsync(
            Profile(),
            new VmtDeviceAddress(7),
            TimeSpan.FromSeconds(0.5),
            20,
            CancellationToken.None);

        Assert.Equal(OneHandBridgeStopReason.HealthFailure, result.StopReason);
        Assert.Contains("not fully tracking-valid", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, context.Vmt.ActivateCalls);
        Assert.Equal(0, context.Overrides.EnableCalls);
        Assert.Equal(2, context.Vmt.DeactivateCalls);
        Assert.Equal(1, context.Overrides.ReleaseCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActiveVmtSlotDisappearanceOrDisconnectTriggersSafeDisable(
        bool disappears)
    {
        var context = CreateContext(
            [],
            [
                HealthySample(1d),
                HealthySample(2d),
                HealthySample(3d),
                HealthySample(4d),
            ]);
        var initial = new[] { TrackerDescriptor(), TouchDescriptor() };
        var active = new[]
        {
            TrackerDescriptor(),
            TouchDescriptor(),
            VmtDescriptor(isConnected: true),
        };
        var unhealthy = disappears
            ? new[] { TrackerDescriptor(), TouchDescriptor() }
            : new[] { TrackerDescriptor(), TouchDescriptor(), VmtDescriptor() };
        context.Runtime.EnumerationFactory = callIndex => callIndex switch
        {
            < 2 => initial,
            < 4 => active,
            _ => unhealthy,
        };

        var result = await RunHealthFailureAsync(context);

        Assert.Contains("VMT pose source", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, context.Overrides.EnableCalls);
        Assert.Equal(2, context.Vmt.DeactivateCalls);
        Assert.Equal(1, context.Overrides.ReleaseCalls);
    }

    [Fact]
    public async Task TrackerTransientIndexReuseTriggersSafeDisable()
    {
        var context = CreateContext(
            [],
            [
                HealthySample(1d),
                HealthySample(2d),
                HealthySample(3d),
                HealthySample(4d),
            ]);
        var initial = new[] { TrackerDescriptor(), TouchDescriptor() };
        var active = new[]
        {
            TrackerDescriptor(), TouchDescriptor(), VmtDescriptor(isConnected: true),
        };
        var movedTracker = new SteamVrDeviceDescriptor(
            TrackerDescriptor().Identity,
            transientDeviceIndex: 99,
            SteamVrDeviceCategory.GenericTracker,
            SteamVrControllerRole.None,
            isConnected: true);
        var reused = new[]
        {
            movedTracker, TouchDescriptor(), VmtDescriptor(isConnected: true),
        };
        context.Runtime.EnumerationFactory = callIndex => callIndex switch
        {
            < 2 => initial,
            < 4 => active,
            _ => reused,
        };

        var result = await RunHealthFailureAsync(context);

        Assert.Contains("transient index reused", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, context.Overrides.EnableCalls);
        Assert.Equal(2, context.Vmt.DeactivateCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ControllerRoleOrPathSwapTriggersSafeDisable(bool swapRole)
    {
        var context = CreateContext(
            [],
            [
                HealthySample(1d),
                HealthySample(2d),
                HealthySample(3d),
                HealthySample(4d),
            ]);
        var initial = new[] { TrackerDescriptor(), TouchDescriptor() };
        var active = new[]
        {
            TrackerDescriptor(), TouchDescriptor(), VmtDescriptor(isConnected: true),
        };
        var swappedController = new SteamVrDeviceDescriptor(
            new SteamVrDeviceIdentity(
                TouchDescriptor().StableDeviceId,
                swapRole
                    ? TouchDescriptor().Identity.DevicePath
                    : "/devices/alvr/TOUCH-TEST-REPLACED"),
            TouchDescriptor().TransientDeviceIndex,
            SteamVrDeviceCategory.InputController,
            swapRole
                ? SteamVrControllerRole.RightHand
                : SteamVrControllerRole.LeftHand,
            isConnected: true);
        var swapped = new[]
        {
            TrackerDescriptor(), swappedController, VmtDescriptor(isConnected: true),
        };
        context.Runtime.EnumerationFactory = callIndex => callIndex switch
        {
            < 2 => initial,
            < 4 => active,
            _ => swapped,
        };

        var result = await RunHealthFailureAsync(context);

        Assert.Contains("path/category/role", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, context.Overrides.EnableCalls);
        Assert.Equal(2, context.Vmt.DeactivateCalls);
    }

    [Fact]
    public async Task TouchLossTriggersSafeDisable()
    {
        var disconnectedTouch = TouchDescriptor(isConnected: false);
        var context = CreateContext(
            [],
            [
                HealthySample(1d),
                HealthySample(2d),
                HealthySample(3d),
                HealthySample(4d),
            ],
            monitorDevices: [TrackerDescriptor(), disconnectedTouch, VmtDescriptor(isConnected: true)]);
        var initial = new[] { TrackerDescriptor(), TouchDescriptor() };
        var active = new[]
        {
            TrackerDescriptor(),
            TouchDescriptor(),
            VmtDescriptor(isConnected: true),
        };
        var lostTouch = new[]
        {
            TrackerDescriptor(),
            disconnectedTouch,
            VmtDescriptor(isConnected: true),
        };
        context.Runtime.EnumerationFactory = callIndex => callIndex switch
        {
            < 2 => initial,
            < 4 => active,
            _ => lostTouch,
        };

        var result = await RunHealthFailureAsync(context);

        Assert.Contains("Touch input controller", result.Message, StringComparison.Ordinal);
        Assert.Equal(2, context.Vmt.DeactivateCalls);
        Assert.Equal(1, context.Overrides.ReleaseCalls);
    }

    [Fact]
    public async Task ActivationFailureCreatesNoOverrideAndStillSafeDisablesBothSurfaces()
    {
        var context = CreateContext(
            [],
            [HealthySample(1d), HealthySample(2d), HealthySample(3d)]);
        context.Vmt.ActivateFailure = new IOException("synthetic activation failure");

        var exception = await Assert.ThrowsAsync<OneHandBridgeRunException>(() =>
            context.Coordinator.RunAsync(
                Profile(),
                new VmtDeviceAddress(7),
                TimeSpan.FromSeconds(0.5),
                20,
                CancellationToken.None));

        Assert.Contains("synthetic activation failure", exception.Message);
        Assert.Equal(0, context.Overrides.EnableCalls);
        Assert.Equal(2, context.Vmt.DeactivateCalls);
        Assert.Equal(1, context.Overrides.ReleaseCalls);
        Assert.False(context.Overrides.IsEnabled);
    }

    [Fact]
    public async Task OverrideEnableFailureImmediatelyDeactivatesAndReleases()
    {
        var context = CreateContext(
            [],
            [HealthySample(1d), HealthySample(2d), HealthySample(3d)]);
        context.Overrides.EnableFailure = new IOException("synthetic settings failure");

        var exception = await Assert.ThrowsAsync<OneHandBridgeRunException>(() =>
            context.Coordinator.RunAsync(
                Profile(),
                new VmtDeviceAddress(7),
                TimeSpan.FromSeconds(0.5),
                20,
                CancellationToken.None));

        Assert.Contains("synthetic settings failure", exception.Message);
        Assert.Equal(1, context.Vmt.ActivateCalls);
        Assert.Equal(2, context.Vmt.DeactivateCalls);
        Assert.Equal(1, context.Overrides.ReleaseCalls);
    }

    [Fact]
    public async Task ExistingExactBindingIsReleasedBeforeActivation()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = Path.Combine(directory.Path, "steamvr.vrsettings");
        File.WriteAllText(settingsPath, """
            {
              "TrackingOverrides": {
                "/devices/vmt/VMT_7": "/user/hand/left"
              }
            }
            """);
        using var cancel = new CancellationTokenSource();
        var events = new List<string>();
        var vmt = new FakeVmtController(events);
        var bindingWasReleasedBeforeActivation = false;
        vmt.ActivateAction = () =>
        {
            using var settingsAtActivation = JsonDocument.Parse(File.ReadAllBytes(settingsPath));
            bindingWasReleasedBeforeActivation = !settingsAtActivation.RootElement
                .GetProperty("TrackingOverrides")
                .TryGetProperty("/devices/vmt/VMT_7", out _);
        };
        var coordinator = new OneHandBridgeCoordinator(
            new FakeRuntime(
                [TrackerDescriptor(), TouchDescriptor()],
                [HealthySample(1d), HealthySample(2d), HealthySample(3d)]),
            vmt,
            new SteamVrOneHandBridgeOverrideController(
                new SteamVrSettingsManager(settingsPath)),
            new FakeVerificationProbe(events),
            new FakeDelay(events, () => cancel.Cancel()));

        var result = await coordinator.RunAsync(
            Profile(),
            new VmtDeviceAddress(7),
            TimeSpan.FromSeconds(0.5),
            20,
            cancel.Token);

        Assert.Equal(OneHandBridgeStopReason.Cancellation, result.StopReason);
        Assert.Equal(1, vmt.ActivateCalls);
        Assert.Equal(2, vmt.DeactivateCalls);
        Assert.True(
            events.IndexOf("vmt.deactivate:LHR-TEST0001:7") <
            events.IndexOf("vmt.activate:LHR-TEST0001:7"));
        Assert.True(bindingWasReleasedBeforeActivation);
        using var settings = JsonDocument.Parse(File.ReadAllBytes(settingsPath));
        Assert.False(settings.RootElement.GetProperty("TrackingOverrides")
            .TryGetProperty("/devices/vmt/VMT_7", out _));
    }

    [Fact]
    public async Task HandOwnershipConflictIsRejectedBeforeVmtActivation()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = Path.Combine(directory.Path, "steamvr.vrsettings");
        File.WriteAllText(settingsPath, """
            {
              "TrackingOverrides": {
                "/devices/other/POSE-OWNER": "/user/hand/left"
              }
            }
            """);
        var events = new List<string>();
        var vmt = new FakeVmtController(events);
        var coordinator = new OneHandBridgeCoordinator(
            new FakeRuntime(
                [TrackerDescriptor(), TouchDescriptor()],
                [HealthySample(1d)]),
            vmt,
            new SteamVrOneHandBridgeOverrideController(
                new SteamVrSettingsManager(settingsPath)),
            new FakeVerificationProbe(events),
            new FakeDelay(events, null));

        var exception = await Assert.ThrowsAsync<OneHandBridgeRunException>(() =>
            coordinator.RunAsync(
                Profile(),
                new VmtDeviceAddress(7),
                TimeSpan.FromSeconds(0.5),
                20,
                CancellationToken.None));

        Assert.Contains("already supplied", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, vmt.ActivateCalls);
        Assert.Equal(1, vmt.DeactivateCalls);
    }

    [Fact]
    public async Task RequestedVmtSlotCannotBeSelectedAsItsOwnPhysicalTracker()
    {
        var events = new List<string>();
        var vmt = new FakeVmtController(events);
        var overrides = new FakeOverrideController(events);
        var coordinator = new OneHandBridgeCoordinator(
            new FakeRuntime(
                [VmtDescriptor(isConnected: true), TouchDescriptor()],
                [HealthySample(1d)]),
            vmt,
            overrides,
            new FakeVerificationProbe(events),
            new FakeDelay(events, null));
        var selfFollowingProfile = Profile() with
        {
            TrackerSerial = VmtDescriptor(isConnected: true).StableDeviceId,
        };

        var exception = await Assert.ThrowsAsync<OneHandBridgeRunException>(() =>
            coordinator.RunAsync(
                selfFollowingProfile,
                new VmtDeviceAddress(7),
                TimeSpan.FromSeconds(0.5),
                20,
                CancellationToken.None));

        Assert.Contains("cannot follow itself", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, vmt.ActivateCalls);
        Assert.Equal(2, vmt.DeactivateCalls);
        Assert.Equal(1, overrides.PrepareCalls);
        Assert.Equal(1, overrides.ReleaseCalls);
    }

    [Theory]
    [InlineData("missing-tracker")]
    [InlineData("disconnected-tracker")]
    [InlineData("missing-touch")]
    [InlineData("wrong-touch-role")]
    [InlineData("enumeration-throws")]
    public async Task DiscoveryOrSelectionFailureStillCleansBothSurfacesBeforeAndAfter(
        string scenario)
    {
        var events = new List<string>();
        IReadOnlyList<SteamVrDeviceDescriptor> devices = scenario switch
        {
            "missing-tracker" => [TouchDescriptor()],
            "disconnected-tracker" => [TrackerDescriptor(isConnected: false), TouchDescriptor()],
            "missing-touch" => [TrackerDescriptor()],
            "wrong-touch-role" => [TrackerDescriptor(), TouchDescriptorWithRole(SteamVrControllerRole.RightHand)],
            _ => [TrackerDescriptor(), TouchDescriptor()],
        };
        var runtime = new FakeRuntime(devices, [HealthySample(1d)]);
        if (scenario == "enumeration-throws")
        {
            runtime.EnumerationFactory = _ =>
                throw new InvalidOperationException("synthetic enumeration failure");
        }

        var vmt = new FakeVmtController(events);
        var overrides = new FakeOverrideController(events);
        var coordinator = new OneHandBridgeCoordinator(
            runtime,
            vmt,
            overrides,
            new FakeVerificationProbe(events),
            new FakeDelay(events, null));

        _ = await Assert.ThrowsAsync<OneHandBridgeRunException>(() =>
            coordinator.RunAsync(
                Profile(),
                new VmtDeviceAddress(7),
                TimeSpan.FromSeconds(0.5),
                20,
                CancellationToken.None));

        Assert.Equal(0, vmt.ActivateCalls);
        Assert.Equal(2, vmt.DeactivateCalls);
        Assert.Equal(1, overrides.PrepareCalls);
        Assert.Equal(1, overrides.ReleaseCalls);
        Assert.True(
            events.IndexOf("vmt.deactivate:LHR-TEST0001:7") <
            events.IndexOf("override.prepare:/devices/vmt/VMT_7"));
    }

    [Theory]
    [InlineData("missing-tracker")]
    [InlineData("disconnected-tracker")]
    [InlineData("missing-touch")]
    [InlineData("wrong-touch-role")]
    [InlineData("enumeration-throws")]
    [InlineData("self-follow")]
    public async Task ExactPersistedBindingIsRemovedBeforeEveryDiscoveryOrSelectionFailure(
        string scenario)
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = Path.Combine(directory.Path, "steamvr.vrsettings");
        File.WriteAllText(settingsPath, """
            {
              "TrackingOverrides": {
                "/devices/vmt/VMT_7": "/user/hand/left"
              }
            }
            """);
        var events = new List<string>();
        var vmt = new FakeVmtController(events);
        IReadOnlyList<SteamVrDeviceDescriptor> devices = scenario switch
        {
            "missing-tracker" => [TouchDescriptor()],
            "disconnected-tracker" => [TrackerDescriptor(isConnected: false), TouchDescriptor()],
            "missing-touch" => [TrackerDescriptor()],
            "wrong-touch-role" => [TrackerDescriptor(), TouchDescriptorWithRole(SteamVrControllerRole.RightHand)],
            "self-follow" => [VmtDescriptor(isConnected: true), TouchDescriptor()],
            _ => [TrackerDescriptor(), TouchDescriptor()],
        };
        var runtime = new FakeRuntime(devices, [HealthySample(1d)]);
        if (scenario == "enumeration-throws")
        {
            runtime.EnumerationFactory = _ =>
                throw new InvalidOperationException("synthetic enumeration failure");
        }

        var overrides = new RecordingOverrideController(
            new SteamVrOneHandBridgeOverrideController(
                new SteamVrSettingsManager(settingsPath)));
        var coordinator = new OneHandBridgeCoordinator(
            runtime,
            vmt,
            overrides,
            new FakeVerificationProbe(events),
            new FakeDelay(events, null));
        var profile = scenario == "self-follow"
            ? Profile() with
            {
                TrackerSerial = VmtDescriptor(isConnected: true).StableDeviceId,
            }
            : Profile();

        _ = await Assert.ThrowsAsync<OneHandBridgeRunException>(() =>
            coordinator.RunAsync(
                profile,
                new VmtDeviceAddress(7),
                TimeSpan.FromSeconds(0.5),
                20,
                CancellationToken.None));

        Assert.Equal(0, vmt.ActivateCalls);
        Assert.Equal(2, vmt.DeactivateCalls);
        Assert.Equal(1, overrides.PrepareCalls);
        Assert.Equal(1, overrides.ReleaseCalls);
        Assert.Equal(0, overrides.EnableCalls);
        using var settings = JsonDocument.Parse(File.ReadAllBytes(settingsPath));
        Assert.False(settings.RootElement.GetProperty("TrackingOverrides")
            .TryGetProperty("/devices/vmt/VMT_7", out _));
    }

    [Fact]
    public async Task BootstrapCleanupAttemptsBothSurfacesAndSurfacesBothFailures()
    {
        var context = CreateContext([], [HealthySample(1d)]);
        context.Vmt.DeactivateFailure = new IOException("synthetic bootstrap deactivate failure");
        context.Vmt.DeactivateFailureOnCall = 1;
        context.Overrides.PrepareFailure = new IOException("synthetic bootstrap mapping failure");

        var exception = await Assert.ThrowsAsync<OneHandBridgeRunException>(() =>
            context.Coordinator.RunAsync(
                Profile(),
                new VmtDeviceAddress(7),
                TimeSpan.FromSeconds(0.5),
                20,
                CancellationToken.None));

        Assert.Equal(2, exception.SafeDisableFailures.Count);
        Assert.Contains("bootstrap deactivate failure", exception.Message);
        Assert.Contains("bootstrap mapping failure", exception.Message);
        Assert.Equal(1, context.Vmt.DeactivateCalls);
        Assert.Equal(1, context.Overrides.PrepareCalls);
        Assert.Equal(0, context.Vmt.ActivateCalls);
        Assert.Empty(context.Runtime.EnumerationHistory);
    }

    [Fact]
    public async Task VerificationMismatchTriggersSafeDisable()
    {
        var context = CreateContext(
            [],
            [HealthySample(1d), HealthySample(2d), HealthySample(3d)]);
        context.Verifier.ObservationFactory = request => new OneHandBridgeVerificationObservation(
            "TOUCH-WRONG",
            request.ExpectedPoseSourceDevicePath,
            request.ExpectedOutputPose,
            IsDeferred: false,
            "synthetic mismatch");

        var exception = await Assert.ThrowsAsync<OneHandBridgeRunException>(() =>
            context.Coordinator.RunAsync(
                Profile(),
                new VmtDeviceAddress(7),
                TimeSpan.FromSeconds(0.5),
                20,
                CancellationToken.None));

        Assert.Contains("Touch inputs", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, context.Vmt.DeactivateCalls);
        Assert.Equal(1, context.Overrides.ReleaseCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerificationWrongPoseSourceOrOutputTriggersSafeDisable(bool wrongPath)
    {
        var context = CreateContext(
            [],
            [HealthySample(1d), HealthySample(2d), HealthySample(3d)]);
        context.Verifier.ObservationFactory = request =>
            new OneHandBridgeVerificationObservation(
                request.ExpectedInputControllerSerial,
                wrongPath ? "/devices/vmt/VMT_6" : request.ExpectedPoseSourceDevicePath,
                wrongPath
                    ? request.ExpectedOutputPose
                    : new RigidTransform(
                        request.ExpectedOutputPose.Rotation,
                        request.ExpectedOutputPose.TranslationMeters +
                        new Vector3(0.2f, 0f, 0f)),
                IsDeferred: false,
                "synthetic mismatch");

        var exception = await Assert.ThrowsAsync<OneHandBridgeRunException>(() =>
            context.Coordinator.RunAsync(
                Profile(),
                new VmtDeviceAddress(7),
                TimeSpan.FromSeconds(0.5),
                20,
                CancellationToken.None));

        Assert.Contains(
            wrongPath ? "VMT pose source" : "output pose",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, context.Vmt.DeactivateCalls);
        Assert.Equal(1, context.Overrides.ReleaseCalls);
    }

    [Fact]
    public async Task CancellationUsesSafeDisableAndReturnsDistinctResult()
    {
        using var cancel = new CancellationTokenSource();
        var context = CreateContext(
            [],
            [HealthySample(1d), HealthySample(2d), HealthySample(3d)],
            delayAction: () => cancel.Cancel());

        var result = await context.Coordinator.RunAsync(
            Profile(),
            new VmtDeviceAddress(7),
            TimeSpan.FromSeconds(0.5),
            20,
            cancel.Token);

        Assert.Equal(OneHandBridgeStopReason.Cancellation, result.StopReason);
        Assert.Equal(2, context.Vmt.DeactivateCalls);
        Assert.Equal(1, context.Overrides.ReleaseCalls);
    }

    [Fact]
    public async Task CancellationBeforeStartupStillAttemptsSafeDisable()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var context = CreateContext([], [HealthySample(1d)]);

        var result = await context.Coordinator.RunAsync(
            Profile(),
            new VmtDeviceAddress(7),
            TimeSpan.FromSeconds(0.5),
            20,
            cancel.Token);

        Assert.Equal(OneHandBridgeStopReason.Cancellation, result.StopReason);
        Assert.Null(result.VmtDevice);
        Assert.Contains("safely disabled", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, context.Vmt.DeactivateCalls);
        Assert.Equal(1, context.Overrides.ReleaseCalls);
    }

    [Fact]
    public async Task CancellationReportsIncompleteCleanupWhenSafeDisableFails()
    {
        using var cancel = new CancellationTokenSource();
        var context = CreateContext(
            [],
            [HealthySample(1d), HealthySample(2d), HealthySample(3d)],
            delayAction: () => cancel.Cancel());
        context.Vmt.DeactivateFailure = new IOException("synthetic final deactivation failure");
        context.Vmt.DeactivateFailureOnCall = 2;

        var result = await context.Coordinator.RunAsync(
            Profile(),
            new VmtDeviceAddress(7),
            TimeSpan.FromSeconds(0.5),
            20,
            cancel.Token);

        Assert.Equal(OneHandBridgeStopReason.Cancellation, result.StopReason);
        Assert.Contains("cleanup is incomplete", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(result.SafeDisableFailures);
        Assert.Equal(2, context.Vmt.DeactivateCalls);
        Assert.Equal(1, context.Overrides.ReleaseCalls);
    }

    [Fact]
    public async Task HungBootstrapDeactivationIsBoundedAndSettingsCleanupStillRuns()
    {
        var context = CreateContext([], [HealthySample(1d)]);
        context.Vmt.HangDeactivateOnCall = 1;

        var exception = await Assert.ThrowsAsync<OneHandBridgeRunException>(() =>
            context.Coordinator.RunAsync(
                Profile(),
                new VmtDeviceAddress(7),
                TimeSpan.FromSeconds(0.5),
                20,
                CancellationToken.None));

        Assert.Contains("within 2 seconds", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, context.Vmt.DeactivateCalls);
        Assert.Equal(1, context.Overrides.PrepareCalls);
        Assert.Equal(0, context.Vmt.ActivateCalls);
        Assert.Contains(TimeSpan.FromSeconds(2), context.Delay.Delays);
    }

    [Fact]
    public async Task RequestedSlowMonitorRateIsCappedBySafetyThresholds()
    {
        using var cancel = new CancellationTokenSource();
        OneHandBridgeActiveState? active = null;
        var context = CreateContext(
            [],
            [HealthySample(1d), HealthySample(2d), HealthySample(3d)],
            delayAction: () => cancel.Cancel(),
            onActivated: state => active = state);

        var result = await context.Coordinator.RunAsync(
            Profile(),
            new VmtDeviceAddress(7),
            TimeSpan.FromSeconds(0.5),
            monitorRateHz: 0.01,
            cancel.Token);

        Assert.Equal(OneHandBridgeStopReason.Cancellation, result.StopReason);
        Assert.NotNull(active);
        Assert.Equal(TimeSpan.FromSeconds(0.25), active!.EffectiveMonitorInterval);
        Assert.Contains(TimeSpan.FromSeconds(0.25), context.Delay.Delays);
    }

    [Fact]
    public async Task HungRuntimeVerificationTimesOutWhileHealthMonitoringContinues()
    {
        var samples = Enumerable.Range(1, 10)
            .Select(index => HealthySample(index))
            .ToArray();
        var context = CreateContext([], samples);
        context.Verifier.AsyncObservationFactory = (_, _) =>
            new ValueTask<OneHandBridgeVerificationObservation>(
                new TaskCompletionSource<OneHandBridgeVerificationObservation>(
                    TaskCreationOptions.RunContinuationsAsynchronously).Task);

        var exception = await Assert.ThrowsAsync<OneHandBridgeRunException>(() =>
            context.Coordinator.RunAsync(
                Profile(),
                new VmtDeviceAddress(7),
                TimeSpan.FromSeconds(5),
                monitorRateHz: 1,
                CancellationToken.None));

        Assert.Contains("did not complete within 5 seconds", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, context.Delay.Delays.Count(delay => delay == TimeSpan.FromSeconds(1)));
        Assert.True(context.Runtime.EnumerationHistory.Count >= 9);
        Assert.Equal(2, context.Vmt.DeactivateCalls);
        Assert.Equal(1, context.Overrides.ReleaseCalls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SafeDisableAttemptsBothOperationsAndReportsEitherFailure(
        bool failDeactivate,
        bool failRelease)
    {
        var context = CreateContext(
            [],
            [HealthySample(1d), HealthySample(2d), HealthySample(3d)]);
        context.Verifier.ObservationFactory = _ =>
            throw new InvalidOperationException("synthetic verification exception");
        context.Vmt.DeactivateFailure = failDeactivate
            ? new IOException("synthetic deactivate failure")
            : null;
        context.Vmt.DeactivateFailureOnCall = 2;
        context.Overrides.ReleaseFailure = failRelease
            ? new IOException("synthetic release failure")
            : null;

        var exception = await Assert.ThrowsAsync<OneHandBridgeRunException>(() =>
            context.Coordinator.RunAsync(
                Profile(),
                new VmtDeviceAddress(7),
                TimeSpan.FromSeconds(0.5),
                20,
                CancellationToken.None));

        Assert.Equal(2, context.Vmt.DeactivateCalls);
        Assert.Equal(1, context.Overrides.ReleaseCalls);
        var expectedFailures = (failDeactivate ? 1 : 0) + (failRelease ? 1 : 0);
        Assert.Equal(expectedFailures, exception.SafeDisableFailures.Count);
        Assert.Contains(
            $"SafeDisable also reported {expectedFailures}",
            exception.Message);
    }

    [Fact]
    public async Task MonitorExceptionTriggersSafeDisableInsteadOfRetrying()
    {
        var context = CreateContext(
            [],
            [HealthySample(1d), HealthySample(2d), HealthySample(3d)]);

        var result = await RunHealthFailureAsync(context);

        Assert.Contains("monitor failed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, context.Vmt.DeactivateCalls);
        Assert.Equal(1, context.Overrides.ReleaseCalls);
    }

    [Fact]
    public async Task EndToEndProfileOscSettingsVerificationAndStalenessCleanupUseOnlyFakes()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = Path.Combine(directory.Path, "steamvr.vrsettings");
        File.WriteAllText(settingsPath, """
            {
              "steamvr": { "showAdvancedSettings": true },
              "TrackingOverrides": {},
              "unrelated": { "keep": 42 }
            }
            """, new UTF8Encoding(false));
        var profile = BridgeProfileLoader.Parse("""
            {
              "schema_version": 1,
              "profile_name": "Fake one-hand bridge",
              "hand": "left",
              "controller_runtime": "ALVR",
              "controller_model": "Fake Touch",
              "controller_serial": "TOUCH-TEST-LEFT",
              "tracker_serial": "LHR-TEST0001",
              "calibration_policy": "auto",
              "selected_mode": "full_6dof",
              "tracker_to_controller": {
                "translation_m": [0.02, -0.04, 0.03],
                "rotation_xyzw": [0, 0, 0.12467473, 0.9921977]
              }
            }
            """);
        var transport = new RecordingVmtTransport();
        await using var client = new VmtClient(
            transport,
            OneHandBridgeCoordinator.VmtHeartbeatTimeout);
        var vmt = new OscBackedVmtController(client);
        var runtime = new FakeRuntime(
            [TrackerDescriptor(), TouchDescriptor()],
            [
                HealthySample(1d),
                HealthySample(2d),
                HealthySample(3d),
                Sample(4d, ageSeconds: 0.5),
            ]);
        var verifier = new EffectBackedVerificationProbe(
            transport,
            settingsPath,
            TouchDescriptor(),
            HealthySample(3d).Pose);
        var coordinator = new OneHandBridgeCoordinator(
            runtime,
            vmt,
            new SteamVrOneHandBridgeOverrideController(
                new SteamVrSettingsManager(settingsPath)),
            verifier,
            new FakeDelay([], null));

        var result = await coordinator.RunAsync(
            profile,
            new VmtDeviceAddress(7),
            TimeSpan.FromSeconds(0.5),
            20,
            CancellationToken.None);

        Assert.Equal(OneHandBridgeStopReason.HealthFailure, result.StopReason);
        Assert.Contains("stale", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(verifier.LastRequest);
        AssertTransformClose(
            HealthySample(1d).Pose * profile.TrackerToController,
            verifier.LastRequest!.ExpectedOutputPose);
        Assert.Collection(
            transport.Sent,
            packet => Assert.Equal(7, ReadJointSlot(packet)),
            packet => Assert.Equal(VmtOscProtocol.SetAutoPoseUpdateAddress, ReadOscAddress(packet)),
            packet => Assert.Equal(7, ReadJointSlot(packet)),
            packet => Assert.Equal(7, ReadJointSlot(packet)));
        Assert.Equal(0, ReadJointEnable(transport.Sent[0]));
        Assert.Equal((int)VmtDeviceMode.Tracker, ReadJointEnable(transport.Sent[2]));
        Assert.Equal(0, ReadJointEnable(transport.Sent[3]));

        using var settings = JsonDocument.Parse(File.ReadAllBytes(settingsPath));
        Assert.True(settings.RootElement.GetProperty("steamvr")
            .GetProperty("activateMultipleDrivers").GetBoolean());
        Assert.Equal(42, settings.RootElement.GetProperty("unrelated").GetProperty("keep").GetInt32());
        Assert.False(settings.RootElement.GetProperty("TrackingOverrides")
            .TryGetProperty(VmtDescriptor().Identity.DevicePath, out _));
    }

    [Fact]
    public void BridgeCommandLineRequiresExplicitSettingsAndKeepsThresholdsSeparate()
    {
        Assert.True(AppCommandLineOptions.TryParse(
            [
                "bridge",
                "--profile", "profile.json",
                "--vmt-slot", "7",
                "--steamvr-settings", "fixture.vrsettings",
                "--stale-after", "0.25",
                "--monitor-rate", "40",
            ],
            out var options,
            out var error), error);
        Assert.Equal(AppCommand.Bridge, options.Command);
        Assert.Equal("profile.json", options.ProfilePath);
        Assert.Equal(7, options.VmtSlot);
        Assert.Equal("fixture.vrsettings", options.SteamVrSettingsPath);
        Assert.Equal(0.25, options.StaleAfterSeconds);
        Assert.Equal(40, options.MonitorRateHz);
        Assert.Equal(TimeSpan.FromSeconds(5), OneHandBridgeCoordinator.VmtHeartbeatTimeout);

        Assert.False(AppCommandLineOptions.TryParse(
            ["bridge", "--profile", "profile.json", "--vmt-slot", "7"],
            out _,
            out var missingError));
        Assert.Contains("--steamvr-settings", missingError);

        foreach (var invalidThreshold in new[] { "0", "0.000999", "NaN" })
        {
            Assert.False(AppCommandLineOptions.TryParse(
                [
                    "bridge",
                    "--profile", "profile.json",
                    "--vmt-slot", "7",
                    "--steamvr-settings", "fixture.vrsettings",
                    "--stale-after", invalidThreshold,
                ],
                out _,
                out var thresholdError));
            Assert.Contains("--stale-after must be at least 0.001", thresholdError);
        }
    }

    private static async Task<OneHandBridgeResult> RunHealthFailureAsync(TestContext context) =>
        await context.Coordinator.RunAsync(
            Profile(),
            new VmtDeviceAddress(7),
            TimeSpan.FromSeconds(0.5),
            20,
            CancellationToken.None);

    private static TestContext CreateContext(
        List<string> events,
        IReadOnlyList<PoseSourceSample> trackerSamples,
        Action? delayAction = null,
        IReadOnlyList<SteamVrDeviceDescriptor>? monitorDevices = null,
        Action<OneHandBridgeActiveState>? onActivated = null)
    {
        var clock = new BridgeManualTimeProvider();
        var delay = new FakeDelay(events, delayAction, clock);
        var runtime = new FakeRuntime(
            [TrackerDescriptor(), TouchDescriptor()],
            trackerSamples,
            monitorDevices);
        var vmt = new FakeVmtController(events);
        var overrides = new FakeOverrideController(events);
        var observedTrackerPose = trackerSamples.Count > 2
            ? trackerSamples[2].Pose
            : HealthySample(3d).Pose;
        var verifier = new FakeVerificationProbe(
            events,
            () => new OneHandBridgeVerificationObservation(
                TouchDescriptor().StableDeviceId,
                VmtDescriptor().Identity.DevicePath,
                observedTrackerPose * Mount,
                IsDeferred: false,
                "Independent fake verification passed."));
        var coordinator = new OneHandBridgeCoordinator(
            runtime,
            vmt,
            overrides,
            verifier,
            delay,
            onActivated,
            timeProvider: clock);
        return new TestContext(
            coordinator,
            runtime,
            vmt,
            overrides,
            verifier,
            delay,
            clock);
    }

    private static BridgeProfile Profile() => new(
        BridgeProfileLoader.SupportedSchemaVersion,
        "Synthetic bridge profile",
        BridgeHand.Left,
        TouchDescriptor().StableDeviceId,
        TrackerDescriptor().StableDeviceId,
        BridgeCalibrationMode.Full6Dof,
        Mount);

    private static SteamVrDeviceDescriptor TrackerDescriptor(bool isConnected = true) => new(
        new SteamVrDeviceIdentity("LHR-TEST0001", "/devices/lighthouse/LHR-TEST0001"),
        3,
        SteamVrDeviceCategory.GenericTracker,
        SteamVrControllerRole.None,
        isConnected);

    private static SteamVrDeviceDescriptor TouchDescriptor(bool isConnected = true) => new(
        new SteamVrDeviceIdentity("TOUCH-TEST-LEFT", "/devices/alvr/TOUCH-TEST-LEFT"),
        4,
        SteamVrDeviceCategory.InputController,
        SteamVrControllerRole.LeftHand,
        isConnected);

    private static SteamVrDeviceDescriptor TouchDescriptorWithRole(
        SteamVrControllerRole role) => new(
        TouchDescriptor().Identity,
        TouchDescriptor().TransientDeviceIndex,
        SteamVrDeviceCategory.InputController,
        role,
        isConnected: true);

    private static SteamVrDeviceDescriptor VmtDescriptor(bool isConnected = false) => new(
        new SteamVrDeviceIdentity("VMT-TEST-SLOT-7", "/devices/vmt/VMT_7"),
        7,
        SteamVrDeviceCategory.GenericTracker,
        SteamVrControllerRole.None,
        isConnected);

    private static PoseSourceSample HealthySample(
        double monotonicSeconds,
        RigidTransform? pose = null) =>
        Sample(monotonicSeconds, pose: pose, ageSeconds: 0.01);

    private static PoseSourceSample Sample(
        double monotonicSeconds,
        bool connected = true,
        double? ageSeconds = 0.01,
        RigidTransform? pose = null) =>
        new(
            new TimestampedPoseSample(
                monotonicSeconds,
                pose ?? new RigidTransform(Quaternion.Identity, new Vector3(1f, 2f, 3f)),
                PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid),
            connected,
            PoseTrackingResult.RunningOk,
            sampleAgeSeconds: ageSeconds);

    private static PoseSourceSample InvalidTrackingSample(double monotonicSeconds) =>
        new(
            new TimestampedPoseSample(
                monotonicSeconds,
                new RigidTransform(Quaternion.Identity, new Vector3(1f, 2f, 3f)),
                PoseValidity.Orientation),
            isConnected: true,
            PoseTrackingResult.RunningOutOfRange,
            sampleAgeSeconds: 0.01);

    private static PoseSourceSample VmtOutputSample(PoseSourceSample trackerSample) =>
        new(
            new TimestampedPoseSample(
                trackerSample.MonotonicHostTimeSeconds,
                trackerSample.Pose * Mount,
                trackerSample.Validity),
            trackerSample.IsConnected,
            trackerSample.TrackingResult,
            trackerSample.RuntimeTimeSeconds,
            trackerSample.PredictionOffsetSeconds,
            trackerSample.SampleAgeSeconds);

    private static void AssertTransformClose(RigidTransform expected, RigidTransform actual)
    {
        Assert.InRange(Vector3.Distance(expected.TranslationMeters, actual.TranslationMeters), 0f, 1e-5f);
        Assert.True(
            MathF.Abs(Quaternion.Dot(expected.Rotation, actual.Rotation)) >= 0.99999f,
            "Rotations are not equivalent within the quaternion-dot tolerance.");
    }

    private static string ReadOscAddress(byte[] packet)
    {
        var terminator = Array.IndexOf(packet, (byte)0);
        return Encoding.UTF8.GetString(packet, 0, terminator);
    }

    private static int ReadJointSlot(byte[] packet)
    {
        var reader = new TestOscReader(packet);
        Assert.Equal(VmtOscProtocol.JointDriverAddress, reader.ReadString());
        _ = reader.ReadString();
        return reader.ReadInt32();
    }

    private static int ReadJointEnable(byte[] packet)
    {
        var reader = new TestOscReader(packet);
        _ = reader.ReadString();
        _ = reader.ReadString();
        _ = reader.ReadInt32();
        return reader.ReadInt32();
    }

    private sealed record TestContext(
        OneHandBridgeCoordinator Coordinator,
        FakeRuntime Runtime,
        FakeVmtController Vmt,
        FakeOverrideController Overrides,
        FakeVerificationProbe Verifier,
        FakeDelay Delay,
        BridgeManualTimeProvider Clock);
}

internal sealed class FakeRuntime : IOneHandBridgeRuntime
{
    private readonly IReadOnlyList<SteamVrDeviceDescriptor> _initialDevices;
    private readonly IReadOnlyList<SteamVrDeviceDescriptor> _monitorDevices;
    private IReadOnlyList<PoseSourceSample> _trackerSamples;
    private IReadOnlyList<PoseSourceSample> _vmtSamples;
    private int _enumerationCalls;

    public FakeRuntime(
        IReadOnlyList<SteamVrDeviceDescriptor> initialDevices,
        IReadOnlyList<PoseSourceSample> trackerSamples,
        IReadOnlyList<SteamVrDeviceDescriptor>? monitorDevices = null)
    {
        _initialDevices = initialDevices;
        _trackerSamples = trackerSamples;
        _vmtSamples = trackerSamples.Skip(2).Select(ToVmtOutputSample).ToArray();
        _monitorDevices = monitorDevices ?? initialDevices
            .Concat(
            [
                new SteamVrDeviceDescriptor(
                    new SteamVrDeviceIdentity(
                        "VMT-TEST-SLOT-7",
                        "/devices/vmt/VMT_7"),
                    7,
                    SteamVrDeviceCategory.GenericTracker,
                    SteamVrControllerRole.None,
                    isConnected: true),
            ])
            .ToArray();
    }

    public Func<int, IReadOnlyList<SteamVrDeviceDescriptor>>? EnumerationFactory { get; set; }

    public List<IReadOnlyList<SteamVrDeviceDescriptor>> EnumerationHistory { get; } = [];

    public IReadOnlyList<SteamVrDeviceDescriptor> EnumerateDevices()
    {
        var callIndex = _enumerationCalls++;
        var devices = EnumerationFactory?.Invoke(callIndex) ??
                      (callIndex < 2 ? _initialDevices : _monitorDevices);
        EnumerationHistory.Add(devices);
        return devices;
    }

    public TrackedPoseSource CreateTrackedPoseSource(SteamVrDeviceDescriptor device) =>
        new SimulatedTrackedPoseSource(
            device,
            VmtDeviceAddress.TryParse(device.Identity.DevicePath, out _)
                ? _vmtSamples
                : _trackerSamples);

    public void ReplaceTrackerSamples(IReadOnlyList<PoseSourceSample> trackerSamples) =>
        _trackerSamples = trackerSamples;

    public void ReplaceVmtSamples(IReadOnlyList<PoseSourceSample> vmtSamples) =>
        _vmtSamples = vmtSamples;

    private static PoseSourceSample ToVmtOutputSample(PoseSourceSample trackerSample) =>
        new(
            new TimestampedPoseSample(
                trackerSample.MonotonicHostTimeSeconds,
                trackerSample.Pose * new RigidTransform(
                    Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.25f),
                    new Vector3(0.02f, -0.04f, 0.03f)),
                trackerSample.Validity),
            trackerSample.IsConnected,
            trackerSample.TrackingResult,
            trackerSample.RuntimeTimeSeconds,
            trackerSample.PredictionOffsetSeconds,
            trackerSample.SampleAgeSeconds);
}

internal sealed class FakeVmtController : IOneHandBridgeVmtController
{
    private readonly List<string> _events;

    public FakeVmtController(List<string> events)
    {
        _events = events;
    }

    public bool Alive { get; set; } = true;

    public bool IsAlive => Alive;

    public Exception? ActivateFailure { get; set; }

    public Action? ActivateAction { get; set; }

    public Exception? DeactivateFailure { get; set; }

    public int DeactivateFailureOnCall { get; set; } = int.MaxValue;

    public int HangDeactivateOnCall { get; set; } = int.MaxValue;

    public int ActivateCalls { get; private set; }

    public int DeactivateCalls { get; private set; }

    public TimeSpan? WaitTimeout { get; private set; }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Add("vmt.start");
        return ValueTask.CompletedTask;
    }

    public ValueTask WaitForAliveAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WaitTimeout = timeout;
        _events.Add($"vmt.wait:{timeout.TotalSeconds:R}");
        if (!Alive)
        {
            throw new TimeoutException("synthetic missing heartbeat");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ActivateAsync(
        VmtDeviceConfiguration configuration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActivateCalls++;
        _events.Add($"vmt.activate:{configuration.FollowDeviceSerial}:{configuration.Device.Index}");
        ActivateAction?.Invoke();
        if (ActivateFailure is not null)
        {
            throw ActivateFailure;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeactivateAsync(
        VmtDeviceConfiguration configuration,
        CancellationToken cancellationToken)
    {
        DeactivateCalls++;
        _events.Add($"vmt.deactivate:{configuration.FollowDeviceSerial}:{configuration.Device.Index}");
        if (DeactivateCalls == HangDeactivateOnCall)
        {
            return new ValueTask(new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously).Task);
        }

        if (DeactivateFailure is not null &&
            DeactivateCalls == DeactivateFailureOnCall)
        {
            throw DeactivateFailure;
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeOverrideController : IOneHandBridgeOverrideController
{
    private readonly List<string> _events;

    public FakeOverrideController(List<string> events)
    {
        _events = events;
    }

    public Exception? EnableFailure { get; set; }

    public Exception? PrepareFailure { get; set; }

    public Exception? ReleaseFailure { get; set; }

    public int EnableCalls { get; private set; }

    public int PrepareCalls { get; private set; }

    public int ReleaseCalls { get; private set; }

    public bool IsEnabled { get; private set; }

    public void Prepare(TrackingOverrideBinding binding)
    {
        PrepareCalls++;
        _events.Add($"override.prepare:{binding.PoseSourceDevicePath}");
        if (PrepareFailure is not null)
        {
            throw PrepareFailure;
        }

        IsEnabled = false;
    }

    public void Enable(TrackingOverrideBinding binding)
    {
        EnableCalls++;
        _events.Add($"override.enable:{binding.PoseSourceDevicePath}");
        if (EnableFailure is not null)
        {
            throw EnableFailure;
        }

        IsEnabled = true;
    }

    public void Release(TrackingOverrideBinding binding)
    {
        ReleaseCalls++;
        _events.Add($"override.release:{binding.PoseSourceDevicePath}");
        if (ReleaseFailure is not null)
        {
            throw ReleaseFailure;
        }

        IsEnabled = false;
    }
}

internal sealed class RecordingOverrideController : IOneHandBridgeOverrideController
{
    private readonly IOneHandBridgeOverrideController _inner;

    public RecordingOverrideController(IOneHandBridgeOverrideController inner)
    {
        _inner = inner;
    }

    public int PrepareCalls { get; private set; }

    public int EnableCalls { get; private set; }

    public int ReleaseCalls { get; private set; }

    public void Prepare(TrackingOverrideBinding binding)
    {
        PrepareCalls++;
        _inner.Prepare(binding);
    }

    public void Enable(TrackingOverrideBinding binding)
    {
        EnableCalls++;
        _inner.Enable(binding);
    }

    public void Release(TrackingOverrideBinding binding)
    {
        ReleaseCalls++;
        _inner.Release(binding);
    }
}

internal sealed class FakeVerificationProbe : IOneHandBridgeVerificationProbe
{
    private readonly List<string> _events;
    private readonly Func<OneHandBridgeVerificationObservation> _observation;

    public FakeVerificationProbe(
        List<string> events,
        Func<OneHandBridgeVerificationObservation>? observation = null)
    {
        _events = events;
        _observation = observation ?? (() => new OneHandBridgeVerificationObservation(
            "TOUCH-TEST-LEFT",
            "/devices/vmt/VMT_7",
            new RigidTransform(Quaternion.Identity, new Vector3(1f, 2f, 3f)) *
            new RigidTransform(
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.25f),
                new Vector3(0.02f, -0.04f, 0.03f)),
            IsDeferred: false,
            "Independent fake verification passed."));
    }

    public Func<OneHandBridgeVerificationRequest, OneHandBridgeVerificationObservation>?
        ObservationFactory
    { get; set; }

    public Func<
        OneHandBridgeVerificationRequest,
        CancellationToken,
        ValueTask<OneHandBridgeVerificationObservation>>? AsyncObservationFactory
    { get; set; }

    public OneHandBridgeVerificationRequest? LastRequest { get; private set; }

    public ValueTask<OneHandBridgeVerificationObservation> ObserveAsync(
        OneHandBridgeVerificationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastRequest = request;
        _events.Add("verify");
        if (AsyncObservationFactory is not null)
        {
            return AsyncObservationFactory(request, cancellationToken);
        }

        var observation = ObservationFactory?.Invoke(request) ??
            _observation();
        return ValueTask.FromResult(observation);
    }
}

internal sealed class EffectBackedVerificationProbe : IOneHandBridgeVerificationProbe
{
    private readonly RecordingVmtTransport _transport;
    private readonly string _settingsPath;
    private readonly SteamVrDeviceDescriptor _inputController;
    private readonly RigidTransform _trackerPose;

    public EffectBackedVerificationProbe(
        RecordingVmtTransport transport,
        string settingsPath,
        SteamVrDeviceDescriptor inputController,
        RigidTransform trackerPose)
    {
        _transport = transport;
        _settingsPath = settingsPath;
        _inputController = inputController;
        _trackerPose = trackerPose;
    }

    public OneHandBridgeVerificationRequest? LastRequest { get; private set; }

    public ValueTask<OneHandBridgeVerificationObservation> ObserveAsync(
        OneHandBridgeVerificationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastRequest = request;

        using var settings = JsonDocument.Parse(File.ReadAllBytes(_settingsPath));
        var activePoseSources = settings.RootElement
            .GetProperty("TrackingOverrides")
            .EnumerateObject()
            .Where(property => string.Equals(
                property.Value.GetString(),
                TrackingOverrideBinding.LeftHandPath,
                StringComparison.Ordinal))
            .Select(property => property.Name)
            .ToArray();
        if (activePoseSources.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected one active left-hand fake override; found {activePoseSources.Length}.");
        }

        var enabledMount = ReadLatestEnabledMount(_transport.Sent);
        var outputPose = CoordinateConventions.ComposeRuntimeOutput(
            _trackerPose,
            enabledMount);
        return ValueTask.FromResult(new OneHandBridgeVerificationObservation(
            _inputController.StableDeviceId,
            activePoseSources[0],
            outputPose,
            IsDeferred: false,
            "Observed fake input, active settings mapping, and OSC-configured output pose."));
    }

    private static RigidTransform ReadLatestEnabledMount(IReadOnlyList<byte[]> packets)
    {
        for (var index = packets.Count - 1; index >= 0; index--)
        {
            if (!string.Equals(
                    ReadAddress(packets[index]),
                    VmtOscProtocol.JointDriverAddress,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var reader = new TestOscReader(packets[index]);
            _ = reader.ReadString();
            _ = reader.ReadString();
            _ = reader.ReadInt32();
            var mode = reader.ReadInt32();
            _ = reader.ReadSingle();
            var position = new Vector3(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle());
            var rotation = new Quaternion(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle());
            _ = reader.ReadString();
            if (mode != 0)
            {
                return VmtTransformConvention.FromVmtDriverLocal(
                    new VmtDriverLocalTransform(position, rotation));
            }
        }

        throw new InvalidOperationException(
            "No enabled /VMT/Joint/Driver packet was observed by the fake verifier.");
    }

    private static string ReadAddress(byte[] packet)
    {
        var terminator = Array.IndexOf(packet, (byte)0);
        return terminator < 0
            ? string.Empty
            : Encoding.UTF8.GetString(packet, 0, terminator);
    }
}

internal sealed class FakeDelay : IOneHandBridgeDelay
{
    private readonly List<string> _events;
    private readonly Action? _action;
    private readonly BridgeManualTimeProvider? _clock;

    public FakeDelay(
        List<string> events,
        Action? action,
        BridgeManualTimeProvider? clock = null)
    {
        _events = events;
        _action = action;
        _clock = clock;
    }

    public List<TimeSpan> Delays { get; } = [];

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        _events.Add("delay");
        Delays.Add(delay);
        _clock?.Advance(delay);
        _action?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

internal sealed class BridgeManualTimeProvider : TimeProvider
{
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
}

internal sealed class OscBackedVmtController : IOneHandBridgeVmtController
{
    private readonly VmtClient _client;

    public OscBackedVmtController(VmtClient client)
    {
        _client = client;
    }

    public bool IsAlive => _client.DriverHealth.IsAlive;

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _client.ObserveDriverDatagram(OneHandBridgeOscTestData.EncodeAlive());
        return ValueTask.CompletedTask;
    }

    public ValueTask WaitForAliveAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAlive)
        {
            throw new TimeoutException("Fake VMT heartbeat is stale.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ActivateAsync(
        VmtDeviceConfiguration configuration,
        CancellationToken cancellationToken) =>
        _client.ActivateAsync(configuration, cancellationToken);

    public ValueTask DeactivateAsync(
        VmtDeviceConfiguration configuration,
        CancellationToken cancellationToken) =>
        _client.DeactivateAsync(configuration, cancellationToken);
}

internal sealed class RecordingVmtTransport : IVmtDatagramTransport
{
    public List<byte[]> Sent { get; } = [];

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> datagram,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Sent.Add(datagram.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask<VmtReceivedDatagram> ReceiveAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal static class OneHandBridgeOscTestData
{
    public static byte[] EncodeAlive()
    {
        using var stream = new MemoryStream();
        WriteString(stream, VmtOscProtocol.AliveAddress);
        WriteString(stream, VmtOscProtocol.AliveTypeTags);
        WriteString(stream, "0.15-test");
        WriteString(stream, "C:/VMT-TEST");
        return stream.ToArray();
    }

    private static void WriteString(Stream stream, string value)
    {
        stream.Write(Encoding.UTF8.GetBytes(value));
        stream.WriteByte(0);
        while (stream.Position % 4 != 0)
        {
            stream.WriteByte(0);
        }
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"ltb-one-hand-bridge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
