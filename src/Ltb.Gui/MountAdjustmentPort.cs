using System.Numerics;
using Ltb.App;
using Ltb.Core;
using Ltb.Protocol;

namespace Ltb.Gui;

/// <summary>
/// Semantic controller hand used by the GUI mount-adjustment boundary.
/// </summary>
public enum MountAdjustmentHand
{
    Left = 0,
    Right,
}

/// <summary>
/// Stopped-only calibration target requested by the presentation layer.
/// </summary>
public enum MountAdjustmentCalibrationTarget
{
    Left = 0,
    Right,
    Both,
}

/// <summary>
/// Explicit update semantics prevent a terminal restore failure from
/// disappearing merely because a later stopped snapshot has no new message.
/// </summary>
public enum MountAdjustmentRestoreWarningUpdateKind
{
    Unchanged = 0,
    Clear,
    Failure,
}

/// <summary>
/// Active parent-from-child transform. Translation is meters and rotation is
/// a finite normalized quaternion in XYZW component order.
/// </summary>
public readonly record struct MountAdjustmentTransform(
    Vector3 TranslationMeters,
    Quaternion RotationXyzw)
{
    public static MountAdjustmentTransform Identity { get; } =
        new(Vector3.Zero, Quaternion.Identity);
}

/// <summary>
/// Absolute tracker-side and controller-side adjustments for one hand.
/// These are full values, never relative edit deltas.
/// </summary>
public readonly record struct MountAdjustmentPair(
    MountAdjustmentTransform TrackerSide,
    MountAdjustmentTransform ControllerSide)
{
    public static MountAdjustmentPair Identity { get; } =
        new(MountAdjustmentTransform.Identity, MountAdjustmentTransform.Identity);
}

/// <summary>
/// App-authoritative mount state for one hand. EffectiveMount obeys
/// A_tracker * BaseMount * A_controller.
/// </summary>
public sealed record MountAdjustmentHandSnapshot(
    MountAdjustmentTransform BaseMount,
    MountAdjustmentPair AppliedAdjustments,
    MountAdjustmentPair SavedAdjustments,
    MountAdjustmentTransform EffectiveMount)
{
    public static MountAdjustmentHandSnapshot Identity { get; } = new(
        MountAdjustmentTransform.Identity,
        MountAdjustmentPair.Identity,
        MountAdjustmentPair.Identity,
        MountAdjustmentTransform.Identity);
}

public sealed record MountAdjustmentNeutralizationSnapshot(
    InternalDriverTrackerNeutralizationState Phase,
    string Detail)
{
    public static MountAdjustmentNeutralizationSnapshot Inactive { get; } =
        new(InternalDriverTrackerNeutralizationState.Inactive, "Tracker output is not neutralized.");
}

public sealed record MountAdjustmentRestoreWarningUpdate(
    MountAdjustmentRestoreWarningUpdateKind Kind,
    string? Message)
{
    public static MountAdjustmentRestoreWarningUpdate Unchanged { get; } =
        new(MountAdjustmentRestoreWarningUpdateKind.Unchanged, null);

    public static MountAdjustmentRestoreWarningUpdate Clear { get; } =
        new(MountAdjustmentRestoreWarningUpdateKind.Clear, null);

    public static MountAdjustmentRestoreWarningUpdate Failure(string message) =>
        new(MountAdjustmentRestoreWarningUpdateKind.Failure, message);
}

/// <summary>
/// Immutable app snapshot. Revision is monotonic for adjustment values and
/// availability retirement. RestoreWarning uses explicit
/// unchanged/clear/failure semantics.
/// </summary>
public sealed record MountAdjustmentSnapshot(
    long Revision,
    bool IsAvailable,
    MountAdjustmentHandSnapshot Left,
    MountAdjustmentHandSnapshot Right,
    MountAdjustmentNeutralizationSnapshot Neutralization,
    MountAdjustmentRestoreWarningUpdate RestoreWarning)
{
    public static MountAdjustmentSnapshot Unavailable { get; } = new(
        Revision: 0,
        IsAvailable: false,
        MountAdjustmentHandSnapshot.Identity,
        MountAdjustmentHandSnapshot.Identity,
        MountAdjustmentNeutralizationSnapshot.Inactive,
        MountAdjustmentRestoreWarningUpdate.Unchanged);
}

