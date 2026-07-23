using Ltb.OpenVr;

namespace Ltb.Integration.Tests;

public sealed class ActiveHmdReadinessTests
{
    [Fact]
    public void ConnectedLighthouseHmdAtIndexZeroPassesWithoutModelAllowlist()
    {
        var devices = new[]
        {
            Hmd(
                index: 0,
                driver: "vendor-neutral-driver",
                trackingSystem: "Lighthouse",
                manufacturer: "Future HMD Vendor",
                model: "Unlisted HMD 9000"),
        };

        var result = ActiveHmdReadiness.Evaluate(devices);

        Assert.True(result.IsReady);
        Assert.Contains("OpenVR index 0", result.Diagnostic);
        Assert.Contains("Lighthouse", result.Diagnostic);
    }

    [Fact]
    public void ConnectedLighthouseHmdWithUnavailableDriverPassesOnTrackingSystemEvidence()
    {
        var result = ActiveHmdReadiness.Evaluate(
        [
            Descriptor(
                0,
                SteamVrDeviceCategory.HeadMountedDisplay,
                metadata: new SteamVrDeviceMetadata(
                    driverId: null,
                    trackingSystemName: "lighthouse",
                    manufacturerName: "Bigscreen",
                    modelNumber: "Beyond 2e",
                    controllerType: null)),
        ]);

        Assert.True(result.IsReady, result.Diagnostic);
    }

    [Fact]
    public void VendorPrimaryWithLighthouseActualTrackingEvidencePasses()
    {
        var metadata = OpenVrDeviceMetadataComposer.Compose(
            "openvr://device/HMD-BEYOND",
            trackingSystemName: "vendor_tracking",
            actualTrackingSystemName: "lighthouse",
            manufacturerName: "Bigscreen",
            modelNumber: "Beyond 2e",
            controllerType: null,
            inputProfilePath: null,
            driverVersion: "vendor-build");

        var result = ActiveHmdReadiness.Evaluate(
            [Descriptor(0, SteamVrDeviceCategory.HeadMountedDisplay, metadata: metadata)]);

        Assert.True(result.IsReady, result.Diagnostic);
    }

    [Fact]
    public void QuestAlvrHmdAtIndexZeroFailsEvenWhenLighthouseHmdExistsElsewhere()
    {
        var devices = new[]
        {
            Hmd(0, "alvr_server", "oculus", "Meta", "Quest 2"),
            Hmd(7, "lighthouse", "lighthouse", "Future Vendor", "LH HMD"),
        };

        var result = ActiveHmdReadiness.Evaluate(devices);

        Assert.False(result.IsReady);
        AssertRemediation(result);
        Assert.Contains("Quest/ALVR/Meta/Oculus", result.Diagnostic);
    }

    [Fact]
    public void NonActiveAlvrDeviceDoesNotPoisonValidIndexZeroLighthouseHmd()
    {
        var devices = new[]
        {
            Hmd(0, "lighthouse", "lighthouse", "Future Vendor", "LH HMD"),
            Hmd(9, "alvr_server", "oculus", "Meta", "Quest 2"),
        };

        var result = ActiveHmdReadiness.Evaluate(devices);

        Assert.True(result.IsReady);
    }

    [Fact]
    public void MissingIndexZeroFailsClosed()
    {
        var result = ActiveHmdReadiness.Evaluate(
            [Hmd(4, "lighthouse", "lighthouse", "Vendor", "HMD")]);

        Assert.False(result.IsReady);
        Assert.Contains("did not report a device", result.Diagnostic);
        AssertRemediation(result);
    }

    [Fact]
    public void DisconnectedIndexZeroHmdFailsClosed()
    {
        var result = ActiveHmdReadiness.Evaluate(
            [Hmd(0, "lighthouse", "lighthouse", "Vendor", "HMD", connected: false)]);

        Assert.False(result.IsReady);
        Assert.Contains("disconnected", result.Diagnostic);
        AssertRemediation(result);
    }

    [Fact]
    public void WrongClassAtIndexZeroFailsClosed()
    {
        var result = ActiveHmdReadiness.Evaluate(
            [Descriptor(
                0,
                SteamVrDeviceCategory.GenericTracker,
                "lighthouse",
                "lighthouse")]);

        Assert.False(result.IsReady);
        Assert.Contains("not HeadMountedDisplay", result.Diagnostic);
        AssertRemediation(result);
    }

    [Fact]
    public void MissingOrUnknownRuntimeEvidenceFailsClosed()
    {
        var missing = ActiveHmdReadiness.Evaluate(
            [Descriptor(0, SteamVrDeviceCategory.HeadMountedDisplay)]);
        var unknown = ActiveHmdReadiness.Evaluate(
            [Hmd(0, "future_driver", "future_tracking", "Vendor", "HMD")]);

        Assert.False(missing.IsReady);
        Assert.Contains("no driver or tracking-system metadata", missing.Diagnostic);
        Assert.False(unknown.IsReady);
        Assert.Contains("does not report positive Lighthouse", unknown.Diagnostic);
        AssertRemediation(missing);
        AssertRemediation(unknown);
    }

