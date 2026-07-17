using System.Numerics;
using Ltb.Core;

namespace Ltb.Calibration;

/// <summary>
/// Deterministic staged hand-eye solver for <c>A * X = X * B</c>. Rotation is
/// solved independently before translation so invalid controller positions can
/// never corrupt an accepted mount orientation.
/// </summary>
public static class HandEyeCalibrationSolver
{
    private const double DegreesPerRadian = 180d / Math.PI;
    private const double JacobiTolerance = 1e-13d;
    private const double RotationResidualResolutionDegrees = 0.05d;

    /// <summary>
    /// Solves a tracker-to-controller mount transform from already synchronized
    /// pose pairs. Stream interpolation and lag estimation are intentionally
    /// outside this Milestone 0 API.
    /// </summary>
    public static CalibrationResult Solve(
        IReadOnlyList<SynchronizedPosePair> synchronizedPairs,
        CalibrationPolicy requestedPolicy,
        CalibrationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(synchronizedPairs);
        if (!Enum.IsDefined(requestedPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedPolicy));
        }

        options ??= new CalibrationOptions();
        options.Validate();

        var sampleCount = synchronizedPairs.Count;
        var rotationValidIndices = new List<int>(sampleCount);
        var positionValidCount = 0;
        var previousTime = double.NegativeInfinity;
        for (var index = 0; index < sampleCount; index++)
        {
            var pair = synchronizedPairs[index];
            if (Math.Abs(pair.TimeDeltaSeconds) > options.MaximumTimestampDifferenceSeconds ||
                !double.IsFinite(pair.MonotonicTimeSeconds) ||
                pair.MonotonicTimeSeconds <= previousTime)
            {
                return CreateEarlyFailure(
                    requestedPolicy,
                    "Input pairs are not strictly monotonic and timestamp-matched; lag estimation belongs to Milestone 1.",
                    CalibrationDegeneracy.TimestampMismatch,
                    sampleCount,
                    rotationValidIndices.Count,
                    positionValidCount);
            }

            previousTime = pair.MonotonicTimeSeconds;
            if (pair.Tracker.HasValidOrientation && pair.Controller.HasValidOrientation)
            {
                rotationValidIndices.Add(index);
                if (pair.Tracker.HasValidPosition && pair.Controller.HasValidPosition)
                {
                    positionValidCount++;
                }
            }
        }

        if (sampleCount < options.MinimumSampleCount)
        {
            return CreateEarlyFailure(
                requestedPolicy,
                $"At least {options.MinimumSampleCount} synchronized samples are required.",
                CalibrationDegeneracy.InsufficientSamples,
                sampleCount,
                rotationValidIndices.Count,
                positionValidCount);
        }

        if (rotationValidIndices.Count < options.MinimumSampleCount)
        {
            return CreateEarlyFailure(
                requestedPolicy,
                "Too few samples contain tracked orientations in both streams.",
                CalibrationDegeneracy.MissingOrientation,
                sampleCount,
                rotationValidIndices.Count,
                positionValidCount);
        }

        SplitSampleIndices(
            rotationValidIndices,
            options.ValidationFraction,
            out var solveSampleIndices,
            out var validationSampleIndices);
        var solveMotionLimit = Math.Max(
            3,
            (int)Math.Round(options.MaximumMotionPairs * (1d - options.ValidationFraction)));
        var validationMotionLimit = Math.Max(3, options.MaximumMotionPairs - solveMotionLimit);
        var solveMotions = BuildBalancedMotions(
            synchronizedPairs,
            solveSampleIndices,
            solveMotionLimit,
            options.MinimumRelativeRotationDegrees,
            options.MaximumRelativeRotationDegrees);
        var validationMotions = BuildBalancedMotions(
            synchronizedPairs,
            validationSampleIndices,
            validationMotionLimit,
            options.MinimumRelativeRotationDegrees,
            options.MaximumRelativeRotationDegrees);

        if (solveMotions.Count < 3 || validationMotions.Count < 3)
        {
            var totalUsableMotionCount = solveMotions.Count + validationMotions.Count;
            var degeneracy = totalUsableMotionCount < 3
                ? CalibrationDegeneracy.StaticMotion
                : CalibrationDegeneracy.InsufficientSamples;
            return CreateRotationFailure(
                requestedPolicy,
                "Capture does not contain enough usable relative rotations in disjoint solve and validation sample sets.",
                degeneracy,
                sampleCount,
                rotationValidIndices.Count,
                positionValidCount,
                0d,
                solveMotions.Count,
                validationMotions.Count);
        }

        var axisCoverage = ComputeAxisCoverage(solveMotions);
        if (axisCoverage < options.MinimumRotationAxisCoverage)
        {
            return CreateRotationFailure(
                requestedPolicy,
                "Relative rotations do not cover at least two sufficiently independent axes.",
                CalibrationDegeneracy.SingleAxisRotation,
                sampleCount,
                rotationValidIndices.Count,
                positionValidCount,
                axisCoverage,
                solveMotions.Count,
                validationMotions.Count);
        }

        var rotationSolveMotions = TrimRotationOutliers(solveMotions);
        if (!TrySolveRotation(rotationSolveMotions, out var mountRotation))
        {
            return CreateRotationFailure(
                requestedPolicy,
                "The quaternion hand-eye normal matrix did not yield a finite mount rotation.",
                CalibrationDegeneracy.RotationQualityRejected,
                sampleCount,
                rotationValidIndices.Count,
                positionValidCount,
                axisCoverage,
                solveMotions.Count,
                validationMotions.Count);
        }

