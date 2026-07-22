using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Ltb.Driver;
using Ltb.Protocol;

namespace Ltb.Driver.Tests;

[CollectionDefinition("Driver transport lifecycle", DisableParallelization = true)]
public sealed class DriverLifecycleTestGroup
{
    public const string Name = "Driver transport lifecycle";
}

internal static class DriverTestTimeouts
{
    public const int TestTimeoutMilliseconds = 30_000;
    public static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(10);

    public static CancellationTokenSource CreatePhaseCancellation() => new(PhaseTimeout);

    public static async Task AwaitPhaseAsync(
        Task task,
        string phase,
        CancellationToken timeoutToken = default,
        [CallerMemberName] string testName = "")
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        WriteDiagnostic(testName, phase, "started");
        using var timeoutCancellation = new CancellationTokenSource();
        var timeoutTask = Task.Delay(PhaseTimeout, timeoutCancellation.Token);
        var completed = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);
        if (completed != task)
        {
            WriteDiagnostic(testName, phase, "timed out");
            throw CreateTimeout(testName, phase);
        }

        timeoutCancellation.Cancel();
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (timeoutToken.IsCancellationRequested)
        {
            WriteDiagnostic(testName, phase, "timed out by cancellation");
            throw CreateTimeout(testName, phase, exception);
        }

        WriteDiagnostic(testName, phase, "completed");
    }

    public static TimeoutException CreateTimeout(
        string testName,
        string phase,
        Exception? innerException = null) =>
        new(
            $"Ltb.Driver.Tests '{testName}' exceeded the {PhaseTimeout.TotalSeconds:0}-second " +
            $"limit during lifecycle phase '{phase}'.",
            innerException);

    public static void WriteDiagnostic(string testName, string phase, string state) =>
        Console.Error.WriteLine(
            $"[Ltb.Driver.Tests] test={testName} phase={phase} state={state}");
}

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
    private readonly TaskCompletionSource _connectStarted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposeRelease = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposeStarted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposeCount;
    private int _writeCount;

    public ConcurrentQueue<byte[]> Packets { get; } = new();

    public bool BlockConnect { get; init; }

    public Exception? ConnectFailure { get; init; }

    public int? FailOnWriteNumber { get; init; }

    public Exception? WriteFailure { get; init; }

    public bool BlockDispose { get; init; }

    public bool IsConnected { get; private set; }

    public bool IsDisposed { get; private set; }

    public Task ConnectStarted => _connectStarted.Task;

    public Task DisposeStarted => _disposeStarted.Task;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public async ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        _connectStarted.TrySetResult();
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

    public void ReleaseConnect() => _connectRelease.TrySetResult();

    public void ReleaseDispose() => _disposeRelease.TrySetResult();

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
            throw WriteFailure ?? new IOException("Scripted write failure.");
        }

        Packets.Enqueue(packet.ToArray());
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        IsConnected = false;
        _connectRelease.TrySetCanceled();
        _disposeStarted.TrySetResult();
        if (BlockDispose)
        {
            await _disposeRelease.Task.ConfigureAwait(false);
        }

        IsDisposed = true;
        GC.SuppressFinalize(this);
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

    public static async Task WaitUntilAsync(
        Func<bool> condition,
        string? phase = null,
        [CallerArgumentExpression(nameof(condition))] string? conditionExpression = null,
        [CallerMemberName] string testName = "")
    {
        ArgumentNullException.ThrowIfNull(condition);
        phase ??= $"wait for {conditionExpression ?? "condition"}";
        DriverTestTimeouts.WriteDiagnostic(testName, phase, "started");
        using var timeout = DriverTestTimeouts.CreatePhaseCancellation();
        try
        {
            while (!condition())
            {
                await Task.Delay(1, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            DriverTestTimeouts.WriteDiagnostic(testName, phase, "timed out");
            throw DriverTestTimeouts.CreateTimeout(testName, phase, exception);
        }

        DriverTestTimeouts.WriteDiagnostic(testName, phase, "completed");
    }
}
