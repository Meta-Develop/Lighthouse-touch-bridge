using Ltb.Calibration;
using Ltb.Configuration;
using Ltb.Core;

namespace Ltb.App;

/// <summary>
/// Thin composition adapter over the portable calibration and configuration
/// libraries. Numeric gates and schema rules remain owned by those libraries.
/// </summary>
public sealed class FileCalibrationWizardBackend : ICalibrationWizardBackend
{
    private readonly string _profileStorePath;
    private readonly Func<DateTimeOffset> _utcNow;
    private bool _replaceIncompatibleStore;

    public FileCalibrationWizardBackend(
        string profileStorePath,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileStorePath);
        _profileStorePath = profileStorePath;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public CalibrationWizardProfileLookup FindReusableProfiles(
        CalibrationWizardDeviceSet devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _replaceIncompatibleStore = false;
        if (!File.Exists(_profileStorePath))
        {
            return EmptyLookup("profile store does not exist; first-run capture is required");
        }

        CalibrationProfileStore store;
        try
        {
            store = CalibrationProfileFile.LoadStore(_profileStorePath);
        }
        catch (CalibrationProfileFormatException exception) when (
            exception.Reason is CalibrationProfileFormatReason.UnsupportedSchemaVersion)
        {
            _replaceIncompatibleStore = true;
            return EmptyLookup(
                $"recalibration trigger {RecalibrationTriggerKind.SchemaVersionChanged}: " +
                $"the stored profile schema is unsupported; a new schema-" +
                $"{devices.Recalibration.ExpectedSchemaVersion} capture " +
                "will replace the incompatible store.");
        }

        var left = FindSingleReusable(
            store,
            devices,
            ControllerHand.Left,
            out var leftDiagnostic);
        var right = FindSingleReusable(
            store,
            devices,
            ControllerHand.Right,
            out var rightDiagnostic);
        if (left is null || right is null ||
            string.Equals(left.TrackerSerial, right.TrackerSerial, StringComparison.Ordinal))
        {
            return EmptyLookup(
                $"complete reusable pair not found; left={leftDiagnostic}; right={rightDiagnostic}");
        }

        return new CalibrationWizardProfileLookup(
            [ToView(left), ToView(right)],
            (left.IsLegacy
                ? "loaded deprecated ALVR/VMT schema-1 profiles for each "
                : $"loaded reusable schema-{left.SchemaVersion} profiles for each ") +
            "exact tracker serial, hand, and controller identity");
    }

    public CalibrationWizardAnalysis AnalyzeFirstRun(
        CalibrationWizardDeviceSet devices,
        CalibrationWizardCapture leftCapture,
        CalibrationWizardCapture rightCapture)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var left = ToHandCapture(
            leftCapture,
            CalibrationHand.Left,
            devices.LeftControllerSerial);
        var right = ToHandCapture(
            rightCapture,
            CalibrationHand.Right,
            devices.RightControllerSerial);
        var association = TrackerHandAssociator.Associate(left, right);
        if (!association.Success)
        {
            throw new CalibrationWizardRunException(
                CalibrationWizardState.Association,
                $"Tracker association rejected ({association.Status}): {association.Reason}");
        }

