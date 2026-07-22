using Ltb.Driver;
using Ltb.Protocol;

namespace Ltb.Driver.Tests;

[Collection(DriverLifecycleTestGroup.Name)]
public sealed class DriverFeedTests
{
    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
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
        Assert.Equal(clock.GetMonotonicNanoseconds(), heartbeat.Ordering.ProducerMonotonicNanoseconds);
        Assert.Equal(DriverFeedReadiness.Ready, feed.Health.Readiness);
        Assert.False(feed.Health.IsStale);
        Assert.Equal(clock.GetMonotonicNanoseconds(), feed.Health.LastSuccessfulHeartbeatNanoseconds);
    }

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
    public async Task SuccessfulHeartbeatHealthUsesPostWriteMonotonicTime()
    {
        var clock = new ManualDriverFeedClock();
        var transport = new AdvancingWriteDriverTransport(
            () => clock.Advance(TimeSpan.FromMilliseconds(25), releaseDueDelays: false));
        await using var feed = CreateFeed(
            new QueueDriverTransportFactory(transport),
            clock,
            DriverTestData.SessionA);

        await feed.StartAsync();

        var heartbeat = Assert.IsType<ProtocolHeartbeat>(
            ProtocolCodec.Decode(Assert.Single(transport.Packets)));
        Assert.True(
            feed.Health.LastSuccessfulHeartbeatNanoseconds >
            heartbeat.Ordering.ProducerMonotonicNanoseconds);
        Assert.Equal(
            clock.GetMonotonicNanoseconds(),
            feed.Health.LastSuccessfulHeartbeatNanoseconds);
    }

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
    public async Task PublishPreservesFinalPoseSampleTimestampInsteadOfSendTime()
    {
        var transport = new ScriptedDriverTransport();
        var clock = new ManualDriverFeedClock();
        await using var feed = CreateFeed(transport, clock, DriverTestData.SessionA);
        await feed.StartAsync();
        await DriverTestData.WaitUntilAsync(() => clock.PendingDelayCount > 0);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await DriverTestData.WaitUntilAsync(() => transport.Packets.Count == 2);
        clock.SetTime(1_000_000_000);

        await feed.PublishAsync(DriverTestData.State(sampleMonotonicNanoseconds: 123));

        var precedingHeartbeat = Assert.IsType<ProtocolHeartbeat>(
            ProtocolCodec.Decode(transport.Packets.ElementAt(1)));
        var state = Assert.IsType<ProtocolHandState>(
            ProtocolCodec.Decode(transport.Packets.Last()));
        Assert.True(precedingHeartbeat.Ordering.ProducerMonotonicNanoseconds > 123UL);
        Assert.Equal(123UL, state.Ordering.ProducerMonotonicNanoseconds);
        Assert.Equal(1_000_000_000UL, feed.Health.LastSuccessfulSendNanoseconds);
        Assert.Equal(
            precedingHeartbeat.Ordering.ProducerMonotonicNanoseconds,
            feed.Health.LastSuccessfulHeartbeatNanoseconds);
    }

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
    public async Task SuccessfulHandWriteDoesNotAdvanceSuccessfulHeartbeatHealth()
    {
        var transport = new ScriptedDriverTransport();
        var clock = new ManualDriverFeedClock();
        await using var feed = CreateFeed(transport, clock, DriverTestData.SessionA);
        await feed.StartAsync();
        var successfulHeartbeat = feed.Health.LastSuccessfulHeartbeatNanoseconds;
        clock.SetTime(1_000_000_000);

        await feed.PublishAsync(DriverTestData.State());

        Assert.Equal(successfulHeartbeat, feed.Health.LastSuccessfulHeartbeatNanoseconds);
        Assert.Equal(1_000_000_000UL, feed.Health.LastSuccessfulSendNanoseconds);
    }

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
    public async Task HeartbeatUsesSendTimeIndependentOfNewerHandSampleTime()
    {
        var transport = new ScriptedDriverTransport();
        var clock = new ManualDriverFeedClock();
        await using var feed = CreateFeed(transport, clock, DriverTestData.SessionA);
        await feed.StartAsync();
        await DriverTestData.WaitUntilAsync(() => clock.PendingDelayCount > 0);
        await feed.PublishAsync(DriverTestData.State(sampleMonotonicNanoseconds: 500_000_000));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await DriverTestData.WaitUntilAsync(() => transport.Packets.Count == 3);

        var heartbeat = Assert.IsType<ProtocolHeartbeat>(
            ProtocolCodec.Decode(transport.Packets.Last()));
        Assert.Equal(clock.GetMonotonicNanoseconds(), heartbeat.Ordering.ProducerMonotonicNanoseconds);
        Assert.True(heartbeat.Ordering.ProducerMonotonicNanoseconds < 500_000_000UL);
    }

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
    public async Task HandSampleTimestampOrderingIsIndependentPerHand()
    {
        var transport = new ScriptedDriverTransport();
        var clock = new ManualDriverFeedClock();
        await using var feed = CreateFeed(transport, clock, DriverTestData.SessionA);
        await feed.StartAsync();
        await feed.PublishAsync(DriverTestData.State(ProtocolHand.Left, 100));
        await feed.PublishAsync(DriverTestData.State(ProtocolHand.Right, 50));
        await feed.PublishAsync(DriverTestData.State(ProtocolHand.Left, 100));

        await Assert.ThrowsAsync<ProtocolException>(() =>
            feed.PublishAsync(DriverTestData.State(ProtocolHand.Left, 99)).AsTask());
        await Assert.ThrowsAsync<ProtocolException>(() =>
            feed.PublishAsync(DriverTestData.State(ProtocolHand.Right, 49)).AsTask());

        Assert.Equal(4, transport.Packets.Count);
    }

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
    public async Task ZeroHandSampleTimestampIsRejectedBeforeWrite()
    {
        var transport = new ScriptedDriverTransport();
        var clock = new ManualDriverFeedClock();
        await using var feed = CreateFeed(transport, clock, DriverTestData.SessionA);
        await feed.StartAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            feed.PublishAsync(DriverTestData.State(sampleMonotonicNanoseconds: 0)).AsTask());

        Assert.Single(transport.Packets);
    }

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
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

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
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
        Assert.Null(feed.Health.LastSuccessfulHeartbeatNanoseconds);
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
        Assert.Null(feed.Health.LastSuccessfulHeartbeatNanoseconds);
    }

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
    public async Task FailedHeartbeatWriteDoesNotAdvanceSuccessfulHeartbeatHealth()
    {
        var transport = new ScriptedDriverTransport
        {
            FailOnWriteNumber = 2,
            WriteFailure = new ProtocolException("Scripted non-recoverable heartbeat failure."),
        };
        var clock = new ManualDriverFeedClock();
        await using var feed = CreateFeed(transport, clock, DriverTestData.SessionA);
        await feed.StartAsync();
        await DriverTestData.WaitUntilAsync(() => clock.PendingDelayCount > 0);
        var successfulHeartbeat = feed.Health.LastSuccessfulHeartbeatNanoseconds;

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await DriverTestData.WaitUntilAsync(
            () => feed.Health.Readiness == DriverFeedReadiness.Faulted);

        Assert.Equal(successfulHeartbeat, feed.Health.LastSuccessfulHeartbeatNanoseconds);
        Assert.Equal(successfulHeartbeat, feed.Health.LastSuccessfulSendNanoseconds);
        Assert.Single(transport.Packets);
    }

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
    public async Task DisconnectedNamedPipeFailureReconnectsWithNewSessionInsteadOfFaulting()
    {
        var first = new ScriptedDriverTransport
        {
            FailOnWriteNumber = 2,
            WriteFailure = new DriverTransportDisconnectedException(
                "The named pipe disconnected during a driver-feed write."),
        };
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
        Assert.Equal(DriverFeedReadiness.Reconnecting, feed.Health.Readiness);
        clock.Advance(TimeSpan.FromMilliseconds(25));
        await publish;

        var recoveredState = Assert.IsType<ProtocolHandState>(
            ProtocolCodec.Decode(Assert.Single(second.Packets)));
        Assert.Equal(DriverTestData.SessionB, recoveredState.Ordering.SessionId);
        Assert.Equal(0UL, recoveredState.Ordering.Sequence);
        Assert.Equal(DriverFeedReadiness.Ready, feed.Health.Readiness);
    }

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
    public async Task AuthorizationRejectionDuringReconnectRecoversWithFreshSession()
    {
        var first = new ScriptedDriverTransport { FailOnWriteNumber = 2 };
        var rejected = new ScriptedDriverTransport
        {
            ConnectFailure = new DriverPeerAuthorizationException(
                "Named-pipe server authorization failed: server session 1 does not match client session 2."),
        };
        var recovered = new ScriptedDriverTransport();
        var factory = new QueueDriverTransportFactory(first, rejected, recovered);
        var clock = new ManualDriverFeedClock();
        await using var feed = CreateFeed(
            factory,
            clock,
            DriverTestData.SessionA,
            DriverTestData.SessionB);
        await feed.StartAsync();
        await DriverTestData.WaitUntilAsync(() => clock.PendingDelayCount == 1);

        Task publish = feed.PublishAsync(DriverTestData.State()).AsTask();
        await DriverTestData.WaitUntilAsync(() =>
        {
            var health = feed.Health;
            return first.IsDisposed &&
                health.Readiness == DriverFeedReadiness.Reconnecting &&
                health.LastError == "Scripted write failure." &&
                clock.PendingDelayCount == 2;
        });
        clock.Advance(TimeSpan.FromMilliseconds(25));
        await DriverTestData.WaitUntilAsync(() =>
        {
            var health = feed.Health;
            return rejected.IsDisposed &&
                health.Readiness == DriverFeedReadiness.Reconnecting &&
                health.LastError?.Contains("server session 1", StringComparison.Ordinal) == true &&
                clock.PendingDelayCount == 2;
        });
        Assert.Equal(DriverFeedReadiness.Reconnecting, feed.Health.Readiness);
        Assert.Contains("server session 1", feed.Health.LastError, StringComparison.Ordinal);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await publish;

        var firstHeartbeat = Assert.IsType<ProtocolHeartbeat>(
            ProtocolCodec.Decode(Assert.Single(first.Packets)));
        var recoveredState = Assert.IsType<ProtocolHandState>(
            ProtocolCodec.Decode(Assert.Single(recovered.Packets)));
        Assert.Equal(DriverTestData.SessionA, firstHeartbeat.Ordering.SessionId);
        Assert.Equal(DriverTestData.SessionB, recoveredState.Ordering.SessionId);
        Assert.Equal(0UL, recoveredState.Ordering.Sequence);
        Assert.Empty(rejected.Packets);
        Assert.Equal(3, factory.CreateCount);
        Assert.Equal(DriverFeedReadiness.Ready, feed.Health.Readiness);
    }

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
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

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
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

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
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

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
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

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
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

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
    public async Task DisposeCancelsBlockedStartWithoutDeadlock()
    {
        var transport = new ScriptedDriverTransport { BlockConnect = true };
        var clock = new ManualDriverFeedClock();
        var feed = CreateFeed(transport, clock, DriverTestData.SessionA);
        Task start = feed.StartAsync().AsTask();
        await DriverTestTimeouts.AwaitPhaseAsync(
            transport.ConnectStarted,
            "initial transport connect entered");

        Task dispose = feed.DisposeAsync().AsTask();
        try
        {
            await DriverTestTimeouts.AwaitPhaseAsync(
                dispose,
                "dispose cancels blocked initial connect");
        }
        finally
        {
            transport.ReleaseConnect();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.True(transport.IsDisposed);
        Assert.Equal(DriverFeedReadiness.Disposed, feed.Health.Readiness);
    }

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
    public async Task StartQueuedDuringDisposalCannotCreateNewRun()
    {
        var first = new ScriptedDriverTransport { BlockDispose = true };
        var second = new ScriptedDriverTransport();
        var factory = new QueueDriverTransportFactory(first, second);
        var clock = new ManualDriverFeedClock();
        var feed = CreateFeed(
            factory,
            clock,
            DriverTestData.SessionA,
            DriverTestData.SessionB);
        await feed.StartAsync();

        Task dispose = feed.DisposeAsync().AsTask();
        await DriverTestTimeouts.AwaitPhaseAsync(
            first.DisposeStarted,
            "dispose entered transport cleanup");
        Task start = feed.StartAsync().AsTask();
        Task concurrentDispose = feed.DisposeAsync().AsTask();
        try
        {
            Assert.True(
                start.IsCompleted,
                "A start requested after disposal begins must be rejected before waiting on lifecycle cleanup.");
            await Assert.ThrowsAsync<ObjectDisposedException>(() => start);
            Assert.False(dispose.IsCompleted);
        }
        finally
        {
            first.ReleaseDispose();
            await DriverTestTimeouts.AwaitPhaseAsync(dispose, "dispose completed");
            await DriverTestTimeouts.AwaitPhaseAsync(
                concurrentDispose,
                "concurrent dispose completed");
        }

        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, first.DisposeCount);
        Assert.Empty(second.Packets);
        Assert.False(second.IsConnected);
        Assert.Equal(DriverFeedReadiness.Disposed, feed.Health.Readiness);
    }

    [Fact(Timeout = DriverTestTimeouts.TestTimeoutMilliseconds)]
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
        Assert.NotNull(feed.Health.LastSuccessfulHeartbeatNanoseconds);
        await feed.StopAsync();
        Assert.Null(feed.Health.LastSuccessfulHeartbeatNanoseconds);
        await feed.StopAsync();
        clock.Advance(TimeSpan.FromMilliseconds(1), releaseDueDelays: false);
        await feed.StartAsync();

        var restarted = Assert.IsType<ProtocolHeartbeat>(
            ProtocolCodec.Decode(Assert.Single(second.Packets)));
        Assert.Equal(DriverTestData.SessionB, restarted.Ordering.SessionId);
        Assert.Equal(0UL, restarted.Ordering.Sequence);
        Assert.NotEqual(0UL, restarted.Ordering.ProducerMonotonicNanoseconds);
        Assert.Equal(
            restarted.Ordering.ProducerMonotonicNanoseconds,
            feed.Health.LastSuccessfulHeartbeatNanoseconds);
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

    private sealed class AdvancingWriteDriverTransport(Action onWrite) : IDriverTransport
    {
        public Queue<byte[]> Packets { get; } = new();

        public ValueTask ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> packet,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Packets.Enqueue(packet.ToArray());
            onWrite();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }
}
