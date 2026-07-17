using Ltb.App;
using Ltb.OpenVr;

namespace Ltb.Integration.Tests;

public sealed class SteamVrInputDeviceClassifierTests
{
    [Theory]
    [InlineData(
        SteamVrControllerRole.LeftHand,
        "Miramar (Left Controller)",
        "OCULUS_TOUCH")]
    [InlineData(
        SteamVrControllerRole.RightHand,
        " miramar-right controller ",
        "oculus touch")]
    public void AcceptsNormalizedOfficialQuest2TouchEmulationTuple(
        SteamVrControllerRole role,
        string model,
        string controllerType)
    {
        var classification = SteamVrInputDeviceClassifier.Classify(
            Controller(role, model, controllerType));

        Assert.True(classification.IsSupported, classification.Diagnostic);
        Assert.Equal("ALVR", classification.ControllerRuntime);
        Assert.Equal("Quest 2 Touch", classification.ControllerModel);
    }

    [Theory]
    [InlineData("Meta Quest 3 Controller", "oculus_touch")]
    [InlineData("Miramar (Left Controller)", "knuckles")]
    [InlineData("Miramar (Right Controller)", "oculus_touch")]
    public void RejectsWrongEmulationOrWrongHandTuple(string model, string controllerType)
    {
        var classification = SteamVrInputDeviceClassifier.Classify(
            Controller(SteamVrControllerRole.LeftHand, model, controllerType));

        Assert.False(classification.IsSupported);
    }

    [Fact]
    public void RejectsMissingCurrentMetadataInsteadOfTrustingStoredProfileIdentity()
    {
        var controller = new SteamVrDeviceDescriptor(
            new SteamVrDeviceIdentity("TOUCH-L", "/devices/oculus/TOUCH-L"),
            3,
            SteamVrDeviceCategory.InputController,
            SteamVrControllerRole.LeftHand,
            true);

        var classification = SteamVrInputDeviceClassifier.Classify(controller);

        Assert.False(classification.IsSupported);
        Assert.Contains("metadata", classification.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OptionalStoredSerialStillRequiresOneCurrentSupportedRole()
    {
        var observed = Controller(
            SteamVrControllerRole.LeftHand,
            "Miramar (Left Controller)",
            "oculus_touch");
        var profile = ReliableProfileBackend.CreateProfiles()
            .Single(candidate => candidate.Hand == CalibrationWizardHand.Left);

        var withoutStoredSerial = profile with { ControllerSerial = string.Empty };
        Assert.Same(
            observed,
            ProductionReliableDailyUseRuntime.SelectCurrentController(
                [observed],
                withoutStoredSerial));

        var staleStoredSerial = profile with { ControllerSerial = "STALE-SERIAL" };
        Assert.Throws<InvalidOperationException>(() =>
            ProductionReliableDailyUseRuntime.SelectCurrentController(
                [observed],
                staleStoredSerial));
    }

    [Fact]
    public void RecalibrationObservationsComeFromCurrentTrackerTuple()
    {
        var observations = ProductionReliableDailyUseRuntime
            .CreateCurrentRecalibrationObservations(
                Tracker("OBSERVED-LEFT", 5),
                Tracker("OBSERVED-RIGHT", 6));

        Assert.Equal("OBSERVED-LEFT", observations.ObservedLeftTrackerSerial);
        Assert.Equal("OBSERVED-RIGHT", observations.ObservedRightTrackerSerial);
        Assert.Equal("ALVR", observations.ControllerRuntime);
        Assert.Equal("Quest 2 Touch", observations.ControllerModel);
    }

    private static SteamVrDeviceDescriptor Controller(
        SteamVrControllerRole role,
        string model,
        string controllerType) =>
        new(
            new SteamVrDeviceIdentity(
                role.ToString(),
                $"/devices/oculus/{role}"),
            role == SteamVrControllerRole.LeftHand ? 3u : 4u,
            SteamVrDeviceCategory.InputController,
            role,
            true,
            new SteamVrDeviceMetadata(
                "oculus",
                "Oculus",
                "Oculus",
                model,
                controllerType));

    private static SteamVrDeviceDescriptor Tracker(string serial, uint index) =>
        new(
            new SteamVrDeviceIdentity(serial, $"/devices/lighthouse/{serial}"),
            index,
            SteamVrDeviceCategory.GenericTracker,
            SteamVrControllerRole.None,
            true);
}
