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
            device.IsConnected);
    }
}

internal abstract class OpenVrPoseSourceAdapter
{
    private readonly IOpenVrRuntime _runtime;
    private readonly IMonotonicClock _clock;
    private readonly double _predictionOffsetSeconds;

    protected OpenVrPoseSourceAdapter(
        IOpenVrRuntime runtime,
        IMonotonicClock clock,
        SteamVrDeviceDescriptor device,
        double predictionOffsetSeconds)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Device = device ?? throw new ArgumentNullException(nameof(device));

        if (!double.IsFinite(predictionOffsetSeconds) ||
            predictionOffsetSeconds < float.MinValue ||
            predictionOffsetSeconds > float.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(predictionOffsetSeconds),
                "Prediction offset must be finite and representable by OpenVR's float-seconds API.");
        }

        _predictionOffsetSeconds = predictionOffsetSeconds;
    }

    public SteamVrDeviceDescriptor Device { get; }

    public PoseSourceSample ReadPose()
    {
        var runtimePose = _runtime.ReadPose(
            Device.TransientDeviceIndex,
            _predictionOffsetSeconds);

        // Capture host time only after the native pose has crossed the LTB
        // ingress boundary. It intentionally is not wall-clock time.
        var monotonicHostTimeSeconds = _clock.GetTimestampSeconds();
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
            runtimePose.SampleAgeSeconds);
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
        : base(runtime, clock, RequireController(device), predictionOffsetSeconds)
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
        : base(runtime, clock, RequireTrackedDevice(device), predictionOffsetSeconds)
    {
    }

    private static SteamVrDeviceDescriptor RequireTrackedDevice(SteamVrDeviceDescriptor device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Category is not (
            SteamVrDeviceCategory.GenericTracker or
            SteamVrDeviceCategory.HeadMountedDisplay or
            SteamVrDeviceCategory.TrackingReference))
        {
            throw new ArgumentException(
                "Device must be a tracker, HMD, or tracking reference.",
                nameof(device));
        }

        return device;
    }
}
