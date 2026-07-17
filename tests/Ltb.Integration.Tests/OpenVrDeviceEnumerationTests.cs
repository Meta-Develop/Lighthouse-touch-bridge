using Ltb.OpenVr;

namespace Ltb.Integration.Tests;

public sealed class OpenVrDeviceEnumerationTests
{
    [Fact]
    public void AdapterMapsAndSortsDevicesByStableSerial()
    {
        using var runtime = new FakeOpenVrRuntime(
            devices:
            [
                new OpenVrRuntimeDevice(
                    9,
                    "tracker-z",
                    "lighthouse/vive_tracker",
                    OpenVrRuntimeDeviceClass.GenericTracker,
                    OpenVrRuntimeControllerRole.None,
                    true),
                new OpenVrRuntimeDevice(
                    3,
                    "controller-a",
                    "oculus/controller",
                    OpenVrRuntimeDeviceClass.Controller,
                    OpenVrRuntimeControllerRole.LeftHand,
                    false),
            ]);
        var enumerator = new OpenVrDeviceEnumeratorAdapter(runtime);

        var devices = enumerator.EnumerateDevices();

        Assert.Collection(
            devices,
            controller =>
            {
                Assert.Equal("controller-a", controller.StableDeviceId);
                Assert.Equal("oculus/controller", controller.Identity.DevicePath);
                Assert.Equal(3u, controller.TransientDeviceIndex);
                Assert.Equal(SteamVrDeviceCategory.InputController, controller.Category);
                Assert.Equal(SteamVrControllerRole.LeftHand, controller.ControllerRole);
                Assert.False(controller.IsConnected);
            },
            tracker =>
            {
                Assert.Equal("tracker-z", tracker.StableDeviceId);
                Assert.Equal("lighthouse/vive_tracker", tracker.Identity.DevicePath);
                Assert.Equal(9u, tracker.TransientDeviceIndex);
                Assert.Equal(SteamVrDeviceCategory.GenericTracker, tracker.Category);
                Assert.Equal(SteamVrControllerRole.None, tracker.ControllerRole);
                Assert.True(tracker.IsConnected);
            });
    }

    [Fact]
    public void TransientIndexIsNotPartOfPhysicalIdentity()
    {
        var first = Descriptor("tracker-serial", 4);
        var reconnected = Descriptor("tracker-serial", 17);

        Assert.NotEqual(first.TransientDeviceIndex, reconnected.TransientDeviceIndex);
        Assert.Equal(first.Identity, reconnected.Identity);
        Assert.Equal(first.StableDeviceId, reconnected.StableDeviceId);
        Assert.True(first.IsSamePhysicalDeviceAs(reconnected));
    }

    [Fact]
    public void SimulatedEnumerationIsDeterministicAndRejectsDuplicateSerials()
    {
        var trackerB = Descriptor("tracker-b", 1);
        var trackerA = Descriptor("tracker-a", 12);
        var enumerator = new SimulatedSteamVrDeviceEnumerator([trackerB, trackerA]);

        Assert.Equal(
            ["tracker-a", "tracker-b"],
            enumerator.EnumerateDevices().Select(device => device.StableDeviceId));
        Assert.Same(enumerator.EnumerateDevices(), enumerator.EnumerateDevices());

        var duplicate = Descriptor("tracker-a", 28);
        Assert.Throws<ArgumentException>(
            () => new SimulatedSteamVrDeviceEnumerator([trackerA, duplicate]));
    }

    [Fact]
    public void RuntimeAdapterRejectsDuplicateSerialsRatherThanUsingIndexesAsIdentity()
    {
        using var runtime = new FakeOpenVrRuntime(
            devices:
            [
                RuntimeTracker("duplicate", 2),
                RuntimeTracker("duplicate", 18),
            ]);
        var enumerator = new OpenVrDeviceEnumeratorAdapter(runtime);

        var exception = Assert.Throws<InvalidOperationException>(enumerator.EnumerateDevices);

        Assert.Contains("duplicate serial", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static SteamVrDeviceDescriptor Descriptor(string serial, uint index) =>
        new(
            new SteamVrDeviceIdentity(serial, $"lighthouse/{serial}"),
            index,
            SteamVrDeviceCategory.GenericTracker,
            SteamVrControllerRole.None,
            true);

    private static OpenVrRuntimeDevice RuntimeTracker(string serial, uint index) =>
        new(
            index,
            serial,
            $"lighthouse/{serial}",
            OpenVrRuntimeDeviceClass.GenericTracker,
            OpenVrRuntimeControllerRole.None,
            true);
}
