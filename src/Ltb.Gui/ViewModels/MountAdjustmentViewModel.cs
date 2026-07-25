using System.Globalization;
using System.Numerics;

namespace Ltb.Gui.ViewModels;

/// <summary>
/// Presentation model for bounded per-hand mount adjustments. Runtime
/// sequencing and persistence remain behind <see cref="IMountAdjustmentPort"/>.
/// </summary>
public sealed class MountAdjustmentViewModel : ObservableObject, IDisposable
{
    public const double PositionStepMillimeters = 1d;
    public const double RotationStepDegrees = 1d;
    public const double MaximumTranslationMillimeters = 500d;

    private readonly IMountAdjustmentPort _port;
    private readonly Action<Action> _dispatch;
    private readonly Func<MountAdjustmentCalibrationTarget, bool> _canCalibrate;
    private readonly Func<MountAdjustmentCalibrationTarget, Task>? _unavailableCalibrationFallback;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private long _revision;
    private MountAdjustmentPair _savedLeft = MountAdjustmentPair.Identity;
    private MountAdjustmentPair _savedRight = MountAdjustmentPair.Identity;
    private bool _isAvailable;
    private bool _isDirty;
    private bool _isBusy;
    private bool _disposed;
    private string _dirtyStatusText = "No unsaved mount adjustments.";
    private string _statusText = "Mount adjustment is unavailable.";
    private string _trackerNeutralizationStatusText = "Inactive: Tracker output is not neutralized.";
    private bool _hasRestoreFailureWarning;
    private string _restoreFailureWarningText = string.Empty;

    public MountAdjustmentViewModel(
        IMountAdjustmentPort port,
        Action<Action> dispatch,
        Func<MountAdjustmentCalibrationTarget, bool>? canCalibrate = null,
        Func<MountAdjustmentCalibrationTarget, Task>? unavailableCalibrationFallback = null)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _canCalibrate = canCalibrate ?? (static _ => true);
        _unavailableCalibrationFallback = unavailableCalibrationFallback;
        LeftHand = new MountAdjustmentHandViewModel(
            "Left hand",
            MountAdjustmentHand.Left,
            OnSlotEdited,
            PresentInvalidInput);
        RightHand = new MountAdjustmentHandViewModel(
            "Right hand",
            MountAdjustmentHand.Right,
            OnSlotEdited,
            PresentInvalidInput);
        SaveCommand = new RelayCommand(
            () => _ = SaveAsync(),
            () => CanSave);
        CalibrateLeftCommand = new RelayCommand(
            () => _ = RequestCalibrationAsync(MountAdjustmentCalibrationTarget.Left),
            () => CanRequestCalibration(MountAdjustmentCalibrationTarget.Left));
        CalibrateRightCommand = new RelayCommand(
            () => _ = RequestCalibrationAsync(MountAdjustmentCalibrationTarget.Right),
            () => CanRequestCalibration(MountAdjustmentCalibrationTarget.Right));
        CalibrateBothCommand = new RelayCommand(
            () => _ = RequestCalibrationAsync(MountAdjustmentCalibrationTarget.Both),
            () => CanRequestCalibration(MountAdjustmentCalibrationTarget.Both));

