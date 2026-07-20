using Ltb.Driver;
using Ltb.Protocol;

namespace Ltb.Driver.Tests;

public sealed class DriverFeedTests
{
    [Fact]
    public async Task StartSendsSequenceZeroHeartbeatAndBecomesReady()
    {
        var transport = new ScriptedDriverTransport();
        var clock = new ManualDriverFeedClock();
        await using var feed = CreateFeed(transport, clock, DriverTestData.SessionA);

        await feed.StartAsync();

        var packet = Assert.Single(transport.Packets);
        var heartbeat = Assert.IsType<ProtocolHeartbeat>(ProtocolCodec.Decode(packet));
        Assert.Equal(DriverTestData.SessionA, heartbeat.Ordering.SessionId);
        Assert.Equal(0UL, heartbeat.Ordering.Sequence);
        Assert.Equal(DriverFeedReadiness.Ready, feed.Health.Readiness);
        Assert.False(feed.Health.IsStale);
    }

    [Fact]
    public async Task PublishUsesOneGlobalIncreasingSequenceAcrossHands()
    {
        var transport = new ScriptedDriverTransport();
        var clock = new ManualDriverFeedClock();
        await using var feed = CreateFeed(transport, clock, DriverTestData.SessionA);
        await feed.StartAsync();

        await feed.PublishAsync(DriverTestData.State(ProtocolHand.Left));
        clock.Advance(TimeSpan.FromMilliseconds(1), releaseDueDelays: false);
        await feed.PublishAsync(DriverTestData.State(ProtocolHand.Right));

        var messages = transport.Packets.Select(packet => ProtocolCodec.Decode(packet)).ToArray();
        Assert.Collection(
            messages,
            first => Assert.Equal(0UL, first.Ordering.Sequence),
            second =>
            {
                var state = Assert.IsType<ProtocolHandState>(second);
                Assert.Equal(ProtocolHand.Left, state.Hand);
                Assert.Equal(1UL, state.Ordering.Sequence);
            },
            third =>
            {
                var state = Assert.IsType<ProtocolHandState>(third);
                Assert.Equal(ProtocolHand.Right, state.Hand);
                Assert.Equal(2UL, state.Ordering.Sequence);
            });
    }

    [Fact]
    public async Task RecoverableWriteFailureReconnectsWithNewSessionAtSequenceZero()
    {
        var first = new ScriptedDriverTransport { FailOnWriteNumber = 2 };
        var second = new ScriptedDriverTransport();
        var factory = new QueueDriverTransportFactory(first, second);
        var clock = new ManualDriverFeedClock();
        await using var feed = CreateFeed(
            factory,
            clock,
            DriverTestData.SessionA,
            DriverTestData.SessionB);
        await feed.StartAsync();

        var publish = feed.PublishAsync(DriverTestData.State()).AsTask();
        await DriverTestData.WaitUntilAsync(() => first.IsDisposed && clock.PendingDelayCount > 0);
        clock.Advance(TimeSpan.FromMilliseconds(25));
        await publish;

        var firstHeartbeat = Assert.IsType<ProtocolHeartbeat>(
            ProtocolCodec.Decode(Assert.Single(first.Packets)));
        var recoveredState = Assert.IsType<ProtocolHandState>(
            ProtocolCodec.Decode(Assert.Single(second.Packets)));
        Assert.Equal(DriverTestData.SessionA, firstHeartbeat.Ordering.SessionId);
        Assert.Equal(0UL, firstHeartbeat.Ordering.Sequence);
        Assert.Equal(DriverTestData.SessionB, recoveredState.Ordering.SessionId);
        Assert.Equal(0UL, recoveredState.Ordering.Sequence);
        Assert.Equal(2, factory.CreateCount);
        Assert.Equal(DriverFeedReadiness.Ready, feed.Health.Readiness);
    }

    [Fact]
    public async Task HeartbeatContinuesWhenHandStateDoesNotChange()
    {
        var transport = new ScriptedDriverTransport();
        var clock = new ManualDriverFeedClock();
        await using var feed = CreateFeed(transport, clock, DriverTestData.SessionA);
        await feed.StartAsync();
        await DriverTestData.WaitUntilAsync(() => clock.PendingDelayCount > 0);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await DriverTestData.WaitUntilAsync(() => transport.Packets.Count == 2);

        var messages = transport.Packets.Select(packet => ProtocolCodec.Decode(packet)).ToArray();
        Assert.All(messages, message => Assert.IsType<ProtocolHeartbeat>(message));
        Assert.Equal(new ulong[] { 0, 1 }, messages.Select(message => message.Ordering.Sequence));
    }

    [Fact]
    public async Task HealthBecomesStaleAfterFiveHundredMillisecondsWithoutSend()
    {
        var transport = new ScriptedDriverTransport();
        var clock = new ManualDriverFeedClock();
        await using var feed = CreateFeed(transport, clock, DriverTestData.SessionA);
        await feed.StartAsync();
        await DriverTestData.WaitUntilAsync(() => clock.PendingDelayCount > 0);

        clock.Advance(TimeSpan.FromMilliseconds(500), releaseDueDelays: false);

        Assert.True(feed.Health.IsStale);
        clock.SetTime(clock.GetMonotonicNanoseconds(), releaseDueDelays: true);
        await DriverTestData.WaitUntilAsync(() => transport.Packets.Count == 2);
        Assert.False(feed.Health.IsStale);
    }

