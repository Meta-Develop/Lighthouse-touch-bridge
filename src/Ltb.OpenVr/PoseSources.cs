using Ltb.Core;

namespace Ltb.OpenVr;

/// <summary>
/// One runtime pose captured at the point it enters LTB. All time values are
/// seconds. Positive prediction offsets mean that the runtime was asked for a
/// pose predicted ahead of its current time.
/// </summary>
public readonly record struct PoseSourceSample
{
    public PoseSourceSample(
        TimestampedPoseSample poseSample,
        bool isConnected,
        PoseTrackingResult trackingResult,
        double? runtimeTimeSeconds = null,
        double? predictionOffsetSeconds = null,
        double? sampleAgeSeconds = null)
    {
        RecordedSample = new RecordedPoseSample(
            poseSample,
            isConnected,
            trackingResult,
            runtimeTimeSeconds,
            predictionOffsetSeconds,
            sampleAgeSeconds);
    }

    private PoseSourceSample(RecordedPoseSample recordedSample)
    {
        RecordedSample = recordedSample;
    }

    public RecordedPoseSample RecordedSample { get; }

    public TimestampedPoseSample PoseSample => RecordedSample.PoseSample;

    public double MonotonicHostTimeSeconds => RecordedSample.MonotonicHostTimeSeconds;

    public RigidTransform Pose => RecordedSample.Pose;

    public PoseValidity Validity => RecordedSample.Validity;

    public bool IsConnected => RecordedSample.IsConnected;

    public PoseTrackingResult TrackingResult => RecordedSample.TrackingResult;

    public double? RuntimeTimeSeconds => RecordedSample.RuntimeTimeSeconds;

    public double? PredictionOffsetSeconds => RecordedSample.PredictionOffsetSeconds;

    public double? SampleAgeSeconds => RecordedSample.SampleAgeSeconds;

    /// <summary>Lossless conversion to the versioned Core recording contract.</summary>
    public RecordedPoseSample ToRecordedPoseSample() => RecordedSample;

    public static PoseSourceSample FromRecordedPoseSample(RecordedPoseSample sample) => new(sample);
}

/// <summary>Pose source for an original, non-overridden input controller.</summary>
public interface InputControllerPoseSource
{
    SteamVrDeviceDescriptor Device { get; }

    PoseSourceSample ReadPose();
}

/// <summary>Pose source for a Lighthouse-tracked device.</summary>
public interface TrackedPoseSource
{
    SteamVrDeviceDescriptor Device { get; }

    PoseSourceSample ReadPose();
}