public sealed record MountAdjustmentLiveApplyRequest(
    long Revision,
    MountAdjustmentHand Hand,
    MountAdjustmentPair Adjustments);

public sealed record MountAdjustmentSaveRequest(
    long Revision,
    MountAdjustmentPair Left,
    MountAdjustmentPair Right);

/// <summary>
/// Exact revision acknowledgement returned by apply and save operations.
/// Snapshot is app-authoritative and contains the revalidated effective value.
/// </summary>
public sealed record MountAdjustmentPortResult(
    long AcknowledgedRevision,
    bool Succeeded,
    string Diagnostic,
    MountAdjustmentSnapshot Snapshot);

/// <summary>
/// Narrow presentation/application boundary. The GUI edits absolute values;
/// the application revalidates, composes, applies, persists, neutralizes, and
/// sequences runtimes.
/// </summary>
public interface IMountAdjustmentPort
{
    event EventHandler<MountAdjustmentSnapshot>? SnapshotChanged;

    MountAdjustmentSnapshot CurrentSnapshot { get; }

    ValueTask<MountAdjustmentPortResult> ApplyLiveAsync(
        MountAdjustmentLiveApplyRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<MountAdjustmentPortResult> SaveAsync(
        MountAdjustmentSaveRequest request,
        CancellationToken cancellationToken = default);

    ValueTask RequestCalibrationAsync(
        MountAdjustmentCalibrationTarget target,
        CancellationToken cancellationToken = default);
}

internal sealed class UnavailableMountAdjustmentPort : IMountAdjustmentPort
{
    public static UnavailableMountAdjustmentPort Instance { get; } = new();

    private UnavailableMountAdjustmentPort()
    {
    }

    public event EventHandler<MountAdjustmentSnapshot>? SnapshotChanged
    {
        add { }
        remove { }
    }

    public MountAdjustmentSnapshot CurrentSnapshot => MountAdjustmentSnapshot.Unavailable;

    public ValueTask<MountAdjustmentPortResult> ApplyLiveAsync(
        MountAdjustmentLiveApplyRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(UnavailableResult(request.Revision));

    public ValueTask<MountAdjustmentPortResult> SaveAsync(
        MountAdjustmentSaveRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(UnavailableResult(request.Revision));

    public ValueTask RequestCalibrationAsync(
        MountAdjustmentCalibrationTarget target,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new InvalidOperationException(
            "The application mount-adjustment adapter is unavailable."));

    private static MountAdjustmentPortResult UnavailableResult(long revision) => new(
        revision,
        Succeeded: false,
        "The application mount-adjustment adapter is unavailable.",
        MountAdjustmentSnapshot.Unavailable);
}

/// <summary>
/// Concrete presentation adapter over the App-owned mount-adjustment control
/// and tracker-neutralization evidence. The hot loop consumes only App/Core
/// immutable state; GUI DTOs are constructed at this boundary.
/// </summary>
public sealed class AppMountAdjustmentPort : IMountAdjustmentPort
{
    private readonly object _sync = new();
    private readonly object _publicationSync = new();
    private readonly Dictionary<long, int> _activeRequestRevisions = [];
    private IInternalDriverSession? _session;
    private IInternalDriverMountAdjustmentControl? _control;
    private EventHandler<InternalDriverSessionSnapshot>? _sessionSnapshotHandler;
    private EventHandler<InternalDriverMountAdjustmentSnapshot>? _mountAdjustmentHandler;
    private MountAdjustmentSnapshot _snapshot = MountAdjustmentSnapshot.Unavailable;
    private long _bindingGeneration;
    private long _bindingLocalRevision;

    public event EventHandler<MountAdjustmentSnapshot>? SnapshotChanged;

    public MountAdjustmentSnapshot CurrentSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _snapshot;
            }
        }
    }

