using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;

namespace Ltb.Gui.Tests;

public sealed class WindowPlacementStoreTests
{
    [Fact]
    public async Task JsonStoreRoundTripsAtomicallyAndTreatsCorruptionAsNoPreference()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ltb-window-placement-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "window-placement.json");
        try
        {
            var store = new JsonWindowPlacementStore(path);
            var expected = new WindowPlacement(-1200, 80, 980d, 740d);

            await store.SaveAsync(expected);

            Assert.Equal(expected, await store.LoadAsync());
            Assert.Equal(
                ["window-placement.json"],
                Directory.GetFiles(directory).Select(Path.GetFileName));

            await File.WriteAllTextAsync(path, "{ not valid JSON");

            Assert.Null(await store.LoadAsync());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void NormalizeClampsDimensionsAndOffscreenPositionsToAvailableWorkingArea()
    {
        WindowWorkingArea[] areas =
        [
            new(0, 0, 1920, 1080, 1d),
            new(-2560, 0, 2560, 1440, 2d),
        ];

        var secondary = JsonWindowPlacementStore.Normalize(
            new WindowPlacement(-2000, 200, 4000d, 3000d),
            areas,
            minimumWidth: 760d,
            minimumHeight: 620d);
        var offscreen = JsonWindowPlacementStore.Normalize(
            new WindowPlacement(50_000, 50_000, 300d, 200d),
            areas,
            minimumWidth: 760d,
            minimumHeight: 620d);

        Assert.Equal(-2560, secondary.X);
        Assert.Equal(0, secondary.Y);
        Assert.Equal(1280d, secondary.Width);
        Assert.Equal(720d, secondary.Height);
        Assert.Equal(1160, offscreen.X);
        Assert.Equal(460, offscreen.Y);
        Assert.Equal(760d, offscreen.Width);
        Assert.Equal(620d, offscreen.Height);
    }

    [AvaloniaFact]
    public async Task InjectedWindowStoreRestoresSafelyAndFinalSaveIsExplicit()
    {
        var store = new RecordingWindowPlacementStore(
            new WindowPlacement(50_000, 50_000, 100d, 100d));
        var window = new MainWindow(store);
        try
        {
            window.Show();

            Assert.True(window.Width >= window.MinWidth);
            Assert.True(window.Height >= window.MinHeight);
            var primary = Assert.IsAssignableFrom<Screen>(window.Screens.Primary);
            Assert.True(primary.WorkingArea.Contains(window.Position));

            window.Width = 900d;
            window.Height = 700d;
            window.Position = new PixelPoint(25, 35);
            await window.SavePlacementAsync();

            var saved = Assert.Single(store.Saved);
            Assert.Equal(new WindowPlacement(25, 35, 900d, 700d), saved);
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class RecordingWindowPlacementStore(WindowPlacement? placement)
        : IWindowPlacementStore
    {
        public List<WindowPlacement> Saved { get; } = [];

        public ValueTask<WindowPlacement?> LoadAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(placement);

        public ValueTask SaveAsync(
            WindowPlacement current,
            CancellationToken cancellationToken = default)
        {
            Saved.Add(current);
            return ValueTask.CompletedTask;
        }
    }
}
