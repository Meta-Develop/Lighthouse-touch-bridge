namespace Ltb.Protocol;

public static class ProtocolConstants
{
    public const uint Magic = 0x3142544C; // "LTB1" in little-endian byte order.
    public const ushort MajorVersion = 1;
    public const ushort MinorVersion = 0;
    public const int HeaderSize = 16;
    public const int CommonPayloadSize = 32;
    public const int HeartbeatPayloadSize = CommonPayloadSize;
    public const int HeartbeatPacketSize = HeaderSize + HeartbeatPayloadSize;
    public const int HandStatePayloadSize = 116;
    public const int HandStatePacketSize = HeaderSize + HandStatePayloadSize;
    public const float QuaternionNormTolerance = 0.001f;
    public const float AnalogMinimum = 0f;
    public const float AnalogMaximum = 1f;
    public const float StickMinimum = -1f;
    public const float StickMaximum = 1f;
    public const ulong WatchdogTimeoutNanoseconds = 500_000_000;
    public const ProtocolPresence AllowedPresence =
        ProtocolPresence.Connected |
        ProtocolPresence.OrientationValid |
        ProtocolPresence.PositionValid |
        ProtocolPresence.LinearVelocityValid |
        ProtocolPresence.AngularVelocityValid |
        ProtocolPresence.InputsValid |
        ProtocolPresence.Tracked;
    public const ProtocolButtons AllowedButtons = (ProtocolButtons)0x0000001F;
    public const ProtocolTouches AllowedTouches = (ProtocolTouches)0x0000007F;

    public const int MagicOffset = 0;
    public const int MajorVersionOffset = 4;
    public const int MinorVersionOffset = 6;
    public const int MessageTypeOffset = 8;
    public const int HeaderReservedOffset = 10;
    public const int PayloadLengthOffset = 12;
    public const int SessionIdOffset = 16;
    public const int SequenceOffset = 32;
    public const int MonotonicNanosecondsOffset = 40;
    public const int HandOffset = 48;
    public const int HandReservedOffset = 49;
    public const int PresenceFlagsOffset = 50;
    public const int PositionOffset = 52;
    public const int QuaternionOffset = 64;
    public const int LinearVelocityOffset = 80;
    public const int AngularVelocityOffset = 92;
    public const int ButtonsOffset = 104;
    public const int TouchesOffset = 108;
    public const int TriggerOffset = 112;
    public const int GripOffset = 116;
    public const int StickXOffset = 120;
    public const int StickYOffset = 124;
    public const int BatteryOffset = 128;
}

public enum ProtocolMessageType : ushort
{
    HandState = 1,
    Heartbeat = 2,
}

public enum ProtocolHand : byte
{
    Left = 1,
    Right = 2,
}

[Flags]
public enum ProtocolPresence : ushort
{
    None = 0,
    Connected = 1 << 0,
    OrientationValid = 1 << 1,
    PositionValid = 1 << 2,
    LinearVelocityValid = 1 << 3,
    AngularVelocityValid = 1 << 4,
    InputsValid = 1 << 5,
    BatteryPresent = 1 << 6,
    Tracked = 1 << 7,
}

[Flags]
public enum ProtocolButtons : uint
{
    None = 0,
    Primary = 1 << 0,
    Secondary = 1 << 1,
    Menu = 1 << 2,
    ThumbstickClick = 1 << 3,
    TriggerClick = 1 << 4,
}

[Flags]
public enum ProtocolTouches : uint
{
    None = 0,
    Primary = 1 << 0,
    Secondary = 1 << 1,
    Trigger = 1 << 2,
    Thumbstick = 1 << 3,
    ThumbRest = 1 << 4,
    IndexPointing = 1 << 5,
    ThumbUp = 1 << 6,
}
