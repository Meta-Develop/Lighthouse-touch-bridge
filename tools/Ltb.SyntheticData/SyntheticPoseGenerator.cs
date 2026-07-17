using System.Numerics;
using Ltb.Core;

namespace Ltb.SyntheticData;

/// <summary>
/// Generates deterministic tracker/controller streams satisfying
/// T_Q_C(t) = T_Q_L * T_L_T(t) * T_T_C.
/// </summary>
public static class SyntheticPoseGenerator
{
    private static readonly RigidTransform DefaultMount = new(
        Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(
            DegreesToRadians(31.0),
            DegreesToRadians(-18.0),
            DegreesToRadians(23.0))),
        new Vector3(0.034f, -0.052f, 0.021f));

    private static readonly RigidTransform DefaultQuestFromLighthouse = new(
        Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(
            DegreesToRadians(-47.0),
            DegreesToRadians(16.0),
            DegreesToRadians(11.0))),
        new Vector3(1.25f, -0.45f, 0.82f));

    public static SyntheticPoseDataset Generate(SyntheticGenerationOptions? options = null)
    {
        options ??= SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean);
        options.Validate();
        var groundTruthMount = options.GroundTruthMount ?? DefaultMount;
        var questFromLighthouse = options.QuestFromLighthouse ?? DefaultQuestFromLighthouse;

        var random = new Random(options.Seed);
        var rawTrackerSamples = new List<TimestampedPoseSample>(options.SampleCount);
        var rawControllerSamples = new List<TimestampedPoseSample>(options.SampleCount);
        var alignedPairs = new List<SynchronizedPosePair>(options.SampleCount);
        var knownLagSeconds = options.KnownLagMilliseconds / 1_000.0;
        // Leave headroom for zero-mean timestamp jitter while preserving the
        // non-negative monotonic timestamp contract.
        var timestampSeconds = 1.0;
        var droppedSampleCount = 0;
        var outlierPoseCount = 0;
        var quaternionSignFlipCount = 0;
        var trackingInvalidSampleCount = 0;
        var controllerPositionInvalidSampleCount = 0;

        for (var sampleIndex = 0; sampleIndex < options.SampleCount; sampleIndex++)
        {
            if (sampleIndex > 0)
            {
                var rateVariation = options.VariableRateFraction == 0.0
                    ? 0.0
                    : ((2.0 * random.NextDouble()) - 1.0) * options.VariableRateFraction;
                timestampSeconds += (1.0 / options.SampleRateHz) * (1.0 + rateVariation);
            }

            var trackerTruth = EvaluateTrackerPose(options.Scenario, sampleIndex, options.SampleCount);
            var controllerTruth = questFromLighthouse * trackerTruth * groundTruthMount;

            var trackerMeasurement = AddMeasurementError(trackerTruth, options, random);
            var controllerMeasurement = AddMeasurementError(controllerTruth, options, random);
            var controllerSignResult = MaybeFlipQuaternionSign(
                controllerMeasurement.Pose,
                options.QuaternionSignFlipProbability,
                random);
            var trackerPose = trackerMeasurement.Pose;
            var controllerPose = controllerSignResult.Pose;

            var controllerHasPosition = random.NextDouble() <= options.ControllerPositionAvailability;
            var trackerValidity = PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid;
            var controllerValidity = PoseValidity.Orientation | PoseValidity.TrackingValid;
            if (controllerHasPosition)
            {
                controllerValidity |= PoseValidity.Position;
            }

            var trackerTrackingInvalid = random.NextDouble() < options.TrackingInvalidProbability;
            var controllerTrackingInvalid = random.NextDouble() < options.TrackingInvalidProbability;
            if (trackerTrackingInvalid)
            {
                trackerValidity &= ~PoseValidity.TrackingValid;
            }

            if (controllerTrackingInvalid)
            {
                controllerValidity &= ~PoseValidity.TrackingValid;
            }

            if (random.NextDouble() < options.DropProbability)
            {
                droppedSampleCount++;
                continue;
            }

            outlierPoseCount += (trackerMeasurement.IsOutlier ? 1 : 0) +
                (controllerMeasurement.IsOutlier ? 1 : 0);
            quaternionSignFlipCount += controllerSignResult.WasFlipped ? 1 : 0;
            trackingInvalidSampleCount += (trackerTrackingInvalid ? 1 : 0) +
                (controllerTrackingInvalid ? 1 : 0);
            controllerPositionInvalidSampleCount += controllerHasPosition ? 0 : 1;

            var trackerJitterSeconds = NextBoundedTimestampJitterSeconds(options, random);
            var controllerJitterSeconds = NextBoundedTimestampJitterSeconds(options, random);
            var trackerRawTimestamp = timestampSeconds + trackerJitterSeconds;
            var controllerRawTimestamp = timestampSeconds + knownLagSeconds + controllerJitterSeconds;

            rawTrackerSamples.Add(new TimestampedPoseSample(trackerRawTimestamp, trackerPose, trackerValidity));
            rawControllerSamples.Add(new TimestampedPoseSample(controllerRawTimestamp, controllerPose, controllerValidity));

            var alignedTracker = new TimestampedPoseSample(timestampSeconds, trackerPose, trackerValidity);
            var alignedController = new TimestampedPoseSample(timestampSeconds, controllerPose, controllerValidity);
            alignedPairs.Add(new SynchronizedPosePair(alignedTracker, alignedController));
        }

        return new SyntheticPoseDataset(
            options.Scenario,
            options.Seed,
            groundTruthMount,
            questFromLighthouse,
            knownLagSeconds,
            knownLagSeconds,
            options.SampleCount,
            droppedSampleCount,
            new SyntheticInjectionSummary(
                outlierPoseCount,
                quaternionSignFlipCount,
                trackingInvalidSampleCount,
                controllerPositionInvalidSampleCount),
            rawTrackerSamples,
            rawControllerSamples,
            alignedPairs);
    }

    private static RigidTransform EvaluateTrackerPose(
        SyntheticScenario scenario,
        int sampleIndex,
        int sampleCount)
    {
        var phase = sampleCount == 1
            ? 0.0
            : (2.0 * Math.PI * sampleIndex) / (sampleCount - 1);

        return scenario switch
        {
            SyntheticScenario.Static => new RigidTransform(
                Quaternion.CreateFromYawPitchRoll(0.2f, -0.1f, 0.15f),
                new Vector3(0.15f, 1.05f, -0.25f)),
            SyntheticScenario.SingleAxisRotation => new RigidTransform(
                Quaternion.CreateFromAxisAngle(
                    Vector3.Normalize(new Vector3(0.2f, 0.9f, -0.1f)),
                    (float)(1.15 * Math.Sin(phase))),
                new Vector3(0.15f, 1.05f, -0.25f)),
            SyntheticScenario.PureTranslation => new RigidTransform(
                Quaternion.CreateFromYawPitchRoll(0.2f, -0.1f, 0.15f),
                EvaluateExcitingPosition(phase)),
            _ => new RigidTransform(EvaluateExcitingRotation(phase), EvaluateExcitingPosition(phase)),
        };
    }

    private static Quaternion EvaluateExcitingRotation(double phase)
    {
        var yaw = 0.82 * Math.Sin(0.91 * phase) + 0.16 * Math.Sin(2.7 * phase + 0.2);
        var pitch = 0.64 * Math.Sin(1.31 * phase + 0.45) + 0.12 * Math.Cos(2.1 * phase);
        var roll = 0.73 * Math.Sin(1.73 * phase - 0.25) + 0.14 * Math.Cos(2.9 * phase);
        return Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll((float)yaw, (float)pitch, (float)roll));
    }

    private static Vector3 EvaluateExcitingPosition(double phase) => new(
        (float)(0.18 + (0.23 * Math.Sin(0.83 * phase)) + (0.04 * Math.Cos(2.2 * phase))),
        (float)(1.08 + (0.17 * Math.Sin(1.37 * phase + 0.4))),
        (float)(-0.28 + (0.21 * Math.Cos(1.11 * phase - 0.2))));

    private static MeasurementResult AddMeasurementError(
        RigidTransform truth,
        SyntheticGenerationOptions options,
        Random random)
    {
        if (options.RotationNoiseStdDevDegrees == 0.0 &&
            options.PositionNoiseStdDevMeters == 0.0 &&
            options.OutlierProbability == 0.0)
        {
            return new MeasurementResult(truth, IsOutlier: false);
        }

        var noiseAngleRadians = DegreesToRadians(options.RotationNoiseStdDevDegrees) * NextGaussian(random);
        var noiseAxis = RandomUnitVector(random);
        var rotationNoise = Quaternion.CreateFromAxisAngle(noiseAxis, (float)noiseAngleRadians);
        var rotation = Quaternion.Normalize(truth.Rotation * rotationNoise);

        var translation = truth.TranslationMeters + new Vector3(
            (float)(options.PositionNoiseStdDevMeters * NextGaussian(random)),
            (float)(options.PositionNoiseStdDevMeters * NextGaussian(random)),
            (float)(options.PositionNoiseStdDevMeters * NextGaussian(random)));

        var isOutlier = random.NextDouble() < options.OutlierProbability;
        if (isOutlier)
        {
            rotation = Quaternion.Normalize(rotation * Quaternion.CreateFromAxisAngle(
                RandomUnitVector(random),
                (float)DegreesToRadians(8.0 + (8.0 * random.NextDouble()))));
            translation += 0.015f * RandomUnitVector(random);
        }

        return new MeasurementResult(new RigidTransform(rotation, translation), isOutlier);
    }

    private static QuaternionSignResult MaybeFlipQuaternionSign(
        RigidTransform pose,
        double probability,
        Random random)
    {
        if (random.NextDouble() >= probability)
        {
            return new QuaternionSignResult(pose, WasFlipped: false);
        }

        var rotation = pose.Rotation;
        return new QuaternionSignResult(
            new RigidTransform(
                new Quaternion(-rotation.X, -rotation.Y, -rotation.Z, -rotation.W),
                pose.TranslationMeters),
            WasFlipped: true);
    }

    private static Vector3 RandomUnitVector(Random random)
    {
        while (true)
        {
            var candidate = new Vector3(
                (float)((2.0 * random.NextDouble()) - 1.0),
                (float)((2.0 * random.NextDouble()) - 1.0),
                (float)((2.0 * random.NextDouble()) - 1.0));
            var lengthSquared = candidate.LengthSquared();
            if (lengthSquared is > 1e-8f and <= 1.0f)
            {
                return Vector3.Normalize(candidate);
            }
        }
    }

    private static double NextGaussian(Random random)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = 1.0 - random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static double NextBoundedTimestampJitterSeconds(
        SyntheticGenerationOptions options,
        Random random)
    {
        var requestedJitter = NextGaussian(random) *
            (options.TimestampJitterStdDevMilliseconds / 1_000.0);
        var maximumMagnitude = 0.2 / options.SampleRateHz;
        return Math.Clamp(requestedJitter, -maximumMagnitude, maximumMagnitude);
    }

    private static float DegreesToRadians(double degrees) => (float)(degrees * Math.PI / 180.0);

    private readonly record struct MeasurementResult(RigidTransform Pose, bool IsOutlier);

    private readonly record struct QuaternionSignResult(RigidTransform Pose, bool WasFlipped);
}
