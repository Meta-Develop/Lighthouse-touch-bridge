using System.Numerics;
using Ltb.Core;

namespace Ltb.Calibration;

/// <summary>Configuration for bounded angular-speed lag estimation.</summary>
public sealed record LagEstimationOptions
{
    private const int MaximumHalfGridCandidateCount = 4096;

    public double MaximumAbsoluteLagSeconds { get; init; } = 0.100d;

    public double SearchStepSeconds { get; init; } = 0.001d;

    public double ResampleIntervalSeconds { get; init; } = 0.005d;

    /// <summary>
    /// Maximum interval from which one angular-speed observation may be
    /// derived. Larger source gaps are discontinuities, not slow motion.
    /// </summary>
    public double MaximumSourcePoseIntervalSeconds { get; init; } = 0.050d;

    public double MaximumAngularSpeedInterpolationGapSeconds { get; init; } = 0.050d;

    public int MinimumCorrelationSampleCount { get; init; } = 20;

    public double MinimumCorrelationScore { get; init; } = 0.75d;

    public double MinimumPeakProminence { get; init; } = 0.002d;

    public double PeakComparisonExclusionSeconds { get; init; } = 0.020d;

    public int MinimumRunnerUpCandidateCount { get; init; } = 2;

    public bool RequireInteriorPeak { get; init; } = true;

    public int MinimumBoundaryMarginSteps { get; init; } = 1;

    public int StrongestCoarseCandidateCount { get; init; } = 5;

    public double RotationCandidateSeparationSeconds { get; init; } = 0.004d;

    public int RotationRefinementSubdivisions { get; init; } = 8;

    public double UncertaintyCorrelationDrop { get; init; } = 0.01d;

    internal void Validate()
    {
        RequirePositive(MaximumAbsoluteLagSeconds, nameof(MaximumAbsoluteLagSeconds));
        RequirePositive(SearchStepSeconds, nameof(SearchStepSeconds));
        RequirePositive(ResampleIntervalSeconds, nameof(ResampleIntervalSeconds));
        RequirePositive(MaximumSourcePoseIntervalSeconds, nameof(MaximumSourcePoseIntervalSeconds));
        RequirePositive(
            MaximumAngularSpeedInterpolationGapSeconds,
            nameof(MaximumAngularSpeedInterpolationGapSeconds));
        if (SearchStepSeconds > MaximumAbsoluteLagSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SearchStepSeconds),
                "Search step cannot exceed the lag-search bound.");
        }

        if (Math.Ceiling(MaximumAbsoluteLagSeconds / SearchStepSeconds) >
            MaximumHalfGridCandidateCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SearchStepSeconds),
                $"Lag search may contain at most {(2 * MaximumHalfGridCandidateCount) + 1} coarse candidates.");
        }

        if (MinimumCorrelationSampleCount < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumCorrelationSampleCount));
        }

        RequireRange(MinimumCorrelationScore, -1d, 1d, nameof(MinimumCorrelationScore));
        RequireRange(MinimumPeakProminence, 0d, 2d, nameof(MinimumPeakProminence));
        RequirePositive(PeakComparisonExclusionSeconds, nameof(PeakComparisonExclusionSeconds));
        if (MinimumRunnerUpCandidateCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumRunnerUpCandidateCount));
        }

        if (MinimumBoundaryMarginSteps < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumBoundaryMarginSteps));
        }

        if (StrongestCoarseCandidateCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(StrongestCoarseCandidateCount));
        }

        RequirePositive(RotationCandidateSeparationSeconds, nameof(RotationCandidateSeparationSeconds));
        if (RotationRefinementSubdivisions < 2 || RotationRefinementSubdivisions > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(RotationRefinementSubdivisions));
        }

        RequireRange(UncertaintyCorrelationDrop, 0d, 2d, nameof(UncertaintyCorrelationDrop));
    }

    private static void RequirePositive(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0d)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void RequireRange(double value, double minimum, double maximum, string parameterName)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

