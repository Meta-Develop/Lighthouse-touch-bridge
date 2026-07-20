using System.Diagnostics;

namespace Ltb.MetaLink;

/// <summary>Injectable application monotonic clock.</summary>
public interface IMetaLinkMonotonicClock
{
    double GetSeconds();
}

/// <summary>Stopwatch-backed process-monotonic clock.</summary>
public sealed class StopwatchMetaLinkClock : IMetaLinkMonotonicClock
{
    public double GetSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}

/// <summary>Result of mapping one raw Meta timestamp into application time.</summary>
public readonly record struct MetaClockMapping(
    double RawMetaSeconds,
    double AppMonotonicSeconds,
    long AppMonotonicNanoseconds,
    double EstimatedRate,
    double UncertaintySeconds,
    bool MonotonicityAdjusted);

/// <summary>
/// Maps the LibOVR clock into the app clock from bracketed observations. The
/// first and most recent midpoint define an affine clock model; rate is bounded
/// to reject transient scheduling delay. Mapped output is strictly monotonic
/// independently for each hand at one-nanosecond resolution.
/// </summary>
public sealed class MetaClockMapper
{
    private const double MinimumRate = 0.99d;
    private const double MaximumRate = 1.01d;
    private const double NanosecondsPerSecond = 1_000_000_000d;
    private readonly object _sync = new();
    private bool _hasObservation;
    private double _firstMeta;
    private double _firstApp;
    private double _latestMeta;
    private double _latestApp;
    private double _uncertainty;
    private long _leftLastNanoseconds = -1;
    private long _rightLastNanoseconds = -1;

    /// <summary>
    /// Adds one paired observation where the Meta reading occurred between the
    /// two app-clock readings. Half the bracket width is retained as uncertainty.
    /// </summary>
    public void Observe(double metaSeconds, double appBeforeSeconds, double appAfterSeconds)
    {
        if (!double.IsFinite(metaSeconds) || metaSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(metaSeconds));
        }

        if (!double.IsFinite(appBeforeSeconds) || appBeforeSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(appBeforeSeconds));
        }

        if (!double.IsFinite(appAfterSeconds) || appAfterSeconds < appBeforeSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(appAfterSeconds));
        }

        var midpoint = appBeforeSeconds + ((appAfterSeconds - appBeforeSeconds) * 0.5d);
        var uncertainty = (appAfterSeconds - appBeforeSeconds) * 0.5d;

        lock (_sync)
        {
            if (!_hasObservation)
            {
                _firstMeta = metaSeconds;
                _firstApp = midpoint;
                _hasObservation = true;
            }

            _latestMeta = metaSeconds;
            _latestApp = midpoint;
            _uncertainty = uncertainty;
        }
    }

    public MetaClockMapping Map(double rawMetaSeconds, MetaLinkHand hand)
    {
        if (!double.IsFinite(rawMetaSeconds) || rawMetaSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(rawMetaSeconds));
        }

        if (!Enum.IsDefined(hand))
        {
            throw new ArgumentOutOfRangeException(nameof(hand));
        }

        lock (_sync)
        {
            if (!_hasObservation)
            {
                throw new InvalidOperationException("At least one bracketed clock observation is required.");
            }

            var metaSpan = _latestMeta - _firstMeta;
            var rate = Math.Abs(metaSpan) > 1e-9d
                ? Math.Clamp((_latestApp - _firstApp) / metaSpan, MinimumRate, MaximumRate)
                : 1d;
            var mapped = _latestApp + ((rawMetaSeconds - _latestMeta) * rate);
            if (!double.IsFinite(mapped) || mapped < 0d || mapped > long.MaxValue / NanosecondsPerSecond)
            {
                throw new InvalidOperationException("Mapped Meta timestamp is outside the app clock range.");
            }

            var nanoseconds = checked((long)Math.Round(
                mapped * NanosecondsPerSecond,
                MidpointRounding.AwayFromZero));
            ref var previous = ref hand == MetaLinkHand.Left
                ? ref _leftLastNanoseconds
                : ref _rightLastNanoseconds;
            var adjusted = nanoseconds <= previous;
            if (adjusted)
            {
                nanoseconds = checked(previous + 1);
                mapped = nanoseconds / NanosecondsPerSecond;
            }

            previous = nanoseconds;
            return new MetaClockMapping(
                rawMetaSeconds,
                mapped,
                nanoseconds,
                rate,
                _uncertainty,
                adjusted);
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _hasObservation = false;
            _firstMeta = 0d;
            _firstApp = 0d;
            _latestMeta = 0d;
            _latestApp = 0d;
            _uncertainty = 0d;
            _leftLastNanoseconds = -1;
            _rightLastNanoseconds = -1;
        }
    }
}
