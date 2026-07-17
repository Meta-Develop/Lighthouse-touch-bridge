using System.Numerics;
using Ltb.Calibration;
using Ltb.Core;
using Ltb.SyntheticData;

namespace Ltb.Calibration.Tests;

public sealed class MotionCoverageTests
{
    [Fact]
    public void RichMultiAxisMotionReachesRotationAndPositionCoverage()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean, seed: 1717) with
            {
                SampleCount = 300,
            });

        var result = MotionCoverageAnalyzer.Evaluate(dataset.RawControllerSamples);

        Assert.True(result.IsRotationSufficient);
        Assert.True(result.IsPositionSufficient);
        Assert.InRange(result.RotationAxisCoverage, 0.04d, 1d);
        Assert.True(result.TotalRotationDegrees >= 180d);
        Assert.Equal(1d, result.TrackingValidityFraction);
        Assert.Equal(1d, result.OrientationValidityFraction);
        Assert.Equal(1d, result.PositionValidityFraction);
        Assert.Equal(1d, result.RotationProgress);
        Assert.Equal(1d, result.PositionProgress);
    }

    [Fact]
    public void LongSingleAxisMotionDoesNotSatisfyAxisDiversity()
    {
        var samples = Enumerable.Range(0, 300)
            .Select(index => Sample(
                index * 0.01d,
                Quaternion.CreateFromAxisAngle(Vector3.UnitX, index * 0.02f),
                hasPosition: true))
            .ToArray();

        var result = MotionCoverageAnalyzer.Evaluate(samples);

        Assert.True(result.TotalRotationDegrees >= 180d);
        Assert.InRange(result.RotationAxisCoverage, 0d, 1e-10d);
        Assert.False(result.IsRotationSufficient);
        Assert.True(result.RotationProgress < 1d);
        Assert.True(result.IsPositionSufficient);
    }

    [Fact]
    public void PositionValidityIsReportedSeparatelyFromRotationCoverage()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean, seed: 1818) with
            {
                SampleCount = 300,
            });
        var samples = dataset.RawControllerSamples
            .Select((sample, index) => new TimestampedPoseSample(
                sample.MonotonicTimeSeconds,
                sample.Pose,
                index % 4 == 0
                    ? PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid
                    : PoseValidity.Orientation | PoseValidity.TrackingValid))
            .ToArray();

        var result = MotionCoverageAnalyzer.Evaluate(samples);

        Assert.True(result.IsRotationSufficient);
        Assert.False(result.IsPositionSufficient);
        Assert.Equal(0.25d, result.PositionValidityFraction, precision: 6);
        Assert.InRange(result.PositionProgress, 0d, 0.5d);
    }

    [Fact]
    public void TrackingDropoutsReduceAllApplicableValidityMetrics()
    {
        var samples = Enumerable.Range(0, 20)
            .Select(index => new TimestampedPoseSample(
                index * 0.01d,
                new RigidTransform(
                    Quaternion.CreateFromYawPitchRoll(index * 0.02f, index * 0.01f, 0f),
                    Vector3.Zero),
                index < 10
                    ? PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid
                    : PoseValidity.Orientation | PoseValidity.Position))
            .ToArray();

        var result = MotionCoverageAnalyzer.Evaluate(samples);

        Assert.Equal(0.5d, result.TrackingValidityFraction);
        Assert.Equal(0.5d, result.OrientationValidityFraction);
        Assert.Equal(0.5d, result.PositionValidityFraction);
        Assert.False(result.IsPositionSufficient);
        Assert.False(result.IsRotationSufficient);
    }

    private static TimestampedPoseSample Sample(
        double time,
        Quaternion rotation,
        bool hasPosition)
    {
        var validity = PoseValidity.Orientation | PoseValidity.TrackingValid;
        if (hasPosition)
        {
            validity |= PoseValidity.Position;
        }

        return new TimestampedPoseSample(
            time,
            new RigidTransform(rotation, Vector3.Zero),
            validity);
    }
}
