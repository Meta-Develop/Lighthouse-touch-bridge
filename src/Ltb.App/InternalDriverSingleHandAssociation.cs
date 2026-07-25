using Ltb.Calibration;
using Ltb.Core;

namespace Ltb.App;

internal sealed record InternalDriverSingleHandAssociationResult(
    TrackerAssociationStatus Status,
    string Reason,
    HandTrackerAssignment? Assignment,
    IReadOnlyList<TrackerAssociationCandidateScore> Scores)
{
    public bool Success =>
        Status == TrackerAssociationStatus.Success && Assignment is not null;
}

/// <summary>
/// App-side selected-hand association. Every viable current candidate is
/// scored, weak candidates with usable lag evidence remain ambiguity
/// contenders, and the retained other-hand tracker is never reassigned.
/// </summary>
internal static class InternalDriverSingleHandAssociator
{
    public static InternalDriverSingleHandAssociationResult Associate(
        HandMotionCapture capture,
        string preservedOtherHandTrackerSerial,
        TrackerAssociationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentException.ThrowIfNullOrWhiteSpace(preservedOtherHandTrackerSerial);
        options ??= new TrackerAssociationOptions();
        Validate(options);

        var scores = capture.TrackerCandidates
            .OrderBy(candidate => candidate.TrackerSerial, StringComparer.Ordinal)
            .Select(candidate => Score(capture, candidate, options))
            .ToArray();
        var accepted = scores
            .Where(score => score.IsAccepted)
            .OrderByDescending(score => score.CorrelationScore)
            .ThenBy(score => score.TrackerSerial, StringComparer.Ordinal)
            .ToArray();
        if (accepted.Length == 0)
        {
            var status = scores.Any(score =>
                score.Rejection == TrackerAssociationCandidateRejection.AmbiguousLag)
                    ? TrackerAssociationStatus.Ambiguous
                    : scores.Any(score =>
                        score.Rejection == TrackerAssociationCandidateRejection.WeakCorrelation)
                        ? TrackerAssociationStatus.WeakCorrelation
                        : TrackerAssociationStatus.InvalidCandidate;
            return Failure(
                status,
                "No selected-hand tracker candidate passed connection, validity, motion, lag, and correlation gates.",
                scores);
        }

        var winner = accepted[0];
        var runnerUp = scores
            .Where(score =>
                score.Lag is not null &&
                !string.Equals(
                    score.TrackerSerial,
                    winner.TrackerSerial,
                    StringComparison.Ordinal))
            .OrderByDescending(score => score.CorrelationScore)
            .ThenBy(score => score.TrackerSerial, StringComparer.Ordinal)
            .FirstOrDefault();
        if (runnerUp is not null)
        {
            var margin = winner.CorrelationScore - runnerUp.CorrelationScore;
            if (margin < options.MinimumAssignmentScoreMargin)
            {
                return Failure(
                    TrackerAssociationStatus.Ambiguous,
                    $"The requested {capture.Hand.ToString().ToLowerInvariant()} hand has similar " +
                    $"motion on {winner.TrackerSerial} and {runnerUp.TrackerSerial}; the " +
                    $"winner/runner-up margin {margin:F4} is below the " +
                    $"{options.MinimumAssignmentScoreMargin:F4} gate.",
                    scores);
            }
        }

        if (string.Equals(
                winner.TrackerSerial,
                preservedOtherHandTrackerSerial,
                StringComparison.Ordinal))
        {
            return Failure(
                TrackerAssociationStatus.Ambiguous,
                $"The requested {capture.Hand.ToString().ToLowerInvariant()} hand resolved to " +
                $"'{winner.TrackerSerial}', which is retained by the unselected hand; " +
                "the existing hand association was preserved and no profile was replaced.",
                scores);
        }

        return new InternalDriverSingleHandAssociationResult(
            TrackerAssociationStatus.Success,
            $"Assigned the requested {capture.Hand.ToString().ToLowerInvariant()} hand to " +
            $"{winner.TrackerSerial} after scoring all viable candidates while preserving " +
            $"{preservedOtherHandTrackerSerial}.",
            new HandTrackerAssignment(
                capture.Hand,
                winner.TrackerSerial,
                winner.Lag!),
            Array.AsReadOnly(scores));
    }

