using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using EdgeSpan.Layout;
using EdgeSpan.Session;

namespace EdgeSpan.Windows.Controls;

/// <summary>
/// Arrange map in the style of Windows 11 Settings &gt; Display: every Windows monitor at its real
/// virtual-desktop position and resolution (numbered, primary marked) plus the Android tiles, which snap
/// edge-to-edge onto any side of any monitor (or between two monitors) and slide along it to set the offset.
/// </summary>
public sealed class LayoutCanvas : Control
{
    public static readonly StyledProperty<IReadOnlyList<DeviceSnapshot>?> DevicesProperty =
        AvaloniaProperty.Register<LayoutCanvas, IReadOnlyList<DeviceSnapshot>?>(nameof(Devices));
    public static readonly StyledProperty<IReadOnlyList<HostMonitor>?> MonitorsProperty =
        AvaloniaProperty.Register<LayoutCanvas, IReadOnlyList<HostMonitor>?>(nameof(Monitors));
    public static readonly StyledProperty<string?> ActiveIdProperty =
        AvaloniaProperty.Register<LayoutCanvas, string?>(nameof(ActiveId));
    public static readonly StyledProperty<string?> SelectedIdProperty =
        AvaloniaProperty.Register<LayoutCanvas, string?>(nameof(SelectedId));
    public static readonly StyledProperty<string?> HostLabelProperty =
        AvaloniaProperty.Register<LayoutCanvas, string?>(nameof(HostLabel), "PC");

    public IReadOnlyList<DeviceSnapshot>? Devices { get => GetValue(DevicesProperty); set => SetValue(DevicesProperty, value); }
    public IReadOnlyList<HostMonitor>? Monitors { get => GetValue(MonitorsProperty); set => SetValue(MonitorsProperty, value); }
    public string? ActiveId { get => GetValue(ActiveIdProperty); set => SetValue(ActiveIdProperty, value); }
    public string? SelectedId { get => GetValue(SelectedIdProperty); set => SetValue(SelectedIdProperty, value); }
    public string? HostLabel { get => GetValue(HostLabelProperty); set => SetValue(HostLabelProperty, value); }

    /// <summary>(deviceId, monitorId, edge, anchor, corner, offsetPx)</summary>
    public event Action<string, string, string, string, string, int>? PlacementChanged;
    public event Action<string>? TileSelected;

    private string? _dragId;
    private Point _grab;
    private Vector _drag;
    private bool _moved;
    private (Rect Free, Snapped Snap)? _preview;
    private double _scale = 1, _ox, _oy;

    private readonly record struct Snapped(HostMonitor Monitor, ScreenEdge Edge, int Offset, Rect Rect);

    static LayoutCanvas()
    {
        AffectsRender<LayoutCanvas>(DevicesProperty, MonitorsProperty, ActiveIdProperty, SelectedIdProperty, HostLabelProperty);
    }

    public LayoutCanvas() => Cursor = new Cursor(StandardCursorType.Hand);

    private IReadOnlyList<HostMonitor> Mons()
    {
        var list = Monitors;
        return list is { Count: > 0 } ? list : new[] { new HostMonitor("", 0, 0, 1920, 1080, 96, true) };
    }

    private HostMonitor MonitorOf(DeviceSnapshot d)
    {
        var all = Mons();
        return all.FirstOrDefault(m => d.Monitor.Length > 0 && m.Id == d.Monitor) ?? all.FirstOrDefault(m => m.Primary) ?? all[0];
    }

    private static (double W, double H) Size(DeviceSnapshot d) =>
        (d.ScreenW > 0 ? Math.Clamp(d.ScreenW, 400, 4000) : 1600, d.ScreenH > 0 ? Math.Clamp(d.ScreenH, 400, 4000) : 1000);

    private static Rect PlaceOn(HostMonitor m, ScreenEdge edge, int offset, double w, double h) => edge switch
    {
        ScreenEdge.Left => new Rect(m.X - w, m.Y + offset, w, h),
        ScreenEdge.Top => new Rect(m.X + offset, m.Y - h, w, h),
        ScreenEdge.Bottom => new Rect(m.X + offset, m.Bottom, w, h),
        _ => new Rect(m.Right, m.Y + offset, w, h)
    };

