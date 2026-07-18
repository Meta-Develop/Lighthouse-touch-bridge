using System.Numerics;
using Ltb.Core;

namespace Ltb.Configuration;

/// <summary>The controller side to which a persisted tracker profile applies.</summary>
public enum ControllerHand
{
    Left,
    Right,
}

/// <summary>The calibration policy requested when a profile was produced.</summary>
public enum ProfileCalibrationPolicy
{
    RotationOnly,
    FullSixDof,
    Auto,
}

/// <summary>The calibration model selected by validation and persisted in a profile.</summary>
public enum ProfileCalibrationMode
{
    RotationOnly,
    FullSixDof,
}

/// <summary>Constants defining calibration profile schema version 1.</summary>
public static class CalibrationProfileSchema
{
    public const int CurrentVersion = 1;

    /// <summary>
    /// The schema-1 transform convention: <c>T_T_C</c>, translation in meters,
    /// and quaternion components serialized in XYZW order.
    /// </summary>
    public const string TransformConvention = "T_T_C:translation_m:rotation_xyzw";
}

/// <summary>
/// A schema-independent representation of <c>T_T_C</c>, the
/// tracker-to-controller mount transform. Per the <c>T_parent_child</c>
/// convention (parent = physical tracker frame <c>T</c>, child = controller
/// frame <c>C</c>) it maps controller-frame coordinates into the physical
/// tracker frame and composes at runtime as <c>T_L_tracker * T_T_C</c>.
/// Translation is in meters and <see cref="RotationXyzw"/> is a normalized
/// quaternion.
/// </summary>
public sealed record TrackerToControllerTransform
{
    private const float MaximumQuaternionNormError = 0.00025f;

    public TrackerToControllerTransform(Vector3 translationMeters, Quaternion rotationXyzw)
    {
        var rotationNorm = rotationXyzw.Length();
        if (!float.IsFinite(rotationNorm) ||
            MathF.Abs(rotationNorm - 1f) > MaximumQuaternionNormError)
        {
            throw new ArgumentException(
                "Tracker-to-controller rotation must have unit length within 0.00025.",
                nameof(rotationXyzw));
        }

        var transform = new RigidTransform(rotationXyzw, translationMeters);
        TranslationMeters = transform.TranslationMeters;
        RotationXyzw = transform.Rotation;
    }

    public Vector3 TranslationMeters { get; }

    public Quaternion RotationXyzw { get; }

    public RigidTransform ToRigidTransform() => new(RotationXyzw, TranslationMeters);

    public static TrackerToControllerTransform FromRigidTransform(RigidTransform transform)
    {
        if (!transform.IsValid)
        {
            throw new ArgumentException("Tracker-to-controller transform must be valid.", nameof(transform));
        }

        return new TrackerToControllerTransform(transform.TranslationMeters, transform.Rotation);
    }
}

/// <summary>Persisted validation metrics with the units required by schema version 1.</summary>
public sealed record CalibrationProfileQuality
{
    public CalibrationProfileQuality(
        double rotationRmsDegrees,
        double? positionRmsMillimeters,
        double? translationCondition,
        double inlierRatio)
    {
        ProfileValidation.RequireFiniteNonNegative(rotationRmsDegrees, nameof(rotationRmsDegrees));
        ProfileValidation.RequireNullableFiniteNonNegative(positionRmsMillimeters, nameof(positionRmsMillimeters));
        ProfileValidation.RequireNullableFiniteNonNegative(translationCondition, nameof(translationCondition));
        ProfileValidation.RequireFiniteRange(inlierRatio, 0d, 1d, nameof(inlierRatio));

        RotationRmsDegrees = rotationRmsDegrees;
        PositionRmsMillimeters = positionRmsMillimeters;
        TranslationCondition = translationCondition;
        InlierRatio = inlierRatio;
    }

    public double RotationRmsDegrees { get; }

    /// <summary>
    /// Held-out position RMS in millimeters, or <see langword="null"/> when
    /// position was unavailable for a rotation-only calibration.
    /// </summary>
    public double? PositionRmsMillimeters { get; }

    /// <summary>
    /// Translation-system condition number, or <see langword="null"/> when
    /// translation was not observable or attempted.
    /// </summary>
    public double? TranslationCondition { get; }

    public double InlierRatio { get; }
}

/// <summary>
/// One complete schema-version-1 calibration profile. Candidate profiles are
/// located by the exact <see cref="TrackerSerial"/> plus <see cref="Hand"/>
/// pair, then checked against the currently observed controller runtime and
/// model. Collection order and controller serial are not matching keys.
/// </summary>
public sealed record CalibrationProfile
{
    public CalibrationProfile(
        int schemaVersion,
        string profileName,
        ControllerHand hand,
        string controllerRuntime,
        string controllerModel,
        string? controllerSerial,
        string trackerSerial,
        ProfileCalibrationPolicy calibrationPolicy,
        ProfileCalibrationMode selectedMode,
        string selectionReason,
        TrackerToControllerTransform trackerToController,
        double estimatedLagMilliseconds,
        CalibrationProfileQuality quality,
        DateTimeOffset createdUtc)
    {
        if (schemaVersion != CalibrationProfileSchema.CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                $"Only calibration profile schema version {CalibrationProfileSchema.CurrentVersion} is supported.");
        }