        // A second robust pass uses residuals from the first closed-form estimate.
        rotationSolveMotions = TrimRotationOutliers(solveMotions, mountRotation);
        if (!TrySolveRotation(rotationSolveMotions, out mountRotation))
        {
            return CreateRotationFailure(
                requestedPolicy,
                "Robust rotation inliers did not yield a finite mount rotation.",
                CalibrationDegeneracy.RotationQualityRejected,
                sampleCount,
                rotationValidIndices.Count,
                positionValidCount,
                axisCoverage,
                solveMotions.Count,
                validationMotions.Count);
        }

        axisCoverage = ComputeAxisCoverage(rotationSolveMotions);
        if (axisCoverage < options.MinimumRotationAxisCoverage)
        {
            return CreateRotationFailure(
                requestedPolicy,
                "Robust inlier motions collapse to a single rotational axis.",
                CalibrationDegeneracy.SingleAxisRotation,
                sampleCount,
                rotationValidIndices.Count,
                positionValidCount,
                axisCoverage,
                solveMotions.Count,
                validationMotions.Count);
        }

        var rotationResiduals = validationMotions
            .Select(motion => RotationResidualDegrees(motion, mountRotation))
            .ToArray();
        var rotationInlierIndices = SelectRobustResidualIndices(
            rotationResiduals,
            RotationResidualResolutionDegrees);
        var rotationInlierRatio = rotationInlierIndices.Count / (double)rotationResiduals.Length;
        var rotationRms = RootMeanSquare(rotationResiduals, rotationInlierIndices);
        var rotationPercentile = Percentile(rotationResiduals, rotationInlierIndices, options.ResidualPercentile);
        if (!double.IsFinite(rotationRms) ||
            rotationRms > options.MaximumRotationRmsDegrees ||
            rotationInlierRatio < options.MinimumValidationInlierRatio)
        {
            return new CalibrationResult(
                requestedPolicy,
                CalibrationModel.Failed,
                $"Rotation held-out validation was rejected: RMS {rotationRms:F4} deg; inlier ratio {rotationInlierRatio:P1}.",
                new RigidTransform(mountRotation, Vector3.Zero),
                new CalibrationQualityMetrics(rotationRms, rotationPercentile, null, null, null, 0d)
                {
                    RotationInlierRatio = rotationInlierRatio,
                },
                new MotionObservability(
                    false,
                    false,
                    CalibrationDegeneracy.RotationQualityRejected,
                    CalibrationDegeneracy.NotRequested,
                    axisCoverage,
                    double.NaN),
                sampleCount,
                rotationValidIndices.Count,
                positionValidCount,
                solveMotions.Count,
                validationMotions.Count);
        }

        if (requestedPolicy is CalibrationPolicy.RotationOnly)
        {
            return CreateRotationOnlyResult(
                requestedPolicy,
                "Rotation-only was explicitly requested; mount translation is fixed to zero.",
                mountRotation,
                rotationRms,
                rotationPercentile,
                rotationInlierRatio,
                CalibrationDegeneracy.NotRequested,
                axisCoverage,
                double.NaN,
                sampleCount,
                rotationValidIndices.Count,
                positionValidCount,
                solveMotions.Count,
                validationMotions.Count);
        }

        var positionFraction = positionValidCount / (double)rotationValidIndices.Count;
        if (positionFraction < options.MinimumPositionSampleFraction)
        {
            return TranslationFallbackOrFailure(
                requestedPolicy,
                $"Only {positionFraction:P1} of orientation-valid samples have valid positions; {options.MinimumPositionSampleFraction:P1} is required.",
                CalibrationDegeneracy.MissingPosition,
                mountRotation,
                rotationRms,
                rotationPercentile,
                rotationInlierRatio,
                axisCoverage,
                double.NaN,
                sampleCount,
                rotationValidIndices.Count,
                positionValidCount,
                solveMotions.Count,
                validationMotions.Count);
        }

        var translationSolveMotions = rotationSolveMotions.Where(IsPositionUsable).ToList();
        var translationValidationMotions = validationMotions.Where(IsPositionUsable).ToList();
        if (translationSolveMotions.Count < 3 || translationValidationMotions.Count < 3)
        {
            return TranslationFallbackOrFailure(
                requestedPolicy,
                "Too few independent position-valid relative motions remain for solve and held-out validation.",
                CalibrationDegeneracy.TranslationUnobservable,
                mountRotation,
                rotationRms,
                rotationPercentile,
                rotationInlierRatio,
                axisCoverage,
                double.NaN,
                sampleCount,
                rotationValidIndices.Count,
                positionValidCount,
                solveMotions.Count,
                validationMotions.Count);
        }

        var translationAttempt = SolveTranslationRobust(translationSolveMotions, mountRotation, options);
        if (!translationAttempt.Success)
        {
            return TranslationFallbackOrFailure(
                requestedPolicy,
                translationAttempt.Reason,
                translationAttempt.Degeneracy,
                mountRotation,
                rotationRms,
                rotationPercentile,
                rotationInlierRatio,
                axisCoverage,
                translationAttempt.ConditionNumber,
                sampleCount,
                rotationValidIndices.Count,
                positionValidCount,
                solveMotions.Count,
                validationMotions.Count);
        }

