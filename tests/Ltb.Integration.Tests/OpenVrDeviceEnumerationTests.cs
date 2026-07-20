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
                    "/devices/lighthouse/vive_tracker",
                    OpenVrRuntimeDeviceClass.GenericTracker,
                    OpenVrRuntimeControllerRole.None,
                    true),
                new OpenVrRuntimeDevice(
                    3,
                    "controller-a",
                    "/devices/oculus/controller",
                    OpenVrRuntimeDeviceClass.Controller,
                    OpenVrRuntimeControllerRole.LeftHand,
                    false,
                    new SteamVrDeviceMetadata(
                        "oculus",
                        "Oculus",
                        "Oculus",
                        "Miramar (Left Controller)",
                        "oculus_touch",
                        "/drivers/oculus/resources/input/oculus_touch_profile.json",
                        "driver-build-42")),
            ]);
        var enumerator = new OpenVrDeviceEnumeratorAdapter(runtime);

        var devices = enumerator.EnumerateDevices();

        Assert.Collection(
            devices,
            controller =>
            {
                Assert.Equal("controller-a", controller.StableDeviceId);
                Assert.Equal("/devices/oculus/controller", controller.Identity.DevicePath);
                Assert.Equal(3u, controller.TransientDeviceIndex);
                Assert.Equal(SteamVrDeviceCategory.InputController, controller.Category);
                Assert.Equal(SteamVrControllerRole.LeftHand, controller.ControllerRole);
                Assert.False(controller.IsConnected);
                Assert.Equal("oculus", controller.Metadata?.DriverId);
                Assert.Equal("Oculus", controller.Metadata?.TrackingSystemName);
                Assert.Equal("Miramar (Left Controller)", controller.Metadata?.ModelNumber);
                Assert.Equal("oculus_touch", controller.Metadata?.ControllerType);
                Assert.Equal(
                    "/drivers/oculus/resources/input/oculus_touch_profile.json",
                    controller.Metadata?.InputProfilePath);
                Assert.Equal("driver-build-42", controller.Metadata?.DriverVersion);
                Assert.True(controller.Capabilities.HasPosition);
                Assert.Equal(
                    SteamVrControllerFamily.MetaTouch,
                    controller.Capabilities.ControllerFamily);
                Assert.Equal("ALVR", controller.Capabilities.ControllerRuntime);
                Assert.Equal("Quest 2 Touch", controller.Capabilities.ControllerModel);
            },
            tracker =>
            {
                Assert.Equal("tracker-z", tracker.StableDeviceId);
                Assert.Equal(
                    "/devices/lighthouse/vive_tracker",
                    tracker.Identity.DevicePath);
                Assert.Equal(9u, tracker.TransientDeviceIndex);
                Assert.Equal(SteamVrDeviceCategory.GenericTracker, tracker.Category);
                Assert.Equal(SteamVrControllerRole.None, tracker.ControllerRole);
                Assert.True(tracker.IsConnected);
                Assert.True(tracker.CanUseAsPhysicalPoseSource);
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
    public void LegacyExtendedAndVersionedPublicConstructorAritiesRemainAvailable()
    {
        Assert.NotNull(typeof(SteamVrDeviceMetadata).GetConstructor(
        [
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
        ]));
        Assert.NotNull(typeof(SteamVrDeviceMetadata).GetConstructor(
        [
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
        ]));
        Assert.NotNull(typeof(SteamVrDeviceMetadata).GetConstructor(
        [
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
        ]));
        Assert.NotNull(typeof(SteamVrDeviceDescriptor).GetConstructor(
        [
            typeof(SteamVrDeviceIdentity),
            typeof(uint),
            typeof(SteamVrDeviceCategory),
            typeof(SteamVrControllerRole),
            typeof(bool),
            typeof(SteamVrDeviceMetadata),
        ]));
        Assert.NotNull(typeof(SteamVrDeviceDescriptor).GetConstructor(
        [
            typeof(SteamVrDeviceIdentity),
            typeof(uint),
            typeof(SteamVrDeviceCategory),
            typeof(SteamVrControllerRole),
            typeof(bool),
            typeof(SteamVrDeviceMetadata),
            typeof(SteamVrDeviceCapabilities),
        ]));

        var legacyMetadata = new SteamVrDeviceMetadata(
            "lighthouse",
            "lighthouse",
            "Tracker Vendor",
            "Generic Tracker",
            controllerType: null);
        var extendedMetadata = new SteamVrDeviceMetadata(
            "lighthouse",
            "lighthouse",
            "Tracker Vendor",
            "Generic Tracker",
            controllerType: null,
            inputProfilePath: null);
        var versionedMetadata = new SteamVrDeviceMetadata(
            "lighthouse",
            "lighthouse",
            "Tracker Vendor",
            "Generic Tracker",
            controllerType: null,
            inputProfilePath: null,
            driverVersion: "  build-identity-42  ");
        var identity = new SteamVrDeviceIdentity(
            "TRACKER-COMPAT",
            "/devices/lighthouse/TRACKER-COMPAT");
        var defaultMetadataDescriptor = new SteamVrDeviceDescriptor(
            identity,
            7,
            SteamVrDeviceCategory.GenericTracker,
            SteamVrControllerRole.None,
            isConnected: true);
        var legacyDescriptor = new SteamVrDeviceDescriptor(
            identity,
            7,
            SteamVrDeviceCategory.GenericTracker,
            SteamVrControllerRole.None,
            isConnected: true,
            legacyMetadata);
        var extendedDescriptor = new SteamVrDeviceDescriptor(
            identity,
            7,
            SteamVrDeviceCategory.GenericTracker,
            SteamVrControllerRole.None,
            isConnected: true,
            extendedMetadata,
            capabilities: null);

        Assert.Null(defaultMetadataDescriptor.Metadata);
        Assert.Equal(legacyMetadata, extendedMetadata);
        Assert.Null(legacyMetadata.DriverVersion);
        Assert.Null(extendedMetadata.DriverVersion);
        Assert.Equal("build-identity-42", versionedMetadata.DriverVersion);
        Assert.Equal(legacyDescriptor.Metadata, extendedDescriptor.Metadata);
        Assert.Equal(legacyDescriptor.Capabilities, extendedDescriptor.Capabilities);
        Assert.True(legacyDescriptor.CanUseAsPhysicalPoseSource);
        Assert.True(extendedDescriptor.CanUseAsPhysicalPoseSource);
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
            new SteamVrDeviceIdentity(serial, $"/devices/lighthouse/{serial}"),
            index,
            SteamVrDeviceCategory.GenericTracker,
            SteamVrControllerRole.None,
            true);

    private static OpenVrRuntimeDevice RuntimeTracker(string serial, uint index) =>
        new(
            index,
            serial,
            $"/devices/lighthouse/{serial}",
            OpenVrRuntimeDeviceClass.GenericTracker,
            OpenVrRuntimeControllerRole.None,
            true);
}
