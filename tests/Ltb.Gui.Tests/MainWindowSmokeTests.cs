using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
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
            Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
            Assert.Equal("Stopped", window.FindControl<TextBlock>("PhaseText")!.Text);
            Assert.Equal("Start", window.FindControl<Button>("ActionButton")!.Content);
            var calibrationButton = window.FindControl<Button>("CalibrationButton");
            Assert.NotNull(calibrationButton);
            Assert.Equal("Calibrate / Recalibrate", calibrationButton!.Content);
            Assert.True(calibrationButton.IsEnabled);
            var readiness = window.FindControl<ItemsControl>("ReadinessList");
            Assert.NotNull(readiness);
            Assert.Equal(4, readiness!.ItemCount);
            Assert.Equal(12, viewModel.ReadinessRows.Count);
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
            Assert.False(window.FindControl<Border>("CalibrationWorkspace")!.IsVisible);
            Assert.False(window.FindControl<Border>("DebugDrawer")!.IsVisible);
            Assert.False(window.FindControl<ToggleSwitch>("DebugToggle")!.IsChecked);
            Assert.False(window.FindControl<ToggleSwitch>("ReducedMotionToggle")!.IsChecked);
            Assert.False(window.FindControl<Expander>("MaintenanceExpander")!.IsExpanded);
            var motionGuide =
                window.FindControl<Ltb.Gui.Controls.MotionGuideControl>("MotionGuide");
            Assert.NotNull(motionGuide);
            Assert.False(motionGuide!.IsAnimationTimerRunning);
            var statusBands = window.FindControl<Ltb.Gui.Controls.DiagnosticsPlot>("StatusBandPlot");
            Assert.NotNull(statusBands);
            Assert.Equal(6d, statusBands!.Series1Offset);
            Assert.Equal(4d, statusBands.Series2Offset);
            Assert.Equal(2d, statusBands.Series3Offset);
            Assert.Equal(0d, statusBands.Series4Offset);
            Assert.Equal(-0.5d, statusBands.Minimum);
            Assert.Equal(7.5d, statusBands.Maximum);
            Assert.NotNull(
                window.FindControl<Ltb.Gui.Controls.DiagnosticsPlot>("IterationIntervalPlot"));
            Assert.NotNull(
                window.FindControl<Ltb.Gui.Controls.DiagnosticsPlot>("ManagedWorkDurationPlot"));
            Assert.NotNull(
                window.FindControl<Ltb.Gui.Controls.DiagnosticsPlot>("HostIngressAgePlot"));
            var timingScope = window.FindControl<TextBlock>("TimingScopeText");
            Assert.NotNull(timingScope);
            Assert.Contains(
                "Software lower bound only",
                timingScope!.Text,
                StringComparison.Ordinal);
            Assert.Contains("hardware/device acquisition", timingScope.Text, StringComparison.Ordinal);
            Assert.Contains("SteamVR compositor", timingScope.Text, StringComparison.Ordinal);
            Assert.Contains("display scanout", timingScope.Text, StringComparison.Ordinal);
            var visibleText = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(text => text.Text ?? string.Empty)
                .ToArray();
            Assert.Equal(2, visibleText.Count(text => text == "Rotation progress"));
            Assert.Equal(
                2,
                visibleText.Count(text => text == "Position tracking availability (optional)"));
            Assert.DoesNotContain(visibleText, text => text == "Position progress");
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
    public void MotionGuideTimerRunsOnlyWhileVisibleAndMotionIsEnabled()
    {
        var guide = new Ltb.Gui.Controls.MotionGuideControl();
        var window = new Window
        {
            Content = guide,
        };
        try
        {
            window.Show();
            Assert.True(guide.IsAnimationTimerRunning);

            guide.ReduceMotion = true;
            Assert.False(guide.IsAnimationTimerRunning);

            guide.ReduceMotion = false;
            Assert.True(guide.IsAnimationTimerRunning);

            guide.IsVisible = false;
            Assert.False(guide.IsAnimationTimerRunning);

            guide.IsVisible = true;
            Assert.True(guide.IsAnimationTimerRunning);
        }
        finally
        {
            window.Close();
            Assert.False(guide.IsAnimationTimerRunning);
        }
    }

    [AvaloniaFact]
    public void PrimaryActionsRemainPinnedAndUsableAtMinimumSizeWithLargeText()
    {
        var viewModel = new InternalDriverViewModel(
            new IdleSessionFactory(),
            action => action());
        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = 760,
            Height = 620,
            FontSize = 20,
        };
        try
        {
            window.Show();

            var scrollViewer = window.FindControl<ScrollViewer>("EvidenceScrollViewer");
            var calibration = window.FindControl<Button>("CalibrationButton");
            var action = window.FindControl<Button>("ActionButton");
            Assert.NotNull(scrollViewer);
            Assert.NotNull(calibration);
            Assert.NotNull(action);
            Assert.True(calibration!.Bounds.Width >= calibration.MinWidth);
            Assert.True(action!.Bounds.Width >= action.MinWidth);
            Assert.DoesNotContain(scrollViewer!, calibration.GetVisualAncestors());
            Assert.DoesNotContain(scrollViewer, action.GetVisualAncestors());
            Assert.Equal(ScrollBarVisibility.Disabled, scrollViewer.HorizontalScrollBarVisibility);
            Assert.True(window.Bounds.Width >= window.MinWidth);
            Assert.True(window.Bounds.Height >= window.MinHeight);
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
        public IInternalDriverSession Create(InternalDriverSessionIntent intent) =>
            new IdleSession();
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
