using Ltb.Calibration;
using Ltb.Core;
using Ltb.SyntheticData;

namespace Ltb.Calibration.Tests;

public sealed class PerHandCalibrationPipelineTests
{
    [Fact]
    public void AutoPipelineSelectsFullSixDofAndReturnsQualityReport()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean, seed: 1919) with
            {
                KnownLagMilliseconds = 13d,
                SampleCount = 260,
            });

        var result = PerHandCalibrationPipeline.Run(new HandCalibrationInput(
            CalibrationHand.Left,
            "LHR-TEST0001",
            dataset.RawTrackerSamples,
            dataset.RawControllerSamples));

        Assert.True(result.Success, result.Reason);
        Assert.Equal(HandCalibrationFailure.None, result.Failure);
        Assert.Equal(CalibrationModel.FullSixDof, result.SelectedModel);
        Assert.NotNull(result.Lag);
        Assert.NotEmpty(result.AlignedPairs);
        Assert.NotNull(result.Quality);
        Assert.NotNull(result.QualityReport);
        Assert.Equal(result.Calibration!.SelectionReason, result.SelectionReason);
        Assert.InRange(result.Lag.LagSeconds, 0.011d, 0.015d);
    }

    [Fact]
    public void AutoPipelinePreservesRotationOnlyFallbackReasonFromSolver()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.TranslationDegenerate, seed: 2020) with
            {
                KnownLagMilliseconds = 11d,
                SampleCount = 260,
            });

        var result = PerHandCalibrationPipeline.Run(new HandCalibrationInput(
            CalibrationHand.Right,
            "LHR-TEST0002",
            dataset.RawTrackerSamples,
            dataset.RawControllerSamples));

        Assert.True(result.Success, result.Reason);
        Assert.Equal(CalibrationModel.RotationOnly, result.SelectedModel);
        Assert.False(result.Coverage.IsPositionSufficient);
        Assert.Contains("rotation-only", result.SelectionReason, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.Quality);
    }

    [Fact]
    public void StaticCaptureReturnsExpectedTimeAlignmentFailure()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Static, seed: 2121) with
            {
                SampleCount = 100,
            });

        var result = PerHandCalibrationPipeline.Run(new HandCalibrationInput(
            CalibrationHand.Left,
            "LHR-TEST0001",
            dataset.RawTrackerSamples,
            dataset.RawControllerSamples));

        Assert.False(result.Success);
        Assert.Equal(HandCalibrationFailure.TimeAlignmentRejected, result.Failure);
        Assert.Equal(CalibrationModel.Failed, result.SelectedModel);
        Assert.Null(result.Lag);
        Assert.Null(result.Calibration);
        Assert.Contains("InsufficientMotion", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NonMonotonicControllerTimestampsReturnInvalidCapture()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean, seed: 2222) with
            {
                SampleCount = 100,
            });

        var result = PerHandCalibrationPipeline.Run(new HandCalibrationInput(
            CalibrationHand.Left,
            "LHR-TEST0001",
            dataset.RawTrackerSamples,
            Disordered(dataset.RawControllerSamples)));

        Assert.False(result.Success);
        Assert.Equal(HandCalibrationFailure.InvalidCapture, result.Failure);
        Assert.Equal(CalibrationModel.Failed, result.SelectedModel);
        Assert.Null(result.Lag);
        Assert.Null(result.Calibration);
        Assert.Contains("timestamps", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonMonotonicTrackerTimestampsReturnInvalidCapture()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean, seed: 2323) with
            {
                SampleCount = 100,
            });

        var result = PerHandCalibrationPipeline.Run(new HandCalibrationInput(
            CalibrationHand.Right,
            "LHR-TEST0002",
            Disordered(dataset.RawTrackerSamples),
            dataset.RawControllerSamples));

        Assert.False(result.Success);
        Assert.Equal(HandCalibrationFailure.InvalidCapture, result.Failure);
        Assert.Equal(CalibrationModel.Failed, result.SelectedModel);
        Assert.Null(result.Lag);
        Assert.Null(result.Calibration);
        Assert.Contains("timestamps", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidPipelineOptionsRemainProgrammerErrors()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean, seed: 2424));
        var input = new HandCalibrationInput(
            CalibrationHand.Left,
            "LHR-TEST0001",
            dataset.RawTrackerSamples,
            dataset.RawControllerSamples);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PerHandCalibrationPipeline.Run(
                input,
                new HandCalibrationPipelineOptions
                {
                    Alignment = new PoseStreamAlignmentOptions
                    {
                        MaximumInterpolationGapSeconds = 0d,
                    },
                }));
    }

    private static IReadOnlyList<TimestampedPoseSample> Disordered(
        IReadOnlyList<TimestampedPoseSample> source)
    {
        var result = source.ToArray();
        (result[10], result[11]) = (result[11], result[10]);
        return result;
    }
}
