using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Ltb.App;
using Ltb.Gui.ViewModels;

namespace Ltb.Gui.Tests;

public sealed class MainWindowSmokeTests
{
    [AvaloniaFact]
    public void MainWindowConstructsAndPropagatesStateBinding()
    {
        using var viewModel = new CalibrationWizardViewModel(
            new IdleSessionFactory(),
            new GuiCommandLineOptions { ProfileStorePath = "profiles.json" });
        var window = new MainWindow
        {
            DataContext = viewModel,
        };
        try
        {
            window.Show();
            var stateText = window.FindControl<TextBlock>("StateText");
            var diagnosticText = window.FindControl<TextBlock>("DiagnosticText");
            var eventList = window.FindControl<ListBox>("EventList");
            var scriptedMode = window.FindControl<RadioButton>("ScriptedModeButton");
            var productionMode = window.FindControl<RadioButton>("ProductionModeButton");
            var productionOptions = window.FindControl<StackPanel>("ProductionOptionsPanel");
            Assert.NotNull(stateText);
            Assert.NotNull(diagnosticText);
            Assert.NotNull(eventList);
            Assert.NotNull(scriptedMode);
            Assert.NotNull(productionMode);
            Assert.NotNull(productionOptions);
            Assert.Equal(nameof(CalibrationWizardState.Stopped), stateText!.Text);
            Assert.Equal(0, eventList!.ItemCount);
            Assert.True(scriptedMode!.IsChecked);
            Assert.False(productionOptions!.IsVisible);

            viewModel.IsProductionMode = true;
            Assert.True(productionMode!.IsChecked);
            Assert.True(productionOptions.IsVisible);

            ICalibrationWizardOutput output = viewModel;
            output.OnStateChanged(CalibrationWizardState.Recording, "smoke transition");

            Assert.Equal(nameof(CalibrationWizardState.Recording), stateText.Text);
            Assert.Equal("smoke transition", diagnosticText!.Text);
            Assert.Equal(1, eventList.ItemCount);
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class IdleSession : ICalibrationWizardSession
    {
        public Task<CalibrationWizardResult> RunAsync(
            ICalibrationWizardOutput output,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The smoke test never starts a wizard run.");
    }

    private sealed class IdleSessionFactory : ICalibrationWizardSessionFactory
    {
        private readonly ICalibrationWizardSession _session = new IdleSession();

        public ICalibrationWizardSession CreateScripted(
            string profileStorePath,
            string? logPath) => _session;

        public ICalibrationWizardSession CreateProduction(
            ProductionCalibrationWizardSessionOptions options) => _session;
    }
}
