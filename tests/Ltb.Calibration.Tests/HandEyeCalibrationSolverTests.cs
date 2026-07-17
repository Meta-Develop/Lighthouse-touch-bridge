using System.Numerics;
using Ltb.Core;

namespace Ltb.Calibration.Tests;

public sealed class HandEyeCalibrationSolverTests
{
    [Fact]
    public void CleanMultiAxisMotionRecoversFullTransform()
    {
        var truth = HandEyeTestData.MountTransform;
        var pairs = HandEyeTestData.CreatePairs(72, HandEyeTestMotion.MultiAxis);

        var result = HandEyeCalibrationSolver.Solve(pairs, CalibrationPolicy.Auto);

        Assert.True(result.Success, result.SelectionReason);
        Assert.Equal(CalibrationModel.FullSixDof, result.SelectedModel);
        Assert.True(result.Motion.RotationObservable);
        Assert.True(result.Motion.TranslationObservable);
        Assert.InRange(HandEyeTestData.RotationErrorDegrees(
            truth.Rotation,
            result.TrackerToController.Rotation), 0d, 0.01d);
        Assert.InRange(Vector3.Distance(
            truth.TranslationMeters,
            result.TrackerToController.TranslationMeters), 0f, 0.0001f);
        Assert.InRange(result.Quality.RotationRmsDegrees, 0d, 0.01d);
        Assert.InRange(result.Quality.PositionRmsMillimeters!.Value, 0d, 0.02d);
    }

    [Fact]
    public void SeededNoiseAndSignFlipsRemainWithinMilestoneTolerance()
    {
        var truth = HandEyeTestData.MountTransform;
        var pairs = HandEyeTestData.CreatePairs(
            96,
            HandEyeTestMotion.MultiAxis,
            seed: 8172,
            rotationNoiseDegrees: 0.08d,
            positionNoiseMeters: 0.0004d,
            flipQuaternionSigns: true);

        var result = HandEyeCalibrationSolver.Solve(pairs, CalibrationPolicy.Auto);

        Assert.True(result.Success, result.SelectionReason);
        Assert.Equal(CalibrationModel.FullSixDof, result.SelectedModel);
        Assert.True(result.ValidationMotionPairCount > 0);
        Assert.InRange(result.Quality.RotationInlierRatio, 0.7d, 1d);
        Assert.InRange(result.Quality.TranslationInlierRatio, 0.7d, 1d);
        Assert.InRange(HandEyeTestData.RotationErrorDegrees(
            truth.Rotation,
            result.TrackerToController.Rotation), 0d, 0.5d);
        Assert.InRange(Vector3.Distance(
            truth.TranslationMeters,
            result.TrackerToController.TranslationMeters), 0f, 0.005f);
    }

    [Fact]
    public void RotationOnlyPolicyReturnsCanonicalRotationAndZeroTranslation()
    {
        var pairs = HandEyeTestData.CreatePairs(64, HandEyeTestMotion.MultiAxis);

        var result = HandEyeCalibrationSolver.Solve(pairs, CalibrationPolicy.RotationOnly);

        Assert.Equal(CalibrationModel.RotationOnly, result.SelectedModel);
        Assert.Equal(Vector3.Zero, result.TrackerToController.TranslationMeters);
        Assert.True(result.TrackerToController.Rotation.W >= 0f);
        Assert.Equal(CalibrationDegeneracy.NotRequested, result.Motion.TranslationDegeneracy);
    }

    [Fact]
    public void GrossPoseOutlierIsTrimmedWithoutChangingSelectedModel()
    {
        var truth = HandEyeTestData.MountTransform;
        var pairs = HandEyeTestData.CreatePairs(
            96,
            HandEyeTestMotion.MultiAxis,
            seed: 23,
            grossControllerOutlierIndex: 47);

        var result = HandEyeCalibrationSolver.Solve(pairs, CalibrationPolicy.Auto);

        Assert.True(result.Success, result.SelectionReason);
        Assert.Equal(CalibrationModel.FullSixDof, result.SelectedModel);
        Assert.InRange(HandEyeTestData.RotationErrorDegrees(
            truth.Rotation,
            result.TrackerToController.Rotation), 0d, 0.5d);
        Assert.InRange(Vector3.Distance(
            truth.TranslationMeters,
            result.TrackerToController.TranslationMeters), 0f, 0.006f);
    }

