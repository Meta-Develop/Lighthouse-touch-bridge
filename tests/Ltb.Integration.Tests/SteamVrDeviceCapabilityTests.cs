using Ltb.OpenVr;

namespace Ltb.Integration.Tests;

public sealed class SteamVrDeviceCapabilityTests
{
    [Theory]
    [InlineData("VIVE-LHR", "Vive Tracker 3.0")]
    [InlineData("TUNDRA-LHR", "Tundra Tracker")]
    [InlineData("GENERIC-LHR", "Generic Lighthouse Tracked Device")]
    public void PhysicalGenericTrackersAreSelectedByCapability(
        string serial,
        string model)
    {
        var device = Descriptor(
            serial,
            $"/devices/lighthouse/{serial}",
            SteamVrDeviceCategory.GenericTracker,
            isConnected: true,
            new SteamVrDeviceMetadata(
                "lighthouse",
                "lighthouse",
                "Tracked Device Vendor",
                model,
                controllerType: null));

        Assert.True(device.Capabilities.HasPosition);
        Assert.True(device.Capabilities.IsPhysicalPoseSourceEligible);
        Assert.False(device.Capabilities.IsVirtualPoseSource);
        Assert.True(device.CanUseAsPhysicalPoseSource);
        Assert.Equal(SteamVrControllerFamily.None, device.Capabilities.ControllerFamily);
    }

    [Fact]
    public void ConnectionStateIsRequiredInAdditionToPhysicalCapability()
    {
        var disconnected = Descriptor(
            "TUNDRA-DISCONNECTED",
            "/devices/lighthouse/TUNDRA-DISCONNECTED",
            SteamVrDeviceCategory.GenericTracker,
            isConnected: false);

        Assert.True(disconnected.Capabilities.IsPhysicalPoseSourceEligible);
        Assert.False(disconnected.CanUseAsPhysicalPoseSource);
    }

    [Fact]
    public void VmtOutputIsVirtualAndNeverEligibleAsPhysicalPoseSource()
    {
        var vmt = Descriptor(
            "VMT-1",
            "/devices/vmt/VMT_1",
            SteamVrDeviceCategory.GenericTracker,
            isConnected: true);

        Assert.True(vmt.Capabilities.HasPosition);
        Assert.True(vmt.Capabilities.IsVirtualPoseSource);
        Assert.False(vmt.Capabilities.IsPhysicalPoseSourceEligible);
        Assert.False(vmt.CanUseAsPhysicalPoseSource);
    }

    [Theory]
    [InlineData("Beyond-2e", "Bigscreen Beyond 2e")]
    [InlineData("INDEX-HMD", "Valve Index")]
    [InlineData("VIVE-PRO-HMD", "HTC Vive Pro 2")]
    public void LighthouseHmdFamiliesUseClassAndPositionCapabilityNotModelBranches(
        string serial,
        string model)
    {
        var hmd = Descriptor(
            serial,
            $"/devices/lighthouse/{serial}",
            SteamVrDeviceCategory.HeadMountedDisplay,
            isConnected: true,
            new SteamVrDeviceMetadata(
                "lighthouse",
                "lighthouse",
                "HMD Vendor",
                model,
                controllerType: null));

        Assert.Equal(SteamVrDeviceCategory.HeadMountedDisplay, hmd.Category);
        Assert.True(hmd.Capabilities.HasPosition);
        Assert.False(hmd.Capabilities.IsVirtualPoseSource);
        Assert.False(hmd.Capabilities.IsPhysicalPoseSourceEligible);
    }

    [Fact]
    public void ExplicitPositionUnavailabilityPreventsPhysicalSelection()
    {
        var noPosition = Descriptor(
            "NO-POSITION",
            "/devices/lighthouse/NO-POSITION",
            SteamVrDeviceCategory.GenericTracker,
            isConnected: true,
            metadata: null,
            capabilities: new SteamVrDeviceCapabilities(
                hasPosition: false,
                isPhysicalPoseSourceEligible: true,
                isVirtualPoseSource: false));

        Assert.False(noPosition.CanUseAsPhysicalPoseSource);
        Assert.Throws<ArgumentException>(() => new SimulatedTrackedPoseSource(
            noPosition,
            []));
    }

    [Fact]
    public void CapabilityContractRejectsPhysicalVirtualContradiction()
    {
        Assert.Throws<ArgumentException>(() => new SteamVrDeviceCapabilities(
            hasPosition: true,
            isPhysicalPoseSourceEligible: true,
            isVirtualPoseSource: true));
    }

    private static SteamVrDeviceDescriptor Descriptor(
        string serial,
        string path,
        SteamVrDeviceCategory category,
        bool isConnected,
        SteamVrDeviceMetadata? metadata = null,
        SteamVrDeviceCapabilities? capabilities = null) =>
        new(
            new SteamVrDeviceIdentity(serial, path),
            7,
            category,
            SteamVrControllerRole.None,
            isConnected,
            metadata,
            capabilities);
}
