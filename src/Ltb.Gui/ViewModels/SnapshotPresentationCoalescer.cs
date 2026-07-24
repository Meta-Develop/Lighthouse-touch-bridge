using Ltb.App;

namespace Ltb.Gui.ViewModels;

/// <summary>
/// Coalesces only equivalent Active snapshots. State, error, readiness, hand
/// publication, feed-session, and run-generation changes remain immediate.
/// </summary>
internal sealed class SnapshotPresentationCoalescer
{
    public static readonly TimeSpan ActivePresentationInterval = TimeSpan.FromMilliseconds(100);

    private readonly IGuiTimeSource _timeSource;
    private long _generation;
    private long _lastPresentationTimestamp;
    private ActiveIdentity? _lastActiveIdentity;
    private bool _hasPresented;

    public SnapshotPresentationCoalescer(IGuiTimeSource timeSource)
    {
        _timeSource = timeSource ?? throw new ArgumentNullException(nameof(timeSource));
    }

    public void Reset(long generation, InternalDriverSessionSnapshot initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _generation = generation;
        _lastPresentationTimestamp = _timeSource.GetTimestamp();
        _lastActiveIdentity = ActiveIdentity.From(initial);
        _hasPresented = true;
    }

    public bool ShouldPresent(long generation, InternalDriverSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var now = _timeSource.GetTimestamp();
        var identity = ActiveIdentity.From(snapshot);
        var immediate =
            !_hasPresented ||
            generation != _generation ||
            identity is null ||
            _lastActiveIdentity is null ||
            identity != _lastActiveIdentity;

        if (!immediate &&
            _timeSource.GetElapsedTime(_lastPresentationTimestamp, now) <
            ActivePresentationInterval)
        {
            return false;
        }

        _generation = generation;
        _lastPresentationTimestamp = now;
        _lastActiveIdentity = identity;
        _hasPresented = true;
        return true;
    }

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
