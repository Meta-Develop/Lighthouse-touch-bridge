using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Ltb.Gui;

/// <summary>
/// Rendering-and-binding-only shell over <c>WizardDemoViewModel</c>.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
