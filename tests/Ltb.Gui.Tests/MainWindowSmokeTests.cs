using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ltb.App;
using Ltb.Gui.ViewModels;

namespace Ltb.Gui.Tests;

public sealed class MainWindowSmokeTests
{
    [AvaloniaFact]
    public void MainWindowDefaultsToInternalDriverBindingsAndRequiredSurface()
    {
        var viewModel = new InternalDriverViewModel(
            new IdleSessionFactory(),
            action => action());
        var window = new MainWindow
        {
            DataContext = viewModel,
        };
        try
        {
            window.Show();

            Assert.Same(viewModel, window.DataContext);
            Assert.Equal("Stopped", window.FindControl<TextBlock>("PhaseText")!.Text);
            Assert.Equal("Start", window.FindControl<Button>("ActionButton")!.Content);
            var readiness = window.FindControl<ListBox>("ReadinessList");
            Assert.NotNull(readiness);
            Assert.Equal(12, readiness!.ItemCount);
            Assert.Equal(
                [
                    "Windows x64",
                    "SteamVR",
                    "Driver registration",
                    "Loaded controllers / build",
                    "Meta Link",
                    "Left input",
                    "Right input",
                    "Lighthouse HMD",
                    "Tracker 1 / left",
                    "Tracker 2 / right",
                    "Profiles / calibration",
                    "Driver feed",
                ],
                viewModel.ReadinessRows.Select(row => row.Title));
            Assert.NotNull(window.FindControl<TextBlock>("DiagnosticText"));
            Assert.NotNull(window.FindControl<TextBlock>("RemediationText"));
            Assert.NotNull(window.FindControl<TextBlock>("LeftTrackerText"));
            Assert.NotNull(window.FindControl<TextBlock>("RightTrackerText"));
            Assert.NotNull(window.FindControl<TextBlock>("LeftPoseText"));
            Assert.NotNull(window.FindControl<TextBlock>("RightPoseText"));
            Assert.NotNull(window.FindControl<TextBlock>("LeftNeutralReasonText"));
            Assert.NotNull(window.FindControl<TextBlock>("RightNeutralReasonText"));
            Assert.NotNull(window.FindControl<TextBlock>("LeftCalibrationModeText"));
            Assert.NotNull(window.FindControl<TextBlock>("RightCalibrationModeText"));
            Assert.NotNull(window.FindControl<TextBlock>("LeftCalibrationReasonText"));
            Assert.NotNull(window.FindControl<TextBlock>("RightCalibrationReasonText"));
            Assert.NotNull(window.FindControl<TextBlock>("LeftCalibrationLagText"));
            Assert.NotNull(window.FindControl<TextBlock>("RightCalibrationLagText"));
            Assert.NotNull(window.FindControl<TextBlock>("LeftCalibrationQualityText"));
            Assert.NotNull(window.FindControl<TextBlock>("RightCalibrationQualityText"));
            Assert.NotNull(window.FindControl<TextBlock>("LeftCalibrationCreatedText"));
            Assert.NotNull(window.FindControl<TextBlock>("RightCalibrationCreatedText"));
            Assert.NotNull(window.FindControl<TextBlock>("LeftCaptureSamplesText"));
            Assert.NotNull(window.FindControl<TextBlock>("RightCaptureSamplesText"));
            Assert.NotNull(window.FindControl<TextBlock>("LeftCaptureValidityText"));
            Assert.NotNull(window.FindControl<TextBlock>("RightCaptureValidityText"));
            Assert.NotNull(window.FindControl<TextBlock>("LeftCaptureMotionText"));
            Assert.NotNull(window.FindControl<TextBlock>("RightCaptureMotionText"));
            Assert.NotNull(window.FindControl<ProgressBar>("LeftRotationProgress"));
            Assert.NotNull(window.FindControl<ProgressBar>("RightRotationProgress"));
            Assert.NotNull(window.FindControl<TextBlock>("LeftRotationProgressText"));
            Assert.NotNull(window.FindControl<TextBlock>("RightRotationProgressText"));
            Assert.NotNull(window.FindControl<ProgressBar>("LeftPositionProgress"));
            Assert.NotNull(window.FindControl<ProgressBar>("RightPositionProgress"));
            Assert.NotNull(window.FindControl<TextBlock>("LeftPositionProgressText"));
            Assert.NotNull(window.FindControl<TextBlock>("RightPositionProgressText"));
            var visibleText = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(text => text.Text ?? string.Empty)
                .ToArray();
            Assert.Equal(2, visibleText.Count(text => text == "Calibration evidence"));
            Assert.Equal(2, visibleText.Count(text => text == "Capture evidence"));
            Assert.Equal(2, visibleText.Count(text => text == "Rotation progress"));
            Assert.Equal(2, visibleText.Count(text => text == "Position progress"));
            Assert.DoesNotContain(
                visibleText,
                text => text.Contains("Global calibration phase estimate", StringComparison.Ordinal));
            Assert.DoesNotContain(
                visibleText,
                text => text.Contains("Not exposed", StringComparison.Ordinal));
            Assert.NotNull(window.FindControl<TextBlock>("FeedStateText"));
            Assert.NotNull(window.FindControl<TextBlock>("FeedSessionText"));
            Assert.NotNull(window.FindControl<TextBlock>("FeedSequenceText"));
            Assert.NotNull(window.FindControl<TextBlock>("FeedHeartbeatText"));
            Assert.NotNull(window.FindControl<TextBlock>("FeedReconnectText"));
            Assert.NotNull(window.FindControl<TextBlock>("FeedErrorText"));
        }
        finally
        {
            window.Close();
            viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [AvaloniaFact]
    public void MainWindowHasNoEditableLegacyTargetsAndLabelsCompileOnlyPath()
    {
        var viewModel = new InternalDriverViewModel(
            new IdleSessionFactory(),
            action => action());
        var window = new MainWindow
        {
            DataContext = viewModel,
        };
        try
        {
            window.Show();

            Assert.Empty(window.GetVisualDescendants().OfType<TextBox>());
            Assert.Null(window.FindControl<TextBox>("LeftSlotTextBox"));
            Assert.Null(window.FindControl<TextBox>("RightSlotTextBox"));
            Assert.Null(window.FindControl<TextBox>("SteamVrSettingsTextBox"));

            var notice = window.FindControl<TextBlock>("LegacyNotice");
            Assert.NotNull(notice);
            Assert.Contains("Unsupported compile-only migration code", notice!.Text);
            Assert.Contains("ALVR / VMT / TrackingOverrides", notice.Text);
            Assert.Contains("not used by Start", notice.Text);
        }
        finally
        {
            window.Close();
            viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class IdleSessionFactory : IInternalDriverSessionFactory
    {
        public IInternalDriverSession Create() => new IdleSession();
    }

    private sealed class IdleSession : IInternalDriverSession
    {
        public event EventHandler<InternalDriverSessionSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public InternalDriverSessionSnapshot CurrentSnapshot =>
            throw new NotSupportedException("The smoke test never starts a session.");

        public Task RunAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The smoke test never starts a session.");

        public ValueTask StopAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
