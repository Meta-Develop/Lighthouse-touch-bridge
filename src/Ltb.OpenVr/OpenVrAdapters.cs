using Ltb.Core;

namespace Ltb.OpenVr;

internal sealed class OpenVrDeviceEnumeratorAdapter : SteamVrDeviceEnumerator
{
    private readonly IOpenVrRuntime _runtime;

    public OpenVrDeviceEnumeratorAdapter(IOpenVrRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public IReadOnlyList<SteamVrDeviceDescriptor> EnumerateDevices()
    {
        var descriptors = _runtime.EnumerateDevices()
            .Where(device => device.DeviceClass != OpenVrRuntimeDeviceClass.Invalid)
            .Select(MapDevice)
            .OrderBy(device => device.Identity.SerialNumber, StringComparer.Ordinal)
            .ThenBy(device => device.Identity.DevicePath, StringComparer.Ordinal)
            .ToArray();

        var duplicateSerial = descriptors
            .GroupBy(device => device.Identity.SerialNumber, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateSerial is not null)
        {
            throw new InvalidOperationException(
                $"OpenVR reported duplicate serial '{duplicateSerial.Key}'; stable device association is unsafe.");
        }

        return Array.AsReadOnly(descriptors);
    }

    private static SteamVrDeviceDescriptor MapDevice(OpenVrRuntimeDevice device)
    {
        var category = device.DeviceClass switch
        {
            OpenVrRuntimeDeviceClass.HeadMountedDisplay => SteamVrDeviceCategory.HeadMountedDisplay,
            OpenVrRuntimeDeviceClass.Controller => SteamVrDeviceCategory.InputController,
            OpenVrRuntimeDeviceClass.GenericTracker => SteamVrDeviceCategory.GenericTracker,
            OpenVrRuntimeDeviceClass.TrackingReference => SteamVrDeviceCategory.TrackingReference,
            OpenVrRuntimeDeviceClass.DisplayRedirect => SteamVrDeviceCategory.DisplayRedirect,
            _ => SteamVrDeviceCategory.Unknown,
        };
        var role = device.ControllerRole switch
        {
            OpenVrRuntimeControllerRole.LeftHand => SteamVrControllerRole.LeftHand,
            OpenVrRuntimeControllerRole.RightHand => SteamVrControllerRole.RightHand,
            OpenVrRuntimeControllerRole.Other when category == SteamVrDeviceCategory.InputController =>
                SteamVrControllerRole.Other,
            _ => SteamVrControllerRole.None,
        };

        return new SteamVrDeviceDescriptor(
            new SteamVrDeviceIdentity(device.SerialNumber, device.DevicePath),
            device.TransientDeviceIndex,
            category,
            role,
            device.IsConnected,
            device.Metadata);
    }
}

internal abstract class OpenVrPoseSourceAdapter
{
    private readonly IOpenVrRuntime _runtime;
    private readonly IMonotonicClock _clock;
    private readonly OpenVrTrackingUniverse _trackingUniverse;
    private readonly double _predictionOffsetSeconds;

    protected OpenVrPoseSourceAdapter(
        IOpenVrRuntime runtime,
        IMonotonicClock clock,
        SteamVrDeviceDescriptor device,
        OpenVrTrackingUniverse trackingUniverse,
        double predictionOffsetSeconds)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Device = device ?? throw new ArgumentNullException(nameof(device));

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

        _trackingUniverse = trackingUniverse;
        _predictionOffsetSeconds = predictionOffsetSeconds;
    }

    public SteamVrDeviceDescriptor Device { get; }

    public PoseSourceSample ReadPose()
    {
        var runtimePose = _runtime.ReadPose(
            Device.TransientDeviceIndex,
            _trackingUniverse,
            _predictionOffsetSeconds);

        // Capture host time only after the native pose has crossed the LTB
        // ingress boundary. It intentionally is not wall-clock time.
        var monotonicHostTimeSeconds = _clock.GetTimestampSeconds();
        return ToPoseSourceSample(runtimePose, monotonicHostTimeSeconds);
    }

    internal static PoseSourceSample ToPoseSourceSample(
        OpenVrRuntimePose runtimePose,
        double monotonicHostTimeSeconds)
    {
        var poseSample = new TimestampedPoseSample(
            monotonicHostTimeSeconds,
            runtimePose.Pose,
            runtimePose.Validity);

        return new PoseSourceSample(
            poseSample,
            runtimePose.IsConnected,
            runtimePose.TrackingResult,
            runtimePose.RuntimeTimeSeconds,
            runtimePose.PredictionOffsetSeconds,
            runtimePose.SampleAgeSeconds,
            runtimePose.LinearVelocityMetersPerSecond,
            runtimePose.AngularVelocityRadiansPerSecond);
    }

    internal static void ValidateAcquisition(
        OpenVrTrackingUniverse trackingUniverse,
        double predictionOffsetSeconds)
    {
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
    }
}

