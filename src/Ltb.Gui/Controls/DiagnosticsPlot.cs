using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Ltb.Gui.ViewModels;

namespace Ltb.Gui.Controls;

/// <summary>
/// Low-overhead renderer over fixed diagnostic ring buffers. Null values
/// deliberately break line segments so unavailable evidence is never drawn as
/// zero.
/// </summary>
public sealed class DiagnosticsPlot : Control
{
    public static readonly StyledProperty<IReadOnlyList<DiagnosticPoint>?> Series1Property =
        AvaloniaProperty.Register<DiagnosticsPlot, IReadOnlyList<DiagnosticPoint>?>(nameof(Series1));
    public static readonly StyledProperty<IReadOnlyList<DiagnosticPoint>?> Series2Property =
        AvaloniaProperty.Register<DiagnosticsPlot, IReadOnlyList<DiagnosticPoint>?>(nameof(Series2));
    public static readonly StyledProperty<IReadOnlyList<DiagnosticPoint>?> Series3Property =
        AvaloniaProperty.Register<DiagnosticsPlot, IReadOnlyList<DiagnosticPoint>?>(nameof(Series3));
    public static readonly StyledProperty<IReadOnlyList<DiagnosticPoint>?> Series4Property =
        AvaloniaProperty.Register<DiagnosticsPlot, IReadOnlyList<DiagnosticPoint>?>(nameof(Series4));
    public static readonly StyledProperty<int> RefreshVersionProperty =
        AvaloniaProperty.Register<DiagnosticsPlot, int>(nameof(RefreshVersion));
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<DiagnosticsPlot, double>(nameof(Minimum), double.NaN);
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<DiagnosticsPlot, double>(nameof(Maximum), double.NaN);

    private static readonly IBrush PlotBackground =
        new SolidColorBrush(Color.Parse("#0A121B"));
    private static readonly IPen GridPen =
        new Pen(new SolidColorBrush(Color.Parse("#294157")), 1d);
    private static readonly IPen[] SeriesPens =
    [
        new Pen(new SolidColorBrush(Color.Parse("#79D8FF")), 2d),
        new Pen(new SolidColorBrush(Color.Parse("#FFBF69")), 2d),
        new Pen(new SolidColorBrush(Color.Parse("#8BE0A4")), 2d),
        new Pen(new SolidColorBrush(Color.Parse("#D6A5FF")), 2d),
    ];

    static DiagnosticsPlot()
    {
        AffectsRender<DiagnosticsPlot>(
            Series1Property,
            Series2Property,
            Series3Property,
            Series4Property,
            RefreshVersionProperty,
            MinimumProperty,
            MaximumProperty);
    }

    public DiagnosticsPlot()
    {
        MinHeight = 130d;
        ClipToBounds = true;
        Focusable = false;
    }

    public IReadOnlyList<DiagnosticPoint>? Series1
    {
        get => GetValue(Series1Property);
        set => SetValue(Series1Property, value);
    }

    public IReadOnlyList<DiagnosticPoint>? Series2
    {
        get => GetValue(Series2Property);
        set => SetValue(Series2Property, value);
    }

    public IReadOnlyList<DiagnosticPoint>? Series3
    {
        get => GetValue(Series3Property);
        set => SetValue(Series3Property, value);
    }

    public IReadOnlyList<DiagnosticPoint>? Series4
    {
        get => GetValue(Series4Property);
        set => SetValue(Series4Property, value);
    }

    public int RefreshVersion
    {
        get => GetValue(RefreshVersionProperty);
        set => SetValue(RefreshVersionProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var plot = Bounds.Deflate(8d);
        if (plot.Width <= 0d || plot.Height <= 0d)
        {
            return;
        }

        context.DrawRectangle(PlotBackground, GridPen, plot, 8d, 8d);
        for (var index = 1; index < 4; index++)
        {
            var y = plot.Y + ((plot.Height / 4d) * index);
            context.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        var range = DetermineRange();
        DrawSeries(context, plot, Series1, SeriesPens[0], range.Minimum, range.Maximum);
        DrawSeries(context, plot, Series2, SeriesPens[1], range.Minimum, range.Maximum);
        DrawSeries(context, plot, Series3, SeriesPens[2], range.Minimum, range.Maximum);
        DrawSeries(context, plot, Series4, SeriesPens[3], range.Minimum, range.Maximum);
    }

    private (double Minimum, double Maximum) DetermineRange()
    {
        var minimum = Minimum;
        var maximum = Maximum;
        if (double.IsFinite(minimum) && double.IsFinite(maximum) && maximum > minimum)
        {
            return (minimum, maximum);
        }

        minimum = double.PositiveInfinity;
        maximum = double.NegativeInfinity;
        FindRange(Series1, ref minimum, ref maximum);
        FindRange(Series2, ref minimum, ref maximum);
        FindRange(Series3, ref minimum, ref maximum);
        FindRange(Series4, ref minimum, ref maximum);

        if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
        {
            return (0d, 1d);
        }

        if (Math.Abs(maximum - minimum) < 1e-9d)
        {
            var padding = Math.Max(1d, Math.Abs(maximum) * 0.1d);
            return (minimum - padding, maximum + padding);
        }

        var margin = (maximum - minimum) * 0.08d;
        return (minimum - margin, maximum + margin);
    }

    private static void FindRange(
        IReadOnlyList<DiagnosticPoint>? series,
        ref double minimum,
        ref double maximum)
    {
        if (series is null)
        {
            return;
        }

        for (var index = 0; index < series.Count; index++)
        {
            if (series[index].Value is not { } value || !double.IsFinite(value))
            {
                continue;
            }

            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }
    }

    private static void DrawSeries(
        DrawingContext context,
        Rect plot,
        IReadOnlyList<DiagnosticPoint>? series,
        IPen pen,
        double minimum,
        double maximum)
    {
        if (series is null || series.Count == 0)
        {
            return;
        }

        var latestSeconds = series[^1].ElapsedSeconds;
        var earliestSeconds = Math.Max(0d, latestSeconds - DebugDiagnosticsViewModel.WindowSeconds);
        Point? previous = null;
        for (var index = 0; index < series.Count; index++)
        {
            var sample = series[index];
            if (sample.ElapsedSeconds < earliestSeconds ||
                sample.Value is not { } value ||
                !double.IsFinite(value))
            {
                previous = null;
                continue;
            }

            var xProgress = DebugDiagnosticsViewModel.WindowSeconds <= 0d
                ? 1d
                : (sample.ElapsedSeconds - earliestSeconds) /
                  DebugDiagnosticsViewModel.WindowSeconds;
            var yProgress = (value - minimum) / (maximum - minimum);
            var point = new Point(
                plot.Left + (Math.Clamp(xProgress, 0d, 1d) * plot.Width),
                plot.Bottom - (Math.Clamp(yProgress, 0d, 1d) * plot.Height));
            if (previous is { } prior)
            {
                context.DrawLine(pen, prior, point);
            }

            previous = point;
        }
    }
}
