using Ltb.Core;
using Ltb.SyntheticData;

namespace Ltb.Calibration.Tests;

public sealed class TimestampAlignmentTests
{
    [Fact]
    public void AlignedPairsUseSharedTruthTimestamp()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Noisy, seed: 9173));

        Assert.NotEmpty(dataset.AlignedPairs);
        Assert.All(dataset.AlignedPairs, pair =>
        {
            Assert.Equal(pair.Tracker.MonotonicTimeSeconds, pair.Controller.MonotonicTimeSeconds);
            Assert.Equal(0.0, pair.TimeDeltaSeconds);
        });
        Assert.Contains("known-lag alignment", SyntheticPoseDataset.AlignmentContract);
    }

    [Fact]
    public void RawCleanStreamsPreserveKnownControllerLag()
    {
        var options = SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean, seed: 22) with
        {
            KnownLagMilliseconds = 17.25,
        };

        var dataset = SyntheticPoseGenerator.Generate(options);

        Assert.Equal(dataset.RequestedLagSeconds, dataset.KnownLagSeconds);
        Assert.Equal(dataset.RawTrackerSamples.Count, dataset.RawControllerSamples.Count);
        Assert.All(
            dataset.RawTrackerSamples.Zip(dataset.RawControllerSamples),
            samples => Assert.Equal(dataset.KnownLagSeconds,
                samples.Second.MonotonicTimeSeconds - samples.First.MonotonicTimeSeconds,
                precision: 12));
    }

    [Fact]
    public void JitteredRawTimestampsRemainNonNegativeAndMonotonic()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Noisy, seed: 2026));

        Assert.All(dataset.RawTrackerSamples, sample => Assert.True(sample.MonotonicTimeSeconds >= 0.0));
        Assert.All(dataset.RawControllerSamples, sample => Assert.True(sample.MonotonicTimeSeconds >= 0.0));
        AssertStrictlyIncreasing(dataset.RawTrackerSamples);
        AssertStrictlyIncreasing(dataset.RawControllerSamples);
    }

    [Fact]
    public void ExtremeRequestedJitterIsBoundedToPreserveStreamOrder()
    {
        var options = SyntheticGenerationOptions.ForScenario(SyntheticScenario.Noisy, seed: 8802) with
        {
            TimestampJitterStdDevMilliseconds = 1_000.0,
            VariableRateFraction = 0.5,
        };

        var dataset = SyntheticPoseGenerator.Generate(options);

        AssertStrictlyIncreasing(dataset.RawTrackerSamples);
        AssertStrictlyIncreasing(dataset.RawControllerSamples);
        Assert.Equal(options.KnownLagMilliseconds / 1_000.0, dataset.KnownLagSeconds, precision: 12);
    }

    [Fact]
    public void QuaternionSignInjectionIsObservableButDoesNotChangePhysicalPose()
    {
        var baseline = SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean, seed: 44);
        var noFlips = SyntheticPoseGenerator.Generate(baseline with
        {
            QuaternionSignFlipProbability = 0.0,
        });
        var allFlips = SyntheticPoseGenerator.Generate(baseline with
        {
            QuaternionSignFlipProbability = 1.0,
        });

        Assert.Equal(noFlips.AlignedPairs.Count, allFlips.AlignedPairs.Count);
        for (var index = 0; index < noFlips.AlignedPairs.Count; index++)
        {
            var unflipped = noFlips.AlignedPairs[index].Controller.Pose;
            var flipped = allFlips.AlignedPairs[index].Controller.Pose;
            Assert.NotEqual(unflipped.Rotation, flipped.Rotation);
            Assert.Equal(unflipped.TranslationMeters, flipped.TranslationMeters);
            var absoluteDot = Math.Clamp(Math.Abs(System.Numerics.Quaternion.Dot(
                unflipped.Rotation,
                flipped.Rotation)), 0.0f, 1.0f);
            Assert.InRange(absoluteDot, 0.999999f, 1.0f);
        }
    }

    private static void AssertStrictlyIncreasing(IReadOnlyList<TimestampedPoseSample> samples)
    {
        for (var index = 1; index < samples.Count; index++)
        {
            Assert.True(
                samples[index].MonotonicTimeSeconds > samples[index - 1].MonotonicTimeSeconds,
                $"Timestamp at index {index} was not strictly increasing.");
        }
    }
}