        SplitSampleIndices(
            solveSampleIndices,
            0.5d,
            out var translationSubsetSampleIndicesA,
            out var translationSubsetSampleIndicesB);
        var translationSubsetMotionLimit = Math.Max(3, options.MaximumMotionPairs / 2);
        var translationSubsetMotionsA = BuildBalancedMotions(
                synchronizedPairs,
                translationSubsetSampleIndicesA,
                translationSubsetMotionLimit,
                options.MinimumRelativeRotationDegrees,
                options.MaximumRelativeRotationDegrees)
            .Where(IsPositionUsable)
            .ToList();
        var translationSubsetMotionsB = BuildBalancedMotions(
                synchronizedPairs,
                translationSubsetSampleIndicesB,
                translationSubsetMotionLimit,
                options.MinimumRelativeRotationDegrees,
                options.MaximumRelativeRotationDegrees)
            .Where(IsPositionUsable)
            .ToList();
        var translationSubsetAttemptA = SolveTranslationRobust(
            translationSubsetMotionsA,
            mountRotation,
            options);
        var translationSubsetAttemptB = SolveTranslationRobust(
            translationSubsetMotionsB,
            mountRotation,
            options);
        if (!translationSubsetAttemptA.Success || !translationSubsetAttemptB.Success)
        {
            return TranslationFallbackOrFailure(
                requestedPolicy,
                "Independent translation subsets were not both observable and well-conditioned.",
                CalibrationDegeneracy.TranslationUnstable,
                mountRotation,
                rotationRms,
                rotationPercentile,
                rotationInlierRatio,
                axisCoverage,
                translationAttempt.ConditionNumber,
                sampleCount,
                rotationValidIndices.Count,
                positionValidCount,
                solveMotions.Count,
                validationMotions.Count);
        }

        var translationSplitDisagreementMillimeters = Vector3.Distance(
            translationSubsetAttemptA.Translation,
            translationSubsetAttemptB.Translation) * 1000d;
        if (translationSplitDisagreementMillimeters >
            options.MaximumTranslationSplitDisagreementMillimeters)
        {
            return TranslationFallbackOrFailure(
                requestedPolicy,
                $"Independent translation estimates disagree by {translationSplitDisagreementMillimeters:F3} mm, exceeding the {options.MaximumTranslationSplitDisagreementMillimeters:F3} mm stability gate.",
                CalibrationDegeneracy.TranslationUnstable,
                mountRotation,
                rotationRms,
                rotationPercentile,
                rotationInlierRatio,
                axisCoverage,
                translationAttempt.ConditionNumber,
                sampleCount,
                rotationValidIndices.Count,
                positionValidCount,
                solveMotions.Count,
                validationMotions.Count,
                translationSplitDisagreementMillimeters: translationSplitDisagreementMillimeters);
        }

        var mountTranslation = translationAttempt.Translation;
        var translationMagnitude = mountTranslation.Length();
        if (!float.IsFinite(translationMagnitude) ||
            translationMagnitude > options.MaximumTranslationMagnitudeMeters)
        {
            return TranslationFallbackOrFailure(
                requestedPolicy,
                $"Estimated mount translation {translationMagnitude:F4} m exceeds the {options.MaximumTranslationMagnitudeMeters:F4} m plausibility gate.",
                CalibrationDegeneracy.ImplausibleTranslation,
                mountRotation,
                rotationRms,
                rotationPercentile,
                rotationInlierRatio,
                axisCoverage,
                translationAttempt.ConditionNumber,
                sampleCount,
                rotationValidIndices.Count,
                positionValidCount,
                solveMotions.Count,
                validationMotions.Count,
                translationSplitDisagreementMillimeters: translationSplitDisagreementMillimeters);
        }

        var fullPositionResiduals = translationValidationMotions
            .Select(motion => PositionResidualMeters(motion, mountRotation, mountTranslation))
            .ToArray();
        var rotationOnlyPositionResiduals = translationValidationMotions
            .Select(motion => PositionResidualMeters(motion, mountRotation, Vector3.Zero))
            .ToArray();
        var positionInlierIndices = SelectRobustResidualIndices(fullPositionResiduals);
        var translationInlierRatio = positionInlierIndices.Count / (double)fullPositionResiduals.Length;
        var fullPositionRmsMillimeters =
            RootMeanSquare(fullPositionResiduals, positionInlierIndices) * 1000d;
        var fullPositionPercentileMillimeters =
            Percentile(fullPositionResiduals, positionInlierIndices, options.ResidualPercentile) * 1000d;
        var rotationOnlyPositionRmsMillimeters =
            RootMeanSquare(rotationOnlyPositionResiduals, positionInlierIndices) * 1000d;
        var positionImprovementMillimeters =
            rotationOnlyPositionRmsMillimeters - fullPositionRmsMillimeters;
        var positionImprovementFraction = rotationOnlyPositionRmsMillimeters > 1e-9d
            ? positionImprovementMillimeters / rotationOnlyPositionRmsMillimeters
            : 0d;

