using Ltb.Core;

namespace Ltb.Calibration;

/// <summary>Portable raw streams and stable identity for one hand solve.</summary>
public sealed record HandCalibrationInput
{
    public HandCalibrationInput(
        CalibrationHand hand,
        string trackerSerial,
        IReadOnlyList<TimestampedPoseSample> trackerSamples,
        IReadOnlyList<TimestampedPoseSample> controllerSamples)
    {
        if (!Enum.IsDefined(hand))
        {
            throw new ArgumentOutOfRangeException(nameof(hand));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(trackerSerial);
        ArgumentNullException.ThrowIfNull(trackerSamples);
        ArgumentNullException.ThrowIfNull(controllerSamples);
        Hand = hand;
        TrackerSerial = trackerSerial;
        TrackerSamples = Array.AsReadOnly(trackerSamples.ToArray());
        ControllerSamples = Array.AsReadOnly(controllerSamples.ToArray());
    }

    public CalibrationHand Hand { get; }

    public string TrackerSerial { get; }

    public IReadOnlyList<TimestampedPoseSample> TrackerSamples { get; }

    public IReadOnlyList<TimestampedPoseSample> ControllerSamples { get; }
}

/// <summary>Options passed through to existing lag, alignment, and solver stages.</summary>
public sealed record HandCalibrationPipelineOptions
{
    public CalibrationPolicy CalibrationPolicy { get; init; } = CalibrationPolicy.Auto;

    public LagEstimationOptions? LagEstimation { get; init; }

    public PoseStreamAlignmentOptions? Alignment { get; init; }

    public CalibrationOptions? Calibration { get; init; }

    public MotionCoverageOptions? Coverage { get; init; }

    internal void Validate()
    {
        if (!Enum.IsDefined(CalibrationPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(CalibrationPolicy));
        }

        LagEstimation?.Validate();
        Alignment?.Validate();
        Calibration?.Validate();
        Coverage?.Validate();
    }
}

public enum HandCalibrationFailure
{
    None,
    InvalidCapture,
    TimeAlignmentRejected,
    CalibrationRejected,
}

/// <summary>
/// Staged per-hand outcome. Expected data-quality failures are returned rather
/// than thrown so a wizard can display the reason and request another capture.
/// </summary>
public sealed record HandCalibrationResult(
    CalibrationHand Hand,
    string TrackerSerial,
    HandCalibrationFailure Failure,
    string Reason,
    MotionCoverageResult Coverage,
    LagEstimate? Lag,
    IReadOnlyList<SynchronizedPosePair> AlignedPairs,
    CalibrationResult? Calibration)
{
    public bool Success => Failure is HandCalibrationFailure.None && Calibration?.Success is true;

    public CalibrationModel SelectedModel =>
        Calibration?.SelectedModel ?? CalibrationModel.Failed;

    public string SelectionReason => Calibration?.SelectionReason ?? Reason;

    public CalibrationQualityMetrics? Quality => Calibration?.Quality;

    public CalibrationReport? QualityReport =>
        Calibration is null ? null : new CalibrationReport(Calibration);
}

/// <summary>
/// Reuses the Milestone 1 lag/alignment path and Milestone 0 solver/model gates
/// for one serial-keyed hand capture.
/// </summary>
public static class PerHandCalibrationPipeline
{
    public static HandCalibrationResult Run(
        HandCalibrationInput input,
        HandCalibrationPipelineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        options ??= new HandCalibrationPipelineOptions();
        options.Validate();

        MotionCoverageResult coverage;
        try
        {
            coverage = MotionCoverageAnalyzer.Evaluate(
                input.ControllerSamples,
                options.Coverage);
        }
        catch (ArgumentException exception)
        {
            return InvalidCapture(
                input,
                CreateUnavailableCoverage(input.ControllerSamples),
                null,
                exception);
        }

        LagEstimate lag;
        try
        {
            lag = StreamLagEstimator.EstimateControllerLag(
                input.TrackerSamples,
                input.ControllerSamples,
                options.LagEstimation);
        }
        catch (LagEstimationException exception)
        {
            return new HandCalibrationResult(
                input.Hand,
                input.TrackerSerial,
                HandCalibrationFailure.TimeAlignmentRejected,
                $"Time alignment rejected ({exception.Reason}): {exception.Message}",
                coverage,
                null,
                Array.Empty<SynchronizedPosePair>(),
                null);
        }
        catch (ArgumentException exception)
        {
            return InvalidCapture(input, coverage, null, exception);
        }

        IReadOnlyList<SynchronizedPosePair> alignedPairs;
        try
        {
            alignedPairs = PoseStreamAligner.AlignControllerToTracker(
                input.TrackerSamples,
                input.ControllerSamples,
                lag.LagSeconds,
                options.Alignment);
        }
        catch (ArgumentException exception)
        {
            return InvalidCapture(input, coverage, lag, exception);
        }

        var calibration = HandEyeCalibrationSolver.Solve(
            alignedPairs,
            options.CalibrationPolicy,
            options.Calibration);
        var failure = calibration.Success
            ? HandCalibrationFailure.None
            : HandCalibrationFailure.CalibrationRejected;
        return new HandCalibrationResult(
            input.Hand,
            input.TrackerSerial,
            failure,
            calibration.SelectionReason,
            coverage,
            lag,
            alignedPairs,
            calibration);
    }

    private static HandCalibrationResult InvalidCapture(
        HandCalibrationInput input,
        MotionCoverageResult coverage,
        LagEstimate? lag,
        ArgumentException exception) =>
        new(
            input.Hand,
            input.TrackerSerial,
            HandCalibrationFailure.InvalidCapture,
            $"Invalid capture data: {exception.Message}",
            coverage,
            lag,
            Array.Empty<SynchronizedPosePair>(),
            null);

    private static MotionCoverageResult CreateUnavailableCoverage(
        IReadOnlyList<TimestampedPoseSample> samples)
    {
        if (samples.Count == 0)
        {
            return new MotionCoverageResult(0, 0, 0d, 0d, 0d, 0d, 0d, 0d, 0d, false, false);
        }

        return new MotionCoverageResult(
            samples.Count,
            0,
            samples.Count(sample => sample.IsTrackingValid) / (double)samples.Count,
            samples.Count(sample => sample.HasValidOrientation) / (double)samples.Count,
            samples.Count(sample => sample.HasValidPosition) / (double)samples.Count,
            0d,
            0d,
            0d,
            0d,
            false,
            false);
    }
}
