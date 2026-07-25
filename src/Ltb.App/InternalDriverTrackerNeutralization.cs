using Ltb.Protocol;

namespace Ltb.App;

public enum InternalDriverTrackerNeutralizationState
{
    Inactive = 0,
    Recovering,
    Recovered,
    Neutralizing,
    Active,
    Restoring,
    Restored,
    RestoreFailed,
}

/// <summary>Stable evidence for one physical tracker path controlled by LTB.</summary>
public sealed record InternalDriverTrackerPath
{
    public InternalDriverTrackerPath(
        ProtocolHand hand,
        string trackerSerial,
        string devicePath)
    {
        if (hand is not ProtocolHand.Left and not ProtocolHand.Right)
        {
            throw new ArgumentOutOfRangeException(nameof(hand));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(trackerSerial);
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        Hand = hand;
        TrackerSerial = trackerSerial;
        DevicePath = devicePath;
    }

    public ProtocolHand Hand { get; }

    public string TrackerSerial { get; }

    public string DevicePath { get; }
}

/// <summary>
/// App-owned lifecycle evidence. The backend snapshot identifier is opaque;
/// native/settings implementations retain the actual reversible snapshot.
/// </summary>
public sealed record InternalDriverTrackerNeutralizationSnapshot(
    InternalDriverTrackerNeutralizationState State,
    IReadOnlyList<InternalDriverTrackerPath> TrackerPaths,
    string? BackendSnapshotId,
    string Diagnostic,
    IReadOnlyList<string> RestoreFailures)
{
    internal static InternalDriverTrackerNeutralizationSnapshot Inactive { get; } = new(
        InternalDriverTrackerNeutralizationState.Inactive,
        Array.Empty<InternalDriverTrackerPath>(),
        BackendSnapshotId: null,
        "Tracker-path neutralization is inactive.",
        Array.Empty<string>());
}

internal sealed record InternalDriverTrackerNeutralizationReceipt(
    string SnapshotId,
    IReadOnlyList<InternalDriverTrackerPath> TrackerPaths)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SnapshotId);
        InternalDriverTrackerNeutralizationLifecycle.ValidateExactPair(TrackerPaths);
    }
}

internal sealed record InternalDriverTrackerRecoveryResult(
    bool Restored,
    string Diagnostic,
    IReadOnlyList<string> Failures)
{
    internal static InternalDriverTrackerRecoveryResult NothingToRecover { get; } = new(
        Restored: true,
        "No retained tracker-path neutralization snapshot required recovery.",
        Array.Empty<string>());
}

/// <summary>
/// Provisional W3 reconciliation seam. Implementations capture original state,
/// neutralize the exact supplied paths, and restore only from the receipt.
/// Capture-and-neutralize must be atomic or self-rollback before throwing, and
/// must retain enough durable pending-snapshot state for RecoverAsync to
/// finish rollback after process/session interruption.
/// </summary>
internal interface IInternalDriverTrackerNeutralizationBackend
{
    ValueTask<InternalDriverTrackerNeutralizationReceipt> CaptureAndNeutralizeAsync(
        IReadOnlyList<InternalDriverTrackerPath> trackerPaths,
        CancellationToken cancellationToken);

    ValueTask RestoreAsync(
        InternalDriverTrackerNeutralizationReceipt receipt,
        CancellationToken cancellationToken);

    ValueTask<InternalDriverTrackerRecoveryResult> RecoverAsync(
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(InternalDriverTrackerRecoveryResult.NothingToRecover);
}

/// <summary>Optional runtime capability consumed by the App session.</summary>
internal interface IInternalDriverTrackerNeutralizationRuntime
{
    IInternalDriverTrackerNeutralizationBackend TrackerNeutralizationBackend { get; }
}

/// <summary>
/// Linear App lifecycle around a backend-owned reversible snapshot. It accepts
/// exactly one left and one right physical tracker path and retains failures
/// for structured session diagnostics.
/// </summary>
internal sealed class InternalDriverTrackerNeutralizationLifecycle
{
    private readonly IInternalDriverTrackerNeutralizationBackend _backend;
    private readonly Action<InternalDriverTrackerNeutralizationSnapshot>? _onChanged;
    private InternalDriverTrackerNeutralizationReceipt? _receipt;
    private InternalDriverTrackerNeutralizationSnapshot _snapshot =
        InternalDriverTrackerNeutralizationSnapshot.Inactive;

    public InternalDriverTrackerNeutralizationLifecycle(
        IInternalDriverTrackerNeutralizationBackend backend,
        Action<InternalDriverTrackerNeutralizationSnapshot>? onChanged = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _onChanged = onChanged;
    }

    public InternalDriverTrackerNeutralizationSnapshot Snapshot =>
        Volatile.Read(ref _snapshot);

