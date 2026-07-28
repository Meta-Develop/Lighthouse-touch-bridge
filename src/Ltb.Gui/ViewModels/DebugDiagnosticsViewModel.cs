using System.Globalization;
using Ltb.App;
using Ltb.Driver;

namespace Ltb.Gui.ViewModels;

public readonly record struct DiagnosticPoint(double ElapsedSeconds, double? Value);

/// <summary>
/// Session-local, opt-in diagnostic history. It consumes only the same typed
/// snapshots as the normal UI and never polls a runtime or changes calibration.
/// </summary>
public sealed class DebugDiagnosticsViewModel : ObservableObject
{
    public const int MaximumSamples = 600;
    public static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(100);
    public const double WindowSeconds = 60d;
    private const string NoFrozenLagSummary =
        "No completed calibration/profile lag estimate is available in this snapshot.";
    private static readonly IReadOnlyList<DiagnosticPoint> EmptySeries =
        Array.Empty<DiagnosticPoint>();

    private readonly IGuiTimeSource _timeSource;
    private DiagnosticBuffers? _buffers;
    private bool _isEnabled;
    private int _version;
    private long? _runStartedTimestamp;
    private long? _lastSampleTimestamp;
    private bool _hasFrozenLagValues;
    private double? _lastLeftFrozenLag;
    private double? _lastRightFrozenLag;
    private string _frozenLagSummary = NoFrozenLagSummary;

    internal DebugDiagnosticsViewModel(IGuiTimeSource timeSource)
    {
        _timeSource = timeSource ?? throw new ArgumentNullException(nameof(timeSource));
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        internal set
        {
            if (!SetProperty(ref _isEnabled, value))
            {
                return;
            }

            if (value)
            {
                _buffers = new DiagnosticBuffers();
            }
            else
            {
                _buffers?.Clear();
                _buffers = null;
            }

            ResetSessionState();
            NotifySeriesChanged();
        }
    }

    public int Version
    {
        get => _version;
        private set => SetProperty(ref _version, value);
    }

    public IReadOnlyList<DiagnosticPoint> LeftTrackerAge =>
        _buffers?.LeftTrackerAge ?? EmptySeries;

    public IReadOnlyList<DiagnosticPoint> RightTrackerAge =>
        _buffers?.RightTrackerAge ?? EmptySeries;

    public IReadOnlyList<DiagnosticPoint> SendAge => _buffers?.SendAge ?? EmptySeries;

    public IReadOnlyList<DiagnosticPoint> HeartbeatAge =>
        _buffers?.HeartbeatAge ?? EmptySeries;

    public IReadOnlyList<DiagnosticPoint> LeftPublishing =>
        _buffers?.LeftPublishing ?? EmptySeries;

    public IReadOnlyList<DiagnosticPoint> RightPublishing =>
        _buffers?.RightPublishing ?? EmptySeries;

    public IReadOnlyList<DiagnosticPoint> LeftInputValid =>
        _buffers?.LeftInputValid ?? EmptySeries;

    public IReadOnlyList<DiagnosticPoint> RightInputValid =>
        _buffers?.RightInputValid ?? EmptySeries;

    public IReadOnlyList<DiagnosticPoint> FeedReconnecting =>
        _buffers?.FeedReconnecting ?? EmptySeries;

    public IReadOnlyList<DiagnosticPoint> LeftFrozenLag =>
        _buffers?.LeftFrozenLag ?? EmptySeries;

    public IReadOnlyList<DiagnosticPoint> RightFrozenLag =>
        _buffers?.RightFrozenLag ?? EmptySeries;

    public IReadOnlyList<DiagnosticPoint> IterationInterval =>
        _buffers?.IterationInterval ?? EmptySeries;

    public IReadOnlyList<DiagnosticPoint> ObserveDuration =>
        _buffers?.ObserveDuration ?? EmptySeries;

    public IReadOnlyList<DiagnosticPoint> PairPublicationDuration =>
        _buffers?.PairPublicationDuration ?? EmptySeries;

    public IReadOnlyList<DiagnosticPoint> LeftTrackerHostIngressAge =>
        _buffers?.LeftTrackerHostIngressAge ?? EmptySeries;

    public IReadOnlyList<DiagnosticPoint> RightTrackerHostIngressAge =>
        _buffers?.RightTrackerHostIngressAge ?? EmptySeries;

    public string FrozenLagSummary
    {
        get => _frozenLagSummary;
        private set => SetProperty(ref _frozenLagSummary, value);
    }

    public int RetainedSampleCount => _buffers?.LeftTrackerAge.Count ?? 0;

