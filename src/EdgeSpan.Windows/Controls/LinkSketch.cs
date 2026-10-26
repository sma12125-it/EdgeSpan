using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace EdgeSpan.Windows.Controls;

/// <summary>PC and phone joined by a cable, Wi-Fi arcs, or a globe.</summary>
public sealed class LinkSketch : Control
{
    public static readonly StyledProperty<string> MethodProperty =
        AvaloniaProperty.Register<LinkSketch, string>(nameof(Method), "usb");

    public static readonly StyledProperty<string> PhaseProperty =
        AvaloniaProperty.Register<LinkSketch, string>(nameof(Phase), "idle");

    private readonly DispatcherTimer _timer;
    private double _t;

    public string Method
    {
        get => GetValue(MethodProperty);
        set => SetValue(MethodProperty, value);
    }

    public string Phase
    {
        get => GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    public LinkSketch()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(32) };
        _timer.Tick += (_, _) =>
        {
            _t += 0.045;
            InvalidateVisual();
        };
        _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 24 || h < 24)
            return;
        var phase = Phase ?? "idle";
        var failed = phase is "failed" or "needDebug" or "unauthorized" or "offline";
        var connected = phase == "connected";
        var accent = connected ? Color.Parse("#2EE6C0") : failed ? Color.Parse("#F0B429") : Color.Parse("#8EB6E3");
        var pulse = phase is "searching" or "connecting" ? 0.45 + 0.55 * (0.5 + 0.5 * Math.Sin(_t * 2)) : 1;
        var link = new SolidColorBrush(Color.FromArgb((byte)(255 * pulse), accent.R, accent.G, accent.B));
        var blue = new SolidColorBrush(Color.Parse("#174A8B"));
        var stroke = new Pen(Brushes.White, 2.2, lineCap: PenLineCap.Round);
        var pc = new Rect(w * 0.06, h * 0.28, w * 0.30, h * 0.40);
        var phone = new Rect(w * 0.70, h * 0.16, w * 0.16, h * 0.62);
        context.DrawRectangle(blue, stroke, new RoundedRect(pc, 10));
        context.DrawLine(stroke, new Point(pc.Left + 18, pc.Bottom + 8), new Point(pc.Right - 28, pc.Bottom + 8));
        context.DrawRectangle(blue, stroke, new RoundedRect(phone, 12));
        var start = new Point(pc.Right - 4, pc.Center.Y);
        var end = new Point(phone.Left + 4, phone.Center.Y);
        var pen = new Pen(link, connected ? 3.4 : 2.6, Dash(phase), PenLineCap.Round);
        var method = (Method ?? "usb").ToLowerInvariant();
        if (method == "relay")
            DrawGlobe(context, new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2), Math.Min(w, h) * 0.16, pen, failed);
        else if (method == "lan")
            DrawWaves(context, new Point((start.X + end.X) / 2, h * 0.50), pen, failed);
        else
            DrawCable(context, start, end, pen, link, failed, connected || phase == "connecting");
    }

    private void DrawCable(DrawingContext context, Point start, Point end, Pen pen, IBrush brush, bool failed, bool moving)
    {
        var mid = (start.X + end.X) / 2;
        var c1 = new Point(mid - 10, start.Y - 28);
        var c2 = new Point(mid + 10, end.Y + 22);
        if (failed)
        {
            context.DrawLine(pen, start, new Point(mid - 16, start.Y));
            context.DrawLine(pen, new Point(mid + 16, end.Y), end);
            return;
        }
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(start, false);
            g.CubicBezierTo(c1, c2, end);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geo);
        if (moving)
        {
            var p = Cubic(start, c1, c2, end, (_t * 0.35) % 1);
            context.DrawEllipse(brush, null, p, 5, 5);
        }
        context.DrawEllipse(null, pen, end, 7, 7);
    }

    private void DrawWaves(DrawingContext context, Point center, Pen pen, bool failed)
    {
        var count = failed ? 1 : 3;
        for (var i = 1; i <= count; i++)
        {
            var radius = 10 + i * 12 + (failed ? 0 : 3 * Math.Sin(_t + i));
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(center.X - radius, center.Y), false);
                g.ArcTo(new Point(center.X + radius, center.Y), new Size(radius, radius * 0.72), 0, false, SweepDirection.Clockwise);
                g.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geo);
        }
    }

    private void DrawGlobe(DrawingContext context, Point center, double radius, Pen pen, bool failed)
    {
        context.DrawEllipse(null, pen, center, radius, radius);
        if (failed)
            return;
        var spin = (_t * 8) % 40 - 20;
        context.DrawEllipse(null, pen, center, radius * 0.42 + Math.Abs(spin) * 0.15, radius);
        context.DrawLine(pen, new Point(center.X - radius, center.Y), new Point(center.X + radius, center.Y));
        context.DrawEllipse(null, pen, new Point(center.X, center.Y - 2), radius * 0.72, radius * 0.28);
    }

    private IDashStyle? Dash(string phase) =>
        phase is "connecting" or "searching" ? new DashStyle(new double[] { 5, 6 }, _t * 12) : null;

    private static Point Cubic(Point a, Point b, Point c, Point d, double t)
    {
        var u = 1 - t;
        return new Point(
            u * u * u * a.X + 3 * u * u * t * b.X + 3 * u * t * t * c.X + t * t * t * d.X,
            u * u * u * a.Y + 3 * u * u * t * b.Y + 3 * u * t * t * c.Y + t * t * t * d.Y);
    }
}
