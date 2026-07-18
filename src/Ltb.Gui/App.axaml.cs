using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Ltb.Gui.ViewModels;

namespace Ltb.Gui;

/// <summary>
/// Composition root for the desktop shell. It wires the scripted wizard-demo
/// session to the view model; it contains no wizard policy.
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
            var viewModel = new WizardDemoViewModel(
                new ScriptedCalibrationWizardSession(profileStorePath),
                action => Dispatcher.UIThread.Post(action));
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            desktop.Exit += (_, _) => viewModel.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
