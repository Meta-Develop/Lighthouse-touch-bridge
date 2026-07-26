using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ltb.Calibration;
using Ltb.Configuration;
using Ltb.MetaLink;

namespace Ltb.App;

/// <summary>
/// Current observations that authorize compatible schema-2 or schema-3
/// profile reuse. Public LibOVR exposes no stable per-controller identity, so
/// the identity is deliberately absent rather than replaced with a
/// capture-stream or SteamVR identity.
/// </summary>
internal sealed record InternalDriverCalibrationContext
{
    public InternalDriverCalibrationContext(
        MetaLinkHand hand,
        string trackerSerial,
        string controllerModel)
    {
        if (!Enum.IsDefined(hand))
        {
            throw new ArgumentOutOfRangeException(nameof(hand));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(trackerSerial);
        ArgumentException.ThrowIfNullOrWhiteSpace(controllerModel);
        Hand = hand;
        TrackerSerial = InternalDriverTrackerSerial.Require(
            trackerSerial,
            nameof(trackerSerial));
        ControllerModel = controllerModel;
    }

    public MetaLinkHand Hand { get; }

    public string TrackerSerial { get; }

    public string ControllerModel { get; }

    public bool ExplicitRequest { get; init; }

    public bool MountMoved { get; init; }

    public bool ValidationThresholdExceeded { get; init; }
}

internal sealed record InternalDriverProfileLookup(
    InternalDriverCalibrationContext Context,
    CalibrationProfile? Profile,
    RecalibrationEvaluation? Recalibration,
    string Diagnostic)
{
    public bool CanReuse => Profile is not null && Recalibration?.IsRequired is false;
}

internal sealed record InternalDriverCalibrationRunResult(
    InternalDriverCalibrationContext Context,
    MetaLinkCalibrationCapture Capture,
    HandCalibrationResult PipelineResult,
    CalibrationProfile? Profile,
    string Diagnostic)
{
    public bool Success => PipelineResult.Success && Profile is not null;

    public IReadOnlyList<MetaLinkCalibrationClockEvidence> ClockEvidence =>
        Capture.ClockEvidence;
}

/// <summary>
/// File-backed first-party calibration/profile adapter. Association and guided
/// capture are owned by the session; this class reuses the existing portable
/// lag/alignment/solver pipeline and the compatible schema-2/schema-3
/// configuration store.
/// </summary>
internal sealed class InternalDriverCalibration
{
    private readonly string _profileStorePath;
    private readonly Func<DateTimeOffset> _utcNow;

    public InternalDriverCalibration(
        string profileStorePath,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileStorePath);
        _profileStorePath = profileStorePath;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public InternalDriverProfileLookup FindReusableProfile(
        InternalDriverCalibrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!File.Exists(_profileStorePath))
        {
            return CalibrationRequired(context, "profile store does not exist");
        }

        CalibrationProfileStore store;
        try
        {
            store = CalibrationProfileFile.LoadStore(_profileStorePath);
        }
        catch (CalibrationProfileFormatException exception) when (
            exception.Reason is CalibrationProfileFormatReason.UnsupportedSchemaVersion)
        {
            return CalibrationRequired(
                context,
                $"stored profile schema is unsupported and will not be overwritten; " +
                $"manual migration or backup is required: {exception.Message}");
        }