    internal void Bind(IInternalDriverSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        MountAdjustmentSnapshot snapshot;
        lock (_publicationSync)
        {
            lock (_sync)
            {
                Unsubscribe();
                var generation = checked(++_bindingGeneration);
                _session = session;
                _control = session as IInternalDriverMountAdjustmentControl;
                _activeRequestRevisions.Clear();
                _sessionSnapshotHandler = (sender, current) =>
                    OnSessionSnapshotChanged(
                        generation,
                        session,
                        sender,
                        current);
                _session.SnapshotChanged += _sessionSnapshotHandler;
                if (_control is not null)
                {
                    var control = _control;
                    _mountAdjustmentHandler = (sender, adjustment) =>
                        OnMountAdjustmentChanged(
                            generation,
                            session,
                            control,
                            sender,
                            adjustment);
                    control.MountAdjustmentChanged += _mountAdjustmentHandler;
                }

                var adjustment = _control?.CurrentMountAdjustment ??
                    InternalDriverMountAdjustmentSnapshot.Unavailable;
                _bindingLocalRevision = adjustment.Revision;
                snapshot = AdoptSnapshot(
                    BuildSnapshot(
                        adjustment,
                        session.CurrentSnapshot,
                        MountAdjustmentRestoreWarningUpdate.Unchanged),
                    preserveRequestRevision: false);
                _snapshot = snapshot;
            }

            PublishSnapshotChanged(snapshot);
        }
    }

