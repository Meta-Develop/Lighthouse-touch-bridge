using Ltb.Protocol;

namespace Ltb.Protocol.Tests;

internal static class ProtocolTestData
{
    public static ProtocolSessionId SessionA { get; } =
        new(0x0807060504030201, 0x100F0E0D0C0B0A09);

    public static ProtocolSessionId SessionB { get; } =
        new(0x8877665544332211, 0xFFEEDDCCBBAA0099);

    public static ProtocolOrdering Ordering(
        ulong sequence = 0,
        ulong nanoseconds = 0x201F1E1D1C1B1A19,
        ProtocolSessionId? sessionId = null) =>
        new(sessionId ?? SessionA, sequence, nanoseconds);

    public static ProtocolHandState HandState(
        ProtocolOrdering? ordering = null,
        ProtocolHand hand = ProtocolHand.Left) =>
        new(
            ordering ?? Ordering(0x1817161514131211),
            hand,
            ProtocolPresence.Connected |
            ProtocolPresence.OrientationValid |
            ProtocolPresence.PositionValid |
            ProtocolPresence.LinearVelocityValid |
            ProtocolPresence.AngularVelocityValid |
            ProtocolPresence.InputsValid |
            ProtocolPresence.Tracked,
            new ProtocolDriverPose(
                new ProtocolVector3(1f, -2f, 0.5f),
                ProtocolQuaternion.Identity),
            new ProtocolMotion(
                new ProtocolVector3(0.25f, -0.5f, 1.5f),
                new ProtocolVector3(2f, -3f, 4f)),
            new ProtocolInputState(
                ProtocolButtons.Primary | ProtocolButtons.Menu,
                ProtocolTouches.Secondary | ProtocolTouches.ThumbRest,
                0.25f,
                0.75f,
                -0.5f,
                0.5f),
            0f);
}
