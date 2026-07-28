using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace Ltb.Gui;

/// <summary>
/// Rendering-and-binding-only shell over <c>InternalDriverViewModel</c>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Avalonia owns the Window lifetime; Closed cancels owned asynchronous resources.")]
public partial class MainWindow : Window
{
    private static readonly TimeSpan PlacementSaveDelay = TimeSpan.FromMilliseconds(350);
    private readonly IWindowPlacementStore? _placementStore;
    private readonly CancellationTokenSource _placementLifetime = new();
    private readonly SemaphoreSlim _placementSaveGate = new(1, 1);
    private CancellationTokenSource? _placementSaveDelay;
    private bool _isRestoringPlacement;
    private bool _isOpen;

    public MainWindow() : this(placementStore: null)
    {
    }

    internal MainWindow(IWindowPlacementStore? placementStore)
    {
        _placementStore = placementStore;
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Opened += OnOpened;
        PositionChanged += OnPlacementChanged;
        PropertyChanged += OnWindowPropertyChanged;
        Closed += OnClosed;
    }

    internal async Task SavePlacementAsync()
    {
        if (_placementStore is null)
        {
            return;
        }

        CancelPendingPlacementSave();
        var placement = CapturePlacement();
        if (placement is null)
        {
            return;
        }

        await PersistPlacementAsync(
            placement,
            _placementLifetime.Token).ConfigureAwait(false);
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        _isOpen = true;
        if (_placementStore is null)
        {
            return;
        }

        _isRestoringPlacement = true;
        try
        {
            var placement = await _placementStore.LoadAsync(
                _placementLifetime.Token);
            if (placement is null || _placementLifetime.IsCancellationRequested)
            {
                return;
            }

            var normalized = JsonWindowPlacementStore.Normalize(
                placement,
                GetWorkingAreas(),
                MinWidth,
                MinHeight);
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = normalized.Width;
            Height = normalized.Height;
            Position = new PixelPoint(normalized.X, normalized.Y);
        }
        catch (OperationCanceledException) when (_placementLifetime.IsCancellationRequested)
        {
            // Closing owns placement cancellation.
        }
        catch
        {
            // Corrupt or unavailable local placement must never block the GUI.
        }
        finally
        {
            _isRestoringPlacement = false;
        }
    }

    private IReadOnlyList<WindowWorkingArea> GetWorkingAreas()
    {
        var areas = new List<WindowWorkingArea>();
        var primary = Screens.Primary;
        if (primary is not null)
        {
            areas.Add(ToWorkingArea(primary));
        }

        foreach (var screen in Screens.All)
        {
            if (!ReferenceEquals(screen, primary))
            {
                areas.Add(ToWorkingArea(screen));
            }
        }

        return areas;
    }

    private static WindowWorkingArea ToWorkingArea(Screen screen)
    {
        var area = screen.WorkingArea;
        return new WindowWorkingArea(
            area.X,
            area.Y,
            area.Width,
            area.Height,
            screen.Scaling);
    }

    private void OnPlacementChanged(object? sender, PixelPointEventArgs eventArgs) =>
        SchedulePlacementSave();

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.Property == BoundsProperty ||
            eventArgs.Property == WindowStateProperty)
        {
            SchedulePlacementSave();
        }
    }

    private void SchedulePlacementSave()
    {
        if (_placementStore is null || !_isOpen || _isRestoringPlacement)
        {
            return;
        }

        var placement = CapturePlacement();
        if (placement is null)
        {
            return;
        }

        CancelPendingPlacementSave();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _placementLifetime.Token);
        _placementSaveDelay = cancellation;
        _ = SaveAfterDelayAsync(placement, cancellation);
    }

    private WindowPlacement? CapturePlacement()
    {
        if (WindowState != WindowState.Normal ||
            !double.IsFinite(Width) ||
            !double.IsFinite(Height) ||
            Width <= 0d ||
            Height <= 0d)
        {
            return null;
        }

        return new WindowPlacement(
            Position.X,
            Position.Y,
            Math.Max(MinWidth, Width),
            Math.Max(MinHeight, Height));
    }

    private async Task SaveAfterDelayAsync(
        WindowPlacement placement,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(PlacementSaveDelay, cancellation.Token).ConfigureAwait(false);
            await PersistPlacementAsync(placement, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer placement or close superseded this write.
        }
        catch
        {
            // Local preference persistence is intentionally fail-safe.
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _placementSaveDelay,
                value: null,
                comparand: cancellation);
            cancellation.Dispose();
        }
    }

    private async Task PersistPlacementAsync(
        WindowPlacement placement,
        CancellationToken cancellationToken)
    {
        await _placementSaveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _placementStore!.SaveAsync(placement, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _placementSaveGate.Release();
        }
    }

    private void CancelPendingPlacementSave()
    {
        var cancellation = Interlocked.Exchange(ref _placementSaveDelay, null);
        if (cancellation is not null)
        {
            cancellation.Cancel();
        }
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        _isOpen = false;
        CancelPendingPlacementSave();
        _placementLifetime.Cancel();
    }

    private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Source is not TextBox textBox ||
            !textBox.Classes.Contains("mount-editor") ||
            (eventArgs.KeyModifiers & KeyModifiers.Control) == 0)
        {
            return;
        }

        var buttonSuffix = eventArgs.Key switch
        {
            Key.Add or Key.OemPlus => "IncrementButton",
            Key.Subtract or Key.OemMinus => "DecrementButton",
            _ => null,
        };
        if (buttonSuffix is null ||
            string.IsNullOrWhiteSpace(textBox.Name) ||
            !textBox.Name.EndsWith("TextBox", StringComparison.Ordinal))
        {
            return;
        }

        var buttonName = string.Concat(
            textBox.Name.AsSpan(0, textBox.Name.Length - "TextBox".Length),
            buttonSuffix);
        var button = this.FindControl<Button>(buttonName);
        if (button?.Command?.CanExecute(button.CommandParameter) != true)
        {
            return;
        }

        button.Command.Execute(button.CommandParameter);
        eventArgs.Handled = true;
    }
}
