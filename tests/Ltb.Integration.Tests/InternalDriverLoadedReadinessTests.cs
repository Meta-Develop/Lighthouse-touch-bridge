using Ltb.App;
using Ltb.OpenVr;

namespace Ltb.Integration.Tests;

public sealed class InternalDriverLoadedReadinessTests
{
    private const string BuildIdentity = "driver-ltb-test-build-42";

    [Fact]
    public void ExactLoadedBuildControllersAndActiveLighthouseHmdAreReady()
    {
        var result = InternalDriverLoadedReadiness.Evaluate(
            ReadyDevices(),
            BuildIdentity);

        Assert.True(result.IsReady, result.Diagnostic);
        Assert.Empty(result.Diagnostics);
        Assert.Contains(BuildIdentity, result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains(
            InternalDriverLoadedReadiness.LeftControllerSerial,
            result.Diagnostic,
            StringComparison.Ordinal);
        Assert.Contains(
            InternalDriverLoadedReadiness.RightControllerSerial,
            result.Diagnostic,
            StringComparison.Ordinal);
        Assert.Contains("OpenVR index 0", result.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingStagedBuildMarkerFailsClosed(string? stagedBuildIdentity)
    {
        var result = InternalDriverLoadedReadiness.Evaluate(
            ReadyDevices(),
            stagedBuildIdentity);

        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.StagedBuildIdentityMissing);
        Assert.Contains("build-id.txt", result.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(InternalDriverLoadedReadiness.LeftControllerSerial)]
    [InlineData(InternalDriverLoadedReadiness.RightControllerSerial)]
    public void MissingEitherRequiredSerialFailsClosed(string missingSerial)
    {
        var result = InternalDriverLoadedReadiness.Evaluate(
            ReadyDevices()
                .Where(device => device.Identity.SerialNumber != missingSerial)
                .ToArray(),
            BuildIdentity);

        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.RequiredControllerMissing,
            missingSerial);
        Assert.Contains("restart SteamVR", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateRequiredSerialFailsClosed()
    {
        var devices = ReadyDevices().ToList();
        devices.Add(LtbController(
            InternalDriverLoadedReadiness.LeftControllerSerial,
            SteamVrControllerRole.LeftHand,
            index: 12));

        var result = InternalDriverLoadedReadiness.Evaluate(devices, BuildIdentity);

        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.DuplicateControllerIdentity,
            InternalDriverLoadedReadiness.LeftControllerSerial);
        Assert.Contains("2 devices", result.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        InternalDriverLoadedReadiness.LeftControllerSerial,
        SteamVrControllerRole.LeftHand)]
    [InlineData(
        InternalDriverLoadedReadiness.RightControllerSerial,
        SteamVrControllerRole.RightHand)]
    public void DisconnectedRequiredControllerFailsClosed(
        string serial,
        SteamVrControllerRole role)
    {
        var result = EvaluateWithReplacement(
            LtbController(serial, role, isConnected: false));

        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.ControllerDisconnected,
            serial);
    }

    [Fact]
    public void RequiredSerialWithWrongDeviceClassFailsClosed()
    {
        var serial = InternalDriverLoadedReadiness.LeftControllerSerial;
        var replacement = Descriptor(
            serial,
            1,
            SteamVrDeviceCategory.GenericTracker,
            SteamVrControllerRole.None,
            true,
            LtbMetadata());

        var result = EvaluateWithReplacement(replacement);

        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.ControllerCategoryMismatch,
            serial);
    }

    [Theory]
    [InlineData(
        InternalDriverLoadedReadiness.LeftControllerSerial,
        SteamVrControllerRole.RightHand)]
    [InlineData(
        InternalDriverLoadedReadiness.RightControllerSerial,
        SteamVrControllerRole.LeftHand)]
    [InlineData(
        InternalDriverLoadedReadiness.LeftControllerSerial,
        SteamVrControllerRole.None)]
    [InlineData(
        InternalDriverLoadedReadiness.RightControllerSerial,
        SteamVrControllerRole.Other)]
    public void EveryIncorrectRequiredControllerRoleFailsClosed(
        string serial,
        SteamVrControllerRole role)
    {
        var result = EvaluateWithReplacement(LtbController(serial, role));

        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.ControllerRoleMismatch,
            serial);
    }