    internal bool HasAllocatedBuffers => _buffers is not null;

    internal void ResetForRun()
    {
        _buffers?.Clear();
        ResetSessionState();
        if (IsEnabled)
        {
            _runStartedTimestamp = _timeSource.GetTimestamp();
        }
    }

    internal bool TrySample(InternalDriverSessionSnapshot snapshot, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!IsEnabled)
        {
            return false;
        }

        var buffers = _buffers ?? throw new InvalidOperationException(
            "Enabled diagnostics must own their sample buffers.");
        var now = _timeSource.GetTimestamp();
        _runStartedTimestamp ??= now;
        if (!force &&
            _lastSampleTimestamp is { } previous &&
            _timeSource.GetElapsedTime(previous, now) < SampleInterval)
        {
            return false;
        }

        _lastSampleTimestamp = now;
        var elapsed = _timeSource.GetElapsedTime(_runStartedTimestamp.Value, now).TotalSeconds;
        Add(buffers.LeftTrackerAge, elapsed, Milliseconds(snapshot.Left.PoseAge));
        Add(buffers.RightTrackerAge, elapsed, Milliseconds(snapshot.Right.PoseAge));
        Add(buffers.SendAge, elapsed, Milliseconds(snapshot.Feed.LastSuccessfulSendAge));
        Add(
            buffers.HeartbeatAge,
            elapsed,
            Milliseconds(snapshot.Feed.LastSuccessfulHeartbeatAge));
        Add(buffers.LeftPublishing, elapsed, snapshot.Left.IsPublishing ? 1d : 0d);
        Add(buffers.RightPublishing, elapsed, snapshot.Right.IsPublishing ? 1d : 0d);
        Add(buffers.LeftInputValid, elapsed, snapshot.Left.MetaInputsValid ? 1d : 0d);
        Add(buffers.RightInputValid, elapsed, snapshot.Right.MetaInputsValid ? 1d : 0d);
        Add(
            buffers.FeedReconnecting,
            elapsed,
            snapshot.Feed.Readiness is DriverFeedReadiness.Reconnecting or DriverFeedReadiness.Faulted
                ? 1d
                : 0d);
        var leftFrozenLag = snapshot.Left.Calibration?.EstimatedLagMilliseconds;
        var rightFrozenLag = snapshot.Right.Calibration?.EstimatedLagMilliseconds;
        Add(buffers.LeftFrozenLag, elapsed, leftFrozenLag);
        Add(buffers.RightFrozenLag, elapsed, rightFrozenLag);
        AddTiming(buffers, snapshot.Timing, elapsed);
        UpdateFrozenLagSummary(leftFrozenLag, rightFrozenLag);
        Version++;
        OnPropertyChanged(nameof(RetainedSampleCount));
        return true;
    }

    private void ResetSessionState()
    {
        _runStartedTimestamp = null;
        _lastSampleTimestamp = null;
        _hasFrozenLagValues = false;
        _lastLeftFrozenLag = null;
        _lastRightFrozenLag = null;
        FrozenLagSummary = NoFrozenLagSummary;
        Version++;
        OnPropertyChanged(nameof(RetainedSampleCount));
    }

    private void NotifySeriesChanged()
    {
        OnPropertyChanged(nameof(LeftTrackerAge));
        OnPropertyChanged(nameof(RightTrackerAge));
        OnPropertyChanged(nameof(SendAge));
        OnPropertyChanged(nameof(HeartbeatAge));
        OnPropertyChanged(nameof(LeftPublishing));
        OnPropertyChanged(nameof(RightPublishing));
        OnPropertyChanged(nameof(LeftInputValid));
        OnPropertyChanged(nameof(RightInputValid));
        OnPropertyChanged(nameof(FeedReconnecting));
        OnPropertyChanged(nameof(LeftFrozenLag));
        OnPropertyChanged(nameof(RightFrozenLag));
        OnPropertyChanged(nameof(IterationInterval));
        OnPropertyChanged(nameof(ObserveDuration));
        OnPropertyChanged(nameof(PairPublicationDuration));
        OnPropertyChanged(nameof(LeftTrackerHostIngressAge));
        OnPropertyChanged(nameof(RightTrackerHostIngressAge));
    }

    private static void AddTiming(
        DiagnosticBuffers buffers,
        InternalDriverTimingSnapshot? timing,
        double elapsed)
    {
        Add(
            buffers.IterationInterval,
            elapsed,
            Milliseconds(timing?.IterationInterval));
        Add(buffers.ObserveDuration, elapsed, Milliseconds(timing?.ObserveDuration));
        Add(
            buffers.PairPublicationDuration,
            elapsed,
            Milliseconds(timing?.PairPublicationDuration));
        Add(
            buffers.LeftTrackerHostIngressAge,
            elapsed,
            Milliseconds(timing?.LeftTrackerHostIngressAgeAtPublish));
        Add(
            buffers.RightTrackerHostIngressAge,
            elapsed,
            Milliseconds(timing?.RightTrackerHostIngressAgeAtPublish));
    }

    private static void Add(
        FixedRingBuffer<DiagnosticPoint> buffer,
        double elapsed,
        double? value) =>
        buffer.Add(new DiagnosticPoint(elapsed, value));

    private static double? Milliseconds(TimeSpan? value) => value?.TotalMilliseconds;

    private void UpdateFrozenLagSummary(double? left, double? right)
    {
        if (_hasFrozenLagValues &&
            left == _lastLeftFrozenLag &&
            right == _lastRightFrozenLag)
        {
            return;
        }

        _hasFrozenLagValues = true;
        _lastLeftFrozenLag = left;
        _lastRightFrozenLag = right;
        FrozenLagSummary = (left, right) switch
        {
            ({ } leftValue, { } rightValue) =>
                $"Left {leftValue.ToString("F1", CultureInfo.InvariantCulture)} ms; " +
                $"Right {rightValue.ToString("F1", CultureInfo.InvariantCulture)} ms. " +
                "Completed profile analysis, frozen; not a live estimator.",
            ({ } leftValue, null) =>
                $"Left {leftValue.ToString("F1", CultureInfo.InvariantCulture)} ms. " +
                "Completed profile analysis, frozen; not a live estimator.",
            (null, { } rightValue) =>
                $"Right {rightValue.ToString("F1", CultureInfo.InvariantCulture)} ms. " +
                "Completed profile analysis, frozen; not a live estimator.",
            _ => NoFrozenLagSummary,
        };
    }

    private sealed class DiagnosticBuffers
    {
        public FixedRingBuffer<DiagnosticPoint> LeftTrackerAge { get; } =
            new(MaximumSamples);
        public FixedRingBuffer<DiagnosticPoint> RightTrackerAge { get; } =
            new(MaximumSamples);
        public FixedRingBuffer<DiagnosticPoint> SendAge { get; } =
            new(MaximumSamples);
        public FixedRingBuffer<DiagnosticPoint> HeartbeatAge { get; } =
            new(MaximumSamples);
        public FixedRingBuffer<DiagnosticPoint> LeftPublishing { get; } =
            new(MaximumSamples);
        public FixedRingBuffer<DiagnosticPoint> RightPublishing { get; } =
            new(MaximumSamples);
        public FixedRingBuffer<DiagnosticPoint> LeftInputValid { get; } =
            new(MaximumSamples);
        public FixedRingBuffer<DiagnosticPoint> RightInputValid { get; } =
            new(MaximumSamples);
        public FixedRingBuffer<DiagnosticPoint> FeedReconnecting { get; } =
            new(MaximumSamples);
        public FixedRingBuffer<DiagnosticPoint> LeftFrozenLag { get; } =
            new(MaximumSamples);
        public FixedRingBuffer<DiagnosticPoint> RightFrozenLag { get; } =
            new(MaximumSamples);
        public FixedRingBuffer<DiagnosticPoint> IterationInterval { get; } =
            new(MaximumSamples);
        public FixedRingBuffer<DiagnosticPoint> ObserveDuration { get; } =
            new(MaximumSamples);
        public FixedRingBuffer<DiagnosticPoint> PairPublicationDuration { get; } =
            new(MaximumSamples);
        public FixedRingBuffer<DiagnosticPoint> LeftTrackerHostIngressAge { get; } =
            new(MaximumSamples);
        public FixedRingBuffer<DiagnosticPoint> RightTrackerHostIngressAge { get; } =
            new(MaximumSamples);

        public void Clear()
        {
            LeftTrackerAge.Clear();
            RightTrackerAge.Clear();
            SendAge.Clear();
            HeartbeatAge.Clear();
            LeftPublishing.Clear();
            RightPublishing.Clear();
            LeftInputValid.Clear();
            RightInputValid.Clear();
            FeedReconnecting.Clear();
            LeftFrozenLag.Clear();
            RightFrozenLag.Clear();
            IterationInterval.Clear();
            ObserveDuration.Clear();
            PairPublicationDuration.Clear();
            LeftTrackerHostIngressAge.Clear();
            RightTrackerHostIngressAge.Clear();
        }
    }
}
