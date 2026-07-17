using System.Globalization;
using System.Text;
using Ltb.Core;

namespace Ltb.Calibration;

/// <summary>Policy requested by the caller for one calibration solve.</summary>
public enum CalibrationPolicy
{
    RotationOnly,
    FullSixDof,
    Auto,
}

/// <summary>Model selected after observability and validation gates.</summary>
public enum CalibrationModel
{
    Failed,
    RotationOnly,
    FullSixDof,
}

/// <summary>A machine-readable reason that a calibration stage was not observable or accepted.</summary>
public enum CalibrationDegeneracy
{
    None,
    NotRequested,
    InsufficientSamples,
    TimestampMismatch,
    MissingOrientation,
    StaticMotion,
    SingleAxisRotation,
    RotationQualityRejected,
    MissingPosition,
    TranslationUnobservable,
    TranslationIllConditioned,
    TranslationUnstable,
    ImplausibleTranslation,
    PositionQualityRejected,
}

/// <summary>
/// Numeric gates for deterministic offline calibration. Initial values are
/// deliberately configurable because hardware recordings, not the Milestone 0
/// synthetic proof, must determine final product thresholds.
/// </summary>
public sealed record CalibrationOptions
{
    public int MinimumSampleCount { get; init; } = 8;

    public int MaximumMotionPairs { get; init; } = 256;

    public double MaximumTimestampDifferenceSeconds { get; init; } =
        SynchronizedPosePair.TimestampMatchToleranceSeconds;

    public double MinimumRelativeRotationDegrees { get; init; } = 2d;

    public double MaximumRelativeRotationDegrees { get; init; } = 170d;

    /// <summary>
    /// Minimum ratio of the second-largest to largest motion-axis tensor
    /// eigenvalue. Zero represents one-axis motion; richer motion approaches one.
    /// </summary>
    public double MinimumRotationAxisCoverage { get; init; } = 0.04d;

    public double MaximumRotationRmsDegrees { get; init; } = 2.5d;

    public double ResidualPercentile { get; init; } = 0.95d;

    public double ValidationFraction { get; init; } = 0.25d;

    public double MinimumPositionSampleFraction { get; init; } = 0.6d;

    /// <summary>Minimum eigenvalue of the per-motion translation normal matrix.</summary>
    public double MinimumTranslationEigenvalue { get; init; } = 1e-4d;

    public double MaximumTranslationConditionNumber { get; init; } = 500d;

    public double MaximumTranslationSplitDisagreementMillimeters { get; init; } = 5d;

    public double MaximumTranslationMagnitudeMeters { get; init; } = 0.5d;

    public double MaximumPositionRmsMillimeters { get; init; } = 40d;

    public double MinimumPositionImprovementMillimeters { get; init; } = 0.5d;

    public double MinimumPositionImprovementFraction { get; init; } = 0.02d;

    public double MinimumValidationInlierRatio { get; init; } = 0.7d;

