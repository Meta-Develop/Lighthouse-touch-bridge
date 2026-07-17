using Ltb.Core;

namespace Ltb.Calibration;

/// <summary>Selection and numeric options for one deterministic recording replay.</summary>
public sealed record RecordingReplayOptions
{
    public RecordingReplayOptions(
        string trackerStreamId,
        string controllerStreamId,
        CalibrationPolicy calibrationPolicy = CalibrationPolicy.Auto)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackerStreamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(controllerStreamId);
        TrackerStreamId = trackerStreamId;
        ControllerStreamId = controllerStreamId;
        CalibrationPolicy = calibrationPolicy;
    }

    public string TrackerStreamId { get; }

    public string ControllerStreamId { get; }

    public CalibrationPolicy CalibrationPolicy { get; }

    public LagEstimationOptions? LagEstimation { get; init; }

    public PoseStreamAlignmentOptions? Alignment { get; init; }

    public CalibrationOptions? Calibration { get; init; }
}

/// <summary>Lag, synchronized input, and Milestone 0 solver result from replay.</summary>
public sealed record RecordingReplayResult(
    LagEstimate Lag,
    IReadOnlyList<SynchronizedPosePair> AlignedPairs,
    CalibrationResult Calibration);

/// <summary>Replays two selected recording streams through time alignment and calibration.</summary>
public static class RecordingCalibrationReplay
{
    public static RecordingReplayResult Replay(
        PoseRecording recording,
        RecordingReplayOptions options)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.CalibrationPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(options.CalibrationPolicy));
        }

        var trackerStream = recording.GetStream(options.TrackerStreamId);
        var controllerStream = recording.GetStream(options.ControllerStreamId);
        if (trackerStream.Identity.SourceKind != PoseSourceKind.TrackedPose)
        {
            throw new ArgumentException(
                $"Stream '{trackerStream.Identity.StreamId}' is not a tracked-pose source.",
                nameof(options));
        }

        if (controllerStream.Identity.SourceKind != PoseSourceKind.InputController)
        {
            throw new ArgumentException(
                $"Stream '{controllerStream.Identity.StreamId}' is not an input-controller source.",
                nameof(options));
        }

        var trackerSamples = trackerStream.Samples.Select(ToReplaySample).ToArray();
        var controllerSamples = controllerStream.Samples.Select(ToReplaySample).ToArray();
        var lag = StreamLagEstimator.EstimateControllerLag(
            trackerSamples,
            controllerSamples,
            options.LagEstimation);
        var alignedPairs = PoseStreamAligner.AlignControllerToTracker(
            trackerSamples,
            controllerSamples,
            lag.LagSeconds,
            options.Alignment);
        var calibration = HandEyeCalibrationSolver.Solve(
            alignedPairs,
            options.CalibrationPolicy,
            options.Calibration);
        return new RecordingReplayResult(lag, alignedPairs, calibration);
    }

    private static TimestampedPoseSample ToReplaySample(RecordedPoseSample recorded)
    {
        var validity = recorded.Validity;
        if (!recorded.IsConnected)
        {
            validity &= ~PoseValidity.TrackingValid;
        }

        return new TimestampedPoseSample(
            recorded.MonotonicHostTimeSeconds,
            recorded.Pose,
            validity);
    }
}
