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
        using var viewModel = new WizardDemoViewModel(new IdleSession());
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
            Assert.NotNull(stateText);
            Assert.NotNull(diagnosticText);
            Assert.NotNull(eventList);
            Assert.Equal(nameof(CalibrationWizardState.Stopped), stateText!.Text);
            Assert.Equal(0, eventList!.ItemCount);

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
}
