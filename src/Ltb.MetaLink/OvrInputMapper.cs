using System.Numerics;
using Ltb.Core;
using Ltb.MetaLink.Interop;

namespace Ltb.MetaLink;

internal static class OvrInputMapper
{
    internal static bool TryMap(
        MetaLinkHand hand,
        OvrTrackingState tracking,
        OvrInputState input,
        uint connectedControllerTypes,
        MetaClockMapper clockMapper,
        out MetaLinkControllerSnapshot? snapshot,
        out string diagnostic)
    {
        var requiredController = hand == MetaLinkHand.Left
            ? OvrConstants.ControllerLTouch
            : OvrConstants.ControllerRTouch;
        if ((connectedControllerTypes & requiredController) == 0 ||
            (input.ControllerType & requiredController) == 0)
        {
            snapshot = null;
            diagnostic = $"{hand} Touch is not present in the LibOVR controller masks.";
            return false;
        }

        var poseState = hand == MetaLinkHand.Left
            ? tracking.LeftHandPose
            : tracking.RightHandPose;
        var status = hand == MetaLinkHand.Left
            ? tracking.LeftHandStatusFlags
            : tracking.RightHandStatusFlags;
        if ((status & OvrConstants.StatusOrientationValid) == 0)
        {
            snapshot = null;
            diagnostic = $"{hand} Touch orientation is not valid.";
            return false;
        }

        if (!TryPose(hand, poseState, status, clockMapper, out var pose, out diagnostic))
        {
            snapshot = null;
            return false;
        }

        var analog = MapAnalog(hand, input);
        if (!analog.IsValid)
        {
            snapshot = null;
            diagnostic = $"{hand} Touch input contains a non-finite or out-of-range analog value.";
            return false;
        }

        snapshot = new MetaLinkControllerSnapshot(
            hand,
            pose!,
            MapButtons(hand, input.Buttons),
            MapTouches(hand, input.Touches),
            analog,
            MetaLinkBatteryState.Unavailable);
        diagnostic = LiveDiagnostic(hand, snapshot.Pose);
        return true;
    }

    private static bool TryPose(
        MetaLinkHand hand,
        OvrPoseStatef state,
        uint status,
        MetaClockMapper clockMapper,
        out MetaLinkPoseSnapshot? pose,
        out string diagnostic)
    {
        var orientation = new Quaternion(
            state.ThePose.Orientation.X,
            state.ThePose.Orientation.Y,
            state.ThePose.Orientation.Z,
            state.ThePose.Orientation.W);
        if (!IsFinite(orientation) || orientation.LengthSquared() < 1e-12f)
        {
            pose = null;
            diagnostic = $"{hand} Touch reports orientation tracked but returned an invalid quaternion.";
            return false;
        }

        var isOrientationTracked = (status & OvrConstants.StatusOrientationTracked) != 0;
        var isPositionTracked = (status & OvrConstants.StatusPositionTracked) != 0;
        var hasPosition = (status & OvrConstants.StatusPositionValid) != 0;
        var position = Vector(state.ThePose.Position);
        if (!IsFinite(position))
        {
            hasPosition = false;
            position = Vector3.Zero;
        }

        var angularVelocity = Vector(state.AngularVelocity);
        var linearVelocity = Vector(state.LinearVelocity);
        var angularAcceleration = Vector(state.AngularAcceleration);
        var linearAcceleration = Vector(state.LinearAcceleration);
        if (!IsFinite(angularVelocity) ||
            !IsFinite(linearVelocity) ||
            !IsFinite(angularAcceleration) ||
            !IsFinite(linearAcceleration) ||
            !double.IsFinite(state.TimeInSeconds) ||
            state.TimeInSeconds < 0d)
        {
            pose = null;
            diagnostic = $"{hand} Touch pose state contains non-finite kinematic or timing data.";
            return false;
        }

        MetaClockMapping mapping;
        try
        {
            mapping = clockMapper.Map(state.TimeInSeconds, hand);
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException or InvalidOperationException or OverflowException)
        {
            pose = null;
            diagnostic = $"{hand} Touch clock mapping failed: {exception.Message}";
            return false;
        }

        pose = new MetaLinkPoseSnapshot(
            new RigidTransform(orientation, position),
            angularVelocity,
            linearVelocity,
            angularAcceleration,
            linearAcceleration,
            isOrientationTracked,
            isPositionTracked,
            hasValidOrientation: true,
            hasValidPosition: hasPosition,
            state.TimeInSeconds,
            mapping.AppMonotonicSeconds,
            mapping.AppMonotonicNanoseconds,
            mapping.UncertaintySeconds);
        diagnostic = string.Empty;
        return true;
    }