    private Rect Place(DeviceSnapshot d)
    {
        var m = MonitorOf(d);
        var (w, h) = Size(d);
        if (string.Equals(d.Anchor, "Corner", StringComparison.OrdinalIgnoreCase))
        {
            return d.Corner switch
            {
                "TopLeft" => new Rect(m.X - w, m.Y - h, w, h),
                "BottomLeft" => new Rect(m.X - w, m.Bottom, w, h),
                "BottomRight" => new Rect(m.Right, m.Bottom, w, h),
                _ => new Rect(m.Right, m.Y - h, w, h)
            };
        }
        PlacementParser.TryEdge(d.PcEdge, out var edge);
        return PlaceOn(m, edge, Math.Max(0, d.OffsetPx), w, h);
    }

    /// <summary>A device placed on an edge another monitor continues from sits between them: draw it on the seam.</summary>
    private Rect Between(Rect r, DeviceSnapshot d, out bool between)
    {
        between = false;
        var m = MonitorOf(d);
        PlacementParser.TryEdge(d.PcEdge, out var edge);
        foreach (var n in Mons())
        {
            if (n == m)
                continue;
            var touch = edge switch
            {
                ScreenEdge.Right => n.X == m.Right && n.Y < r.Bottom && n.Bottom > r.Y,
                ScreenEdge.Left => n.Right == m.X && n.Y < r.Bottom && n.Bottom > r.Y,
                ScreenEdge.Bottom => n.Y == m.Bottom && n.X < r.Right && n.Right > r.X,
                _ => n.Bottom == m.Y && n.X < r.Right && n.Right > r.X
            };
            if (!touch)
                continue;
            between = true;
            var vertical = edge is ScreenEdge.Left or ScreenEdge.Right;
            var thin = (vertical ? r.Width : r.Height) * 0.34;
            var seam = edge switch { ScreenEdge.Right => m.Right, ScreenEdge.Left => m.X, ScreenEdge.Bottom => m.Bottom, _ => m.Y };
            return vertical ? new Rect(seam - thin / 2, r.Y, thin, r.Height) : new Rect(r.X, seam - thin / 2, r.Width, thin);
        }
        return r;
    }

