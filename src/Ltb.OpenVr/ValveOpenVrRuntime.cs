extern alias OpenVrInterop;

using System.Numerics;
using System.Text;
using Ltb.Core;
using ValveVr = OpenVrInterop::Valve.VR;

namespace Ltb.OpenVr;

internal sealed class ValveOpenVrRuntime : IOpenVrRuntime
{
    private static readonly object InitializationSync = new();
    private static bool _sessionActive;

    private readonly object _sync = new();
    private readonly ValveVr.CVRSystem _system;
    private readonly ValveVr.TrackedDevicePose_t[] _poseBuffer =
        new ValveVr.TrackedDevicePose_t[ValveVr.OpenVR.k_unMaxTrackedDeviceCount];
    private bool _quitObserved;
    private bool _disposed;

    private ValveOpenVrRuntime(ValveVr.CVRSystem system)
    {
        _system = system;
    }

    public static ValveOpenVrRuntime Open()
    {
        lock (InitializationSync)
        {
            if (_sessionActive)
            {
                throw new OpenVrRuntimeInitializationException(
                    "An OpenVR session is already active in this process.");
            }

            var error = ValveVr.EVRInitError.None;
            var system = ValveVr.OpenVR.Init(
                ref error,
                ValveVr.EVRApplicationType.VRApplication_Background,
                "lighthouse-touch-bridge");
            if (error != ValveVr.EVRInitError.None || system is null)
            {
                throw new OpenVrRuntimeInitializationException(FormatInitError(error));
            }

            _sessionActive = true;
            return new ValveOpenVrRuntime(system);
        }
    }

    public IReadOnlyList<OpenVrRuntimeDevice> EnumerateDevices()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            var devices = new List<OpenVrRuntimeDevice>();
            for (uint index = 0; index < ValveVr.OpenVR.k_unMaxTrackedDeviceCount; index++)
            {
                var nativeClass = _system.GetTrackedDeviceClass(index);
                if (nativeClass == ValveVr.ETrackedDeviceClass.Invalid)
                {
                    continue;
                }

                var serialNumber = ReadStringProperty(
                    index,
                    ValveVr.ETrackedDeviceProperty.Prop_SerialNumber_String);
                if (string.IsNullOrWhiteSpace(serialNumber))
                {
                    // A transient slot without a serial is unsafe for profile
                    // association, so it is deliberately not exposed.
                    continue;
                }

                var registeredDeviceType = ReadStringProperty(
                    index,
                    ValveVr.ETrackedDeviceProperty.Prop_RegisteredDeviceType_String);
                var devicePath = OpenVrDevicePath.Resolve(
                    registeredDeviceType,
                    serialNumber);
                SteamVrDeviceMetadata? metadata = null;
                if (OpenVrDevicePath.TryGetDriverId(devicePath, out var driverId))
                {
                    metadata = new SteamVrDeviceMetadata(
                        driverId,
                        ReadStringProperty(
                            index,
                            ValveVr.ETrackedDeviceProperty.Prop_TrackingSystemName_String),
                        ReadStringProperty(
                            index,
                            ValveVr.ETrackedDeviceProperty.Prop_ManufacturerName_String),
                        ReadStringProperty(
                            index,
                            ValveVr.ETrackedDeviceProperty.Prop_ModelNumber_String),
                        ReadStringProperty(
                            index,
                            ValveVr.ETrackedDeviceProperty.Prop_ControllerType_String));
                }

                devices.Add(new OpenVrRuntimeDevice(
                    index,
                    serialNumber,
                    devicePath,
                    MapDeviceClass(nativeClass),
                    MapControllerRole(_system.GetControllerRoleForTrackedDeviceIndex(index)),
                    _system.IsTrackedDeviceConnected(index),
                    metadata));
            }

