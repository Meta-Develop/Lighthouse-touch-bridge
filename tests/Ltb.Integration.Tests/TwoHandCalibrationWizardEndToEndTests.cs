using System.Numerics;
using Ltb.App;
using Ltb.Calibration;
using Ltb.Configuration;

namespace Ltb.Integration.Tests;

public sealed class TwoHandCalibrationWizardEndToEndTests
{
    [Fact]
    public void ScriptedWizardCommandRequiresProfilesAndAcceptsAnOptionalLog()
    {
        Assert.True(AppCommandLineOptions.TryParse(
            ["wizard-demo", "--profiles", "profiles.json"],
            out var options,
            out var error), error);
        Assert.Equal(AppCommand.WizardDemo, options.Command);
        Assert.Equal("profiles.json", options.WizardProfileStorePath);
        Assert.Null(options.WizardLogPath);

        Assert.True(AppCommandLineOptions.TryParse(
            [
                "wizard-demo",
                "--profiles", "profiles.json",
                "--log", "events.jsonl",
            ],
            out var loggedOptions,
            out var loggedError), loggedError);
        Assert.Equal("profiles.json", loggedOptions.WizardProfileStorePath);
        Assert.Equal("events.jsonl", loggedOptions.WizardLogPath);

        Assert.False(AppCommandLineOptions.TryParse(
            ["wizard-demo"],
            out _,
            out var missingError));
        Assert.Contains("requires --profiles", missingError);
    }