/// <summary>Machine-readable reason that lag estimation was not accepted.</summary>
public enum LagEstimationFailure
{
    InsufficientMotion,
    InsufficientOverlap,
    WeakCorrelation,
    InsufficientComparisonEvidence,
    AmbiguousPeak,
    BoundaryPeak,
    RotationRefinementFailed,
}

/// <summary>Raised when a lag candidate fails an acceptance gate.</summary>
public sealed class LagEstimationException : InvalidOperationException
{
    public LagEstimationException(LagEstimationFailure reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    public LagEstimationFailure Reason { get; }
}

/// <summary>
/// Angular-speed lag estimate. <see cref="LagSeconds"/> is positive when the
/// controller host timestamps occur later than tracker timestamps for the same
/// physical motion. Equivalently, the specification's
/// <c>corr(touch(t), tracker(t + tau))</c> shift is <c>tau = -LagSeconds</c>.
/// </summary>
public sealed record LagEstimate(
    double LagSeconds,
    double CorrelationScore,
    double Confidence,
    int ComparedSampleCount,
    double SearchStepSeconds,
    double MaximumAbsoluteLagSeconds)
{
    public double CoarseLagSeconds { get; init; } = double.NaN;

    public double CoarseCorrelationScore { get; init; } = double.NaN;

    public double RunnerUpCorrelationScore { get; init; } = double.NaN;

    public double PeakProminence { get; init; } = double.NaN;

    public double CorrelationIntervalMinimumSeconds { get; init; } = double.NaN;

    public double CorrelationIntervalMaximumSeconds { get; init; } = double.NaN;

    public double CoarseRotationResidualDegrees { get; init; } = double.NaN;

    public double ProvisionalRotationLagSeconds { get; init; } = double.NaN;

    public double RefinedRotationResidualDegrees { get; init; } = double.NaN;

    public IReadOnlyList<double> EvaluatedCandidateLagsSeconds { get; init; } =
        Array.Empty<double>();
}

/// <summary>
/// Estimates controller-minus-tracker stream lag from normalized-correlation
/// of quaternion-derived angular-speed magnitudes.
/// </summary>
public static class StreamLagEstimator
{
    public static LagEstimate EstimateControllerLag(
        IReadOnlyList<TimestampedPoseSample> trackerSamples,
        IReadOnlyList<TimestampedPoseSample> controllerSamples,
        LagEstimationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(trackerSamples);
        ArgumentNullException.ThrowIfNull(controllerSamples);
        options ??= new LagEstimationOptions();
        options.Validate();

        ValidateStrictlyMonotonic(trackerSamples, nameof(trackerSamples));
        ValidateStrictlyMonotonic(controllerSamples, nameof(controllerSamples));
        var trackerSpeed = BuildAngularSpeedSeries(
            trackerSamples,
            options.MaximumSourcePoseIntervalSeconds);
        var controllerSpeed = BuildAngularSpeedSeries(
            controllerSamples,
            options.MaximumSourcePoseIntervalSeconds);
        if (trackerSpeed.Count < 2 || controllerSpeed.Count < 2)
        {
            throw new LagEstimationException(
                LagEstimationFailure.InsufficientMotion,
                "Lag estimation requires at least three orientation-valid samples in each stream without oversized source intervals.");
        }

        if (!HasVariation(trackerSpeed) || !HasVariation(controllerSpeed))
        {
            throw new LagEstimationException(
                LagEstimationFailure.InsufficientMotion,
                "Angular-speed magnitude does not vary enough to identify stream lag.");
        }

        var candidateLags = BuildSymmetricCandidateGrid(options);
        var candidates = candidateLags
            .Select(lag => ScoreLag(trackerSpeed, controllerSpeed, lag, options))
            .ToArray();
        var bestIndex = FindBestCandidateIndex(candidates);
        if (bestIndex < 0)
        {
            throw new LagEstimationException(
                LagEstimationFailure.InsufficientOverlap,
                "The streams do not have enough overlapping, varying angular-speed samples for lag estimation.");
        }

        var coarseBest = candidates[bestIndex];
        var boundaryMargin = Math.Min(
            options.MinimumBoundaryMarginSteps,
            Math.Max(0, (candidates.Length - 1) / 2));
        if (options.RequireInteriorPeak &&
            (bestIndex <= boundaryMargin || bestIndex >= candidates.Length - 1 - boundaryMargin))
        {
            throw new LagEstimationException(
                LagEstimationFailure.BoundaryPeak,
                $"Best correlation peak at {coarseBest.LagSeconds:R} s is clipped by the configured lag-search boundary.");
        }

        if (coarseBest.Correlation < options.MinimumCorrelationScore)
        {
            throw new LagEstimationException(
                LagEstimationFailure.WeakCorrelation,
                $"Best normalized correlation {coarseBest.Correlation:R} is below the configured minimum {options.MinimumCorrelationScore:R}.");
        }

        var runnerUpCandidates = candidates
            .Where(candidate =>
                candidate.IsValid &&
                Math.Abs(candidate.LagSeconds - coarseBest.LagSeconds) >=
                    options.PeakComparisonExclusionSeconds)
            .ToArray();
        if (runnerUpCandidates.Length < options.MinimumRunnerUpCandidateCount)
        {
            throw new LagEstimationException(
                LagEstimationFailure.InsufficientComparisonEvidence,
                $"Only {runnerUpCandidates.Length} valid runner-up lag candidates exist outside the peak exclusion region; {options.MinimumRunnerUpCandidateCount} are required.");
        }

        var runnerUp = runnerUpCandidates.Max(candidate => candidate.Correlation);
        var peakProminence = coarseBest.Correlation - runnerUp;
        if (peakProminence < options.MinimumPeakProminence)
        {
            throw new LagEstimationException(
                LagEstimationFailure.AmbiguousPeak,
                $"Correlation peak prominence {peakProminence:R} is below the configured minimum {options.MinimumPeakProminence:R}.");
        }

        var (intervalMinimum, intervalMaximum) = EstimateCorrelationInterval(
            candidates,
            bestIndex,
            coarseBest.Correlation - options.UncertaintyCorrelationDrop);
        var strongestCandidates = SelectStrongestCandidates(candidates, options);
        var coarseRotationScores = strongestCandidates
            .Select(candidate => EvaluateRotationResidual(
                trackerSamples,
                controllerSamples,
                candidate.LagSeconds,
                options))
            .Where(score => score.IsValid)
            .ToArray();
        if (coarseRotationScores.Length == 0)
        {
            throw new LagEstimationException(
                LagEstimationFailure.RotationRefinementFailed,
                "None of the strongest correlation candidates produced an accepted provisional rotation-only calibration.");
        }

        var coarseRotationBest = coarseRotationScores
            .OrderBy(score => score.ResidualDegrees)
            .ThenBy(score => Math.Abs(score.LagSeconds - coarseBest.LagSeconds))
            .ThenBy(score => score.LagSeconds)
            .First();
        var refinedRotationBest = RefineWithRotationResidual(
            trackerSamples,
            controllerSamples,
            coarseRotationBest,
            options);
        var finalCorrelation = ScoreLag(
            trackerSpeed,
            controllerSpeed,
            refinedRotationBest.LagSeconds,
            options);
        if (!finalCorrelation.IsValid)
        {
            throw new LagEstimationException(
                LagEstimationFailure.RotationRefinementFailed,
                "The rotation-refined lag does not retain enough samples for correlation diagnostics.");
        }

        if (finalCorrelation.Correlation < options.MinimumCorrelationScore)
        {
            throw new LagEstimationException(
                LagEstimationFailure.RotationRefinementFailed,
                $"Rotation-refined lag correlation {finalCorrelation.Correlation:R} fell below the configured minimum {options.MinimumCorrelationScore:R}.");
        }

        var scoreEvidence = Math.Clamp(
            (coarseBest.Correlation - options.MinimumCorrelationScore) /
            Math.Max(1e-12d, 1d - options.MinimumCorrelationScore),
            0d,
            1d);
        var prominenceEvidence = Math.Clamp(
            peakProminence / Math.Max(0.01d, 4d * options.MinimumPeakProminence),
            0d,
            1d);
        var confidence = Math.Sqrt(scoreEvidence * prominenceEvidence);

        return new LagEstimate(
            refinedRotationBest.LagSeconds,
            finalCorrelation.Correlation,
            confidence,
            finalCorrelation.SampleCount,
            options.SearchStepSeconds,
            options.MaximumAbsoluteLagSeconds)
        {
            CoarseLagSeconds = coarseBest.LagSeconds,
            CoarseCorrelationScore = coarseBest.Correlation,
            RunnerUpCorrelationScore = runnerUp,
            PeakProminence = peakProminence,
            CorrelationIntervalMinimumSeconds = intervalMinimum,
            CorrelationIntervalMaximumSeconds = intervalMaximum,
            CoarseRotationResidualDegrees = coarseRotationBest.ResidualDegrees,
            ProvisionalRotationLagSeconds = coarseRotationBest.LagSeconds,
            RefinedRotationResidualDegrees = refinedRotationBest.ResidualDegrees,
            EvaluatedCandidateLagsSeconds = Array.AsReadOnly(candidateLags),
        };
    }

