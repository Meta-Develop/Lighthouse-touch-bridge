using System.Buffers.Binary;

namespace Ltb.Protocol;

public static class ProtocolCodec
{
    public static byte[] Encode(ProtocolMessage message)
    {
        ProtocolValidation.Validate(message);
        var packet = new byte[message.MessageType switch
        {
            ProtocolMessageType.HandState => ProtocolConstants.HandStatePacketSize,
            ProtocolMessageType.Heartbeat => ProtocolConstants.HeartbeatPacketSize,
            _ => throw new ProtocolException("The protocol message type is unsupported."),
        }];

        WriteHeader(packet, message.MessageType);
        WriteOrdering(packet, message.Ordering);
        if (message is ProtocolHandState handState)
        {
            WriteHandState(packet, handState);
        }

        return packet;
    }

    public static ProtocolMessage Decode(ReadOnlySpan<byte> packet)
    {
        var header = DecodeHeader(packet);
        var expectedPayloadLength = header.MessageType switch
        {
            ProtocolMessageType.HandState => ProtocolConstants.HandStatePayloadSize,
            ProtocolMessageType.Heartbeat => ProtocolConstants.HeartbeatPayloadSize,
            _ => throw new ProtocolException("The message type is unsupported."),
        };

        if (header.PayloadLength != expectedPayloadLength)
        {
            throw new ProtocolException("The payload length does not match the message type.");
        }

        var expectedPacketLength = checked(ProtocolConstants.HeaderSize + expectedPayloadLength);
        if (packet.Length != expectedPacketLength)
        {
            throw new ProtocolException("The packet is truncated or contains trailing data.");
        }

        var ordering = ReadOrdering(packet);
        ProtocolMessage message = header.MessageType switch
        {
            ProtocolMessageType.HandState => ReadHandState(packet, ordering),
            ProtocolMessageType.Heartbeat => new ProtocolHeartbeat(ordering),
            _ => throw new ProtocolException("The message type is unsupported."),
        };
        ProtocolValidation.Validate(message);
        return message;
    }

    public static bool TryDecode(ReadOnlySpan<byte> packet, out ProtocolMessage? message)
    {
        try
        {
            message = Decode(packet);
            return true;
        }
        catch (Exception exception) when (exception is ProtocolException or OverflowException)
        {
            message = null;
            return false;
        }
    }

    public static ProtocolHeader DecodeHeader(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < ProtocolConstants.HeaderSize)
        {
            throw new ProtocolException("The packet is shorter than the protocol header.");
        }

        var header = new ProtocolHeader(
            ReadUInt32(packet, ProtocolConstants.MagicOffset),
            ReadUInt16(packet, ProtocolConstants.MajorVersionOffset),
            ReadUInt16(packet, ProtocolConstants.MinorVersionOffset),
            (ProtocolMessageType)ReadUInt16(packet, ProtocolConstants.MessageTypeOffset),
            ReadUInt16(packet, ProtocolConstants.HeaderReservedOffset),
            ReadUInt32(packet, ProtocolConstants.PayloadLengthOffset));

        if (header.Magic != ProtocolConstants.Magic)
        {
            throw new ProtocolException("The packet magic is invalid.");
        }

        if (header.MajorVersion != ProtocolConstants.MajorVersion ||
            header.MinorVersion != ProtocolConstants.MinorVersion)
        {
            throw new ProtocolException("The protocol version is unsupported.");
        }

        if (header.MessageType is not ProtocolMessageType.HandState and not ProtocolMessageType.Heartbeat)
        {
            throw new ProtocolException("The message type is unsupported.");
        }

        if (header.Reserved != 0)
        {
            throw new ProtocolException("Reserved header bits must be zero.");
        }

