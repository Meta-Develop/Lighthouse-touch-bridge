using System.Diagnostics;
using Ltb.Core;

namespace Ltb.OpenVr;

/// <summary>
/// Monotonic scheduler used by <see cref="PoseRecordingCapture"/>. The
/// abstraction keeps capture timing deterministic in tests without changing
/// the timestamps supplied by individual pose sources.
/// </summary>
public interface RecordingCaptureClock
{
    double ElapsedSeconds { get; }

    void Restart();

    void WaitUntil(double targetElapsedSeconds);

    void WaitUntil(
        double targetElapsedSeconds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WaitUntil(targetElapsedSeconds);
        cancellationToken.ThrowIfCancellationRequested();
    }
}

/// <summary>Stopwatch-backed capture scheduler for live recording.</summary>
public sealed class StopwatchRecordingCaptureClock : RecordingCaptureClock
{
    private readonly Stopwatch _stopwatch = new();

    public double ElapsedSeconds => _stopwatch.Elapsed.TotalSeconds;

    public void Restart() => _stopwatch.Restart();

    public void WaitUntil(double targetElapsedSeconds) =>
        WaitUntil(targetElapsedSeconds, CancellationToken.None);

    public void WaitUntil(
        double targetElapsedSeconds,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(targetElapsedSeconds) || targetElapsedSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetElapsedSeconds),
                "Capture wait target must be finite and non-negative.");
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingSeconds = targetElapsedSeconds - ElapsedSeconds;
            if (remainingSeconds <= 0d)
            {
                return;
            }

            if (remainingSeconds > 0.002d)
            {
                if (cancellationToken.WaitHandle.WaitOne(
                        TimeSpan.FromSeconds(remainingSeconds - 0.001d)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            else
            {
                Thread.SpinWait(32);
            }
        }
    }
}

/// <summary>Result of one synchronized multi-source pose capture.</summary>
public sealed record PoseRecordingCaptureResult(
    PoseRecording Recording,
    int SamplingTicks,
    double CaptureElapsedSeconds);

/// <summary>One recorded source sample observed after a complete capture tick.</summary>
public readonly record struct PoseRecordingCaptureSample
{
    public PoseRecordingCaptureSample(
        PoseStreamIdentity identity,
        RecordedPoseSample sample)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Sample = sample;
    }

    public PoseStreamIdentity Identity { get; }

    public RecordedPoseSample Sample { get; }
}

/// <summary>
/// Immutable observation emitted after every source has been sampled and
/// appended for one capture tick. <see cref="SamplingTick"/> is one-based.
/// </summary>
public sealed record PoseRecordingCaptureTick
{
    private readonly IReadOnlyList<PoseRecordingCaptureSample> _samples;

    public PoseRecordingCaptureTick(
        int samplingTick,
        double captureElapsedSeconds,
        IEnumerable<PoseRecordingCaptureSample> samples)
    {
        if (samplingTick <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(samplingTick),
                "Capture sampling tick must be greater than zero.");
        }

        if (!double.IsFinite(captureElapsedSeconds) || captureElapsedSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(captureElapsedSeconds),
                "Capture elapsed time must be finite and non-negative.");
        }

        ArgumentNullException.ThrowIfNull(samples);
        var snapshot = samples.ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException(
                "A capture tick observation must contain at least one sample.",
                nameof(samples));
        }

        SamplingTick = samplingTick;
        CaptureElapsedSeconds = captureElapsedSeconds;
        _samples = Array.AsReadOnly(snapshot);
    }

    public int SamplingTick { get; }

    public double CaptureElapsedSeconds { get; }

    public IReadOnlyList<PoseRecordingCaptureSample> Samples => _samples;
}

/// <summary>
/// Runtime-neutral synchronized recorder over the narrow controller and
/// tracked-pose source interfaces.
/// </summary>
public static class PoseRecordingCapture
{
    /// <summary>
    /// Samples every source once at each nominal tick <c>n / sampleRateHz</c>
    /// in the half-open interval <c>[0, durationSeconds)</c>. A tick that is
    /// already at or past the deadline after waiting is dropped. After one
    /// sampling pass, any nominal ticks at or before the newly observed elapsed
    /// time are skipped rather than read in a catch-up burst. The method always
    /// waits through the requested deadline before returning, including when
    /// the period is longer than the duration.
    /// </summary>
    public static PoseRecordingCaptureResult Capture(
        IEnumerable<TrackedPoseSource> trackedPoseSources,
        IEnumerable<InputControllerPoseSource> controllerPoseSources,
        double durationSeconds,
        double sampleRateHz,
        RecordingCaptureClock clock) =>
        Capture(
            trackedPoseSources,
            controllerPoseSources,
            durationSeconds,
            sampleRateHz,
            clock,
            CancellationToken.None);