    private static double[] BuildSymmetricCandidateGrid(LagEstimationOptions options)
    {
        var positive = new List<double> { 0d };
        var fullStepCount = (int)Math.Floor(
            options.MaximumAbsoluteLagSeconds / options.SearchStepSeconds);
        for (var index = 1; index <= fullStepCount; index++)
        {
            var value = index * options.SearchStepSeconds;
            if (value < options.MaximumAbsoluteLagSeconds ||
                NearlyEqual(value, options.MaximumAbsoluteLagSeconds))
            {
                positive.Add(Math.Min(value, options.MaximumAbsoluteLagSeconds));
            }
        }

        if (!NearlyEqual(positive[^1], options.MaximumAbsoluteLagSeconds))
        {
            positive.Add(options.MaximumAbsoluteLagSeconds);
        }

        return positive
            .Skip(1)
            .Select(value => -value)
            .Reverse()
            .Concat(positive)
            .ToArray();
    }

    private static int FindBestCandidateIndex(IReadOnlyList<CandidateScore> candidates)
    {
        var bestIndex = -1;
        for (var index = 0; index < candidates.Count; index++)
        {
            if (candidates[index].IsValid &&
                (bestIndex < 0 || candidates[index].Correlation > candidates[bestIndex].Correlation))
            {
                bestIndex = index;
            }
        }

        return bestIndex;
    }

