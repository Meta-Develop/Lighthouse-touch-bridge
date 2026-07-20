using Ltb.Protocol;

namespace Ltb.Driver;

public interface IDriverFeed : IAsyncDisposable
{
    DriverFeedHealth Health { get; }

    ValueTask StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a final hand state while preserving its mapped pose-sample
    /// timestamp. Samples must be non-regressing independently for each hand
    /// within the current protocol session.
    /// </summary>
    ValueTask PublishAsync(DriverHandState state, CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);
}

public interface IDriverTransport : IAsyncDisposable
{
    ValueTask ConnectAsync(CancellationToken cancellationToken);

    ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken);
}

public interface IDriverTransportFactory
{
    IDriverTransport Create();
}

public interface IDriverFeedClock
{
    ulong GetMonotonicNanoseconds();

    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public interface IProtocolSessionIdFactory
{
    ProtocolSessionId Create();
}