    private static MetaLinkButtons MapButtons(MetaLinkHand hand, uint mask) => hand switch
    {
        MetaLinkHand.Left => new MetaLinkButtons(
            A: false,
            B: false,
            X: Has(mask, OvrConstants.ButtonX),
            Y: Has(mask, OvrConstants.ButtonY),
            Thumbstick: Has(mask, OvrConstants.ButtonLThumb),
            Menu: Has(mask, OvrConstants.ButtonEnter),
            mask),
        MetaLinkHand.Right => new MetaLinkButtons(
            A: Has(mask, OvrConstants.ButtonA),
            B: Has(mask, OvrConstants.ButtonB),
            X: false,
            Y: false,
            Thumbstick: Has(mask, OvrConstants.ButtonRThumb),
            Menu: false,
            mask),
        _ => throw new ArgumentOutOfRangeException(nameof(hand)),
    };

    private static MetaLinkTouches MapTouches(MetaLinkHand hand, uint mask) => hand switch
    {
        MetaLinkHand.Left => new MetaLinkTouches(
            A: false,
            B: false,
            X: Has(mask, OvrConstants.TouchX),
            Y: Has(mask, OvrConstants.TouchY),
            Thumbstick: Has(mask, OvrConstants.TouchLThumb),
            ThumbRest: Has(mask, OvrConstants.TouchLThumbRest),
            IndexTrigger: Has(mask, OvrConstants.TouchLIndexTrigger),
            IndexPointing: Has(mask, OvrConstants.TouchLIndexPointing),
            ThumbUp: Has(mask, OvrConstants.TouchLThumbUp),
            mask),
        MetaLinkHand.Right => new MetaLinkTouches(
            A: Has(mask, OvrConstants.TouchA),
            B: Has(mask, OvrConstants.TouchB),
            X: false,
            Y: false,
            Thumbstick: Has(mask, OvrConstants.TouchRThumb),
            ThumbRest: Has(mask, OvrConstants.TouchRThumbRest),
            IndexTrigger: Has(mask, OvrConstants.TouchRIndexTrigger),
            IndexPointing: Has(mask, OvrConstants.TouchRIndexPointing),
            ThumbUp: Has(mask, OvrConstants.TouchRThumbUp),
            mask),
        _ => throw new ArgumentOutOfRangeException(nameof(hand)),
    };

    private static MetaLinkAnalogState MapAnalog(MetaLinkHand hand, OvrInputState input) =>
        hand switch
        {
            MetaLinkHand.Left => new MetaLinkAnalogState(
                ClampTrigger(input.IndexTriggerLeft),
                ClampTrigger(input.HandTriggerLeft),
                ClampStick(input.ThumbstickLeft)),
            MetaLinkHand.Right => new MetaLinkAnalogState(
                ClampTrigger(input.IndexTriggerRight),
                ClampTrigger(input.HandTriggerRight),
                ClampStick(input.ThumbstickRight)),
            _ => throw new ArgumentOutOfRangeException(nameof(hand)),
        };

    private static float ClampTrigger(float value) =>
        float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : value;

    private static Vector2 ClampStick(OvrVector2f value) => new(
        float.IsFinite(value.X) ? Math.Clamp(value.X, -1f, 1f) : value.X,
        float.IsFinite(value.Y) ? Math.Clamp(value.Y, -1f, 1f) : value.Y);

    private static Vector3 Vector(OvrVector3f value) => new(value.X, value.Y, value.Z);

    private static bool Has(uint value, uint flag) => (value & flag) != 0;

    private static string LiveDiagnostic(MetaLinkHand hand, MetaLinkPoseSnapshot pose)
    {
        if (!pose.IsOrientationTracked)
        {
            return $"{hand} Touch inputs and orientation are valid; orientation is currently estimated rather than tracked.";
        }

        if (pose.HasValidPosition && !pose.IsPositionTracked)
        {
            return $"{hand} Touch inputs and pose are valid; position is currently estimated rather than tracked.";
        }

        return $"{hand} Touch inputs and orientation are live.";
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);
}
