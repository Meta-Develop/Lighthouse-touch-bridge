using Ltb.App;
using Ltb.Configuration;
using Ltb.OpenVr;

namespace Ltb.Integration.Tests;

public sealed class GeneralizedTouchProfilePipelineTests
{
    [Theory]
    [InlineData(
        "Quest 2 Touch",
        "Miramar (Left Controller)",
        "Miramar (Right Controller)",
        "/input/oculus_touch_profile.json",
        "HTC",
        "Vive Tracker 3.0")]
    [InlineData(
        "Quest 3 Touch Plus",
        "Meta Quest 3 Controller",
        "Meta Quest 3 Controller",
        "/input/quest3_touch_plus_profile.json",
        "Tundra Labs",
        "Tundra Tracker")]
    [InlineData(
        "Quest Pro Touch",
        "Meta Quest Pro Controller",
        "Meta Quest Pro Controller",
        "/input/quest_pro_touch_profile.json",
        "Lighthouse Compatible",
        "Generic Lighthouse Tracked Device")]
    public async Task ControllerAndTrackerFamiliesAssociateCalibratePersistAndReloadSchemaOne(
        string expectedModel,
        string leftModel,
        string rightModel,
        string inputProfile,
        string trackerManufacturer,
        string trackerModel)
    {
        var leftTracker = Tracker(
            ScriptedCalibrationWizardRuntime.LeftTrackerSerial,
            5,
            trackerManufacturer,
            trackerModel);
        var rightTracker = Tracker(
            ScriptedCalibrationWizardRuntime.RightTrackerSerial,
            6,
            trackerManufacturer,
            trackerModel);
        Assert.True(leftTracker.CanUseAsPhysicalPoseSource);
        Assert.True(rightTracker.CanUseAsPhysicalPoseSource);
        Assert.Equal(trackerModel, leftTracker.Metadata?.ModelNumber);
        Assert.Equal(trackerModel, rightTracker.Metadata?.ModelNumber);

        var observations = ProductionReliableDailyUseRuntime
            .CreateCurrentRecalibrationObservations(
                Controller(
                    SteamVrControllerRole.LeftHand,
                    leftModel,
                    inputProfile),
                Controller(
                    SteamVrControllerRole.RightHand,
                    rightModel,
                    inputProfile),
                leftTracker,
                rightTracker);
        Assert.Equal("ALVR", observations.ControllerRuntime);
        Assert.Equal(expectedModel, observations.ControllerModel);

        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ltb-generalized-touch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var profilePath = Path.Combine(directory, "profiles.json");
        try
        {
            var firstOutput = new RecordingWizardOutput();
            var firstRuntime = new ScriptedCalibrationWizardRuntime(
                firstOutput,
                observations);
            var first = await new TwoHandCalibrationWizard(
                firstRuntime,
                new FileCalibrationWizardBackend(profilePath),
                firstOutput).RunAsync();

            Assert.True(first.Success, first.Diagnostic);
            Assert.False(first.ReusedProfiles);
            Assert.Equal(
                [CalibrationWizardHand.Left, CalibrationWizardHand.Right],
                firstRuntime.CapturedHands);
            Assert.Equal(2, firstRuntime.AppliedProfiles.Count);

            var persisted = CalibrationProfileFile.LoadStore(profilePath);
            Assert.Equal(2, persisted.Profiles.Count);
            Assert.All(persisted.Profiles, profile =>
            {
                Assert.Equal(1, profile.SchemaVersion);
                Assert.Equal("ALVR", profile.ControllerRuntime);
                Assert.Equal(expectedModel, profile.ControllerModel);
                Assert.True(profile.MatchesController("ALVR", expectedModel));
                Assert.Contains(
                    profile.TrackerSerial,
                    new[]
                    {
                        leftTracker.StableDeviceId,
                        rightTracker.StableDeviceId,
                    });
            });
            Assert.Equal(
                leftTracker.StableDeviceId,
                persisted.Profiles.Single(profile =>
                    profile.Hand == ControllerHand.Left).TrackerSerial);
            Assert.Equal(
                rightTracker.StableDeviceId,
                persisted.Profiles.Single(profile =>
                    profile.Hand == ControllerHand.Right).TrackerSerial);

            var laterOutput = new RecordingWizardOutput();
            var laterRuntime = new ScriptedCalibrationWizardRuntime(laterOutput, observations);
            var later = await new TwoHandCalibrationWizard(
                laterRuntime,
                new FileCalibrationWizardBackend(profilePath),
                laterOutput).RunAsync();

            Assert.True(later.Success, later.Diagnostic);
            Assert.True(later.ReusedProfiles);
            Assert.Empty(laterRuntime.CapturedHands);
            Assert.Equal(2, laterRuntime.AppliedProfiles.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MixedTouchFamiliesAreNotAcceptedAsACompatiblePair()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionReliableDailyUseRuntime.CreateCurrentRecalibrationObservations(
                Controller(
                    SteamVrControllerRole.LeftHand,
                    "Meta Quest 3 Controller",
                    "/input/quest3_touch_plus_profile.json"),
                Controller(
                    SteamVrControllerRole.RightHand,
                    "Meta Quest Pro Controller",
                    "/input/quest_pro_touch_profile.json"),
                Tracker("TRACKER-LEFT", 5),
                Tracker("TRACKER-RIGHT", 6)));

        Assert.Contains("incompatible runtime/model", exception.Message);
    }

    private static SteamVrDeviceDescriptor Controller(
        SteamVrControllerRole role,
        string model,
        string inputProfile) =>
        new(
            new SteamVrDeviceIdentity(
                $"TOUCH-{role}",
                $"/devices/alvr/TOUCH-{role}"),
            role == SteamVrControllerRole.LeftHand ? 3u : 4u,
            SteamVrDeviceCategory.InputController,
            role,
            true,
            new SteamVrDeviceMetadata(
                "alvr",
                "ALVR",
                "Meta",
                model,
                "meta_touch",
                inputProfile));

    private static SteamVrDeviceDescriptor Tracker(
        string serial,
        uint index,
        string manufacturer = "Lighthouse Compatible",
        string model = "Generic Lighthouse Tracked Device") =>
        new(
            new SteamVrDeviceIdentity(serial, $"/devices/lighthouse/{serial}"),
            index,
            SteamVrDeviceCategory.GenericTracker,
            SteamVrControllerRole.None,
            true,
            new SteamVrDeviceMetadata(
                "lighthouse",
                "lighthouse",
                manufacturer,
                model,
                controllerType: null));
}
