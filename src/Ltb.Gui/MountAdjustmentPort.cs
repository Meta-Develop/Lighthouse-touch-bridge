using System.Numerics;

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
/// Read-only tracker neutralization phase reported by the application.
/// </summary>
public enum MountAdjustmentNeutralizationPhase
{
    Inactive = 0,
    Neutralizing,
    Neutralized,
    Restoring,
    Restored,
    RestoreFailed,
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
    MountAdjustmentNeutralizationPhase Phase,
    string Detail)
{
    public static MountAdjustmentNeutralizationSnapshot Inactive { get; } =
        new(MountAdjustmentNeutralizationPhase.Inactive, "Tracker output is not neutralized.");
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
/// Immutable app snapshot. Revision is monotonic for adjustment values.
/// RestoreWarning uses explicit unchanged/clear/failure semantics.
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
