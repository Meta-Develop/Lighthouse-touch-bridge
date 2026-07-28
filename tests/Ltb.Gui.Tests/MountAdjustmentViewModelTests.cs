using System.Collections.Concurrent;
using System.Numerics;
using Ltb.Gui;
using Ltb.Gui.ViewModels;

namespace Ltb.Gui.Tests;

public sealed class MountAdjustmentViewModelTests
{
    [Fact]
    public void NumericEditsClampWrapStepResetAndDispatchAbsoluteValues()
    {
        var port = new FakeMountAdjustmentPort(AvailableSnapshot());
        using var viewModel = NewViewModel(port);
        var slot = viewModel.LeftHand.TrackerSide;

        slot.PositionXMillimeters = 600d;
        Assert.Equal(500d, slot.PositionXMillimeters, 6);
        Assert.Equal(0.5f, port.ApplyRequests[^1].Adjustments.TrackerSide.TranslationMeters.X, 6);
        Assert.True(viewModel.IsDirty);
        Assert.True(viewModel.SaveCommand.CanExecute(null));

        slot.PositionXMillimeters = 10d;
        slot.PositionXDecrementCommand.Execute(null);
        Assert.Equal(9d, slot.PositionXMillimeters);
        slot.PositionXIncrementCommand.Execute(null);
        Assert.Equal(10d, slot.PositionXMillimeters);

        slot.RotationXDegrees = 181d;
        Assert.Equal(-179d, slot.RotationXDegrees);
        slot.RotationXIncrementCommand.Execute(null);
        Assert.Equal(-178d, slot.RotationXDegrees);
        slot.RotationXDecrementCommand.Execute(null);
        Assert.Equal(-179d, slot.RotationXDegrees);

        slot.PositionYMillimeters = 500d;
        Assert.Equal(500d, Math.Sqrt(
            (slot.PositionXMillimeters * slot.PositionXMillimeters) +
            (slot.PositionYMillimeters * slot.PositionYMillimeters) +
            (slot.PositionZMillimeters * slot.PositionZMillimeters)), 6);

        slot.ResetCommand.Execute(null);

        Assert.Equal(0d, slot.PositionXMillimeters);
        Assert.Equal(0d, slot.PositionYMillimeters);
        Assert.Equal(0d, slot.PositionZMillimeters);
        Assert.Equal(0d, slot.RotationXDegrees);
        Assert.Equal(MountAdjustmentTransform.Identity, port.ApplyRequests[^1].Adjustments.TrackerSide);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public void SelectableBoundedPresetsDrivePositionAndRotationCommands()
    {
        var port = new FakeMountAdjustmentPort(AvailableSnapshot());
        using var viewModel = NewViewModel(port);
        var slot = viewModel.LeftHand.TrackerSide;

        Assert.Equal([0.1d, 1d, 5d, 10d], viewModel.PositionStepPresetsMillimeters);
        Assert.Equal([0.1d, 1d, 5d, 15d], viewModel.RotationStepPresetsDegrees);

        viewModel.SelectedPositionStepMillimeters = 5d;
        viewModel.SelectedRotationStepDegrees = 15d;
        slot.PositionXIncrementCommand.Execute(null);
        slot.RotationZDecrementCommand.Execute(null);

        Assert.Equal(5d, slot.PositionXMillimeters, 6);
        Assert.Equal(-15d, slot.RotationZDegrees, 6);

        viewModel.SelectedPositionStepMillimeters = 2d;
        viewModel.SelectedRotationStepDegrees = double.PositiveInfinity;

        Assert.Equal(5d, viewModel.SelectedPositionStepMillimeters);
        Assert.Equal(15d, viewModel.SelectedRotationStepDegrees);
        Assert.Contains("preset", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EulerEditsUseIntrinsicLocalXThenYThenZQuaternionOrder()
    {
        var port = new FakeMountAdjustmentPort(AvailableSnapshot());
        using var viewModel = NewViewModel(port);
        var slot = viewModel.RightHand.ControllerSide;

        slot.RotationXDegrees = 20d;
        slot.RotationYDegrees = 30d;
        slot.RotationZDegrees = 40d;

        var toRadians = MathF.PI / 180f;
        var expected = Quaternion.Normalize(Quaternion.Multiply(
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, 20f * toRadians),
            Quaternion.Multiply(
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, 30f * toRadians),
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 40f * toRadians))));
        var reversed = Quaternion.Normalize(Quaternion.Multiply(
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 40f * toRadians),
            Quaternion.Multiply(
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, 30f * toRadians),
                Quaternion.CreateFromAxisAngle(Vector3.UnitX, 20f * toRadians))));
        var actual = port.ApplyRequests[^1].Adjustments.ControllerSide.RotationXyzw;

        Assert.InRange(MathF.Abs(Quaternion.Dot(expected, actual)), 0.999999f, 1f);
        Assert.True(MathF.Abs(Quaternion.Dot(reversed, actual)) < 0.999f);
        Assert.Contains("q = Qx * Qy * Qz", viewModel.AxisOrderHelpText, StringComparison.Ordinal);
    }

    [Fact]
    public void NoncommutingQuaternionSnapshotRoundTripsThroughIntrinsicLocalXyzEditors()
    {
        var toRadians = MathF.PI / 180f;
        var adjustment = new MountAdjustmentTransform(
            Vector3.Zero,
            Quaternion.Normalize(Quaternion.Multiply(
                Quaternion.CreateFromAxisAngle(Vector3.UnitX, 23f * toRadians),
                Quaternion.Multiply(
                    Quaternion.CreateFromAxisAngle(Vector3.UnitY, -31f * toRadians),
                    Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 47f * toRadians)))));
        var left = HandSnapshot(
            MountAdjustmentTransform.Identity,
            new MountAdjustmentPair(adjustment, MountAdjustmentTransform.Identity),
            MountAdjustmentPair.Identity);
        var port = new FakeMountAdjustmentPort(AvailableSnapshot() with { Left = left });

        using var viewModel = NewViewModel(port);

        Assert.True(
            MathF.Abs(Quaternion.Dot(
                adjustment.RotationXyzw,
                viewModel.LeftHand.TrackerSide.Transform.RotationXyzw)) >= 0.999999f);
    }

    [Fact]
    public void AppAcknowledgementDrivesEffectiveTrackerMountControllerOrder()
    {
        var baseMount = new MountAdjustmentTransform(
            new Vector3(0.1f, 0f, 0f),
            Quaternion.Identity);
        var port = new FakeMountAdjustmentPort(AvailableSnapshot(leftBase: baseMount));
        using var viewModel = NewViewModel(port);

        viewModel.LeftHand.TrackerSide.PositionXMillimeters = 10d;
        viewModel.LeftHand.TrackerSide.RotationZDegrees = 90d;
        viewModel.LeftHand.ControllerSide.PositionXMillimeters = 200d;

        Assert.Contains(
            "t=(10.0, 300.0, 0.0) mm",
            viewModel.LeftHand.EffectiveTransform,
            StringComparison.Ordinal);
        Assert.Contains(
            "t=(100.0, 0.0, 0.0) mm",
            viewModel.LeftHand.BaseMountTransform,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveClearsDirtyOnlyAfterExactSuccessfulAcknowledgement()
    {
        var port = new FakeMountAdjustmentPort(AvailableSnapshot());
        using var viewModel = NewViewModel(port);

        viewModel.LeftHand.TrackerSide.PositionXMillimeters = 12d;
        Assert.True(viewModel.IsDirty);

        await viewModel.SaveAsync();

        Assert.False(viewModel.IsDirty);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.Contains("saved", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);

        viewModel.RightHand.ControllerSide.RotationYDegrees = 7d;
        port.RejectNextSave = true;
        await viewModel.SaveAsync();

        Assert.True(viewModel.IsDirty);
        Assert.Contains("failed", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RevertAppliesLastSavedValuesLiveWithoutPersistence()
    {
        var savedTracker = new MountAdjustmentTransform(
            new Vector3(0.003f, 0f, 0f),
            Quaternion.Identity);
        var savedLeft = new MountAdjustmentPair(
            savedTracker,
            MountAdjustmentTransform.Identity);
        var left = HandSnapshot(
            MountAdjustmentTransform.Identity,
            applied: savedLeft,
            saved: savedLeft);
        var port = new FakeMountAdjustmentPort(
            AvailableSnapshot() with { Left = left });
        using var viewModel = NewViewModel(port);

        viewModel.LeftHand.TrackerSide.PositionXMillimeters = 12d;
        viewModel.RightHand.ControllerSide.RotationYDegrees = 7d;
        Assert.True(viewModel.IsDirty);
        Assert.True(viewModel.RevertCommand.CanExecute(null));
        var applyCount = port.ApplyRequests.Count;

        await viewModel.RevertAsync();

        Assert.Equal(3d, viewModel.LeftHand.TrackerSide.PositionXMillimeters, 6);
        Assert.Equal(0d, viewModel.RightHand.ControllerSide.RotationYDegrees, 6);
        Assert.False(viewModel.IsDirty);
        Assert.False(viewModel.RevertCommand.CanExecute(null));
        Assert.Equal(applyCount + 2, port.ApplyRequests.Count);
        Assert.Empty(port.SaveRequests);
        Assert.Contains("last saved", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OlderSaveAcknowledgementCannotClearNewerEdit()
    {
        var port = new FakeMountAdjustmentPort(AvailableSnapshot());
        using var viewModel = NewViewModel(port);
        viewModel.LeftHand.TrackerSide.PositionXMillimeters = 1d;

        port.BlockNextSave();
        var save = viewModel.SaveAsync();
        await port.SaveEntered;
        var nextApply = port.ObserveNextApply();
        viewModel.LeftHand.TrackerSide.PositionXMillimeters = 2d;
        port.CompleteBlockedSave();

        await save;
        await nextApply;

        Assert.Equal(2d, viewModel.LeftHand.TrackerSide.PositionXMillimeters, 6);
        Assert.True(viewModel.IsDirty);
        Assert.Contains("Unsaved", viewModel.DirtyStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RapidEditsRemainOrderedAcrossAnIntermediateRejectedRevision()
    {
        var port = new FakeMountAdjustmentPort(AvailableSnapshot());
        using var viewModel = NewViewModel(port);
        var slot = viewModel.LeftHand.TrackerSide;
        port.BlockNextApply();

        slot.PositionXMillimeters = 1d;
        await port.ApplyEntered.WaitAsync(TimeSpan.FromSeconds(5));
        port.RejectNextApply = true;
        slot.PositionXMillimeters = 2d;
        slot.PositionXMillimeters = 3d;

        Assert.Single(port.ApplyRequests);
        port.CompleteBlockedApply();
        await WaitUntilAsync(() => port.ApplyRequests.Count == 3);

        Assert.Equal([1L, 2L, 3L], port.ApplyRequests.Select(request => request.Revision));
        Assert.Equal(
            [1f, 2f, 3f],
            port.ApplyRequests.Select(request =>
                request.Adjustments.TrackerSide.TranslationMeters.X * 1_000f));
        Assert.Equal([2L], port.RejectedApplyRevisions);
        Assert.Equal(3L, port.CurrentSnapshot.Revision);
        Assert.Equal(
            0.003f,
            port.CurrentSnapshot.Left.AppliedAdjustments.TrackerSide.TranslationMeters.X,
            6);
        Assert.True(viewModel.IsDirty);
    }

    [Fact]
    public async Task ResetSaveAndRevertCannotOvertakeQueuedRapidEditsOrLoseCallTimeIntent()
    {
        var initialTracker = new MountAdjustmentTransform(
            new Vector3(0.005f, 0f, 0f),
            Quaternion.Identity);
        var initialPair = new MountAdjustmentPair(
            initialTracker,
            MountAdjustmentTransform.Identity);
        var port = new FakeMountAdjustmentPort(AvailableSnapshot() with
        {
            Left = HandSnapshot(
                MountAdjustmentTransform.Identity,
                initialPair,
                initialPair),
        });
        using var viewModel = NewViewModel(port);
        var slot = viewModel.LeftHand.TrackerSide;
        port.BlockNextApply();

        slot.PositionXMillimeters = 1d;
        await port.ApplyEntered.WaitAsync(TimeSpan.FromSeconds(5));
        slot.PositionXMillimeters = 2d;
        slot.PositionXMillimeters = 3d;
        slot.ResetCommand.Execute(null);
        var save = viewModel.SaveAsync();
        var revert = viewModel.RevertAsync();

        Assert.Single(port.ApplyRequests);
        Assert.Empty(port.SaveRequests);
        port.CompleteBlockedApply();
        await Task.WhenAll(save, revert).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            [
                "apply:1:Left",
                "apply:2:Left",
                "apply:3:Left",
                "apply:4:Left",
                "save:4",
                "apply:5:Left",
                "apply:5:Right",
            ],
            port.OperationLog);
        Assert.Equal(
            [1f, 2f, 3f, 0f],
            port.ApplyRequests.Take(4).Select(request =>
                request.Adjustments.TrackerSide.TranslationMeters.X * 1_000f));
        Assert.Equal(0f, Assert.Single(port.SaveRequests)
            .Left.TrackerSide.TranslationMeters.X);
        Assert.Equal(5d, viewModel.LeftHand.TrackerSide.PositionXMillimeters, 6);
        Assert.True(viewModel.IsDirty);
    }

    [Fact]
    public void ApplyAndInputFailuresStayDirtyAndSurfaceStatus()
    {
        var port = new FakeMountAdjustmentPort(AvailableSnapshot())
        {
            RejectNextApply = true,
        };
        using var viewModel = NewViewModel(port);

        viewModel.RightHand.TrackerSide.PositionZMillimeters = 4d;

        Assert.True(viewModel.IsDirty);
        Assert.Contains("rejected", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        var applyCount = port.ApplyRequests.Count;

        viewModel.RightHand.TrackerSide.PositionZMillimeters = double.NaN;

        Assert.Equal(4d, viewModel.RightHand.TrackerSide.PositionZMillimeters);
        Assert.Equal(applyCount, port.ApplyRequests.Count);
        Assert.Contains("finite", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelectedCalibrationIsStoppedOnlyAndDispatchesExactTarget()
    {
        var canCalibrate = false;
        var port = new FakeMountAdjustmentPort(AvailableSnapshot());
        using var viewModel = NewViewModel(port, _ => canCalibrate);

        Assert.False(viewModel.CalibrateLeftCommand.CanExecute(null));
        Assert.False(viewModel.CalibrateRightCommand.CanExecute(null));
        Assert.False(viewModel.CalibrateBothCommand.CanExecute(null));

        canCalibrate = true;
        viewModel.NotifyLifecycleAvailabilityChanged();
        Assert.True(viewModel.CalibrateLeftCommand.CanExecute(null));
        Assert.True(viewModel.CalibrateRightCommand.CanExecute(null));
        Assert.True(viewModel.CalibrateBothCommand.CanExecute(null));

        await viewModel.RequestCalibrationAsync(MountAdjustmentCalibrationTarget.Left);
        await viewModel.RequestCalibrationAsync(MountAdjustmentCalibrationTarget.Right);
        await viewModel.RequestCalibrationAsync(MountAdjustmentCalibrationTarget.Both);

        Assert.Equal(
            [
                MountAdjustmentCalibrationTarget.Left,
                MountAdjustmentCalibrationTarget.Right,
                MountAdjustmentCalibrationTarget.Both,
            ],
            port.CalibrationTargets);
    }

    [Fact]
    public void NeutralizationAndRestoreFailureRemainVisibleUntilExplicitClear()
    {
        var port = new FakeMountAdjustmentPort(AvailableSnapshot());
        using var viewModel = NewViewModel(port);

        port.Publish(port.CurrentSnapshot with
        {
            Neutralization = new MountAdjustmentNeutralizationSnapshot(
                Ltb.App.InternalDriverTrackerNeutralizationState.Active,
                "Both tracker outputs are neutral for calibration."),
            RestoreWarning = MountAdjustmentRestoreWarningUpdate.Failure(
                "TRACKER RESTORE FAILED: outputs remain neutral."),
        });

        Assert.Contains("Neutralized", viewModel.TrackerNeutralizationStatusText, StringComparison.Ordinal);
        Assert.True(viewModel.HasRestoreFailureWarning);
        Assert.Contains("RESTORE FAILED", viewModel.RestoreFailureWarningText, StringComparison.Ordinal);

        port.Publish(port.CurrentSnapshot with
        {
            IsAvailable = false,
            RestoreWarning = MountAdjustmentRestoreWarningUpdate.Unchanged,
        });

        Assert.True(viewModel.HasRestoreFailureWarning);
        Assert.Contains("RESTORE FAILED", viewModel.RestoreFailureWarningText, StringComparison.Ordinal);

        port.Publish(port.CurrentSnapshot with
        {
            RestoreWarning = MountAdjustmentRestoreWarningUpdate.Clear,
        });

        Assert.False(viewModel.HasRestoreFailureWarning);
        Assert.Equal(string.Empty, viewModel.RestoreFailureWarningText);
    }

    [Fact]
    public void EqualRevisionStatusSnapshotDoesNotEraseVisibleDirtyEdits()
    {
        var port = new FakeMountAdjustmentPort(AvailableSnapshot());
        using var viewModel = NewViewModel(port);
        viewModel.LeftHand.TrackerSide.PositionXMillimeters = 17d;
        var revision = port.CurrentSnapshot.Revision;

        port.Publish(port.CurrentSnapshot with
        {
            Revision = revision,
            Left = HandSnapshot(MountAdjustmentTransform.Identity),
            Neutralization = new MountAdjustmentNeutralizationSnapshot(
                Ltb.App.InternalDriverTrackerNeutralizationState.Restoring,
                "Restoring tracker roles."),
            RestoreWarning = MountAdjustmentRestoreWarningUpdate.Unchanged,
        });

        Assert.Equal(17d, viewModel.LeftHand.TrackerSide.PositionXMillimeters, 6);
        Assert.True(viewModel.IsDirty);
        Assert.Contains("Restoring", viewModel.TrackerNeutralizationStatusText);
    }

    [Fact]
    public async Task DisposeQueuesAllCommandNotificationsThroughSuppliedDispatcher()
    {
        var dispatches = new ConcurrentQueue<Action>();
        using var suppliedDispatchDepth = new ThreadLocal<int>(() => 0);
        var viewModel = new MountAdjustmentViewModel(
            new FakeMountAdjustmentPort(AvailableSnapshot()),
            dispatches.Enqueue);
        DrainDispatches(dispatches, suppliedDispatchDepth);

        var notifications =
            new ConcurrentQueue<(string Name, bool InSuppliedDispatch)>();
        void Observe(string name, RelayCommand command) =>
            command.CanExecuteChanged += (_, _) =>
                notifications.Enqueue((name, suppliedDispatchDepth.Value > 0));

        Observe("save", viewModel.SaveCommand);
        Observe("revert", viewModel.RevertCommand);
        Observe("calibrate-left", viewModel.CalibrateLeftCommand);
        Observe("calibrate-right", viewModel.CalibrateRightCommand);
        Observe("calibrate-both", viewModel.CalibrateBothCommand);

        await Task.Run(viewModel.Dispose);

        Assert.Empty(notifications);
        DrainDispatches(dispatches, suppliedDispatchDepth);

        Assert.Equal(
            ["save", "revert", "calibrate-left", "calibrate-right", "calibrate-both"],
            notifications.Select(notification => notification.Name));
        Assert.All(
            notifications,
            notification => Assert.True(
                notification.InSuppliedDispatch,
                $"{notification.Name} bypassed the supplied UI dispatcher."));
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.RevertCommand.CanExecute(null));
        Assert.False(viewModel.CalibrateLeftCommand.CanExecute(null));
        Assert.False(viewModel.CalibrateRightCommand.CanExecute(null));
        Assert.False(viewModel.CalibrateBothCommand.CanExecute(null));
    }

    private static MountAdjustmentViewModel NewViewModel(
        IMountAdjustmentPort port,
        Func<MountAdjustmentCalibrationTarget, bool>? canCalibrate = null) =>
        new(port, action => action(), canCalibrate);

    private static void DrainDispatches(
        ConcurrentQueue<Action> dispatches,
        ThreadLocal<int> suppliedDispatchDepth)
    {
        while (dispatches.TryDequeue(out var dispatch))
        {
            suppliedDispatchDepth.Value++;
            try
            {
                dispatch();
            }
            finally
            {
                suppliedDispatchDepth.Value--;
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the expected mount operation.");
            }

            await Task.Delay(10);
        }
    }

    private static MountAdjustmentSnapshot AvailableSnapshot(
        long revision = 0,
        MountAdjustmentTransform? leftBase = null) => new(
        revision,
        IsAvailable: true,
        HandSnapshot(leftBase ?? MountAdjustmentTransform.Identity),
        HandSnapshot(MountAdjustmentTransform.Identity),
        MountAdjustmentNeutralizationSnapshot.Inactive,
        MountAdjustmentRestoreWarningUpdate.Clear);

    private static MountAdjustmentHandSnapshot HandSnapshot(
        MountAdjustmentTransform baseMount,
        MountAdjustmentPair? applied = null,
        MountAdjustmentPair? saved = null)
    {
        var appliedValue = applied ?? MountAdjustmentPair.Identity;
        return new MountAdjustmentHandSnapshot(
            baseMount,
            appliedValue,
            saved ?? MountAdjustmentPair.Identity,
            Effective(baseMount, appliedValue));
    }

    private static MountAdjustmentTransform Effective(
        MountAdjustmentTransform baseMount,
        MountAdjustmentPair adjustments) =>
        Compose(Compose(adjustments.TrackerSide, baseMount), adjustments.ControllerSide);

    private static MountAdjustmentTransform Compose(
        MountAdjustmentTransform parent,
        MountAdjustmentTransform child) => new(
        parent.TranslationMeters +
            Vector3.Transform(child.TranslationMeters, parent.RotationXyzw),
        Quaternion.Normalize(Quaternion.Multiply(
            parent.RotationXyzw,
            child.RotationXyzw)));

    private sealed class FakeMountAdjustmentPort : IMountAdjustmentPort
    {
        private TaskCompletionSource<MountAdjustmentPortResult>? _blockedApply;
        private MountAdjustmentLiveApplyRequest? _blockedApplyRequest;
        private TaskCompletionSource? _applyEntered;
        private TaskCompletionSource<MountAdjustmentPortResult>? _blockedSave;
        private MountAdjustmentSaveRequest? _blockedSaveRequest;
        private TaskCompletionSource? _saveEntered;
        private TaskCompletionSource? _nextApply;

        public FakeMountAdjustmentPort(MountAdjustmentSnapshot snapshot)
        {
            CurrentSnapshot = snapshot;
        }

        public event EventHandler<MountAdjustmentSnapshot>? SnapshotChanged;

        public MountAdjustmentSnapshot CurrentSnapshot { get; private set; }

        public List<MountAdjustmentLiveApplyRequest> ApplyRequests { get; } = [];

        public List<MountAdjustmentSaveRequest> SaveRequests { get; } = [];

        public List<MountAdjustmentCalibrationTarget> CalibrationTargets { get; } = [];

        public List<long> RejectedApplyRevisions { get; } = [];

        public List<string> OperationLog { get; } = [];

        public bool RejectNextApply { get; set; }

        public bool RejectNextSave { get; set; }

        public Task ApplyEntered => _applyEntered?.Task ??
            throw new InvalidOperationException("No live apply is blocked.");

        public Task SaveEntered => _saveEntered?.Task ??
            throw new InvalidOperationException("No save is blocked.");

        public ValueTask<MountAdjustmentPortResult> ApplyLiveAsync(
            MountAdjustmentLiveApplyRequest request,
            CancellationToken cancellationToken = default)
        {
            ApplyRequests.Add(request);
            OperationLog.Add($"apply:{request.Revision}:{request.Hand}");
            if (_blockedApply is not null)
            {
                _blockedApplyRequest = request;
                _applyEntered!.TrySetResult();
                return new ValueTask<MountAdjustmentPortResult>(_blockedApply.Task);
            }

            return ValueTask.FromResult(ApplyResult(request));
        }

        private MountAdjustmentPortResult ApplyResult(
            MountAdjustmentLiveApplyRequest request,
            bool honorConfiguredRejection = true)
        {
            if (honorConfiguredRejection && RejectNextApply)
            {
                RejectNextApply = false;
                RejectedApplyRevisions.Add(request.Revision);
                SignalNextApply();
                return new MountAdjustmentPortResult(
                    request.Revision,
                    Succeeded: false,
                    "injected live apply failure",
                    CurrentSnapshot);
            }

            var left = CurrentSnapshot.Left;
            var right = CurrentSnapshot.Right;
            if (request.Hand == MountAdjustmentHand.Left)
            {
                left = left with
                {
                    AppliedAdjustments = request.Adjustments,
                    EffectiveMount = Effective(left.BaseMount, request.Adjustments),
                };
            }
            else
            {
                right = right with
                {
                    AppliedAdjustments = request.Adjustments,
                    EffectiveMount = Effective(right.BaseMount, request.Adjustments),
                };
            }

            CurrentSnapshot = CurrentSnapshot with
            {
                Revision = request.Revision,
                Left = left,
                Right = right,
                RestoreWarning = MountAdjustmentRestoreWarningUpdate.Unchanged,
            };
            SignalNextApply();
            return new MountAdjustmentPortResult(
                request.Revision,
                Succeeded: true,
                $"Applied revision {request.Revision}.",
                CurrentSnapshot);
        }

        public ValueTask<MountAdjustmentPortResult> SaveAsync(
            MountAdjustmentSaveRequest request,
            CancellationToken cancellationToken = default)
        {
            SaveRequests.Add(request);
            OperationLog.Add($"save:{request.Revision}");
            if (_blockedSave is not null)
            {
                _blockedSaveRequest = request;
                _saveEntered!.TrySetResult();
                return new ValueTask<MountAdjustmentPortResult>(_blockedSave.Task);
            }

            return ValueTask.FromResult(SaveResult(request));
        }

        public ValueTask RequestCalibrationAsync(
            MountAdjustmentCalibrationTarget target,
            CancellationToken cancellationToken = default)
        {
            CalibrationTargets.Add(target);
            return ValueTask.CompletedTask;
        }

        public void Publish(MountAdjustmentSnapshot snapshot)
        {
            CurrentSnapshot = snapshot;
            SnapshotChanged?.Invoke(this, snapshot);
        }

        public void BlockNextSave()
        {
            _blockedSave = new TaskCompletionSource<MountAdjustmentPortResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _saveEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void BlockNextApply()
        {
            _blockedApply = new TaskCompletionSource<MountAdjustmentPortResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _applyEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void CompleteBlockedApply()
        {
            var completion = _blockedApply ??
                throw new InvalidOperationException("No live apply is blocked.");
            var request = _blockedApplyRequest ??
                throw new InvalidOperationException("The blocked live apply has not entered.");
            _blockedApply = null;
            _blockedApplyRequest = null;
            completion.TrySetResult(ApplyResult(
                request,
                honorConfiguredRejection: false));
        }

        public void CompleteBlockedSave()
        {
            var completion = _blockedSave ??
                throw new InvalidOperationException("No save is blocked.");
            var request = _blockedSaveRequest ??
                throw new InvalidOperationException("The blocked save has not entered.");
            _blockedSave = null;
            _blockedSaveRequest = null;
            completion.TrySetResult(SaveResult(request));
        }

        public Task ObserveNextApply()
        {
            _nextApply = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return _nextApply.Task;
        }

        private MountAdjustmentPortResult SaveResult(MountAdjustmentSaveRequest request)
        {
            if (RejectNextSave)
            {
                RejectNextSave = false;
                return new MountAdjustmentPortResult(
                    request.Revision,
                    Succeeded: false,
                    "injected persistence failure",
                    CurrentSnapshot);
            }

            CurrentSnapshot = CurrentSnapshot with
            {
                Revision = request.Revision,
                Left = CurrentSnapshot.Left with { SavedAdjustments = request.Left },
                Right = CurrentSnapshot.Right with { SavedAdjustments = request.Right },
                RestoreWarning = MountAdjustmentRestoreWarningUpdate.Unchanged,
            };
            return new MountAdjustmentPortResult(
                request.Revision,
                Succeeded: true,
                $"Saved revision {request.Revision}.",
                CurrentSnapshot);
        }

        private void SignalNextApply()
        {
            _nextApply?.TrySetResult();
            _nextApply = null;
        }
    }
}
