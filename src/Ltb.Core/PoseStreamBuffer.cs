namespace Ltb.Core;

/// <summary>
/// Thread-safe append-only buffer for one strictly monotonic pose stream.
/// Snapshotting copies the current samples into an immutable recording stream.
/// </summary>
public sealed class PoseStreamBuffer
{
    private readonly object _sync = new();
    private readonly List<RecordedPoseSample> _samples = [];

    public PoseStreamBuffer(PoseStreamIdentity identity)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    public PoseStreamIdentity Identity { get; }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _samples.Count;
            }
        }
    }

    public void Append(RecordedPoseSample sample)
    {
        lock (_sync)
        {
            if (!sample.Pose.IsValid)
            {
                throw new ArgumentException("Buffered sample must contain a valid pose.", nameof(sample));
            }

            if (_samples.Count > 0 &&
                sample.MonotonicHostTimeSeconds <= _samples[^1].MonotonicHostTimeSeconds)
            {
                throw new ArgumentException(
                    $"Stream '{Identity.StreamId}' host timestamps must increase strictly.",
                    nameof(sample));
            }

            _samples.Add(sample);
        }
    }

    public PoseStreamRecording Snapshot()
    {
        lock (_sync)
        {
            return new PoseStreamRecording(Identity, _samples);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _samples.Clear();
        }
    }
}