    /// <summary>
    /// Captures with cooperative cancellation and an optional completed-tick
    /// observer. Cancellation is checked before a tick begins and before and
    /// after scheduler waits, so an observed cancellation never starts another
    /// sampling pass.
    /// </summary>
    public static PoseRecordingCaptureResult Capture(
        IEnumerable<TrackedPoseSource> trackedPoseSources,
        IEnumerable<InputControllerPoseSource> controllerPoseSources,
        double durationSeconds,
        double sampleRateHz,
        RecordingCaptureClock clock,
        CancellationToken cancellationToken,
        Action<PoseRecordingCaptureTick>? observeTick = null)
    {
        ArgumentNullException.ThrowIfNull(trackedPoseSources);
        ArgumentNullException.ThrowIfNull(controllerPoseSources);
        ArgumentNullException.ThrowIfNull(clock);
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationSeconds),
                "Capture duration must be finite and greater than zero.");
        }

        if (!double.IsFinite(sampleRateHz) || sampleRateHz <= 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRateHz),
                "Capture sample rate must be finite and greater than zero.");
        }

        var trackers = SnapshotSources(trackedPoseSources, nameof(trackedPoseSources));
        var controllers = SnapshotSources(controllerPoseSources, nameof(controllerPoseSources));
        if (trackers.Length == 0 || controllers.Length == 0)
        {
            throw new ArgumentException(
                "Capture requires at least one tracked-pose source and one input-controller source.");
        }

        RejectDuplicateDevices(trackers, controllers);

        var streams = trackers
            .Select(source => CaptureStream.ForTracker(source, trackers.Length))
            .Concat(controllers.Select(source => CaptureStream.ForController(source, controllers.Length)))
            .ToArray();

        cancellationToken.ThrowIfCancellationRequested();
        clock.Restart();
        var samplingTicks = 0;
        for (long tick = 0; ;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetSeconds = tick / sampleRateHz;
            if (targetSeconds >= durationSeconds)
            {
                break;
            }

            clock.WaitUntil(targetSeconds, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var elapsedSeconds = clock.ElapsedSeconds;
            if (elapsedSeconds >= durationSeconds)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var observedSamples = new PoseRecordingCaptureSample[streams.Length];
            for (var streamIndex = 0; streamIndex < streams.Length; streamIndex++)
            {
                observedSamples[streamIndex] = streams[streamIndex].ReadAndAppend();
            }

            samplingTicks = checked(samplingTicks + 1);
            elapsedSeconds = clock.ElapsedSeconds;
            observeTick?.Invoke(new PoseRecordingCaptureTick(
                samplingTicks,
                elapsedSeconds,
                observedSamples));
            cancellationToken.ThrowIfCancellationRequested();
            do
            {
                tick = checked(tick + 1);
            }
            while (tick / sampleRateHz <= elapsedSeconds);
        }

        cancellationToken.ThrowIfCancellationRequested();
        clock.WaitUntil(durationSeconds, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new PoseRecordingCaptureResult(
            new PoseRecording(streams.Select(stream => stream.Buffer.Snapshot())),
            samplingTicks,
            clock.ElapsedSeconds);
    }

    private static TSource[] SnapshotSources<TSource>(
        IEnumerable<TSource> sources,
        string parameterName)
        where TSource : class
    {
        var snapshot = sources.ToArray();
        if (snapshot.Any(source => source is null))
        {
            throw new ArgumentException("Capture source lists cannot contain null entries.", parameterName);
        }

        return snapshot;
    }

    private static void RejectDuplicateDevices(
        IReadOnlyList<TrackedPoseSource> trackers,
        IReadOnlyList<InputControllerPoseSource> controllers)
    {
        var duplicateDeviceId = trackers
            .Select(source => source.Device.StableDeviceId)
            .Concat(controllers.Select(source => source.Device.StableDeviceId))
            .GroupBy(deviceId => deviceId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateDeviceId is not null)
        {
            throw new ArgumentException(
                $"Device serial '{duplicateDeviceId}' was selected more than once; each physical device may have only one recording stream.");
        }
    }

    private sealed class CaptureStream
    {
        private readonly Func<PoseSourceSample> _readPose;

        private CaptureStream(PoseStreamIdentity identity, Func<PoseSourceSample> readPose)
        {
            Buffer = new PoseStreamBuffer(identity);
            _readPose = readPose;
        }

        public PoseStreamBuffer Buffer { get; }

        public static CaptureStream ForTracker(TrackedPoseSource source, int trackerCount) =>
            new(
                new PoseStreamIdentity(
                    StreamId("tracker", source.Device.StableDeviceId, trackerCount),
                    PoseSourceKind.TrackedPose,
                    source.Device.StableDeviceId,
                    $"SteamVR {source.Device.Category}"),
                source.ReadPose);

        public static CaptureStream ForController(
            InputControllerPoseSource source,
            int controllerCount) =>
            new(
                new PoseStreamIdentity(
                    StreamId("controller", source.Device.StableDeviceId, controllerCount),
                    PoseSourceKind.InputController,
                    source.Device.StableDeviceId,
                    $"SteamVR {source.Device.ControllerRole} controller"),
                source.ReadPose);

        public PoseRecordingCaptureSample ReadAndAppend()
        {
            var sample = _readPose().ToRecordedPoseSample();
            Buffer.Append(sample);
            return new PoseRecordingCaptureSample(Buffer.Identity, sample);
        }

        private static string StreamId(string prefix, string stableDeviceId, int sourceCount) =>
            sourceCount == 1 ? prefix : $"{prefix}:{stableDeviceId}";
    }
}
