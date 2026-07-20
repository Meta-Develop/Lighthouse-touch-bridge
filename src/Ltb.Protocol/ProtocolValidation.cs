namespace Ltb.Protocol;

public static class ProtocolValidation
{
    public static void Validate(ProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateOrdering(message.Ordering);

        switch (message)
        {
            case ProtocolHeartbeat:
                return;
            case ProtocolHandState handState:
                ValidateHandState(handState);
                return;
            default:
                throw new ProtocolException("The protocol message type is unsupported.");
        }
    }

    public static void ValidateOrdering(ProtocolOrdering ordering)
    {
        if (ordering.SessionId.IsEmpty)
        {
            throw new ProtocolException("The session identifier must not be all zeroes.");
        }

        if (ordering.ProducerMonotonicNanoseconds == 0)
        {
            throw new ProtocolException("The producer monotonic timestamp must be greater than zero.");
        }
    }

    public static void ValidateHandState(ProtocolHandState state)
    {
        if (state.Hand is not ProtocolHand.Left and not ProtocolHand.Right)
        {
            throw new ProtocolException("The hand identifier is invalid.");
        }

        if ((state.Presence & ~ProtocolConstants.AllowedPresence) != 0)
        {
            throw new ProtocolException("The presence flags contain unknown bits.");
        }

        var connected = state.Presence.HasFlag(ProtocolPresence.Connected);
        var tracked = state.Presence.HasFlag(ProtocolPresence.Tracked);
        var orientationValid = state.Presence.HasFlag(ProtocolPresence.OrientationValid);
        var positionValid = state.Presence.HasFlag(ProtocolPresence.PositionValid);
        var inputsValid = state.Presence.HasFlag(ProtocolPresence.InputsValid);
        var disconnectedValidityFlags =
            ProtocolPresence.OrientationValid |
            ProtocolPresence.PositionValid |
            ProtocolPresence.LinearVelocityValid |
            ProtocolPresence.AngularVelocityValid |
            ProtocolPresence.InputsValid |
            ProtocolPresence.Tracked;
        if (!connected && (state.Presence & disconnectedValidityFlags) != 0)
        {
            throw new ProtocolException("A disconnected hand cannot advertise valid state.");
        }

        if (tracked && (!orientationValid || !positionValid))
        {
            throw new ProtocolException("Tracked state requires valid position and orientation.");
        }

        ValidateFinite(state.DriverSpacePose.PositionMeters, "position");
        ValidateFinite(state.DriverSpacePose.OrientationXyzw, "quaternion");
        ValidateQuaternion(state.DriverSpacePose.OrientationXyzw);
        ValidateFinite(state.Motion.LinearVelocityMetersPerSecond, "linear velocity");
        ValidateFinite(state.Motion.AngularVelocityRadiansPerSecond, "angular velocity");
        if (!state.Presence.HasFlag(ProtocolPresence.LinearVelocityValid) &&
            state.Motion.LinearVelocityMetersPerSecond != ProtocolVector3.Zero)
        {
            throw new ProtocolException("Linear velocity must be zero when it is not valid.");
        }

        if (!state.Presence.HasFlag(ProtocolPresence.AngularVelocityValid) &&
            state.Motion.AngularVelocityRadiansPerSecond != ProtocolVector3.Zero)
        {
            throw new ProtocolException("Angular velocity must be zero when it is not valid.");
        }

        if ((state.Input.Buttons & ~ProtocolConstants.AllowedButtons) != 0)
        {
            throw new ProtocolException("The button bitset contains unknown bits.");
        }

        if ((state.Input.Touches & ~ProtocolConstants.AllowedTouches) != 0)
        {
            throw new ProtocolException("The touch bitset contains unknown bits.");
        }

        ValidateRange(state.Input.Trigger, ProtocolConstants.AnalogMinimum, ProtocolConstants.AnalogMaximum, "trigger");
        ValidateRange(state.Input.Grip, ProtocolConstants.AnalogMinimum, ProtocolConstants.AnalogMaximum, "grip");
        ValidateRange(state.Input.StickX, ProtocolConstants.StickMinimum, ProtocolConstants.StickMaximum, "stick X");
        ValidateRange(state.Input.StickY, ProtocolConstants.StickMinimum, ProtocolConstants.StickMaximum, "stick Y");

        if (!inputsValid && state.Input != ProtocolInputState.Neutral)
        {
            throw new ProtocolException("Inputs without the inputs-valid flag must be neutral.");
        }

        var batteryPresent = state.Presence.HasFlag(ProtocolPresence.BatteryPresent);
        ValidateRange(state.BatteryLevel, ProtocolConstants.AnalogMinimum, ProtocolConstants.AnalogMaximum, "battery");
        if (!batteryPresent && state.BatteryLevel != 0f)
        {
            throw new ProtocolException("Battery level must be zero when battery telemetry is absent.");
        }
    }

    private static void ValidateQuaternion(ProtocolQuaternion value)
    {
        var norm = MathF.Sqrt(
            (value.X * value.X) +
            (value.Y * value.Y) +
            (value.Z * value.Z) +
            (value.W * value.W));
        if (MathF.Abs(norm - 1f) > ProtocolConstants.QuaternionNormTolerance)
        {
            throw new ProtocolException("The quaternion must be normalized.");
        }
    }

    private static void ValidateFinite(ProtocolVector3 value, string field)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
        {
            throw new ProtocolException($"The {field} must contain only finite values.");
        }
    }

    private static void ValidateFinite(ProtocolQuaternion value, string field)
    {
        if (!float.IsFinite(value.X) ||
            !float.IsFinite(value.Y) ||
            !float.IsFinite(value.Z) ||
            !float.IsFinite(value.W))
        {
            throw new ProtocolException($"The {field} must contain only finite values.");
        }
    }

    private static void ValidateRange(float value, float minimum, float maximum, string field)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new ProtocolException($"The {field} value is outside its permitted range.");
        }
    }
}
