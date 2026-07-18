using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Ltb.Gui.ViewModels;

namespace Ltb.Gui;

/// <summary>
/// Composition root for the desktop shell. It parses editable launch defaults
/// and wires the shared session factory; it contains no wizard policy.
/// </summary>
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var profileStorePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LighthouseTouchBridge",
                "wizard-demo-profiles.json");
            var options = GuiCommandLineOptions.Parse(
                desktop.Args ?? Array.Empty<string>(),
                profileStorePath,
                out var startupDiagnostic);
            var viewModel = new CalibrationWizardViewModel(
                new CalibrationWizardSessionFactory(),
                options,
                startupDiagnostic,
                action => Dispatcher.UIThread.Post(action));
            var window = new MainWindow
            {
                DataContext = viewModel,
            };
            var cleanupCompletedClose = false;
            window.Closing += async (_, eventArgs) =>
            {
                if (cleanupCompletedClose || !viewModel.IsRunning)
                {
                    return;
                }

                eventArgs.Cancel = true;
                await viewModel.StopAsync();
                cleanupCompletedClose = true;
                window.Close();
            };
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => viewModel.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
