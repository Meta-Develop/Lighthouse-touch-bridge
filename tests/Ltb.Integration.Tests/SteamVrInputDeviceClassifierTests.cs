using Ltb.App;
using Ltb.OpenVr;

namespace Ltb.Integration.Tests;

public sealed class SteamVrInputDeviceClassifierTests
{
    [Theory]
    [InlineData(
        SteamVrControllerRole.LeftHand,
        "Miramar (Left Controller)",
        "Oculus",
        "/drivers/oculus/resources/input/oculus_touch_profile.json",
        "Quest 2 Touch")]
    [InlineData(
        SteamVrControllerRole.RightHand,
        " miramar-right controller ",
        "Oculus",
        "/input/quest_2_touch_profile.json",
        "Quest 2 Touch")]
    [InlineData(
        SteamVrControllerRole.LeftHand,
        "Miramar (Left Controller)",
        "Oculus",
        "C:\\SteamVR\\input\\oculus_touch_profile.json",
        "Quest 2 Touch")]
    [InlineData(
        SteamVrControllerRole.LeftHand,
        "Meta Quest 3 Controller",
        "Meta",
        "/input/meta_quest_3_touch_plus_profile.json",
        "Quest 3 Touch Plus")]
    [InlineData(
        SteamVrControllerRole.RightHand,
        "Meta Quest 3 Controller",
        "Meta",
        "/input/meta_quest_3_touch_plus_profile.json",
        "Quest 3 Touch Plus")]
    [InlineData(
        SteamVrControllerRole.LeftHand,
        "Meta Quest Pro Controller",
        "Meta",
        "/input/meta_quest_pro_touch_profile.json",
        "Quest Pro Touch")]
    [InlineData(
        SteamVrControllerRole.RightHand,
        "Meta Quest Pro Controller",
        "Meta",
        "/input/meta_quest_pro_touch_profile.json",
        "Quest Pro Touch")]
    public void AcceptsSupportedMetaTouchProfilesFromCentralCatalog(
        SteamVrControllerRole role,
        string model,
        string manufacturer,
        string inputProfile,
        string expectedModel)
    {
        var classification = SteamVrInputDeviceClassifier.Classify(
            Controller(
                role,
                model,
                "OCULUS_TOUCH",
                manufacturer,
                inputProfile));

        Assert.True(classification.IsSupported, classification.Diagnostic);
        Assert.Equal("ALVR", classification.ControllerRuntime);
        Assert.Equal(expectedModel, classification.ControllerModel);
        Assert.Equal(SteamVrControllerFamily.MetaTouch, classification.ControllerFamily);
        Assert.Equal(inputProfile, classification.InputProfile);
    }

    [Theory]
    [InlineData("/input/not_oculus_touch_profile.json")]
    [InlineData("C:\\SteamVR\\input\\not_oculus_touch_profile.json")]
    public void RejectsInputProfileFileNameWithApprovedNameOnlyAsPrefix(
        string inputProfile)
    {
        var classification = SteamVrInputDeviceClassifier.Classify(
            Controller(
                SteamVrControllerRole.LeftHand,
                "Miramar (Left Controller)",
                "oculus_touch",
                inputProfile: inputProfile));

        Assert.False(classification.IsSupported);
        Assert.Contains(
            "input-profile",
            classification.Diagnostic,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Meta Quest 4 Controller", "oculus_touch", "/input/oculus_touch_profile.json")]
    [InlineData("Miramar (Left Controller)", "knuckles")]
    [InlineData("Miramar (Right Controller)", "oculus_touch")]
    [InlineData(
        "Meta Quest 3 Controller",
        "oculus_touch",
        "/input/meta_quest_pro_touch_profile.json")]
    public void RejectsUnknownProfileWrongTypeOrWrongHandTuple(
        string model,
        string controllerType,
        string? inputProfile = null)
    {
        var classification = SteamVrInputDeviceClassifier.Classify(
            Controller(
                SteamVrControllerRole.LeftHand,
                model,
                controllerType,
                inputProfile: inputProfile));

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
    public void RecalibrationObservationsComeFromCurrentControllerAndTrackerTuples()
    {
        var observations = ProductionReliableDailyUseRuntime
            .CreateCurrentRecalibrationObservations(
                Controller(
                    SteamVrControllerRole.LeftHand,
                    "Meta Quest 3 Controller",
                    "oculus_touch",
                    "Meta",
                    "/input/meta_quest_3_touch_plus_profile.json"),
                Controller(
                    SteamVrControllerRole.RightHand,
                    "Meta Quest 3 Controller",
                    "oculus_touch",
                    "Meta",
                    "/input/meta_quest_3_touch_plus_profile.json"),
                Tracker("OBSERVED-LEFT", 5),
                Tracker("OBSERVED-RIGHT", 6));

        Assert.Equal("OBSERVED-LEFT", observations.ObservedLeftTrackerSerial);
        Assert.Equal("OBSERVED-RIGHT", observations.ObservedRightTrackerSerial);
        Assert.Equal("ALVR", observations.ControllerRuntime);
        Assert.Equal("Quest 3 Touch Plus", observations.ControllerModel);
    }

    private static SteamVrDeviceDescriptor Controller(
        SteamVrControllerRole role,
        string model,
        string controllerType,
        string manufacturer = "Oculus",
        string? inputProfile = null) =>
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
                manufacturer,
                model,
                controllerType,
                inputProfile));

    private static SteamVrDeviceDescriptor Tracker(string serial, uint index) =>
        new(
            new SteamVrDeviceIdentity(serial, $"/devices/lighthouse/{serial}"),
            index,
            SteamVrDeviceCategory.GenericTracker,
            SteamVrControllerRole.None,
            true);
}
