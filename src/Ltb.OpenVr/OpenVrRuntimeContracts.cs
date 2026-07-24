using System.Numerics;
using Ltb.Core;

namespace Ltb.OpenVr;

internal enum OpenVrRuntimeDeviceClass
{
    Invalid = 0,
    HeadMountedDisplay = 1,
    Controller = 2,
    GenericTracker = 3,
    TrackingReference = 4,
    DisplayRedirect = 5,
    Unknown = 6,
}

internal enum OpenVrRuntimeControllerRole
{
    None = 0,
    LeftHand = 1,
    RightHand = 2,
    Other = 3,
}

internal enum OpenVrTrackingResultCode
{
    Uninitialized = 1,
    CalibratingInProgress = 100,
    CalibratingOutOfRange = 101,
    RunningOk = 200,
    RunningOutOfRange = 201,
    FallbackRotationOnly = 300,
}

internal readonly record struct OpenVrRuntimeDevice(
    uint TransientDeviceIndex,
    string SerialNumber,
    string DevicePath,
    OpenVrRuntimeDeviceClass DeviceClass,
    OpenVrRuntimeControllerRole ControllerRole,
    bool IsConnected,
    SteamVrDeviceMetadata? Metadata = null);

internal readonly record struct OpenVrRuntimePose(
    RigidTransform Pose,
    PoseValidity Validity,
    bool IsConnected,
    PoseTrackingResult TrackingResult,
    double? RuntimeTimeSeconds,
    double? PredictionOffsetSeconds,
    double? SampleAgeSeconds,
    Vector3? LinearVelocityMetersPerSecond,
    Vector3? AngularVelocityRadiansPerSecond)
{
    public OpenVrRuntimePose(
        RigidTransform Pose,
        PoseValidity Validity,
        bool IsConnected,
        PoseTrackingResult TrackingResult,
        double? RuntimeTimeSeconds,
        double? PredictionOffsetSeconds,
        double? SampleAgeSeconds)
        : this(
            Pose,
            Validity,
            IsConnected,
            TrackingResult,
            RuntimeTimeSeconds,
            PredictionOffsetSeconds,
            SampleAgeSeconds,
            LinearVelocityMetersPerSecond: null,
            AngularVelocityRadiansPerSecond: null)
    {
    }
}

internal interface IOpenVrRuntime : IDisposable
{
    IReadOnlyList<OpenVrRuntimeDevice> EnumerateDevices();

    OpenVrRuntimePose ReadPose(
        uint transientDeviceIndex,
        OpenVrTrackingUniverse trackingUniverse,
        double predictionOffsetSeconds);

    /// <summary>
    /// Reads several transient indexes for one logical acquisition. Runtime
    /// implementations may override this to take one native all-device
    /// snapshot. The default preserves compatibility with deterministic fakes
    /// and other narrow implementations by delegating to <see cref="ReadPose"/>.
    /// </summary>
    IReadOnlyList<OpenVrRuntimePose> ReadPoses(
        IReadOnlyList<uint> transientDeviceIndexes,
        OpenVrTrackingUniverse trackingUniverse,
        double predictionOffsetSeconds)
    {
        var indexes = OpenVrRuntimePoseBatchValidation.Validate(
            transientDeviceIndexes,
            trackingUniverse,
            predictionOffsetSeconds);
        return Array.AsReadOnly(indexes
            .Select(index => ReadPose(index, trackingUniverse, predictionOffsetSeconds))
            .ToArray());
    }

    OpenVrRuntimeHealthSnapshot GetRuntimeHealth() => OpenVrRuntimeHealthSnapshot.Running;
}

internal static class OpenVrRuntimePoseBatchValidation
{
    // OpenVR fixes this limit at k_unMaxTrackedDeviceCount. Keep the portable
    // contract independent of generated Valve types so Linux fakes validate
    // the same transient-index range as the Windows adapter.
    public const uint MaximumTrackedDeviceCount = 64;

    public static uint[] Validate(
        IReadOnlyList<uint> transientDeviceIndexes,
        OpenVrTrackingUniverse trackingUniverse,
        double predictionOffsetSeconds)
    {
        ArgumentNullException.ThrowIfNull(transientDeviceIndexes);
        if (transientDeviceIndexes.Count == 0)
        {
            throw new ArgumentException(
                "A pose batch must request at least one transient device index.",
                nameof(transientDeviceIndexes));
        }

        if (!Enum.IsDefined(trackingUniverse))
        {
            throw new ArgumentOutOfRangeException(
                nameof(trackingUniverse),
                trackingUniverse,
                "Tracking universe must be a defined OpenVR frame contract.");
        }

        if (!double.IsFinite(predictionOffsetSeconds) ||
            predictionOffsetSeconds < float.MinValue ||
            predictionOffsetSeconds > float.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(predictionOffsetSeconds),
                "Prediction offset must be finite and representable by OpenVR's float-seconds API.");
        }

        var indexes = new uint[transientDeviceIndexes.Count];
        var distinctIndexes = new HashSet<uint>();
        for (var index = 0; index < transientDeviceIndexes.Count; index++)
        {
            var transientDeviceIndex = transientDeviceIndexes[index];
            if (transientDeviceIndex >= MaximumTrackedDeviceCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(transientDeviceIndexes),
                    transientDeviceIndex,
                    $"Transient device indexes must be below {MaximumTrackedDeviceCount}.");
            }

            if (!distinctIndexes.Add(transientDeviceIndex))
            {
                throw new ArgumentException(
                    $"Transient device index {transientDeviceIndex} was requested more than once.",
                    nameof(transientDeviceIndexes));
            }

            indexes[index] = transientDeviceIndex;
        }

        return indexes;
    }
}

internal interface IMonotonicClock
{
    double GetTimestampSeconds();
}

internal sealed class StopwatchMonotonicClock : IMonotonicClock
{
    public static StopwatchMonotonicClock Instance { get; } = new();

    private StopwatchMonotonicClock()
    {
    }

    public double GetTimestampSeconds() =>
        System.Diagnostics.Stopwatch.GetTimestamp() /
        (double)System.Diagnostics.Stopwatch.Frequency;
}

internal readonly record struct OpenVrMatrix34(
    float M0,
    float M1,
    float M2,
    float M3,
    float M4,
    float M5,
    float M6,
    float M7,
    float M8,
    float M9,
    float M10,
    float M11);
