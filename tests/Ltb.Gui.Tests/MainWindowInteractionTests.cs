using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using System.Numerics;
using Ltb.App;
using Ltb.Gui.ViewModels;

namespace Ltb.Gui.Tests;

public sealed class MainWindowInteractionTests
{
    private static readonly TimeSpan InteractionTimeout = TimeSpan.FromSeconds(2);

    [AvaloniaFact]
    public void ActionButtonMouseClicksStartAndStopControlledSession()
    {
        var session = new ControlledSession();
        var factory = new ControlledSessionFactory(session);
        var viewModel = new InternalDriverViewModel(factory, action => action());
        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        try
        {
            window.Show();
            var button = window.FindControl<Button>("ActionButton")!;

            Click(window, button);
            AssertCompletes(session.Started, "The click did not start the controlled session.");

            Assert.Equal(1, factory.CreateCount);
            Assert.Equal("Stop", button.Content);
            Assert.Equal("Stop", viewModel.ActionButtonText);
            Assert.Equal(InternalDriverSessionState.DependencyCheck, viewModel.CurrentPhase);
            Assert.Equal("Dependency Check", viewModel.PhaseText);
            Assert.Equal(
                "Dependency Check",
                window.FindControl<TextBlock>("PhaseText")!.Text);

            Click(window, button);
            Assert.True(
                SpinWait.SpinUntil(
                    () => session.DisposeCallCount == 1 &&
                        viewModel.ActionButtonText == "Start" &&
                        viewModel.CurrentPhase == InternalDriverSessionState.Stopped,
                    InteractionTimeout),
                "The second click did not complete bounded stop and disposal.");

            Assert.Equal(1, session.StopCallCount);
            Assert.Equal(1, session.DisposeCallCount);
            Assert.Equal("Start", button.Content);
            Assert.Equal("Start", viewModel.ActionButtonText);
            Assert.Equal(InternalDriverSessionState.Stopped, viewModel.CurrentPhase);
            Assert.Equal("Stopped", viewModel.PhaseText);
            Assert.Equal("Stopped", viewModel.OverallStatus);
            Assert.Equal("Stopped", window.FindControl<TextBlock>("PhaseText")!.Text);
        }
        finally
        {
            session.AllowRunExit();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ActionButtonMouseClickSurfacesFactoryCreationFailure()
    {
        var viewModel = new InternalDriverViewModel(
            new ThrowingSessionFactory(),
            action => action());
        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        try
        {
            window.Show();
            var button = window.FindControl<Button>("ActionButton")!;

            Click(window, button);

            Assert.Equal("Start", button.Content);
            Assert.Equal(InternalDriverSessionState.Faulted, viewModel.CurrentPhase);
            Assert.Equal("Faulted", viewModel.PhaseText);
            Assert.Equal("Action required", viewModel.OverallStatus);
            Assert.Contains("Unable to create", viewModel.Diagnostic, StringComparison.Ordinal);
            Assert.Contains("synthetic factory failure", viewModel.Diagnostic, StringComparison.Ordinal);
            Assert.Contains("correct the problem", viewModel.Remediation, StringComparison.Ordinal);
            Assert.Equal(viewModel.Diagnostic, viewModel.LastError);
            Assert.Equal("Faulted", window.FindControl<TextBlock>("PhaseText")!.Text);
            Assert.Equal(
                viewModel.Diagnostic,
                window.FindControl<TextBlock>("DiagnosticText")!.Text);
            Assert.Equal(
                viewModel.Remediation,
                window.FindControl<TextBlock>("RemediationText")!.Text);
            Assert.Equal(
                $"Last error: {viewModel.LastError}",
                window.FindControl<TextBlock>("LastErrorText")!.Text);
        }
        finally
        {
            window.Close();
            AssertCompletes(
                viewModel.DisposeAsync().AsTask(),
                "The failure test ViewModel did not close cleanly.");
        }
    }

    [AvaloniaFact]
    public void CalibrationButtonMouseClickStartsExplicitCalibrationAndSharesStopFlow()
    {
        var session = new ControlledSession();
        var factory = new ControlledSessionFactory(session);
        var viewModel = new InternalDriverViewModel(factory, action => action());
        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        try
        {
            window.Show();
            var calibrationButton = window.FindControl<Button>("CalibrationButton")!;
            var actionButton = window.FindControl<Button>("ActionButton")!;

            Click(window, calibrationButton);
            AssertCompletes(
                session.Started,
                "The calibration click did not start the controlled session.");

            Assert.Equal([InternalDriverSessionIntent.Calibrate], factory.Intents);
            Assert.False(viewModel.CalibrationCommand.CanExecute(null));
            Assert.Equal("Stop", actionButton.Content);

            Click(window, actionButton);
            Assert.True(
                SpinWait.SpinUntil(
                    () => session.DisposeCallCount == 1 &&
                        viewModel.ActionButtonText == "Start" &&
                        viewModel.CurrentPhase == InternalDriverSessionState.Stopped,
                    InteractionTimeout),
                "Stop did not complete the explicit calibration session.");

            Assert.True(viewModel.CalibrationCommand.CanExecute(null));
            Assert.Equal(1, session.StopCallCount);
            Assert.Equal(1, session.DisposeCallCount);
        }
        finally
        {
            session.AllowRunExit();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MountAdjustmentControlsClampStepResetApplyLiveAndSaveExplicitly()
    {
        var port = new ControlledMountAdjustmentPort(CreateMountSnapshot());
        var viewModel = new InternalDriverViewModel(
            new ControlledSessionFactory(new ControlledSession()),
            action => action(),
            mountAdjustmentPort: port);
        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        try
        {
            window.Show();
            var positionX = window.FindControl<TextBox>("LeftTrackerPositionXTextBox")!;
            var decrement =
                window.FindControl<Button>("LeftTrackerPositionXDecrementButton")!;
            var increment =
                window.FindControl<Button>("LeftTrackerPositionXIncrementButton")!;
            var rotationIncrement =
                window.FindControl<Button>("LeftTrackerRotationXIncrementButton")!;
            var rotationDecrement =
                window.FindControl<Button>("LeftTrackerRotationXDecrementButton")!;
            var reset = window.FindControl<Button>("LeftTrackerResetButton")!;
            var save = window.FindControl<Button>("SaveMountAdjustmentsButton")!;

            Assert.False(viewModel.MountAdjustments.IsDirty);
            Assert.False(viewModel.MountAdjustments.SaveCommand.CanExecute(null));
            Assert.False(save.IsEffectivelyEnabled);

            positionX.Text = "600";
            Assert.True(
                SpinWait.SpinUntil(
                    () => port.ApplyRequests.Count >= 1,
                    InteractionTimeout),
                "Editing the numeric field did not dispatch a live adjustment.");
            Assert.Equal(
                MountAdjustmentViewModel.MaximumTranslationMillimeters,
                viewModel.MountAdjustments.LeftHand.TrackerSide.PositionXMillimeters,
                6);
            Assert.Equal(
                0.5f,
                port.ApplyRequests[^1].Adjustments.TrackerSide.TranslationMeters.X,
                6);
            Assert.True(viewModel.MountAdjustments.IsDirty);
            Assert.True(
                window.FindControl<Border>("MountAdjustmentDirtyIndicator")!.IsVisible);

            var applyCount = port.ApplyRequests.Count;
            ExecuteBoundCommand(decrement);
            Assert.True(
                SpinWait.SpinUntil(
                    () => port.ApplyRequests.Count > applyCount,
                    InteractionTimeout),
                "The visible decrement control did not dispatch.");
            Assert.Equal(
                499d,
                viewModel.MountAdjustments.LeftHand.TrackerSide.PositionXMillimeters,
                6);

            applyCount = port.ApplyRequests.Count;
            ExecuteBoundCommand(increment);
            Assert.True(
                SpinWait.SpinUntil(
                    () => port.ApplyRequests.Count > applyCount,
                    InteractionTimeout),
                "The visible increment control did not dispatch.");
            Assert.Equal(
                500d,
                viewModel.MountAdjustments.LeftHand.TrackerSide.PositionXMillimeters,
                6);

            applyCount = port.ApplyRequests.Count;
            ExecuteBoundCommand(rotationIncrement);
            Assert.True(
                SpinWait.SpinUntil(
                    () => port.ApplyRequests.Count > applyCount,
                    InteractionTimeout),
                "The rotation increment control did not dispatch.");
            Assert.Equal(
                1d,
                viewModel.MountAdjustments.LeftHand.TrackerSide.RotationXDegrees,
                6);
            ExecuteBoundCommand(rotationDecrement);
            Assert.Equal(
                0d,
                viewModel.MountAdjustments.LeftHand.TrackerSide.RotationXDegrees,
                6);

            applyCount = port.ApplyRequests.Count;
            ExecuteBoundCommand(reset);
            Assert.True(
                SpinWait.SpinUntil(
                    () => port.ApplyRequests.Count > applyCount,
                    InteractionTimeout),
                "Reset did not dispatch the identity adjustment.");
            Assert.Equal(
                0d,
                viewModel.MountAdjustments.LeftHand.TrackerSide.PositionXMillimeters,
                6);
            Assert.False(viewModel.MountAdjustments.IsDirty);

            ExecuteBoundCommand(increment);
            Assert.True(
                SpinWait.SpinUntil(
                    () => viewModel.MountAdjustments.IsDirty &&
                        viewModel.MountAdjustments.SaveCommand.CanExecute(null) &&
                        port.ApplyRequests[^1].Adjustments.TrackerSide.TranslationMeters.X > 0f,
                    InteractionTimeout),
                "A fresh edit did not become dirty and saveable.");
            Assert.Contains(
                "t=(1.0, 0.0, 0.0) mm",
                window.FindControl<TextBlock>("LeftEffectiveMountTransformText")!.Text,
                StringComparison.Ordinal);

            ExecuteBoundCommand(save);
            Assert.True(
                SpinWait.SpinUntil(
                    () => port.SaveRequests.Count == 1 &&
                        !viewModel.MountAdjustments.IsDirty,
                    InteractionTimeout),
                "Save did not persist and acknowledge the current revision.");
            Assert.False(viewModel.MountAdjustments.SaveCommand.CanExecute(null));
            Assert.False(save.IsEffectivelyEnabled);
            Assert.Equal(
                "No unsaved mount adjustments.",
                window.FindControl<TextBlock>("MountAdjustmentDirtyText")!.Text);
        }
        finally
        {
            window.Close();
            AssertCompletes(
                viewModel.DisposeAsync().AsTask(),
                "The mount-adjustment test ViewModel did not close cleanly.");
        }
    }

    [AvaloniaFact]
    public void SelectedCalibrationActionsDispatchOnlyWhileStopped()
    {
        var session = new ControlledSession();
        var port = new ControlledMountAdjustmentPort(CreateMountSnapshot());
        var viewModel = new InternalDriverViewModel(
            new ControlledSessionFactory(session),
            action => action(),
            mountAdjustmentPort: port);
        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        try
        {
            window.Show();
            var left = window.FindControl<Button>("CalibrateLeftMountButton")!;
            var right = window.FindControl<Button>("CalibrateRightMountButton")!;
            var both = window.FindControl<Button>("CalibrateBothMountButton")!;
            var action = window.FindControl<Button>("ActionButton")!;

            Assert.True(left.IsEffectivelyEnabled);
            Assert.True(right.IsEffectivelyEnabled);
            Assert.True(both.IsEffectivelyEnabled);

            Click(window, left);
            Assert.True(
                SpinWait.SpinUntil(
                    () => port.CalibrationTargets.Count == 1,
                    InteractionTimeout),
                "The left-only calibration action did not dispatch.");
            Assert.Equal(MountAdjustmentCalibrationTarget.Left, port.CalibrationTargets[0]);

            Click(window, action);
            AssertCompletes(session.Started, "The controlled session did not start.");
            Assert.False(left.IsEffectivelyEnabled);
            Assert.False(right.IsEffectivelyEnabled);
            Assert.False(both.IsEffectivelyEnabled);

            Click(window, action);
            Assert.True(
                SpinWait.SpinUntil(
                    () => session.DisposeCallCount == 1 &&
                        viewModel.CurrentPhase == InternalDriverSessionState.Stopped &&
                        viewModel.MountAdjustments.CalibrateLeftCommand.CanExecute(null) &&
                        viewModel.MountAdjustments.CalibrateRightCommand.CanExecute(null) &&
                        viewModel.MountAdjustments.CalibrateBothCommand.CanExecute(null),
                    InteractionTimeout),
                "The controlled session did not stop.");
            ExecuteBoundCommand(right);
            ExecuteBoundCommand(both);
            Assert.True(
                SpinWait.SpinUntil(
                    () => port.CalibrationTargets.Count == 3,
                    InteractionTimeout),
                "The right-only and both-hand actions did not dispatch.");
            Assert.Equal(
                [
                    MountAdjustmentCalibrationTarget.Left,
                    MountAdjustmentCalibrationTarget.Right,
                    MountAdjustmentCalibrationTarget.Both,
                ],
                port.CalibrationTargets);
        }
        finally
        {
            session.AllowRunExit();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void NeutralizationStateAndRestoreFailureWarningRemainConspicuousUntilCleared()
    {
        var initial = CreateMountSnapshot(
            neutralization: new MountAdjustmentNeutralizationSnapshot(
                MountAdjustmentNeutralizationPhase.RestoreFailed,
                "Tracker roles could not be restored."),
            warning: MountAdjustmentRestoreWarningUpdate.Failure(
                "Restore failed: inspect SteamVR tracker roles before continuing."));
        var port = new ControlledMountAdjustmentPort(initial);
        var viewModel = new InternalDriverViewModel(
            new ControlledSessionFactory(new ControlledSession()),
            action => action(),
            mountAdjustmentPort: port);
        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        try
        {
            window.Show();
            var neutralization =
                window.FindControl<TextBlock>("TrackerNeutralizationStatusText")!;
            var warning =
                window.FindControl<Border>("MountAdjustmentRestoreFailureWarning")!;
            var warningText =
                window.FindControl<TextBlock>("MountAdjustmentRestoreFailureWarningText")!;

            Assert.Contains("Restore Failed", neutralization.Text, StringComparison.Ordinal);
            Assert.Contains(
                "Tracker roles could not be restored.",
                neutralization.Text,
                StringComparison.Ordinal);
            Assert.True(warning.IsVisible);
            Assert.Equal(
                "Restore failed: inspect SteamVR tracker roles before continuing.",
                warningText.Text);

            port.Publish(initial with
            {
                Neutralization = new MountAdjustmentNeutralizationSnapshot(
                    MountAdjustmentNeutralizationPhase.Restored,
                    "Tracker roles restored after teardown."),
                RestoreWarning = MountAdjustmentRestoreWarningUpdate.Unchanged,
            });
            Assert.Contains("Restored", neutralization.Text, StringComparison.Ordinal);
            Assert.True(warning.IsVisible);
            Assert.Contains("Restore failed", warningText.Text, StringComparison.Ordinal);

            port.Publish(initial with
            {
                Revision = 1,
                Neutralization = MountAdjustmentNeutralizationSnapshot.Inactive,
                RestoreWarning = MountAdjustmentRestoreWarningUpdate.Clear,
            });
            Assert.False(warning.IsVisible);
            Assert.Equal(string.Empty, warningText.Text);
        }
        finally
        {
            window.Close();
            AssertCompletes(
                viewModel.DisposeAsync().AsTask(),
                "The restore-warning test ViewModel did not close cleanly.");
        }
    }

    private static void AssertCompletes(Task task, string message) =>
        Assert.True(task.Wait(InteractionTimeout), message);

    private static void Click(MainWindow window, Button button)
    {
        button.BringIntoView();
        window.UpdateLayout();
        Assert.True(button.Bounds.Width > 0, "The ActionButton must have a laid-out width.");
        Assert.True(button.Bounds.Height > 0, "The ActionButton must have a laid-out height.");

        var center = button.TranslatePoint(
            new Point(button.Bounds.Width / 2d, button.Bounds.Height / 2d),
            window);
        Assert.True(center.HasValue, "The ActionButton must be attached to the shown window.");

        window.MouseDown(center.Value, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(center.Value, MouseButton.Left, RawInputModifiers.None);
    }

    private static void ExecuteBoundCommand(Button button)
    {
        Assert.NotNull(button.Command);
        Assert.True(button.Command!.CanExecute(button.CommandParameter));
        button.Command.Execute(button.CommandParameter);
    }

    private static MountAdjustmentSnapshot CreateMountSnapshot(
        MountAdjustmentNeutralizationSnapshot? neutralization = null,
        MountAdjustmentRestoreWarningUpdate? warning = null) =>
        new(
            Revision: 0,
            IsAvailable: true,
            Left: MountAdjustmentHandSnapshot.Identity,
            Right: MountAdjustmentHandSnapshot.Identity,
            Neutralization: neutralization ?? MountAdjustmentNeutralizationSnapshot.Inactive,
            RestoreWarning: warning ?? MountAdjustmentRestoreWarningUpdate.Clear);

    private sealed class ControlledMountAdjustmentPort(MountAdjustmentSnapshot initial)
        : IMountAdjustmentPort
    {
        private readonly List<MountAdjustmentLiveApplyRequest> _applyRequests = [];
        private readonly List<MountAdjustmentSaveRequest> _saveRequests = [];
        private readonly List<MountAdjustmentCalibrationTarget> _calibrationTargets = [];

        public event EventHandler<MountAdjustmentSnapshot>? SnapshotChanged;

        public MountAdjustmentSnapshot CurrentSnapshot { get; private set; } = initial;

        public IReadOnlyList<MountAdjustmentLiveApplyRequest> ApplyRequests =>
            _applyRequests;

        public IReadOnlyList<MountAdjustmentSaveRequest> SaveRequests =>
            _saveRequests;

        public IReadOnlyList<MountAdjustmentCalibrationTarget> CalibrationTargets =>
            _calibrationTargets;

        public ValueTask<MountAdjustmentPortResult> ApplyLiveAsync(
            MountAdjustmentLiveApplyRequest request,
            CancellationToken cancellationToken = default)
        {
            _applyRequests.Add(request);
            var source = request.Hand == MountAdjustmentHand.Left
                ? CurrentSnapshot.Left
                : CurrentSnapshot.Right;
            var effective = request.Adjustments.TrackerSide;
            var updatedHand = source with
            {
                AppliedAdjustments = request.Adjustments,
                EffectiveMount = effective,
            };
            CurrentSnapshot = CurrentSnapshot with
            {
                Revision = request.Revision,
                Left = request.Hand == MountAdjustmentHand.Left
                    ? updatedHand
                    : CurrentSnapshot.Left,
                Right = request.Hand == MountAdjustmentHand.Right
                    ? updatedHand
                    : CurrentSnapshot.Right,
                RestoreWarning = MountAdjustmentRestoreWarningUpdate.Unchanged,
            };
            return ValueTask.FromResult(new MountAdjustmentPortResult(
                request.Revision,
                Succeeded: true,
                "Applied by the controlled mount-adjustment port.",
                CurrentSnapshot));
        }

        public ValueTask<MountAdjustmentPortResult> SaveAsync(
            MountAdjustmentSaveRequest request,
            CancellationToken cancellationToken = default)
        {
            _saveRequests.Add(request);
            CurrentSnapshot = CurrentSnapshot with
            {
                Revision = request.Revision,
                Left = CurrentSnapshot.Left with
                {
                    AppliedAdjustments = request.Left,
                    SavedAdjustments = request.Left,
                },
                Right = CurrentSnapshot.Right with
                {
                    AppliedAdjustments = request.Right,
                    SavedAdjustments = request.Right,
                },
                RestoreWarning = MountAdjustmentRestoreWarningUpdate.Unchanged,
            };
            return ValueTask.FromResult(new MountAdjustmentPortResult(
                request.Revision,
                Succeeded: true,
                "Saved by the controlled mount-adjustment port.",
                CurrentSnapshot));
        }

        public ValueTask RequestCalibrationAsync(
            MountAdjustmentCalibrationTarget target,
            CancellationToken cancellationToken = default)
        {
            _calibrationTargets.Add(target);
            return ValueTask.CompletedTask;
        }

        public void Publish(MountAdjustmentSnapshot snapshot)
        {
            CurrentSnapshot = snapshot;
            SnapshotChanged?.Invoke(this, snapshot);
        }
    }

    private sealed class ControlledSessionFactory(IInternalDriverSession session)
        : IInternalDriverSessionFactory
    {
        private readonly List<InternalDriverSessionIntent> _intents = [];

        public int CreateCount { get; private set; }

        public IReadOnlyList<InternalDriverSessionIntent> Intents => _intents;

        public IInternalDriverSession Create(InternalDriverSessionIntent intent)
        {
            CreateCount++;
            _intents.Add(intent);
            return session;
        }
    }

    private sealed class ThrowingSessionFactory : IInternalDriverSessionFactory
    {
        public IInternalDriverSession Create(InternalDriverSessionIntent intent) =>
            throw new InvalidOperationException("synthetic factory failure");
    }

    private sealed class ControlledSession : IInternalDriverSession
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _runExit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<InternalDriverSessionSnapshot>? SnapshotChanged;

        public InternalDriverSessionSnapshot CurrentSnapshot { get; private set; } =
            CreateDependencyCheckSnapshot();

        public Task Started => _started.Task;

        public int StopCallCount { get; private set; }

        public int DisposeCallCount { get; private set; }

        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await _runExit.Task.ConfigureAwait(false);
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            StopCallCount++;
            CurrentSnapshot = InternalDriverSessionSnapshot.Initial;
            SnapshotChanged?.Invoke(this, CurrentSnapshot);
            _runExit.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            return ValueTask.CompletedTask;
        }

        public void AllowRunExit() => _runExit.TrySetResult();

        private static InternalDriverSessionSnapshot CreateDependencyCheckSnapshot() =>
            InternalDriverSessionSnapshot.Initial with
            {
                State = InternalDriverSessionState.DependencyCheck,
                Diagnostic = "Controlled dependency checks are running.",
                Remediation = "Wait for controlled readiness.",
            };
    }
}
