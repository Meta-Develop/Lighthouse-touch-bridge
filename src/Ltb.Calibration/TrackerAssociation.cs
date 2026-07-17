using Ltb.Core;

namespace Ltb.Calibration;

/// <summary>One serial-keyed tracker stream observed during a hand-motion capture.</summary>
public sealed record TrackerAssociationCandidate
{
    public TrackerAssociationCandidate(
        string trackerSerial,
        IReadOnlyList<TimestampedPoseSample> samples,
        bool isConnected = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackerSerial);
        ArgumentNullException.ThrowIfNull(samples);
        TrackerSerial = trackerSerial;
        Samples = Array.AsReadOnly(samples.ToArray());
        IsConnected = isConnected;
    }

    public string TrackerSerial { get; }

    public IReadOnlyList<TimestampedPoseSample> Samples { get; }

    public bool IsConnected { get; }
}

/// <summary>
/// Controller and tracker candidate streams captured while the user was asked
/// to move only one hand.
/// </summary>
public sealed record HandMotionCapture
{
    public HandMotionCapture(
        CalibrationHand hand,
        IReadOnlyList<TimestampedPoseSample> controllerSamples,
        IReadOnlyList<TrackerAssociationCandidate> trackerCandidates)
    {
        if (!Enum.IsDefined(hand))
        {
            throw new ArgumentOutOfRangeException(nameof(hand));
        }

        ArgumentNullException.ThrowIfNull(controllerSamples);
        ArgumentNullException.ThrowIfNull(trackerCandidates);
        if (trackerCandidates.Any(candidate => candidate is null))
        {
            throw new ArgumentException("Tracker candidates cannot contain null entries.", nameof(trackerCandidates));
        }

        var duplicateSerial = trackerCandidates
            .GroupBy(candidate => candidate.TrackerSerial, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateSerial is not null)
        {
            throw new ArgumentException(
                $"Capture contains duplicate tracker serial '{duplicateSerial}'.",
                nameof(trackerCandidates));
        }

        Hand = hand;
        ControllerSamples = Array.AsReadOnly(controllerSamples.ToArray());
        TrackerCandidates = Array.AsReadOnly(trackerCandidates.ToArray());
    }

    public CalibrationHand Hand { get; }

    public IReadOnlyList<TimestampedPoseSample> ControllerSamples { get; }

    public IReadOnlyList<TrackerAssociationCandidate> TrackerCandidates { get; }
}

/// <summary>Association-specific gates layered over the reusable lag estimator.</summary>
public sealed record TrackerAssociationOptions
{
    public LagEstimationOptions? LagEstimation { get; init; }

    public double MinimumOrientationValidityFraction { get; init; } = 0.70d;

    public double MinimumAcceptedCorrelation { get; init; } = 0.80d;

    public double MinimumAssignmentScoreMargin { get; init; } = 0.05d;

    public double MaximumInterHandLagDifferenceSeconds { get; init; } = 0.025d;

