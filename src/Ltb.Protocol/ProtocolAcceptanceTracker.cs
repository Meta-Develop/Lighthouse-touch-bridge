namespace Ltb.Protocol;

public enum ProtocolRejectionReason
{
    None = 0,
    InvalidMessage,
    NewSessionMustStartAtZero,
    RetiredSession,
    ReplayedOrOutOfOrderSequence,
    RegressingTimestamp,
}

public readonly record struct ProtocolAcceptedOrder(
    ulong Sequence,
    ulong ProducerMonotonicNanoseconds);

public readonly record struct ProtocolAcceptanceSnapshot(
    ProtocolSessionId? SessionId,
    ProtocolAcceptedOrder? LastAccepted,
    ProtocolAcceptedOrder? LastHeartbeat,
    ProtocolAcceptedOrder? LastLeftHand,
    ProtocolAcceptedOrder? LastRightHand);

/// <summary>
/// Applies global sequence ordering within a session and independent timestamp
/// ordering for heartbeat, left-hand, and right-hand streams.
/// </summary>
public sealed class ProtocolAcceptanceTracker
{
    private const int RetiredSessionLimit = 16;
    private readonly object _sync = new();
    private readonly HashSet<ProtocolSessionId> _retiredSessions = [];
    private readonly Queue<ProtocolSessionId> _retiredSessionOrder = new();
    private ProtocolSessionId? _sessionId;
    private ProtocolAcceptedOrder? _lastAccepted;
    private ProtocolAcceptedOrder? _lastHeartbeat;
    private ProtocolAcceptedOrder? _lastLeftHand;
    private ProtocolAcceptedOrder? _lastRightHand;

    public ProtocolAcceptanceSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return new ProtocolAcceptanceSnapshot(
                    _sessionId,
                    _lastAccepted,
                    _lastHeartbeat,
                    _lastLeftHand,
                    _lastRightHand);
            }
        }
    }

    public bool TryAccept(ProtocolMessage message, out ProtocolRejectionReason rejectionReason)
    {
        try
        {
            ProtocolValidation.Validate(message);
        }
        catch (ProtocolException)
        {
            rejectionReason = ProtocolRejectionReason.InvalidMessage;
            return false;
        }

        lock (_sync)
        {
            var ordering = message.Ordering;
            if (_sessionId != ordering.SessionId)
            {
                if (ordering.Sequence != 0)
                {
                    rejectionReason = ProtocolRejectionReason.NewSessionMustStartAtZero;
                    return false;
                }

                if (_retiredSessions.Contains(ordering.SessionId))
                {
                    rejectionReason = ProtocolRejectionReason.RetiredSession;
                    return false;
                }

                BeginSession(ordering.SessionId);
            }
            else if (_lastAccepted is { } lastAccepted)
            {
                if (ordering.Sequence <= lastAccepted.Sequence)
                {
                    rejectionReason = ProtocolRejectionReason.ReplayedOrOutOfOrderSequence;
                    return false;
                }
            }

            var lastForStream = GetLastForStream(message);
            if (lastForStream is { } lastStreamOrder &&
                ordering.ProducerMonotonicNanoseconds < lastStreamOrder.ProducerMonotonicNanoseconds)
            {
                rejectionReason = ProtocolRejectionReason.RegressingTimestamp;
                return false;
            }

            var accepted = new ProtocolAcceptedOrder(
                ordering.Sequence,
                ordering.ProducerMonotonicNanoseconds);
            _lastAccepted = accepted;
            switch (message)
            {
                case ProtocolHeartbeat:
                    _lastHeartbeat = accepted;
                    break;
                case ProtocolHandState { Hand: ProtocolHand.Left }:
                    _lastLeftHand = accepted;
                    break;
                case ProtocolHandState { Hand: ProtocolHand.Right }:
                    _lastRightHand = accepted;
                    break;
            }

            rejectionReason = ProtocolRejectionReason.None;
            return true;
        }
    }

    private ProtocolAcceptedOrder? GetLastForStream(ProtocolMessage message) =>
        message switch
        {
            ProtocolHeartbeat => _lastHeartbeat,
            ProtocolHandState { Hand: ProtocolHand.Left } => _lastLeftHand,
            ProtocolHandState { Hand: ProtocolHand.Right } => _lastRightHand,
            _ => null,
        };

    private void BeginSession(ProtocolSessionId sessionId)
    {
        if (_sessionId is { } previous)
        {
            _retiredSessions.Add(previous);
            _retiredSessionOrder.Enqueue(previous);
            while (_retiredSessionOrder.Count > RetiredSessionLimit)
            {
                _retiredSessions.Remove(_retiredSessionOrder.Dequeue());
            }
        }

        _sessionId = sessionId;
        _lastAccepted = null;
        _lastHeartbeat = null;
        _lastLeftHand = null;
        _lastRightHand = null;
    }
}
