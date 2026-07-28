using Ltb.App;
using Ltb.Driver;
using Ltb.MetaLink;
using Ltb.Protocol;

namespace Ltb.Gui.ViewModels;

/// <summary>
/// Coalesces only equivalent Active snapshots. State, error, readiness, hand
/// publication, feed-session, and run-generation changes remain immediate.
/// </summary>
internal sealed class SnapshotPresentationCoalescer : IDisposable
{
    public static readonly TimeSpan ActivePresentationInterval = TimeSpan.FromMilliseconds(100);

    private readonly object _sync = new();
    private readonly object _trailingCallbackGate = new();
    private readonly IGuiTimeSource _timeSource;
    private readonly IGuiDelayScheduler _delayScheduler;
    private readonly Action<long, long, InternalDriverSessionSnapshot> _trailingFlush;
    private long _generation;
    private long _lastPresentationTimestamp;
    private ActiveIdentity _lastActiveIdentity;
    private bool _hasLastActiveIdentity;
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
            var hasActiveIdentity = ActiveIdentity.TryCreate(snapshot, out var identity);
            var immediate =
                !_hasPresented ||
                generation != _generation ||
                !hasActiveIdentity ||
                !_hasLastActiveIdentity ||
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
        IDisposable? scheduled = null;
        lock (_trailingCallbackGate)
        {
            // Disposal and trailing-callback entry are linearized by this
            // gate. The production callback only posts to the UI dispatcher,
            // so the UI thread never waits for UI work while holding it.
            // Monitor reentrancy also lets a callback dispose itself.
            lock (_sync)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _pending = null;
                    scheduled = _scheduledFlush;
                    _scheduledFlush = null;
                }
            }
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
            lock (_trailingCallbackGate)
            {
                lock (_sync)
                {
                    if (_disposed)
                    {
                        return;
                    }
                }

                _trailingFlush(snapshot.Generation, snapshot.Sequence, snapshot.Snapshot);
            }
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
        _hasLastActiveIdentity = ActiveIdentity.TryCreate(
            snapshot,
            out _lastActiveIdentity);
        _hasPresented = true;
    }

    internal static bool HasEquivalentActivePresentationState(
        InternalDriverSessionSnapshot left,
        InternalDriverSessionSnapshot right) =>
        ActiveIdentity.TryCreate(left, out var leftIdentity) &&
        ActiveIdentity.TryCreate(right, out var rightIdentity) &&
        leftIdentity == rightIdentity;

    private readonly record struct PendingSnapshot(
        long Generation,
        long Sequence,
        InternalDriverSessionSnapshot Snapshot);

    private readonly record struct ActiveIdentity(
        InternalDriverSessionReadiness Readiness,
        bool RestartRequired,
        string Diagnostic,
        string Remediation,
        HandIdentity Left,
        HandIdentity Right,
        FeedIdentity Feed,
        InternalDriverDriverEvidence? Driver,
        InternalDriverLighthouseHmdEvidence? LighthouseHmd,
        TrackerNeutralizationIdentity? TrackerNeutralization,
        ManualBindingVerificationIdentity? ManualBindingVerification)
    {
        public static bool TryCreate(
            InternalDriverSessionSnapshot snapshot,
            out ActiveIdentity identity)
        {
            if (snapshot.State != InternalDriverSessionState.Active)
            {
                identity = default;
                return false;
            }

            identity = new ActiveIdentity(
                snapshot.Readiness,
                snapshot.RestartRequired,
                snapshot.Diagnostic,
                snapshot.Remediation,
                HandIdentity.From(snapshot.Left),
                HandIdentity.From(snapshot.Right),
                FeedIdentity.From(snapshot.Feed),
                snapshot.Driver,
                snapshot.LighthouseHmd,
                TrackerNeutralizationIdentity.From(snapshot.TrackerNeutralization),
                ManualBindingVerificationIdentity.From(
                    snapshot.ManualBindingVerification));
            return true;
        }
    }

    private readonly record struct HandIdentity(
        string? TrackerSerial,
        bool TrackerConnected,
        bool TrackerTracked,
        MetaLinkReadiness MetaReadiness,
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

    private readonly record struct FeedIdentity(
        DriverFeedReadiness Readiness,
        ProtocolSessionId? SessionId,
        int ReconnectAttempts,
        string? LastError)
    {
        public static FeedIdentity From(InternalDriverFeedSnapshot feed) => new(
            feed.Readiness,
            feed.SessionId,
            feed.ReconnectAttempts,
            feed.LastError);
    }

    private readonly record struct ManualBindingVerificationIdentity(
        InternalDriverManualBindingVerificationState State,
        string LeftTrackerSerial,
        string RightTrackerSerial,
        string? CorrectionLeftTrackerSerial,
        string? CorrectionRightTrackerSerial,
        string Diagnostic)
    {
        public static ManualBindingVerificationIdentity? From(
            InternalDriverManualBindingVerificationEvidence? verification) =>
            verification is null
                ? null
                : new ManualBindingVerificationIdentity(
                    verification.State,
                    verification.LeftTrackerSerial,
                    verification.RightTrackerSerial,
                    verification.CorrectionLeftTrackerSerial,
                    verification.CorrectionRightTrackerSerial,
                    verification.Diagnostic);
    }

    /// <summary>
    /// Retains the immutable App snapshot and compares its list members by
    /// content. The lists contain at most the exact controlled tracker pair
    /// plus restoration diagnostics, so this avoids both identity allocations
    /// and reference-only list equality.
    /// </summary>
    private readonly struct TrackerNeutralizationIdentity :
        IEquatable<TrackerNeutralizationIdentity>
    {
        private readonly InternalDriverTrackerNeutralizationSnapshot _snapshot;

        private TrackerNeutralizationIdentity(
            InternalDriverTrackerNeutralizationSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public static TrackerNeutralizationIdentity? From(
            InternalDriverTrackerNeutralizationSnapshot? snapshot) =>
            snapshot is null ? null : new TrackerNeutralizationIdentity(snapshot);

        public bool Equals(TrackerNeutralizationIdentity other)
        {
            if (ReferenceEquals(_snapshot, other._snapshot))
            {
                return true;
            }

            return
                _snapshot.State == other._snapshot.State &&
                string.Equals(
                    _snapshot.BackendSnapshotId,
                    other._snapshot.BackendSnapshotId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    _snapshot.Diagnostic,
                    other._snapshot.Diagnostic,
                    StringComparison.Ordinal) &&
                TrackerPathsEqual(
                    _snapshot.TrackerPaths,
                    other._snapshot.TrackerPaths) &&
                StringsEqual(
                    _snapshot.RestoreFailures,
                    other._snapshot.RestoreFailures);
        }

        public override bool Equals(object? obj) =>
            obj is TrackerNeutralizationIdentity other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(
                _snapshot.State,
                _snapshot.BackendSnapshotId,
                _snapshot.Diagnostic,
                _snapshot.TrackerPaths.Count,
                _snapshot.RestoreFailures.Count);

        public static bool operator ==(
            TrackerNeutralizationIdentity left,
            TrackerNeutralizationIdentity right) =>
            left.Equals(right);

        public static bool operator !=(
            TrackerNeutralizationIdentity left,
            TrackerNeutralizationIdentity right) =>
            !left.Equals(right);

        private static bool TrackerPathsEqual(
            IReadOnlyList<InternalDriverTrackerPath> left,
            IReadOnlyList<InternalDriverTrackerPath> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (var index = 0; index < left.Count; index++)
            {
                var leftPath = left[index];
                var rightPath = right[index];
                if (leftPath.Hand != rightPath.Hand ||
                    !string.Equals(
                        leftPath.TrackerSerial,
                        rightPath.TrackerSerial,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        leftPath.DevicePath,
                        rightPath.DevicePath,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool StringsEqual(
            IReadOnlyList<string> left,
            IReadOnlyList<string> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (var index = 0; index < left.Count; index++)
            {
                if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
