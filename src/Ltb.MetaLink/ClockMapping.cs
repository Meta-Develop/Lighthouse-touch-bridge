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
/// first and most recent midpoint define an affine clock model. Invalid clock
/// scale, discontinuities, and non-increasing observations or hand timestamps
/// are rejected rather than hidden by clamping or timestamp adjustment.
/// </summary>
public sealed class MetaClockMapper
{
    private const double MinimumRate = 0.99d;
    private const double MaximumRate = 1.01d;
    private const double MaximumOffsetDiscontinuitySeconds = 0.25d;
    private const double MaximumHandTimestampDistanceSeconds = 5d;
    private const double NanosecondsPerSecond = 1_000_000_000d;
    private readonly object _sync = new();
    private bool _hasObservation;
    private double _firstMeta;
    private double _firstApp;
    private double _latestMeta;
    private double _latestApp;
    private double _estimatedRate = 1d;
    private double _uncertainty;
    private double _leftLastRawMeta = -1d;
    private double _rightLastRawMeta = -1d;
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
                _latestMeta = metaSeconds;
                _latestApp = midpoint;
                _estimatedRate = 1d;
                _uncertainty = uncertainty;
                return;
            }

            var metaDelta = metaSeconds - _latestMeta;
            if (metaDelta <= 0d)
            {
                throw new InvalidOperationException(
                    "Meta clock observations must advance; repeated or regressing observations are rejected.");
            }

            var appDelta = midpoint - _latestApp;
            if (appDelta <= 0d)
            {
                throw new InvalidOperationException(
                    "Application clock observations must advance; repeated or regressing observations are rejected.");
            }

            var intervalRate = appDelta / metaDelta;
            var aggregateRate = (midpoint - _firstApp) / (metaSeconds - _firstMeta);
            if (!double.IsFinite(intervalRate) ||
                !double.IsFinite(aggregateRate) ||
                intervalRate is < MinimumRate or > MaximumRate ||
                aggregateRate is < MinimumRate or > MaximumRate)
            {
                throw new InvalidOperationException(
                    $"Meta clock observation has an implausible scale ({intervalRate:R}); expected {MinimumRate:R} to {MaximumRate:R}.");
            }

            var offsetDiscontinuity = Math.Abs(appDelta - metaDelta);
            if (offsetDiscontinuity > MaximumOffsetDiscontinuitySeconds)
            {
                throw new InvalidOperationException(
                    $"Meta clock observation is discontinuous by {offsetDiscontinuity:R} seconds.");
            }

            _latestMeta = metaSeconds;
            _latestApp = midpoint;
            _estimatedRate = aggregateRate;
            _uncertainty = Math.Max(_uncertainty, uncertainty);
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

            if (Math.Abs(rawMetaSeconds - _latestMeta) > MaximumHandTimestampDistanceSeconds)
            {
                throw new InvalidOperationException(
                    "Per-hand Meta timestamp is discontinuous from the latest accepted clock observation.");
            }

            ref var previousRawMeta = ref hand == MetaLinkHand.Left
                ? ref _leftLastRawMeta
                : ref _rightLastRawMeta;
            if (previousRawMeta >= 0d && rawMetaSeconds <= previousRawMeta)
            {
                throw new InvalidOperationException(
                    $"{hand} Meta timestamp must advance; repeated or regressing timestamps are rejected.");
            }

            var mapped = _latestApp + ((rawMetaSeconds - _latestMeta) * _estimatedRate);
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
            if (nanoseconds <= previous)
            {
                throw new InvalidOperationException(
                    $"{hand} mapped timestamp must advance; repeated or regressing timestamps are rejected.");
            }

            previousRawMeta = rawMetaSeconds;
            previous = nanoseconds;
            return new MetaClockMapping(
                rawMetaSeconds,
                mapped,
                nanoseconds,
                _estimatedRate,
                _uncertainty,
                MonotonicityAdjusted: false);
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
            _estimatedRate = 1d;
            _uncertainty = 0d;
            _leftLastRawMeta = -1d;
            _rightLastRawMeta = -1d;
            _leftLastNanoseconds = -1;
            _rightLastNanoseconds = -1;
        }
    }
}