    private static bool HasVariation(IReadOnlyList<AngularSpeedPoint> points)
    {
        var minimum = points.Min(point => point.Value);
        var maximum = points.Max(point => point.Value);
        return double.IsFinite(minimum) && double.IsFinite(maximum) && maximum - minimum > 1e-6d;
    }

    private static (double Minimum, double Maximum) EstimateCorrelationInterval(
        IReadOnlyList<CandidateScore> candidates,
        int bestIndex,
        double minimumCorrelation)
    {
        var lower = bestIndex;
        while (lower > 0 &&
            candidates[lower - 1].IsValid &&
            candidates[lower - 1].Correlation >= minimumCorrelation)
        {
            lower--;
        }

        var upper = bestIndex;
        while (upper < candidates.Count - 1 &&
            candidates[upper + 1].IsValid &&
            candidates[upper + 1].Correlation >= minimumCorrelation)
        {
            upper++;
        }

        return (candidates[lower].LagSeconds, candidates[upper].LagSeconds);
    }

    private static IReadOnlyList<CandidateScore> SelectStrongestCandidates(
        IReadOnlyList<CandidateScore> candidates,
        LagEstimationOptions options)
    {
        var selected = new List<CandidateScore>(options.StrongestCoarseCandidateCount);
        foreach (var candidate in candidates
                     .Where(candidate =>
                         candidate.IsValid &&
                         candidate.Correlation >= options.MinimumCorrelationScore)
                     .OrderByDescending(candidate => candidate.Correlation)
                     .ThenBy(candidate => Math.Abs(candidate.LagSeconds))
                     .ThenBy(candidate => candidate.LagSeconds))
        {
            if (selected.All(existing =>
                    Math.Abs(existing.LagSeconds - candidate.LagSeconds) >=
                        options.RotationCandidateSeparationSeconds))
            {
                selected.Add(candidate);
            }

            if (selected.Count >= options.StrongestCoarseCandidateCount)
            {
                break;
            }
        }

        return selected;
    }

