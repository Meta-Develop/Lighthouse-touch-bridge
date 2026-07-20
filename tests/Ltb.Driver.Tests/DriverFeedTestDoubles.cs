using System.Collections.Concurrent;
using Ltb.Driver;
using Ltb.Protocol;

namespace Ltb.Driver.Tests;

internal sealed class ManualDriverFeedClock : IDriverFeedClock
{
    private readonly object _sync = new();
    private readonly List<DelayWaiter> _waiters = [];
    private ulong _nanoseconds = 1;

    public int PendingDelayCount
    {
        get
        {
            lock (_sync)
            {
                return _waiters.Count;
            }
        }
    }

    public ulong GetMonotonicNanoseconds()
    {
        lock (_sync)
        {
            return _nanoseconds;
        }
    }

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        DelayWaiter waiter;
        lock (_sync)
        {
            waiter = new DelayWaiter(
                checked(_nanoseconds + ToNanoseconds(delay)),
                completion);
            _waiters.Add(waiter);
        }

        return new ValueTask(WaitWithCancellationAsync(waiter, cancellationToken));
    }

    public void SetTime(ulong nanoseconds, bool releaseDueDelays = false)
    {
        List<DelayWaiter> due = [];
        lock (_sync)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(nanoseconds, _nanoseconds);

            _nanoseconds = nanoseconds;
            if (releaseDueDelays)
            {
                for (var index = _waiters.Count - 1; index >= 0; index--)
                {
                    if (_waiters[index].DueNanoseconds <= nanoseconds)
                    {
                        due.Add(_waiters[index]);
                        _waiters.RemoveAt(index);
                    }
                }
            }
        }

        foreach (var waiter in due)
        {
            waiter.Completion.TrySetResult();
        }
    }

    public void Advance(TimeSpan duration, bool releaseDueDelays = true) =>
        SetTime(checked(GetMonotonicNanoseconds() + ToNanoseconds(duration)), releaseDueDelays);

    private async Task WaitWithCancellationAsync(
        DelayWaiter waiter,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetCanceled(),
            waiter.Completion);
        try
        {
            await waiter.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _waiters.Remove(waiter);
            }
        }
    }

    private static ulong ToNanoseconds(TimeSpan duration) =>
        checked((ulong)duration.Ticks * 100UL);

    private sealed record DelayWaiter(
        ulong DueNanoseconds,
        TaskCompletionSource Completion);
}

internal sealed class ScriptedDriverTransport : IDriverTransport
{
    private readonly TaskCompletionSource _connectRelease = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int _writeCount;

    public ConcurrentQueue<byte[]> Packets { get; } = new();

    public bool BlockConnect { get; init; }

    public Exception? ConnectFailure { get; init; }

    public int? FailOnWriteNumber { get; init; }

    public bool IsConnected { get; private set; }

    public bool IsDisposed { get; private set; }

    public async ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        if (ConnectFailure is not null)
        {
            throw ConnectFailure;
        }

        if (BlockConnect)
        {
            await _connectRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        IsConnected = true;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConnected)
        {
            throw new IOException("Fake transport is disconnected.");
        }

        var writeNumber = Interlocked.Increment(ref _writeCount);
        if (writeNumber == FailOnWriteNumber)
        {
            IsConnected = false;
            throw new IOException("Scripted write failure.");
        }

        Packets.Enqueue(packet.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        IsConnected = false;
        _connectRelease.TrySetCanceled();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

internal sealed class QueueDriverTransportFactory : IDriverTransportFactory
{
    private readonly Queue<IDriverTransport> _transports;

    public QueueDriverTransportFactory(params IDriverTransport[] transports)
    {
        _transports = new Queue<IDriverTransport>(transports);
    }

    public int CreateCount { get; private set; }

    public IDriverTransport Create()
    {
        CreateCount++;
        if (_transports.Count == 0)
        {
            throw new InvalidOperationException("No scripted transport remains.");
        }

        return _transports.Dequeue();
    }
}

internal sealed class QueueSessionIdFactory : IProtocolSessionIdFactory
{
    private readonly Queue<ProtocolSessionId> _sessionIds;

    public QueueSessionIdFactory(params ProtocolSessionId[] sessionIds)
    {
        _sessionIds = new Queue<ProtocolSessionId>(sessionIds);
    }

    public ProtocolSessionId Create() => _sessionIds.Dequeue();
}

internal static class DriverTestData
{
    public static ProtocolSessionId SessionA { get; } = new(1, 2);

    public static ProtocolSessionId SessionB { get; } = new(3, 4);

    public static DriverHandState State(
        ProtocolHand hand = ProtocolHand.Left,
        ulong sampleMonotonicNanoseconds = 10) =>
        new(
            hand,
            sampleMonotonicNanoseconds,
            ProtocolPresence.Connected |
            ProtocolPresence.OrientationValid |
            ProtocolPresence.PositionValid |
            ProtocolPresence.LinearVelocityValid |
            ProtocolPresence.AngularVelocityValid |
            ProtocolPresence.InputsValid |
            ProtocolPresence.Tracked,
            new ProtocolDriverPose(
                new ProtocolVector3(0.1f, 0.2f, 0.3f),
                ProtocolQuaternion.Identity),
            new ProtocolMotion(ProtocolVector3.Zero, ProtocolVector3.Zero),
            new ProtocolInputState(
                ProtocolButtons.Primary,
                ProtocolTouches.Primary,
                0.25f,
                0.5f,
                -0.25f,
                0.75f),
            0f);

    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(1, timeout.Token).ConfigureAwait(false);
        }
    }
}
