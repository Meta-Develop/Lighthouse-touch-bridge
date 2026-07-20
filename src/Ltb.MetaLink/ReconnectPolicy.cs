namespace Ltb.MetaLink;

/// <summary>Injectable retry policy for native runtime/session failures.</summary>
public interface IMetaLinkReconnectPolicy
{
    TimeSpan GetDelay(int consecutiveFailures);
}

/// <summary>Bounded exponential reconnect delay.</summary>
public sealed class ExponentialMetaLinkReconnectPolicy : IMetaLinkReconnectPolicy
{
    public ExponentialMetaLinkReconnectPolicy(
        TimeSpan? initialDelay = null,
        TimeSpan? maximumDelay = null)
    {
        InitialDelay = initialDelay ?? TimeSpan.FromMilliseconds(250);
        MaximumDelay = maximumDelay ?? TimeSpan.FromSeconds(5);
        if (InitialDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initialDelay));
        }

        if (MaximumDelay < InitialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDelay));
        }
    }

    public TimeSpan InitialDelay { get; }

    public TimeSpan MaximumDelay { get; }

    public TimeSpan GetDelay(int consecutiveFailures)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(consecutiveFailures);

        var exponent = Math.Min(consecutiveFailures - 1, 30);
        var ticks = Math.Min(
            MaximumDelay.Ticks,
            InitialDelay.Ticks * Math.Pow(2d, exponent));
        return TimeSpan.FromTicks(checked((long)ticks));
    }
}
