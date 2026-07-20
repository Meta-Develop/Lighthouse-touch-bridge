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
    double? SampleAgeSeconds);

internal interface IOpenVrRuntime : IDisposable
{
    IReadOnlyList<OpenVrRuntimeDevice> EnumerateDevices();

    OpenVrRuntimePose ReadPose(
        uint transientDeviceIndex,
        OpenVrTrackingUniverse trackingUniverse,
        double predictionOffsetSeconds);

    OpenVrRuntimeHealthSnapshot GetRuntimeHealth() => OpenVrRuntimeHealthSnapshot.Running;
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