            return devices;
        }
    }

    public OpenVrRuntimePose ReadPose(
        uint transientDeviceIndex,
        double predictionOffsetSeconds)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (transientDeviceIndex >= _poseBuffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(transientDeviceIndex));
            }

            _system.GetDeviceToAbsoluteTrackingPose(
                ValveVr.ETrackingUniverseOrigin.TrackingUniverseStanding,
                (float)predictionOffsetSeconds,
                _poseBuffer);
            var nativePose = _poseBuffer[transientDeviceIndex];
            var matrix = nativePose.mDeviceToAbsoluteTracking;
            var hasPose = OpenVrMatrixConverter.TryConvert(
                new OpenVrMatrix34(
                    matrix.m0,
                    matrix.m1,
                    matrix.m2,
                    matrix.m3,
                    matrix.m4,
                    matrix.m5,
                    matrix.m6,
                    matrix.m7,
                    matrix.m8,
                    matrix.m9,
                    matrix.m10,
                    matrix.m11),
                out var pose);

            var validity = OpenVrPoseValidityMapper.Map(
                hasPose,
                nativePose.bPoseIsValid,
                nativePose.eTrackingResult == ValveVr.ETrackingResult.Fallback_RotationOnly);

            return new OpenVrRuntimePose(
                hasPose ? pose : RigidTransform.Identity,
                validity,
                nativePose.bDeviceIsConnected,
                MapTrackingResult((OpenVrTrackingResultCode)(int)nativePose.eTrackingResult),
                RuntimeTimeSeconds: null,
                PredictionOffsetSeconds: predictionOffsetSeconds,
                SampleAgeSeconds: null);
        }
    }

    public OpenVrRuntimeHealthSnapshot GetRuntimeHealth()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_quitObserved)
            {
                return new OpenVrRuntimeHealthSnapshot(
                    OpenVrRuntimeHealthState.Stopped,
                    "SteamVR previously reported a terminal runtime-quit event.");
            }

            var runtimeEvent = new ValveVr.VREvent_t();
            var eventSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ValveVr.VREvent_t>();
            while (_system.PollNextEvent(ref runtimeEvent, eventSize))
            {
                var disposition = OpenVrRuntimeEventSemantics.Classify(runtimeEvent.eventType);
                if (disposition ==
                    OpenVrRuntimeEventDisposition.RuntimeStoppedAndAcknowledgeQuit)
                {
                    _system.AcknowledgeQuit_Exiting();
                    _quitObserved = true;
                    break;
                }
                else if (disposition == OpenVrRuntimeEventDisposition.RuntimeStopped)
                {
                    _quitObserved = true;
                }
            }

            return _quitObserved
                ? new OpenVrRuntimeHealthSnapshot(
                    OpenVrRuntimeHealthState.Stopped,
                    "SteamVR reported a terminal runtime-quit event.")
                : OpenVrRuntimeHealthSnapshot.Running;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            lock (InitializationSync)
            {
                try
                {
                    ValveVr.OpenVR.Shutdown();
                }
                finally
                {
                    _sessionActive = false;
                    _disposed = true;
                }
            }
        }
    }

    private string? ReadStringProperty(
        uint deviceIndex,
        ValveVr.ETrackedDeviceProperty property)
    {
        var error = ValveVr.ETrackedPropertyError.TrackedProp_Success;
        var builder = new StringBuilder(256);
        var requiredLength = _system.GetStringTrackedDeviceProperty(
            deviceIndex,
            property,
            builder,
            (uint)builder.Capacity,
            ref error);

        if (error == ValveVr.ETrackedPropertyError.TrackedProp_BufferTooSmall &&
            requiredLength > builder.Capacity &&
            requiredLength <= int.MaxValue)
        {
            builder = new StringBuilder((int)requiredLength);
            error = ValveVr.ETrackedPropertyError.TrackedProp_Success;
            _system.GetStringTrackedDeviceProperty(
                deviceIndex,
                property,
                builder,
                (uint)builder.Capacity,
                ref error);
        }

        return error == ValveVr.ETrackedPropertyError.TrackedProp_Success
            ? builder.ToString()
            : null;
    }

    private static OpenVrRuntimeDeviceClass MapDeviceClass(
        ValveVr.ETrackedDeviceClass deviceClass) => deviceClass switch
    {
        ValveVr.ETrackedDeviceClass.HMD => OpenVrRuntimeDeviceClass.HeadMountedDisplay,
        ValveVr.ETrackedDeviceClass.Controller => OpenVrRuntimeDeviceClass.Controller,
        ValveVr.ETrackedDeviceClass.GenericTracker => OpenVrRuntimeDeviceClass.GenericTracker,
        ValveVr.ETrackedDeviceClass.TrackingReference => OpenVrRuntimeDeviceClass.TrackingReference,
        ValveVr.ETrackedDeviceClass.DisplayRedirect => OpenVrRuntimeDeviceClass.DisplayRedirect,
        ValveVr.ETrackedDeviceClass.Invalid => OpenVrRuntimeDeviceClass.Invalid,
        _ => OpenVrRuntimeDeviceClass.Unknown,
    };

    private static OpenVrRuntimeControllerRole MapControllerRole(
        ValveVr.ETrackedControllerRole role) => role switch
    {
        ValveVr.ETrackedControllerRole.LeftHand => OpenVrRuntimeControllerRole.LeftHand,
        ValveVr.ETrackedControllerRole.RightHand => OpenVrRuntimeControllerRole.RightHand,
        ValveVr.ETrackedControllerRole.Invalid => OpenVrRuntimeControllerRole.None,
        _ => OpenVrRuntimeControllerRole.Other,
    };

    internal static PoseTrackingResult MapTrackingResult(
        OpenVrTrackingResultCode trackingResult) => trackingResult switch
    {
        OpenVrTrackingResultCode.Uninitialized => PoseTrackingResult.Uninitialized,
        OpenVrTrackingResultCode.CalibratingInProgress => PoseTrackingResult.CalibratingInProgress,
        OpenVrTrackingResultCode.CalibratingOutOfRange => PoseTrackingResult.CalibratingOutOfRange,
        OpenVrTrackingResultCode.RunningOk => PoseTrackingResult.RunningOk,
        OpenVrTrackingResultCode.RunningOutOfRange => PoseTrackingResult.RunningOutOfRange,
        OpenVrTrackingResultCode.FallbackRotationOnly => PoseTrackingResult.FallbackRotationOnly,
        _ => PoseTrackingResult.Unknown,
    };

    private static string FormatInitError(ValveVr.EVRInitError error)
    {
        try
        {
            var description = ValveVr.OpenVR.GetStringForHmdError(error);
            return string.IsNullOrWhiteSpace(description)
                ? error.ToString()
                : $"{error}: {description}";
        }
        catch
        {
            return error.ToString();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal static class OpenVrMatrixConverter
{
    private const float MinimumBasisLengthSquared = 0.64f;
    private const float MaximumBasisLengthSquared = 1.44f;
    private const float MaximumBasisDotMagnitude = 0.2f;
    private const float MinimumDeterminant = 0.5f;

    public static bool TryConvert(OpenVrMatrix34 source, out RigidTransform transform)
    {
        var xBasis = new Vector3(source.M0, source.M4, source.M8);
        var yBasis = new Vector3(source.M1, source.M5, source.M9);
        var zBasis = new Vector3(source.M2, source.M6, source.M10);
        var translation = new Vector3(source.M3, source.M7, source.M11);

        if (!IsFinite(xBasis) ||
            !IsFinite(yBasis) ||
            !IsFinite(zBasis) ||
            !IsFinite(translation) ||
            !HasUsableBasis(xBasis, yBasis, zBasis))
        {
            transform = RigidTransform.Identity;
            return false;
        }

        // OpenVR stores a column-vector 3x4 transform. System.Numerics stores
        // rotation matrices for row-vector multiplication, so transpose the
        // 3x3 basis before extracting the equivalent XYZW quaternion.
        var rotationMatrix = new Matrix4x4(
            source.M0, source.M4, source.M8, 0f,
            source.M1, source.M5, source.M9, 0f,
            source.M2, source.M6, source.M10, 0f,
            0f, 0f, 0f, 1f);
        var rotation = Quaternion.CreateFromRotationMatrix(rotationMatrix);
        if (!IsFinite(rotation) || rotation.LengthSquared() < 1e-12f)
        {
            transform = RigidTransform.Identity;
            return false;
        }

        transform = new RigidTransform(rotation, translation);
        return true;
    }

    private static bool HasUsableBasis(Vector3 x, Vector3 y, Vector3 z)
    {
        if (!IsUsableLength(x.LengthSquared()) ||
            !IsUsableLength(y.LengthSquared()) ||
            !IsUsableLength(z.LengthSquared()))
        {
            return false;
        }

        var normalizedX = Vector3.Normalize(x);
        var normalizedY = Vector3.Normalize(y);
        var normalizedZ = Vector3.Normalize(z);
        return MathF.Abs(Vector3.Dot(normalizedX, normalizedY)) <= MaximumBasisDotMagnitude &&
               MathF.Abs(Vector3.Dot(normalizedX, normalizedZ)) <= MaximumBasisDotMagnitude &&
               MathF.Abs(Vector3.Dot(normalizedY, normalizedZ)) <= MaximumBasisDotMagnitude &&
               Vector3.Dot(Vector3.Cross(normalizedX, normalizedY), normalizedZ) >= MinimumDeterminant;
    }

    private static bool IsUsableLength(float lengthSquared) =>
        float.IsFinite(lengthSquared) &&
        lengthSquared >= MinimumBasisLengthSquared &&
        lengthSquared <= MaximumBasisLengthSquared;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);
}

internal static class OpenVrPoseValidityMapper
{
    public static PoseValidity Map(
        bool hasUsableMatrix,
        bool nativePoseIsValid,
        bool isRotationOnlyFallback)
    {
        if (!hasUsableMatrix)
        {
            return PoseValidity.None;
        }

        var validity = PoseValidity.Orientation;
        if (!isRotationOnlyFallback)
        {
            validity |= PoseValidity.Position;
        }

        if (nativePoseIsValid)
        {
            validity |= PoseValidity.TrackingValid;
        }

        return validity;
    }
}
