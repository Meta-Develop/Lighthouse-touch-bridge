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

internal readonly record struct OpenVrRuntimePoseRequest(
    uint TransientDeviceIndex,
    string StableSerial,
    string DevicePath);

internal readonly record struct OpenVrRuntimeVerifiedPose(
    OpenVrRuntimePoseRequest Device,
    OpenVrRuntimePose Pose);

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

    /// <summary>
    /// Revalidates every transient index against its stable identity before
    /// reading one logical pose batch. The Valve production implementation
    /// performs identity validation and acquisition inside one runtime lock.
    /// </summary>
    IReadOnlyList<OpenVrRuntimeVerifiedPose> ReadVerifiedPoses(
        IReadOnlyList<OpenVrRuntimePoseRequest> requests,
        OpenVrTrackingUniverse trackingUniverse,
        double predictionOffsetSeconds)
    {
        var validated = OpenVrRuntimePoseBatchValidation.ValidateRequests(
            requests,
            trackingUniverse,
            predictionOffsetSeconds);
        var currentDevices = EnumerateDevices();
        var currentByIndex = new Dictionary<uint, OpenVrRuntimeDevice>();
        foreach (var device in currentDevices)
        {
            if (!currentByIndex.TryAdd(device.TransientDeviceIndex, device))
            {
                throw new InvalidDataException(
                    $"OpenVR enumerated transient device index {device.TransientDeviceIndex} more than once.");
            }
        }

        var observedDevices = new OpenVrRuntimePoseRequest[validated.Length];
        for (var index = 0; index < validated.Length; index++)
        {
            var request = validated[index];
            if (!currentByIndex.TryGetValue(request.TransientDeviceIndex, out var current) ||
                !string.Equals(current.SerialNumber, request.StableSerial, StringComparison.Ordinal) ||
                !string.Equals(current.DevicePath, request.DevicePath, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"OpenVR transient device index {request.TransientDeviceIndex} no longer resolves to " +
                    $"serial '{request.StableSerial}' at path '{request.DevicePath}'.");
            }

            observedDevices[index] = new OpenVrRuntimePoseRequest(
                current.TransientDeviceIndex,
                current.SerialNumber,
                current.DevicePath);
        }

        var poses = ReadPoses(
            validated.Select(request => request.TransientDeviceIndex).ToArray(),
            trackingUniverse,
            predictionOffsetSeconds);
        if (poses.Count != validated.Length)
        {
            throw new InvalidDataException(
                $"OpenVR returned {poses.Count} poses for {validated.Length} identity-verified requests.");
        }

        return Array.AsReadOnly(observedDevices
            .Select((device, index) => new OpenVrRuntimeVerifiedPose(device, poses[index]))
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

    public static OpenVrRuntimePoseRequest[] ValidateRequests(
        IReadOnlyList<OpenVrRuntimePoseRequest> requests,
        OpenVrTrackingUniverse trackingUniverse,
        double predictionOffsetSeconds)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var snapshot = requests.ToArray();
        _ = Validate(
            snapshot.Select(request => request.TransientDeviceIndex).ToArray(),
            trackingUniverse,
            predictionOffsetSeconds);
        foreach (var request in snapshot)
        {
            if (string.IsNullOrWhiteSpace(request.StableSerial))
            {
                throw new ArgumentException(
                    "Pose-batch stable serials cannot be blank.",
                    nameof(requests));
            }

            if (string.IsNullOrWhiteSpace(request.DevicePath))
            {
                throw new ArgumentException(
                    "Pose-batch device paths cannot be blank.",
                    nameof(requests));
            }
        }

        return snapshot;
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