    private static RotationResidualScore RefineWithRotationResidual(
        IReadOnlyList<TimestampedPoseSample> trackerSamples,
        IReadOnlyList<TimestampedPoseSample> controllerSamples,
        RotationResidualScore coarseBest,
        LagEstimationOptions options)
    {
        var fineStep = options.SearchStepSeconds / options.RotationRefinementSubdivisions;
        var evaluated = new List<RotationResidualScore>();
        for (var index = -options.RotationRefinementSubdivisions;
             index <= options.RotationRefinementSubdivisions;
             index++)
        {
            var lag = coarseBest.LagSeconds + (index * fineStep);
            if (lag <= -options.MaximumAbsoluteLagSeconds ||
                lag >= options.MaximumAbsoluteLagSeconds)
            {
                continue;
            }

            var score = EvaluateRotationResidual(
                trackerSamples,
                controllerSamples,
                lag,
                options);
            if (score.IsValid)
            {
                evaluated.Add(score);
            }
        }

        if (evaluated.Count == 0)
        {
            return coarseBest;
        }

        var best = evaluated
            .OrderBy(score => score.ResidualDegrees)
            .ThenBy(score => Math.Abs(score.LagSeconds - coarseBest.LagSeconds))
            .ThenBy(score => score.LagSeconds)
            .First();
        var previous = evaluated.FirstOrDefault(score =>
            NearlyEqual(score.LagSeconds, best.LagSeconds - fineStep));
        var next = evaluated.FirstOrDefault(score =>
            NearlyEqual(score.LagSeconds, best.LagSeconds + fineStep));
        if (!previous.IsValid || !next.IsValid)
        {
            return best;
        }

        var denominator = previous.ResidualDegrees -
            (2d * best.ResidualDegrees) + next.ResidualDegrees;
        if (!double.IsFinite(denominator) || denominator <= 1e-12d)
        {
            return best;
        }

        var fractionalStep = Math.Clamp(
            0.5d * (previous.ResidualDegrees - next.ResidualDegrees) / denominator,
            -1d,
            1d);
        var parabolicLag = best.LagSeconds + (fractionalStep * fineStep);
        if (parabolicLag <= -options.MaximumAbsoluteLagSeconds ||
            parabolicLag >= options.MaximumAbsoluteLagSeconds)
        {
            return best;
        }

        var parabolic = EvaluateRotationResidual(
            trackerSamples,
            controllerSamples,
            parabolicLag,
            options);
        return parabolic.IsValid && parabolic.ResidualDegrees < best.ResidualDegrees
            ? parabolic
            : best;
    }

    private static RotationResidualScore EvaluateRotationResidual(
        IReadOnlyList<TimestampedPoseSample> trackerSamples,
        IReadOnlyList<TimestampedPoseSample> controllerSamples,
        double lagSeconds,
        LagEstimationOptions options)
    {
        var aligned = PoseStreamAligner.AlignControllerToTracker(
            trackerSamples,
            controllerSamples,
            lagSeconds,
            new PoseStreamAlignmentOptions
            {
                MaximumInterpolationGapSeconds =
                    options.MaximumAngularSpeedInterpolationGapSeconds,
            });
        if (aligned.Count < 8)
        {
            return RotationResidualScore.Invalid(lagSeconds);
        }

        var result = HandEyeCalibrationSolver.Solve(
            aligned,
            CalibrationPolicy.RotationOnly);
        return result.Success && double.IsFinite(result.Quality.RotationRmsDegrees)
            ? new RotationResidualScore(
                lagSeconds,
                result.Quality.RotationRmsDegrees,
                aligned.Count,
                true)
            : RotationResidualScore.Invalid(lagSeconds);
    }