    public ValueTask<MountAdjustmentPortResult> ApplyLiveAsync(
        MountAdjustmentLiveApplyRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteOperationAsync(
            request.Revision,
            "live apply",
            control => control.ApplyMountAdjustmentAsync(
                request.Revision,
                ToProtocolHand(request.Hand),
                ToCore(request.Adjustments),
                cancellationToken));

    public ValueTask<MountAdjustmentPortResult> SaveAsync(
        MountAdjustmentSaveRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteOperationAsync(
            request.Revision,
            "save",
            control => control.SaveMountAdjustmentsAsync(
                request.Revision,
                ToCore(request.Left),
                ToCore(request.Right),
                cancellationToken));

    public ValueTask RequestCalibrationAsync(
        MountAdjustmentCalibrationTarget target,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new InvalidOperationException(
            "Calibration requests are sequenced by the stopped GUI session factory."));

    private async ValueTask<MountAdjustmentPortResult> ExecuteOperationAsync(
        long requestRevision,
        string operation,
        Func<
            IInternalDriverMountAdjustmentControl,
            ValueTask<InternalDriverMountAdjustmentResult>> start)
    {
        OperationBinding binding;
        lock (_sync)
        {
            binding = CaptureOperationBinding(requestRevision, operation);
            AddActiveRequestRevision(requestRevision);
        }

        ValueTask<InternalDriverMountAdjustmentResult> pending;
        try
        {
            pending = start(binding.Control);
        }
        catch
        {
            lock (_sync)
            {
                CompleteOperation(binding);
            }

            throw;
        }

        InternalDriverMountAdjustmentResult result;
        try
        {
            result = await pending.ConfigureAwait(false);
        }
        catch
        {
            lock (_sync)
            {
                CompleteOperation(binding);
            }

            throw;
        }

        return Present(binding, result);
    }

    private MountAdjustmentPortResult Present(
        OperationBinding binding,
        InternalDriverMountAdjustmentResult result)
    {
        MountAdjustmentSnapshot snapshot;
        lock (_publicationSync)
        {
            lock (_sync)
            {
                if (!IsCurrentBinding(binding))
                {
                    return new MountAdjustmentPortResult(
                        binding.RequestRevision,
                        Succeeded: false,
                        $"The mount-adjustment {binding.Operation} completed after " +
                        "the App session binding changed; the stale result was ignored.",
                        _snapshot);
                }

                try
                {
                    if (result.Snapshot.Revision < _bindingLocalRevision ||
                        (result.Snapshot.Revision == _bindingLocalRevision &&
                         !_snapshot.IsAvailable &&
                         result.Snapshot.IsAvailable))
                    {
                        snapshot = _snapshot;
                    }
                    else
                    {
                        _bindingLocalRevision = result.Snapshot.Revision;
                        snapshot = AdoptSnapshot(
                            BuildSnapshot(
                                result.Snapshot,
                                binding.Session.CurrentSnapshot,
                                _snapshot.RestoreWarning),
                            preserveRequestRevision:
                                result.Snapshot.Revision == binding.RequestRevision);
                        _snapshot = snapshot;
                    }
                }
                finally
                {
                    CompleteOperation(binding);
                }
            }
        }

        return new MountAdjustmentPortResult(
            result.AcknowledgedRevision,
            result.Succeeded,
            result.Diagnostic,
            snapshot);
    }

    private OperationBinding CaptureOperationBinding(
        long requestRevision,
        string operation)
    {
        if (_session is null || _control is null)
        {
            throw new InvalidOperationException(
                "The active App session does not expose mount-adjustment control.");
        }

        if (!_snapshot.IsAvailable)
        {
            throw new InvalidOperationException(
                "The active App session's mount-adjustment control is unavailable.");
        }

        return new OperationBinding(
            _bindingGeneration,
            _session,
            _control,
            requestRevision,
            operation);
    }

    private void OnMountAdjustmentChanged(
        long generation,
        IInternalDriverSession expectedSession,
        IInternalDriverMountAdjustmentControl expectedControl,
        object? sender,
        InternalDriverMountAdjustmentSnapshot adjustment)
    {
        MountAdjustmentSnapshot snapshot;
        lock (_publicationSync)
        {
            lock (_sync)
            {
                if (generation != _bindingGeneration ||
                    !ReferenceEquals(expectedSession, _session) ||
                    !ReferenceEquals(expectedControl, _control) ||
                    !ReferenceEquals(sender, expectedControl) ||
                    adjustment.Revision < _bindingLocalRevision)
                {
                    return;
                }

                _bindingLocalRevision = adjustment.Revision;
                snapshot = AdoptSnapshot(
                    BuildSnapshot(
                        adjustment,
                        expectedSession.CurrentSnapshot,
                        _snapshot.RestoreWarning),
                    preserveRequestRevision:
                        IsActiveRequestRevision(adjustment.Revision));
                _snapshot = snapshot;
            }

            PublishSnapshotChanged(snapshot);
        }
    }

    private void OnSessionSnapshotChanged(
        long generation,
        IInternalDriverSession expectedSession,
        object? sender,
        InternalDriverSessionSnapshot session)
    {
        MountAdjustmentSnapshot snapshot;
        lock (_publicationSync)
        {
            lock (_sync)
            {
                if (generation != _bindingGeneration ||
                    !ReferenceEquals(expectedSession, _session) ||
                    !ReferenceEquals(sender, expectedSession))
                {
                    return;
                }

                var warning = session.TrackerNeutralization?.State switch
                {
                    InternalDriverTrackerNeutralizationState.RestoreFailed =>
                        MountAdjustmentRestoreWarningUpdate.Failure(
                            session.TrackerNeutralization.Diagnostic),
                    InternalDriverTrackerNeutralizationState.Recovered or
                    InternalDriverTrackerNeutralizationState.Restored =>
                        MountAdjustmentRestoreWarningUpdate.Clear,
                    _ => MountAdjustmentRestoreWarningUpdate.Unchanged,
                };
                var adjustment = _control?.CurrentMountAdjustment ??
                    InternalDriverMountAdjustmentSnapshot.Unavailable;
                if (adjustment.Revision < _bindingLocalRevision)
                {
                    snapshot = _snapshot with
                    {
                        Neutralization = ToGuiNeutralization(session),
                        RestoreWarning = warning,
                    };
                }
                else
                {
                    _bindingLocalRevision = adjustment.Revision;
                    snapshot = AdoptSnapshot(
                        BuildSnapshot(
                            adjustment,
                            session,
                            warning),
                        preserveRequestRevision:
                            IsActiveRequestRevision(adjustment.Revision));
                }

                _snapshot = snapshot;
            }

            PublishSnapshotChanged(snapshot);
        }
    }

    private MountAdjustmentSnapshot AdoptSnapshot(
        MountAdjustmentSnapshot candidate,
        bool preserveRequestRevision)
    {
        var materiallyChanged = !MateriallyEquivalent(_snapshot, candidate);
        var revision = preserveRequestRevision &&
                       candidate.Revision >= _snapshot.Revision
            ? candidate.Revision
            : !materiallyChanged
                ? _snapshot.Revision
                : candidate.Revision > _snapshot.Revision
                    ? candidate.Revision
                    : checked(_snapshot.Revision + 1);
        return candidate with { Revision = revision };
    }

    private static bool MateriallyEquivalent(
        MountAdjustmentSnapshot left,
        MountAdjustmentSnapshot right) =>
        left.IsAvailable == right.IsAvailable &&
        left.Left == right.Left &&
        left.Right == right.Right;

    private bool IsCurrentBinding(OperationBinding binding) =>
        binding.Generation == _bindingGeneration &&
        ReferenceEquals(binding.Session, _session) &&
        ReferenceEquals(binding.Control, _control);

    private void AddActiveRequestRevision(long revision)
    {
        _activeRequestRevisions.TryGetValue(revision, out var count);
        _activeRequestRevisions[revision] = checked(count + 1);
    }

    private bool IsActiveRequestRevision(long revision) =>
        _activeRequestRevisions.ContainsKey(revision);

    private void CompleteOperation(OperationBinding binding)
    {
        if (!IsCurrentBinding(binding) ||
            !_activeRequestRevisions.TryGetValue(
                binding.RequestRevision,
                out var count))
        {
            return;
        }

        if (count == 1)
        {
            _activeRequestRevisions.Remove(binding.RequestRevision);
        }
        else
        {
            _activeRequestRevisions[binding.RequestRevision] = count - 1;
        }
    }

    private void Unsubscribe()
    {
        if (_session is not null && _sessionSnapshotHandler is not null)
        {
            _session.SnapshotChanged -= _sessionSnapshotHandler;
        }

        if (_control is not null && _mountAdjustmentHandler is not null)
        {
            _control.MountAdjustmentChanged -= _mountAdjustmentHandler;
        }

        _sessionSnapshotHandler = null;
        _mountAdjustmentHandler = null;
    }

    private sealed record OperationBinding(
        long Generation,
        IInternalDriverSession Session,
        IInternalDriverMountAdjustmentControl Control,
        long RequestRevision,
        string Operation);

    private static MountAdjustmentSnapshot BuildSnapshot(
        InternalDriverMountAdjustmentSnapshot adjustment,
        InternalDriverSessionSnapshot session,
        MountAdjustmentRestoreWarningUpdate warning)
    {
        return new MountAdjustmentSnapshot(
            adjustment.Revision,
            adjustment.IsAvailable,
            ToGui(adjustment.Left),
            ToGui(adjustment.Right),
            ToGuiNeutralization(session),
            warning);
    }

    private static MountAdjustmentNeutralizationSnapshot ToGuiNeutralization(
        InternalDriverSessionSnapshot session)
    {
        var neutralization = session.TrackerNeutralization;
        return neutralization is null
            ? MountAdjustmentNeutralizationSnapshot.Inactive
            : new MountAdjustmentNeutralizationSnapshot(
                neutralization.State,
                neutralization.Diagnostic);
    }

    private void PublishSnapshotChanged(MountAdjustmentSnapshot snapshot)
    {
        var handlers = SnapshotChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<MountAdjustmentSnapshot> handler
                 in handlers.GetInvocationList())
        {
            try
            {
                handler(this, snapshot);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Presentation observers cannot break adapter publication
                // ordering or turn an App operation into a reported failure.
            }
        }
    }

    private static MountAdjustmentHandSnapshot ToGui(
        InternalDriverMountAdjustmentHandSnapshot hand) => new(
        ToGui(hand.BaseMount),
        ToGui(hand.AppliedAdjustments),
        ToGui(hand.SavedAdjustments),
        ToGui(hand.EffectiveMount));

    private static MountAdjustmentPair ToGui(MountAdjustment adjustment) => new(
        ToGui(adjustment.TrackerSideAdjustment),
        ToGui(adjustment.ControllerSideAdjustment));

    private static MountAdjustmentTransform ToGui(RigidTransform transform) => new(
        transform.TranslationMeters,
        transform.Rotation);

    private static MountAdjustment ToCore(MountAdjustmentPair pair) => new(
        ToCore(pair.TrackerSide),
        ToCore(pair.ControllerSide));

    private static RigidTransform ToCore(MountAdjustmentTransform transform) =>
        new(transform.RotationXyzw, transform.TranslationMeters);

    private static ProtocolHand ToProtocolHand(MountAdjustmentHand hand) =>
        hand switch
        {
            MountAdjustmentHand.Left => ProtocolHand.Left,
            MountAdjustmentHand.Right => ProtocolHand.Right,
            _ => throw new ArgumentOutOfRangeException(nameof(hand)),
        };
}
