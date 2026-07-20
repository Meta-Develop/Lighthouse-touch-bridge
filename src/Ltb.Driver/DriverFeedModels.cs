using Ltb.Protocol;

namespace Ltb.Driver;

public enum DriverFeedReadiness
{
    Stopped = 0,
    Connecting,
    Ready,
    Reconnecting,
    Faulted,
    Disposed,
}

public sealed record DriverFeedOptions
{
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan StaleAfter { get; init; } =
        TimeSpan.FromTicks((long)(ProtocolConstants.WatchdogTimeoutNanoseconds / 100));

    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromMilliseconds(25);

    public TimeSpan MaximumReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    internal void Validate()
    {
        if (HeartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(HeartbeatInterval));
        }

        if (StaleAfter <= HeartbeatInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(StaleAfter),
                "The stale timeout must be longer than the heartbeat interval.");
        }

        if (InitialReconnectDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialReconnectDelay));
        }

        if (MaximumReconnectDelay < InitialReconnectDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumReconnectDelay),
                "The maximum reconnect delay must not be shorter than the initial delay.");
        }
    }
}

public readonly record struct DriverHandState(
    ProtocolHand Hand,
    ProtocolPresence Presence,
    ProtocolDriverPose DriverSpacePose,
    ProtocolMotion Motion,
    ProtocolInputState Input,
    float BatteryLevel)
{
    internal ProtocolHandState ToProtocolMessage(ProtocolOrdering ordering) =>
        new(ordering, Hand, Presence, DriverSpacePose, Motion, Input, BatteryLevel);

    public static DriverHandState Neutral(ProtocolHand hand, bool connected = false) =>
        new(
            hand,
            connected ? ProtocolPresence.Connected : ProtocolPresence.None,
            new ProtocolDriverPose(ProtocolVector3.Zero, ProtocolQuaternion.Identity),
            new ProtocolMotion(ProtocolVector3.Zero, ProtocolVector3.Zero),
            ProtocolInputState.Neutral,
            0f);
}

public readonly record struct DriverFeedHealth(
    DriverFeedReadiness Readiness,
    bool IsStale,
    ProtocolSessionId? SessionId,
    ulong? LastSuccessfulSequence,
    ulong? LastSuccessfulSendNanoseconds,
    int ConsecutiveReconnectAttempts,
    string? LastError);