    internal void Validate()
    {
        if (MinimumSampleCount < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumSampleCount));
        }

        if (MaximumMotionPairs < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumMotionPairs));
        }

        RequireFiniteRange(MaximumTimestampDifferenceSeconds, 0d,
            SynchronizedPosePair.TimestampMatchToleranceSeconds,
            nameof(MaximumTimestampDifferenceSeconds), lowerInclusive: false);
        RequireFiniteRange(MinimumRelativeRotationDegrees, 0d, 180d,
            nameof(MinimumRelativeRotationDegrees), lowerInclusive: false, upperInclusive: false);
        RequireFiniteRange(MaximumRelativeRotationDegrees, MinimumRelativeRotationDegrees, 180d,
            nameof(MaximumRelativeRotationDegrees), lowerInclusive: false, upperInclusive: true);
        RequireFiniteRange(MinimumRotationAxisCoverage, 0d, 1d,
            nameof(MinimumRotationAxisCoverage), lowerInclusive: false);
        RequireFiniteRange(MaximumRotationRmsDegrees, 0d, 180d,
            nameof(MaximumRotationRmsDegrees), lowerInclusive: false);
        RequireFiniteRange(ResidualPercentile, 0.5d, 1d,
            nameof(ResidualPercentile));
        RequireFiniteRange(ValidationFraction, 0d, 0.5d,
            nameof(ValidationFraction), lowerInclusive: false);
        RequireFiniteRange(MinimumPositionSampleFraction, 0d, 1d,
            nameof(MinimumPositionSampleFraction), lowerInclusive: false);
        RequirePositive(MinimumTranslationEigenvalue, nameof(MinimumTranslationEigenvalue));
        RequirePositive(MaximumTranslationConditionNumber, nameof(MaximumTranslationConditionNumber));
        RequirePositive(MaximumTranslationSplitDisagreementMillimeters,
            nameof(MaximumTranslationSplitDisagreementMillimeters));
        RequirePositive(MaximumTranslationMagnitudeMeters, nameof(MaximumTranslationMagnitudeMeters));
        RequirePositive(MaximumPositionRmsMillimeters, nameof(MaximumPositionRmsMillimeters));
        RequireFiniteRange(MinimumPositionImprovementMillimeters, 0d, double.MaxValue,
            nameof(MinimumPositionImprovementMillimeters));
        RequireFiniteRange(MinimumPositionImprovementFraction, 0d, 1d,
            nameof(MinimumPositionImprovementFraction));
        RequireFiniteRange(MinimumValidationInlierRatio, 0.5d, 1d,
            nameof(MinimumValidationInlierRatio));
    }

    private static void RequirePositive(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0d)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void RequireFiniteRange(
        double value,
        double minimum,
        double maximum,
        string parameterName,
        bool lowerInclusive = true,
        bool upperInclusive = true)
    {
        var belowMinimum = lowerInclusive ? value < minimum : value <= minimum;
        var aboveMaximum = upperInclusive ? value > maximum : value >= maximum;
        if (!double.IsFinite(value) || belowMinimum || aboveMaximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

/// <summary>Motion coverage and translation-system observability diagnostics.</summary>
public sealed record MotionObservability(
    bool RotationObservable,
    bool TranslationObservable,
    CalibrationDegeneracy RotationDegeneracy,
    CalibrationDegeneracy TranslationDegeneracy,
    double RotationAxisCoverage,
    double TranslationConditionNumber);

/// <summary>Validation residuals and physical magnitude metrics with explicit units.</summary>
public sealed record CalibrationQualityMetrics(
    double RotationRmsDegrees,
    double RotationPercentileDegrees,
    double? PositionRmsMillimeters,
    double? PositionPercentileMillimeters,
    double? RotationOnlyPositionRmsMillimeters,
    double TranslationMagnitudeMeters)
{
    public double RotationInlierRatio { get; init; } = double.NaN;

    public double TranslationInlierRatio { get; init; } = double.NaN;

    public double TranslationSplitDisagreementMillimeters { get; init; } = double.NaN;
}

/// <summary>Immutable outcome of a staged hand-eye calibration attempt.</summary>
public sealed record CalibrationResult(
    CalibrationPolicy RequestedPolicy,
    CalibrationModel SelectedModel,
    string SelectionReason,
    RigidTransform TrackerToController,
    CalibrationQualityMetrics Quality,
    MotionObservability Motion,
    int SampleCount,
    int RotationValidSampleCount,
    int PositionValidSampleCount,
    int MotionPairCount,
    int ValidationMotionPairCount)
{
    public bool Success => SelectedModel is not CalibrationModel.Failed;
}

/// <summary>A stable human-readable console report for one calibration result.</summary>
public sealed record CalibrationReport(CalibrationResult Result)
{
    public override string ToString()
    {
        ArgumentNullException.ThrowIfNull(Result);

        var culture = CultureInfo.InvariantCulture;
        var quality = Result.Quality;
        var motion = Result.Motion;
        var builder = new StringBuilder();
        builder.AppendLine("Lighthouse Touch Bridge - Offline Calibration Report");
        builder.AppendLine($"Requested policy: {Result.RequestedPolicy}");
        builder.AppendLine($"Selected model: {Result.SelectedModel}");
        builder.AppendLine($"Selection reason: {Result.SelectionReason}");
        builder.AppendLine($"Samples: {Result.SampleCount} total, {Result.RotationValidSampleCount} orientation-valid, {Result.PositionValidSampleCount} position-valid");
        builder.AppendLine($"Relative motions: {Result.MotionPairCount} solve, {Result.ValidationMotionPairCount} validation");
        builder.AppendLine($"Rotation observable: {motion.RotationObservable} ({motion.RotationDegeneracy})");
        builder.AppendLine($"Motion-axis coverage: {motion.RotationAxisCoverage.ToString("F4", culture)}");
        builder.AppendLine($"Rotation RMS / p95: {quality.RotationRmsDegrees.ToString("F4", culture)} deg / {quality.RotationPercentileDegrees.ToString("F4", culture)} deg");
        builder.AppendLine($"Rotation validation inliers: {FormatRatio(quality.RotationInlierRatio, culture)}");
        builder.AppendLine($"Translation observable: {motion.TranslationObservable} ({motion.TranslationDegeneracy})");
        builder.AppendLine($"Translation condition: {FormatFinite(motion.TranslationConditionNumber, "F3", culture)}");
        builder.AppendLine($"Translation magnitude: {(quality.TranslationMagnitudeMeters * 1000d).ToString("F3", culture)} mm");
        builder.AppendLine($"Translation validation inliers: {FormatRatio(quality.TranslationInlierRatio, culture)}");
        builder.AppendLine($"Translation split disagreement: {FormatFinite(quality.TranslationSplitDisagreementMillimeters, "F3", culture)} mm");

        if (quality.PositionRmsMillimeters is { } positionRms &&
            quality.PositionPercentileMillimeters is { } positionPercentile)
        {
            builder.AppendLine($"Full-model position RMS / p95: {positionRms.ToString("F3", culture)} mm / {positionPercentile.ToString("F3", culture)} mm");
        }

        if (quality.RotationOnlyPositionRmsMillimeters is { } rotationOnlyRms)
        {
            builder.AppendLine($"Rotation-only position RMS: {rotationOnlyRms.ToString("F3", culture)} mm");
        }

        var transform = Result.TrackerToController;
        builder.AppendLine(
            $"X_mount translation m: [{transform.TranslationMeters.X.ToString("F6", culture)}, {transform.TranslationMeters.Y.ToString("F6", culture)}, {transform.TranslationMeters.Z.ToString("F6", culture)}]");
        builder.Append(
            $"X_mount rotation XYZW: [{transform.Rotation.X.ToString("F8", culture)}, {transform.Rotation.Y.ToString("F8", culture)}, {transform.Rotation.Z.ToString("F8", culture)}, {transform.Rotation.W.ToString("F8", culture)}]");
        return builder.ToString();
    }

    private static string FormatFinite(double value, string format, CultureInfo culture) =>
        double.IsFinite(value) ? value.ToString(format, culture) : "unavailable";

    private static string FormatRatio(double value, CultureInfo culture) =>
        double.IsFinite(value) ? value.ToString("P1", culture) : "unavailable";
}