        var hand = ToControllerHand(context.Hand);
        var candidates = store.Profiles
            .Where(profile =>
                profile.Hand == hand &&
                string.Equals(
                    profile.TrackerSerial,
                    context.TrackerSerial,
                    StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (candidates.Length == 0)
        {
            return CalibrationRequired(
                context,
                "no profile matches the tracker serial and hand");
        }

        if (candidates.Length > 1)
        {
            throw new InvalidDataException(
                $"Multiple profiles for {context.Hand} tracker '{context.TrackerSerial}' " +
                "differ only by serial casing. Resolve the ambiguous stored identities " +
                "before profile reuse.");
        }

        var candidate = candidates[0];
        var evaluation = RecalibrationEvaluator.Evaluate(
            candidate,
            CurrentRecalibrationContext(context) with
            {
                TrackerSerial = candidate.TrackerSerial,
            });
        if (evaluation.IsRequired)
        {
            return new InternalDriverProfileLookup(
                context,
                null,
                evaluation,
                string.Join(
                    "; ",
                    evaluation.Triggers.Select(trigger =>
                        $"{trigger.Kind}: {trigger.Message}")));
        }

        var exact = candidate.MatchesController(
            CalibrationDriverProfiles.LtbTouch,
            ControllerRuntimeIdentities.MetaLinkLibOvr,
            context.ControllerModel,
            controllerIdentity: null)
                ? candidate
                : null;
        if (exact is null)
        {
            return CalibrationRequired(
                context,
                "profile failed the exact compatible first-party identity match");
        }

        return new InternalDriverProfileLookup(
            context,
            exact,
            evaluation,
            $"reusing schema-{exact.SchemaVersion} {context.Hand} profile for " +
            $"tracker '{context.TrackerSerial}'");
    }

    internal IReadOnlyList<string> FindCandidateTrackerSerials(MetaLinkHand hand)
    {
        if (!Enum.IsDefined(hand))
        {
            throw new ArgumentOutOfRangeException(nameof(hand));
        }

        if (!File.Exists(_profileStorePath))
        {
            return Array.Empty<string>();
        }

        var controllerHand = ToControllerHand(hand);
        return CalibrationProfileFile.LoadStore(_profileStorePath)
            .Profiles
            .Where(profile =>
                profile.Hand == controllerHand &&
                profile.SchemaVersion is
                    CalibrationProfileSchema.DriverProfileVersion or
                    CalibrationProfileSchema.CurrentVersion &&
                string.Equals(
                    profile.DriverProfile,
                    CalibrationDriverProfiles.LtbTouch,
                    StringComparison.Ordinal))
            .Select(profile => InternalDriverTrackerSerial.Require(
                profile.TrackerSerial,
                nameof(profile.TrackerSerial)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(serial => serial, StringComparer.Ordinal)
            .ToArray();
    }

    public InternalDriverCalibrationRunResult CalibrateAndSave(
        InternalDriverCalibrationContext context,
        MetaLinkCalibrationCapture capture,
        HandCalibrationPipelineOptions? options = null,
        string? replacedTrackerSerial = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.Hand != context.Hand ||
            !string.Equals(
                capture.TrackerSerial,
                context.TrackerSerial,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Calibration capture must match the exact context hand and tracker serial.",
                nameof(capture));
        }

        options ??= new HandCalibrationPipelineOptions
        {
            CalibrationPolicy = CalibrationPolicy.Auto,
        };
        var pipeline = PerHandCalibrationPipeline.Run(
            capture.ToCalibrationInput(),
            options);
        if (!pipeline.Success || pipeline.Calibration is null || pipeline.Lag is null)
        {
            return new InternalDriverCalibrationRunResult(
                context,
                capture,
                pipeline,
                null,
                $"{context.Hand} calibration rejected ({pipeline.Failure}): {pipeline.Reason}");
        }

        var createdUtc = _utcNow().ToUniversalTime();
        if (createdUtc == default)
        {
            throw new InvalidOperationException("The profile creation clock returned an unset time.");
        }

        var profile = ToProfile(context, pipeline, options.CalibrationPolicy, createdUtc);
        var store = LoadStoreForSuccessfulRecalibration();
        store = ReplaceSelectedProfile(store, profile, replacedTrackerSerial);
        CalibrationProfileFile.SaveStore(_profileStorePath, store);

        var reloaded = CalibrationProfileFile.LoadStore(_profileStorePath)
            .FindMatchingProfile(
                context.TrackerSerial,
                ToControllerHand(context.Hand),
                CalibrationDriverProfiles.LtbTouch,
                ControllerRuntimeIdentities.MetaLinkLibOvr,
                context.ControllerModel,
                controllerIdentity: null)
            ?? throw new InvalidDataException(
                $"Saved {context.Hand} profile did not reload under its exact first-party identity.");

        return new InternalDriverCalibrationRunResult(
            context,
            capture,
            pipeline,
            reloaded,
            $"saved schema-{reloaded.SchemaVersion} {context.Hand} profile with " +
            $"{capture.ClockEvidence.Count} per-hand Meta clock observations");
    }

    private CalibrationProfileStore LoadStoreForSuccessfulRecalibration()
    {
        if (!File.Exists(_profileStorePath))
        {
            return CalibrationProfileStore.Empty;
        }

        // Unknown or malformed schemas are not disposable input. Loading is
        // intentionally allowed to throw before SaveStore can replace bytes.
        return CalibrationProfileFile.LoadStore(_profileStorePath);
    }

    internal static CalibrationProfileStore ReplaceSelectedProfile(
        CalibrationProfileStore store,
        CalibrationProfile replacement,
        string? replacedTrackerSerial)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(replacement);
        var activeSerial = InternalDriverTrackerSerial.Require(
            replacement.TrackerSerial,
            nameof(replacement));
        var replacedSerial = replacedTrackerSerial is null
            ? activeSerial
            : InternalDriverTrackerSerial.Require(
                replacedTrackerSerial,
                nameof(replacedTrackerSerial));
        var withoutReplacedActiveKey = new CalibrationProfileStore(
            store.Profiles.Where(existing =>
                existing.Hand != replacement.Hand ||
                !string.Equals(
                    existing.TrackerSerial,
                    replacedSerial,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    existing.TrackerSerial,
                    activeSerial,
                    StringComparison.OrdinalIgnoreCase)));
        return withoutReplacedActiveKey.Upsert(replacement);
    }

    private static CalibrationProfile ToProfile(
        InternalDriverCalibrationContext context,
        HandCalibrationResult pipeline,
        CalibrationPolicy policy,
        DateTimeOffset createdUtc)
    {
        var result = pipeline.Calibration!;
        var mode = result.SelectedModel switch
        {
            CalibrationModel.RotationOnly => ProfileCalibrationMode.RotationOnly,
            CalibrationModel.FullSixDof => ProfileCalibrationMode.FullSixDof,
            _ => throw new InvalidOperationException("A failed calibration cannot be persisted."),
        };
        var quality = result.Quality;
        var inlierRatio = mode is ProfileCalibrationMode.FullSixDof &&
            double.IsFinite(quality.TranslationInlierRatio)
                ? quality.TranslationInlierRatio
                : quality.RotationInlierRatio;
        if (!double.IsFinite(inlierRatio))
        {
            throw new InvalidOperationException(
                "An accepted calibration requires finite held-out inlier evidence.");
        }

        var translationCondition = mode is ProfileCalibrationMode.FullSixDof &&
            double.IsFinite(result.Motion.TranslationConditionNumber)
                ? result.Motion.TranslationConditionNumber
                : (double?)null;
        return new CalibrationProfile(
            CalibrationProfileSchema.CurrentVersion,
            $"LTB {context.Hand.ToString().ToLowerInvariant()} internal-driver calibration",
            ToControllerHand(context.Hand),
            ControllerRuntimeIdentities.MetaLinkLibOvr,
            context.ControllerModel,
            controllerIdentity: null,
            InternalDriverTrackerSerial.Require(
                context.TrackerSerial,
                nameof(context.TrackerSerial)),
            CalibrationDriverProfiles.LtbTouch,
            ToProfilePolicy(policy),
            mode,
            result.SelectionReason,
            TrackerToControllerTransform.FromRigidTransform(result.TrackerToController),
            pipeline.Lag!.LagSeconds * 1000d,
            new CalibrationProfileQuality(
                quality.RotationRmsDegrees,
                quality.PositionRmsMillimeters,
                translationCondition,
                inlierRatio),
            createdUtc);
    }

    private static RecalibrationContext CurrentRecalibrationContext(
        InternalDriverCalibrationContext context) => new(
        context.ExplicitRequest,
        context.TrackerSerial,
        ToControllerHand(context.Hand),
        ControllerRuntimeIdentities.MetaLinkLibOvr,
        context.ControllerModel,
        context.MountMoved,
        context.ValidationThresholdExceeded,
        CalibrationDriverProfiles.LtbTouch,
        ControllerIdentity: null,
        CalibrationProfileSchema.CurrentVersion,
        CalibrationProfileSchema.TransformConvention);

    private static InternalDriverProfileLookup CalibrationRequired(
        InternalDriverCalibrationContext context,
        string diagnostic) => new(
        context,
        null,
        null,
        diagnostic);

    private static ControllerHand ToControllerHand(MetaLinkHand hand) => hand switch
    {
        MetaLinkHand.Left => ControllerHand.Left,
        MetaLinkHand.Right => ControllerHand.Right,
        _ => throw new ArgumentOutOfRangeException(nameof(hand)),
    };

    private static ProfileCalibrationPolicy ToProfilePolicy(CalibrationPolicy policy) =>
        policy switch
        {
            CalibrationPolicy.RotationOnly => ProfileCalibrationPolicy.RotationOnly,
            CalibrationPolicy.FullSixDof => ProfileCalibrationPolicy.FullSixDof,
            CalibrationPolicy.Auto => ProfileCalibrationPolicy.Auto,
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };
}
