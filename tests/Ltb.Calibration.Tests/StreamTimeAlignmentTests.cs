using System.Numerics;
using Ltb.Calibration;
using Ltb.Core;
using Ltb.SyntheticData;

namespace Ltb.Calibration.Tests;

public sealed class StreamTimeAlignmentTests
{
    [Fact]
    public void KnownPositiveControllerTimestampLagIsRecoveredWithinTolerance()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean, seed: 144) with
            {
                KnownLagMilliseconds = 17.0,
                SampleCount = 240,
            });

        var estimate = StreamLagEstimator.EstimateControllerLag(
            dataset.RawTrackerSamples,
            dataset.RawControllerSamples);

        Assert.InRange(
            estimate.LagSeconds,
            dataset.KnownLagSeconds - 0.002,
            dataset.KnownLagSeconds + 0.002);
        Assert.InRange(estimate.CorrelationScore, 0.98, 1.0);
        Assert.InRange(estimate.Confidence, 0.0, 1.0);
        Assert.True(estimate.ComparedSampleCount >= 20);
    }

    [Fact]
    public void AlignmentUsesShortestPathSlerpAndLinearTranslation()
    {
        var tracker = new[]
        {
            Sample(1.0, Quaternion.Identity, Vector3.Zero),
        };
        var halfTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);
        var controller = new[]
        {
            Sample(0.0, Quaternion.Identity, Vector3.Zero),
            Sample(2.0, Negate(halfTurn), new Vector3(2f, 4f, 6f)),
        };

        var pair = Assert.Single(PoseStreamAligner.AlignControllerToTracker(
            tracker,
            controller,
            controllerLagSeconds: 0d,
            new PoseStreamAlignmentOptions { MaximumInterpolationGapSeconds = 3d }));

        Assert.Equal(new Vector3(1f, 2f, 3f), pair.Controller.Pose.TranslationMeters);
        var rotatedX = Vector3.Transform(Vector3.UnitX, pair.Controller.Pose.Rotation);
        Assert.InRange(Math.Abs(rotatedX.X), 0f, 1e-5f);
        Assert.InRange(Math.Abs(rotatedX.Y), 0.99999f, 1.00001f);
        Assert.Equal(1.0, pair.Controller.MonotonicTimeSeconds);
    }

    [Fact]
    public void ReplayAlignsRecordingAndRecoversKnownFullMountTransform()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean, seed: 9128) with
            {
                KnownLagMilliseconds = 13.0,
                SampleCount = 240,
            });
        var recording = new PoseRecording(
        [
            Stream(
                "tracker",
                PoseSourceKind.TrackedPose,
                dataset.RawTrackerSamples),
            Stream(
                "controller",
                PoseSourceKind.InputController,
                dataset.RawControllerSamples),
        ]);

        var replay = RecordingCalibrationReplay.Replay(
            recording,
            new RecordingReplayOptions("tracker", "controller"));

        Assert.InRange(
            replay.Lag.LagSeconds,
            dataset.KnownLagSeconds - 0.002,
            dataset.KnownLagSeconds + 0.002);
        Assert.True(replay.Calibration.Success, replay.Calibration.SelectionReason);
        Assert.Equal(CalibrationModel.FullSixDof, replay.Calibration.SelectedModel);
        Assert.InRange(
            HandEyeTestData.RotationErrorDegrees(
                dataset.GroundTruthMount.Rotation,
                replay.Calibration.TrackerToController.Rotation),
            0d,
            0.1d);
        Assert.InRange(
            Vector3.Distance(
                dataset.GroundTruthMount.TranslationMeters,
                replay.Calibration.TrackerToController.TranslationMeters),
            0f,
            0.001f);
        Assert.All(replay.AlignedPairs, pair => Assert.Equal(0d, pair.TimeDeltaSeconds));
    }

    [Fact]
    public void LagEstimatorRejectsNonMonotonicInput()
    {
        var valid = new[]
        {
            Sample(0.0, Quaternion.Identity, Vector3.Zero),
            Sample(0.1, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.1f), Vector3.Zero),
            Sample(0.2, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.2f), Vector3.Zero),
        };
        var nonMonotonic = new[] { valid[0], valid[2], valid[1] };

        Assert.Throws<ArgumentException>(() =>
            StreamLagEstimator.EstimateControllerLag(valid, nonMonotonic));
    }

    private static PoseStreamRecording Stream(
        string id,
        PoseSourceKind sourceKind,
        IReadOnlyList<TimestampedPoseSample> samples) =>
        new(
            new PoseStreamIdentity(id, sourceKind, $"synthetic-{id}"),
            samples.Select(sample => new RecordedPoseSample(
                sample,
                true,
                PoseTrackingResult.RunningOk)));

    private static TimestampedPoseSample Sample(
        double time,
        Quaternion rotation,
        Vector3 translation) =>
        new(
            time,
            new RigidTransform(rotation, translation),
            PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid);

    private static Quaternion Negate(Quaternion value) =>
        new(-value.X, -value.Y, -value.Z, -value.W);
}