    [Fact]
    public void UnavailableDriverWithMissingOrUnknownTrackingFailsClosed()
    {
        var missingTracking = ActiveHmdReadiness.Evaluate(
        [
            Descriptor(
                0,
                SteamVrDeviceCategory.HeadMountedDisplay,
                metadata: new SteamVrDeviceMetadata(null, null, "Vendor", "HMD", null)),
        ]);
        var unknownTracking = ActiveHmdReadiness.Evaluate(
        [
            Descriptor(
                0,
                SteamVrDeviceCategory.HeadMountedDisplay,
                metadata: new SteamVrDeviceMetadata(
                    null,
                    "future_tracking",
                    "Vendor",
                    "HMD",
                    null)),
        ]);

        Assert.False(missingTracking.IsReady);
        Assert.Contains("driver='unavailable'", missingTracking.Diagnostic);
        Assert.False(unknownTracking.IsReady);
        Assert.Contains("positive Lighthouse", unknownTracking.Diagnostic);
        AssertRemediation(missingTracking);
        AssertRemediation(unknownTracking);
    }

    [Fact]
    public void DisallowedPrimaryOrActualTrackingEvidenceVetoesHmd()
    {
        var disallowedPrimary = OpenVrDeviceMetadataComposer.Compose(
            "openvr://device/HMD-BEYOND",
            trackingSystemName: "Oculus",
            actualTrackingSystemName: "lighthouse",
            manufacturerName: "Bigscreen",
            modelNumber: "Beyond 2e",
            controllerType: null,
            inputProfilePath: null,
            driverVersion: null);
        var disallowedActual = OpenVrDeviceMetadataComposer.Compose(
            "/devices/lighthouse/HMD-BEYOND",
            trackingSystemName: "lighthouse",
            actualTrackingSystemName: "Oculus",
            manufacturerName: "Bigscreen",
            modelNumber: "Beyond 2e",
            controllerType: null,
            inputProfilePath: null,
            driverVersion: null);

        var primaryResult = ActiveHmdReadiness.Evaluate(
        [
            Descriptor(
                0,
                SteamVrDeviceCategory.HeadMountedDisplay,
                metadata: disallowedPrimary),
        ]);
        var actualResult = ActiveHmdReadiness.Evaluate(
        [
            Descriptor(
                0,
                SteamVrDeviceCategory.HeadMountedDisplay,
                metadata: disallowedActual),
        ]);

        Assert.False(primaryResult.IsReady);
        Assert.Contains("Quest/ALVR/Meta/Oculus", primaryResult.Diagnostic);
        Assert.Contains("tracking_system='Oculus'", primaryResult.Diagnostic);
        Assert.False(actualResult.IsReady);
        Assert.Contains("Quest/ALVR/Meta/Oculus", actualResult.Diagnostic);
        Assert.Contains("actual_tracking_system='Oculus'", actualResult.Diagnostic);
        AssertRemediation(primaryResult);
        AssertRemediation(actualResult);
    }

    [Fact]
    public void ConflictingAndDuplicateIndexZeroEvidenceFailsClosed()
    {
        var conflicting = ActiveHmdReadiness.Evaluate(
            [Hmd(0, "lighthouse", "ALVR", "Vendor", "HMD")]);
        var conflictingVersion = ActiveHmdReadiness.Evaluate(
            [Hmd(
                0,
                "lighthouse",
                "lighthouse",
                "Vendor",
                "HMD",
                driverVersion: "ALVR-display-build")]);
        var duplicate = ActiveHmdReadiness.Evaluate(
        [
            Hmd(0, "lighthouse", "lighthouse", "Vendor A", "HMD A"),
            Hmd(0, "lighthouse", "lighthouse", "Vendor B", "HMD B"),
        ]);

        Assert.False(conflicting.IsReady);
        Assert.Contains("Quest/ALVR/Meta/Oculus", conflicting.Diagnostic);
        Assert.False(conflictingVersion.IsReady);
        Assert.Contains("ALVR-display-build", conflictingVersion.Diagnostic);
        Assert.False(duplicate.IsReady);
        Assert.Contains("multiple devices", duplicate.Diagnostic);
        AssertRemediation(conflicting);
        AssertRemediation(conflictingVersion);
        AssertRemediation(duplicate);
    }

    private static SteamVrDeviceDescriptor Hmd(
        uint index,
        string driver,
        string trackingSystem,
        string manufacturer,
        string model,
        bool connected = true,
        string? driverVersion = null) =>
        Descriptor(
            index,
            SteamVrDeviceCategory.HeadMountedDisplay,
            driver,
            trackingSystem,
            manufacturer,
            model,
            connected,
            driverVersion);

    private static SteamVrDeviceDescriptor Descriptor(
        uint index,
        SteamVrDeviceCategory category,
        string? driver = null,
        string? trackingSystem = null,
        string manufacturer = "Vendor",
        string model = "Device",
        bool connected = true,
        string? driverVersion = null,
        SteamVrDeviceMetadata? metadata = null)
    {
        var driverForPath = driver ?? "unknown";
        return new SteamVrDeviceDescriptor(
            new SteamVrDeviceIdentity(
                $"DEVICE-{index}",
                $"/devices/{driverForPath}/DEVICE-{index}"),
            index,
            category,
            SteamVrControllerRole.None,
            connected,
            metadata ?? (driver is null
                ? null
                : new SteamVrDeviceMetadata(
                    driver,
                    trackingSystem,
                    manufacturer,
                    model,
                    controllerType: null,
                    inputProfilePath: null,
                    driverVersion: driverVersion)));
    }

    private static void AssertRemediation(ActiveHmdReadinessResult result)
    {
        Assert.Contains("intended Lighthouse HMD", result.Diagnostic);
        Assert.Contains("sole active SteamVR display HMD", result.Diagnostic);
        Assert.Contains("exclude Quest/Meta/Oculus/ALVR", result.Diagnostic);
        Assert.DoesNotContain("tracking-reference-only", result.Diagnostic);
        Assert.DoesNotContain("Configure ALVR", result.Diagnostic);
    }
}
