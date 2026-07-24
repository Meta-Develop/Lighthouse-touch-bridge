using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Ltb.Gui.Controls;

public enum MotionGuideCue
{
    Prepare = 0,
    Pitch,
    Yaw,
    Roll,
    ModerateTranslation,
    Processing,
}

/// <summary>
/// Original, asset-free conceptual controller/tracker motion diagram. Animation
/// is decorative coaching only; the adjacent text carries the complete meaning.
/// </summary>
public sealed class MotionGuideControl : Control
{
    public static readonly StyledProperty<MotionGuideCue> CueProperty =
        AvaloniaProperty.Register<MotionGuideControl, MotionGuideCue>(nameof(Cue));

    public static readonly StyledProperty<bool> IsRightHandProperty =
        AvaloniaProperty.Register<MotionGuideControl, bool>(nameof(IsRightHand));

    public static readonly StyledProperty<bool> ReduceMotionProperty =
        AvaloniaProperty.Register<MotionGuideControl, bool>(nameof(ReduceMotion));

    private static readonly IBrush DeviceBrush = new SolidColorBrush(Color.Parse("#17293A"));
    private static readonly IBrush TrackerBrush = new SolidColorBrush(Color.Parse("#79D8FF"));
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#FFBF69"));
    private static readonly IPen OutlinePen =
        new Pen(new SolidColorBrush(Color.Parse("#DCEBFA")), 2d);
    private static readonly IPen TrackerAxisPen = new Pen(TrackerBrush, 2d);
    private static readonly IPen UpAxisPen =
        new Pen(new SolidColorBrush(Color.Parse("#8BE0A4")), 2d);
    private static readonly IPen AccentPen = new Pen(AccentBrush, 3d);
    private readonly DispatcherTimer _timer;
    private double _phase;

    static MotionGuideControl()
    {
        AffectsRender<MotionGuideControl>(CueProperty, IsRightHandProperty, ReduceMotionProperty);
    }

    public MotionGuideControl()
    {
        Focusable = false;
        MinHeight = 190d;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Render, OnTick);
        AttachedToVisualTree += (_, _) => _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    public MotionGuideCue Cue
    {
        get => GetValue(CueProperty);
        set => SetValue(CueProperty, value);
    }

    public bool IsRightHand
    {
        get => GetValue(IsRightHandProperty);
        set => SetValue(IsRightHandProperty, value);
    }