        ProfileValidation.RequireDefined(hand, nameof(hand));
        ProfileValidation.RequireDefined(calibrationPolicy, nameof(calibrationPolicy));
        ProfileValidation.RequireDefined(selectedMode, nameof(selectedMode));

        if (calibrationPolicy == ProfileCalibrationPolicy.RotationOnly &&
            selectedMode != ProfileCalibrationMode.RotationOnly)
        {
            throw new ArgumentException(
                "A rotation-only calibration policy can only persist a rotation-only selected mode.",
                nameof(selectedMode));
        }

        if (calibrationPolicy == ProfileCalibrationPolicy.FullSixDof &&
            selectedMode != ProfileCalibrationMode.FullSixDof)
        {
            throw new ArgumentException(
                "A full-6DoF calibration policy can only persist a full-6DoF selected mode.",
                nameof(selectedMode));
        }

        var validatedQuality = quality ?? throw new ArgumentNullException(nameof(quality));
        if (selectedMode == ProfileCalibrationMode.FullSixDof &&
            (validatedQuality.PositionRmsMillimeters is null ||
             validatedQuality.TranslationCondition is null))
        {
            throw new ArgumentException(
                "A full-6DoF selected mode requires position RMS and translation-condition quality evidence.",
                nameof(quality));
        }

        SchemaVersion = schemaVersion;
        ProfileName = ProfileValidation.RequireText(profileName, nameof(profileName));
        Hand = hand;
        ControllerRuntime = ProfileValidation.RequireText(controllerRuntime, nameof(controllerRuntime));
        ControllerModel = ProfileValidation.RequireText(controllerModel, nameof(controllerModel));
        ControllerSerial = ProfileValidation.OptionalIdentity(controllerSerial, nameof(controllerSerial));
        TrackerSerial = ProfileValidation.RequireIdentity(trackerSerial, nameof(trackerSerial));
        CalibrationPolicy = calibrationPolicy;
        SelectedMode = selectedMode;
        SelectionReason = ProfileValidation.RequireText(selectionReason, nameof(selectionReason));
        TrackerToController = trackerToController ?? throw new ArgumentNullException(nameof(trackerToController));

        if (selectedMode == ProfileCalibrationMode.RotationOnly &&
            trackerToController.TranslationMeters != Vector3.Zero)
        {
            throw new ArgumentException(
                "Rotation-only profiles must store exactly zero tracker-to-controller translation.",
                nameof(trackerToController));
        }

        if (!double.IsFinite(estimatedLagMilliseconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(estimatedLagMilliseconds),
                "Estimated lag must be finite.");
        }

        EstimatedLagMilliseconds = estimatedLagMilliseconds;
        Quality = validatedQuality;

        if (createdUtc == default)
        {
            throw new ArgumentOutOfRangeException(nameof(createdUtc), "Creation time must be set.");
        }

        CreatedUtc = createdUtc.ToUniversalTime();
    }

    public int SchemaVersion { get; }

    public string ProfileName { get; }

    public ControllerHand Hand { get; }

    public string ControllerRuntime { get; }

    public string ControllerModel { get; }

    public string? ControllerSerial { get; }

    public string TrackerSerial { get; }

    public ProfileCalibrationPolicy CalibrationPolicy { get; }

    public ProfileCalibrationMode SelectedMode { get; }

    public string SelectionReason { get; }

    public TrackerToControllerTransform TrackerToController { get; }

    public double EstimatedLagMilliseconds { get; }

    public CalibrationProfileQuality Quality { get; }

    public DateTimeOffset CreatedUtc { get; }

    /// <summary>
    /// Returns whether current controller observations are compatible with
    /// this profile. Compatibility is intentionally driven by the persisted
    /// schema-1 runtime/model values rather than model-specific code branches.
    /// </summary>
    public bool MatchesController(string controllerRuntime, string controllerModel)
    {
        var runtime = ProfileValidation.RequireText(
            controllerRuntime,
            nameof(controllerRuntime));
        var model = ProfileValidation.RequireText(
            controllerModel,
            nameof(controllerModel));
        return string.Equals(runtime, ControllerRuntime, StringComparison.Ordinal) &&
            string.Equals(model, ControllerModel, StringComparison.Ordinal);
    }
}

internal static class ProfileValidation
{
    internal static string RequireText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty or whitespace.", parameterName);
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("Value must not contain control characters.", parameterName);
        }

        return value;
    }

    internal static string RequireIdentity(string value, string parameterName)
    {
        var identity = RequireText(value, parameterName);
        if (char.IsWhiteSpace(identity[0]) || char.IsWhiteSpace(identity[^1]))
        {
            throw new ArgumentException("Identity must not contain leading or trailing whitespace.", parameterName);
        }

        return identity;
    }

    internal static string? OptionalIdentity(string? value, string parameterName) =>
        value is null ? null : RequireIdentity(value, parameterName);

    internal static void RequireFiniteNonNegative(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0d)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Value must be finite and non-negative.");
        }
    }

    internal static void RequireNullableFiniteNonNegative(double? value, string parameterName)
    {
        if (value is { } present)
        {
            RequireFiniteNonNegative(present, parameterName);
        }
    }

    internal static void RequireFiniteRange(double value, double minimum, double maximum, string parameterName)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Value must be finite and between {minimum} and {maximum}, inclusive.");
        }
    }

    internal static void RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value is not defined.");
        }
    }
}
