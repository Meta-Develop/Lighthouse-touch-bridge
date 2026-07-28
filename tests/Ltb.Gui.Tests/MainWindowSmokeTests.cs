using Avalonia;
using Avalonia.Automation;
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
            Assert.Equal("Alt+C", calibrationButton.HotKey!.ToString());
            Assert.Equal(
                "Alt+S",
                window.FindControl<Button>("ActionButton")!.HotKey!.ToString());
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
            Assert.NotNull(window.FindControl<Border>("TrackerBindingPanel"));
            Assert.NotNull(window.FindControl<ComboBox>("LeftTrackerBindingComboBox"));
            Assert.NotNull(window.FindControl<ComboBox>("RightTrackerBindingComboBox"));
            Assert.NotNull(window.FindControl<Button>("SaveTrackerBindingButton"));
            Assert.NotNull(window.FindControl<Button>("ClearTrackerBindingButton"));
            Assert.NotNull(window.FindControl<Button>("RefreshTrackerBindingButton"));
            Assert.Equal(
                "F5",
                window.FindControl<Button>("RefreshTrackerBindingButton")!.HotKey!.ToString());
            var unregisterOnExit =
                window.FindControl<ToggleSwitch>("UnregisterOnExitToggle");
            Assert.NotNull(unregisterOnExit);
            Assert.True(unregisterOnExit!.IsChecked);
            Assert.Equal(
                "Unregister driver_ltb on Stop or exit",
                unregisterOnExit.Content);
            var unregisterConsequence =
                window.FindControl<TextBlock>("UnregisterOnExitConsequenceText");
            Assert.NotNull(unregisterConsequence);
            Assert.Contains(
                "next Start re-registers driver_ltb",
                unregisterConsequence!.Text,
                StringComparison.Ordinal);
            Assert.Contains(
                "may require one SteamVR restart",
                unregisterConsequence.Text,
                StringComparison.Ordinal);
            Assert.Contains(
                "do not disappear live",
                unregisterConsequence.Text,
                StringComparison.Ordinal);
            Assert.NotNull(window.FindControl<Button>("SaveUnregisterOnExitButton"));
            Assert.NotNull(window.FindControl<Border>("ManualBindingVerificationPanel"));
            Assert.False(
                window.FindControl<WrapPanel>("ManualBindingCorrectionActions")!.IsVisible);
            Assert.NotNull(window.FindControl<Button>("RetainManualBindingButton"));
            Assert.NotNull(window.FindControl<Button>("AcceptBindingCorrectionButton"));
            Assert.NotNull(window.FindControl<TextBlock>("NextStartRegistrationStateText"));
            Assert.NotNull(window.FindControl<TextBlock>("SteamVrProcessGateText"));
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

            Assert.NotNull(window.FindControl<Border>("MountAdjustmentPanel"));
            var mountExpander =
                window.FindControl<Expander>("MountAdjustmentExpander")!;
            Assert.False(mountExpander.IsExpanded);
            Assert.NotNull(window.FindControl<Border>("LeftMountAdjustment"));
            Assert.NotNull(window.FindControl<Border>("RightMountAdjustment"));
            Assert.NotNull(window.FindControl<Border>("LeftTrackerSideAdjustment"));
            Assert.NotNull(window.FindControl<Border>("LeftControllerSideAdjustment"));
            Assert.NotNull(window.FindControl<Border>("RightTrackerSideAdjustment"));
            Assert.NotNull(window.FindControl<Border>("RightControllerSideAdjustment"));
            Assert.Equal(
                viewModel.MountAdjustments.StatusText,
                window.FindControl<TextBlock>("MountAdjustmentStatusText")!.Text);
            var axisOrderHelp =
                window.FindControl<TextBlock>("MountAdjustmentAxisOrderHelpText");
            Assert.NotNull(axisOrderHelp);
            Assert.Equal(viewModel.MountAdjustments.AxisOrderHelpText, axisOrderHelp!.Text);
            Assert.Contains("+X right", axisOrderHelp.Text, StringComparison.Ordinal);
            Assert.Contains("+Y up", axisOrderHelp.Text, StringComparison.Ordinal);
            Assert.Contains("-Z forward", axisOrderHelp.Text, StringComparison.Ordinal);
            Assert.Contains(
                "Intrinsic local rotation order is X then Y then Z",
                axisOrderHelp.Text,
                StringComparison.Ordinal);
            Assert.Contains("Qx * Qy * Qz", axisOrderHelp.Text, StringComparison.Ordinal);
            Assert.Equal(
                1d,
                window.FindControl<ComboBox>("PositionStepPresetComboBox")!.SelectedItem);
            Assert.Equal(
                1d,
                window.FindControl<ComboBox>("RotationStepPresetComboBox")!.SelectedItem);

            var prefixes = new[]
            {
                "LeftTracker",
                "LeftController",
                "RightTracker",
                "RightController",
            };
            var components = new[]
            {
                "PositionX",
                "PositionY",
                "PositionZ",
                "RotationX",
                "RotationY",
                "RotationZ",
            };
            var editorStarts = new[] { 22, 29, 37, 44 };
            var resetIndices = new[] { 28, 35, 43, 50 };
            for (var prefixIndex = 0; prefixIndex < prefixes.Length; prefixIndex++)
            {
                var prefix = prefixes[prefixIndex];
                Assert.Equal(
                    resetIndices[prefixIndex],
                    window.FindControl<Button>($"{prefix}ResetButton")!.TabIndex);
                for (var componentIndex = 0;
                     componentIndex < components.Length;
                     componentIndex++)
                {
                    var component = components[componentIndex];
                    var editor =
                        window.FindControl<TextBox>($"{prefix}{component}TextBox");
                    Assert.NotNull(editor);
                    Assert.Equal(
                        editorStarts[prefixIndex] + componentIndex,
                        editor!.TabIndex);
                    Assert.Contains("mount-editor", editor!.Classes);
                    Assert.False(string.IsNullOrWhiteSpace(
                        AutomationProperties.GetName(editor)));
                    Assert.False(string.IsNullOrWhiteSpace(
                        AutomationProperties.GetHelpText(editor)));
                    Assert.False(window
                        .FindControl<Button>($"{prefix}{component}DecrementButton")!
                        .IsTabStop);
                    Assert.False(window
                        .FindControl<Button>($"{prefix}{component}IncrementButton")!
                        .IsTabStop);
                }
            }

            Assert.Empty(window.GetVisualDescendants()
                .OfType<TextBox>()
                .Where(textBox => textBox.Classes.Contains("mount-editor")));
            Assert.NotNull(window.FindControl<TextBlock>("LeftEffectiveMountTransformText"));
            Assert.NotNull(window.FindControl<TextBlock>("RightEffectiveMountTransformText"));
            Assert.NotNull(window.FindControl<Button>("CalibrateLeftMountButton"));
            Assert.NotNull(window.FindControl<Button>("CalibrateRightMountButton"));
            Assert.NotNull(window.FindControl<Button>("CalibrateBothMountButton"));
            Assert.NotNull(window.FindControl<Button>("SaveMountAdjustmentsButton"));
            Assert.NotNull(window.FindControl<Button>("RevertMountAdjustmentsButton"));
            Assert.Equal(19, mountExpander.TabIndex);
            Assert.Equal(36, window.FindControl<Button>("CalibrateLeftMountButton")!.TabIndex);
            Assert.Equal(51, window.FindControl<Button>("CalibrateRightMountButton")!.TabIndex);
            Assert.Equal(52, window.FindControl<Button>("CalibrateBothMountButton")!.TabIndex);
            Assert.Equal(53, window.FindControl<Button>("SaveMountAdjustmentsButton")!.TabIndex);
            Assert.Equal(54, window.FindControl<Button>("RevertMountAdjustmentsButton")!.TabIndex);
            Assert.Equal(
                "Ctrl+S",
                window.FindControl<Button>("SaveMountAdjustmentsButton")!.HotKey!.ToString());
            Assert.Equal(
                "Ctrl+Z",
                window.FindControl<Button>("RevertMountAdjustmentsButton")!.HotKey!.ToString());
            Assert.NotNull(window.FindControl<Border>("MountAdjustmentDirtyIndicator"));
            Assert.NotNull(window.FindControl<TextBlock>("MountAdjustmentDirtyText"));
            Assert.NotNull(window.FindControl<TextBlock>("TrackerNeutralizationStatusText"));
            Assert.False(
                window.FindControl<Border>("MountAdjustmentRestoreFailureWarning")!.IsVisible);
            Assert.NotNull(
                window.FindControl<TextBlock>("MountAdjustmentRestoreFailureWarningText"));
            Assert.Equal(
                "Alt+M",
                window.FindControl<Button>("RemoveDriverButton")!.HotKey!.ToString());
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

            Assert.Empty(window.GetVisualDescendants()
                .OfType<TextBox>()
                .Where(textBox => textBox.Classes.Contains("mount-editor")));
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
