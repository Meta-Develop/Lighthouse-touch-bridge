using System.Numerics;
using Ltb.Calibration;
using Ltb.Core;
using Ltb.SyntheticData;

namespace Ltb.Calibration.Tests;

public sealed class SyntheticCalibrationTests
{
    [Fact]
    public void CleanSyntheticDataRecoversFullMountTransform()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean, seed: 8128));

        var result = HandEyeCalibrationSolver.Solve(dataset.AlignedPairs, CalibrationPolicy.Auto);

        Assert.True(result.Success, result.SelectionReason);
        Assert.Equal(CalibrationModel.FullSixDof, result.SelectedModel);
        AssertTransformNear(dataset.GroundTruthMount, result.TrackerToController, 0.05, 0.0002);
        Assert.True(result.Motion.RotationObservable);
        Assert.True(result.Motion.TranslationObservable);

        var report = CalibrationConsoleReport.Create(dataset, result).Text;
        AssertFiniteInvariantMetric(report, "rotation_inlier_ratio");
        AssertFiniteInvariantMetric(report, "translation_inlier_ratio");
        AssertFiniteInvariantMetric(report, "translation_split_disagreement_mm");
    }

    [Fact]
    public void SeededNoiseRecoversTransformWithinMilestoneTolerance()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Noisy, seed: 451));

        var result = HandEyeCalibrationSolver.Solve(dataset.AlignedPairs, CalibrationPolicy.Auto);

        Assert.True(result.Success, result.SelectionReason);
        Assert.Equal(CalibrationModel.FullSixDof, result.SelectedModel);
        AssertTransformNear(dataset.GroundTruthMount, result.TrackerToController, 1.0, 0.010);
        Assert.InRange(result.Quality.RotationRmsDegrees, 0.0, 2.0);
    }

    [Theory]
    [InlineData(SyntheticScenario.Static)]
    [InlineData(SyntheticScenario.SingleAxisRotation)]
    [InlineData(SyntheticScenario.PureTranslation)]
    public void RotationDegenerateMotionFailsInsteadOfProducingFalseCalibration(
        SyntheticScenario scenario)
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(scenario, seed: 701));

        var result = HandEyeCalibrationSolver.Solve(dataset.AlignedPairs, CalibrationPolicy.Auto);

        Assert.False(result.Success);
        Assert.Equal(CalibrationModel.Failed, result.SelectedModel);
        Assert.False(result.Motion.RotationObservable);
        Assert.NotEmpty(result.SelectionReason);
    }

    [Fact]
    public void AutoFallsBackToRotationOnlyWhenPositionCoverageIsInsufficient()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.TranslationDegenerate, seed: 933));

        var result = HandEyeCalibrationSolver.Solve(dataset.AlignedPairs, CalibrationPolicy.Auto);

        Assert.True(result.Success, result.SelectionReason);
        Assert.Equal(CalibrationModel.RotationOnly, result.SelectedModel);
        Assert.True(result.Motion.RotationObservable);
        Assert.False(result.Motion.TranslationObservable);
        Assert.Equal(Vector3.Zero, result.TrackerToController.TranslationMeters);
        Assert.NotEmpty(result.SelectionReason);
    }

    [Fact]
    public void RotationOnlyPolicyNeverUsesGroundTruthTranslation()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean, seed: 191));

        var result = HandEyeCalibrationSolver.Solve(dataset.AlignedPairs, CalibrationPolicy.RotationOnly);

        Assert.True(result.Success, result.SelectionReason);
        Assert.Equal(CalibrationModel.RotationOnly, result.SelectedModel);
        Assert.Equal(Vector3.Zero, result.TrackerToController.TranslationMeters);
        AssertTransformNear(
            dataset.GroundTruthMount,
            new RigidTransform(result.TrackerToController.Rotation, dataset.GroundTruthMount.TranslationMeters),
            0.05,
            0.000001);

        var report = CalibrationConsoleReport.Create(dataset, result).Text;
        AssertFiniteInvariantMetric(report, "rotation_inlier_ratio");
        Assert.Contains("translation_inlier_ratio: n/a", report);
        Assert.Contains("translation_split_disagreement_mm: n/a", report);
    }

    [Fact]
    public void ModelSelectionIsDeterministicForSameSeed()
    {
        var options = SyntheticGenerationOptions.ForScenario(SyntheticScenario.Noisy, seed: 6021);
        var firstData = SyntheticPoseGenerator.Generate(options);
        var secondData = SyntheticPoseGenerator.Generate(options);

        var first = HandEyeCalibrationSolver.Solve(firstData.AlignedPairs, CalibrationPolicy.Auto);
        var second = HandEyeCalibrationSolver.Solve(secondData.AlignedPairs, CalibrationPolicy.Auto);

        Assert.Equal(first, second);
    }

    [Fact]
    public void CleanGeneratorSatisfiesCoordinateEquationExactly()
    {
        var dataset = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean, seed: 73));

        Assert.Equal(dataset.RequestedSampleCount, dataset.AlignedPairs.Count);
        Assert.Equal(0, dataset.DroppedSampleCount);
        foreach (var pair in dataset.AlignedPairs)
        {
            var expectedController =
                dataset.QuestFromLighthouse * pair.Tracker.Pose * dataset.GroundTruthMount;
            AssertTransformNear(expectedController, pair.Controller.Pose, 1e-5, 1e-6);
        }
    }

    [Fact]
    public void GeneratorAcceptsArbitraryKnownMountAndWorldTransforms()
    {
        var mount = new RigidTransform(
            Quaternion.CreateFromYawPitchRoll(-0.41f, 0.27f, 0.13f),
            new Vector3(-0.028f, 0.044f, 0.019f));
        var world = new RigidTransform(
            Quaternion.CreateFromYawPitchRoll(0.77f, -0.22f, 0.31f),
            new Vector3(-2.1f, 0.7f, 1.4f));
        var options = SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean, seed: 51) with
        {
            GroundTruthMount = mount,
            QuestFromLighthouse = world,
        };

        var dataset = SyntheticPoseGenerator.Generate(options);

        Assert.Equal(mount, dataset.GroundTruthMount);
        Assert.Equal(world, dataset.QuestFromLighthouse);
        Assert.All(dataset.AlignedPairs, pair =>
            AssertTransformNear(
                world * pair.Tracker.Pose * mount,
                pair.Controller.Pose,
                1e-5,
                1e-6));

        var result = HandEyeCalibrationSolver.Solve(dataset.AlignedPairs, CalibrationPolicy.Auto);
        Assert.True(result.Success, result.SelectionReason);
        Assert.Equal(CalibrationModel.FullSixDof, result.SelectedModel);
        AssertTransformNear(mount, result.TrackerToController, 0.05, 0.0002);
    }

    [Fact]
    public void SameSeedAndOptionsProduceIdenticalStreams()
    {
        var options = SyntheticGenerationOptions.ForScenario(SyntheticScenario.Noisy, seed: 991);

        var first = SyntheticPoseGenerator.Generate(options);
        var second = SyntheticPoseGenerator.Generate(options);

        Assert.Equal(first.Scenario, second.Scenario);
        Assert.Equal(first.Seed, second.Seed);
        Assert.Equal(first.GroundTruthMount, second.GroundTruthMount);
        Assert.Equal(first.QuestFromLighthouse, second.QuestFromLighthouse);
        Assert.Equal(first.RequestedLagSeconds, second.RequestedLagSeconds);
        Assert.Equal(first.KnownLagSeconds, second.KnownLagSeconds);
        Assert.Equal(first.DroppedSampleCount, second.DroppedSampleCount);
        Assert.Equal(first.RawTrackerSamples, second.RawTrackerSamples);
        Assert.Equal(first.RawControllerSamples, second.RawControllerSamples);
        Assert.Equal(first.AlignedPairs, second.AlignedPairs);
    }

    [Fact]
    public void DegeneratePresetsHaveTheirDeclaredMotionStructure()
    {
        var staticData = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Static, seed: 1));
        var singleAxisData = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.SingleAxisRotation, seed: 1));
        var pureTranslationData = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.PureTranslation, seed: 1));
        var translationDegenerateData = SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.TranslationDegenerate, seed: 1));

        Assert.All(staticData.AlignedPairs, pair =>
            Assert.Equal(staticData.AlignedPairs[0].Tracker.Pose, pair.Tracker.Pose));
        Assert.All(singleAxisData.AlignedPairs, pair =>
            Assert.Equal(singleAxisData.AlignedPairs[0].Tracker.Pose.TranslationMeters,
                pair.Tracker.Pose.TranslationMeters));
        Assert.All(pureTranslationData.AlignedPairs, pair =>
            Assert.Equal(pureTranslationData.AlignedPairs[0].Tracker.Pose.Rotation,
                pair.Tracker.Pose.Rotation));
        Assert.True(
            translationDegenerateData.AlignedPairs.Count(pair => pair.Controller.HasValidPosition) <
            translationDegenerateData.AlignedPairs.Count / 2);
        Assert.All(translationDegenerateData.AlignedPairs, pair =>
            Assert.True(pair.Controller.HasValidOrientation));
    }

    [Fact]
    public void StressArtifactsAreSeededExercisedAndRepeatable()
    {
        var options = SyntheticGenerationOptions.ForScenario(SyntheticScenario.Noisy, seed: 44021) with
        {
            SampleCount = 240,
            DropProbability = 0.08,
            OutlierProbability = 0.05,
            QuaternionSignFlipProbability = 0.45,
            TrackingInvalidProbability = 0.08,
            ControllerPositionAvailability = 0.65,
            TimestampJitterStdDevMilliseconds = 0.35,
            VariableRateFraction = 0.08,
        };

        var first = SyntheticPoseGenerator.Generate(options);
        var second = SyntheticPoseGenerator.Generate(options);

        Assert.Equal(first.DroppedSampleCount, second.DroppedSampleCount);
        Assert.Equal(first.Injections, second.Injections);
        Assert.Equal(first.RawTrackerSamples, second.RawTrackerSamples);
        Assert.Equal(first.RawControllerSamples, second.RawControllerSamples);
        Assert.Equal(first.AlignedPairs, second.AlignedPairs);
        Assert.True(first.DroppedSampleCount > 0);
        Assert.True(first.Injections.OutlierPoseCount > 0);
        Assert.True(first.Injections.QuaternionSignFlipCount > 0);
        Assert.True(first.Injections.TrackingInvalidSampleCount > 0);
        Assert.True(first.Injections.ControllerPositionInvalidSampleCount > 0);
        Assert.Equal(first.RequestedSampleCount - first.DroppedSampleCount, first.AlignedPairs.Count);
        Assert.Contains(first.AlignedPairs, pair =>
            !pair.Tracker.IsTrackingValid || !pair.Controller.IsTrackingValid);

        var adjacentIntervals = first.RawTrackerSamples.Zip(first.RawTrackerSamples.Skip(1))
            .Select(samples => samples.Second.MonotonicTimeSeconds - samples.First.MonotonicTimeSeconds)
            .Select(interval => Math.Round(interval, 8))
            .Distinct()
            .Count();
        Assert.True(adjacentIntervals > 1, "Variable rate and jitter should produce differing intervals.");
    }

    [Fact]
    public void OutliersAndTrackingDiscontinuitiesDoNotCorruptCalibration()
    {
        var options = SyntheticGenerationOptions.ForScenario(SyntheticScenario.Noisy, seed: 62017) with
        {
            SampleCount = 240,
            DropProbability = 0.03,
            OutlierProbability = 0.02,
            QuaternionSignFlipProbability = 0.5,
            TrackingInvalidProbability = 0.04,
            ControllerPositionAvailability = 0.9,
            TimestampJitterStdDevMilliseconds = 0.3,
            VariableRateFraction = 0.05,
        };
        var dataset = SyntheticPoseGenerator.Generate(options);

        var result = HandEyeCalibrationSolver.Solve(dataset.AlignedPairs, CalibrationPolicy.Auto);

        Assert.True(dataset.Injections.OutlierPoseCount > 0);
        Assert.True(dataset.Injections.TrackingInvalidSampleCount > 0);
        Assert.True(result.RotationValidSampleCount < dataset.AlignedPairs.Count);
        Assert.True(result.Success, result.SelectionReason);
        Assert.Equal(CalibrationModel.FullSixDof, result.SelectedModel);
        AssertTransformNear(dataset.GroundTruthMount, result.TrackerToController, 1.5, 0.015);
    }

    private static void AssertTransformNear(
        RigidTransform expected,
        RigidTransform actual,
        double rotationToleranceDegrees,
        double translationToleranceMeters)
    {
        var expectedRotation = expected.Rotation;
        var actualRotation = actual.Rotation;
        var numerator = Math.Abs(
            ((double)expectedRotation.X * actualRotation.X) +
            ((double)expectedRotation.Y * actualRotation.Y) +
            ((double)expectedRotation.Z * actualRotation.Z) +
            ((double)expectedRotation.W * actualRotation.W));
        var expectedNormSquared =
            ((double)expectedRotation.X * expectedRotation.X) +
            ((double)expectedRotation.Y * expectedRotation.Y) +
            ((double)expectedRotation.Z * expectedRotation.Z) +
            ((double)expectedRotation.W * expectedRotation.W);
        var actualNormSquared =
            ((double)actualRotation.X * actualRotation.X) +
            ((double)actualRotation.Y * actualRotation.Y) +
            ((double)actualRotation.Z * actualRotation.Z) +
            ((double)actualRotation.W * actualRotation.W);
        var dot = Math.Clamp(
            numerator / Math.Sqrt(expectedNormSquared * actualNormSquared),
            0.0,
            1.0);
        var rotationErrorDegrees = 2.0 * Math.Acos(dot) * 180.0 / Math.PI;
        Assert.True(rotationErrorDegrees <= rotationToleranceDegrees,
            $"Rotation error {rotationErrorDegrees:R} degrees exceeded {rotationToleranceDegrees:R} degrees.");
        Assert.True(Vector3.Distance(expected.TranslationMeters, actual.TranslationMeters) <= translationToleranceMeters);
    }

    private static void AssertFiniteInvariantMetric(string report, string metricName)
    {
        var line = report.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(candidate => candidate.StartsWith($"{metricName}: ", StringComparison.Ordinal));
        var value = line[(metricName.Length + 2)..];
        Assert.NotEqual("n/a", value);
        Assert.DoesNotContain(',', value);
        Assert.True(double.TryParse(
            value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out _), $"Metric '{metricName}' was not invariant numeric text: '{value}'.");
    }
}
