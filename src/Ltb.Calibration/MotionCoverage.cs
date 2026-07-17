using System.Numerics;
using Ltb.Core;

namespace Ltb.Calibration;

/// <summary>Configurable gates for live capture motion and validity feedback.</summary>
public sealed record MotionCoverageOptions
{
    public double MaximumSampleIntervalSeconds { get; init; } = 0.050d;

    public double MinimumRotationStepDegrees { get; init; } = 0.5d;

    public int MinimumRotationSegmentCount { get; init; } = 8;

    public double MinimumTotalRotationDegrees { get; init; } = 180d;

    public double MinimumRotationAxisCoverage { get; init; } = 0.04d;

    public double MinimumOrientationValidityFraction { get; init; } = 0.75d;

    public double MinimumPositionValidityFraction { get; init; } = 0.60d;

    internal void Validate()
    {
        RequirePositive(MaximumSampleIntervalSeconds, nameof(MaximumSampleIntervalSeconds));
        RequireRange(MinimumRotationStepDegrees, 0d, 180d, nameof(MinimumRotationStepDegrees), false);
        if (MinimumRotationSegmentCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumRotationSegmentCount));
        }

        RequirePositive(MinimumTotalRotationDegrees, nameof(MinimumTotalRotationDegrees));
        RequireRange(MinimumRotationAxisCoverage, 0d, 1d, nameof(MinimumRotationAxisCoverage), false);
        RequireRange(MinimumOrientationValidityFraction, 0d, 1d, nameof(MinimumOrientationValidityFraction), false);
        RequireRange(MinimumPositionValidityFraction, 0d, 1d, nameof(MinimumPositionValidityFraction), false);
    }

    private static void RequirePositive(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0d)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void RequireRange(
        double value,
        double minimum,
        double maximum,
        string parameterName,
        bool lowerInclusive = true)
    {
        if (!double.IsFinite(value) ||
            (lowerInclusive ? value < minimum : value <= minimum) ||
            value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

/// <summary>
/// Coordinate-invariant rotational excitation and tracking-validity feedback for
/// one capture stream. Fractions and progress values are in the range [0, 1].
/// </summary>
public sealed record MotionCoverageResult(
    int SampleCount,
    int RotationSegmentCount,
    double TrackingValidityFraction,
    double OrientationValidityFraction,
    double PositionValidityFraction,
    double TotalRotationDegrees,
    double RotationAxisCoverage,
    double RotationProgress,
    double PositionProgress,
    bool IsRotationSufficient,
    bool IsPositionSufficient);

/// <summary>Evaluates live capture coverage without depending on device or UI layers.</summary>
public static class MotionCoverageAnalyzer
{
    public static MotionCoverageResult Evaluate(
        IReadOnlyList<TimestampedPoseSample> samples,
        MotionCoverageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        options ??= new MotionCoverageOptions();
        options.Validate();
        ValidateStrictlyMonotonic(samples);

        if (samples.Count == 0)
        {
            return new MotionCoverageResult(0, 0, 0d, 0d, 0d, 0d, 0d, 0d, 0d, false, false);
        }

        var trackingCount = samples.Count(sample => sample.IsTrackingValid);
        var orientationCount = samples.Count(sample => sample.HasValidOrientation);
        var positionCount = samples.Count(sample => sample.HasValidPosition);
        var trackingFraction = trackingCount / (double)samples.Count;
        var orientationFraction = orientationCount / (double)samples.Count;
        var positionFraction = positionCount / (double)samples.Count;
        var axes = new List<Vector3>();
        var totalRotationDegrees = 0d;

        for (var index = 1; index < samples.Count; index++)
        {
            var previous = samples[index - 1];
            var current = samples[index];
            var interval = current.MonotonicTimeSeconds - previous.MonotonicTimeSeconds;
            if (!previous.HasValidOrientation ||
                !current.HasValidOrientation ||
                interval > options.MaximumSampleIntervalSeconds)
            {
                continue;
            }

            var relative = Quaternion.Normalize(
                Quaternion.Conjugate(previous.Pose.Rotation) * current.Pose.Rotation);
            if (relative.W < 0f)
            {
                relative = new Quaternion(-relative.X, -relative.Y, -relative.Z, -relative.W);
            }

            var halfSine = Math.Sqrt(Math.Max(0d, 1d - (relative.W * relative.W)));
            var angleRadians = 2d * Math.Acos(Math.Clamp(relative.W, -1f, 1f));
            var angleDegrees = angleRadians * (180d / Math.PI);
            if (angleDegrees < options.MinimumRotationStepDegrees || halfSine <= 1e-8d)
            {
                continue;
            }

            axes.Add(new Vector3(
                (float)(relative.X / halfSine),
                (float)(relative.Y / halfSine),
                (float)(relative.Z / halfSine)));
            totalRotationDegrees += angleDegrees;
        }

        var axisCoverage = MotionAxisCoverage.Compute(axes);
        var motionAmountProgress = Math.Min(
            totalRotationDegrees / options.MinimumTotalRotationDegrees,
            axes.Count / (double)options.MinimumRotationSegmentCount);
        var axisProgress = Math.Clamp(
            axisCoverage / options.MinimumRotationAxisCoverage,
            0d,
            1d);
        var orientationProgress = Math.Clamp(
            orientationFraction / options.MinimumOrientationValidityFraction,
            0d,
            1d);
        var rotationProgress = Math.Clamp(
            (0.45d * Math.Clamp(motionAmountProgress, 0d, 1d)) +
            (0.35d * axisProgress) +
            (0.20d * orientationProgress),
            0d,
            1d);
        var positionProgress = Math.Clamp(
            positionFraction / options.MinimumPositionValidityFraction,
            0d,
            1d);
        var rotationSufficient =
            axes.Count >= options.MinimumRotationSegmentCount &&
            totalRotationDegrees >= options.MinimumTotalRotationDegrees &&
            axisCoverage >= options.MinimumRotationAxisCoverage &&
            orientationFraction >= options.MinimumOrientationValidityFraction;

        return new MotionCoverageResult(
            samples.Count,
            axes.Count,
            trackingFraction,
            orientationFraction,
            positionFraction,
            totalRotationDegrees,
            axisCoverage,
            rotationProgress,
            positionProgress,
            rotationSufficient,
            positionFraction >= options.MinimumPositionValidityFraction);
    }

    private static void ValidateStrictlyMonotonic(IReadOnlyList<TimestampedPoseSample> samples)
    {
        for (var index = 1; index < samples.Count; index++)
        {
            if (samples[index].MonotonicTimeSeconds <= samples[index - 1].MonotonicTimeSeconds)
            {
                throw new ArgumentException(
                    $"Sample timestamps must increase strictly; sample {index} is not later than sample {index - 1}.",
                    nameof(samples));
            }
        }
    }
}
