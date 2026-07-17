using System.Numerics;
using Ltb.Calibration;
using Ltb.Core;
using Ltb.SyntheticData;

namespace Ltb.Calibration.Tests;

public sealed class LagEstimationAcceptanceTests
{
    [Theory]
    [InlineData(-0.017)]
    [InlineData(0.0)]
    [InlineData(0.017)]
    public void ControllerLagSignIsRecoveredForNegativeZeroAndPositiveOffsets(double lagSeconds)
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean, seed: 718) with
            {
                KnownLagMilliseconds = 0d,
                SampleCount = 240,
            });
        var shiftedController = ShiftTimestamps(dataset.RawControllerSamples, lagSeconds);

        var estimate = StreamLagEstimator.EstimateControllerLag(
            dataset.RawTrackerSamples,
            shiftedController);

        Assert.InRange(estimate.LagSeconds, lagSeconds - 0.002, lagSeconds + 0.002);
        Assert.InRange(estimate.CoarseLagSeconds, lagSeconds - 0.002, lagSeconds + 0.002);
        Assert.True(double.IsFinite(estimate.RefinedRotationResidualDegrees));
        Assert.True(estimate.CorrelationIntervalMinimumSeconds <= estimate.CoarseLagSeconds);
        Assert.True(estimate.CorrelationIntervalMaximumSeconds >= estimate.CoarseLagSeconds);
    }

    [Fact]
    public void NonDivisibleSearchStepStillProducesSymmetricGridContainingZero()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean, seed: 811) with
            {
                KnownLagMilliseconds = 12d,
                SampleCount = 240,
            });
        var options = new LagEstimationOptions
        {
            MaximumAbsoluteLagSeconds = 0.0275,
            SearchStepSeconds = 0.006,
            PeakComparisonExclusionSeconds = 0.012,
        };

        var estimate = StreamLagEstimator.EstimateControllerLag(
            dataset.RawTrackerSamples,
            dataset.RawControllerSamples,
            options);

        Assert.Contains(0d, estimate.EvaluatedCandidateLagsSeconds);
        Assert.Equal(11, estimate.EvaluatedCandidateLagsSeconds.Count);
        Assert.Equal(-0.0275, estimate.EvaluatedCandidateLagsSeconds[0], precision: 12);
        Assert.Equal(0.0275, estimate.EvaluatedCandidateLagsSeconds[^1], precision: 12);
        for (var index = 0; index < estimate.EvaluatedCandidateLagsSeconds.Count; index++)
        {
            Assert.Equal(
                -estimate.EvaluatedCandidateLagsSeconds[^(index + 1)],
                estimate.EvaluatedCandidateLagsSeconds[index],
                precision: 12);
        }

        Assert.InRange(estimate.LagSeconds, 0.010, 0.014);
    }

    [Fact]
    public void StaticStreamsAreRejectedAsInsufficientMotion()
    {
        var samples = Enumerable.Range(0, 80)
            .Select(index => Sample(index * 0.01, Quaternion.Identity))
            .ToArray();

        var exception = Assert.Throws<LagEstimationException>(() =>
            StreamLagEstimator.EstimateControllerLag(samples, samples));

        Assert.Equal(LagEstimationFailure.InsufficientMotion, exception.Reason);
    }

    [Fact]
    public void ShortCaptureWithoutEnoughCorrelationSamplesIsRejected()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean, seed: 30) with
            {
                KnownLagMilliseconds = 0d,
                SampleCount = 10,
            });

        var exception = Assert.Throws<LagEstimationException>(() =>
            StreamLagEstimator.EstimateControllerLag(
                dataset.RawTrackerSamples,
                dataset.RawControllerSamples));

        Assert.Equal(LagEstimationFailure.InsufficientOverlap, exception.Reason);
    }

    [Fact]
    public void OversizedSourceIntervalsAreNotConvertedIntoAngularSpeed()
    {
        var samples = new[]
        {
            Sample(0.0, Quaternion.Identity),
            Sample(0.01, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.02f)),
            Sample(1.0, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.2f)),
        };

        var exception = Assert.Throws<LagEstimationException>(() =>
            StreamLagEstimator.EstimateControllerLag(
                samples,
                samples,
                new LagEstimationOptions
                {
                    MaximumSourcePoseIntervalSeconds = 0.05,
                    MinimumCorrelationSampleCount = 3,
                }));

        Assert.Equal(LagEstimationFailure.InsufficientMotion, exception.Reason);
    }

    [Fact]
    public void PeakOutsideConfiguredSearchIsRejectedAtBoundary()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean, seed: 88) with
            {
                KnownLagMilliseconds = 45d,
                SampleCount = 240,
            });

        var exception = Assert.Throws<LagEstimationException>(() =>
            StreamLagEstimator.EstimateControllerLag(
                dataset.RawTrackerSamples,
                dataset.RawControllerSamples,
                new LagEstimationOptions
                {
                    MaximumAbsoluteLagSeconds = 0.020,
                    SearchStepSeconds = 0.002,
                }));

        Assert.Equal(LagEstimationFailure.BoundaryPeak, exception.Reason);
    }

    [Fact]
    public void MissingRunnerUpEvidenceCannotProduceAnEstimate()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean, seed: 29) with
            {
                KnownLagMilliseconds = 0d,
                SampleCount = 240,
            });

        var exception = Assert.Throws<LagEstimationException>(() =>
            StreamLagEstimator.EstimateControllerLag(
                dataset.RawTrackerSamples,
                dataset.RawControllerSamples,
                new LagEstimationOptions
                {
                    MaximumAbsoluteLagSeconds = 0.005,
                    SearchStepSeconds = 0.005,
                    PeakComparisonExclusionSeconds = 0.010,
                    MinimumBoundaryMarginSteps = 0,
                }));

        Assert.Equal(LagEstimationFailure.InsufficientComparisonEvidence, exception.Reason);
    }

    [Fact]
    public void PeriodicEqualPeaksAreRejectedAsAmbiguous()
    {
        const double periodSeconds = 0.20;
        var samples = Enumerable.Range(0, 601)
            .Select(index =>
            {
                var time = index * 0.005;
                var angle = 0.6 * Math.Sin(2d * Math.PI * time / periodSeconds);
                return Sample(time, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)angle));
            })
            .ToArray();

        var exception = Assert.Throws<LagEstimationException>(() =>
            StreamLagEstimator.EstimateControllerLag(
                samples,
                samples,
                new LagEstimationOptions
                {
                    MaximumAbsoluteLagSeconds = 0.16,
                    SearchStepSeconds = 0.005,
                    PeakComparisonExclusionSeconds = 0.02,
                    MinimumPeakProminence = 0.01,
                }));

        Assert.Equal(LagEstimationFailure.AmbiguousPeak, exception.Reason);
    }

    [Fact]
    public void WeakUnrelatedMotionIsRejectedBeforeRotationRefinement()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean, seed: 19) with
            {
                KnownLagMilliseconds = 0d,
                SampleCount = 240,
            });
        var random = new Random(991);
        var unrelated = dataset.RawControllerSamples
            .Select((sample, index) =>
            {
                var axis = Vector3.Normalize(new Vector3(
                    (float)(0.2 + random.NextDouble()),
                    (float)(0.2 + random.NextDouble()),
                    (float)(0.2 + random.NextDouble())));
                var rotation = Quaternion.CreateFromAxisAngle(
                    axis,
                    (float)((2d * random.NextDouble()) - 1d));
                return Sample(sample.MonotonicTimeSeconds, rotation);
            })
            .ToArray();

        var exception = Assert.Throws<LagEstimationException>(() =>
            StreamLagEstimator.EstimateControllerLag(
                dataset.RawTrackerSamples,
                unrelated,
                new LagEstimationOptions { RequireInteriorPeak = false }));

        Assert.Equal(LagEstimationFailure.WeakCorrelation, exception.Reason);
    }

    [Fact]
    public void RotationResidualRefinementImprovesBeyondTheCoarseGrid()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean, seed: 550) with
            {
                KnownLagMilliseconds = 13.4,
                SampleCount = 260,
            });

        var estimate = StreamLagEstimator.EstimateControllerLag(
            dataset.RawTrackerSamples,
            dataset.RawControllerSamples,
            new LagEstimationOptions
            {
                SearchStepSeconds = 0.005,
                RotationRefinementSubdivisions = 10,
            });

        Assert.InRange(estimate.LagSeconds, 0.0114, 0.0154);
        Assert.True(
            Math.Abs(estimate.LagSeconds - dataset.KnownLagSeconds) <
            Math.Abs(estimate.ProvisionalRotationLagSeconds - dataset.KnownLagSeconds));
        Assert.True(estimate.RefinedRotationResidualDegrees <= estimate.CoarseRotationResidualDegrees);
        Assert.DoesNotContain(
            estimate.EvaluatedCandidateLagsSeconds,
            candidate => Math.Abs(candidate - estimate.LagSeconds) < 1e-9);
    }

    [Fact]
    public void NoisyVariableRateReplayIsAccurateAndDeterministic()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Noisy, seed: 62018) with
            {
                KnownLagMilliseconds = 14d,
                SampleCount = 260,
                VariableRateFraction = 0.08,
                TimestampJitterStdDevMilliseconds = 0.25,
                QuaternionSignFlipProbability = 0.25,
            });
        var recording = Recording(dataset.RawTrackerSamples, dataset.RawControllerSamples);

        var first = RecordingCalibrationReplay.Replay(
            recording,
            new RecordingReplayOptions("tracker", "controller"));
        var second = RecordingCalibrationReplay.Replay(
            recording,
            new RecordingReplayOptions("tracker", "controller"));

        Assert.InRange(first.Lag.LagSeconds, dataset.KnownLagSeconds - 0.003, dataset.KnownLagSeconds + 0.003);
        Assert.True(first.Calibration.Success, first.Calibration.SelectionReason);
        Assert.Equal(CalibrationModel.FullSixDof, first.Calibration.SelectedModel);
        Assert.Equal(first.Lag.LagSeconds, second.Lag.LagSeconds);
        Assert.Equal(first.Lag.RefinedRotationResidualDegrees, second.Lag.RefinedRotationResidualDegrees);
        Assert.Equal(first.Calibration, second.Calibration);
        Assert.Equal(first.AlignedPairs, second.AlignedPairs);
    }

    [Fact]
    public void DisconnectedSamplesDoNotRemainTrackingValidDuringReplay()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean, seed: 914) with
            {
                KnownLagMilliseconds = 12d,
                SampleCount = 240,
            });
        var recording = Recording(
            dataset.RawTrackerSamples,
            dataset.RawControllerSamples,
            controllerConnected: index => index % 23 != 0);

        var replay = RecordingCalibrationReplay.Replay(
            recording,
            new RecordingReplayOptions("tracker", "controller"));

        Assert.True(replay.Calibration.Success, replay.Calibration.SelectionReason);
        Assert.True(replay.Calibration.RotationValidSampleCount < replay.AlignedPairs.Count);
    }

    private static PoseRecording Recording(
        IReadOnlyList<TimestampedPoseSample> tracker,
        IReadOnlyList<TimestampedPoseSample> controller,
        Func<int, bool>? controllerConnected = null) =>
        new(
        [
            new PoseStreamRecording(
                new PoseStreamIdentity("tracker", PoseSourceKind.TrackedPose, "synthetic-tracker"),
                tracker.Select(sample => new RecordedPoseSample(
                    sample,
                    true,
                    PoseTrackingResult.RunningOk))),
            new PoseStreamRecording(
                new PoseStreamIdentity("controller", PoseSourceKind.InputController, "synthetic-controller"),
                controller.Select((sample, index) => new RecordedPoseSample(
                    sample,
                    controllerConnected?.Invoke(index) ?? true,
                    PoseTrackingResult.RunningOk))),
        ]);

    private static IReadOnlyList<TimestampedPoseSample> ShiftTimestamps(
        IReadOnlyList<TimestampedPoseSample> samples,
        double offsetSeconds) =>
        samples.Select(sample => new TimestampedPoseSample(
                sample.MonotonicTimeSeconds + offsetSeconds,
                sample.Pose,
                sample.Validity))
            .ToArray();

    private static TimestampedPoseSample Sample(double time, Quaternion rotation) =>
        new(
            time,
            new RigidTransform(rotation, Vector3.Zero),
            PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid);
}
