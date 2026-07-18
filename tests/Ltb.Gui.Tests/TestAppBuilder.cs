using Avalonia;
using Avalonia.Headless;
using Ltb.Gui;

[assembly: AvaloniaTestApplication(typeof(Ltb.Gui.Tests.TestAppBuilder))]

namespace Ltb.Gui.Tests;

/// <summary>
/// Headless Avalonia bootstrap for the GUI tests. Headless drawing avoids the
/// Skia native library, which NixOS cannot load from NuGet's runtime folders.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = true,
            });
}
