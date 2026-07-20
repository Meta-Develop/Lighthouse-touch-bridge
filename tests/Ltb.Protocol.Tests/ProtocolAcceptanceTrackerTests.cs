using Ltb.Protocol;

namespace Ltb.Protocol.Tests;

public sealed class ProtocolAcceptanceTrackerTests
{
    [Fact]
    public void FirstSessionMustStartAtSequenceZero()
    {
        var tracker = new ProtocolAcceptanceTracker();

        var accepted = tracker.TryAccept(
            new ProtocolHeartbeat(ProtocolTestData.Ordering(sequence: 1)),
            out var reason);

        Assert.False(accepted);
        Assert.Equal(ProtocolRejectionReason.NewSessionMustStartAtZero, reason);
        Assert.Null(tracker.Snapshot.SessionId);
    }

    [Fact]
    public void RejectsReplayAndOutOfOrderWithoutChangingAcceptedState()
    {
        var tracker = new ProtocolAcceptanceTracker();
        Assert.True(tracker.TryAccept(
            new ProtocolHeartbeat(ProtocolTestData.Ordering(0, 100)),
            out _));
        Assert.True(tracker.TryAccept(
            ProtocolTestData.HandState(ProtocolTestData.Ordering(2, 200)),
            out _));
        var before = tracker.Snapshot;

        var accepted = tracker.TryAccept(
            ProtocolTestData.HandState(ProtocolTestData.Ordering(1, 150)),
            out var reason);

        Assert.False(accepted);
        Assert.Equal(ProtocolRejectionReason.ReplayedOrOutOfOrderSequence, reason);
        Assert.Equal(before, tracker.Snapshot);
    }

    [Fact]
    public void RejectsRegressingTimestampWithoutChangingAcceptedState()
    {
        var tracker = new ProtocolAcceptanceTracker();
        Assert.True(tracker.TryAccept(
            new ProtocolHeartbeat(ProtocolTestData.Ordering(0, 200)),
            out _));
        var before = tracker.Snapshot;

        var accepted = tracker.TryAccept(
            new ProtocolHeartbeat(ProtocolTestData.Ordering(1, 199)),
            out var reason);

        Assert.False(accepted);
        Assert.Equal(ProtocolRejectionReason.RegressingTimestamp, reason);
        Assert.Equal(before, tracker.Snapshot);
    }

    [Fact]
    public void NewSessionAtZeroResetsPerHandAndHeartbeatState()
    {
        var tracker = new ProtocolAcceptanceTracker();
        Assert.True(tracker.TryAccept(
            ProtocolTestData.HandState(ProtocolTestData.Ordering(0, 100), ProtocolHand.Left),
            out _));
        Assert.True(tracker.TryAccept(
            ProtocolTestData.HandState(ProtocolTestData.Ordering(1, 110), ProtocolHand.Right),
            out _));

        var accepted = tracker.TryAccept(
            new ProtocolHeartbeat(ProtocolTestData.Ordering(0, 200, ProtocolTestData.SessionB)),
            out var reason);

        Assert.True(accepted);
        Assert.Equal(ProtocolRejectionReason.None, reason);
        Assert.Equal(ProtocolTestData.SessionB, tracker.Snapshot.SessionId);
        Assert.Null(tracker.Snapshot.LastLeftHand);
        Assert.Null(tracker.Snapshot.LastRightHand);
        Assert.NotNull(tracker.Snapshot.LastHeartbeat);
    }

    [Fact]
    public void NewSessionRequiresTimestampNewerThanPriorSession()
    {
        var tracker = new ProtocolAcceptanceTracker();
        Assert.True(tracker.TryAccept(
            new ProtocolHeartbeat(ProtocolTestData.Ordering(0, 100)),
            out _));

        var accepted = tracker.TryAccept(
            new ProtocolHeartbeat(ProtocolTestData.Ordering(0, 100, ProtocolTestData.SessionB)),
            out var reason);

        Assert.False(accepted);
        Assert.Equal(ProtocolRejectionReason.RegressingTimestamp, reason);
        Assert.Equal(ProtocolTestData.SessionA, tracker.Snapshot.SessionId);
    }

    [Fact]
    public void RetiredSessionCannotBeReplayedAsAnotherRollover()
    {
        var tracker = new ProtocolAcceptanceTracker();
        Assert.True(tracker.TryAccept(
            new ProtocolHeartbeat(ProtocolTestData.Ordering(0, 100)),
            out _));
        Assert.True(tracker.TryAccept(
            new ProtocolHeartbeat(ProtocolTestData.Ordering(0, 200, ProtocolTestData.SessionB)),
            out _));

        var accepted = tracker.TryAccept(
            new ProtocolHeartbeat(ProtocolTestData.Ordering(0, 300)),
            out var reason);

        Assert.False(accepted);
        Assert.Equal(ProtocolRejectionReason.RetiredSession, reason);
        Assert.Equal(ProtocolTestData.SessionB, tracker.Snapshot.SessionId);
    }

    [Fact]
    public void InvalidLeftPacketDoesNotChangeRightHandState()
    {
        var tracker = new ProtocolAcceptanceTracker();
        Assert.True(tracker.TryAccept(
            ProtocolTestData.HandState(ProtocolTestData.Ordering(0, 100), ProtocolHand.Right),
            out _));
        var rightBefore = tracker.Snapshot.LastRightHand;
        var invalidLeft = ProtocolTestData.HandState(
            ProtocolTestData.Ordering(1, 110),
            ProtocolHand.Left) with
        {
            Input = ProtocolTestData.HandState().Input with { Trigger = float.NaN },
        };

        var accepted = tracker.TryAccept(invalidLeft, out var reason);

        Assert.False(accepted);
        Assert.Equal(ProtocolRejectionReason.InvalidMessage, reason);
        Assert.Equal(rightBefore, tracker.Snapshot.LastRightHand);
        Assert.Null(tracker.Snapshot.LastLeftHand);
    }
}