    [Fact]
    public async Task InvalidStateDoesNotWriteOrAdvanceSequence()
    {
        var transport = new ScriptedDriverTransport();
        var clock = new ManualDriverFeedClock();
        await using var feed = CreateFeed(transport, clock, DriverTestData.SessionA);
        await feed.StartAsync();
        var invalid = DriverTestData.State() with
        {
            Input = DriverTestData.State().Input with { Trigger = float.NaN },
        };

        await Assert.ThrowsAsync<ProtocolException>(() => feed.PublishAsync(invalid).AsTask());
        await feed.PublishAsync(DriverTestData.State());

        Assert.Equal(2, transport.Packets.Count);
        var state = Assert.IsType<ProtocolHandState>(
            ProtocolCodec.Decode(transport.Packets.Last()));
        Assert.Equal(1UL, state.Ordering.Sequence);
    }

    [Fact]
    public async Task CanceledStartDisposesBlockedTransportAndReturnsToStopped()
    {
        var transport = new ScriptedDriverTransport { BlockConnect = true };
        var clock = new ManualDriverFeedClock();
        await using var feed = CreateFeed(transport, clock, DriverTestData.SessionA);
        using var cancellation = new CancellationTokenSource();

        var start = feed.StartAsync(cancellation.Token).AsTask();
        await DriverTestData.WaitUntilAsync(() => !start.IsCompleted);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.True(transport.IsDisposed);
        Assert.Equal(DriverFeedReadiness.Stopped, feed.Health.Readiness);
    }

    [Fact]
    public async Task DisposeCancelsReconnectAndBlockedPublishWithoutDeadlock()
    {
        var first = new ScriptedDriverTransport { FailOnWriteNumber = 2 };
        var second = new ScriptedDriverTransport { BlockConnect = true };
        var factory = new QueueDriverTransportFactory(first, second);
        var clock = new ManualDriverFeedClock();
        var feed = CreateFeed(
            factory,
            clock,
            DriverTestData.SessionA,
            DriverTestData.SessionB);
        await feed.StartAsync();
        var publish = feed.PublishAsync(DriverTestData.State()).AsTask();
        await DriverTestData.WaitUntilAsync(() => clock.PendingDelayCount > 0);
        clock.Advance(TimeSpan.FromMilliseconds(25));
        await DriverTestData.WaitUntilAsync(() => factory.CreateCount == 2);

        await feed.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publish);
        Assert.True(first.IsDisposed);
        Assert.True(second.IsDisposed);
        Assert.Equal(DriverFeedReadiness.Disposed, feed.Health.Readiness);
    }

    [Fact]
    public async Task StopIsIdempotentAndStartCanCreateFreshSession()
    {
        var first = new ScriptedDriverTransport();
        var second = new ScriptedDriverTransport();
        var factory = new QueueDriverTransportFactory(first, second);
        var clock = new ManualDriverFeedClock();
        await using var feed = CreateFeed(
            factory,
            clock,
            DriverTestData.SessionA,
            DriverTestData.SessionB);

        await feed.StartAsync();
        await feed.StopAsync();
        await feed.StopAsync();
        await feed.StartAsync();

        var restarted = Assert.IsType<ProtocolHeartbeat>(
            ProtocolCodec.Decode(Assert.Single(second.Packets)));
        Assert.Equal(DriverTestData.SessionB, restarted.Ordering.SessionId);
        Assert.Equal(0UL, restarted.Ordering.Sequence);
        var firstStart = Assert.IsType<ProtocolHeartbeat>(
            ProtocolCodec.Decode(Assert.Single(first.Packets)));
        Assert.True(
            restarted.Ordering.ProducerMonotonicNanoseconds >
            firstStart.Ordering.ProducerMonotonicNanoseconds);
    }

    private static DriverFeed CreateFeed(
        ScriptedDriverTransport transport,
        ManualDriverFeedClock clock,
        params ProtocolSessionId[] sessionIds) =>
        CreateFeed(new QueueDriverTransportFactory(transport), clock, sessionIds);

    private static DriverFeed CreateFeed(
        IDriverTransportFactory factory,
        ManualDriverFeedClock clock,
        params ProtocolSessionId[] sessionIds) =>
        new(
            factory,
            clock,
            new DriverFeedOptions
            {
                HeartbeatInterval = TimeSpan.FromMilliseconds(100),
                StaleAfter = TimeSpan.FromMilliseconds(500),
                InitialReconnectDelay = TimeSpan.FromMilliseconds(25),
                MaximumReconnectDelay = TimeSpan.FromMilliseconds(100),
            },
            new QueueSessionIdFactory(sessionIds));
}