        _port.SnapshotChanged += OnSnapshotChanged;
        _dispatch(() => ApplySnapshot(_port.CurrentSnapshot));
    }

    public MountAdjustmentHandViewModel LeftHand { get; }

    public MountAdjustmentHandViewModel RightHand { get; }

    public RelayCommand SaveCommand { get; }

    public RelayCommand CalibrateLeftCommand { get; }

    public RelayCommand CalibrateRightCommand { get; }

    public RelayCommand CalibrateBothCommand { get; }

    public string AxisOrderHelpText { get; } =
        "Right-handed axes: +X right, +Y up, -Z forward. " +
        "Intrinsic local rotation order is X then Y then Z (q = Qz * Qy * Qx).";

    public bool IsAvailable
    {
        get => _isAvailable;
        private set => SetProperty(ref _isAvailable, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public string DirtyStatusText
    {
        get => _dirtyStatusText;
        private set => SetProperty(ref _dirtyStatusText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string TrackerNeutralizationStatusText
    {
        get => _trackerNeutralizationStatusText;
        private set => SetProperty(ref _trackerNeutralizationStatusText, value);
    }

    public bool HasRestoreFailureWarning
    {
        get => _hasRestoreFailureWarning;
        private set => SetProperty(ref _hasRestoreFailureWarning, value);
    }

    public string RestoreFailureWarningText
    {
        get => _restoreFailureWarningText;
        private set => SetProperty(ref _restoreFailureWarningText, value);
    }

    private bool CanSave => IsAvailable && IsDirty && !_isBusy && !_disposed;

    public void NotifyLifecycleAvailabilityChanged()
    {
        RaiseCommandAvailability();
    }

    public async Task SaveAsync()
    {
        if (!CanSave)
        {
            return;
        }

        await _operationGate.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
        try
        {
            if (_disposed || !IsAvailable)
            {
                return;
            }

            var request = new MountAdjustmentSaveRequest(
                Volatile.Read(ref _revision),
                LeftHand.Adjustments,
                RightHand.Adjustments);
            DispatchBusy(true, $"Saving mount adjustment revision {request.Revision}...");
            MountAdjustmentPortResult result;
            try
            {
                result = await _port.SaveAsync(
                    request,
                    _lifetimeCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                DispatchFailure($"Saving mount adjustments failed: {exception.Message}");
                return;
            }

            _dispatch(() => PresentSaveResult(request, result));
        }
        finally
        {
            _operationGate.Release();
            DispatchBusy(false);
        }
    }

    public async Task RequestCalibrationAsync(MountAdjustmentCalibrationTarget target)
    {
        if (!CanRequestCalibration(target))
        {
            return;
        }

        _dispatch(() => StatusText = $"Requesting {CalibrationLabel(target)} calibration...");
        try
        {
            if (IsAvailable)
            {
                await _port.RequestCalibrationAsync(
                    target,
                    _lifetimeCancellation.Token).ConfigureAwait(false);
            }
            else if (_unavailableCalibrationFallback is not null)
            {
                await _unavailableCalibrationFallback(target).ConfigureAwait(false);
            }

            _dispatch(() => StatusText = $"{CalibrationLabel(target)} calibration requested.");
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Disposal owns cancellation.
        }
        catch (Exception exception)
        {
            DispatchFailure($"{CalibrationLabel(target)} calibration request failed: {exception.Message}");
        }
    }

    private bool CanRequestCalibration(MountAdjustmentCalibrationTarget target) =>
        !_disposed &&
        _canCalibrate(target) &&
        (IsAvailable ||
         (target == MountAdjustmentCalibrationTarget.Both &&
          _unavailableCalibrationFallback is not null));

    private void OnSlotEdited(MountAdjustmentHand hand)
    {
        var revision = Interlocked.Increment(ref _revision);
        RefreshDirty();
        _ = ApplyLiveAsync(hand, revision);
    }

    private async Task ApplyLiveAsync(MountAdjustmentHand hand, long requestedRevision)
    {
        try
        {
            await _operationGate.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            if (_disposed || !IsAvailable)
            {
                DispatchFailure("Live mount adjustment is unavailable.");
                return;
            }

            var revision = Volatile.Read(ref _revision);
            var adjustments = hand == MountAdjustmentHand.Left
                ? LeftHand.Adjustments
                : RightHand.Adjustments;
            var request = new MountAdjustmentLiveApplyRequest(revision, hand, adjustments);
            DispatchBusy(
                true,
                $"Applying {HandLabel(hand)} mount adjustment revision {revision} live...");
            MountAdjustmentPortResult result;
            try
            {
                result = await _port.ApplyLiveAsync(
                    request,
                    _lifetimeCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                DispatchFailure($"Live mount adjustment failed: {exception.Message}");
                return;
            }

            _dispatch(() => PresentApplyResult(request, result, requestedRevision));
        }
        finally
        {
            _operationGate.Release();
            DispatchBusy(false);
        }
    }

    private void PresentApplyResult(
        MountAdjustmentLiveApplyRequest request,
        MountAdjustmentPortResult result,
        long requestedRevision)
    {
        if (!result.Succeeded)
        {
            StatusText = $"Live mount adjustment was rejected: {result.Diagnostic}";
            return;
        }

        if (result.AcknowledgedRevision != request.Revision)
        {
            StatusText =
                $"Live apply acknowledgement mismatch (requested {request.Revision}, " +
                $"received {result.AcknowledgedRevision}); dirty state retained.";
            return;
        }

        var currentRevision = Volatile.Read(ref _revision);
        if (currentRevision == result.AcknowledgedRevision)
        {
            ApplySnapshot(result.Snapshot);
            StatusText = string.IsNullOrWhiteSpace(result.Diagnostic)
                ? $"Applied mount adjustment revision {result.AcknowledgedRevision} live."
                : result.Diagnostic;
            return;
        }

        StatusText =
            $"Applied revision {result.AcknowledgedRevision}; newer revision {currentRevision} " +
            "is still pending.";
    }

    private void PresentSaveResult(
        MountAdjustmentSaveRequest request,
        MountAdjustmentPortResult result)
    {
        if (!result.Succeeded)
        {
            StatusText = $"Saving mount adjustments failed: {result.Diagnostic}";
            RefreshDirty();
            return;
        }

        if (result.AcknowledgedRevision != request.Revision)
        {
            StatusText =
                $"Save acknowledgement mismatch (requested {request.Revision}, " +
                $"received {result.AcknowledgedRevision}); dirty state retained.";
            RefreshDirty();
            return;
        }

        var currentRevision = Volatile.Read(ref _revision);
        if (currentRevision == result.AcknowledgedRevision &&
            ApproximatelyEqual(LeftHand.Adjustments, request.Left) &&
            ApproximatelyEqual(RightHand.Adjustments, request.Right))
        {
            ApplySnapshot(result.Snapshot);
            _savedLeft = result.Snapshot.Left.SavedAdjustments;
            _savedRight = result.Snapshot.Right.SavedAdjustments;
            RefreshDirty();
            StatusText = string.IsNullOrWhiteSpace(result.Diagnostic)
                ? $"Saved mount adjustment revision {result.AcknowledgedRevision}."
                : result.Diagnostic;
            return;
        }

        _savedLeft = result.Snapshot.Left.SavedAdjustments;
        _savedRight = result.Snapshot.Right.SavedAdjustments;
        RefreshDirty();
        StatusText =
            $"Saved revision {result.AcknowledgedRevision}; newer edits remain unsaved.";
    }

    private void OnSnapshotChanged(object? sender, MountAdjustmentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _dispatch(() => ApplySnapshot(snapshot));
    }

    private void ApplySnapshot(MountAdjustmentSnapshot snapshot)
    {
        if (!TryNormalizeSnapshot(snapshot, out var normalized, out var error))
        {
            StatusText = $"Mount adjustment snapshot rejected: {error}";
            return;
        }

        ApplyNeutralization(normalized);
        ApplyRestoreWarning(normalized.RestoreWarning);

        var currentRevision = Volatile.Read(ref _revision);
        if (normalized.Revision < currentRevision)
        {
            return;
        }

        Interlocked.Exchange(ref _revision, normalized.Revision);
        IsAvailable = normalized.IsAvailable;
        LeftHand.Load(normalized.Left);
        RightHand.Load(normalized.Right);
        _savedLeft = normalized.Left.SavedAdjustments;
        _savedRight = normalized.Right.SavedAdjustments;
        RefreshDirty();
        if (!normalized.IsAvailable)
        {
            StatusText = "Mount adjustment is unavailable.";
        }
        else if (StatusText == "Mount adjustment is unavailable.")
        {
            StatusText = "Mount adjustments loaded.";
        }

        RaiseCommandAvailability();
    }

    private void ApplyNeutralization(MountAdjustmentSnapshot snapshot)
    {
        var neutralization = snapshot.Neutralization;
        TrackerNeutralizationStatusText =
            $"{SplitPascalCase(neutralization.Phase.ToString())}: {neutralization.Detail}";
    }

    private void ApplyRestoreWarning(MountAdjustmentRestoreWarningUpdate warning)
    {
        switch (warning.Kind)
        {
            case MountAdjustmentRestoreWarningUpdateKind.Unchanged:
                return;
            case MountAdjustmentRestoreWarningUpdateKind.Clear:
                HasRestoreFailureWarning = false;
                RestoreFailureWarningText = string.Empty;
                return;
            case MountAdjustmentRestoreWarningUpdateKind.Failure:
                HasRestoreFailureWarning = true;
                RestoreFailureWarningText = string.IsNullOrWhiteSpace(warning.Message)
                    ? "Tracker restoration failed. Inspect runtime state before continuing."
                    : warning.Message;
                return;
            default:
                StatusText = "Mount adjustment restore-warning update was invalid.";
                return;
        }
    }

    private void RefreshDirty()
    {
        IsDirty =
            !ApproximatelyEqual(LeftHand.Adjustments, _savedLeft) ||
            !ApproximatelyEqual(RightHand.Adjustments, _savedRight);
        DirtyStatusText = IsDirty
            ? "Unsaved mount adjustments are applied live. Select Save to persist them."
            : "No unsaved mount adjustments.";
        RaiseCommandAvailability();
    }

    private void PresentInvalidInput(string message) =>
        _dispatch(() => StatusText = $"Invalid mount adjustment: {message}");

    private void DispatchFailure(string message) =>
        _dispatch(() =>
        {
            StatusText = message;
            RefreshDirty();
        });

    private void DispatchBusy(bool isBusy, string? status = null) =>
        _dispatch(() =>
        {
            _isBusy = isBusy;
            if (status is not null)
            {
                StatusText = status;
            }

            RaiseCommandAvailability();
        });

    private void RaiseCommandAvailability()
    {
        SaveCommand.RaiseCanExecuteChanged();
        CalibrateLeftCommand.RaiseCanExecuteChanged();
        CalibrateRightCommand.RaiseCanExecuteChanged();
        CalibrateBothCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _port.SnapshotChanged -= OnSnapshotChanged;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        RaiseCommandAvailability();
    }

    private static bool TryNormalizeSnapshot(
        MountAdjustmentSnapshot snapshot,
        out MountAdjustmentSnapshot normalized,
        out string error)
    {
        if (snapshot.Revision < 0)
        {
            normalized = snapshot;
            error = "revision cannot be negative";
            return false;
        }

        if (!TryNormalizeHand(snapshot.Left, out var left, out error) ||
            !TryNormalizeHand(snapshot.Right, out var right, out error))
        {
            normalized = snapshot;
            return false;
        }

        if (snapshot.Neutralization is null ||
            string.IsNullOrWhiteSpace(snapshot.Neutralization.Detail))
        {
            normalized = snapshot;
            error = "neutralization detail is required";
            return false;
        }

        if (snapshot.RestoreWarning is null)
        {
            normalized = snapshot;
            error = "restore-warning update is required";
            return false;
        }

        normalized = snapshot with { Left = left, Right = right };
        error = string.Empty;
        return true;
    }

    private static bool TryNormalizeHand(
        MountAdjustmentHandSnapshot hand,
        out MountAdjustmentHandSnapshot normalized,
        out string error)
    {
        if (hand is null)
        {
            normalized = MountAdjustmentHandSnapshot.Identity;
            error = "hand snapshot is required";
            return false;
        }

        if (!TryNormalizeTransform(hand.BaseMount, adjustment: false, out var mount, out error) ||
            !TryNormalizePair(hand.AppliedAdjustments, out var applied, out error) ||
            !TryNormalizePair(hand.SavedAdjustments, out var saved, out error) ||
            !TryNormalizeTransform(hand.EffectiveMount, adjustment: false, out var effective, out error))
        {
            normalized = hand;
            return false;
        }

        normalized = hand with
        {
            BaseMount = mount,
            AppliedAdjustments = applied,
            SavedAdjustments = saved,
            EffectiveMount = effective,
        };
        return true;
    }

    private static bool TryNormalizePair(
        MountAdjustmentPair pair,
        out MountAdjustmentPair normalized,
        out string error)
    {
        if (!TryNormalizeTransform(pair.TrackerSide, adjustment: true, out var tracker, out error) ||
            !TryNormalizeTransform(pair.ControllerSide, adjustment: true, out var controller, out error))
        {
            normalized = pair;
            return false;
        }

        normalized = new MountAdjustmentPair(tracker, controller);
        return true;
    }

    private static bool TryNormalizeTransform(
        MountAdjustmentTransform transform,
        bool adjustment,
        out MountAdjustmentTransform normalized,
        out string error)
    {
        var translation = transform.TranslationMeters;
        var rotation = transform.RotationXyzw;
        if (!IsFinite(translation) || !IsFinite(rotation))
        {
            normalized = transform;
            error = "transform values must be finite";
            return false;
        }

        if (adjustment &&
            translation.LengthSquared() >
            (float)(MaximumTranslationMillimeters * MaximumTranslationMillimeters / 1_000_000d) +
            1e-6f)
        {
            normalized = transform;
            error = "an adjustment translation exceeds 0.5 m";
            return false;
        }

        var lengthSquared = rotation.LengthSquared();
        if (lengthSquared < 1e-12f)
        {
            normalized = transform;
            error = "rotation quaternion is degenerate";
            return false;
        }

        normalized = transform with { RotationXyzw = Quaternion.Normalize(rotation) };
        error = string.Empty;
        return true;
    }

    private static bool ApproximatelyEqual(MountAdjustmentPair left, MountAdjustmentPair right) =>
        ApproximatelyEqual(left.TrackerSide, right.TrackerSide) &&
        ApproximatelyEqual(left.ControllerSide, right.ControllerSide);

    private static bool ApproximatelyEqual(
        MountAdjustmentTransform left,
        MountAdjustmentTransform right)
    {
        var translationDelta = left.TranslationMeters - right.TranslationMeters;
        var leftRotation = Quaternion.Normalize(left.RotationXyzw);
        var rightRotation = Quaternion.Normalize(right.RotationXyzw);
        return translationDelta.LengthSquared() <= 1e-12f &&
            MathF.Abs(Quaternion.Dot(leftRotation, rightRotation)) >= 1f - 1e-6f;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);

    private static string CalibrationLabel(MountAdjustmentCalibrationTarget target) =>
        target switch
        {
            MountAdjustmentCalibrationTarget.Left => "left-hand",
            MountAdjustmentCalibrationTarget.Right => "right-hand",
            MountAdjustmentCalibrationTarget.Both => "two-hand",
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };

    private static string HandLabel(MountAdjustmentHand hand) =>
        hand == MountAdjustmentHand.Left ? "left-hand" : "right-hand";

    private static string SplitPascalCase(string value)
    {
        var characters = new List<char>(value.Length + 4) { value[0] };
        for (var index = 1; index < value.Length; index++)
        {
            if (char.IsUpper(value[index]) && !char.IsUpper(value[index - 1]))
            {
                characters.Add(' ');
            }

            characters.Add(value[index]);
        }

        return new string([.. characters]);
    }
}

public sealed class MountAdjustmentHandViewModel : ObservableObject
{
    private string _baseMountTransform = FormatTransform(MountAdjustmentTransform.Identity);
    private string _effectiveTransform = FormatTransform(MountAdjustmentTransform.Identity);

    internal MountAdjustmentHandViewModel(
        string title,
        MountAdjustmentHand hand,
        Action<MountAdjustmentHand> changed,
        Action<string> invalid)
    {
        Title = title;
        Hand = hand;
        TrackerSide = new MountAdjustmentSlotViewModel(
            "Tracker-side adjustment",
            () => changed(hand),
            invalid);
        ControllerSide = new MountAdjustmentSlotViewModel(
            "Controller-side adjustment",
            () => changed(hand),
            invalid);
    }

    public string Title { get; }

    public MountAdjustmentHand Hand { get; }

    public MountAdjustmentSlotViewModel TrackerSide { get; }

    public MountAdjustmentSlotViewModel ControllerSide { get; }

    public string BaseMountTransform
    {
        get => _baseMountTransform;
        private set => SetProperty(ref _baseMountTransform, value);
    }

    public string EffectiveTransform
    {
        get => _effectiveTransform;
        private set => SetProperty(ref _effectiveTransform, value);
    }

    internal MountAdjustmentPair Adjustments =>
        new(TrackerSide.Transform, ControllerSide.Transform);

    internal void Load(MountAdjustmentHandSnapshot snapshot)
    {
        TrackerSide.Load(snapshot.AppliedAdjustments.TrackerSide);
        ControllerSide.Load(snapshot.AppliedAdjustments.ControllerSide);
        BaseMountTransform = FormatTransform(snapshot.BaseMount);
        EffectiveTransform = FormatTransform(snapshot.EffectiveMount);
    }

    private static string FormatTransform(MountAdjustmentTransform transform)
    {
        var translation = transform.TranslationMeters * 1000f;
        var rotation = Quaternion.Normalize(transform.RotationXyzw);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"t=({translation.X:F1}, {translation.Y:F1}, {translation.Z:F1}) mm; " +
            $"q=({rotation.X:F5}, {rotation.Y:F5}, {rotation.Z:F5}, {rotation.W:F5}) XYZW");
    }
}

public sealed class MountAdjustmentSlotViewModel : ObservableObject
{
    private readonly Action _changed;
    private readonly Action<string> _invalid;
    private bool _loading;
    private double _positionXMillimeters;
    private double _positionYMillimeters;
    private double _positionZMillimeters;
    private double _rotationXDegrees;
    private double _rotationYDegrees;
    private double _rotationZDegrees;

    internal MountAdjustmentSlotViewModel(
        string title,
        Action changed,
        Action<string> invalid)
    {
        Title = title;
        _changed = changed;
        _invalid = invalid;
        PositionXDecrementCommand = Step(() => PositionXMillimeters -= 1d);
        PositionXIncrementCommand = Step(() => PositionXMillimeters += 1d);
        PositionYDecrementCommand = Step(() => PositionYMillimeters -= 1d);
        PositionYIncrementCommand = Step(() => PositionYMillimeters += 1d);
        PositionZDecrementCommand = Step(() => PositionZMillimeters -= 1d);
        PositionZIncrementCommand = Step(() => PositionZMillimeters += 1d);
        RotationXDecrementCommand = Step(() => RotationXDegrees -= 1d);
        RotationXIncrementCommand = Step(() => RotationXDegrees += 1d);
        RotationYDecrementCommand = Step(() => RotationYDegrees -= 1d);
        RotationYIncrementCommand = Step(() => RotationYDegrees += 1d);
        RotationZDecrementCommand = Step(() => RotationZDegrees -= 1d);
        RotationZIncrementCommand = Step(() => RotationZDegrees += 1d);
        ResetCommand = new RelayCommand(Reset);
    }

    public string Title { get; }

    public double PositionXMillimeters
    {
        get => _positionXMillimeters;
        set => SetTranslation(value, _positionYMillimeters, _positionZMillimeters);
    }

    public double PositionYMillimeters
    {
        get => _positionYMillimeters;
        set => SetTranslation(_positionXMillimeters, value, _positionZMillimeters);
    }

    public double PositionZMillimeters
    {
        get => _positionZMillimeters;
        set => SetTranslation(_positionXMillimeters, _positionYMillimeters, value);
    }

    public double RotationXDegrees
    {
        get => _rotationXDegrees;
        set => SetRotation(value, _rotationYDegrees, _rotationZDegrees);
    }

    public double RotationYDegrees
    {
        get => _rotationYDegrees;
        set => SetRotation(_rotationXDegrees, value, _rotationZDegrees);
    }

    public double RotationZDegrees
    {
        get => _rotationZDegrees;
        set => SetRotation(_rotationXDegrees, _rotationYDegrees, value);
    }

    public RelayCommand PositionXDecrementCommand { get; }

    public RelayCommand PositionXIncrementCommand { get; }

    public RelayCommand PositionYDecrementCommand { get; }

    public RelayCommand PositionYIncrementCommand { get; }

    public RelayCommand PositionZDecrementCommand { get; }

    public RelayCommand PositionZIncrementCommand { get; }

    public RelayCommand RotationXDecrementCommand { get; }

    public RelayCommand RotationXIncrementCommand { get; }

    public RelayCommand RotationYDecrementCommand { get; }

    public RelayCommand RotationYIncrementCommand { get; }

    public RelayCommand RotationZDecrementCommand { get; }

    public RelayCommand RotationZIncrementCommand { get; }

    public RelayCommand ResetCommand { get; }

    internal MountAdjustmentTransform Transform
    {
        get
        {
            var toRadians = MathF.PI / 180f;
            var qx = Quaternion.CreateFromAxisAngle(
                Vector3.UnitX,
                (float)_rotationXDegrees * toRadians);
            var qy = Quaternion.CreateFromAxisAngle(
                Vector3.UnitY,
                (float)_rotationYDegrees * toRadians);
            var qz = Quaternion.CreateFromAxisAngle(
                Vector3.UnitZ,
                (float)_rotationZDegrees * toRadians);
            var rotation = Quaternion.Normalize(
                Quaternion.Multiply(qz, Quaternion.Multiply(qy, qx)));
            return new MountAdjustmentTransform(
                new Vector3(
                    (float)(_positionXMillimeters / 1000d),
                    (float)(_positionYMillimeters / 1000d),
                    (float)(_positionZMillimeters / 1000d)),
                rotation);
        }
    }

    internal void Load(MountAdjustmentTransform transform)
    {
        _loading = true;
        try
        {
            var translation = transform.TranslationMeters * 1000f;
            SetTranslation(translation.X, translation.Y, translation.Z);
            var euler = ToIntrinsicXyzDegrees(Quaternion.Normalize(transform.RotationXyzw));
            SetRotation(euler.X, euler.Y, euler.Z);
        }
        finally
        {
            _loading = false;
        }
    }

    private static RelayCommand Step(Action action) => new(action);

    private void Reset()
    {
        var translationChanged =
            _positionXMillimeters != 0d ||
            _positionYMillimeters != 0d ||
            _positionZMillimeters != 0d;
        var rotationChanged =
            _rotationXDegrees != 0d ||
            _rotationYDegrees != 0d ||
            _rotationZDegrees != 0d;
        if (!translationChanged && !rotationChanged)
        {
            return;
        }

        _loading = true;
        try
        {
            SetTranslation(0d, 0d, 0d);
            SetRotation(0d, 0d, 0d);
        }
        finally
        {
            _loading = false;
        }

        _changed();
    }

    private void SetTranslation(double x, double y, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
        {
            _invalid("translation values must be finite");
            return;
        }

        var length = Math.Sqrt((x * x) + (y * y) + (z * z));
        if (length > MountAdjustmentViewModel.MaximumTranslationMillimeters)
        {
            var scale = MountAdjustmentViewModel.MaximumTranslationMillimeters / length;
            x *= scale;
            y *= scale;
            z *= scale;
        }

        var changed =
            SetProperty(ref _positionXMillimeters, x, nameof(PositionXMillimeters)) |
            SetProperty(ref _positionYMillimeters, y, nameof(PositionYMillimeters)) |
            SetProperty(ref _positionZMillimeters, z, nameof(PositionZMillimeters));
        if (changed && !_loading)
        {
            _changed();
        }
    }

    private void SetRotation(double x, double y, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
        {
            _invalid("rotation values must be finite");
            return;
        }

        x = WrapDegrees(x);
        y = WrapDegrees(y);
        z = WrapDegrees(z);
        var changed =
            SetProperty(ref _rotationXDegrees, x, nameof(RotationXDegrees)) |
            SetProperty(ref _rotationYDegrees, y, nameof(RotationYDegrees)) |
            SetProperty(ref _rotationZDegrees, z, nameof(RotationZDegrees));
        if (changed && !_loading)
        {
            _changed();
        }
    }

    private static double WrapDegrees(double value)
    {
        value %= 360d;
        if (value > 180d)
        {
            value -= 360d;
        }
        else if (value < -180d)
        {
            value += 360d;
        }

        return value;
    }

    private static Vector3 ToIntrinsicXyzDegrees(Quaternion quaternion)
    {
        var x = Math.Atan2(
            2d * ((quaternion.W * quaternion.X) + (quaternion.Y * quaternion.Z)),
            1d - (2d * ((quaternion.X * quaternion.X) + (quaternion.Y * quaternion.Y))));
        var y = Math.Asin(Math.Clamp(
            2d * ((quaternion.W * quaternion.Y) - (quaternion.Z * quaternion.X)),
            -1d,
            1d));
        var z = Math.Atan2(
            2d * ((quaternion.W * quaternion.Z) + (quaternion.X * quaternion.Y)),
            1d - (2d * ((quaternion.Y * quaternion.Y) + (quaternion.Z * quaternion.Z))));

        var toDegrees = 180d / Math.PI;
        return new Vector3(
            (float)(x * toDegrees),
            (float)(y * toDegrees),
            (float)(z * toDegrees));
    }
}