    [Fact]
    public void TrackingInvalidSamplesAreExcludedFromBothStages()
    {
        var invalidIndices = new HashSet<int> { 9, 21, 44, 57 };
        var pairs = HandEyeTestData.CreatePairs(
            72,
            HandEyeTestMotion.MultiAxis,
            invalidIndices: invalidIndices);

        var result = HandEyeCalibrationSolver.Solve(pairs, CalibrationPolicy.Auto);

        Assert.True(result.Success, result.SelectionReason);
        Assert.Equal(72 - invalidIndices.Count, result.RotationValidSampleCount);
        Assert.Equal(72 - invalidIndices.Count, result.PositionValidSampleCount);
        Assert.Equal(CalibrationModel.FullSixDof, result.SelectedModel);
    }

    [Fact]
    public void ConsoleReportIncludesSelectionResidualsAndTransformUnits()
    {
        var result = HandEyeCalibrationSolver.Solve(
            HandEyeTestData.CreatePairs(64, HandEyeTestMotion.MultiAxis),
            CalibrationPolicy.Auto);

        var report = new CalibrationReport(result).ToString();

        Assert.Contains("Selected model: FullSixDof", report);
        Assert.Contains("Rotation RMS / p95:", report);
        Assert.Contains("Full-model position RMS / p95:", report);
        Assert.Contains("X_mount translation m:", report);
        Assert.Contains("X_mount rotation XYZW:", report);
    }
}

public enum HandEyeTestMotion
{
    MultiAxis,
    SingleAxis,
    PureTranslation,
    Static,
}

internal static class HandEyeTestData
{
    private const double RadiansPerDegree = Math.PI / 180d;

    public static RigidTransform MountTransform { get; } = new(
        Quaternion.CreateFromYawPitchRoll(0.42f, -0.31f, 0.19f),
        new Vector3(0.037f, -0.052f, 0.024f));

    private static RigidTransform QuestFromLighthouse { get; } = new(
        Quaternion.CreateFromYawPitchRoll(-0.73f, 0.28f, 0.51f),
        new Vector3(1.1f, -0.45f, 0.72f));

