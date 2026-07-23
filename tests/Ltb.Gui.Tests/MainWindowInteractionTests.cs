using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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

    private static void AssertCompletes(Task task, string message) =>
        Assert.True(task.Wait(InteractionTimeout), message);

    private static void Click(MainWindow window, Button button)
    {
        Assert.True(button.Bounds.Width > 0, "The ActionButton must have a laid-out width.");
        Assert.True(button.Bounds.Height > 0, "The ActionButton must have a laid-out height.");

        var center = button.TranslatePoint(
            new Point(button.Bounds.Width / 2d, button.Bounds.Height / 2d),
            window);
        Assert.True(center.HasValue, "The ActionButton must be attached to the shown window.");

        window.MouseDown(center.Value, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(center.Value, MouseButton.Left, RawInputModifiers.None);
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