    internal void Validate()
    {
        RequireRange(
            MinimumOrientationValidityFraction,
            0d,
            1d,
            nameof(MinimumOrientationValidityFraction),
            lowerInclusive: false);
        RequireRange(
            MinimumAcceptedCorrelation,
            -1d,
            1d,
            nameof(MinimumAcceptedCorrelation));
        RequireRange(
            MinimumAssignmentScoreMargin,
            0d,
            2d,
            nameof(MinimumAssignmentScoreMargin));
        if (!double.IsFinite(MaximumInterHandLagDifferenceSeconds) ||
            MaximumInterHandLagDifferenceSeconds <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumInterHandLagDifferenceSeconds));
        }

        LagEstimation?.Validate();
    }

    private static void RequireRange(
        double value,
        double minimum,
        double maximum,
        string parameterName,
        bool lowerInclusive = true)
    {
        if (!double.IsFinite(value) ||
            (lowerInclusive ? value < minimum : value <= minimum) ||
            value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

public enum TrackerAssociationCandidateRejection
{
    None,
    Disconnected,
    RepeatedlyInvalid,
    InvalidTimestamps,
    InsufficientMotion,
    InsufficientOverlap,
    WeakCorrelation,
    AmbiguousLag,
}

/// <summary>One hand/candidate score, including an accepted lag or explicit rejection.</summary>
public sealed record TrackerAssociationCandidateScore(
    CalibrationHand Hand,
    string TrackerSerial,
    TrackerAssociationCandidateRejection Rejection,
    string Reason,
    LagEstimate? Lag)
{
    public bool IsAccepted => Rejection is TrackerAssociationCandidateRejection.None && Lag is not null;

    public double CorrelationScore => Lag?.CorrelationScore ?? double.NaN;
}

public enum TrackerAssociationStatus
{
    Success,
    InsufficientCandidates,
    InvalidCandidate,
    WeakCorrelation,
    Ambiguous,
    InconsistentLag,
}

/// <summary>
/// Relationship between both capture candidate lists and the resolved
/// left/right serial assignment. Candidate order never affects assignment.
/// </summary>
public enum TrackerCandidateOrderDiagnostic
{
    Unavailable,
    ConsistentLeftThenRight,
    ConsistentRightThenLeft,
    InconsistentBetweenCaptures,
}

/// <summary>A stable serial assignment for one hand.</summary>
public sealed record HandTrackerAssignment(
    CalibrationHand Hand,
    string TrackerSerial,
    LagEstimate Lag);

/// <summary>Two-hand association result with stable serials and diagnostics.</summary>
public sealed record TrackerAssociationResult(
    TrackerAssociationStatus Status,
    string Reason,
    HandTrackerAssignment? Left,
    HandTrackerAssignment? Right,
    bool InputOrderWasSwapped,
    IReadOnlyList<TrackerAssociationCandidateScore> Scores)
{
    public bool Success =>
        Status is TrackerAssociationStatus.Success && Left is not null && Right is not null;

    /// <summary>
    /// Detailed interpretation of both input candidate orders. The retained
    /// <see cref="InputOrderWasSwapped"/> flag is true for either a consistently
    /// right-before-left order or inconsistent order across captures.
    /// </summary>
    public TrackerCandidateOrderDiagnostic CandidateOrderDiagnostic { get; init; }
}

/// <summary>
/// Associates two trackers to hands from angular-speed magnitude correlation.
/// It never compares world-space directions or assumes runtime device order.
/// </summary>
public static class TrackerHandAssociator
{
    public static TrackerAssociationResult Associate(
        HandMotionCapture leftCapture,
        HandMotionCapture rightCapture,
        TrackerAssociationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(leftCapture);
        ArgumentNullException.ThrowIfNull(rightCapture);
        if (leftCapture.Hand is not CalibrationHand.Left)
        {
            throw new ArgumentException("The left capture must be labeled Left.", nameof(leftCapture));
        }

        if (rightCapture.Hand is not CalibrationHand.Right)
        {
            throw new ArgumentException("The right capture must be labeled Right.", nameof(rightCapture));
        }

        options ??= new TrackerAssociationOptions();
        options.Validate();

        var commonSerials = leftCapture.TrackerCandidates
            .Select(candidate => candidate.TrackerSerial)
            .Intersect(
                rightCapture.TrackerCandidates.Select(candidate => candidate.TrackerSerial),
                StringComparer.Ordinal)
            .OrderBy(serial => serial, StringComparer.Ordinal)
            .ToArray();
        if (commonSerials.Length < 2)
        {
            return Failure(
                TrackerAssociationStatus.InsufficientCandidates,
                "At least two tracker serials must be present in both hand captures.",
                []);
        }

        var scores = new List<TrackerAssociationCandidateScore>(commonSerials.Length * 2);
        foreach (var serial in commonSerials)
        {
            var leftCandidate = leftCapture.TrackerCandidates.Single(candidate =>
                string.Equals(candidate.TrackerSerial, serial, StringComparison.Ordinal));
            var rightCandidate = rightCapture.TrackerCandidates.Single(candidate =>
                string.Equals(candidate.TrackerSerial, serial, StringComparison.Ordinal));
            scores.Add(Score(
                leftCapture,
                leftCandidate,
                rightCandidate,
                options));
            scores.Add(Score(
                rightCapture,
                rightCandidate,
                leftCandidate,
                options));
        }

        var acceptedLeft = scores.Where(score =>
            score.Hand is CalibrationHand.Left && score.IsAccepted).ToArray();
        var acceptedRight = scores.Where(score =>
            score.Hand is CalibrationHand.Right && score.IsAccepted).ToArray();
        var perHandAmbiguity = FindPerHandAmbiguity(
            acceptedLeft,
            acceptedRight,
            options.MinimumAssignmentScoreMargin);
        if (perHandAmbiguity is not null)
        {
            return Failure(
                TrackerAssociationStatus.Ambiguous,
                perHandAmbiguity,
                scores);
        }

        var assignments = (
            from left in acceptedLeft
            from right in acceptedRight
            where !string.Equals(left.TrackerSerial, right.TrackerSerial, StringComparison.Ordinal)
            select new AssignmentCandidate(
                left,
                right,
                (left.CorrelationScore + right.CorrelationScore) / 2d))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Left.TrackerSerial, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Right.TrackerSerial, StringComparer.Ordinal)
            .ToArray();

        if (assignments.Length == 0)
        {
            var status = DiagnoseNoAssignment(scores);
            return Failure(
                status,
                BuildNoAssignmentReason(status),
                scores);
        }

        var selected = assignments[0];
        if (assignments.Length > 1 &&
            selected.Score - assignments[1].Score < options.MinimumAssignmentScoreMargin)
        {
            return Failure(
                TrackerAssociationStatus.Ambiguous,
                $"The best two serial assignments differ by only {selected.Score - assignments[1].Score:F4}, below the {options.MinimumAssignmentScoreMargin:F4} margin.",
                scores);
        }

        var leftLag = selected.Left.Lag!;
        var rightLag = selected.Right.Lag!;
        var lagDifference = Math.Abs(leftLag.LagSeconds - rightLag.LagSeconds);
        if (lagDifference > options.MaximumInterHandLagDifferenceSeconds)
        {
            return Failure(
                TrackerAssociationStatus.InconsistentLag,
                $"Accepted hand lag estimates differ by {lagDifference * 1000d:F2} ms, above the {options.MaximumInterHandLagDifferenceSeconds * 1000d:F2} ms gate.",
                scores);
        }

        var leftAssignment = new HandTrackerAssignment(
            CalibrationHand.Left,
            selected.Left.TrackerSerial,
            leftLag);
        var rightAssignment = new HandTrackerAssignment(
            CalibrationHand.Right,
            selected.Right.TrackerSerial,
            rightLag);
        var candidateOrderDiagnostic = DiagnoseCandidateOrder(
            leftCapture,
            rightCapture,
            leftAssignment.TrackerSerial,
            rightAssignment.TrackerSerial);
        var inputOrderWasSwapped = candidateOrderDiagnostic is
            TrackerCandidateOrderDiagnostic.ConsistentRightThenLeft or
            TrackerCandidateOrderDiagnostic.InconsistentBetweenCaptures;
        var orderDiagnosticText = candidateOrderDiagnostic switch
        {
            TrackerCandidateOrderDiagnostic.ConsistentRightThenLeft =>
                " Both capture candidate lists were right-before-left; serial-based assignment corrected the swapped order.",
            TrackerCandidateOrderDiagnostic.InconsistentBetweenCaptures =>
                " Candidate order was inconsistent between captures; serial-based assignment ignored list order.",
            _ => string.Empty,
        };
        return new TrackerAssociationResult(
            TrackerAssociationStatus.Success,
            $"Assigned left to {leftAssignment.TrackerSerial} and right to {rightAssignment.TrackerSerial} from coordinate-invariant angular-speed correlation.{orderDiagnosticText}",
            leftAssignment,
            rightAssignment,
            inputOrderWasSwapped,
            Array.AsReadOnly(scores.ToArray()))
        {
            CandidateOrderDiagnostic = candidateOrderDiagnostic,
        };
    }

    private static TrackerAssociationCandidateScore Score(
        HandMotionCapture capture,
        TrackerAssociationCandidate candidate,
        TrackerAssociationCandidate oppositeCaptureCandidate,
        TrackerAssociationOptions options)
    {
        var currentHealth = EvaluateHealth(candidate, options);
        if (!currentHealth.IsHealthy)
        {
            return Rejected(capture.Hand, candidate.TrackerSerial,
                currentHealth.Rejection,
                $"Tracker failed health gates during the {capture.Hand.ToString().ToLowerInvariant()} capture: {currentHealth.Reason}");
        }

        var oppositeHealth = EvaluateHealth(oppositeCaptureCandidate, options);
        if (!oppositeHealth.IsHealthy)
        {
            return Rejected(capture.Hand, candidate.TrackerSerial,
                oppositeHealth.Rejection,
                $"Tracker failed health gates during the opposite hand capture: {oppositeHealth.Reason}");
        }

        try
        {
            var lag = StreamLagEstimator.EstimateControllerLag(
                candidate.Samples,
                capture.ControllerSamples,
                options.LagEstimation);
            if (lag.CorrelationScore < options.MinimumAcceptedCorrelation)
            {
                return Rejected(capture.Hand, candidate.TrackerSerial,
                    TrackerAssociationCandidateRejection.WeakCorrelation,
                    $"Motion correlation {lag.CorrelationScore:F4} is below the {options.MinimumAcceptedCorrelation:F4} association gate.",
                    lag);
            }

            return new TrackerAssociationCandidateScore(
                capture.Hand,
                candidate.TrackerSerial,
                TrackerAssociationCandidateRejection.None,
                "Accepted coordinate-invariant angular-speed correlation.",
                lag);
        }
        catch (LagEstimationException exception)
        {
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

    private static CandidateHealth EvaluateHealth(
        TrackerAssociationCandidate candidate,
        TrackerAssociationOptions options)
    {
        if (!candidate.IsConnected)
        {
            return new CandidateHealth(
                false,
                TrackerAssociationCandidateRejection.Disconnected,
                "tracker was disconnected");
        }

        var orientationValidity = candidate.Samples.Count == 0
            ? 0d
            : candidate.Samples.Count(sample => sample.HasValidOrientation) /
                (double)candidate.Samples.Count;
        if (orientationValidity < options.MinimumOrientationValidityFraction)
        {
            return new CandidateHealth(
                false,
                TrackerAssociationCandidateRejection.RepeatedlyInvalid,
                $"orientation validity {orientationValidity:P1} is below the {options.MinimumOrientationValidityFraction:P1} gate");
        }

        return new CandidateHealth(
            true,
            TrackerAssociationCandidateRejection.None,
            "tracker was connected with sufficient orientation validity");
    }

    private static string? FindPerHandAmbiguity(
        IReadOnlyList<TrackerAssociationCandidateScore> acceptedLeft,
        IReadOnlyList<TrackerAssociationCandidateScore> acceptedRight,
        double minimumMargin)
    {
        foreach (var (hand, scores) in new[]
                 {
                     (CalibrationHand.Left, acceptedLeft),
                     (CalibrationHand.Right, acceptedRight),
                 })
        {
            var ranked = scores
                .OrderByDescending(score => score.CorrelationScore)
                .ThenBy(score => score.TrackerSerial, StringComparer.Ordinal)
                .ToArray();
            if (ranked.Length < 2)
            {
                continue;
            }

            var margin = ranked[0].CorrelationScore - ranked[1].CorrelationScore;
            if (margin < minimumMargin)
            {
                return $"The {hand.ToString().ToLowerInvariant()} capture has similar motion on {ranked[0].TrackerSerial} and {ranked[1].TrackerSerial}; winner/runner-up correlation margin {margin:F4} is below the {minimumMargin:F4} gate.";
            }
        }

        return null;
    }

    private static TrackerCandidateOrderDiagnostic DiagnoseCandidateOrder(
        HandMotionCapture leftCapture,
        HandMotionCapture rightCapture,
        string leftSerial,
        string rightSerial)
    {
        var leftCaptureLeftFirst = IsLeftBeforeRight(
            leftCapture.TrackerCandidates,
            leftSerial,
            rightSerial);
        var rightCaptureLeftFirst = IsLeftBeforeRight(
            rightCapture.TrackerCandidates,
            leftSerial,
            rightSerial);
        if (leftCaptureLeftFirst != rightCaptureLeftFirst)
        {
            return TrackerCandidateOrderDiagnostic.InconsistentBetweenCaptures;
        }

        return leftCaptureLeftFirst
            ? TrackerCandidateOrderDiagnostic.ConsistentLeftThenRight
            : TrackerCandidateOrderDiagnostic.ConsistentRightThenLeft;
    }

    private static bool IsLeftBeforeRight(
        IReadOnlyList<TrackerAssociationCandidate> candidates,
        string leftSerial,
        string rightSerial)
    {
        var leftIndex = candidates
            .Select((candidate, index) => (candidate, index))
            .Single(item => string.Equals(
                item.candidate.TrackerSerial,
                leftSerial,
                StringComparison.Ordinal)).index;
        var rightIndex = candidates
            .Select((candidate, index) => (candidate, index))
            .Single(item => string.Equals(
                item.candidate.TrackerSerial,
                rightSerial,
                StringComparison.Ordinal)).index;
        return leftIndex < rightIndex;
    }

    private static TrackerAssociationCandidateRejection MapRejection(
        LagEstimationFailure failure) => failure switch
        {
            LagEstimationFailure.InsufficientMotion => TrackerAssociationCandidateRejection.InsufficientMotion,
            LagEstimationFailure.InsufficientOverlap => TrackerAssociationCandidateRejection.InsufficientOverlap,
            LagEstimationFailure.AmbiguousPeak or
            LagEstimationFailure.InsufficientComparisonEvidence or
            LagEstimationFailure.BoundaryPeak => TrackerAssociationCandidateRejection.AmbiguousLag,
            _ => TrackerAssociationCandidateRejection.WeakCorrelation,
        };

    private static TrackerAssociationStatus DiagnoseNoAssignment(
        IReadOnlyList<TrackerAssociationCandidateScore> scores)
    {
        if (scores.Any(score => score.Rejection is TrackerAssociationCandidateRejection.AmbiguousLag))
        {
            return TrackerAssociationStatus.Ambiguous;
        }

        if (scores.Any(score => score.Rejection is TrackerAssociationCandidateRejection.WeakCorrelation))
        {
            return TrackerAssociationStatus.WeakCorrelation;
        }

        return TrackerAssociationStatus.InvalidCandidate;
    }

    private static string BuildNoAssignmentReason(TrackerAssociationStatus status) => status switch
    {
        TrackerAssociationStatus.Ambiguous =>
            "No distinct two-hand assignment passed because one or more motion correlations had an ambiguous lag peak.",
        TrackerAssociationStatus.WeakCorrelation =>
            "No distinct two-hand assignment passed the motion-correlation gate.",
        _ =>
            "No distinct two-hand assignment passed connection, validity, motion, and overlap gates.",
    };

    private static TrackerAssociationCandidateScore Rejected(
        CalibrationHand hand,
        string trackerSerial,
        TrackerAssociationCandidateRejection rejection,
        string reason,
        LagEstimate? lag = null) =>
        new(hand, trackerSerial, rejection, reason, lag);

    private static TrackerAssociationResult Failure(
        TrackerAssociationStatus status,
        string reason,
        IReadOnlyList<TrackerAssociationCandidateScore> scores) =>
        new(status, reason, null, null, false, Array.AsReadOnly(scores.ToArray()));

    private sealed record CandidateHealth(
        bool IsHealthy,
        TrackerAssociationCandidateRejection Rejection,
        string Reason);

    private sealed record AssignmentCandidate(
        TrackerAssociationCandidateScore Left,
        TrackerAssociationCandidateScore Right,
        double Score);
}