internal sealed class OpenVrInputControllerPoseSourceAdapter :
    OpenVrPoseSourceAdapter,
    InputControllerPoseSource
{
    public OpenVrInputControllerPoseSourceAdapter(
        IOpenVrRuntime runtime,
        IMonotonicClock clock,
        SteamVrDeviceDescriptor device,
        double predictionOffsetSeconds)
        : this(
            runtime,
            clock,
            device,
            OpenVrTrackingUniverse.Standing,
            predictionOffsetSeconds)
    {
    }

    public OpenVrInputControllerPoseSourceAdapter(
        IOpenVrRuntime runtime,
        IMonotonicClock clock,
        SteamVrDeviceDescriptor device,
        OpenVrTrackingUniverse trackingUniverse,
        double predictionOffsetSeconds)
        : base(
            runtime,
            clock,
            RequireController(device),
            trackingUniverse,
            predictionOffsetSeconds)
    {
    }

    private static SteamVrDeviceDescriptor RequireController(SteamVrDeviceDescriptor device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Category != SteamVrDeviceCategory.InputController)
        {
            throw new ArgumentException("Device must be an input controller.", nameof(device));
        }

        return device;
    }
}

internal sealed class OpenVrTrackedPoseSourceAdapter :
    OpenVrPoseSourceAdapter,
    TrackedPoseSource
{
    public OpenVrTrackedPoseSourceAdapter(
        IOpenVrRuntime runtime,
        IMonotonicClock clock,
        SteamVrDeviceDescriptor device,
        double predictionOffsetSeconds)
        : this(
            runtime,
            clock,
            device,
            OpenVrTrackingUniverse.Standing,
            predictionOffsetSeconds)
    {
    }

    public OpenVrTrackedPoseSourceAdapter(
        IOpenVrRuntime runtime,
        IMonotonicClock clock,
        SteamVrDeviceDescriptor device,
        OpenVrTrackingUniverse trackingUniverse,
        double predictionOffsetSeconds)
        : base(
            runtime,
            clock,
            RequireTrackedDevice(device),
            trackingUniverse,
            predictionOffsetSeconds)
    {
    }

    internal static SteamVrDeviceDescriptor RequireTrackedDevice(SteamVrDeviceDescriptor device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!device.Capabilities.HasPosition ||
            device.Category == SteamVrDeviceCategory.InputController)
        {
            throw new ArgumentException(
                "Device must be a non-controller tracked device with position capability.",
                nameof(device));
        }

        return device;
    }
}

internal sealed class OpenVrTrackedPoseBatchSourceAdapter : TrackedPoseBatchSource
{
    private readonly IOpenVrRuntime _runtime;
    private readonly IMonotonicClock _clock;
    private readonly OpenVrTrackingUniverse _trackingUniverse;
    private readonly double _predictionOffsetSeconds;
    private readonly IReadOnlyList<SteamVrDeviceDescriptor> _devices;
    private readonly OpenVrRuntimePoseRequest[] _requests;

    public OpenVrTrackedPoseBatchSourceAdapter(
        IOpenVrRuntime runtime,
        IMonotonicClock clock,
        IEnumerable<SteamVrDeviceDescriptor> devices,
        OpenVrTrackingUniverse trackingUniverse,
        double predictionOffsetSeconds)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(devices);
        OpenVrPoseSourceAdapter.ValidateAcquisition(
            trackingUniverse,
            predictionOffsetSeconds);

        var snapshot = devices
            .Select(OpenVrTrackedPoseSourceAdapter.RequireTrackedDevice)
            .ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException(
                "A tracked-pose batch must contain at least one device.",
                nameof(devices));
        }

        _requests = OpenVrRuntimePoseBatchValidation.ValidateRequests(
            snapshot.Select(device => new OpenVrRuntimePoseRequest(
                device.TransientDeviceIndex,
                device.StableDeviceId,
                device.Identity.DevicePath)).ToArray(),
            trackingUniverse,
            predictionOffsetSeconds);
        _devices = Array.AsReadOnly(snapshot);
        _trackingUniverse = trackingUniverse;
        _predictionOffsetSeconds = predictionOffsetSeconds;
    }

    public IReadOnlyList<SteamVrDeviceDescriptor> Devices => _devices;

    public IReadOnlyList<TrackedPoseBatchSample> ReadPoses()
    {
        var runtimePoses = _runtime.ReadVerifiedPoses(
            _requests,
            _trackingUniverse,
            _predictionOffsetSeconds);
        if (runtimePoses.Count != _devices.Count)
        {
            throw new InvalidDataException(
                $"OpenVR returned {runtimePoses.Count} poses for {_devices.Count} requested devices.");
        }

        // The batch timestamp is deliberately captured once, after the one
        // logical runtime acquisition has fully crossed the LTB boundary.
        var monotonicHostTimeSeconds = _clock.GetTimestampSeconds();
        var samples = new TrackedPoseBatchSample[_devices.Count];
        for (var index = 0; index < samples.Length; index++)
        {
            if (runtimePoses[index].Device != _requests[index])
            {
                throw new InvalidDataException(
                    $"OpenVR returned a different identity for transient device index " +
                    $"{_requests[index].TransientDeviceIndex}.");
            }

            samples[index] = new TrackedPoseBatchSample(
                _devices[index],
                OpenVrPoseSourceAdapter.ToPoseSourceSample(
                    runtimePoses[index].Pose,
                    monotonicHostTimeSeconds));
        }

        return Array.AsReadOnly(samples);
    }
}