    public bool ReduceMotion
    {
        get => GetValue(ReduceMotionProperty);
        set => SetValue(ReduceMotionProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds.Deflate(12d);
        if (bounds.Width <= 0d || bounds.Height <= 0d)
        {
            return;
        }

        var center = bounds.Center;
        var motion = ReduceMotion ? 0d : Math.Sin(_phase * Math.PI * 2d);
        var cueOffset = Cue switch
        {
            MotionGuideCue.Pitch => new Vector(0d, motion * 7d),
            MotionGuideCue.Yaw => new Vector(motion * 9d, 0d),
            MotionGuideCue.Roll => new Vector(motion * 4d, -motion * 3d),
            MotionGuideCue.ModerateTranslation => new Vector(motion * 24d, motion * 7d),
            _ => default,
        };
        var handDirection = IsRightHand ? 1d : -1d;
        var controllerCenter = center + cueOffset;
        DrawDevice(context, controllerCenter, handDirection);
        DrawCue(context, center, motion, handDirection);
    }

    private static void DrawDevice(DrawingContext context, Point center, double direction)
    {
        var grip = new Rect(center.X - 18d, center.Y - 42d, 36d, 84d);
        context.DrawRectangle(DeviceBrush, OutlinePen, grip, 16d, 16d);
        context.DrawEllipse(
            DeviceBrush,
            OutlinePen,
            new Point(center.X, center.Y - 43d),
            32d,
            16d);
        var puckCenter = new Point(center.X + (direction * 38d), center.Y - 57d);
        context.DrawLine(OutlinePen, new Point(center.X, center.Y - 36d), puckCenter);
        context.DrawEllipse(TrackerBrush, OutlinePen, puckCenter, 15d, 15d);
        context.DrawLine(
            TrackerAxisPen,
            new Point(center.X, center.Y),
            new Point(center.X + (direction * 45d), center.Y));
        context.DrawLine(
            UpAxisPen,
            new Point(center.X, center.Y),
            new Point(center.X, center.Y - 48d));
    }

    private void DrawCue(DrawingContext context, Point center, double motion, double direction)
    {
        var alpha = ReduceMotion ? (byte)255 : (byte)(185 + (35 * (motion + 1d)));
        var pulse = new SolidColorBrush(Color.FromArgb(alpha, 255, 191, 105));
        var pen = new Pen(pulse, 4d);
        var thinPen = new Pen(pulse, 2d);
        switch (Cue)
        {
            case MotionGuideCue.Pitch:
                DrawArc(context, pen, center, 66d, 82d, -2.35d, -0.75d);
                DrawArrowHead(context, pen, ArcPoint(center, 66d, 82d, -0.75d), new Vector(1d, 0.2d));
                break;
            case MotionGuideCue.Yaw:
                DrawArc(context, pen, center, 92d, 36d, 0.15d, 2.95d);
                DrawArrowHead(context, pen, ArcPoint(center, 92d, 36d, 2.95d), new Vector(-1d, -0.1d));
                break;
            case MotionGuideCue.Roll:
                DrawArc(context, pen, center, 62d, 62d, -1.25d, 4.15d);
                DrawArrowHead(context, pen, ArcPoint(center, 62d, 62d, 4.15d), new Vector(-0.5d, -0.8d));
                break;
            case MotionGuideCue.ModerateTranslation:
                var left = new Point(center.X - 100d, center.Y + 65d);
                var right = new Point(center.X + 100d, center.Y + 65d);
                context.DrawLine(pen, left, right);
                DrawArrowHead(context, pen, left, new Vector(-1d, 0d));
                DrawArrowHead(context, pen, right, new Vector(1d, 0d));
                context.DrawLine(
                    AccentPen,
                    new Point(center.X + (direction * 64d), center.Y + 38d),
                    new Point(center.X + (direction * 88d), center.Y - 8d));
                break;
            case MotionGuideCue.Processing:
                for (var index = 0; index < 3; index++)
                {
                    var radius = 28d + (index * 17d);
                    DrawArc(context, thinPen, center, radius, radius, -1.2d, 1.2d);
                }
                break;
            default:
                context.DrawLine(
                    AccentPen,
                    new Point(center.X - 80d, center.Y + 66d),
                    new Point(center.X + 80d, center.Y + 66d));
                break;
        }
    }

    private static void DrawArc(
        DrawingContext context,
        IPen pen,
        Point center,
        double radiusX,
        double radiusY,
        double start,
        double end)
    {
        const int segmentCount = 28;
        var previous = ArcPoint(center, radiusX, radiusY, start);
        for (var index = 1; index <= segmentCount; index++)
        {
            var progress = index / (double)segmentCount;
            var current = ArcPoint(center, radiusX, radiusY, start + ((end - start) * progress));
            context.DrawLine(pen, previous, current);
            previous = current;
        }
    }

    private static Point ArcPoint(
        Point center,
        double radiusX,
        double radiusY,
        double radians) =>
        new(
            center.X + (Math.Cos(radians) * radiusX),
            center.Y + (Math.Sin(radians) * radiusY));

    private static void DrawArrowHead(
        DrawingContext context,
        IPen pen,
        Point tip,
        Vector direction)
    {
        var normalized = direction.Normalize();
        var perpendicular = new Vector(-normalized.Y, normalized.X);
        var basePoint = tip - (normalized * 13d);
        context.DrawLine(pen, tip, basePoint + (perpendicular * 7d));
        context.DrawLine(pen, tip, basePoint - (perpendicular * 7d));
    }

    private void OnTick(object? sender, EventArgs eventArgs)
    {
        if (ReduceMotion || !IsEffectivelyVisible)
        {
            return;
        }

        _phase = (_phase + 0.055d) % 1d;
        InvalidateVisual();
    }
}
