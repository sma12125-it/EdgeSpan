using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using EdgeSpan.Product;

namespace EdgeSpan.Windows.Controls;

/// <summary>The edge membrane, drawn with the same spring as the real glow.</summary>
public sealed class BreakthroughSketch : Control
{
    public static readonly StyledProperty<decimal?> IntensityProperty =
        AvaloniaProperty.Register<BreakthroughSketch, decimal?>(nameof(Intensity), 70);

    public static readonly StyledProperty<bool> ReduceProperty =
        AvaloniaProperty.Register<BreakthroughSketch, bool>(nameof(Reduce));

    private readonly Motion.Spring1D _spring = new(Motion.Breakthrough);
    private readonly DispatcherTimer _timer;
    private bool _out = true;

    public BreakthroughSketch()
    {
        _spring.Retarget(1);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
        AttachedToVisualTree += (_, _) => _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    public decimal? Intensity
    {
        get => GetValue(IntensityProperty);
        set => SetValue(IntensityProperty, value);
    }

    public bool Reduce
    {
        get => GetValue(ReduceProperty);
        set => SetValue(ReduceProperty, value);
    }

    private void Tick()
    {
        if (_spring.Step(0.016, Reduce))
        {
            _out = !_out;
            _spring.Retarget(_out ? 1 : 0);
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 4 || h < 4)
            return;
        var peak = Math.Clamp((double)(Intensity ?? 70) / 100.0, 0.2, 1);
        var push = Reduce ? 0 : Math.Clamp(_spring.Value, 0, 1);
        var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(180 * peak), 61, 220, 132)), 3, lineCap: PenLineCap.Round);
        var prev = new Point(8, h * 0.5);
        for (var i = 1; i <= 24; i++)
        {
            var t = i / 24.0;
            var bow = Math.Sin(t * Math.PI) * push * h * 0.28 * peak;
            var next = new Point(8 + t * (w - 16), h * 0.5 - bow);
            context.DrawLine(pen, prev, next);
            prev = next;
        }
        if (push > 0.45)
        {
            var ripple = (push - 0.45) / 0.55;
            var alpha = (byte)(90 * (1 - ripple) * peak);
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(alpha, 96, 205, 255)), 1.6), new Point(w * 0.5, h * 0.5), w * 0.18 * ripple, h * 0.22 * ripple);
        }
    }
}