        if (!double.IsFinite(fullPositionRmsMillimeters) ||
            fullPositionRmsMillimeters > options.MaximumPositionRmsMillimeters ||
            translationInlierRatio < options.MinimumValidationInlierRatio ||
            positionImprovementMillimeters < options.MinimumPositionImprovementMillimeters ||
            positionImprovementFraction < options.MinimumPositionImprovementFraction)
        {
            var reason =
                $"Full translation failed held-out quality gates: RMS {fullPositionRmsMillimeters:F3} mm; " +
                $"inliers {translationInlierRatio:P1}; rotation-only improvement {positionImprovementMillimeters:F3} mm ({positionImprovementFraction:P1}).";
            return TranslationFallbackOrFailure(
                requestedPolicy,
                reason,
                CalibrationDegeneracy.PositionQualityRejected,
                mountRotation,
                rotationRms,
                rotationPercentile,
                rotationInlierRatio,
                axisCoverage,
                translationAttempt.ConditionNumber,
                sampleCount,
                rotationValidIndices.Count,
                positionValidCount,
                solveMotions.Count,
                validationMotions.Count,
                fullPositionRmsMillimeters,
                fullPositionPercentileMillimeters,
                rotationOnlyPositionRmsMillimeters,
                translationMagnitude,
                translationInlierRatio,
                translationSplitDisagreementMillimeters);
        }

