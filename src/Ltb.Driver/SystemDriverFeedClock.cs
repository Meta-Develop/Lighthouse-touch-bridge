using System.Diagnostics;
using Ltb.Protocol;

namespace Ltb.Driver;

public sealed class SystemDriverFeedClock : IDriverFeedClock
{
    public ulong GetMonotonicNanoseconds()
    {
        var ticks = Stopwatch.GetTimestamp();
        if (ticks <= 0)
        {
            return 1;
        }

        var nanoseconds = ((UInt128)(ulong)ticks * 1_000_000_000U) / (ulong)Stopwatch.Frequency;
        return Math.Max(1UL, (ulong)nanoseconds);
    }

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        new(Task.Delay(delay, cancellationToken));
}

public sealed class RandomProtocolSessionIdFactory : IProtocolSessionIdFactory
{
    public ProtocolSessionId Create() => ProtocolSessionId.CreateRandom();
}
