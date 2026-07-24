using System.Numerics;
using Ltb.Calibration;
using Ltb.Core;
using Ltb.SyntheticData;

namespace Ltb.Calibration.Tests;

public sealed class TrackerAssociationTests
{
    private const string LeftSerial = "LHR-TEST0001";
    private const string RightSerial = "LHR-TEST0002";
    private const string ChestSerial = "LHR-FBT-CHEST";
    private const string LeftFootSerial = "LHR-FBT-LEFT-FOOT";
    private const string RightFootSerial = "LHR-FBT-RIGHT-FOOT";

    [Fact]
    public void SerialAssignmentIgnoresReversedCandidateOrderAndReportsSwap()
    {
        var leftData = Dataset(seed: 1101, lagMilliseconds: 12d);
        var rightData = Dataset(seed: 2202, lagMilliseconds: 14d);
        var leftCapture = Capture(
            CalibrationHand.Left,
            leftData.RawControllerSamples,
            new TrackerAssociationCandidate(RightSerial, Static(leftData.RawTrackerSamples)),
            new TrackerAssociationCandidate(LeftSerial, leftData.RawTrackerSamples));
        var rightCapture = Capture(
            CalibrationHand.Right,
            rightData.RawControllerSamples,
            new TrackerAssociationCandidate(RightSerial, rightData.RawTrackerSamples),
            new TrackerAssociationCandidate(LeftSerial, Static(rightData.RawTrackerSamples)));

        var result = TrackerHandAssociator.Associate(leftCapture, rightCapture);

        Assert.True(result.Success, result.Reason);
        Assert.Equal(TrackerAssociationStatus.Success, result.Status);
        Assert.Equal(LeftSerial, result.Left!.TrackerSerial);
        Assert.Equal(RightSerial, result.Right!.TrackerSerial);
        Assert.True(result.InputOrderWasSwapped);
        Assert.Equal(
            TrackerCandidateOrderDiagnostic.ConsistentRightThenLeft,
            result.CandidateOrderDiagnostic);
        Assert.Contains("swapped", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.All(
            new[] { result.Left, result.Right },
            assignment => Assert.InRange(assignment!.Lag.CorrelationScore, 0.98d, 1d));
    }

    [Fact]
    public void CanonicalOrderAcrossBothCapturesIsReportedWithoutSwap()
    {
        var leftData = Dataset(seed: 1201, lagMilliseconds: 12d);
        var rightData = Dataset(seed: 2302, lagMilliseconds: 14d);
        var leftCapture = Capture(
            CalibrationHand.Left,
            leftData.RawControllerSamples,
            new TrackerAssociationCandidate(LeftSerial, leftData.RawTrackerSamples),
            new TrackerAssociationCandidate(RightSerial, Static(leftData.RawTrackerSamples)));
        var rightCapture = Capture(
            CalibrationHand.Right,
            rightData.RawControllerSamples,
            new TrackerAssociationCandidate(LeftSerial, Static(rightData.RawTrackerSamples)),
            new TrackerAssociationCandidate(RightSerial, rightData.RawTrackerSamples));

        var result = TrackerHandAssociator.Associate(leftCapture, rightCapture);

        Assert.True(result.Success, result.Reason);
        Assert.False(result.InputOrderWasSwapped);
        Assert.Equal(
            TrackerCandidateOrderDiagnostic.ConsistentLeftThenRight,
            result.CandidateOrderDiagnostic);
    }

    [Fact]
    public void FiveCandidateAssignmentSelectsUniqueMountedPairByStableSerial()
    {
        var leftData = Dataset(seed: 1401, lagMilliseconds: 12d);
        var rightData = Dataset(seed: 2502, lagMilliseconds: 14d);
        var leftCapture = Capture(
            CalibrationHand.Left,
            leftData.RawControllerSamples,
            new TrackerAssociationCandidate(ChestSerial, Static(leftData.RawTrackerSamples)),
            new TrackerAssociationCandidate(RightSerial, Static(leftData.RawTrackerSamples)),
            new TrackerAssociationCandidate(LeftSerial, leftData.RawTrackerSamples),
            new TrackerAssociationCandidate(RightFootSerial, Static(leftData.RawTrackerSamples)),
            new TrackerAssociationCandidate(LeftFootSerial, Static(leftData.RawTrackerSamples)));
        var rightCapture = Capture(
            CalibrationHand.Right,
            rightData.RawControllerSamples,
            new TrackerAssociationCandidate(LeftFootSerial, Static(rightData.RawTrackerSamples)),
            new TrackerAssociationCandidate(LeftSerial, Static(rightData.RawTrackerSamples)),
            new TrackerAssociationCandidate(ChestSerial, Static(rightData.RawTrackerSamples)),
            new TrackerAssociationCandidate(RightSerial, rightData.RawTrackerSamples),
            new TrackerAssociationCandidate(RightFootSerial, Static(rightData.RawTrackerSamples)));

        var result = TrackerHandAssociator.Associate(leftCapture, rightCapture);

        Assert.True(result.Success, result.Reason);
        Assert.Equal(LeftSerial, result.Left!.TrackerSerial);
        Assert.Equal(RightSerial, result.Right!.TrackerSerial);
        Assert.Equal(10, result.Scores.Count);
        Assert.Equal(
            TrackerCandidateOrderDiagnostic.InconsistentBetweenCaptures,
            result.CandidateOrderDiagnostic);
    }

    [Fact]
    public void ThirdCandidateMirroringPromptedHandFailsClosedAsAmbiguous()
    {
        var leftData = Dataset(seed: 1501, lagMilliseconds: 12d);
        var rightData = Dataset(seed: 2602, lagMilliseconds: 14d);
        var leftCapture = Capture(
            CalibrationHand.Left,
            leftData.RawControllerSamples,
            new TrackerAssociationCandidate(LeftSerial, leftData.RawTrackerSamples),
            new TrackerAssociationCandidate(RightSerial, Static(leftData.RawTrackerSamples)),
            new TrackerAssociationCandidate(ChestSerial, leftData.RawTrackerSamples));
        var rightCapture = Capture(
            CalibrationHand.Right,
            rightData.RawControllerSamples,
            new TrackerAssociationCandidate(LeftSerial, Static(rightData.RawTrackerSamples)),
            new TrackerAssociationCandidate(RightSerial, rightData.RawTrackerSamples),
            new TrackerAssociationCandidate(ChestSerial, Static(rightData.RawTrackerSamples)));

        var result = TrackerHandAssociator.Associate(leftCapture, rightCapture);

        Assert.False(result.Success);
        Assert.Equal(TrackerAssociationStatus.Ambiguous, result.Status);
        Assert.Null(result.Left);
        Assert.Null(result.Right);
        Assert.Contains("left capture", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("winner/runner-up", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FailedUnrelatedCandidatesDoNotRejectUniqueHealthyPair()
    {
        var leftData = Dataset(seed: 1601, lagMilliseconds: 12d);
        var rightData = Dataset(seed: 2702, lagMilliseconds: 14d);
        var leftCapture = Capture(
            CalibrationHand.Left,
            leftData.RawControllerSamples,
            new TrackerAssociationCandidate(LeftSerial, leftData.RawTrackerSamples),
            new TrackerAssociationCandidate(RightSerial, Static(leftData.RawTrackerSamples)),
            new TrackerAssociationCandidate(
                ChestSerial,
                Static(leftData.RawTrackerSamples),
                isConnected: false),
            new TrackerAssociationCandidate(
                LeftFootSerial,
                Invalid(leftData.RawTrackerSamples)));
        var rightCapture = Capture(
            CalibrationHand.Right,
            rightData.RawControllerSamples,
            new TrackerAssociationCandidate(LeftSerial, Static(rightData.RawTrackerSamples)),
            new TrackerAssociationCandidate(RightSerial, rightData.RawTrackerSamples),
            new TrackerAssociationCandidate(
                ChestSerial,
                Static(rightData.RawTrackerSamples),
                isConnected: false),
            new TrackerAssociationCandidate(
                LeftFootSerial,
                Invalid(rightData.RawTrackerSamples)));

        var result = TrackerHandAssociator.Associate(leftCapture, rightCapture);

        Assert.True(result.Success, result.Reason);
        Assert.Equal(LeftSerial, result.Left!.TrackerSerial);
        Assert.Equal(RightSerial, result.Right!.TrackerSerial);
        Assert.Contains(result.Scores, score =>
            score.TrackerSerial == ChestSerial &&
            score.Rejection is TrackerAssociationCandidateRejection.Disconnected);
        Assert.Contains(result.Scores, score =>
            score.TrackerSerial == LeftFootSerial &&
            score.Rejection is TrackerAssociationCandidateRejection.RepeatedlyInvalid);
    }

    [Fact]
    public void DisconnectedSelectedCandidateDoesNotFallBackToUnrelatedTracker()
    {
        var leftData = Dataset(seed: 1701, lagMilliseconds: 12d);
        var rightData = Dataset(seed: 2802, lagMilliseconds: 14d);
        var leftCapture = Capture(
            CalibrationHand.Left,
            leftData.RawControllerSamples,
            new TrackerAssociationCandidate(
                LeftSerial,
                leftData.RawTrackerSamples,
                isConnected: false),
            new TrackerAssociationCandidate(RightSerial, Static(leftData.RawTrackerSamples)),
            new TrackerAssociationCandidate(ChestSerial, Static(leftData.RawTrackerSamples)));
        var rightCapture = Capture(
            CalibrationHand.Right,
            rightData.RawControllerSamples,
            new TrackerAssociationCandidate(LeftSerial, Static(rightData.RawTrackerSamples)),
            new TrackerAssociationCandidate(RightSerial, rightData.RawTrackerSamples),
            new TrackerAssociationCandidate(ChestSerial, Static(rightData.RawTrackerSamples)));

        var result = TrackerHandAssociator.Associate(leftCapture, rightCapture);

        Assert.False(result.Success);
        Assert.Equal(TrackerAssociationStatus.InvalidCandidate, result.Status);
        Assert.Null(result.Left);
        Assert.Null(result.Right);
        Assert.Contains(result.Scores, score =>
            score.Hand is CalibrationHand.Left &&
            score.TrackerSerial == LeftSerial &&
            score.Rejection is TrackerAssociationCandidateRejection.Disconnected);
    }

    [Fact]
    public void RightCaptureOnlyReversedIsReportedAsInconsistentOrder()
    {
        var leftData = Dataset(seed: 1301, lagMilliseconds: 12d);
        var rightData = Dataset(seed: 2402, lagMilliseconds: 14d);
        var leftCapture = Capture(
            CalibrationHand.Left,
            leftData.RawControllerSamples,
            new TrackerAssociationCandidate(LeftSerial, leftData.RawTrackerSamples),
            new TrackerAssociationCandidate(RightSerial, Static(leftData.RawTrackerSamples)));
        var rightCapture = Capture(
            CalibrationHand.Right,
            rightData.RawControllerSamples,
            new TrackerAssociationCandidate(RightSerial, rightData.RawTrackerSamples),
            new TrackerAssociationCandidate(LeftSerial, Static(rightData.RawTrackerSamples)));

        var result = TrackerHandAssociator.Associate(leftCapture, rightCapture);

        Assert.True(result.Success, result.Reason);
        Assert.True(result.InputOrderWasSwapped);
        Assert.Equal(
            TrackerCandidateOrderDiagnostic.InconsistentBetweenCaptures,
            result.CandidateOrderDiagnostic);
        Assert.Contains("inconsistent", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SimilarTrackerMotionIsRejectedAsAmbiguous()
    {
        var leftData = Dataset(seed: 3303, lagMilliseconds: 12d);
        var rightData = Dataset(seed: 4404, lagMilliseconds: 13d);
        var leftCapture = Capture(
            CalibrationHand.Left,
            leftData.RawControllerSamples,
            new TrackerAssociationCandidate(LeftSerial, leftData.RawTrackerSamples),
            new TrackerAssociationCandidate(RightSerial, leftData.RawTrackerSamples));
        var rightCapture = Capture(
            CalibrationHand.Right,
            rightData.RawControllerSamples,
            new TrackerAssociationCandidate(LeftSerial, rightData.RawTrackerSamples),
            new TrackerAssociationCandidate(RightSerial, rightData.RawTrackerSamples));

        var result = TrackerHandAssociator.Associate(leftCapture, rightCapture);

        Assert.False(result.Success);
        Assert.Equal(TrackerAssociationStatus.Ambiguous, result.Status);
        Assert.Null(result.Left);
        Assert.Null(result.Right);
        Assert.Contains("margin", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SimilarMotionInOnlyOneHandCaptureIsStillAmbiguous()
    {
        var leftData = Dataset(seed: 3503, lagMilliseconds: 12d);
        var rightData = Dataset(seed: 4604, lagMilliseconds: 13d);
        var leftCapture = Capture(
            CalibrationHand.Left,
            leftData.RawControllerSamples,
            new TrackerAssociationCandidate(LeftSerial, leftData.RawTrackerSamples),
            new TrackerAssociationCandidate(RightSerial, leftData.RawTrackerSamples));
        var rightCapture = Capture(
            CalibrationHand.Right,
            rightData.RawControllerSamples,
            new TrackerAssociationCandidate(LeftSerial, Static(rightData.RawTrackerSamples)),
            new TrackerAssociationCandidate(RightSerial, rightData.RawTrackerSamples));

        var result = TrackerHandAssociator.Associate(leftCapture, rightCapture);

        Assert.False(result.Success);
        Assert.Equal(TrackerAssociationStatus.Ambiguous, result.Status);
        Assert.Contains("left capture", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("winner/runner-up", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WeakUnrelatedMotionIsRejectedExplicitly()
    {
        var leftData = Dataset(seed: 5505, lagMilliseconds: 12d);
        var rightData = Dataset(seed: 6606, lagMilliseconds: 13d);
        var leftCapture = Capture(
            CalibrationHand.Left,
            leftData.RawControllerSamples,
            new TrackerAssociationCandidate(LeftSerial, Unrelated(leftData.RawTrackerSamples, 7)),
            new TrackerAssociationCandidate(RightSerial, Unrelated(leftData.RawTrackerSamples, 8)));
        var rightCapture = Capture(
            CalibrationHand.Right,
            rightData.RawControllerSamples,
            new TrackerAssociationCandidate(LeftSerial, Unrelated(rightData.RawTrackerSamples, 9)),
            new TrackerAssociationCandidate(RightSerial, Unrelated(rightData.RawTrackerSamples, 10)));

        var result = TrackerHandAssociator.Associate(
            leftCapture,
            rightCapture,
            new TrackerAssociationOptions
            {
                MinimumAcceptedCorrelation = 0.95d,
                LagEstimation = new LagEstimationOptions
                {
                    MinimumCorrelationScore = -1d,
                    MinimumPeakProminence = 0d,
                    RequireInteriorPeak = false,
                },
            });

        Assert.False(result.Success);
        Assert.Equal(TrackerAssociationStatus.WeakCorrelation, result.Status);
        Assert.Contains(result.Scores, score =>
            score.Rejection is TrackerAssociationCandidateRejection.WeakCorrelation);
    }

    [Fact]
    public void DisconnectedOrRepeatedlyInvalidCandidatesCannotBeAssigned()
    {
        var leftData = Dataset(seed: 7707, lagMilliseconds: 12d);
        var rightData = Dataset(seed: 8808, lagMilliseconds: 13d);
        var leftCapture = Capture(
            CalibrationHand.Left,
            leftData.RawControllerSamples,
            new TrackerAssociationCandidate(LeftSerial, leftData.RawTrackerSamples, isConnected: false),
            new TrackerAssociationCandidate(RightSerial, Invalid(leftData.RawTrackerSamples)));
        var rightCapture = Capture(
            CalibrationHand.Right,
            rightData.RawControllerSamples,
            new TrackerAssociationCandidate(LeftSerial, Static(rightData.RawTrackerSamples)),
            new TrackerAssociationCandidate(RightSerial, rightData.RawTrackerSamples));

        var result = TrackerHandAssociator.Associate(leftCapture, rightCapture);

        Assert.False(result.Success);
        Assert.Equal(TrackerAssociationStatus.InvalidCandidate, result.Status);
        Assert.Contains(result.Scores, score =>
            score.Rejection is TrackerAssociationCandidateRejection.Disconnected);
        Assert.Contains(result.Scores, score =>
            score.Rejection is TrackerAssociationCandidateRejection.RepeatedlyInvalid);
    }

    [Fact]
    public void SelectedSerialMustPassHealthGatesInOppositeCapture()
    {
        var leftData = Dataset(seed: 7807, lagMilliseconds: 12d);
        var rightData = Dataset(seed: 8908, lagMilliseconds: 13d);
        var leftCapture = Capture(
            CalibrationHand.Left,
            leftData.RawControllerSamples,
            new TrackerAssociationCandidate(LeftSerial, leftData.RawTrackerSamples),
            new TrackerAssociationCandidate(
                RightSerial,
                Static(leftData.RawTrackerSamples),
                isConnected: false));
        var rightCapture = Capture(
            CalibrationHand.Right,
            rightData.RawControllerSamples,
            new TrackerAssociationCandidate(LeftSerial, Invalid(rightData.RawTrackerSamples)),
            new TrackerAssociationCandidate(RightSerial, rightData.RawTrackerSamples));

        var result = TrackerHandAssociator.Associate(leftCapture, rightCapture);

        Assert.False(result.Success);
        Assert.Equal(TrackerAssociationStatus.InvalidCandidate, result.Status);
        var leftSelectedScore = Assert.Single(result.Scores, score =>
            score.Hand is CalibrationHand.Left && score.TrackerSerial == LeftSerial);
        Assert.Equal(
            TrackerAssociationCandidateRejection.RepeatedlyInvalid,
            leftSelectedScore.Rejection);
        Assert.Contains("opposite hand capture", leftSelectedScore.Reason, StringComparison.OrdinalIgnoreCase);
        var rightSelectedScore = Assert.Single(result.Scores, score =>
            score.Hand is CalibrationHand.Right && score.TrackerSerial == RightSerial);
        Assert.Equal(
            TrackerAssociationCandidateRejection.Disconnected,
            rightSelectedScore.Rejection);
        Assert.Contains("opposite hand capture", rightSelectedScore.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OtherwiseValidAssignmentRejectsInconsistentInterHandLag()
    {
        var leftData = Dataset(seed: 9909, lagMilliseconds: 5d);
        var rightData = Dataset(seed: 1110, lagMilliseconds: 60d);
        var leftCapture = Capture(
            CalibrationHand.Left,
            leftData.RawControllerSamples,
            new TrackerAssociationCandidate(LeftSerial, leftData.RawTrackerSamples),
            new TrackerAssociationCandidate(RightSerial, Static(leftData.RawTrackerSamples)));
        var rightCapture = Capture(
            CalibrationHand.Right,
            rightData.RawControllerSamples,
            new TrackerAssociationCandidate(LeftSerial, Static(rightData.RawTrackerSamples)),
            new TrackerAssociationCandidate(RightSerial, rightData.RawTrackerSamples));

        var result = TrackerHandAssociator.Associate(leftCapture, rightCapture);

        Assert.False(result.Success);
        Assert.Equal(TrackerAssociationStatus.InconsistentLag, result.Status);
        Assert.Contains("lag", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static SyntheticPoseDataset Dataset(int seed, double lagMilliseconds) =>
        SyntheticPoseGenerator.Generate(
            SyntheticGenerationOptions.ForScenario(SyntheticScenario.Clean, seed) with
            {
                KnownLagMilliseconds = lagMilliseconds,
                SampleCount = 240,
            });

    private static HandMotionCapture Capture(
        CalibrationHand hand,
        IReadOnlyList<TimestampedPoseSample> controller,
        params TrackerAssociationCandidate[] candidates) =>
        new(hand, controller, candidates);

    private static IReadOnlyList<TimestampedPoseSample> Static(
        IReadOnlyList<TimestampedPoseSample> template) =>
        template.Select(sample => new TimestampedPoseSample(
            sample.MonotonicTimeSeconds,
            RigidTransform.Identity,
            PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid)).ToArray();

    private static IReadOnlyList<TimestampedPoseSample> Invalid(
        IReadOnlyList<TimestampedPoseSample> template) =>
        template.Select(sample => new TimestampedPoseSample(
            sample.MonotonicTimeSeconds,
            sample.Pose,
            PoseValidity.Orientation | PoseValidity.Position)).ToArray();

    private static IReadOnlyList<TimestampedPoseSample> Unrelated(
        IReadOnlyList<TimestampedPoseSample> template,
        int seed)
    {
        var random = new Random(seed);
        return template.Select(sample =>
        {
            var axis = Vector3.Normalize(new Vector3(
                (float)(0.1d + random.NextDouble()),
                (float)(0.1d + random.NextDouble()),
                (float)(0.1d + random.NextDouble())));
            var angle = (float)((2d * random.NextDouble()) - 1d);
            return new TimestampedPoseSample(
                sample.MonotonicTimeSeconds,
                new RigidTransform(Quaternion.CreateFromAxisAngle(axis, angle), Vector3.Zero),
                PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid);
        }).ToArray();
    }
}