    internal static IReadOnlyList<TimestampedPoseSample> NormalizeQuaternionSigns(
        IReadOnlyList<TimestampedPoseSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ValidateStrictlyMonotonic(samples, nameof(samples));
        var normalized = new TimestampedPoseSample[samples.Count];
        Quaternion? previous = null;
        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            var rotation = sample.Pose.Rotation;
            var negate = previous is null
                ? rotation.W < 0f
                : Quaternion.Dot(previous.Value, rotation) < 0f;
            if (negate)
            {
                rotation = Negate(rotation);
            }

            normalized[index] = new TimestampedPoseSample(
                sample.MonotonicTimeSeconds,
                new RigidTransform(rotation, sample.Pose.TranslationMeters),
                sample.Validity);
            previous = rotation;
        }

        return Array.AsReadOnly(normalized);
    }

    private static IReadOnlyList<AngularSpeedPoint> BuildAngularSpeedSeries(
        IReadOnlyList<TimestampedPoseSample> samples,
        double maximumSourceIntervalSeconds)
    {
        var normalized = NormalizeQuaternionSigns(samples);
        var result = new List<AngularSpeedPoint>(Math.Max(0, normalized.Count - 1));
        for (var index = 1; index < normalized.Count; index++)
        {
            var previous = normalized[index - 1];
            var current = normalized[index];
            if (!previous.HasValidOrientation || !current.HasValidOrientation)
            {
                continue;
            }

            var deltaTime = current.MonotonicTimeSeconds - previous.MonotonicTimeSeconds;
            if (deltaTime > maximumSourceIntervalSeconds)
            {
                continue;
            }

            var dot = Math.Clamp(
                Math.Abs((double)Quaternion.Dot(previous.Pose.Rotation, current.Pose.Rotation)),
                0d,
                1d);
            var angleRadians = 2d * Math.Acos(dot);
            result.Add(new AngularSpeedPoint(
                previous.MonotonicTimeSeconds + (0.5d * deltaTime),
                angleRadians / deltaTime));
        }

        return result;
    }

    private static CandidateScore ScoreLag(
        IReadOnlyList<AngularSpeedPoint> tracker,
        IReadOnlyList<AngularSpeedPoint> controller,
        double controllerLagSeconds,
        LagEstimationOptions options)
    {
        // Controller samples are evaluated at raw host time t + lag so their
        // corrected physical time is t.
        var start = Math.Max(tracker[0].TimeSeconds, controller[0].TimeSeconds - controllerLagSeconds);
        var end = Math.Min(tracker[^1].TimeSeconds, controller[^1].TimeSeconds - controllerLagSeconds);
        if (end <= start)
        {
            return CandidateScore.Invalid(controllerLagSeconds);
        }

        var trackerValues = new List<double>();
        var controllerValues = new List<double>();
        for (var time = start; time <= end + (options.ResampleIntervalSeconds * 0.25d);
             time += options.ResampleIntervalSeconds)
        {
            if (TryInterpolateScalar(
                    tracker,
                    time,
                    options.MaximumAngularSpeedInterpolationGapSeconds,
                    out var trackerValue) &&
                TryInterpolateScalar(
                    controller,
                    time + controllerLagSeconds,
                    options.MaximumAngularSpeedInterpolationGapSeconds,
                    out var controllerValue))
            {
                trackerValues.Add(trackerValue);
                controllerValues.Add(controllerValue);
            }
        }

        if (trackerValues.Count < options.MinimumCorrelationSampleCount)
        {
            return CandidateScore.Invalid(controllerLagSeconds);
        }

        var trackerMean = trackerValues.Average();
        var controllerMean = controllerValues.Average();
        var numerator = 0d;
        var trackerEnergy = 0d;
        var controllerEnergy = 0d;
        for (var index = 0; index < trackerValues.Count; index++)
        {
            var trackerCentered = trackerValues[index] - trackerMean;
            var controllerCentered = controllerValues[index] - controllerMean;
            numerator += trackerCentered * controllerCentered;
            trackerEnergy += trackerCentered * trackerCentered;
            controllerEnergy += controllerCentered * controllerCentered;
        }

        var denominator = Math.Sqrt(trackerEnergy * controllerEnergy);
        if (!double.IsFinite(denominator) || denominator <= 1e-15d)
        {
            return CandidateScore.Invalid(controllerLagSeconds);
        }

        return new CandidateScore(
            controllerLagSeconds,
            Math.Clamp(numerator / denominator, -1d, 1d),
            trackerValues.Count,
            true);
    }