    private static TrackerAssociationCandidateScore Score(
        HandMotionCapture capture,
        TrackerAssociationCandidate candidate,
        TrackerAssociationOptions options)
    {
        var orientationValidity = candidate.Samples.Count == 0
            ? 0d
            : candidate.Samples.Count(sample => sample.HasValidOrientation) /
                (double)candidate.Samples.Count;
        try
        {
            var lag = StreamLagEstimator.EstimateControllerLag(
                candidate.Samples,
                capture.ControllerSamples,
                options.LagEstimation);
            if (!candidate.IsConnected)
            {
                return Rejected(
                    capture.Hand,
                    candidate.TrackerSerial,
                    TrackerAssociationCandidateRejection.Disconnected,
                    "Tracker was disconnected during the requested-hand capture.",
                    lag);
            }

            if (orientationValidity < options.MinimumOrientationValidityFraction)
            {
                return Rejected(
                    capture.Hand,
                    candidate.TrackerSerial,
                    TrackerAssociationCandidateRejection.RepeatedlyInvalid,
                    $"Tracker orientation validity {orientationValidity:P1} is below the " +
                    $"{options.MinimumOrientationValidityFraction:P1} gate.",
                    lag);
            }

            return lag.CorrelationScore < options.MinimumAcceptedCorrelation
                ? Rejected(
                    capture.Hand,
                    candidate.TrackerSerial,
                    TrackerAssociationCandidateRejection.WeakCorrelation,
                    $"Motion correlation {lag.CorrelationScore:F4} is below the " +
                    $"{options.MinimumAcceptedCorrelation:F4} gate.",
                    lag)
                : new TrackerAssociationCandidateScore(
                    capture.Hand,
                    candidate.TrackerSerial,
                    TrackerAssociationCandidateRejection.None,
                    "Accepted coordinate-invariant angular-speed correlation.",
                    lag);
        }
        catch (LagEstimationException exception)
        {
            if (!candidate.IsConnected)
            {
                return Rejected(
                    capture.Hand,
                    candidate.TrackerSerial,
                    TrackerAssociationCandidateRejection.Disconnected,
                    "Tracker was disconnected during the requested-hand capture.");
            }

            if (orientationValidity < options.MinimumOrientationValidityFraction)
            {
                return Rejected(
                    capture.Hand,
                    candidate.TrackerSerial,
                    TrackerAssociationCandidateRejection.RepeatedlyInvalid,
                    $"Tracker orientation validity {orientationValidity:P1} is below the " +
                    $"{options.MinimumOrientationValidityFraction:P1} gate.");
            }

            return Rejected(
                capture.Hand,
                candidate.TrackerSerial,
                MapRejection(exception.Reason),
                exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Rejected(
                capture.Hand,
                candidate.TrackerSerial,
                TrackerAssociationCandidateRejection.InvalidTimestamps,
                exception.Message);
        }
    }

    private static void Validate(TrackerAssociationOptions options)
    {
        if (!double.IsFinite(options.MinimumOrientationValidityFraction) ||
            options.MinimumOrientationValidityFraction is <= 0d or > 1d ||
            !double.IsFinite(options.MinimumAcceptedCorrelation) ||
            options.MinimumAcceptedCorrelation is < -1d or > 1d ||
            !double.IsFinite(options.MinimumAssignmentScoreMargin) ||
            options.MinimumAssignmentScoreMargin is < 0d or > 2d)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static TrackerAssociationCandidateRejection MapRejection(
        LagEstimationFailure failure) => failure switch
        {
            LagEstimationFailure.InsufficientMotion =>
                TrackerAssociationCandidateRejection.InsufficientMotion,
            LagEstimationFailure.InsufficientOverlap =>
                TrackerAssociationCandidateRejection.InsufficientOverlap,
            LagEstimationFailure.AmbiguousPeak or
            LagEstimationFailure.InsufficientComparisonEvidence or
            LagEstimationFailure.BoundaryPeak =>
                TrackerAssociationCandidateRejection.AmbiguousLag,
            _ => TrackerAssociationCandidateRejection.WeakCorrelation,
        };

    private static TrackerAssociationCandidateScore Rejected(
        CalibrationHand hand,
        string trackerSerial,
        TrackerAssociationCandidateRejection rejection,
        string reason,
        LagEstimate? lag = null) =>
        new(hand, trackerSerial, rejection, reason, lag);

    private static InternalDriverSingleHandAssociationResult Failure(
        TrackerAssociationStatus status,
        string reason,
        IReadOnlyList<TrackerAssociationCandidateScore> scores) =>
        new(status, reason, null, Array.AsReadOnly(scores.ToArray()));
}
