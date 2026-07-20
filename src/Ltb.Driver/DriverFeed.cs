using Ltb.Protocol;

namespace Ltb.Driver;

public sealed class DriverFeed : IDriverFeed
{
    private readonly IDriverTransportFactory _transportFactory;
    private readonly IDriverFeedClock _clock;
    private readonly IProtocolSessionIdFactory _sessionIdFactory;
    private readonly DriverFeedOptions _options;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _healthGate = new();
    private CancellationTokenSource? _runCancellation;
    private Task? _heartbeatTask;
    private IDriverTransport? _transport;
    private DriverFeedReadiness _readiness = DriverFeedReadiness.Stopped;
    private ProtocolSessionId? _sessionId;
    private ulong _nextSequence;
    private ulong? _lastSuccessfulSequence;
    private ulong? _lastSuccessfulSendNanoseconds;
    private ulong? _startedNanoseconds;
    private int _consecutiveReconnectAttempts;
    private string? _lastError;
    private bool _disposed;

    public DriverFeed(
        IDriverTransportFactory transportFactory,
        IDriverFeedClock? clock = null,
        DriverFeedOptions? options = null,
        IProtocolSessionIdFactory? sessionIdFactory = null)
    {
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _clock = clock ?? new SystemDriverFeedClock();
        _options = options ?? new DriverFeedOptions();
        _options.Validate();
        _sessionIdFactory = sessionIdFactory ?? new RandomProtocolSessionIdFactory();
    }

    public DriverFeedHealth Health
    {
        get
        {
            lock (_healthGate)
            {
                var now = _clock.GetMonotonicNanoseconds();
                var freshnessOrigin = _lastSuccessfulSendNanoseconds ?? _startedNanoseconds;
                var stale = _readiness is DriverFeedReadiness.Connecting or
                        DriverFeedReadiness.Ready or
                        DriverFeedReadiness.Reconnecting or
                        DriverFeedReadiness.Faulted &&
                    freshnessOrigin is { } origin &&
                    ElapsedNanoseconds(now, origin) >= ToNanoseconds(_options.StaleAfter);
                return new DriverFeedHealth(
                    _readiness,
                    stale,
                    _sessionId,
                    _lastSuccessfulSequence,
                    _lastSuccessfulSendNanoseconds,
                    _consecutiveReconnectAttempts,
                    _lastError);
            }
        }
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runCancellation is not null)
            {
                return;
            }