    private static bool TryInterpolateScalar(
        IReadOnlyList<AngularSpeedPoint> points,
        double timeSeconds,
        double maximumGapSeconds,
        out double value)
    {
        if (timeSeconds < points[0].TimeSeconds || timeSeconds > points[^1].TimeSeconds)
        {
            value = default;
            return false;
        }

        var low = 0;
        var high = points.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var comparison = points[middle].TimeSeconds.CompareTo(timeSeconds);
            if (comparison == 0)
            {
                value = points[middle].Value;
                return true;
            }

            if (comparison < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        var upperIndex = low;
        var lowerIndex = upperIndex - 1;
        if (lowerIndex < 0 || upperIndex >= points.Count)
        {
            value = default;
            return false;
        }

        var lower = points[lowerIndex];
        var upper = points[upperIndex];
        var interval = upper.TimeSeconds - lower.TimeSeconds;
        if (interval > maximumGapSeconds)
        {
            value = default;
            return false;
        }

        var fraction = (timeSeconds - lower.TimeSeconds) / interval;
        value = lower.Value + (fraction * (upper.Value - lower.Value));
        return true;
    }

    internal static void ValidateStrictlyMonotonic(
        IReadOnlyList<TimestampedPoseSample> samples,
        string parameterName)
    {
        var previous = double.NegativeInfinity;
        for (var index = 0; index < samples.Count; index++)
        {
            var timestamp = samples[index].MonotonicTimeSeconds;
            if (timestamp <= previous)
            {
                throw new ArgumentException(
                    $"Pose stream timestamps must increase strictly; index {index} is not monotonic.",
                    parameterName);
            }

            previous = timestamp;
        }
    }

    private static Quaternion Negate(Quaternion value) =>
        new(-value.X, -value.Y, -value.Z, -value.W);

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) <= 1e-12d * Math.Max(1d, Math.Max(Math.Abs(left), Math.Abs(right)));

    private readonly record struct AngularSpeedPoint(double TimeSeconds, double Value);

    private readonly record struct CandidateScore(
        double LagSeconds,
        double Correlation,
        int SampleCount,
        bool IsValid)
    {
        public static CandidateScore Invalid(double lagSeconds) =>
            new(lagSeconds, double.NegativeInfinity, 0, false);
    }

    private readonly record struct RotationResidualScore(
        double LagSeconds,
        double ResidualDegrees,
        int SampleCount,
        bool IsValid)
    {
        public static RotationResidualScore Invalid(double lagSeconds) =>
            new(lagSeconds, double.PositiveInfinity, 0, false);
    }
}

/// <summary>Interpolation limits for producing synchronized solver inputs.</summary>
public sealed record PoseStreamAlignmentOptions
{
    public double MaximumInterpolationGapSeconds { get; init; } = 0.050d;

    internal void Validate()
    {
        if (!double.IsFinite(MaximumInterpolationGapSeconds) || MaximumInterpolationGapSeconds <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumInterpolationGapSeconds));
        }
    }
}

