using Ltb.App;
using Ltb.Gui.ViewModels;

namespace Ltb.Gui.Tests;

public sealed class CalibrationWizardViewModelTests : IDisposable
{
    private readonly string _tempDirectory =
        Directory.CreateTempSubdirectory("ltb-gui-tests-").FullName;

    private string ProfileStorePath => Path.Combine(_tempDirectory, "profiles.json");

    public void Dispose() => Directory.Delete(_tempDirectory, recursive: true);

    [Fact]
    public async Task ScriptedRunReachesActiveStateAndAccumulatesEvents()
    {
        using var viewModel = NewScriptedViewModel();

        await viewModel.StartAsync();

        Assert.Equal(CalibrationWizardState.Active, viewModel.CurrentState);
        Assert.False(viewModel.IsRunning);
        Assert.StartsWith(
            "wizard_result: success profile_path=first-run-capture",
            viewModel.ResultSummary,
            StringComparison.Ordinal);
        Assert.Contains(
            viewModel.Events,
            line => line.StartsWith("state: DependencyCheck", StringComparison.Ordinal));
        Assert.Contains(
            viewModel.Events,
            line => line.StartsWith("state: Recording", StringComparison.Ordinal));
        Assert.Contains(
            viewModel.Events,
            line => line.StartsWith("state: Active", StringComparison.Ordinal));
        Assert.Contains(
            viewModel.Events,
            line => line.StartsWith("association: hand=left", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScriptedRunUpdatesPerHandCoverage()
    {
        using var viewModel = NewScriptedViewModel();

        await viewModel.StartAsync();

        Assert.Equal(360, viewModel.LeftHand.SampleCount);
        Assert.Equal(360, viewModel.RightHand.SampleCount);
        Assert.True(viewModel.LeftHand.RotationReady);
        Assert.True(viewModel.RightHand.RotationReady);
        Assert.True(viewModel.LeftHand.RotationProgress > 0d);
        Assert.True(viewModel.RightHand.RotationProgress > 0d);

        // The scripted capture provides controller position only on the left
        // hand, so only the left position gate can pass.
        Assert.True(viewModel.LeftHand.PositionReady);
        Assert.False(viewModel.RightHand.PositionReady);
    }

    [Fact]
    public async Task SecondRunReusesPersistedProfiles()
    {
        using var viewModel = NewScriptedViewModel();

        await viewModel.StartAsync();
        await viewModel.StartAsync();

        Assert.Equal(CalibrationWizardState.Active, viewModel.CurrentState);
        Assert.StartsWith(
            "wizard_result: success profile_path=later-run-reuse",
            viewModel.ResultSummary,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StateChangeRaisesPropertyChangedAndAppendsEvent()
    {
        using var viewModel = NewScriptedViewModel();
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        ICalibrationWizardOutput output = viewModel;
        output.OnStateChanged(CalibrationWizardState.Recording, "left-hand capture");

        Assert.Equal(CalibrationWizardState.Recording, viewModel.CurrentState);
        Assert.Equal("left-hand capture", viewModel.CurrentDiagnostic);
        Assert.Contains(nameof(CalibrationWizardViewModel.CurrentState), changed);
        Assert.Contains(nameof(CalibrationWizardViewModel.CurrentDiagnostic), changed);
        Assert.Equal(
            "state: Recording (left-hand capture)",
            Assert.Single(viewModel.Events));
    }

    [Fact]
    public void CaptureProgressRoutesToMatchingHandOnly()
    {
        using var viewModel = NewScriptedViewModel();

        ICalibrationWizardOutput output = viewModel;
        output.OnCaptureProgress(new CalibrationWizardCaptureProgress(
            CalibrationWizardHand.Right,
            SampleCount: 42,
            OrientationTrackingValidFraction: 1d,
            PositionTrackingValidFraction: 0.5d,
            MotionAxisCoverage: 0.8d,
            TotalRotationDegrees: 210d,
            RotationProgress: 0.75d,
            PositionProgress: 0.25d,
            RotationReady: true,
            PositionReady: false,
            Diagnostic: "keep rotating"));

        Assert.Equal(42, viewModel.RightHand.SampleCount);
        Assert.Equal(0.75d, viewModel.RightHand.RotationProgress);
        Assert.True(viewModel.RightHand.RotationReady);
        Assert.False(viewModel.RightHand.PositionReady);
        Assert.Equal("keep rotating", viewModel.RightHand.Diagnostic);
        Assert.Equal(0, viewModel.LeftHand.SampleCount);
        Assert.Equal("no capture yet", viewModel.LeftHand.Diagnostic);
    }

    [Fact]
    public async Task CommandsGateOnRunState()
    {
        var session = new BlockingSession();
        using var viewModel = NewViewModel(session);
        Assert.True(viewModel.StartCommand.CanExecute(null));
        Assert.False(viewModel.AbortCommand.CanExecute(null));

        var runTask = viewModel.StartAsync();
        await session.Started;
        Assert.True(viewModel.IsRunning);
        Assert.False(viewModel.StartCommand.CanExecute(null));
        Assert.True(viewModel.AbortCommand.CanExecute(null));

        await viewModel.StopAsync();
        await runTask;
        Assert.False(viewModel.IsRunning);
        Assert.True(viewModel.StartCommand.CanExecute(null));
        Assert.False(viewModel.AbortCommand.CanExecute(null));
        Assert.Equal(
            "wizard_result: cancelled before completion",
            viewModel.ResultSummary);
    }

    [Fact]
    public async Task FailedSessionSurfacesErrorWithoutThrowing()
    {
        using var viewModel = NewViewModel(new ThrowingSession());

        await viewModel.StartAsync();

        Assert.False(viewModel.IsRunning);
        Assert.Equal("wizard_error: session exploded", viewModel.ResultSummary);
        Assert.Contains("wizard_error: session exploded", viewModel.Events);
    }

    private CalibrationWizardViewModel NewScriptedViewModel() =>
        new(
            new CalibrationWizardSessionFactory(),
            new GuiCommandLineOptions { ProfileStorePath = ProfileStorePath });

    private CalibrationWizardViewModel NewViewModel(ICalibrationWizardSession session) =>
        new(
            new FixedSessionFactory(session),
            new GuiCommandLineOptions { ProfileStorePath = ProfileStorePath });

    private sealed class FixedSessionFactory : ICalibrationWizardSessionFactory
    {
        private readonly ICalibrationWizardSession _session;

        public FixedSessionFactory(ICalibrationWizardSession session)
        {
            _session = session;
        }

        public ICalibrationWizardSession CreateScripted(
            string profileStorePath,
            string? logPath) => _session;

        public ICalibrationWizardSession CreateProduction(
            ProductionCalibrationWizardSessionOptions options) => _session;
    }

    private sealed class BlockingSession : ICalibrationWizardSession
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public async Task<CalibrationWizardResult> RunAsync(
            ICalibrationWizardOutput output,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidOperationException("The blocking session cannot complete.");
        }
    }

    private sealed class ThrowingSession : ICalibrationWizardSession
    {
        public Task<CalibrationWizardResult> RunAsync(
            ICalibrationWizardOutput output,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("session exploded");
    }
}