            var runCancellation = new CancellationTokenSource();
            _runCancellation = runCancellation;
            lock (_healthGate)
            {
                _readiness = DriverFeedReadiness.Connecting;
                _startedNanoseconds = _clock.GetMonotonicNanoseconds();
                _lastError = null;
                _consecutiveReconnectAttempts = 0;
            }

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                runCancellation.Token);
            try
            {
                await SendAsync(
                    ordering => new ProtocolHeartbeat(ordering),
                    linkedCancellation.Token).ConfigureAwait(false);
                _heartbeatTask = HeartbeatLoopAsync(runCancellation.Token);
            }
            catch
            {
                runCancellation.Cancel();
                await DisposeTransportAsync().ConfigureAwait(false);
                runCancellation.Dispose();
                _runCancellation = null;
                lock (_healthGate)
                {
                    _readiness = DriverFeedReadiness.Stopped;
                }

                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask PublishAsync(
        DriverHandState state,
        CancellationToken cancellationToken = default)
    {
        var runCancellation = _runCancellation ??
            throw new InvalidOperationException("The driver feed has not been started.");
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            runCancellation.Token);
        await SendAsync(
            ordering => state.ToProtocolMessage(ordering),
            linkedCancellation.Token).ConfigureAwait(false);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var runCancellation = _runCancellation;
            if (runCancellation is null)
            {
                return;
            }

            runCancellation.Cancel();
            if (_heartbeatTask is { } heartbeatTask)
            {
                try
                {
                    await heartbeatTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
                {
                }
            }

            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await DisposeTransportAsync().ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }

            runCancellation.Dispose();
            _runCancellation = null;
            _heartbeatTask = null;
            lock (_healthGate)
            {
                _readiness = DriverFeedReadiness.Stopped;
                _sessionId = null;
                _startedNanoseconds = null;
                _consecutiveReconnectAttempts = 0;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _disposed = true;
        lock (_healthGate)
        {
            _readiness = DriverFeedReadiness.Disposed;
        }

        _lifecycleGate.Dispose();
        _sendGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await _clock.DelayAsync(_options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
                await SendAsync(
                    ordering => new ProtocolHeartbeat(ordering),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (_healthGate)
            {
                _readiness = DriverFeedReadiness.Faulted;
                _lastError = exception.Message;
            }
        }
    }

    private async ValueTask SendAsync(
        Func<ProtocolOrdering, ProtocolMessage> createMessage,
        CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var reconnectDelay = _options.InitialReconnectDelay;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (_transport is null)
                    {
                        await ConnectAsync(cancellationToken).ConfigureAwait(false);
                    }

                    var transport = _transport ?? throw new InvalidOperationException(
                        "The driver transport did not connect.");
                    var timestamp = GetNextMessageTimestamp();
                    var sessionId = _sessionId ?? throw new InvalidOperationException(
                        "The connected transport has no protocol session.");
                    var sequence = _nextSequence;
                    var packet = ProtocolCodec.Encode(createMessage(
                        new ProtocolOrdering(sessionId, sequence, timestamp)));
                    await transport.WriteAsync(packet, cancellationToken).ConfigureAwait(false);

                    lock (_healthGate)
                    {
                        _lastSuccessfulSequence = sequence;
                        _lastSuccessfulSendNanoseconds = timestamp;
                        _consecutiveReconnectAttempts = 0;
                        _lastError = null;
                        _readiness = DriverFeedReadiness.Ready;
                    }

                    _nextSequence = sequence == ulong.MaxValue
                        ? throw new InvalidOperationException("The protocol sequence space is exhausted.")
                        : sequence + 1;
                    return;
                }
                catch (Exception exception) when (IsRecoverableTransportException(exception))
                {
                    lock (_healthGate)
                    {
                        _readiness = DriverFeedReadiness.Reconnecting;
                        _consecutiveReconnectAttempts++;
                        _lastError = exception.Message;
                    }

                    await DisposeTransportAsync().ConfigureAwait(false);
                    await _clock.DelayAsync(reconnectDelay, cancellationToken).ConfigureAwait(false);
                    reconnectDelay = NextReconnectDelay(reconnectDelay);
                }
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        var transport = _transportFactory.Create() ??
            throw new InvalidOperationException("The driver transport factory returned null.");
        try
        {
            await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var sessionId = _sessionIdFactory.Create();
            if (sessionId.IsEmpty)
            {
                throw new InvalidOperationException("The session identifier factory returned an empty identifier.");
            }

            _transport = transport;
            _sessionId = sessionId;
            _nextSequence = 0;
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask DisposeTransportAsync()
    {
        var transport = _transport;
        _transport = null;
        _sessionId = null;
        if (transport is not null)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool IsRecoverableTransportException(Exception exception) =>
        exception is IOException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException;

    private TimeSpan NextReconnectDelay(TimeSpan current)
    {
        var doubledTicks = current.Ticks > (long.MaxValue / 2)
            ? long.MaxValue
            : current.Ticks * 2;
        return TimeSpan.FromTicks(Math.Min(doubledTicks, _options.MaximumReconnectDelay.Ticks));
    }

    private static ulong ElapsedNanoseconds(ulong now, ulong then) => now >= then ? now - then : 0;

    private ulong GetNextMessageTimestamp()
    {
        var timestamp = Math.Max(1UL, _clock.GetMonotonicNanoseconds());
        lock (_healthGate)
        {
            if (_lastSuccessfulSendNanoseconds is not { } lastTimestamp)
            {
                return timestamp;
            }

            if (_nextSequence == 0)
            {
                if (lastTimestamp == ulong.MaxValue)
                {
                    throw new InvalidOperationException("The protocol timestamp space is exhausted.");
                }

                return Math.Max(timestamp, lastTimestamp + 1);
            }

            return Math.Max(timestamp, lastTimestamp);
        }
    }

    private static ulong ToNanoseconds(TimeSpan duration) =>
        checked((ulong)duration.Ticks * 100UL);
}
