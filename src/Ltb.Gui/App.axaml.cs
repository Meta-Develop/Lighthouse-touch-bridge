using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Ltb.Gui.ViewModels;

namespace Ltb.Gui;

/// <summary>
/// Composition root for the first-party internal-driver desktop flow.
/// </summary>
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new InternalDriverViewModel(
                new InternalDriverSessionFactory(),
                action => Dispatcher.UIThread.Post(action));
            var window = new MainWindow
            {
                DataContext = viewModel,
            };
            var cleanupCompletedClose = false;
            window.Closing += async (_, eventArgs) =>
            {
                if (cleanupCompletedClose)
                {
                    return;
                }

                eventArgs.Cancel = true;
                await viewModel.CloseAsync();
                cleanupCompletedClose = true;
                window.Close();
            };
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