    private Snapped Snap(Rect free, double w, double h)
    {
        Snapped? best = null;
        var bestScore = double.MaxValue;
        foreach (var m in Mons())
        {
            foreach (var edge in new[] { ScreenEdge.Left, ScreenEdge.Right, ScreenEdge.Top, ScreenEdge.Bottom })
            {
                var vertical = edge is ScreenEdge.Left or ScreenEdge.Right;
                var len = vertical ? m.H : m.W;
                var raw = vertical ? free.Y - m.Y : free.X - m.X;
                var offset = (int)Math.Clamp(Math.Round(raw / 10) * 10, 0, Math.Max(0, len - 40));
                var r = PlaceOn(m, edge, offset, w, h);
                var score = Math.Abs(r.Center.X - free.Center.X) + Math.Abs(r.Center.Y - free.Center.Y);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = new Snapped(m, edge, offset, r);
                }
            }
        }
        return best!.Value;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 8 || bounds.Height < 8)
            return;
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var monitors = Mons();
        var devices = Devices ?? Array.Empty<DeviceSnapshot>();
        var all = monitors.Select(m => new Rect(m.X, m.Y, m.W, m.H)).Concat(devices.Select(Place)).ToList();
        if (_preview is { } pv)
            all.Add(pv.Free);
        var world = all.Aggregate((a, b) => a.Union(b));
        var pad = Math.Max(world.Width, world.Height) * 0.08;
        world = world.Inflate(pad);
        _scale = Math.Min(bounds.Width / world.Width, bounds.Height / world.Height);
        _ox = (bounds.Width - world.Width * _scale) / 2 - world.X * _scale;
        _oy = (bounds.Height - world.Height * _scale) / 2 - world.Y * _scale;

        var tile = new SolidColorBrush(Color.Parse(dark ? "#3A3A3A" : "#E3E6EB"));
        var tileLine = new Pen(new SolidColorBrush(Color.Parse(dark ? "#4A4A4A" : "#C9CED6")), 1);
        var ink = new SolidColorBrush(Color.Parse(dark ? "#FFFFFF" : "#1A1A1A"));
        var muted = new SolidColorBrush(Color.Parse(dark ? "#C5C5C5" : "#5D5D5D"));
        var accent = new Pen(new SolidColorBrush(Color.Parse("#3B6CFF")), 2.5);
        var teal = new SolidColorBrush(Color.Parse(dark ? "#1F4D48" : "#CDEFEA"));
        var tealLine = new Pen(new SolidColorBrush(Color.Parse("#14B8A6")), 1.2);
        const double gap = 2;

        for (var i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            var r = Map(new Rect(m.X, m.Y, m.W, m.H)).Deflate(gap);
            context.DrawRectangle(tile, tileLine, r, 6, 6);
            Label(context, r, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                m.Primary ? (HostLabel ?? "PC") + " · primary" : $"{m.W} × {m.H}", ink, muted);
        }

        var number = monitors.Count;
        foreach (var d in devices)
        {
            number++;
            var dragging = d.Id == _dragId && _preview != null;
            var r = dragging ? _preview!.Value.Snap.Rect : Place(d);
            var drawn = Between(r, d, out _);
            if (dragging)
                drawn = r;
            var mapped = Map(drawn).Deflate(gap);
            if (dragging)
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 59, 108, 255)), null, Map(_preview!.Value.Free).Deflate(gap), 6, 6);
            context.DrawRectangle(teal, tealLine, mapped, 6, 6);
            if (d.Id == SelectedId || d.Id == _dragId || d.Id == ActiveId)
                context.DrawRectangle(null, accent, mapped.Deflate(1), 6, 6);
            Label(context, mapped, number.ToString(System.Globalization.CultureInfo.InvariantCulture), d.Name, ink, muted);
        }
    }

    private Rect Map(Rect r) => new(_ox + r.X * _scale, _oy + r.Y * _scale, Math.Max(6, r.Width * _scale), Math.Max(6, r.Height * _scale));

    private static void Label(DrawingContext context, Rect rect, string big, string small, IBrush ink, IBrush muted)
    {
        var face = new Typeface("Segoe UI Variable Display, Segoe UI, Vazirmatn");
        var size = Math.Clamp(Math.Min(rect.Width, rect.Height) * 0.32, 11, 34);
        var a = new FormattedText(big, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, ink);
        var b = new FormattedText(small, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, Math.Clamp(size * 0.42, 10, 13), muted)
        {
            MaxTextWidth = Math.Max(10, rect.Width - 8), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis
        };
        var showSmall = rect.Height > a.Height + b.Height + 6;
        var total = a.Height + (showSmall ? b.Height : 0);
        var y = rect.Y + (rect.Height - total) / 2;
        context.DrawText(a, new Point(rect.X + (rect.Width - a.Width) / 2, y));
        if (showSmall)
            context.DrawText(b, new Point(rect.X + Math.Max(4, (rect.Width - b.Width) / 2), y + a.Height));
    }

    private string? Hit(Point point)
    {
        var devices = Devices ?? Array.Empty<DeviceSnapshot>();
        foreach (var d in devices.Reverse())
            if (Map(Between(Place(d), d, out _)).Contains(point))
                return d.Id;
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetPosition(this);
        var hit = Hit(point);
        if (hit == null)
            return;
        _dragId = hit;
        _grab = point;
        _drag = default;
        _moved = false;
        _preview = null;
        TileSelected?.Invoke(hit);
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragId == null || _scale <= 0)
            return;
        var point = e.GetPosition(this);
        var delta = point - _grab;
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 4)
            _moved = true;
        if (!_moved)
            return;
        var d = (Devices ?? Array.Empty<DeviceSnapshot>()).FirstOrDefault(x => x.Id == _dragId);
        if (d == null)
            return;
        _drag = new Vector(delta.X / _scale, delta.Y / _scale);
        var free = Place(d).Translate(_drag);
        var (w, h) = Size(d);
        _preview = (free, Snap(free, w, h));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_dragId == null)
            return;
        e.Pointer.Capture(null);
        var id = _dragId;
        var snap = _preview?.Snap;
        _dragId = null;
        _preview = null;
        _drag = default;
        if (_moved && snap is { } s)
            PlacementChanged?.Invoke(id, s.Monitor.Id, s.Edge.ToString(), "Edge", "TopRight", s.Offset);
        InvalidateVisual();
    }
}
