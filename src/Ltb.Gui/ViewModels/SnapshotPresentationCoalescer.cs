using Ltb.App;

namespace Ltb.Gui.ViewModels;

/// <summary>
/// Coalesces only equivalent Active snapshots. State, error, readiness, hand
/// publication, feed-session, and run-generation changes remain immediate.
/// </summary>
internal sealed class SnapshotPresentationCoalescer : IDisposable
{
    public static readonly TimeSpan ActivePresentationInterval = TimeSpan.FromMilliseconds(100);

    private readonly object _sync = new();
    private readonly IGuiTimeSource _timeSource;
    private readonly IGuiDelayScheduler _delayScheduler;
    private readonly Action<long, long, InternalDriverSessionSnapshot> _trailingFlush;
    private long _generation;
    private long _lastPresentationTimestamp;
    private ActiveIdentity? _lastActiveIdentity;
    private bool _hasPresented;
    private PendingSnapshot? _pending;
    private IDisposable? _scheduledFlush;
    private bool _disposed;

    public SnapshotPresentationCoalescer(
        IGuiTimeSource timeSource,
        IGuiDelayScheduler delayScheduler,
        Action<long, long, InternalDriverSessionSnapshot> trailingFlush)
    {
        _timeSource = timeSource ?? throw new ArgumentNullException(nameof(timeSource));
        _delayScheduler = delayScheduler ?? throw new ArgumentNullException(nameof(delayScheduler));
        _trailingFlush = trailingFlush ?? throw new ArgumentNullException(nameof(trailingFlush));
    }

    public void Reset(long generation, InternalDriverSessionSnapshot initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancelPendingLocked();
            MarkPresented(generation, initial, _timeSource.GetTimestamp());
        }
    }

    public bool ShouldPresent(
        long generation,
        long sequence,
        InternalDriverSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            if (_disposed)
            {
                return false;
            }

            if (_hasPresented && generation < _generation)
            {
                return false;
            }

            var now = _timeSource.GetTimestamp();
            var identity = ActiveIdentity.From(snapshot);
            var immediate =
                !_hasPresented ||
                generation != _generation ||
                identity is null ||
                _lastActiveIdentity is null ||
                identity != _lastActiveIdentity;
            var elapsed = _hasPresented
                ? _timeSource.GetElapsedTime(_lastPresentationTimestamp, now)
                : ActivePresentationInterval;

            if (!immediate && elapsed < ActivePresentationInterval)
            {
                _pending = new PendingSnapshot(generation, sequence, snapshot);
                ScheduleFlushLocked(ActivePresentationInterval - elapsed);
                return false;
            }

            CancelPendingLocked();
            MarkPresented(generation, snapshot, now);
            return true;
        }
    }

    public void CancelPending(long generation)
    {
        lock (_sync)
        {
            if (!_disposed && generation == _generation)
            {
                CancelPendingLocked();
            }
        }
    }

    public void Dispose()
    {
        IDisposable? scheduled;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending = null;
            scheduled = _scheduledFlush;
            _scheduledFlush = null;
        }

        scheduled?.Dispose();
    }

    private void ScheduleFlushLocked(TimeSpan delay)
    {
        if (_scheduledFlush is not null)
        {
            return;
        }

        _scheduledFlush = _delayScheduler.Schedule(delay, FlushPending);
    }

    private void FlushPending()
    {
        PendingSnapshot? flush = null;
        IDisposable? completedSchedule;
        lock (_sync)
        {
            completedSchedule = _scheduledFlush;
            _scheduledFlush = null;
            if (!_disposed && _pending is { } pending)
            {
                var now = _timeSource.GetTimestamp();
                var elapsed = _timeSource.GetElapsedTime(_lastPresentationTimestamp, now);
                if (elapsed < ActivePresentationInterval)
                {
                    ScheduleFlushLocked(ActivePresentationInterval - elapsed);
                }
                else
                {
                    flush = pending;
                    _pending = null;
                    MarkPresented(pending.Generation, pending.Snapshot, now);
                }
            }
        }

        completedSchedule?.Dispose();
        if (flush is { } snapshot)
        {
            _trailingFlush(snapshot.Generation, snapshot.Sequence, snapshot.Snapshot);
        }
    }

    private void CancelPendingLocked()
    {
        _pending = null;
        _scheduledFlush?.Dispose();
        _scheduledFlush = null;
    }

    private void MarkPresented(
        long generation,
        InternalDriverSessionSnapshot snapshot,
        long timestamp)
    {
        _generation = generation;
        _lastPresentationTimestamp = timestamp;
        _lastActiveIdentity = ActiveIdentity.From(snapshot);
        _hasPresented = true;
    }

    private sealed record PendingSnapshot(
        long Generation,
        long Sequence,
        InternalDriverSessionSnapshot Snapshot);

    private sealed record ActiveIdentity(
        InternalDriverSessionReadiness Readiness,
        bool RestartRequired,
        string Diagnostic,
        string Remediation,
        HandIdentity Left,
        HandIdentity Right,
        FeedIdentity Feed,
        InternalDriverDriverEvidence? Driver,
        InternalDriverLighthouseHmdEvidence? LighthouseHmd)
    {
        public static ActiveIdentity? From(InternalDriverSessionSnapshot snapshot) =>
            snapshot.State == InternalDriverSessionState.Active
                ? new ActiveIdentity(
                    snapshot.Readiness,
                    snapshot.RestartRequired,
                    snapshot.Diagnostic,
                    snapshot.Remediation,
                    HandIdentity.From(snapshot.Left),
                    HandIdentity.From(snapshot.Right),
                    FeedIdentity.From(snapshot.Feed),
                    snapshot.Driver,
                    snapshot.LighthouseHmd)
                : null;
    }

    private sealed record HandIdentity(
        string? TrackerSerial,
        bool TrackerConnected,
        bool TrackerTracked,
        object MetaReadiness,
        bool MetaInputsValid,
        InternalDriverProfileReadiness ProfileReadiness,
        bool IsPublishing,
        InternalDriverNeutralReason NeutralReason,
        string Diagnostic,
        InternalDriverCalibrationEvidence? Calibration,
        InternalDriverCaptureEvidence? Capture)
    {
        public static HandIdentity From(InternalDriverHandSnapshot hand) => new(
            hand.TrackerSerial,
            hand.TrackerConnected,
            hand.TrackerTracked,
            hand.MetaReadiness,
            hand.MetaInputsValid,
            hand.ProfileReadiness,
            hand.IsPublishing,
            hand.NeutralReason,
            hand.Diagnostic,
            hand.Calibration,
            hand.Capture);
    }

    private sealed record FeedIdentity(
        object Readiness,
        object? SessionId,
        int ReconnectAttempts,
        string? LastError)
    {
        public static FeedIdentity From(InternalDriverFeedSnapshot feed) => new(
            feed.Readiness,
            feed.SessionId,
            feed.ReconnectAttempts,
            feed.LastError);
    }
}
