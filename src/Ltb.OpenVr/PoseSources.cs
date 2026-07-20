using System.Numerics;
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
        : this(
            poseSample,
            isConnected,
            trackingResult,
            runtimeTimeSeconds,
            predictionOffsetSeconds,
            sampleAgeSeconds,
            linearVelocityMetersPerSecond: null,
            angularVelocityRadiansPerSecond: null)
    {
    }

    public PoseSourceSample(
        TimestampedPoseSample poseSample,
        bool isConnected,
        PoseTrackingResult trackingResult,
        double? runtimeTimeSeconds,
        double? predictionOffsetSeconds,
        double? sampleAgeSeconds,
        Vector3? linearVelocityMetersPerSecond,
        Vector3? angularVelocityRadiansPerSecond)
    {
        RequireFiniteOptional(
            linearVelocityMetersPerSecond,
            nameof(linearVelocityMetersPerSecond));
        RequireFiniteOptional(
            angularVelocityRadiansPerSecond,
            nameof(angularVelocityRadiansPerSecond));

        RecordedSample = new RecordedPoseSample(
            poseSample,
            isConnected,
            trackingResult,
            runtimeTimeSeconds,
            predictionOffsetSeconds,
            sampleAgeSeconds);
        LinearVelocityMetersPerSecond = linearVelocityMetersPerSecond;
        AngularVelocityRadiansPerSecond = angularVelocityRadiansPerSecond;
    }

    private PoseSourceSample(RecordedPoseSample recordedSample)
    {
        RecordedSample = recordedSample;
        LinearVelocityMetersPerSecond = null;
        AngularVelocityRadiansPerSecond = null;
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

    /// <summary>
    /// Runtime-observed linear velocity in meters per second, when every
    /// component supplied by the pose source was finite.
    /// </summary>
    public Vector3? LinearVelocityMetersPerSecond { get; }

    /// <summary>
    /// Runtime-observed angular velocity in radians per second, when every
    /// component supplied by the pose source was finite.
    /// </summary>
    public Vector3? AngularVelocityRadiansPerSecond { get; }

    /// <summary>Converts pose and runtime-status metadata to the Core recording contract.</summary>
    public RecordedPoseSample ToRecordedPoseSample() => RecordedSample;

    public static PoseSourceSample FromRecordedPoseSample(RecordedPoseSample sample) => new(sample);

    private static void RequireFiniteOptional(Vector3? value, string parameterName)
    {
        if (value is { } present &&
            (!float.IsFinite(present.X) ||
             !float.IsFinite(present.Y) ||
             !float.IsFinite(present.Z)))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Velocity components must be finite when present.");
        }
    }
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