/// <summary>
/// Aligns raw streams after lag correction. Orientation uses shortest-path
/// SLERP after quaternion sign normalization; position uses linear interpolation.
/// </summary>
public static class PoseStreamAligner
{
    public static IReadOnlyList<SynchronizedPosePair> AlignControllerToTracker(
        IReadOnlyList<TimestampedPoseSample> trackerSamples,
        IReadOnlyList<TimestampedPoseSample> controllerSamples,
        double controllerLagSeconds,
        PoseStreamAlignmentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(trackerSamples);
        ArgumentNullException.ThrowIfNull(controllerSamples);
        if (!double.IsFinite(controllerLagSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(controllerLagSeconds));
        }

        options ??= new PoseStreamAlignmentOptions();
        options.Validate();
        StreamLagEstimator.ValidateStrictlyMonotonic(trackerSamples, nameof(trackerSamples));
        StreamLagEstimator.ValidateStrictlyMonotonic(controllerSamples, nameof(controllerSamples));
        if (trackerSamples.Count == 0 || controllerSamples.Count == 0)
        {
            return Array.Empty<SynchronizedPosePair>();
        }

        var tracker = StreamLagEstimator.NormalizeQuaternionSigns(trackerSamples);
        var controller = StreamLagEstimator.NormalizeQuaternionSigns(controllerSamples);
        var aligned = new List<SynchronizedPosePair>(tracker.Count);
        foreach (var trackerSample in tracker)
        {
            var controllerRawTime = trackerSample.MonotonicTimeSeconds + controllerLagSeconds;
            if (!TryInterpolatePose(
                    controller,
                    controllerRawTime,
                    trackerSample.MonotonicTimeSeconds,
                    options.MaximumInterpolationGapSeconds,
                    out var controllerSample))
            {
                continue;
            }

            aligned.Add(new SynchronizedPosePair(trackerSample, controllerSample));
        }

        return aligned.AsReadOnly();
    }

    private static bool TryInterpolatePose(
        IReadOnlyList<TimestampedPoseSample> samples,
        double rawQueryTimeSeconds,
        double alignedTimeSeconds,
        double maximumGapSeconds,
        out TimestampedPoseSample result)
    {
        if (rawQueryTimeSeconds < samples[0].MonotonicTimeSeconds ||
            rawQueryTimeSeconds > samples[^1].MonotonicTimeSeconds)
        {
            result = default;
            return false;
        }

        var low = 0;
        var high = samples.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var comparison = samples[middle].MonotonicTimeSeconds.CompareTo(rawQueryTimeSeconds);
            if (comparison == 0)
            {
                var exact = samples[middle];
                result = new TimestampedPoseSample(alignedTimeSeconds, exact.Pose, exact.Validity);
                return true;
            }

            if (comparison < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        var upperIndex = low;
        var lowerIndex = upperIndex - 1;
        if (lowerIndex < 0 || upperIndex >= samples.Count)
        {
            result = default;
            return false;
        }

        var lower = samples[lowerIndex];
        var upper = samples[upperIndex];
        var interval = upper.MonotonicTimeSeconds - lower.MonotonicTimeSeconds;
        if (interval > maximumGapSeconds)
        {
            result = default;
            return false;
        }

        var fraction = (float)((rawQueryTimeSeconds - lower.MonotonicTimeSeconds) / interval);
        var upperRotation = upper.Pose.Rotation;
        if (Quaternion.Dot(lower.Pose.Rotation, upperRotation) < 0f)
        {
            upperRotation = new Quaternion(
                -upperRotation.X,
                -upperRotation.Y,
                -upperRotation.Z,
                -upperRotation.W);
        }

        var rotation = Quaternion.Normalize(Quaternion.Slerp(
            lower.Pose.Rotation,
            upperRotation,
            fraction));
        var translation = Vector3.Lerp(
            lower.Pose.TranslationMeters,
            upper.Pose.TranslationMeters,
            fraction);
        var validity = lower.Validity & upper.Validity;
        result = new TimestampedPoseSample(
            alignedTimeSeconds,
            new RigidTransform(rotation, translation),
            validity);
        return true;
    }
}
