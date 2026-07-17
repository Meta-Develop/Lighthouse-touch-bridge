using Ltb.Core;

namespace Ltb.SyntheticData;

/// <summary>
/// Controls deterministic paired-pose generation. Times are expressed in seconds,
/// angles in degrees, and distances in meters unless a property says otherwise.
/// </summary>
public sealed record SyntheticGenerationOptions
{
    public SyntheticScenario Scenario { get; init; } = SyntheticScenario.Clean;

    public int Seed { get; init; } = 20260717;

    public int SampleCount { get; init; } = 180;

    public double SampleRateHz { get; init; } = 90.0;

    /// <summary>
    /// Known controller-stream timestamp lag. The generator records this lag but
    /// emits calibration pairs aligned by their common truth time for Milestone 0.
    /// </summary>
    public double KnownLagMilliseconds { get; init; } = 12.0;

    public double RotationNoiseStdDevDegrees { get; init; }

    public double PositionNoiseStdDevMeters { get; init; }

    public double TimestampJitterStdDevMilliseconds { get; init; }

    public double DropProbability { get; init; }

    public double OutlierProbability { get; init; }

    public double QuaternionSignFlipProbability { get; init; }

    /// <summary>
    /// Probability that either source reports a tracking discontinuity by
    /// clearing TrackingValid while retaining a finite pose payload.
    /// </summary>
    public double TrackingInvalidProbability { get; init; }

    /// <summary>
    /// Fraction of controller samples whose position is marked valid.
    /// Orientation remains available independently.
    /// </summary>
    public double ControllerPositionAvailability { get; init; } = 1.0;

    /// <summary>
    /// Bounded deterministic variation in adjacent sampling intervals. Zero gives
    /// a fixed-rate stream; 0.1 varies intervals by up to ten percent. The maximum
    /// accepted value is 0.5 so bounded jitter cannot reverse stream order.
    /// </summary>
    public double VariableRateFraction { get; init; }

    /// <summary>Optional known X_mount = T_T_C override.</summary>
    public RigidTransform? GroundTruthMount { get; init; }

    /// <summary>Optional arbitrary Y = T_Q_L world transform override.</summary>
    public RigidTransform? QuestFromLighthouse { get; init; }

    public static SyntheticGenerationOptions ForScenario(
        SyntheticScenario scenario,
        int seed = 20260717) => scenario switch
        {
            SyntheticScenario.Noisy => new()
            {
                Scenario = scenario,
                Seed = seed,
                RotationNoiseStdDevDegrees = 0.12,
                PositionNoiseStdDevMeters = 0.0008,
                TimestampJitterStdDevMilliseconds = 0.2,
                DropProbability = 0.015,
                QuaternionSignFlipProbability = 0.15,
                VariableRateFraction = 0.04,
            },
            SyntheticScenario.TranslationDegenerate => new()
            {
                Scenario = scenario,
                Seed = seed,
                ControllerPositionAvailability = 0.2,
                QuaternionSignFlipProbability = 0.1,
            },
            _ => new()
            {
                Scenario = scenario,
                Seed = seed,
            },
        };

    internal void Validate()
    {
        if (SampleCount < 8)
        {
            throw new ArgumentOutOfRangeException(nameof(SampleCount), "At least eight samples are required.");
        }

        if (!double.IsFinite(SampleRateHz) || SampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(SampleRateHz), "Sample rate must be finite and positive.");
        }

        RequireNonNegativeFinite(KnownLagMilliseconds, nameof(KnownLagMilliseconds));
        RequireNonNegativeFinite(RotationNoiseStdDevDegrees, nameof(RotationNoiseStdDevDegrees));
        RequireNonNegativeFinite(PositionNoiseStdDevMeters, nameof(PositionNoiseStdDevMeters));
        RequireNonNegativeFinite(TimestampJitterStdDevMilliseconds, nameof(TimestampJitterStdDevMilliseconds));
        RequireProbability(DropProbability, nameof(DropProbability));
        RequireProbability(OutlierProbability, nameof(OutlierProbability));
        RequireProbability(QuaternionSignFlipProbability, nameof(QuaternionSignFlipProbability));
        RequireProbability(TrackingInvalidProbability, nameof(TrackingInvalidProbability));
        RequireProbability(ControllerPositionAvailability, nameof(ControllerPositionAvailability));
        if (!double.IsFinite(VariableRateFraction) || VariableRateFraction < 0.0 ||
            VariableRateFraction > 0.5)
        {
            throw new ArgumentOutOfRangeException(
                nameof(VariableRateFraction),
                "Variable-rate fraction must be in the inclusive range [0, 0.5].");
        }
        if (GroundTruthMount is { IsValid: false })
        {
            throw new ArgumentException("Ground-truth mount must be a valid rigid transform.", nameof(GroundTruthMount));
        }

        if (QuestFromLighthouse is { IsValid: false })
        {
            throw new ArgumentException("World transform must be a valid rigid transform.", nameof(QuestFromLighthouse));
        }
    }

    private static void RequireNonNegativeFinite(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0.0)
        {
            throw new ArgumentOutOfRangeException(name, "Value must be finite and non-negative.");
        }
    }

    private static void RequireProbability(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0.0 || value > 1.0)
        {
            throw new ArgumentOutOfRangeException(name, "Value must be in the inclusive range [0, 1].");
        }
    }
}