        return header;
    }

    private static void WriteHeader(Span<byte> packet, ProtocolMessageType messageType)
    {
        WriteUInt32(packet, ProtocolConstants.MagicOffset, ProtocolConstants.Magic);
        WriteUInt16(packet, ProtocolConstants.MajorVersionOffset, ProtocolConstants.MajorVersion);
        WriteUInt16(packet, ProtocolConstants.MinorVersionOffset, ProtocolConstants.MinorVersion);
        WriteUInt16(packet, ProtocolConstants.MessageTypeOffset, (ushort)messageType);
        WriteUInt16(packet, ProtocolConstants.HeaderReservedOffset, 0);
        WriteUInt32(
            packet,
            ProtocolConstants.PayloadLengthOffset,
            (uint)(messageType == ProtocolMessageType.HandState
                ? ProtocolConstants.HandStatePayloadSize
                : ProtocolConstants.HeartbeatPayloadSize));
    }

    private static void WriteOrdering(Span<byte> packet, ProtocolOrdering ordering)
    {
        WriteUInt64(packet, ProtocolConstants.SessionIdOffset, ordering.SessionId.Word0);
        WriteUInt64(packet, ProtocolConstants.SessionIdOffset + sizeof(ulong), ordering.SessionId.Word1);
        WriteUInt64(packet, ProtocolConstants.SequenceOffset, ordering.Sequence);
        WriteUInt64(packet, ProtocolConstants.MonotonicNanosecondsOffset, ordering.ProducerMonotonicNanoseconds);
    }

    private static ProtocolOrdering ReadOrdering(ReadOnlySpan<byte> packet) =>
        new(
            new ProtocolSessionId(
                ReadUInt64(packet, ProtocolConstants.SessionIdOffset),
                ReadUInt64(packet, ProtocolConstants.SessionIdOffset + sizeof(ulong))),
            ReadUInt64(packet, ProtocolConstants.SequenceOffset),
            ReadUInt64(packet, ProtocolConstants.MonotonicNanosecondsOffset));

    private static void WriteHandState(Span<byte> packet, ProtocolHandState state)
    {
        packet[ProtocolConstants.HandOffset] = (byte)state.Hand;
        packet[ProtocolConstants.HandReservedOffset] = 0;
        WriteUInt16(packet, ProtocolConstants.PresenceFlagsOffset, (ushort)state.Presence);
        WriteVector3(packet, ProtocolConstants.PositionOffset, state.DriverSpacePose.PositionMeters);
        WriteQuaternion(packet, ProtocolConstants.QuaternionOffset, state.DriverSpacePose.OrientationXyzw);
        WriteVector3(packet, ProtocolConstants.LinearVelocityOffset, state.Motion.LinearVelocityMetersPerSecond);
        WriteVector3(packet, ProtocolConstants.AngularVelocityOffset, state.Motion.AngularVelocityRadiansPerSecond);
        WriteUInt32(packet, ProtocolConstants.ButtonsOffset, (uint)state.Input.Buttons);
        WriteUInt32(packet, ProtocolConstants.TouchesOffset, (uint)state.Input.Touches);
        WriteSingle(packet, ProtocolConstants.TriggerOffset, state.Input.Trigger);
        WriteSingle(packet, ProtocolConstants.GripOffset, state.Input.Grip);
        WriteSingle(packet, ProtocolConstants.StickXOffset, state.Input.StickX);
        WriteSingle(packet, ProtocolConstants.StickYOffset, state.Input.StickY);
        WriteSingle(packet, ProtocolConstants.BatteryOffset, state.BatteryLevel);
    }

    private static ProtocolHandState ReadHandState(ReadOnlySpan<byte> packet, ProtocolOrdering ordering)
    {
        if (packet[ProtocolConstants.HandReservedOffset] != 0)
        {
            throw new ProtocolException("Reserved hand-state bits must be zero.");
        }

        return new ProtocolHandState(
            ordering,
            (ProtocolHand)packet[ProtocolConstants.HandOffset],
            (ProtocolPresence)ReadUInt16(packet, ProtocolConstants.PresenceFlagsOffset),
            new ProtocolDriverPose(
                ReadVector3(packet, ProtocolConstants.PositionOffset),
                ReadQuaternion(packet, ProtocolConstants.QuaternionOffset)),
            new ProtocolMotion(
                ReadVector3(packet, ProtocolConstants.LinearVelocityOffset),
                ReadVector3(packet, ProtocolConstants.AngularVelocityOffset)),
            new ProtocolInputState(
                (ProtocolButtons)ReadUInt32(packet, ProtocolConstants.ButtonsOffset),
                (ProtocolTouches)ReadUInt32(packet, ProtocolConstants.TouchesOffset),
                ReadSingle(packet, ProtocolConstants.TriggerOffset),
                ReadSingle(packet, ProtocolConstants.GripOffset),
                ReadSingle(packet, ProtocolConstants.StickXOffset),
                ReadSingle(packet, ProtocolConstants.StickYOffset)),
            ReadSingle(packet, ProtocolConstants.BatteryOffset));
    }

    private static void WriteVector3(Span<byte> packet, int offset, ProtocolVector3 value)
    {
        WriteSingle(packet, offset, value.X);
        WriteSingle(packet, offset + sizeof(float), value.Y);
        WriteSingle(packet, offset + (2 * sizeof(float)), value.Z);
    }

    private static ProtocolVector3 ReadVector3(ReadOnlySpan<byte> packet, int offset) =>
        new(
            ReadSingle(packet, offset),
            ReadSingle(packet, offset + sizeof(float)),
            ReadSingle(packet, offset + (2 * sizeof(float))));

    private static void WriteQuaternion(Span<byte> packet, int offset, ProtocolQuaternion value)
    {
        WriteSingle(packet, offset, value.X);
        WriteSingle(packet, offset + sizeof(float), value.Y);
        WriteSingle(packet, offset + (2 * sizeof(float)), value.Z);
        WriteSingle(packet, offset + (3 * sizeof(float)), value.W);
    }

    private static ProtocolQuaternion ReadQuaternion(ReadOnlySpan<byte> packet, int offset) =>
        new(
            ReadSingle(packet, offset),
            ReadSingle(packet, offset + sizeof(float)),
            ReadSingle(packet, offset + (2 * sizeof(float))),
            ReadSingle(packet, offset + (3 * sizeof(float))));

    private static void WriteUInt16(Span<byte> packet, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(packet[offset..], value);

    private static ushort ReadUInt16(ReadOnlySpan<byte> packet, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet[offset..]);

    private static void WriteUInt32(Span<byte> packet, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(packet[offset..], value);

    private static uint ReadUInt32(ReadOnlySpan<byte> packet, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(packet[offset..]);

    private static void WriteUInt64(Span<byte> packet, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(packet[offset..], value);

    private static ulong ReadUInt64(ReadOnlySpan<byte> packet, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(packet[offset..]);

    private static void WriteSingle(Span<byte> packet, int offset, float value) =>
        WriteUInt32(packet, offset, BitConverter.SingleToUInt32Bits(value));

    private static float ReadSingle(ReadOnlySpan<byte> packet, int offset) =>
        BitConverter.UInt32BitsToSingle(ReadUInt32(packet, offset));
}