        var leftAnalysis = Calibrate(
            leftCapture,
            association.Left!,
            devices.LeftControllerSerial,
            CalibrationWizardHand.Left);
        var rightAnalysis = Calibrate(
            rightCapture,
            association.Right!,
            devices.RightControllerSerial,
            CalibrationWizardHand.Right);
        return new CalibrationWizardAnalysis(
            new CalibrationWizardAssociation(
                association.Left!.TrackerSerial,
                association.Right!.TrackerSerial,
                SelectedCorrelation(association, CalibrationHand.Left),
                SelectedCorrelation(association, CalibrationHand.Right),
                association.InputOrderWasSwapped,
                association.Reason),
            leftAnalysis,
            rightAnalysis)
        {
            Recalibration = devices.Recalibration,
        };
    }

    public IReadOnlyList<CalibrationWizardProfileView> SaveProfiles(
        CalibrationWizardAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var createdUtc = _utcNow().ToUniversalTime();
        if (createdUtc == default)
        {
            throw new InvalidOperationException("The profile creation clock returned an unset time.");
        }

        var profiles = analysis.Hands
            .Select(hand => ToProfile(hand, analysis.Recalibration, createdUtc))
            .ToArray();
        var store = File.Exists(_profileStorePath) && !_replaceIncompatibleStore
            ? CalibrationProfileFile.LoadStore(_profileStorePath)
            : CalibrationProfileStore.Empty;
        foreach (var profile in profiles)
        {
            store = store.Upsert(profile);
        }

        CalibrationProfileFile.SaveStore(_profileStorePath, store);
        _replaceIncompatibleStore = false;
        var reloaded = CalibrationProfileFile.LoadStore(_profileStorePath);
        return profiles
            .Select(profile => FindReloadedProfile(reloaded, profile)
                ?? throw new InvalidDataException(
                    $"Saved profile for {profile.Hand} and '{profile.TrackerSerial}' did not reload."))
            .Select(ToView)
            .ToArray();
    }

    private static CalibrationProfile? FindSingleReusable(
        CalibrationProfileStore store,
        CalibrationWizardDeviceSet devices,
        ControllerHand hand,
        out string diagnostic)
    {
        var candidates = devices.TrackerSerials
            .Select(serial => store.FindCandidateProfile(serial, hand))
            .Where(profile => profile is not null)
            .Cast<CalibrationProfile>()
            .ToArray();
        var matches = new List<CalibrationProfile>();
        var triggers = new List<RecalibrationTrigger>();
        foreach (var profile in candidates)
        {
            var wizardHand = ToWizardHand(hand);
            var currentTrackerSerial = devices.Recalibration
                .ObservedTrackerSerial(wizardHand) ?? profile.TrackerSerial;
            var evaluation = RecalibrationEvaluator.Evaluate(
                profile,
                new RecalibrationContext(
                    ExplicitRequest: devices.Recalibration.ExplicitRequest,
                    TrackerSerial: currentTrackerSerial,
                    Hand: hand,
                    ControllerRuntime: devices.Recalibration.ControllerRuntime,
                    ControllerModel: devices.Recalibration.ControllerModel,
                    MountMoved: devices.Recalibration.MountMoved,
                    ValidationThresholdExceeded:
                        devices.Recalibration.ValidationThresholdExceeded,
                    DriverProfile: devices.Recalibration.DriverProfile,
                    ControllerIdentity: PersistentControllerIdentity(devices, hand),
                    ExpectedSchemaVersion: devices.Recalibration.ExpectedSchemaVersion,
                    ExpectedTransformConvention:
                        devices.Recalibration.ExpectedTransformConvention));
            if (evaluation.IsRequired)
            {
                triggers.AddRange(evaluation.Triggers);
            }
            else
            {
                matches.Add(profile);
            }
        }

        if (matches.Count != 1)
        {
            diagnostic = triggers.Count > 0
                ? string.Join(
                    "; ",
                    triggers
                        .DistinctBy(trigger => trigger.Kind)
                        .Select(trigger => $"{trigger.Kind}: {trigger.Message}"))
                : matches.Count == 0
                    ? "no exact serial-and-hand profile"
                : "multiple tracker profiles claim this hand";
            return null;
        }

        diagnostic = $"matched tracker {matches[0].TrackerSerial}";
        return matches[0];
    }

    private static HandMotionCapture ToHandCapture(
        CalibrationWizardCapture capture,
        CalibrationHand expectedHand,
        string controllerSerial)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var controllerStream = capture.Recording.Streams.SingleOrDefault(stream =>
            stream.Identity.SourceKind == PoseSourceKind.InputController &&
            string.Equals(
                stream.Identity.DeviceId,
                controllerSerial,
                StringComparison.Ordinal))
            ?? throw new InvalidDataException(
                $"{expectedHand} capture does not contain controller '{controllerSerial}'.");
        var trackers = capture.Recording.Streams
            .Where(stream => stream.Identity.SourceKind == PoseSourceKind.TrackedPose)
            .Select(stream => new TrackerAssociationCandidate(
                stream.Identity.DeviceId,
                ToPoseSamples(stream),
                stream.Samples.Any(sample => sample.IsConnected)))
            .ToArray();
        return new HandMotionCapture(
            expectedHand,
            ToPoseSamples(controllerStream),
            trackers);
    }

    private static CalibrationWizardHandAnalysis Calibrate(
        CalibrationWizardCapture capture,
        HandTrackerAssignment assignment,
        string controllerSerial,
        CalibrationWizardHand hand)
    {
        var controller = capture.Recording.Streams.Single(stream =>
            stream.Identity.SourceKind == PoseSourceKind.InputController &&
            string.Equals(
                stream.Identity.DeviceId,
                controllerSerial,
                StringComparison.Ordinal));
        var tracker = capture.Recording.Streams.Single(stream =>
            stream.Identity.SourceKind == PoseSourceKind.TrackedPose &&
            string.Equals(
                stream.Identity.DeviceId,
                assignment.TrackerSerial,
                StringComparison.Ordinal));
        var result = PerHandCalibrationPipeline.Run(
            new HandCalibrationInput(
                assignment.Hand,
                assignment.TrackerSerial,
                ToPoseSamples(tracker),
                ToPoseSamples(controller)),
            new HandCalibrationPipelineOptions
            {
                CalibrationPolicy = CalibrationPolicy.Auto,
            });
        if (!result.Success || result.Lag is null || result.Calibration is null)
        {
            var state = FailureState(result.Failure);
            throw new CalibrationWizardRunException(
                state,
                $"{hand} calibration rejected ({result.Failure}): {result.Reason}");
        }

        return new CalibrationWizardHandAnalysis(
            hand,
            controllerSerial,
            assignment.TrackerSerial,
            result.Lag,
            result.Calibration);
    }

    private static IReadOnlyList<TimestampedPoseSample> ToPoseSamples(
        PoseStreamRecording stream) =>
        stream.Samples.Select(sample =>
        {
            var validity = sample.Validity;
            if (!sample.IsConnected)
            {
                validity &= ~PoseValidity.TrackingValid;
            }

            return new TimestampedPoseSample(
                sample.MonotonicHostTimeSeconds,
                sample.Pose,
                validity);
        }).ToArray();

    private static double SelectedCorrelation(
        TrackerAssociationResult association,
        CalibrationHand hand)
    {
        var assignment = hand == CalibrationHand.Left
            ? association.Left!
            : association.Right!;
        return association.Scores.Single(score =>
            score.Hand == hand &&
            string.Equals(
                score.TrackerSerial,
                assignment.TrackerSerial,
                StringComparison.Ordinal)).CorrelationScore;
    }

    private static CalibrationProfile ToProfile(
        CalibrationWizardHandAnalysis hand,
        CalibrationWizardRecalibrationObservations recalibration,
        DateTimeOffset createdUtc)
    {
        var result = hand.Calibration;
        var mode = result.SelectedModel switch
        {
            CalibrationModel.FullSixDof => ProfileCalibrationMode.FullSixDof,
            CalibrationModel.RotationOnly => ProfileCalibrationMode.RotationOnly,
            _ => throw new InvalidOperationException(
                $"A failed {hand.Hand} calibration cannot be persisted."),
        };
        var quality = result.Quality;
        double? translationCondition = double.IsFinite(result.Motion.TranslationConditionNumber)
            ? result.Motion.TranslationConditionNumber
            : null;
        var inlierRatio = mode == ProfileCalibrationMode.FullSixDof &&
            double.IsFinite(quality.TranslationInlierRatio)
                ? quality.TranslationInlierRatio
                : quality.RotationInlierRatio;
        if (!double.IsFinite(inlierRatio))
        {
            throw new InvalidOperationException(
                $"The accepted {hand.Hand} result has no finite inlier ratio.");
        }

        var profileName = $"LTB {hand.Hand.ToString().ToLowerInvariant()} Auto calibration";
        var controllerHand = ToControllerHand(hand.Hand);
        var transform = TrackerToControllerTransform.FromRigidTransform(
            result.TrackerToController);
        var persistedQuality = new CalibrationProfileQuality(
            quality.RotationRmsDegrees,
            quality.PositionRmsMillimeters,
            translationCondition,
            inlierRatio);
        var persistentControllerIdentity = PersistentControllerIdentity(
            recalibration,
            hand.Hand,
            hand.ControllerSerial);

        return recalibration.ExpectedSchemaVersion switch
        {
            CalibrationProfileSchema.LegacyVersion => new CalibrationProfile(
                CalibrationProfileSchema.LegacyVersion,
                profileName,
                controllerHand,
                recalibration.ControllerRuntime,
                recalibration.ControllerModel,
                persistentControllerIdentity,
                hand.TrackerSerial,
                ProfileCalibrationPolicy.Auto,
                mode,
                result.SelectionReason,
                transform,
                hand.Lag.LagSeconds * 1000d,
                persistedQuality,
                createdUtc),
            CalibrationProfileSchema.CurrentVersion => new CalibrationProfile(
                CalibrationProfileSchema.CurrentVersion,
                profileName,
                controllerHand,
                recalibration.ControllerRuntime,
                recalibration.ControllerModel,
                persistentControllerIdentity,
                hand.TrackerSerial,
                recalibration.DriverProfile
                    ?? throw new InvalidOperationException(
                        "Schema-version-2 calibration requires a driver profile."),
                ProfileCalibrationPolicy.Auto,
                mode,
                result.SelectionReason,
                transform,
                hand.Lag.LagSeconds * 1000d,
                persistedQuality,
                createdUtc),
            _ => throw new InvalidOperationException(
                $"Cannot persist unsupported calibration profile schema version " +
                $"{recalibration.ExpectedSchemaVersion}."),
        };
    }

    internal static CalibrationWizardState FailureState(
        HandCalibrationFailure failure) => failure switch
        {
            HandCalibrationFailure.InvalidCapture => CalibrationWizardState.Recording,
            HandCalibrationFailure.TimeAlignmentRejected => CalibrationWizardState.TimeAlignment,
            HandCalibrationFailure.CalibrationRejected => CalibrationWizardState.RotationSolve,
            _ => CalibrationWizardState.RotationSolve,
        };

    private static CalibrationWizardProfileView ToView(CalibrationProfile profile) =>
        new(
            profile.ProfileName,
            ToWizardHand(profile.Hand),
            profile.ControllerIdentity,
            profile.TrackerSerial,
            profile.SelectedMode == ProfileCalibrationMode.FullSixDof
                ? CalibrationModel.FullSixDof
                : CalibrationModel.RotationOnly,
            profile.SelectionReason,
            profile.TrackerToController.ToRigidTransform(),
            profile.EstimatedLagMilliseconds,
            new CalibrationQualityMetrics(
                profile.Quality.RotationRmsDegrees,
                double.NaN,
                profile.Quality.PositionRmsMillimeters,
                null,
                null,
                profile.TrackerToController.TranslationMeters.Length())
            {
                // The persisted profile stores one undifferentiated inlier ratio and no
                // percentile/split metrics. Do not fabricate richer values on reload.
                RotationInlierRatio = double.NaN,
                TranslationInlierRatio = double.NaN,
                TranslationSplitDisagreementMillimeters = double.NaN,
            },
            profile.CreatedUtc)
        {
            SchemaVersion = profile.SchemaVersion,
            DriverProfile = profile.DriverProfile,
        };

    private static CalibrationProfile? FindReloadedProfile(
        CalibrationProfileStore store,
        CalibrationProfile expected)
    {
        if (!expected.IsLegacy)
        {
            return store.FindMatchingProfile(
                expected.TrackerSerial,
                expected.Hand,
                expected.DriverProfile!,
                expected.ControllerRuntime,
                expected.ControllerModel,
                expected.ControllerIdentity);
        }

        var candidate = store.FindCandidateProfile(expected.TrackerSerial, expected.Hand);
        return candidate is { IsLegacy: true } &&
            candidate.MatchesController(
                expected.ControllerRuntime,
                expected.ControllerModel) &&
            string.Equals(
                candidate.ControllerIdentity,
                expected.ControllerIdentity,
                StringComparison.Ordinal)
            ? candidate
            : null;
    }

    private static string? PersistentControllerIdentity(
        CalibrationWizardDeviceSet devices,
        ControllerHand hand)
    {
        var wizardHand = ToWizardHand(hand);
        var captureStreamIdentity = hand switch
        {
            ControllerHand.Left => devices.LeftControllerSerial,
            ControllerHand.Right => devices.RightControllerSerial,
            _ => throw new ArgumentOutOfRangeException(nameof(hand)),
        };
        return PersistentControllerIdentity(
            devices.Recalibration,
            wizardHand,
            captureStreamIdentity);
    }

    private static string? PersistentControllerIdentity(
        CalibrationWizardRecalibrationObservations observations,
        CalibrationWizardHand hand,
        string captureStreamIdentity) =>
        observations.ControllerIdentity(hand) ??
        (observations.ExpectedSchemaVersion == CalibrationProfileSchema.LegacyVersion
            ? captureStreamIdentity
            : null);

    private static CalibrationWizardProfileLookup EmptyLookup(string diagnostic) =>
        new(Array.Empty<CalibrationWizardProfileView>(), diagnostic);

    private static ControllerHand ToControllerHand(CalibrationWizardHand hand) => hand switch
    {
        CalibrationWizardHand.Left => ControllerHand.Left,
        CalibrationWizardHand.Right => ControllerHand.Right,
        _ => throw new ArgumentOutOfRangeException(nameof(hand)),
    };

    private static CalibrationWizardHand ToWizardHand(ControllerHand hand) => hand switch
    {
        ControllerHand.Left => CalibrationWizardHand.Left,
        ControllerHand.Right => CalibrationWizardHand.Right,
        _ => throw new ArgumentOutOfRangeException(nameof(hand)),
    };
}