    public static IReadOnlyList<SynchronizedPosePair> CreatePairs(
        int count,
        HandEyeTestMotion motion,
        int seed = 1234,
        double rotationNoiseDegrees = 0d,
        double positionNoiseMeters = 0d,
        bool includePosition = true,
        bool flipQuaternionSigns = false,
        int? grossControllerOutlierIndex = null,
        IReadOnlySet<int>? invalidIndices = null,
        double controllerTimestampOffsetSeconds = 0d)
    {
        var random = new Random(seed);
        var pairs = new List<SynchronizedPosePair>(count);
        for (var index = 0; index < count; index++)
        {
            var phase = index / (double)(count - 1);
            var tracker = TrackerPose(phase, motion);
            var controller = QuestFromLighthouse * tracker * MountTransform;
            tracker = AddNoise(tracker, random, rotationNoiseDegrees, positionNoiseMeters);
            controller = AddNoise(controller, random, rotationNoiseDegrees, positionNoiseMeters);

            if (grossControllerOutlierIndex == index)
            {
                controller = new RigidTransform(
                    Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1f, 2f, -1f)), 0.7f) * controller.Rotation,
                    controller.TranslationMeters + new Vector3(0.14f, -0.09f, 0.11f));
            }

            if (flipQuaternionSigns && index % 3 == 1)
            {
                tracker = NegateQuaternionSign(tracker);
            }

            if (flipQuaternionSigns && index % 4 == 2)
            {
                controller = NegateQuaternionSign(controller);
            }

            var validity = PoseValidity.Orientation | PoseValidity.TrackingValid;
            if (includePosition)
            {
                validity |= PoseValidity.Position;
            }

            if (invalidIndices?.Contains(index) is true)
            {
                validity = PoseValidity.Orientation | PoseValidity.Position;
            }

            var timestamp = index * 0.01d;
            pairs.Add(new SynchronizedPosePair(
                new TimestampedPoseSample(timestamp, tracker, validity),
                new TimestampedPoseSample(timestamp + controllerTimestampOffsetSeconds, controller, validity)));
        }

        return pairs;
    }

    public static double RotationErrorDegrees(Quaternion expected, Quaternion actual)
    {
        var dot = Math.Abs(Quaternion.Dot(Quaternion.Normalize(expected), Quaternion.Normalize(actual)));
        return 2d * Math.Acos(Math.Clamp(dot, 0d, 1d)) / RadiansPerDegree;
    }

    private static RigidTransform TrackerPose(double phase, HandEyeTestMotion motion)
    {
        var translation = motion is HandEyeTestMotion.Static
            ? new Vector3(0.2f, -0.1f, 0.4f)
            : new Vector3(
                (float)(0.28d * Math.Sin(phase * 2d * Math.PI)),
                (float)(0.18d * Math.Cos(phase * 3d * Math.PI)),
                (float)(0.12d * Math.Sin((phase * 5d * Math.PI) + 0.4d)));

        var rotation = motion switch
        {
            HandEyeTestMotion.MultiAxis => Quaternion.CreateFromYawPitchRoll(
                (float)((0.9d * Math.Sin(phase * 2.1d * Math.PI)) + (0.2d * phase)),
                (float)(0.65d * Math.Sin((phase * 3.3d * Math.PI) + 0.3d)),
                (float)(0.55d * Math.Cos((phase * 4.2d * Math.PI) - 0.2d))),
            HandEyeTestMotion.SingleAxis => Quaternion.CreateFromAxisAngle(
                Vector3.UnitY,
                (float)((phase - 0.5d) * 2.2d)),
            HandEyeTestMotion.PureTranslation or HandEyeTestMotion.Static => Quaternion.Identity,
            _ => throw new ArgumentOutOfRangeException(nameof(motion)),
        };

        return new RigidTransform(rotation, translation);
    }

    private static RigidTransform AddNoise(
        RigidTransform pose,
        Random random,
        double rotationNoiseDegrees,
        double positionNoiseMeters)
    {
        var noiseVector = new Vector3(
            (float)NextGaussian(random),
            (float)NextGaussian(random),
            (float)NextGaussian(random));
        var axis = noiseVector.LengthSquared() > 1e-12f
            ? Vector3.Normalize(noiseVector)
            : Vector3.UnitX;
        var angle = (float)(NextGaussian(random) * rotationNoiseDegrees * RadiansPerDegree);
        var noisyRotation = Quaternion.CreateFromAxisAngle(axis, angle) * pose.Rotation;
        var noisyTranslation = pose.TranslationMeters + new Vector3(
            (float)(NextGaussian(random) * positionNoiseMeters),
            (float)(NextGaussian(random) * positionNoiseMeters),
            (float)(NextGaussian(random) * positionNoiseMeters));
        return new RigidTransform(noisyRotation, noisyTranslation);
    }

    private static RigidTransform NegateQuaternionSign(RigidTransform pose) =>
        new(
            new Quaternion(-pose.Rotation.X, -pose.Rotation.Y, -pose.Rotation.Z, -pose.Rotation.W),
            pose.TranslationMeters);

    private static double NextGaussian(Random random)
    {
        var first = Math.Max(double.Epsilon, random.NextDouble());
        var second = random.NextDouble();
        return Math.Sqrt(-2d * Math.Log(first)) * Math.Cos(2d * Math.PI * second);
    }
}
