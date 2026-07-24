namespace Ltb.App;

internal enum InternalDriverProfileStoreCommitState
{
    Preparing = 0,
    Canceled = 1,
    Committing = 2,
    Committed = 3,
    Failed = 4,
}

/// <summary>
/// Linearizes cancellation against the start of an uninterruptible canonical
/// profile-store replace. Whichever transition wins from Preparing owns the
/// transaction outcome.
/// </summary>
internal sealed class InternalDriverProfileStoreCommitGate : IDisposable
{
    private readonly CancellationToken _cancellationToken;
    private readonly CancellationTokenRegistration _registration;
    private int _state = (int)InternalDriverProfileStoreCommitState.Preparing;

    public InternalDriverProfileStoreCommitGate(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        _registration = cancellationToken.Register(
            static value => ((InternalDriverProfileStoreCommitGate)value!).CancelBeforeCommit(),
            this);
        if (cancellationToken.IsCancellationRequested)
        {
            CancelBeforeCommit();
        }
    }

    public InternalDriverProfileStoreCommitState State =>
        (InternalDriverProfileStoreCommitState)Volatile.Read(ref _state);

    public TResult Commit<TResult>(Func<TResult> commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var prior = (InternalDriverProfileStoreCommitState)Interlocked.CompareExchange(
            ref _state,
            (int)InternalDriverProfileStoreCommitState.Committing,
            (int)InternalDriverProfileStoreCommitState.Preparing);
        if (prior == InternalDriverProfileStoreCommitState.Canceled)
        {
            throw new OperationCanceledException(_cancellationToken);
        }

        if (prior != InternalDriverProfileStoreCommitState.Preparing)
        {
            throw new InvalidOperationException(
                $"Profile-store commit cannot start from state {prior}.");
        }

        try
        {
            var result = commit();
            Volatile.Write(ref _state, (int)InternalDriverProfileStoreCommitState.Committed);
            return result;
        }
        catch
        {
            Volatile.Write(ref _state, (int)InternalDriverProfileStoreCommitState.Failed);
            throw;
        }
    }

    public void Dispose()
    {
        _registration.Dispose();
    }

    private void CancelBeforeCommit()
    {
        _ = Interlocked.CompareExchange(
            ref _state,
            (int)InternalDriverProfileStoreCommitState.Canceled,
            (int)InternalDriverProfileStoreCommitState.Preparing);
    }
}
