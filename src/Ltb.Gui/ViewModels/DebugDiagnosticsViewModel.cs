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

    private readonly IGuiTimeSource _timeSource;
    private readonly FixedRingBuffer<DiagnosticPoint> _leftTrackerAge = new(MaximumSamples);
    private readonly FixedRingBuffer<DiagnosticPoint> _rightTrackerAge = new(MaximumSamples);
    private readonly FixedRingBuffer<DiagnosticPoint> _sendAge = new(MaximumSamples);
    private readonly FixedRingBuffer<DiagnosticPoint> _heartbeatAge = new(MaximumSamples);
    private readonly FixedRingBuffer<DiagnosticPoint> _leftPublishing = new(MaximumSamples);
    private readonly FixedRingBuffer<DiagnosticPoint> _rightPublishing = new(MaximumSamples);
    private readonly FixedRingBuffer<DiagnosticPoint> _leftInputValid = new(MaximumSamples);
    private readonly FixedRingBuffer<DiagnosticPoint> _rightInputValid = new(MaximumSamples);
    private readonly FixedRingBuffer<DiagnosticPoint> _feedReconnecting = new(MaximumSamples);
    private readonly FixedRingBuffer<DiagnosticPoint> _leftFrozenLag = new(MaximumSamples);
    private readonly FixedRingBuffer<DiagnosticPoint> _rightFrozenLag = new(MaximumSamples);
    private readonly FixedRingBuffer<DiagnosticPoint> _iterationInterval = new(MaximumSamples);
    private readonly FixedRingBuffer<DiagnosticPoint> _observeDuration = new(MaximumSamples);
    private readonly FixedRingBuffer<DiagnosticPoint> _pairPublicationDuration = new(MaximumSamples);
    private readonly FixedRingBuffer<DiagnosticPoint> _leftTrackerHostIngressAge = new(MaximumSamples);
    private readonly FixedRingBuffer<DiagnosticPoint> _rightTrackerHostIngressAge = new(MaximumSamples);
    private bool _isEnabled;
    private int _version;
    private long? _runStartedTimestamp;
    private long? _lastSampleTimestamp;
    private string _frozenLagSummary =
        "No completed calibration/profile lag estimate is available in this snapshot.";

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

            if (!value)
            {
                Clear();
            }
        }
    }

    public int Version
    {
        get => _version;
        private set => SetProperty(ref _version, value);
    }

    public IReadOnlyList<DiagnosticPoint> LeftTrackerAge => _leftTrackerAge;

    public IReadOnlyList<DiagnosticPoint> RightTrackerAge => _rightTrackerAge;

    public IReadOnlyList<DiagnosticPoint> SendAge => _sendAge;

    public IReadOnlyList<DiagnosticPoint> HeartbeatAge => _heartbeatAge;

    public IReadOnlyList<DiagnosticPoint> LeftPublishing => _leftPublishing;

    public IReadOnlyList<DiagnosticPoint> RightPublishing => _rightPublishing;

    public IReadOnlyList<DiagnosticPoint> LeftInputValid => _leftInputValid;

    public IReadOnlyList<DiagnosticPoint> RightInputValid => _rightInputValid;

    public IReadOnlyList<DiagnosticPoint> FeedReconnecting => _feedReconnecting;

    public IReadOnlyList<DiagnosticPoint> LeftFrozenLag => _leftFrozenLag;

    public IReadOnlyList<DiagnosticPoint> RightFrozenLag => _rightFrozenLag;

    public IReadOnlyList<DiagnosticPoint> IterationInterval => _iterationInterval;

    public IReadOnlyList<DiagnosticPoint> ObserveDuration => _observeDuration;

    public IReadOnlyList<DiagnosticPoint> PairPublicationDuration => _pairPublicationDuration;

    public IReadOnlyList<DiagnosticPoint> LeftTrackerHostIngressAge =>
        _leftTrackerHostIngressAge;

    public IReadOnlyList<DiagnosticPoint> RightTrackerHostIngressAge =>
        _rightTrackerHostIngressAge;

    public string FrozenLagSummary
    {
        get => _frozenLagSummary;
        private set => SetProperty(ref _frozenLagSummary, value);
    }

    public int RetainedSampleCount => _leftTrackerAge.Count;

    internal void ResetForRun()
    {
        Clear();
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
        Add(_leftTrackerAge, elapsed, Milliseconds(snapshot.Left.PoseAge));
        Add(_rightTrackerAge, elapsed, Milliseconds(snapshot.Right.PoseAge));
        Add(_sendAge, elapsed, Milliseconds(snapshot.Feed.LastSuccessfulSendAge));
        Add(_heartbeatAge, elapsed, Milliseconds(snapshot.Feed.LastSuccessfulHeartbeatAge));
        Add(_leftPublishing, elapsed, snapshot.Left.IsPublishing ? 1d : 0d);
        Add(_rightPublishing, elapsed, snapshot.Right.IsPublishing ? 1d : 0d);
        Add(_leftInputValid, elapsed, snapshot.Left.MetaInputsValid ? 1d : 0d);
        Add(_rightInputValid, elapsed, snapshot.Right.MetaInputsValid ? 1d : 0d);
        Add(
            _feedReconnecting,
            elapsed,
            snapshot.Feed.Readiness is DriverFeedReadiness.Reconnecting or DriverFeedReadiness.Faulted
                ? 1d
                : 0d);
        Add(_leftFrozenLag, elapsed, snapshot.Left.Calibration?.EstimatedLagMilliseconds);
        Add(_rightFrozenLag, elapsed, snapshot.Right.Calibration?.EstimatedLagMilliseconds);
        AddTiming(snapshot.Timing, elapsed);
        FrozenLagSummary = FormatFrozenLagSummary(snapshot);
        Version++;
        OnPropertyChanged(nameof(RetainedSampleCount));
        return true;
    }

    private void Clear()
    {
        foreach (var buffer in Buffers())
        {
            buffer.Clear();
        }

        _runStartedTimestamp = null;
        _lastSampleTimestamp = null;
        FrozenLagSummary =
            "No completed calibration/profile lag estimate is available in this snapshot.";
        Version++;
        OnPropertyChanged(nameof(RetainedSampleCount));
    }

    private IEnumerable<FixedRingBuffer<DiagnosticPoint>> Buffers()
    {
        yield return _leftTrackerAge;
        yield return _rightTrackerAge;
        yield return _sendAge;
        yield return _heartbeatAge;
        yield return _leftPublishing;
        yield return _rightPublishing;
        yield return _leftInputValid;
        yield return _rightInputValid;
        yield return _feedReconnecting;
        yield return _leftFrozenLag;
        yield return _rightFrozenLag;
        yield return _iterationInterval;
        yield return _observeDuration;
        yield return _pairPublicationDuration;
        yield return _leftTrackerHostIngressAge;
        yield return _rightTrackerHostIngressAge;
    }

    private void AddTiming(InternalDriverTimingSnapshot? timing, double elapsed)
    {
        Add(_iterationInterval, elapsed, Milliseconds(timing?.IterationInterval));
        Add(_observeDuration, elapsed, Milliseconds(timing?.ObserveDuration));
        Add(
            _pairPublicationDuration,
            elapsed,
            Milliseconds(timing?.PairPublicationDuration));
        Add(
            _leftTrackerHostIngressAge,
            elapsed,
            Milliseconds(timing?.LeftTrackerHostIngressAgeAtPublish));
        Add(
            _rightTrackerHostIngressAge,
            elapsed,
            Milliseconds(timing?.RightTrackerHostIngressAgeAtPublish));
    }

    private static void Add(
        FixedRingBuffer<DiagnosticPoint> buffer,
        double elapsed,
        double? value) =>
        buffer.Add(new DiagnosticPoint(elapsed, value));

    private static double? Milliseconds(TimeSpan? value) => value?.TotalMilliseconds;

    private static string FormatFrozenLagSummary(InternalDriverSessionSnapshot snapshot)
    {
        var values = new List<string>(2);
        if (snapshot.Left.Calibration is { } left)
        {
            values.Add(
                $"Left {left.EstimatedLagMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms");
        }

        if (snapshot.Right.Calibration is { } right)
        {
            values.Add(
                $"Right {right.EstimatedLagMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms");
        }

        return values.Count == 0
            ? "No completed calibration/profile lag estimate is available in this snapshot."
            : $"{string.Join("; ", values)}. Completed profile analysis, frozen; not a live estimator.";
    }
}
