using Ltb.Core;

namespace Ltb.Calibration.Tests;

public sealed class CalibrationDegeneracyTests
{
    [Theory]
    [InlineData(HandEyeTestMotion.Static, CalibrationDegeneracy.StaticMotion)]
    [InlineData(HandEyeTestMotion.PureTranslation, CalibrationDegeneracy.StaticMotion)]
    [InlineData(HandEyeTestMotion.SingleAxis, CalibrationDegeneracy.SingleAxisRotation)]
    public void RotationDegenerateCaptureFailsWithoutClaimingFallback(
        HandEyeTestMotion motion,
        CalibrationDegeneracy expectedDegeneracy)
    {
        var result = HandEyeCalibrationSolver.Solve(
            HandEyeTestData.CreatePairs(72, motion),
            CalibrationPolicy.Auto);

        Assert.False(result.Success);
        Assert.Equal(CalibrationModel.Failed, result.SelectedModel);
        Assert.Equal(expectedDegeneracy, result.Motion.RotationDegeneracy);
        Assert.False(result.Motion.RotationObservable);
        Assert.False(result.Motion.TranslationObservable);
    }

    [Fact]
    public void AutoFallsBackWhenTrackedOrientationHasNoPosition()
    {
        var result = HandEyeCalibrationSolver.Solve(
            HandEyeTestData.CreatePairs(72, HandEyeTestMotion.MultiAxis, includePosition: false),
            CalibrationPolicy.Auto);

        Assert.True(result.Success, result.SelectionReason);
        Assert.Equal(CalibrationModel.RotationOnly, result.SelectedModel);
        Assert.True(result.Motion.RotationObservable);
        Assert.False(result.Motion.TranslationObservable);
        Assert.Equal(CalibrationDegeneracy.MissingPosition, result.Motion.TranslationDegeneracy);
        Assert.Contains("Auto selected rotation-only", result.SelectionReason);
    }

    [Fact]
    public void FullSixDofFailsWhenTrackedOrientationHasNoPosition()
    {
        var result = HandEyeCalibrationSolver.Solve(
            HandEyeTestData.CreatePairs(72, HandEyeTestMotion.MultiAxis, includePosition: false),
            CalibrationPolicy.FullSixDof);

        Assert.False(result.Success);
        Assert.Equal(CalibrationModel.Failed, result.SelectedModel);
        Assert.True(result.Motion.RotationObservable);
        Assert.Equal(CalibrationDegeneracy.MissingPosition, result.Motion.TranslationDegeneracy);
    }

    [Fact]
    public void AutoFallsBackWhenTranslationConditionGateRejectsOtherwiseRichMotion()
    {
        var result = HandEyeCalibrationSolver.Solve(
            HandEyeTestData.CreatePairs(72, HandEyeTestMotion.MultiAxis),
            CalibrationPolicy.Auto,
            new CalibrationOptions { MaximumTranslationConditionNumber = 1.000001d });

        Assert.True(result.Success, result.SelectionReason);
        Assert.Equal(CalibrationModel.RotationOnly, result.SelectedModel);
        Assert.Equal(CalibrationDegeneracy.TranslationIllConditioned, result.Motion.TranslationDegeneracy);
        Assert.True(double.IsFinite(result.Motion.TranslationConditionNumber));
    }

    [Fact]
    public void AutoFallsBackWhenIndependentTranslationSubsetsDisagree()
    {
        var pairs = HandEyeTestData.CreatePairs(
            96,
            HandEyeTestMotion.MultiAxis,
            seed: 4421,
            rotationNoiseDegrees: 0.08d,
            positionNoiseMeters: 0.0005d);

        var result = HandEyeCalibrationSolver.Solve(
            pairs,
            CalibrationPolicy.Auto,
            new CalibrationOptions { MaximumTranslationSplitDisagreementMillimeters = 1e-6d });

        Assert.True(result.Success, result.SelectionReason);
        Assert.Equal(CalibrationModel.RotationOnly, result.SelectedModel);
        Assert.Equal(CalibrationDegeneracy.TranslationUnstable, result.Motion.TranslationDegeneracy);
        Assert.True(double.IsFinite(result.Quality.TranslationSplitDisagreementMillimeters));
        Assert.True(result.Quality.TranslationSplitDisagreementMillimeters > 1e-6d);
    }

    [Fact]
    public void SolverRejectsTimestampDeltaStricterThanPairConstructionContract()
    {
        var pairs = HandEyeTestData.CreatePairs(
            24,
            HandEyeTestMotion.MultiAxis,
            controllerTimestampOffsetSeconds: 5e-7d);

        var result = HandEyeCalibrationSolver.Solve(
            pairs,
            CalibrationPolicy.Auto,
            new CalibrationOptions { MaximumTimestampDifferenceSeconds = 1e-7d });

        Assert.False(result.Success);
        Assert.Equal(CalibrationDegeneracy.TimestampMismatch, result.Motion.RotationDegeneracy);
        Assert.Contains("lag estimation belongs to Milestone 1", result.SelectionReason);
    }

    [Fact]
    public void PairConstructionRejectsUnsynchronizedRawSamples()
    {
        var pose = RigidTransform.Identity;
        var validity = PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid;
        var tracker = new TimestampedPoseSample(1d, pose, validity);
        var controller = new TimestampedPoseSample(
            1d + (2d * SynchronizedPosePair.TimestampMatchToleranceSeconds),
            pose,
            validity);

        var exception = Assert.Throws<ArgumentException>(() =>
            new SynchronizedPosePair(tracker, controller));

        Assert.Contains("timestamp-matched", exception.Message);
    }
}
