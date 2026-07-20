using System.Numerics;
using Ltb.Core;
using Ltb.Driver;
using Ltb.OpenVr;
using Ltb.Protocol;

namespace Ltb.App;

/// <summary>
/// Pure first-party hand-state composition at the raw OpenVR driver boundary.
/// The tracker sample is authoritative for pose, motion, connectivity, and the
/// protocol source timestamp; Meta/send/heartbeat time never enters here.
/// </summary>
internal static class InternalDriverHandStateComposer
{
    private const double NanosecondsPerSecond = 1_000_000_000d;

    public static DriverHandState Compose(
        ProtocolHand hand,
        PoseSourceSample trackerSample,
        RigidTransform trackerFromControllerMount,
        ProtocolInputState input,
        bool inputsValid)
    {
        RequireHand(hand);
        if (!trackerFromControllerMount.IsValid)
        {
            throw new ArgumentException(
                "Tracker-from-controller mount must be a valid rigid transform.",
                nameof(trackerFromControllerMount));
        }

        var sourceTimestamp = ToProtocolTimestampNanoseconds(
            trackerSample.PoseSample.MonotonicTimeSeconds);
        var hasOrientation =
            trackerSample.IsConnected && trackerSample.PoseSample.HasValidOrientation;
        var hasPosition =
            trackerSample.IsConnected && trackerSample.PoseSample.HasValidPosition;
        if (!hasOrientation || !hasPosition)
        {
            return DriverHandState.Neutral(
                hand,
                sourceTimestamp,
                connected: trackerSample.IsConnected);
        }

        var driverFromController = CoordinateConventions.ComposeRuntimeOutput(
            trackerSample.Pose,
            trackerFromControllerMount);
        var presence =
            ProtocolPresence.Connected |
            ProtocolPresence.OrientationValid |
            ProtocolPresence.PositionValid |
            ProtocolPresence.Tracked;

        var angularVelocity = ProtocolVector3.Zero;
        if (trackerSample.AngularVelocityRadiansPerSecond is { } observedAngular)
        {
            angularVelocity = ToProtocolVector(observedAngular);
            presence |= ProtocolPresence.AngularVelocityValid;
        }

        var linearVelocity = ComputeControllerLinearVelocity(
            trackerSample,
            trackerFromControllerMount);
        if (linearVelocity is not null)
        {
            presence |= ProtocolPresence.LinearVelocityValid;
        }

        var publishedInput = ProtocolInputState.Neutral;
        if (inputsValid)
        {
            publishedInput = input;
            presence |= ProtocolPresence.InputsValid;
        }

        return new DriverHandState(
            hand,
            sourceTimestamp,
            presence,
            new ProtocolDriverPose(
                ToProtocolVector(driverFromController.TranslationMeters),
                ToProtocolQuaternion(driverFromController.Rotation)),
            new ProtocolMotion(
                linearVelocity ?? ProtocolVector3.Zero,
                angularVelocity),
            publishedInput,
            BatteryLevel: 0f);
    }

    /// <summary>
    /// Creates a best-effort neutral state at an explicit monotonic sample
    /// instant. No last valid pose, motion, input, battery, or haptic state is
    /// retained.
    /// </summary>
    public static DriverHandState Neutral(
        ProtocolHand hand,
        double sampleMonotonicTimeSeconds,
        bool connected = false)
    {
        RequireHand(hand);
        return DriverHandState.Neutral(
            hand,
            ToProtocolTimestampNanoseconds(sampleMonotonicTimeSeconds),
            connected);
    }

    internal static ulong ToProtocolTimestampNanoseconds(double monotonicTimeSeconds)
    {
        if (!double.IsFinite(monotonicTimeSeconds) || monotonicTimeSeconds <= 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(monotonicTimeSeconds),
                "The tracker sample monotonic time must be finite and greater than zero.");
        }

        var nanoseconds = monotonicTimeSeconds * NanosecondsPerSecond;
        if (!double.IsFinite(nanoseconds) || nanoseconds > ulong.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(monotonicTimeSeconds),
                "The tracker sample monotonic time cannot be represented in protocol nanoseconds.");
        }

        var rounded = Math.Round(nanoseconds, MidpointRounding.AwayFromZero);
        if (rounded < 1d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(monotonicTimeSeconds),
                "The tracker sample monotonic time rounds to the reserved zero protocol timestamp.");
        }

        return checked((ulong)rounded);
    }

    private static ProtocolVector3? ComputeControllerLinearVelocity(
        PoseSourceSample trackerSample,
        RigidTransform trackerFromControllerMount)
    {
        if (trackerSample.LinearVelocityMetersPerSecond is not { } trackerLinear)
        {
            return null;
        }

        var mountTranslation = trackerFromControllerMount.TranslationMeters;
        Vector3 controllerLinear;
        if (mountTranslation == Vector3.Zero)
        {
            controllerLinear = trackerLinear;
        }
        else if (trackerSample.AngularVelocityRadiansPerSecond is { } trackerAngular)
        {
            var driverSpaceLeverArm = Vector3.Transform(
                mountTranslation,
                trackerSample.Pose.Rotation);
            controllerLinear = trackerLinear + Vector3.Cross(
                trackerAngular,
                driverSpaceLeverArm);
        }
        else
        {
            return null;
        }

        return IsFinite(controllerLinear)
            ? ToProtocolVector(controllerLinear)
            : null;
    }

    private static ProtocolVector3 ToProtocolVector(Vector3 value) =>
        new(value.X, value.Y, value.Z);

    private static ProtocolQuaternion ToProtocolQuaternion(Quaternion value) =>
        new(value.X, value.Y, value.Z, value.W);

    private static void RequireHand(ProtocolHand hand)
    {
        if (hand is not ProtocolHand.Left and not ProtocolHand.Right)
        {
            throw new ArgumentOutOfRangeException(nameof(hand));
        }
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