    [Fact]
    public async Task ScriptedFakeSessionAssociatesSolvesPersistsAndReloadsBothHands()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ltb-two-hand-wizard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var profilePath = Path.Combine(directory, "profiles.json");
        var createdUtc = new DateTimeOffset(2026, 7, 18, 4, 5, 6, TimeSpan.Zero);
        try
        {
            using var firstOutput = new TranscriptWizardOutput();
            var firstRuntime = new ScriptedCalibrationWizardRuntime(firstOutput);
            var backend = new FileCalibrationWizardBackend(
                profilePath,
                () => createdUtc);

            var first = await new TwoHandCalibrationWizard(
                firstRuntime,
                backend,
                firstOutput).RunAsync();

            Assert.True(first.Success, first.Diagnostic);
            Assert.False(first.ReusedProfiles);
            Assert.Equal(2, first.Profiles.Count);
            Assert.Contains(
                "association_tracker_enumeration_swapped: true",
                firstOutput.Text,
                StringComparison.Ordinal);
            foreach (var hand in Enum.GetValues<CalibrationWizardHand>())
            {
                var snapshots = firstOutput.Progress
                    .Where(progress => progress.Hand == hand)
                    .ToArray();
                Assert.Equal(3, snapshots.Length);
                Assert.False(snapshots[0].RotationReady);
                Assert.True(snapshots[^1].RotationReady);
                Assert.True(snapshots[^1].RotationProgress > snapshots[0].RotationProgress);
                Assert.True(snapshots[^1].TotalRotationDegrees > snapshots[0].TotalRotationDegrees);
                Assert.Equal(hand == CalibrationWizardHand.Left, snapshots[^1].PositionReady);
            }

            Assert.Contains(
                "coverage: hand=left samples=30",
                firstOutput.Text,
                StringComparison.Ordinal);
            Assert.Contains(
                "rotation_progress=1.0000 position_progress=1.0000 rotation_ready=true position_ready=true",
                firstOutput.Text,
                StringComparison.Ordinal);
            Assert.Contains(
                "rotation_progress=1.0000 position_progress=0.0000 rotation_ready=true position_ready=false",
                firstOutput.Text,
                StringComparison.Ordinal);
            Assert.Contains(
                "quality_rotation: hand=left rotation_rms_deg=",
                firstOutput.Text,
                StringComparison.Ordinal);
            Assert.Contains(
                "rotation_percentile_deg=",
                firstOutput.Text,
                StringComparison.Ordinal);
            Assert.Contains(
                "quality_position: hand=left position_rms_mm=0.000 position_percentile_mm=0.001 rotation_only_position_rms_mm=62.410",
                firstOutput.Text,
                StringComparison.Ordinal);
            Assert.Contains(
                "quality_translation: hand=left translation_condition=1.237 translation_inlier_ratio=1.0000 translation_magnitude_mm=62.137 translation_split_disagreement_mm=0.000",
                firstOutput.Text,
                StringComparison.Ordinal);
            Assert.Contains(
                "observability: hand=left rotation_observable=true rotation_degeneracy=None translation_observable=true translation_degeneracy=None axis_coverage=0.9096",
                firstOutput.Text,
                StringComparison.Ordinal);
            Assert.Contains(
                "quality_position: hand=right position_rms_mm=unavailable position_percentile_mm=unavailable rotation_only_position_rms_mm=unavailable",
                firstOutput.Text,
                StringComparison.Ordinal);
            Assert.Contains(
                "quality_translation: hand=right translation_condition=unavailable translation_inlier_ratio=unavailable translation_magnitude_mm=0.000 translation_split_disagreement_mm=unavailable",
                firstOutput.Text,
                StringComparison.Ordinal);
            Assert.Contains(
                "observability: hand=right rotation_observable=true rotation_degeneracy=None translation_observable=false translation_degeneracy=MissingPosition axis_coverage=",
                firstOutput.Text,
                StringComparison.Ordinal);
            var left = first.Profiles.Single(
                profile => profile.Hand == CalibrationWizardHand.Left);
            var right = first.Profiles.Single(
                profile => profile.Hand == CalibrationWizardHand.Right);
            Assert.Equal(
                ScriptedCalibrationWizardRuntime.LeftTrackerSerial,
                left.TrackerSerial);
            Assert.Equal(
                ScriptedCalibrationWizardRuntime.RightTrackerSerial,
                right.TrackerSerial);
            Assert.Equal(CalibrationModel.FullSixDof, left.SelectedModel);
            Assert.Equal(CalibrationModel.RotationOnly, right.SelectedModel);
            Assert.NotEqual(Vector3.Zero, left.TrackerToController.TranslationMeters);
            Assert.Equal(Vector3.Zero, right.TrackerToController.TranslationMeters);
            Assert.Contains("position", right.SelectionReason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, firstRuntime.AppliedProfiles.Count);

            var persisted = CalibrationProfileFile.LoadStore(profilePath);
            Assert.Equal(2, persisted.Profiles.Count);
            var persistedLeft = persisted.FindCandidateProfile(
                ScriptedCalibrationWizardRuntime.LeftTrackerSerial,
                ControllerHand.Left);
            var persistedRight = persisted.FindCandidateProfile(
                ScriptedCalibrationWizardRuntime.RightTrackerSerial,
                ControllerHand.Right);
            Assert.NotNull(persistedLeft);
            Assert.NotNull(persistedRight);
            Assert.True(persistedLeft.IsLegacy);
            Assert.True(persistedRight.IsLegacy);
            Assert.Null(persistedLeft.DriverProfile);
            Assert.Null(persistedRight.DriverProfile);
            Assert.Equal(
                ScriptedCalibrationWizardRuntime.LeftControllerSerial,
                persistedLeft.ControllerIdentity);
            Assert.Equal(
                ScriptedCalibrationWizardRuntime.RightControllerSerial,
                persistedRight.ControllerIdentity);
            Assert.Equal(ProfileCalibrationMode.FullSixDof, persistedLeft.SelectedMode);
            Assert.Equal(ProfileCalibrationMode.RotationOnly, persistedRight.SelectedMode);
            Assert.Equal(Vector3.Zero, persistedRight.TrackerToController.TranslationMeters);
            Assert.Equal(createdUtc, persistedLeft.CreatedUtc);
            Assert.Equal(createdUtc, persistedRight.CreatedUtc);

            var laterOutput = new RecordingWizardOutput();
            var laterRuntime = new ScriptedCalibrationWizardRuntime(laterOutput);
            var later = await new TwoHandCalibrationWizard(
                laterRuntime,
                new FileCalibrationWizardBackend(profilePath),
                laterOutput).RunAsync();

            Assert.True(later.Success, later.Diagnostic);
            Assert.True(later.ReusedProfiles);
            Assert.DoesNotContain(CalibrationWizardState.Recording, later.StateHistory);
            Assert.DoesNotContain(CalibrationWizardState.Association, later.StateHistory);
            Assert.Equal(2, laterRuntime.AppliedProfiles.Count);
            Assert.Equal(
                first.Profiles.Select(profile => (profile.Hand, profile.TrackerSerial)),
                later.Profiles.Select(profile => (profile.Hand, profile.TrackerSerial)));
            Assert.All(later.Profiles, profile =>
            {
                Assert.True(double.IsNaN(profile.Quality.RotationPercentileDegrees));
                Assert.Null(profile.Quality.PositionPercentileMillimeters);
                Assert.Null(profile.Quality.RotationOnlyPositionRmsMillimeters);
                Assert.True(double.IsNaN(profile.Quality.RotationInlierRatio));
                Assert.True(double.IsNaN(profile.Quality.TranslationInlierRatio));
                Assert.True(double.IsNaN(
                    profile.Quality.TranslationSplitDisagreementMillimeters));
            });

            var changedObservations = new CalibrationWizardRecalibrationObservations
            {
                ExplicitRequest = true,
                MountMoved = true,
                ValidationThresholdExceeded = true,
                ControllerRuntime = "Changed runtime",
                ControllerModel = "Changed model",
                ExpectedSchemaVersion = CalibrationProfileSchema.CurrentVersion + 1,
                ExpectedTransformConvention = "changed-transform-convention",
                ObservedLeftTrackerSerial =
                    ScriptedCalibrationWizardRuntime.RightTrackerSerial,
                ObservedRightTrackerSerial =
                    ScriptedCalibrationWizardRuntime.LeftTrackerSerial,
            };
            var changedLookup = new FileCalibrationWizardBackend(profilePath)
                .FindReusableProfiles(ScriptedCalibrationWizardRuntime.Devices with
                {
                    Recalibration = changedObservations,
                });
            Assert.False(changedLookup.HasCompletePair);
            Assert.Contains(
                nameof(RecalibrationTriggerKind.ExplicitRequest),
                changedLookup.Diagnostic);
            Assert.Contains(
                nameof(RecalibrationTriggerKind.TrackerHandAssociationChanged),
                changedLookup.Diagnostic);
            Assert.Contains(
                nameof(RecalibrationTriggerKind.MountMoved),
                changedLookup.Diagnostic);
            Assert.Contains(
                nameof(RecalibrationTriggerKind.ValidationThresholdExceeded),
                changedLookup.Diagnostic);
            Assert.Contains(
                nameof(RecalibrationTriggerKind.ControllerIdentityChanged),
                changedLookup.Diagnostic);
            Assert.Contains(
                nameof(RecalibrationTriggerKind.TransformConventionChanged),
                changedLookup.Diagnostic);
            Assert.Contains(
                nameof(RecalibrationTriggerKind.SchemaVersionChanged),
                changedLookup.Diagnostic);

            var explicitOutput = new RecordingWizardOutput();
            var explicitRuntime = new ScriptedCalibrationWizardRuntime(
                explicitOutput,
                ScriptedCalibrationWizardRuntime.Devices.Recalibration with
                {
                    ExplicitRequest = true,
                });
            var explicitRun = await new TwoHandCalibrationWizard(
                explicitRuntime,
                new FileCalibrationWizardBackend(profilePath, () => createdUtc),
                explicitOutput).RunAsync();
            Assert.True(explicitRun.Success, explicitRun.Diagnostic);
            Assert.False(explicitRun.ReusedProfiles);
            Assert.Equal(
                [CalibrationWizardHand.Left, CalibrationWizardHand.Right],
                explicitRuntime.CapturedHands);

            var incompatible = File.ReadAllText(profilePath).Replace(
                "\"schema_version\": 1",
                "\"schema_version\": 3",
                StringComparison.Ordinal);
            File.WriteAllText(profilePath, incompatible);
            var schemaOutput = new RecordingWizardOutput();
            var schemaRuntime = new ScriptedCalibrationWizardRuntime(schemaOutput);
            var schemaRun = await new TwoHandCalibrationWizard(
                schemaRuntime,
                new FileCalibrationWizardBackend(profilePath, () => createdUtc),
                schemaOutput).RunAsync();
            Assert.True(schemaRun.Success, schemaRun.Diagnostic);
            Assert.False(schemaRun.ReusedProfiles);
            Assert.Equal(2, schemaRuntime.CapturedHands.Count);
            Assert.Contains(
                schemaOutput.Lines,
                line => line.Contains(
                    nameof(RecalibrationTriggerKind.SchemaVersionChanged),
                    StringComparison.Ordinal));
            Assert.All(
                CalibrationProfileFile.LoadStore(profilePath).Profiles,
                profile => Assert.Equal(
                    CalibrationProfileSchema.LegacyVersion,
                    profile.SchemaVersion));

            File.WriteAllText(profilePath, "{\"profiles\":[");
            var malformedOutput = new RecordingWizardOutput();
            var malformedRuntime = new ScriptedCalibrationWizardRuntime(malformedOutput);
            var malformedRun = await new TwoHandCalibrationWizard(
                malformedRuntime,
                new FileCalibrationWizardBackend(profilePath),
                malformedOutput).RunAsync();
            Assert.False(malformedRun.Success);
            Assert.Equal(CalibrationWizardState.Ready, malformedRun.FinalState);
            Assert.Empty(malformedRuntime.CapturedHands);
            Assert.Contains("could not be evaluated safely", malformedRun.Diagnostic);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SchemaVersionTwoProfilesPreserveAndMatchInternalDriverIdentity()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ltb-two-hand-schema-two-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var profilePath = Path.Combine(directory, "profiles.json");
        var observations = new CalibrationWizardRecalibrationObservations
        {
            ControllerRuntime = ControllerRuntimeIdentities.MetaLinkLibOvr,
            ControllerModel = "Quest 2 Touch",
            DriverProfile = CalibrationDriverProfiles.LtbTouch,
            LeftControllerIdentity = ScriptedCalibrationWizardRuntime.LeftControllerSerial,
            RightControllerIdentity = ScriptedCalibrationWizardRuntime.RightControllerSerial,
            ExpectedSchemaVersion = CalibrationProfileSchema.CurrentVersion,
            ObservedLeftTrackerSerial =
                ScriptedCalibrationWizardRuntime.LeftTrackerSerial,
            ObservedRightTrackerSerial =
                ScriptedCalibrationWizardRuntime.RightTrackerSerial,
        };

        try
        {
            var firstOutput = new RecordingWizardOutput();
            var first = await new TwoHandCalibrationWizard(
                new ScriptedCalibrationWizardRuntime(firstOutput, observations),
                new FileCalibrationWizardBackend(profilePath),
                firstOutput).RunAsync();

            Assert.True(first.Success, first.Diagnostic);
            Assert.False(first.ReusedProfiles);
            Assert.All(first.Profiles, profile =>
            {
                Assert.Equal(CalibrationProfileSchema.CurrentVersion, profile.SchemaVersion);
                Assert.Equal(CalibrationDriverProfiles.LtbTouch, profile.DriverProfile);
                Assert.Equal(profile.ControllerSerial, profile.ControllerIdentity);
                Assert.False(string.IsNullOrWhiteSpace(profile.ControllerIdentity));
            });

            var persisted = CalibrationProfileFile.LoadStore(profilePath);
            Assert.NotNull(persisted.FindMatchingProfile(
                ScriptedCalibrationWizardRuntime.LeftTrackerSerial,
                ControllerHand.Left,
                CalibrationDriverProfiles.LtbTouch,
                ControllerRuntimeIdentities.MetaLinkLibOvr,
                "Quest 2 Touch",
                ScriptedCalibrationWizardRuntime.LeftControllerSerial));
            Assert.NotNull(persisted.FindMatchingProfile(
                ScriptedCalibrationWizardRuntime.RightTrackerSerial,
                ControllerHand.Right,
                CalibrationDriverProfiles.LtbTouch,
                ControllerRuntimeIdentities.MetaLinkLibOvr,
                "Quest 2 Touch",
                ScriptedCalibrationWizardRuntime.RightControllerSerial));

            var secondOutput = new RecordingWizardOutput();
            var second = await new TwoHandCalibrationWizard(
                new ScriptedCalibrationWizardRuntime(secondOutput, observations),
                new FileCalibrationWizardBackend(profilePath),
                secondOutput).RunAsync();

            Assert.True(second.Success, second.Diagnostic);
            Assert.True(second.ReusedProfiles);
            Assert.DoesNotContain(CalibrationWizardState.Recording, second.StateHistory);

            var identityMismatch = new FileCalibrationWizardBackend(profilePath)
                .FindReusableProfiles(ScriptedCalibrationWizardRuntime.Devices with
                {
                    Recalibration = observations with
                    {
                        LeftControllerIdentity = "DIFFERENT-CONTROLLER-IDENTITY",
                    },
                });
            Assert.False(identityMismatch.HasCompletePair);
            Assert.Contains(
                nameof(RecalibrationTriggerKind.ControllerIdentityChanged),
                identityMismatch.Diagnostic,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SchemaVersionTwoProfilesWithoutMetaIdentityCreateReuseAndStayHandTrackerBound()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ltb-two-hand-schema-two-no-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var profilePath = Path.Combine(directory, "profiles.json");
        var observations = new CalibrationWizardRecalibrationObservations
        {
            ControllerRuntime = ControllerRuntimeIdentities.MetaLinkLibOvr,
            ControllerModel = "Quest 2 Touch",
            DriverProfile = CalibrationDriverProfiles.LtbTouch,
            ExpectedSchemaVersion = CalibrationProfileSchema.CurrentVersion,
            ObservedLeftTrackerSerial =
                ScriptedCalibrationWizardRuntime.LeftTrackerSerial,
            ObservedRightTrackerSerial =
                ScriptedCalibrationWizardRuntime.RightTrackerSerial,
        };

        try
        {
            var firstOutput = new RecordingWizardOutput();
            var first = await new TwoHandCalibrationWizard(
                new ScriptedCalibrationWizardRuntime(firstOutput, observations),
                new FileCalibrationWizardBackend(profilePath),
                firstOutput).RunAsync();

            Assert.True(first.Success, first.Diagnostic);
            Assert.False(first.ReusedProfiles);
            Assert.All(first.Profiles, profile => Assert.Null(profile.ControllerIdentity));

            var persisted = CalibrationProfileFile.LoadStore(profilePath);
            Assert.All(persisted.Profiles, profile =>
            {
                Assert.Equal(CalibrationProfileSchema.CurrentVersion, profile.SchemaVersion);
                Assert.Equal(CalibrationDriverProfiles.LtbTouch, profile.DriverProfile);
                Assert.Equal(ControllerRuntimeIdentities.MetaLinkLibOvr, profile.ControllerRuntime);
                Assert.Null(profile.ControllerIdentity);
            });

            var secondOutput = new RecordingWizardOutput();
            var second = await new TwoHandCalibrationWizard(
                new ScriptedCalibrationWizardRuntime(secondOutput, observations),
                new FileCalibrationWizardBackend(profilePath),
                secondOutput).RunAsync();
            Assert.True(second.Success, second.Diagnostic);
            Assert.True(second.ReusedProfiles);
            Assert.DoesNotContain(CalibrationWizardState.Recording, second.StateHistory);

            var captureIdsChanged = new FileCalibrationWizardBackend(profilePath)
                .FindReusableProfiles(ScriptedCalibrationWizardRuntime.Devices with
                {
                    LeftControllerSerial = "META-CAPTURE-LEFT-NEW-SESSION",
                    RightControllerSerial = "META-CAPTURE-RIGHT-NEW-SESSION",
                    Recalibration = observations,
                });
            Assert.True(captureIdsChanged.HasCompletePair, captureIdsChanged.Diagnostic);

            var swappedTrackers = new FileCalibrationWizardBackend(profilePath)
                .FindReusableProfiles(ScriptedCalibrationWizardRuntime.Devices with
                {
                    Recalibration = observations with
                    {
                        ObservedLeftTrackerSerial =
                            ScriptedCalibrationWizardRuntime.RightTrackerSerial,
                        ObservedRightTrackerSerial =
                            ScriptedCalibrationWizardRuntime.LeftTrackerSerial,
                    },
                });
            Assert.False(swappedTrackers.HasCompletePair);
            Assert.Contains(
                nameof(RecalibrationTriggerKind.TrackerHandAssociationChanged),
                swappedTrackers.Diagnostic,
                StringComparison.Ordinal);

            var crossRuntime = new FileCalibrationWizardBackend(profilePath)
                .FindReusableProfiles(ScriptedCalibrationWizardRuntime.Devices with
                {
                    Recalibration = observations with
                    {
                        ControllerRuntime = ControllerRuntimeIdentities.LegacyAlvr,
                    },
                });
            Assert.False(crossRuntime.HasCompletePair);
            Assert.Contains(
                nameof(RecalibrationTriggerKind.ControllerIdentityChanged),
                crossRuntime.Diagnostic,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

internal sealed class TranscriptWizardOutput : ICalibrationWizardOutput, IDisposable
{
    private readonly StringWriter _writer = new(System.Globalization.CultureInfo.InvariantCulture);
    private readonly ConsoleCalibrationWizardOutput _console;

    public TranscriptWizardOutput()
    {
        _console = new ConsoleCalibrationWizardOutput(_writer);
    }

    public string Text => _writer.ToString();

    public List<CalibrationWizardCaptureProgress> Progress { get; } = [];

    public void OnStateChanged(CalibrationWizardState state, string diagnostic) =>
        _console.OnStateChanged(state, diagnostic);

    public void OnCaptureProgress(CalibrationWizardCaptureProgress progress)
    {
        Progress.Add(progress);
        _console.OnCaptureProgress(progress);
    }

    public void WriteLine(string message) => _console.WriteLine(message);

    public void Dispose() => _writer.Dispose();
}