    [Fact]
    public void MissingRequiredControllerMetadataFailsClosed()
    {
        var serial = InternalDriverLoadedReadiness.RightControllerSerial;
        var replacement = Descriptor(
            serial,
            2,
            SteamVrDeviceCategory.InputController,
            SteamVrControllerRole.RightHand,
            true,
            metadata: null);

        var result = EvaluateWithReplacement(replacement);

        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.ControllerMetadataMissing,
            serial);
    }

    public static IEnumerable<object?[]> ExactMetadataMismatchCases()
    {
        yield return
        [
            "driver",
            "LTB",
            InternalDriverLoadedReadinessIssue.DriverIdMismatch,
        ];
        yield return
        [
            "tracking_system",
            "LTB",
            InternalDriverLoadedReadinessIssue.TrackingSystemMismatch,
        ];
        yield return
        [
            "controller_type",
            "LTB_TOUCH",
            InternalDriverLoadedReadinessIssue.ControllerTypeMismatch,
        ];
        yield return
        [
            "input_profile",
            "{ltb}\\input\\ltb_touch_profile.json",
            InternalDriverLoadedReadinessIssue.InputProfileMismatch,
        ];
        yield return
        [
            "tracking_system",
            null,
            InternalDriverLoadedReadinessIssue.TrackingSystemMismatch,
        ];
        yield return
        [
            "controller_type",
            null,
            InternalDriverLoadedReadinessIssue.ControllerTypeMismatch,
        ];
        yield return
        [
            "input_profile",
            null,
            InternalDriverLoadedReadinessIssue.InputProfileMismatch,
        ];
    }

    [Theory]
    [MemberData(nameof(ExactMetadataMismatchCases))]
    public void EveryRuntimeIdentityPropertyRequiresItsExactValue(
        string property,
        string? replacementValue,
        InternalDriverLoadedReadinessIssue expectedIssue)
    {
        var serial = InternalDriverLoadedReadiness.LeftControllerSerial;
        var metadata = LtbMetadata(
            driverId: property == "driver"
                ? replacementValue ?? "wrong"
                : InternalDriverLoadedReadiness.DriverId,
            trackingSystem: property == "tracking_system"
                ? replacementValue
                : InternalDriverLoadedReadiness.TrackingSystemName,
            controllerType: property == "controller_type"
                ? replacementValue
                : InternalDriverLoadedReadiness.ControllerType,
            inputProfile: property == "input_profile"
                ? replacementValue
                : InternalDriverLoadedReadiness.InputProfilePath);
        var replacement = LtbController(
            serial,
            SteamVrControllerRole.LeftHand,
            metadata: metadata);

        var result = EvaluateWithReplacement(replacement);

        AssertIssue(result, expectedIssue, serial);
        Assert.Contains("required exact value", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredSerialMatchingIsOrdinalAndWrongCaseBecomesMissingPlusExtra()
    {
        var serial = InternalDriverLoadedReadiness.LeftControllerSerial;
        var result = EvaluateWithReplacement(LtbController(
            serial.ToLowerInvariant(),
            SteamVrControllerRole.LeftHand));

        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.RequiredControllerMissing,
            serial);
        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.AdditionalLtbDevice,
            serial.ToLowerInvariant());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingRuntimeBuildIdentityFailsClosed(string? runtimeBuildIdentity)
    {
        var serial = InternalDriverLoadedReadiness.LeftControllerSerial;
        var result = EvaluateWithReplacement(LtbController(
            serial,
            SteamVrControllerRole.LeftHand,
            metadata: LtbMetadata(driverVersion: runtimeBuildIdentity)));

        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.RuntimeBuildIdentityMissing,
            serial);
    }

    [Theory]
    [InlineData("another-build")]
    [InlineData("DRIVER-LTB-TEST-BUILD-42")]
    [InlineData("driver-ltb-test-build-42+dirty")]
    public void RuntimeBuildIdentityMustMatchStagedMarkerOrdinally(string runtimeBuildIdentity)
    {
        var serial = InternalDriverLoadedReadiness.RightControllerSerial;
        var result = EvaluateWithReplacement(LtbController(
            serial,
            SteamVrControllerRole.RightHand,
            metadata: LtbMetadata(driverVersion: runtimeBuildIdentity)));

        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.RuntimeBuildIdentityMismatch,
            serial);
        Assert.Contains(BuildIdentity, result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("loads the staged build", result.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SteamVrDeviceCategory.HeadMountedDisplay)]
    [InlineData(SteamVrDeviceCategory.InputController)]
    [InlineData(SteamVrDeviceCategory.GenericTracker)]
    [InlineData(SteamVrDeviceCategory.TrackingReference)]
    public void AnyAdditionalLtbDeviceClassFailsClosed(SteamVrDeviceCategory category)
    {
        var devices = ReadyDevices().ToList();
        devices.Add(Descriptor(
            "LTB-EXTRA-DEVICE",
            14,
            category,
            category == SteamVrDeviceCategory.InputController
                ? SteamVrControllerRole.Other
                : SteamVrControllerRole.None,
            true,
            LtbMetadata()));

        var result = InternalDriverLoadedReadiness.Evaluate(devices, BuildIdentity);

        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.AdditionalLtbDevice,
            "LTB-EXTRA-DEVICE");
        Assert.Contains("must expose only", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void DisconnectedAdditionalLtbDeviceStillFailsClosed()
    {
        var devices = ReadyDevices().ToList();
        devices.Add(Descriptor(
            "STALE-LTB-DEVICE",
            14,
            SteamVrDeviceCategory.GenericTracker,
            SteamVrControllerRole.None,
            false,
            LtbMetadata()));

        var result = InternalDriverLoadedReadiness.Evaluate(devices, BuildIdentity);

        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.AdditionalLtbDevice,
            "STALE-LTB-DEVICE");
    }

    public static IEnumerable<object[]> DisallowedRuntimeCategoryCases()
    {
        foreach (var term in new[] { "Meta", "Quest", "Oculus", "ALVR" })
        {
            foreach (var category in Enum.GetValues<SteamVrDeviceCategory>())
            {
                yield return [term, category];
            }
        }
    }

    [Theory]
    [MemberData(nameof(DisallowedRuntimeCategoryCases))]
    public void EveryDisallowedRuntimeIsRejectedAcrossEveryEnumeratedDeviceClass(
        string runtimeTerm,
        SteamVrDeviceCategory category)
    {
        var devices = ReadyDevices().ToList();
        devices.Add(ForeignDevice(category, runtimeTerm, "driver"));

        var result = InternalDriverLoadedReadiness.Evaluate(devices, BuildIdentity);

        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.DisallowedSteamVrDevice);
        Assert.Contains(runtimeTerm, result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restart SteamVR", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("serial")]
    [InlineData("path")]
    [InlineData("driver")]
    [InlineData("tracking_system")]
    [InlineData("manufacturer")]
    [InlineData("model")]
    [InlineData("controller_type")]
    [InlineData("input_profile")]
    [InlineData("driver_version")]
    public void DisallowedRuntimeEvidenceCannotHideInAnyIdentityOrMetadataField(
        string field)
    {
        var devices = ReadyDevices().ToList();
        devices.Add(ForeignDevice(
            SteamVrDeviceCategory.InputController,
            "Quest",
            field));

        var result = InternalDriverLoadedReadiness.Evaluate(devices, BuildIdentity);

        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.DisallowedSteamVrDevice);
    }

    [Fact]
    public void DisconnectedDisallowedInputDeviceStillFailsGlobalExclusion()
    {
        var devices = ReadyDevices().ToList();
        devices.Add(ForeignDevice(
            SteamVrDeviceCategory.InputController,
            "Oculus",
            "model",
            isConnected: false));

        var result = InternalDriverLoadedReadiness.Evaluate(devices, BuildIdentity);

        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.DisallowedSteamVrDevice);
    }

    [Theory]
    [InlineData(SteamVrDeviceCategory.GenericTracker)]
    [InlineData(SteamVrDeviceCategory.TrackingReference)]
    [InlineData(SteamVrDeviceCategory.DisplayRedirect)]
    public void DisallowedRuntimeOnNonHmdDeviceClassStillFailsGlobalExclusion(
        SteamVrDeviceCategory category)
    {
        var devices = ReadyDevices().ToList();
        devices.Add(ForeignDevice(
            category,
            "Meta Quest ALVR Oculus",
            "model"));

        var result = InternalDriverLoadedReadiness.Evaluate(devices, BuildIdentity);

        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.DisallowedSteamVrDevice);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AdditionalNonLtbInputControllerFailsClosedWhetherConnectedOrNot(
        bool isConnected)
    {
        var devices = ReadyDevices().ToList();
        devices.Add(ForeignDevice(
            SteamVrDeviceCategory.InputController,
            "Index",
            "model",
            isConnected));

        var result = InternalDriverLoadedReadiness.Evaluate(devices, BuildIdentity);

        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.InputControllerCountMismatch);
        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.AdditionalInputController,
            "FOREIGN-19");
        Assert.Contains("restart SteamVR", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void SecondHmdFailsClosedRegardlessOfRuntimeOrConnection(
        bool hasLighthouseEvidence,
        bool isConnected)
    {
        const string serial = "SECOND-HMD";
        var devices = ReadyDevices().ToList();
        devices.Add(hasLighthouseEvidence
            ? LighthouseHmd(isConnected, index: 18, serial: serial)
            : ForeignDevice(
                SteamVrDeviceCategory.HeadMountedDisplay,
                "Future HMD",
                "model",
                isConnected,
                index: 18,
                serial: serial));

        var result = InternalDriverLoadedReadiness.Evaluate(devices, BuildIdentity);

        AssertIssue(result, InternalDriverLoadedReadinessIssue.HmdCountMismatch);
        AssertIssue(result, InternalDriverLoadedReadinessIssue.AdditionalHmd, serial);
        Assert.Contains("sole HMD", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("restart SteamVR", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SteamVrDeviceCategory.GenericTracker)]
    [InlineData(SteamVrDeviceCategory.TrackingReference)]
    public void NonForbiddenTrackerAndTrackingReferenceRemainAllowed(
        SteamVrDeviceCategory category)
    {
        var devices = ReadyDevices().ToList();
        devices.Add(ForeignDevice(category, "Vive", "model"));

        var result = InternalDriverLoadedReadiness.Evaluate(devices, BuildIdentity);

        Assert.True(result.IsReady, result.Diagnostic);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ExactMetaDevelopManufacturerDoesNotTriggerGlobalExclusion()
    {
        var devices = ReadyDevices().ToList();
        devices.Add(ForeignDevice(
            SteamVrDeviceCategory.GenericTracker,
            "Meta-Develop",
            "manufacturer"));

        var result = InternalDriverLoadedReadiness.Evaluate(devices, BuildIdentity);

        Assert.True(result.IsReady, result.Diagnostic);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void MissingActiveIndexZeroHmdFailsClosedWithRemediation()
    {
        var result = InternalDriverLoadedReadiness.Evaluate(
            ReadyDevices()
                .Where(device => device.TransientDeviceIndex != 0)
                .ToArray(),
            BuildIdentity);

        AssertIssue(result, InternalDriverLoadedReadinessIssue.ActiveHmdInvalid);
        Assert.Contains("OpenVR index 0", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("restart SteamVR", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisconnectedActiveLighthouseHmdFailsClosed()
    {
        var result = EvaluateWithReplacement(LighthouseHmd(isConnected: false));

        AssertIssue(result, InternalDriverLoadedReadinessIssue.ActiveHmdInvalid);
        Assert.Contains("disconnected", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActiveHmdWithoutPositiveLighthouseEvidenceFailsClosed()
    {
        var result = EvaluateWithReplacement(Descriptor(
            "ACTIVE-HMD",
            0,
            SteamVrDeviceCategory.HeadMountedDisplay,
            SteamVrControllerRole.None,
            true,
            new SteamVrDeviceMetadata(
                "future_driver",
                "future_tracking",
                "Future Vendor",
                "Future HMD",
                controllerType: null)));

        AssertIssue(result, InternalDriverLoadedReadinessIssue.ActiveHmdInvalid);
        Assert.Contains("positive Lighthouse", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void MetaActiveHmdProducesBothTopologyAndGlobalExclusionDiagnostics()
    {
        var result = EvaluateWithReplacement(ForeignDevice(
            SteamVrDeviceCategory.HeadMountedDisplay,
            "Meta Quest",
            "model",
            index: 0,
            serial: "ACTIVE-HMD"));

        AssertIssue(result, InternalDriverLoadedReadinessIssue.ActiveHmdInvalid);
        AssertIssue(result, InternalDriverLoadedReadinessIssue.DisallowedSteamVrDevice);
    }

    [Fact]
    public void IndependentFailuresAreAggregatedInsteadOfShortCircuiting()
    {
        var devices = ReadyDevices()
            .Where(device => device.Identity.SerialNumber !=
                InternalDriverLoadedReadiness.RightControllerSerial)
            .Append(Descriptor(
                "LTB-EXTRA-HMD",
                17,
                SteamVrDeviceCategory.HeadMountedDisplay,
                SteamVrControllerRole.None,
                false,
                LtbMetadata()))
            .ToArray();

        var result = InternalDriverLoadedReadiness.Evaluate(devices, stagedBuildIdentity: null);

        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.StagedBuildIdentityMissing);
        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.RequiredControllerMissing,
            InternalDriverLoadedReadiness.RightControllerSerial);
        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.AdditionalLtbDevice,
            "LTB-EXTRA-HMD");
        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.HmdCountMismatch);
        AssertIssue(
            result,
            InternalDriverLoadedReadinessIssue.AdditionalHmd,
            "LTB-EXTRA-HMD");
        Assert.True(result.Diagnostics.Count >= 5);
    }

    [Fact]
    public void NullDeviceCollectionIsRejectedAtTypedBoundary()
    {
        Assert.Throws<ArgumentNullException>(() =>
            InternalDriverLoadedReadiness.Evaluate(null!, BuildIdentity));
    }

    private static InternalDriverLoadedReadinessResult EvaluateWithReplacement(
        SteamVrDeviceDescriptor replacement)
    {
        var devices = ReadyDevices()
            .Where(device =>
                device.Identity.SerialNumber != replacement.Identity.SerialNumber &&
                device.TransientDeviceIndex != replacement.TransientDeviceIndex)
            .Append(replacement)
            .ToArray();
        return InternalDriverLoadedReadiness.Evaluate(devices, BuildIdentity);
    }

    private static SteamVrDeviceDescriptor[] ReadyDevices() =>
    [
        LighthouseHmd(),
        LtbController(
            InternalDriverLoadedReadiness.LeftControllerSerial,
            SteamVrControllerRole.LeftHand),
        LtbController(
            InternalDriverLoadedReadiness.RightControllerSerial,
            SteamVrControllerRole.RightHand),
    ];

    private static SteamVrDeviceDescriptor LighthouseHmd(
        bool isConnected = true,
        uint index = 0,
        string serial = "ACTIVE-HMD") =>
        Descriptor(
            serial,
            index,
            SteamVrDeviceCategory.HeadMountedDisplay,
            SteamVrControllerRole.None,
            isConnected,
            new SteamVrDeviceMetadata(
                "lighthouse",
                "lighthouse",
                "Bigscreen",
                "Beyond",
                controllerType: null));

    private static SteamVrDeviceDescriptor LtbController(
        string serial,
        SteamVrControllerRole role,
        uint? index = null,
        bool isConnected = true,
        SteamVrDeviceMetadata? metadata = null) =>
        Descriptor(
            serial,
            index ?? (serial == InternalDriverLoadedReadiness.RightControllerSerial ? 2u : 1u),
            SteamVrDeviceCategory.InputController,
            role,
            isConnected,
            metadata ?? LtbMetadata());

    private static SteamVrDeviceMetadata LtbMetadata(
        string driverId = InternalDriverLoadedReadiness.DriverId,
        string? trackingSystem = InternalDriverLoadedReadiness.TrackingSystemName,
        string? controllerType = InternalDriverLoadedReadiness.ControllerType,
        string? inputProfile = InternalDriverLoadedReadiness.InputProfilePath,
        string? driverVersion = BuildIdentity) =>
        new(
            driverId,
            trackingSystem,
            "Meta-Develop",
            "Lighthouse Touch Bridge Controller",
            controllerType,
            inputProfile,
            driverVersion);

    private static SteamVrDeviceDescriptor ForeignDevice(
        SteamVrDeviceCategory category,
        string evidence,
        string evidenceField,
        bool isConnected = true,
        uint index = 19,
        string? serial = null)
    {
        var foreignSerial = serial ?? (evidenceField == "serial"
            ? $"{evidence}-DEVICE"
            : $"FOREIGN-{index}");
        var path = evidenceField == "path"
            ? $"/devices/{evidence}/foreign"
            : "/devices/foreign/device";
        var metadata = new SteamVrDeviceMetadata(
            evidenceField == "driver" ? evidence : "foreign_driver",
            evidenceField == "tracking_system" ? evidence : "foreign_tracking",
            evidenceField == "manufacturer" ? evidence : "Foreign Vendor",
            evidenceField == "model" ? evidence : "Foreign Device",
            evidenceField == "controller_type" ? evidence : "foreign_controller",
            evidenceField == "input_profile" ? evidence : "{foreign}/input/profile.json",
            driverVersion: evidenceField == "driver_version" ? evidence : "foreign-build");
        return new SteamVrDeviceDescriptor(
            new SteamVrDeviceIdentity(foreignSerial, path),
            index,
            category,
            category == SteamVrDeviceCategory.InputController
                ? SteamVrControllerRole.Other
                : SteamVrControllerRole.None,
            isConnected,
            metadata);
    }

    private static SteamVrDeviceDescriptor Descriptor(
        string serial,
        uint index,
        SteamVrDeviceCategory category,
        SteamVrControllerRole role,
        bool isConnected,
        SteamVrDeviceMetadata? metadata) =>
        new(
            new SteamVrDeviceIdentity(serial, $"/devices/device/{serial}"),
            index,
            category,
            role,
            isConnected,
            metadata);

    private static void AssertIssue(
        InternalDriverLoadedReadinessResult result,
        InternalDriverLoadedReadinessIssue issue,
        string? serial = null)
    {
        Assert.False(result.IsReady);
        Assert.Contains(
            result.Diagnostics,
            diagnostic =>
                diagnostic.Issue == issue &&
                (serial is null || diagnostic.DeviceSerial == serial));
    }
}
