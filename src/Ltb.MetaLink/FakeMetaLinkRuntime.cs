namespace Ltb.MetaLink;

/// <summary>
/// Deterministic, thread-safe Meta Link fake. Enqueued observations are returned
/// once in order; the final observation repeats when the queue is empty.
/// </summary>
public sealed class FakeMetaLinkRuntime : IMetaLinkRuntime, IMetaLinkControllerSource
{
    private readonly object _sync = new();
    private readonly Queue<MetaLinkRuntimeSnapshot> _queued = new();
    private MetaLinkRuntimeSnapshot _current;

    public FakeMetaLinkRuntime(MetaLinkRuntimeSnapshot initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    public bool IsDisposed { get; private set; }

    public int PollCount { get; private set; }

    public int ResetCount { get; private set; }

    public void Enqueue(MetaLinkRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            ThrowIfDisposed();
            _queued.Enqueue(snapshot);
        }
    }

    public MetaLinkRuntimeSnapshot Poll()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            PollCount++;
            if (_queued.Count > 0)
            {
                _current = _queued.Dequeue();
            }

            return _current;
        }
    }

    public bool TryGetLatest(MetaLinkHand hand, out MetaLinkControllerSnapshot? snapshot)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            snapshot = _current.ForHand(hand).Controller;
            return snapshot is not null;
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            ResetCount++;
            _queued.Clear();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            _queued.Clear();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
