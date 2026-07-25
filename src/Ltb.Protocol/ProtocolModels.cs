using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Ltb.Protocol;

public readonly record struct ProtocolSessionId(ulong Word0, ulong Word1)
{
    public static ProtocolSessionId Empty => default;

    public bool IsEmpty => Word0 == 0 && Word1 == 0;

    public static ProtocolSessionId CreateRandom()
    {
        Span<byte> bytes = stackalloc byte[16];
        ProtocolSessionId sessionId;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            sessionId = new ProtocolSessionId(
                BinaryPrimitives.ReadUInt64LittleEndian(bytes),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]));
        }
        while (sessionId.IsEmpty);

        return sessionId;
    }
}

public readonly record struct ProtocolHeader(
    uint Magic,
    ushort MajorVersion,
    ushort MinorVersion,
    ProtocolMessageType MessageType,
    ushort Reserved,
    uint PayloadLength);

public readonly record struct ProtocolOrdering(
    ProtocolSessionId SessionId,
    ulong Sequence,
    ulong ProducerMonotonicNanoseconds);

public readonly record struct ProtocolVector3(float X, float Y, float Z)
{
    public static ProtocolVector3 Zero => default;
}

public readonly record struct ProtocolQuaternion(float X, float Y, float Z, float W)
{
    public static ProtocolQuaternion Identity => new(0f, 0f, 0f, 1f);
}

/// <summary>
/// An already-composed controller pose in raw OpenVR driver space. Producers
/// sample tracker poses in TrackingUniverseRawAndUncalibrated space; neither
/// this protocol nor the driver feed applies a Standing-space transform.
/// </summary>
public readonly record struct ProtocolDriverPose(
    ProtocolVector3 PositionMeters,
    ProtocolQuaternion OrientationXyzw);

public readonly record struct ProtocolMotion(
    ProtocolVector3 LinearVelocityMetersPerSecond,
    ProtocolVector3 AngularVelocityRadiansPerSecond);

public readonly record struct ProtocolInputState(
    ProtocolButtons Buttons,
    ProtocolTouches Touches,
    float Trigger,
    float Grip,
    float StickX,
    float StickY)
{
    public static ProtocolInputState Neutral => default;
}

public abstract record ProtocolMessage(ProtocolOrdering Ordering)
{
    public abstract ProtocolMessageType MessageType { get; }
}

public sealed record ProtocolHeartbeat(ProtocolOrdering Ordering) : ProtocolMessage(Ordering)
{
    public override ProtocolMessageType MessageType => ProtocolMessageType.Heartbeat;
}

public sealed record ProtocolHandState(
    ProtocolOrdering Ordering,
    ProtocolHand Hand,
    ProtocolPresence Presence,
    ProtocolDriverPose DriverSpacePose,
    ProtocolMotion Motion,
    ProtocolInputState Input,
    float BatteryLevel) : ProtocolMessage(Ordering)
{
    public override ProtocolMessageType MessageType => ProtocolMessageType.HandState;

    public static ProtocolHandState Neutral(
        ProtocolOrdering ordering,
        ProtocolHand hand,
        bool connected = false) =>
        new(
            ordering,
            hand,
            connected ? ProtocolPresence.Connected : ProtocolPresence.None,
            new ProtocolDriverPose(ProtocolVector3.Zero, ProtocolQuaternion.Identity),
            new ProtocolMotion(ProtocolVector3.Zero, ProtocolVector3.Zero),
            ProtocolInputState.Neutral,
            0f);
}
