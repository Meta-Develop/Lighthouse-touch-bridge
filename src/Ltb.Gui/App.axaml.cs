using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Ltb.App;
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
            var mountAdjustmentPort = new AppMountAdjustmentPort();
            var preSessionControl = InternalDriverPreSessionControl.Create();
            var viewModel = new InternalDriverViewModel(
                new InternalDriverSessionFactory(mountAdjustmentPort),
                action => Dispatcher.UIThread.Post(action),
                mountAdjustmentPort: mountAdjustmentPort,
                preSessionControl: preSessionControl);
            var window = new MainWindow(JsonWindowPlacementStore.CreateDefault())
            {
                DataContext = viewModel,
            };
            window.Opened += async (_, _) => await viewModel.InitializeAsync();
            var closeCoordinator = new WindowCloseCoordinator(
                viewModel.CloseAsync,
                window.SavePlacementAsync,
                window.Close);
            window.Closing += (_, eventArgs) =>
            {
                if (closeCoordinator.AllowFinalClose)
                {
                    return;
                }

                eventArgs.Cancel = true;
                _ = closeCoordinator.RequestCloseAsync();
            };
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