    public async ValueTask RecoverAsync(CancellationToken cancellationToken)
    {
        Publish(
            InternalDriverTrackerNeutralizationState.Recovering,
            Array.Empty<InternalDriverTrackerPath>(),
            snapshotId: null,
            "Recovering any retained tracker-path snapshot before a new session.",
            Array.Empty<string>());
        var recovered = await _backend.RecoverAsync(cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(recovered);
        Publish(
            recovered.Restored
                ? InternalDriverTrackerNeutralizationState.Recovered
                : InternalDriverTrackerNeutralizationState.RestoreFailed,
            Array.Empty<InternalDriverTrackerPath>(),
            snapshotId: null,
            recovered.Diagnostic,
            recovered.Failures);
        if (!recovered.Restored)
        {
            throw new InvalidOperationException(
                $"Retained tracker-path recovery failed: {recovered.Diagnostic}");
        }

        _receipt = null;
    }

    public async ValueTask ActivateAsync(
        IReadOnlyList<InternalDriverTrackerPath> trackerPaths,
        CancellationToken cancellationToken)
    {
        ValidateExactPair(trackerPaths);
        if (_receipt is not null)
        {
            throw new InvalidOperationException(
                "Tracker paths are already neutralized for this App lifecycle.");
        }

        var immutablePaths = Array.AsReadOnly(trackerPaths.ToArray());
        Publish(
            InternalDriverTrackerNeutralizationState.Neutralizing,
            immutablePaths,
            snapshotId: null,
            "Capturing and neutralizing exactly two controller-source tracker paths.",
            Array.Empty<string>());
        InternalDriverTrackerNeutralizationReceipt receipt;
        try
        {
            var returnedReceipt = await _backend
                .CaptureAndNeutralizeAsync(immutablePaths, cancellationToken)
                .ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(returnedReceipt);
            returnedReceipt.Validate();
            if (!returnedReceipt.TrackerPaths.SequenceEqual(immutablePaths))
            {
                throw new InvalidDataException(
                    "The tracker neutralization backend receipt does not match the exact requested paths.");
            }

            // Never retain a backend/caller-owned mutable IReadOnlyList.
            receipt = new InternalDriverTrackerNeutralizationReceipt(
                returnedReceipt.SnapshotId,
                immutablePaths);
        }
        catch (Exception activationFailure) when (activationFailure is not OutOfMemoryException)
        {
            InternalDriverTrackerRecoveryResult recovery;
            try
            {
                recovery = await _backend.RecoverAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception recoveryFailure) when (recoveryFailure is not OutOfMemoryException)
            {
                Publish(
                    InternalDriverTrackerNeutralizationState.RestoreFailed,
                    immutablePaths,
                    snapshotId: null,
                    $"Tracker-path activation failed ({activationFailure.Message}) and durable " +
                    $"recovery also failed ({recoveryFailure.Message}).",
                    [activationFailure.Message, recoveryFailure.Message]);
                throw new AggregateException(
                    "Tracker-path activation and durable recovery both failed.",
                    activationFailure,
                    recoveryFailure);
            }

            Publish(
                recovery.Restored
                    ? InternalDriverTrackerNeutralizationState.Restored
                    : InternalDriverTrackerNeutralizationState.RestoreFailed,
                immutablePaths,
                snapshotId: null,
                $"Tracker-path activation failed ({activationFailure.Message}); " +
                recovery.Diagnostic,
                recovery.Failures.Count == 0
                    ? [activationFailure.Message]
                    : [activationFailure.Message, .. recovery.Failures]);
            if (!recovery.Restored)
            {
                throw new AggregateException(
                    "Tracker-path activation failed and durable recovery was incomplete.",
                    [activationFailure, new InvalidOperationException(recovery.Diagnostic)]);
            }

            throw;
        }

        _receipt = receipt;
        Publish(
            InternalDriverTrackerNeutralizationState.Active,
            immutablePaths,
            receipt.SnapshotId,
            "Exactly two controller-source tracker paths are neutralized while LTB is Active.",
            Array.Empty<string>());
    }

    public async ValueTask<bool> RestoreAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var receipt = _receipt;
        if (receipt is null)
        {
            return true;
        }

        Publish(
            InternalDriverTrackerNeutralizationState.Restoring,
            receipt.TrackerPaths,
            receipt.SnapshotId,
            $"Restoring the exact two tracker paths after {reason}.",
            Array.Empty<string>());
        try
        {
            await _backend.RestoreAsync(receipt, cancellationToken).ConfigureAwait(false);
            _receipt = null;
            Publish(
                InternalDriverTrackerNeutralizationState.Restored,
                receipt.TrackerPaths,
                receipt.SnapshotId,
                $"Restored the exact two tracker paths after {reason}.",
                Array.Empty<string>());
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Publish(
                InternalDriverTrackerNeutralizationState.RestoreFailed,
                receipt.TrackerPaths,
                receipt.SnapshotId,
                $"Tracker-path restore failed after {reason}: {exception.Message}",
                [exception.Message]);
            return false;
        }
    }

    internal static void ValidateExactPair(
        IReadOnlyList<InternalDriverTrackerPath> trackerPaths)
    {
        ArgumentNullException.ThrowIfNull(trackerPaths);
        if (trackerPaths.Count != 2 ||
            trackerPaths.Count(path => path.Hand == ProtocolHand.Left) != 1 ||
            trackerPaths.Count(path => path.Hand == ProtocolHand.Right) != 1 ||
            trackerPaths.Select(path => path.TrackerSerial)
                .Distinct(StringComparer.Ordinal).Count() != 2 ||
            trackerPaths.Select(path => path.DevicePath)
                .Distinct(StringComparer.Ordinal).Count() != 2)
        {
            throw new ArgumentException(
                "Tracker neutralization requires exactly one distinct left path and one distinct right path.",
                nameof(trackerPaths));
        }
    }

    private void Publish(
        InternalDriverTrackerNeutralizationState state,
        IReadOnlyList<InternalDriverTrackerPath> trackerPaths,
        string? snapshotId,
        string diagnostic,
        IReadOnlyList<string> failures)
    {
        var snapshot = new InternalDriverTrackerNeutralizationSnapshot(
            state,
            Array.AsReadOnly(trackerPaths.ToArray()),
            snapshotId,
            diagnostic,
            Array.AsReadOnly(failures.ToArray()));
        Volatile.Write(
            ref _snapshot,
            snapshot);
        _onChanged?.Invoke(snapshot);
    }
}