        return new CalibrationResult(
            requestedPolicy,
            CalibrationModel.FullSixDof,
            $"Translation observable; held-out position RMS improved by {positionImprovementMillimeters:F3} mm ({positionImprovementFraction:P1}).",
            new RigidTransform(mountRotation, mountTranslation),
            new CalibrationQualityMetrics(
                rotationRms,
                rotationPercentile,
                fullPositionRmsMillimeters,
                fullPositionPercentileMillimeters,
                rotationOnlyPositionRmsMillimeters,
                translationMagnitude)
            {
                RotationInlierRatio = rotationInlierRatio,
                TranslationInlierRatio = translationInlierRatio,
                TranslationSplitDisagreementMillimeters = translationSplitDisagreementMillimeters,
            },
            new MotionObservability(
                true,
                true,
                CalibrationDegeneracy.None,
                CalibrationDegeneracy.None,
                axisCoverage,
                translationAttempt.ConditionNumber),
            sampleCount,
            rotationValidIndices.Count,
            positionValidCount,
            solveMotions.Count,
            validationMotions.Count);
    }

    private static CalibrationResult TranslationFallbackOrFailure(
        CalibrationPolicy requestedPolicy,
        string reason,
        CalibrationDegeneracy translationDegeneracy,
        Quaternion mountRotation,
        double rotationRms,
        double rotationPercentile,
        double rotationInlierRatio,
        double axisCoverage,
        double translationCondition,
        int sampleCount,
        int rotationValidSampleCount,
        int positionValidSampleCount,
        int motionPairCount,
        int validationMotionPairCount,
        double? positionRmsMillimeters = null,
        double? positionPercentileMillimeters = null,
        double? rotationOnlyPositionRmsMillimeters = null,
        double translationMagnitudeMeters = 0d,
        double translationInlierRatio = double.NaN,
        double translationSplitDisagreementMillimeters = double.NaN)
    {
        if (requestedPolicy is CalibrationPolicy.Auto)
        {
            return new CalibrationResult(
                requestedPolicy,
                CalibrationModel.RotationOnly,
                $"Auto selected rotation-only: {reason}",
                new RigidTransform(mountRotation, Vector3.Zero),
                new CalibrationQualityMetrics(
                    rotationRms,
                    rotationPercentile,
                    positionRmsMillimeters,
                    positionPercentileMillimeters,
                    rotationOnlyPositionRmsMillimeters,
                    0d)
                {
                    RotationInlierRatio = rotationInlierRatio,
                    TranslationInlierRatio = translationInlierRatio,
                    TranslationSplitDisagreementMillimeters = translationSplitDisagreementMillimeters,
                },
                new MotionObservability(
                    true,
                    false,
                    CalibrationDegeneracy.None,
                    translationDegeneracy,
                    axisCoverage,
                    translationCondition),
                sampleCount,
                rotationValidSampleCount,
                positionValidSampleCount,
                motionPairCount,
                validationMotionPairCount);
        }

        return new CalibrationResult(
            requestedPolicy,
            CalibrationModel.Failed,
            $"Full 6DoF was required but translation was rejected: {reason}",
            new RigidTransform(mountRotation, Vector3.Zero),
            new CalibrationQualityMetrics(
                rotationRms,
                rotationPercentile,
                positionRmsMillimeters,
                positionPercentileMillimeters,
                rotationOnlyPositionRmsMillimeters,
                translationMagnitudeMeters)
            {
                RotationInlierRatio = rotationInlierRatio,
                TranslationInlierRatio = translationInlierRatio,
                TranslationSplitDisagreementMillimeters = translationSplitDisagreementMillimeters,
            },
            new MotionObservability(
                true,
                false,
                CalibrationDegeneracy.None,
                translationDegeneracy,
                axisCoverage,
                translationCondition),
            sampleCount,
            rotationValidSampleCount,
            positionValidSampleCount,
            motionPairCount,
            validationMotionPairCount);
    }

    private static CalibrationResult CreateRotationOnlyResult(
        CalibrationPolicy requestedPolicy,
        string reason,
        Quaternion mountRotation,
        double rotationRms,
        double rotationPercentile,
        double rotationInlierRatio,
        CalibrationDegeneracy translationDegeneracy,
        double axisCoverage,
        double translationCondition,
        int sampleCount,
        int rotationValidSampleCount,
        int positionValidSampleCount,
        int motionPairCount,
        int validationMotionPairCount) =>
        new(
            requestedPolicy,
            CalibrationModel.RotationOnly,
            reason,
            new RigidTransform(mountRotation, Vector3.Zero),
            new CalibrationQualityMetrics(rotationRms, rotationPercentile, null, null, null, 0d)
            {
                RotationInlierRatio = rotationInlierRatio,
            },
            new MotionObservability(
                true,
                false,
                CalibrationDegeneracy.None,
                translationDegeneracy,
                axisCoverage,
                translationCondition),
            sampleCount,
            rotationValidSampleCount,
            positionValidSampleCount,
            motionPairCount,
            validationMotionPairCount);

    private static CalibrationResult CreateEarlyFailure(
        CalibrationPolicy requestedPolicy,
        string reason,
        CalibrationDegeneracy degeneracy,
        int sampleCount,
        int rotationValidSampleCount,
        int positionValidSampleCount) =>
        CreateRotationFailure(
            requestedPolicy,
            reason,
            degeneracy,
            sampleCount,
            rotationValidSampleCount,
            positionValidSampleCount,
            0d,
            0,
            0);

    private static CalibrationResult CreateRotationFailure(
        CalibrationPolicy requestedPolicy,
        string reason,
        CalibrationDegeneracy degeneracy,
        int sampleCount,
        int rotationValidSampleCount,
        int positionValidSampleCount,
        double axisCoverage,
        int motionPairCount,
        int validationMotionPairCount) =>
        new(
            requestedPolicy,
            CalibrationModel.Failed,
            reason,
            RigidTransform.Identity,
            new CalibrationQualityMetrics(double.NaN, double.NaN, null, null, null, 0d),
            new MotionObservability(
                false,
                false,
                degeneracy,
                CalibrationDegeneracy.NotRequested,
                axisCoverage,
                double.NaN),
            sampleCount,
            rotationValidSampleCount,
            positionValidSampleCount,
            motionPairCount,
            validationMotionPairCount);

    private static List<RelativeMotion> BuildBalancedMotions(
        IReadOnlyList<SynchronizedPosePair> pairs,
        IReadOnlyList<int> validIndices,
        int maximumMotionPairs,
        double minimumRotationDegrees,
        double maximumRotationDegrees)
    {
        var maximumAnchorCount = Math.Max(
            3,
            (int)Math.Floor((1d + Math.Sqrt(1d + (8d * maximumMotionPairs))) / 2d));
        var anchorCount = Math.Min(validIndices.Count, maximumAnchorCount);
        var anchors = SelectEvenlySpacedIndices(validIndices, anchorCount);
        var motions = new List<RelativeMotion>(Math.Min(maximumMotionPairs, anchorCount * (anchorCount - 1) / 2));

        // Enumerating by span distributes the retained motions over both local
        // and recording-wide baselines instead of exhausting all pairs at one end.
        for (var span = 1; span < anchors.Count && motions.Count < maximumMotionPairs; span++)
        {
            for (var start = 0; start + span < anchors.Count && motions.Count < maximumMotionPairs; start++)
            {
                var firstIndex = anchors[start];
                var secondIndex = anchors[start + span];
                var first = pairs[firstIndex];
                var second = pairs[secondIndex];
                var trackerMotion = first.Tracker.Pose.Inverse() * second.Tracker.Pose;
                var controllerMotion = first.Controller.Pose.Inverse() * second.Controller.Pose;
                var trackerAngle = RotationAngleDegrees(trackerMotion.Rotation);
                var controllerAngle = RotationAngleDegrees(controllerMotion.Rotation);
                if (trackerAngle < minimumRotationDegrees || controllerAngle < minimumRotationDegrees ||
                    trackerAngle > maximumRotationDegrees || controllerAngle > maximumRotationDegrees)
                {
                    continue;
                }

                motions.Add(new RelativeMotion(
                    CanonicalizeTransformRotation(trackerMotion),
                    CanonicalizeTransformRotation(controllerMotion),
                    first.Tracker.HasValidPosition && first.Controller.HasValidPosition &&
                    second.Tracker.HasValidPosition && second.Controller.HasValidPosition));
            }
        }

        return motions;
    }

    private static List<int> SelectEvenlySpacedIndices(IReadOnlyList<int> source, int count)
    {
        if (count <= 0)
        {
            return [];
        }

        if (count >= source.Count)
        {
            return source.ToList();
        }

        if (count == 1)
        {
            return [source[source.Count / 2]];
        }

        var result = new List<int>(count);
        var lastPosition = -1;
        for (var index = 0; index < count; index++)
        {
            var position = (int)Math.Round(index * (source.Count - 1d) / (count - 1d));
            if (position == lastPosition)
            {
                position++;
            }

            result.Add(source[position]);
            lastPosition = position;
        }

        return result;
    }

    private static void SplitSampleIndices(
        IReadOnlyList<int> sampleIndices,
        double validationFraction,
        out List<int> solve,
        out List<int> validation)
    {
        if (sampleIndices.Count < 2)
        {
            solve = sampleIndices.ToList();
            validation = [];
            return;
        }

        var requestedValidationCount = (int)Math.Round(sampleIndices.Count * validationFraction);
        var validationCount = sampleIndices.Count >= 6
            ? Math.Clamp(requestedValidationCount, 3, sampleIndices.Count - 3)
            : Math.Clamp(requestedValidationCount, 1, sampleIndices.Count - 1);
        validation = SelectEvenlySpacedIndices(sampleIndices, validationCount);
        var validationSet = validation.ToHashSet();
        solve = sampleIndices.Where(index => !validationSet.Contains(index)).ToList();
        if (solve.Count == 0)
        {
            throw new InvalidOperationException("Sample split produced no solve samples.");
        }
    }

    private static bool TrySolveRotation(IReadOnlyList<RelativeMotion> motions, out Quaternion rotation)
    {
        rotation = Quaternion.Identity;
        if (motions.Count < 3)
        {
            return false;
        }

        var normal = new double[4, 4];
        foreach (var motion in motions)
        {
            var left = LeftQuaternionMatrix(motion.TrackerMotion.Rotation);
            var right = RightQuaternionMatrix(motion.ControllerMotion.Rotation);
            for (var row = 0; row < 4; row++)
            {
                for (var column = 0; column < 4; column++)
                {
                    var coefficient = left[row, column] - right[row, column];
                    for (var other = 0; other < 4; other++)
                    {
                        normal[column, other] +=
                            coefficient * (left[row, other] - right[row, other]);
                    }
                }
            }
        }

        var decomposition = EigenDecomposeSymmetric(normal);
        var minimumIndex = IndexOfMinimum(decomposition.Values);
        var candidate = new Quaternion(
            (float)decomposition.Vectors[0, minimumIndex],
            (float)decomposition.Vectors[1, minimumIndex],
            (float)decomposition.Vectors[2, minimumIndex],
            (float)decomposition.Vectors[3, minimumIndex]);
        if (!IsFinite(candidate) || candidate.LengthSquared() < 1e-12f)
        {
            return false;
        }

        rotation = Canonicalize(Quaternion.Normalize(candidate));
        return true;
    }

    private static List<RelativeMotion> TrimRotationOutliers(
        IReadOnlyList<RelativeMotion> motions,
        Quaternion? estimate = null)
    {
        if (motions.Count < 5 || estimate is null)
        {
            return motions.ToList();
        }

        var residuals = motions.Select(motion => RotationResidualDegrees(motion, estimate.Value)).ToArray();
        var indices = SelectRobustResidualIndices(
            residuals,
            RotationResidualResolutionDegrees);
        var minimumInliers = Math.Max(3, (int)Math.Ceiling(motions.Count * 0.7d));
        return indices.Count >= minimumInliers
            ? indices.Select(index => motions[index]).ToList()
            : motions.ToList();
    }

    private static TranslationAttempt SolveTranslationRobust(
        IReadOnlyList<RelativeMotion> motions,
        Quaternion mountRotation,
        CalibrationOptions options)
    {
        var attempt = SolveTranslation(motions, mountRotation, options);
        if (!attempt.Success || motions.Count < 5)
        {
            return attempt;
        }

        var residuals = motions
            .Select(motion => PositionResidualMeters(motion, mountRotation, attempt.Translation))
            .ToArray();
        var indices = SelectRobustResidualIndices(residuals);
        var minimumInliers = Math.Max(3, (int)Math.Ceiling(motions.Count * 0.7d));
        if (indices.Count == motions.Count || indices.Count < minimumInliers)
        {
            return attempt;
        }

        var inliers = indices.Select(index => motions[index]).ToList();
        return SolveTranslation(inliers, mountRotation, options);
    }

    private static TranslationAttempt SolveTranslation(
        IReadOnlyList<RelativeMotion> motions,
        Quaternion mountRotation,
        CalibrationOptions options)
    {
        if (motions.Count < 3)
        {
            return TranslationAttempt.Failed(
                CalibrationDegeneracy.TranslationUnobservable,
                "Translation requires at least three position-valid relative motions.");
        }

        var normal = new double[3, 3];
        var rightHandSide = new double[3];
        foreach (var motion in motions)
        {
            var matrix = RotationMinusIdentity(motion.TrackerMotion.Rotation);
            var targetVector = Vector3.Transform(
                motion.ControllerMotion.TranslationMeters,
                mountRotation) - motion.TrackerMotion.TranslationMeters;
            var target = new[] { (double)targetVector.X, targetVector.Y, targetVector.Z };

            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 3; column++)
                {
                    rightHandSide[column] += matrix[row, column] * target[row];
                    for (var other = 0; other < 3; other++)
                    {
                        normal[column, other] += matrix[row, column] * matrix[row, other];
                    }
                }
            }
        }

        var inverseMotionCount = 1d / motions.Count;
        for (var row = 0; row < 3; row++)
        {
            rightHandSide[row] *= inverseMotionCount;
            for (var column = 0; column < 3; column++)
            {
                normal[row, column] *= inverseMotionCount;
            }
        }

        var decomposition = EigenDecomposeSymmetric(normal);
        var sortedEigenvalues = decomposition.Values.OrderBy(value => value).ToArray();
        var minimumEigenvalue = sortedEigenvalues[0];
        var maximumEigenvalue = sortedEigenvalues[^1];
        if (!double.IsFinite(minimumEigenvalue) || minimumEigenvalue < options.MinimumTranslationEigenvalue)
        {
            return TranslationAttempt.Failed(
                CalibrationDegeneracy.TranslationUnobservable,
                $"Translation normal matrix has insufficient rank (minimum eigenvalue {minimumEigenvalue:E3}).");
        }

        var condition = maximumEigenvalue / minimumEigenvalue;
        if (!double.IsFinite(condition) || condition > options.MaximumTranslationConditionNumber)
        {
            return TranslationAttempt.Failed(
                CalibrationDegeneracy.TranslationIllConditioned,
                $"Translation normal-matrix condition {condition:F3} exceeds the {options.MaximumTranslationConditionNumber:F3} gate.",
                condition);
        }

        var solution = new double[3];
        for (var eigenIndex = 0; eigenIndex < 3; eigenIndex++)
        {
            var eigenvalue = decomposition.Values[eigenIndex];
            var projection = 0d;
            for (var row = 0; row < 3; row++)
            {
                projection += decomposition.Vectors[row, eigenIndex] * rightHandSide[row];
            }

            var scale = projection / eigenvalue;
            for (var row = 0; row < 3; row++)
            {
                solution[row] += decomposition.Vectors[row, eigenIndex] * scale;
            }
        }

        if (solution.Any(value => !double.IsFinite(value)))
        {
            return TranslationAttempt.Failed(
                CalibrationDegeneracy.TranslationUnobservable,
                "Translation solve produced a non-finite value.",
                condition);
        }

        return new TranslationAttempt(
            true,
            new Vector3((float)solution[0], (float)solution[1], (float)solution[2]),
            condition,
            CalibrationDegeneracy.None,
            string.Empty);
    }

    private static double ComputeAxisCoverage(IReadOnlyList<RelativeMotion> motions)
    {
        return MotionAxisCoverage.Compute(
            motions.Select(motion => RotationAxis(motion.TrackerMotion.Rotation)));
    }

    private static double RotationResidualDegrees(RelativeMotion motion, Quaternion mountRotation)
    {
        var left = Quaternion.Normalize(motion.TrackerMotion.Rotation * mountRotation);
        var right = Quaternion.Normalize(mountRotation * motion.ControllerMotion.Rotation);
        return QuaternionDistanceDegrees(left, right);
    }

    private static double PositionResidualMeters(
        RelativeMotion motion,
        Quaternion mountRotation,
        Vector3 mountTranslation)
    {
        var matrix = RotationMinusIdentity(motion.TrackerMotion.Rotation);
        var target = Vector3.Transform(
            motion.ControllerMotion.TranslationMeters,
            mountRotation) - motion.TrackerMotion.TranslationMeters;
        var predicted = new Vector3(
            (float)((matrix[0, 0] * mountTranslation.X) + (matrix[0, 1] * mountTranslation.Y) + (matrix[0, 2] * mountTranslation.Z)),
            (float)((matrix[1, 0] * mountTranslation.X) + (matrix[1, 1] * mountTranslation.Y) + (matrix[1, 2] * mountTranslation.Z)),
            (float)((matrix[2, 0] * mountTranslation.X) + (matrix[2, 1] * mountTranslation.Y) + (matrix[2, 2] * mountTranslation.Z)));
        return Vector3.Distance(predicted, target);
    }

    private static bool IsPositionUsable(RelativeMotion motion) => motion.HasValidPosition;

    private static double[,] RotationMinusIdentity(Quaternion rotation)
    {
        var x = Vector3.Transform(Vector3.UnitX, rotation);
        var y = Vector3.Transform(Vector3.UnitY, rotation);
        var z = Vector3.Transform(Vector3.UnitZ, rotation);
        return new[,]
        {
            { (double)x.X - 1d, y.X, z.X },
            { x.Y, (double)y.Y - 1d, z.Y },
            { x.Z, y.Z, (double)z.Z - 1d },
        };
    }

    private static double[,] LeftQuaternionMatrix(Quaternion quaternion)
    {
        var q = Canonicalize(quaternion);
        return new[,]
        {
            { (double)q.W, -q.Z, q.Y, q.X },
            { (double)q.Z, q.W, -q.X, q.Y },
            { (double)-q.Y, q.X, q.W, q.Z },
            { (double)-q.X, -q.Y, -q.Z, q.W },
        };
    }

    private static double[,] RightQuaternionMatrix(Quaternion quaternion)
    {
        var q = Canonicalize(quaternion);
        return new[,]
        {
            { (double)q.W, q.Z, -q.Y, q.X },
            { (double)-q.Z, q.W, q.X, q.Y },
            { (double)q.Y, -q.X, q.W, q.Z },
            { (double)-q.X, -q.Y, -q.Z, q.W },
        };
    }

    private static SymmetricEigenDecomposition EigenDecomposeSymmetric(double[,] source)
    {
        var size = source.GetLength(0);
        if (size != source.GetLength(1))
        {
            throw new ArgumentException("Matrix must be square.", nameof(source));
        }

        var matrix = (double[,])source.Clone();
        var vectors = new double[size, size];
        for (var index = 0; index < size; index++)
        {
            vectors[index, index] = 1d;
        }

        for (var iteration = 0; iteration < 100 * size * size; iteration++)
        {
            var maximum = 0d;
            var p = 0;
            var q = 1;
            for (var row = 0; row < size; row++)
            {
                for (var column = row + 1; column < size; column++)
                {
                    var magnitude = Math.Abs(matrix[row, column]);
                    if (magnitude > maximum)
                    {
                        maximum = magnitude;
                        p = row;
                        q = column;
                    }
                }
            }

            if (maximum < JacobiTolerance)
            {
                break;
            }

            var app = matrix[p, p];
            var aqq = matrix[q, q];
            var apq = matrix[p, q];
            var angle = 0.5d * Math.Atan2(2d * apq, aqq - app);
            var cosine = Math.Cos(angle);
            var sine = Math.Sin(angle);

            for (var index = 0; index < size; index++)
            {
                if (index == p || index == q)
                {
                    continue;
                }

                var aip = matrix[index, p];
                var aiq = matrix[index, q];
                matrix[index, p] = matrix[p, index] = (cosine * aip) - (sine * aiq);
                matrix[index, q] = matrix[q, index] = (sine * aip) + (cosine * aiq);
            }

            matrix[p, p] =
                (cosine * cosine * app) -
                (2d * sine * cosine * apq) +
                (sine * sine * aqq);
            matrix[q, q] =
                (sine * sine * app) +
                (2d * sine * cosine * apq) +
                (cosine * cosine * aqq);
            matrix[p, q] = matrix[q, p] = 0d;

            for (var row = 0; row < size; row++)
            {
                var vip = vectors[row, p];
                var viq = vectors[row, q];
                vectors[row, p] = (cosine * vip) - (sine * viq);
                vectors[row, q] = (sine * vip) + (cosine * viq);
            }
        }

        var values = new double[size];
        for (var index = 0; index < size; index++)
        {
            values[index] = matrix[index, index];
        }

        return new SymmetricEigenDecomposition(values, vectors);
    }

    private static IReadOnlyList<int> SelectRobustResidualIndices(
        IReadOnlyList<double> residuals,
        double absoluteFloor = 1e-6d)
    {
        if (residuals.Count < 5)
        {
            return Enumerable.Range(0, residuals.Count).ToArray();
        }

        var median = Median(residuals);
        var deviations = residuals.Select(value => Math.Abs(value - median)).ToArray();
        var sigma = 1.4826d * Median(deviations);
        var floor = Math.Max(absoluteFloor, median * 0.05d);
        var threshold = median + (4d * Math.Max(sigma, floor));
        var indices = Enumerable.Range(0, residuals.Count)
            .Where(index => double.IsFinite(residuals[index]) && residuals[index] <= threshold)
            .ToArray();
        return indices;
    }

    private static double RootMeanSquare(IReadOnlyList<double> values, IReadOnlyList<int> indices)
    {
        if (indices.Count == 0)
        {
            return double.NaN;
        }

        var sumSquares = 0d;
        foreach (var index in indices)
        {
            sumSquares += values[index] * values[index];
        }

        return Math.Sqrt(sumSquares / indices.Count);
    }

    private static double Percentile(
        IReadOnlyList<double> values,
        IReadOnlyList<int> indices,
        double percentile)
    {
        if (indices.Count == 0)
        {
            return double.NaN;
        }

        var sorted = indices.Select(index => values[index]).OrderBy(value => value).ToArray();
        var position = percentile * (sorted.Length - 1d);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sorted[lower];
        }

        var fraction = position - lower;
        return sorted[lower] + (fraction * (sorted[upper] - sorted[lower]));
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) * 0.5d
            : sorted[middle];
    }

    private static int IndexOfMinimum(IReadOnlyList<double> values)
    {
        var minimumIndex = 0;
        for (var index = 1; index < values.Count; index++)
        {
            if (values[index] < values[minimumIndex])
            {
                minimumIndex = index;
            }
        }

        return minimumIndex;
    }

    private static Vector3 RotationAxis(Quaternion quaternion)
    {
        var q = Canonicalize(quaternion);
        var sineHalfAngle = Math.Sqrt(Math.Max(0d, 1d - (q.W * q.W)));
        if (sineHalfAngle < 1e-9d)
        {
            return Vector3.UnitX;
        }

        return Vector3.Normalize(new Vector3(
            (float)(q.X / sineHalfAngle),
            (float)(q.Y / sineHalfAngle),
            (float)(q.Z / sineHalfAngle)));
    }

    private static double RotationAngleDegrees(Quaternion quaternion)
    {
        var q = Canonicalize(quaternion);
        return 2d * Math.Acos(Math.Clamp(q.W, -1f, 1f)) * DegreesPerRadian;
    }

    private static double QuaternionDistanceDegrees(Quaternion first, Quaternion second)
    {
        var dot = Math.Abs(Quaternion.Dot(first, second));
        return 2d * Math.Acos(Math.Clamp(dot, 0d, 1d)) * DegreesPerRadian;
    }

    private static RigidTransform CanonicalizeTransformRotation(RigidTransform transform) =>
        new(Canonicalize(transform.Rotation), transform.TranslationMeters);

    private static Quaternion Canonicalize(Quaternion quaternion)
    {
        var normalized = Quaternion.Normalize(quaternion);
        var shouldNegate = normalized.W < 0f ||
            (normalized.W == 0f && normalized.X < 0f) ||
            (normalized.W == 0f && normalized.X == 0f && normalized.Y < 0f) ||
            (normalized.W == 0f && normalized.X == 0f && normalized.Y == 0f && normalized.Z < 0f);
        return shouldNegate
            ? new Quaternion(-normalized.X, -normalized.Y, -normalized.Z, -normalized.W)
            : normalized;
    }

    private static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);

    private readonly record struct RelativeMotion(
        RigidTransform TrackerMotion,
        RigidTransform ControllerMotion,
        bool HasValidPosition);

    private readonly record struct SymmetricEigenDecomposition(double[] Values, double[,] Vectors);

    private readonly record struct TranslationAttempt(
        bool Success,
        Vector3 Translation,
        double ConditionNumber,
        CalibrationDegeneracy Degeneracy,
        string Reason)
    {
        public static TranslationAttempt Failed(
            CalibrationDegeneracy degeneracy,
            string reason,
            double conditionNumber = double.NaN) =>
            new(false, Vector3.Zero, conditionNumber, degeneracy, reason);
    }
}
